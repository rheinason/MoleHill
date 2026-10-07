using System.Diagnostics;
using MoleHill.Core.Geometry;

namespace MoleHill.Core.Engine;

/// <summary>
/// Removes qualifying boundary triangles using validated Triangle.NET adjacency or a generic edge-map fallback.
/// </summary>
internal static class TriangleBoundaryCuller
{
    internal const double DegenerateBoundaryAngleDegrees = BoundaryTrianglePeelSettings.DefaultMaxInteriorAngleDegrees;

    internal sealed class Result
    {
        public bool Changed { get; }

        public int[] Faces { get; }

        public int FaceCount { get; }

        public int[] NewToOld { get; }

        public int VertexCount { get; }

        public Result(bool changed, int[] faces, int faceCount, int[] newToOld, int vertexCount)
        {
            Changed = changed;
            Faces = faces;
            FaceCount = faceCount;
            NewToOld = newToOld;
            VertexCount = vertexCount;
        }
    }

    internal sealed class PerformanceTimings
    {
        public double AutoThresholdMilliseconds { get; internal set; }

        public long AutoThresholdAllocatedBytes { get; internal set; }

        public double ConstraintIndexMilliseconds { get; internal set; }

        public long ConstraintIndexAllocatedBytes { get; internal set; }

        public double EdgeMapsMilliseconds { get; internal set; }

        public long EdgeMapsAllocatedBytes { get; internal set; }

        public double AdjacencySetupMilliseconds { get; internal set; }

        public long AdjacencySetupAllocatedBytes { get; internal set; }

        public double SeedQueueMilliseconds { get; internal set; }

        public long SeedQueueAllocatedBytes { get; internal set; }

        public double PeelMilliseconds { get; internal set; }

        public long PeelAllocatedBytes { get; internal set; }

        public double CompactionMilliseconds { get; internal set; }

        public long CompactionAllocatedBytes { get; internal set; }

        public int InitialQueuedFaceCount { get; internal set; }

        public int RemovedFaceCount { get; internal set; }
    }

    public static Result Cull(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        double[] inputXy,
        int[] inputSegments,
        double maxBoundaryEdgeLength)
    {
        return Cull(
            vertices,
            vertexCount,
            faces,
            faceCount,
            inputXy,
            inputSegments,
            BoundaryTrianglePeelSettings.FromLegacyMaxBoundaryEdgeLength(maxBoundaryEdgeLength));
    }

    public static Result Cull(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        double[] inputXy,
        int[] inputSegments,
        BoundaryTrianglePeelSettings? settings)
    {
        return Cull(
            vertices,
            vertexCount,
            faces,
            faceCount,
            inputXy,
            inputSegments,
            settings,
            performanceTimings: null);
    }

    internal static Result Cull(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        double[] inputXy,
        int[] inputSegments,
        BoundaryTrianglePeelSettings? settings,
        PerformanceTimings? performanceTimings)
    {
        Stopwatch? phaseTimer = performanceTimings != null ? Stopwatch.StartNew() : null;
        long phaseAllocatedBefore = performanceTimings != null
            ? GC.GetTotalAllocatedBytes(precise: true)
            : 0;
        settings ??= BoundaryTrianglePeelSettings.Default;
        if (faceCount <= 0 || !settings.Enabled || settings.MaxBoundaryEdgeLength < 0)
            return new Result(false, faces, faceCount, Array.Empty<int>(), vertexCount);

        double effectiveThreshold = settings.MaxBoundaryEdgeLength > 0
            ? settings.MaxBoundaryEdgeLength
            : ComputeAutoThreshold(vertices, faces, faceCount);
        if (performanceTimings != null)
        {
            performanceTimings.AutoThresholdMilliseconds = phaseTimer!.Elapsed.TotalMilliseconds;
            performanceTimings.AutoThresholdAllocatedBytes =
                GC.GetTotalAllocatedBytes(precise: true) - phaseAllocatedBefore;
            phaseAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            phaseTimer.Restart();
        }

        bool canUseEdgeAngle =
            effectiveThreshold > 0 &&
            !double.IsNaN(effectiveThreshold) &&
            !double.IsInfinity(effectiveThreshold) &&
            settings.MaxInteriorAngleDegrees > 0 &&
            !double.IsNaN(settings.MaxInteriorAngleDegrees) &&
            !double.IsInfinity(settings.MaxInteriorAngleDegrees);
        bool canUseSlope =
            settings.MaxSlopeAngleDegrees > 0 &&
            !double.IsNaN(settings.MaxSlopeAngleDegrees) &&
            !double.IsInfinity(settings.MaxSlopeAngleDegrees);

        if (!canUseEdgeAngle && !canUseSlope && inputSegments.Length == 0)
            return new Result(false, faces, faceCount, Array.Empty<int>(), vertexCount);

        var spatialIndex = ConstraintSpatialIndex.Build(inputXy, inputSegments);
        if (performanceTimings != null)
        {
            performanceTimings.ConstraintIndexMilliseconds = phaseTimer!.Elapsed.TotalMilliseconds;
            performanceTimings.ConstraintIndexAllocatedBytes =
                GC.GetTotalAllocatedBytes(precise: true) - phaseAllocatedBefore;
            phaseAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            phaseTimer.Restart();
        }

        var active = new bool[faceCount];
        Array.Fill(active, true);
        int activeFaceCount = faceCount;

        // Build edge counts and edge→face map in one pass (O(n), no per-iteration rebuild)
        var edgeCounts = new Dictionary<long, int>(faceCount * 3, IndexedMeshTools.EdgeKeyComparer.Instance);
        var edgeToFaces = new Dictionary<long, (int F0, int F1)>(faceCount * 3, IndexedMeshTools.EdgeKeyComparer.Instance);
        for (int f = 0; f < faceCount; f++)
        {
            for (int e = 0; e < 3; e++)
            {
                int a = faces[f * 3 + e];
                int b = faces[f * 3 + (e + 1) % 3];
                long key = IndexedMeshTools.GetEdgeKey(a, b);
                edgeCounts[key] = edgeCounts.GetValueOrDefault(key, 0) + 1;
                if (!edgeToFaces.TryGetValue(key, out var pair))
                    edgeToFaces[key] = (f, -1);
                else if (pair.F1 < 0)
                    edgeToFaces[key] = (pair.F0, f);
            }
        }
        if (performanceTimings != null)
        {
            performanceTimings.EdgeMapsMilliseconds = phaseTimer!.Elapsed.TotalMilliseconds;
            performanceTimings.EdgeMapsAllocatedBytes =
                GC.GetTotalAllocatedBytes(precise: true) - phaseAllocatedBefore;
            phaseAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            phaseTimer.Restart();
        }

        // Seed queue with boundary faces that meet cull criteria
        var inQueue = new bool[faceCount];
        var queue = new Queue<int>();
        for (int f = 0; f < faceCount; f++)
        {
            int i0 = faces[f * 3], i1 = faces[f * 3 + 1], i2 = faces[f * 3 + 2];
            if (HasNakedEdge(i0, i1, i2, edgeCounts) &&
                ShouldPeelBoundaryTriangle(
                    vertices,
                    i0,
                    i1,
                    i2,
                    spatialIndex,
                    effectiveThreshold,
                    canUseEdgeAngle,
                    canUseSlope,
                    settings))
            {
                queue.Enqueue(f);
                inQueue[f] = true;
            }
        }
        if (performanceTimings != null)
        {
            performanceTimings.InitialQueuedFaceCount = queue.Count;
            performanceTimings.SeedQueueMilliseconds = phaseTimer!.Elapsed.TotalMilliseconds;
            performanceTimings.SeedQueueAllocatedBytes =
                GC.GetTotalAllocatedBytes(precise: true) - phaseAllocatedBefore;
            phaseAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            phaseTimer.Restart();
        }

        bool changed = false;
        while (queue.Count > 0)
        {
            int f = queue.Dequeue();
            inQueue[f] = false;

            if (!active[f])
                continue;

            // Don't remove the very last face (mirrors original "don't remove all" guard)
            if (activeFaceCount <= 1)
                break;

            active[f] = false;
            changed = true;
            activeFaceCount--;

            // Incrementally update edge counts; enqueue neighbors that become newly boundary
            for (int e = 0; e < 3; e++)
            {
                int a = faces[f * 3 + e];
                int b = faces[f * 3 + (e + 1) % 3];
                long key = IndexedMeshTools.GetEdgeKey(a, b);

                int oldCount = edgeCounts.GetValueOrDefault(key, 0);
                if (oldCount <= 1)
                    edgeCounts.Remove(key);
                else
                    edgeCounts[key] = oldCount - 1;

                // Edge just became naked — the other face sharing it is now a boundary face
                if (oldCount == 2)
                {
                    int neighbor = GetNeighborFace(edgeToFaces, key, f);
                    if (neighbor >= 0 && active[neighbor] && !inQueue[neighbor])
                    {
                        int n0 = faces[neighbor * 3], n1 = faces[neighbor * 3 + 1], n2 = faces[neighbor * 3 + 2];
                        if (ShouldPeelBoundaryTriangle(
                            vertices,
                            n0,
                            n1,
                            n2,
                            spatialIndex,
                            effectiveThreshold,
                            canUseEdgeAngle,
                            canUseSlope,
                            settings))
                        {
                            queue.Enqueue(neighbor);
                            inQueue[neighbor] = true;
                        }
                    }
                }
            }
        }
        if (performanceTimings != null)
        {
            performanceTimings.RemovedFaceCount = faceCount - activeFaceCount;
            performanceTimings.PeelMilliseconds = phaseTimer!.Elapsed.TotalMilliseconds;
            performanceTimings.PeelAllocatedBytes =
                GC.GetTotalAllocatedBytes(precise: true) - phaseAllocatedBefore;
            phaseAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            phaseTimer.Restart();
        }

        if (!changed)
            return new Result(false, faces, faceCount, Array.Empty<int>(), vertexCount);

        var filteredFaces = new int[activeFaceCount * 3];
        int nextFace = 0;
        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            if (!active[faceIndex])
                continue;

            Array.Copy(faces, faceIndex * 3, filteredFaces, nextFace * 3, 3);
            nextFace++;
        }

        var compact = IndexedMeshTools.Compact(vertexCount, filteredFaces, activeFaceCount);
        if (performanceTimings != null)
        {
            performanceTimings.CompactionMilliseconds = phaseTimer!.Elapsed.TotalMilliseconds;
            performanceTimings.CompactionAllocatedBytes =
                GC.GetTotalAllocatedBytes(precise: true) - phaseAllocatedBefore;
        }

        return new Result(true, compact.Faces, compact.FaceCount, compact.NewToOld, compact.VertexCount);
    }

    internal static Result CullUsingNativeAdjacency(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        double[] inputXy,
        int[] inputSegments,
        BoundaryTrianglePeelSettings? settings,
        TriangleNetExtractor.NativeAdjacency adjacency,
        PerformanceTimings? performanceTimings = null)
    {
        if (!adjacency.IsValid || adjacency.FaceCount != faceCount)
        {
            return Cull(
                vertices,
                vertexCount,
                faces,
                faceCount,
                inputXy,
                inputSegments,
                settings,
                performanceTimings);
        }

        Stopwatch? phaseTimer = performanceTimings != null ? Stopwatch.StartNew() : null;
        long phaseAllocatedBefore = performanceTimings != null
            ? GC.GetTotalAllocatedBytes(precise: true)
            : 0;
        settings ??= BoundaryTrianglePeelSettings.Default;
        if (faceCount <= 0 || !settings.Enabled || settings.MaxBoundaryEdgeLength < 0)
            return new Result(false, faces, faceCount, Array.Empty<int>(), vertexCount);

        double effectiveThreshold = settings.MaxBoundaryEdgeLength > 0
            ? settings.MaxBoundaryEdgeLength
            : ComputeAutoThreshold(vertices, faces, faceCount, adjacency);
        if (performanceTimings != null)
        {
            performanceTimings.AutoThresholdMilliseconds = phaseTimer!.Elapsed.TotalMilliseconds;
            performanceTimings.AutoThresholdAllocatedBytes =
                GC.GetTotalAllocatedBytes(precise: true) - phaseAllocatedBefore;
            phaseAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            phaseTimer.Restart();
        }

        bool canUseEdgeAngle =
            effectiveThreshold > 0 &&
            !double.IsNaN(effectiveThreshold) &&
            !double.IsInfinity(effectiveThreshold) &&
            settings.MaxInteriorAngleDegrees > 0 &&
            !double.IsNaN(settings.MaxInteriorAngleDegrees) &&
            !double.IsInfinity(settings.MaxInteriorAngleDegrees);
        bool canUseSlope =
            settings.MaxSlopeAngleDegrees > 0 &&
            !double.IsNaN(settings.MaxSlopeAngleDegrees) &&
            !double.IsInfinity(settings.MaxSlopeAngleDegrees);

        if (!canUseEdgeAngle && !canUseSlope && inputSegments.Length == 0)
            return new Result(false, faces, faceCount, Array.Empty<int>(), vertexCount);

        var spatialIndex = ConstraintSpatialIndex.Build(inputXy, inputSegments);
        if (performanceTimings != null)
        {
            performanceTimings.ConstraintIndexMilliseconds = phaseTimer!.Elapsed.TotalMilliseconds;
            performanceTimings.ConstraintIndexAllocatedBytes =
                GC.GetTotalAllocatedBytes(precise: true) - phaseAllocatedBefore;
            phaseAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            phaseTimer.Restart();
        }

        var active = new bool[faceCount];
        Array.Fill(active, true);
        int activeFaceCount = faceCount;
        var inQueue = new bool[faceCount];
        var queue = new Queue<int>();
        if (performanceTimings != null)
        {
            performanceTimings.AdjacencySetupMilliseconds = phaseTimer!.Elapsed.TotalMilliseconds;
            performanceTimings.AdjacencySetupAllocatedBytes =
                GC.GetTotalAllocatedBytes(precise: true) - phaseAllocatedBefore;
            phaseAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            phaseTimer.Restart();
        }

        for (int face = 0; face < faceCount; face++)
        {
            int i0 = faces[face * 3];
            int i1 = faces[face * 3 + 1];
            int i2 = faces[face * 3 + 2];
            if (HasNativeNakedEdge(adjacency, face) &&
                ShouldPeelBoundaryTriangle(
                    vertices,
                    i0,
                    i1,
                    i2,
                    spatialIndex,
                    effectiveThreshold,
                    canUseEdgeAngle,
                    canUseSlope,
                    settings))
            {
                queue.Enqueue(face);
                inQueue[face] = true;
            }
        }
        if (performanceTimings != null)
        {
            performanceTimings.InitialQueuedFaceCount = queue.Count;
            performanceTimings.SeedQueueMilliseconds = phaseTimer!.Elapsed.TotalMilliseconds;
            performanceTimings.SeedQueueAllocatedBytes =
                GC.GetTotalAllocatedBytes(precise: true) - phaseAllocatedBefore;
            phaseAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            phaseTimer.Restart();
        }

        bool changed = false;
        while (queue.Count > 0)
        {
            int face = queue.Dequeue();
            inQueue[face] = false;

            if (!active[face])
                continue;

            if (activeFaceCount <= 1)
                break;

            active[face] = false;
            changed = true;
            activeFaceCount--;

            for (int localEdge = 0; localEdge < 3; localEdge++)
            {
                int neighbor = adjacency.GetNeighborFace(face, localEdge);
                if (neighbor < 0 || !active[neighbor] || inQueue[neighbor])
                    continue;

                int n0 = faces[neighbor * 3];
                int n1 = faces[neighbor * 3 + 1];
                int n2 = faces[neighbor * 3 + 2];
                if (ShouldPeelBoundaryTriangle(
                    vertices,
                    n0,
                    n1,
                    n2,
                    spatialIndex,
                    effectiveThreshold,
                    canUseEdgeAngle,
                    canUseSlope,
                    settings))
                {
                    queue.Enqueue(neighbor);
                    inQueue[neighbor] = true;
                }
            }
        }
        if (performanceTimings != null)
        {
            performanceTimings.RemovedFaceCount = faceCount - activeFaceCount;
            performanceTimings.PeelMilliseconds = phaseTimer!.Elapsed.TotalMilliseconds;
            performanceTimings.PeelAllocatedBytes =
                GC.GetTotalAllocatedBytes(precise: true) - phaseAllocatedBefore;
            phaseAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            phaseTimer.Restart();
        }

        if (!changed)
            return new Result(false, faces, faceCount, Array.Empty<int>(), vertexCount);

        var filteredFaces = new int[activeFaceCount * 3];
        int nextFace = 0;
        for (int face = 0; face < faceCount; face++)
        {
            if (!active[face])
                continue;

            Array.Copy(faces, face * 3, filteredFaces, nextFace * 3, 3);
            nextFace++;
        }

        var compact = IndexedMeshTools.Compact(vertexCount, filteredFaces, activeFaceCount);
        if (performanceTimings != null)
        {
            performanceTimings.CompactionMilliseconds = phaseTimer!.Elapsed.TotalMilliseconds;
            performanceTimings.CompactionAllocatedBytes =
                GC.GetTotalAllocatedBytes(precise: true) - phaseAllocatedBefore;
        }

        return new Result(true, compact.Faces, compact.FaceCount, compact.NewToOld, compact.VertexCount);
    }

    private static bool HasNativeNakedEdge(
        TriangleNetExtractor.NativeAdjacency adjacency,
        int face)
    {
        return adjacency.GetNeighborFace(face, 0) < 0 ||
               adjacency.GetNeighborFace(face, 1) < 0 ||
               adjacency.GetNeighborFace(face, 2) < 0;
    }

    private static bool ShouldPeelBoundaryTriangle(
        double[] vertices,
        int i0,
        int i1,
        int i2,
        ConstraintSpatialIndex spatialIndex,
        double effectiveEdgeThreshold,
        bool canUseEdgeAngle,
        bool canUseSlope,
        BoundaryTrianglePeelSettings settings)
    {
        if (TriangleCrossesConstraint(vertices, i0, i1, i2, spatialIndex))
            return true;

        if (canUseEdgeAngle &&
            IsDegenerateBoundaryTriangle(vertices, i0, i1, i2, effectiveEdgeThreshold, settings.MaxInteriorAngleDegrees))
        {
            return true;
        }

        return canUseSlope &&
               IsSteepBoundaryTriangle(vertices, i0, i1, i2, settings.MaxSlopeAngleDegrees);
    }

    internal static double ComputeAutoThreshold(double[] vertices, IndexedMeshTools.EdgeTopology topology)
    {
        if (topology.EdgeCount == 0)
            return 0;

        var lengths = new double[topology.EdgeCount];
        for (int i = 0; i < topology.EdgeCount; i++)
        {
            int a = topology.Edges[i * 2];
            int b = topology.Edges[i * 2 + 1];
            lengths[i] = Distance2D(vertices, a, b);
        }

        Array.Sort(lengths);
        double median = lengths[lengths.Length / 2];
        return median * 4.0;
    }

    internal static double ComputeAutoThreshold(
        double[] vertices,
        int[] faces,
        int faceCount,
        TriangleNetExtractor.NativeAdjacency adjacency)
    {
        if (!adjacency.IsValid || adjacency.FaceCount != faceCount || faceCount == 0)
            return 0;

        int interiorEdgeReferences = checked((faceCount * 3) - adjacency.BoundaryEdgeCount);
        if ((interiorEdgeReferences & 1) != 0)
            return 0;

        int uniqueEdgeCount = checked(adjacency.BoundaryEdgeCount + (interiorEdgeReferences / 2));
        var lengths = new double[uniqueEdgeCount];
        int lengthCount = 0;
        for (int face = 0; face < faceCount; face++)
        {
            for (int localEdge = 0; localEdge < 3; localEdge++)
            {
                int neighbor = adjacency.GetNeighborFace(face, localEdge);
                if (neighbor >= 0 && face > neighbor)
                    continue;

                int a = faces[face * 3 + localEdge];
                int b = faces[face * 3 + ((localEdge + 1) % 3)];
                lengths[lengthCount++] = Distance2D(vertices, a, b);
            }
        }

        if (lengthCount != uniqueEdgeCount)
            return 0;

        Array.Sort(lengths);
        double median = lengths[lengths.Length / 2];
        return median * 4.0;
    }

    private static double ComputeAutoThreshold(double[] vertices, int[] faces, int faceCount)
    {
        var topology = IndexedMeshTools.BuildEdgeTopology(faces, faceCount);
        return ComputeAutoThreshold(vertices, topology);
    }

    private static bool HasNakedEdge(int i0, int i1, int i2, Dictionary<long, int> edgeCounts)
    {
        return edgeCounts.GetValueOrDefault(IndexedMeshTools.GetEdgeKey(i0, i1), 0) == 1 ||
               edgeCounts.GetValueOrDefault(IndexedMeshTools.GetEdgeKey(i1, i2), 0) == 1 ||
               edgeCounts.GetValueOrDefault(IndexedMeshTools.GetEdgeKey(i2, i0), 0) == 1;
    }

    private static int GetNeighborFace(Dictionary<long, (int F0, int F1)> edgeToFaces, long key, int excludeFace)
    {
        if (!edgeToFaces.TryGetValue(key, out var pair))
            return -1;
        return pair.F0 == excludeFace ? pair.F1 : pair.F0;
    }

    private static bool TriangleCrossesConstraint(double[] vertices, int i0, int i1, int i2, ConstraintSpatialIndex spatialIndex)
    {
        if (spatialIndex.Count == 0)
            return false;

        return EdgeCrossesConstraint(vertices, i0, i1, spatialIndex) ||
               EdgeCrossesConstraint(vertices, i1, i2, spatialIndex) ||
               EdgeCrossesConstraint(vertices, i2, i0, spatialIndex);
    }

    private static bool EdgeCrossesConstraint(double[] vertices, int a, int b, ConstraintSpatialIndex spatialIndex)
    {
        double ax = vertices[a * 3];
        double ay = vertices[a * 3 + 1];
        double bx = vertices[b * 3];
        double by = vertices[b * 3 + 1];

        foreach (int segIndex in spatialIndex.Query(ax, ay, bx, by))
        {
            spatialIndex.GetSegment(segIndex, out double sx0, out double sy0, out double sx1, out double sy1);
            if (ProperSegmentsIntersect(ax, ay, bx, by, sx0, sy0, sx1, sy1))
                return true;
        }

        return false;
    }

    private static bool IsDegenerateBoundaryTriangle(
        double[] vertices,
        int i0,
        int i1,
        int i2,
        double maxEdgeThreshold,
        double maxAngleDegrees)
    {
        double len01 = Distance2D(vertices, i0, i1);
        double len12 = Distance2D(vertices, i1, i2);
        double len20 = Distance2D(vertices, i2, i0);
        double longest = Math.Max(len01, Math.Max(len12, len20));

        if (longest <= maxEdgeThreshold)
            return false;

        return ComputeMaxAngleDegrees(len01, len12, len20) >= maxAngleDegrees;
    }

    private static bool IsSteepBoundaryTriangle(double[] vertices, int i0, int i1, int i2, double maxSlopeAngleDegrees)
    {
        double ax = vertices[i0 * 3];
        double ay = vertices[i0 * 3 + 1];
        double az = vertices[i0 * 3 + 2];
        double bx = vertices[i1 * 3];
        double by = vertices[i1 * 3 + 1];
        double bz = vertices[i1 * 3 + 2];
        double cx = vertices[i2 * 3];
        double cy = vertices[i2 * 3 + 1];
        double cz = vertices[i2 * 3 + 2];

        double ux = bx - ax;
        double uy = by - ay;
        double uz = bz - az;
        double vx = cx - ax;
        double vy = cy - ay;
        double vz = cz - az;

        double nx = (uy * vz) - (uz * vy);
        double ny = (uz * vx) - (ux * vz);
        double nz = (ux * vy) - (uy * vx);
        double horizontalNormalLength = Math.Sqrt((nx * nx) + (ny * ny));
        double verticalNormalMagnitude = Math.Abs(nz);
        double normalLength = Math.Sqrt((horizontalNormalLength * horizontalNormalLength) + (verticalNormalMagnitude * verticalNormalMagnitude));
        if (!(normalLength > 1e-12) || double.IsNaN(normalLength) || double.IsInfinity(normalLength))
            return false;

        double slopeAngle = Math.Atan2(horizontalNormalLength, verticalNormalMagnitude) * 180.0 / Math.PI;
        return slopeAngle >= maxSlopeAngleDegrees;
    }

    private static double ComputeMaxAngleDegrees(double len01, double len12, double len20)
    {
        double angle0 = AngleDegrees(len20, len01, len12);
        double angle1 = AngleDegrees(len01, len12, len20);
        double angle2 = AngleDegrees(len12, len20, len01);
        return Math.Max(angle0, Math.Max(angle1, angle2));
    }

    private static double AngleDegrees(double adjA, double adjB, double opposite)
    {
        if (adjA <= 1e-12 || adjB <= 1e-12)
            return 180.0;

        double cos = (adjA * adjA + adjB * adjB - opposite * opposite) / (2.0 * adjA * adjB);
        cos = Math.Clamp(cos, -1.0, 1.0);
        return Math.Acos(cos) * 180.0 / Math.PI;
    }

    private static double Distance2D(double[] vertices, int a, int b)
    {
        double dx = vertices[a * 3] - vertices[b * 3];
        double dy = vertices[a * 3 + 1] - vertices[b * 3 + 1];
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static bool ProperSegmentsIntersect(
        double ax, double ay, double bx, double by,
        double cx, double cy, double dx, double dy)
    {
        const double eps = 1e-9;

        double abC = Geometry2D.Orient(ax, ay, bx, by, cx, cy);
        double abD = Geometry2D.Orient(ax, ay, bx, by, dx, dy);
        if (Math.Abs(abC) <= eps || Math.Abs(abD) <= eps)
            return false;

        if ((abC > 0) == (abD > 0))
            return false;

        double cdA = Geometry2D.Orient(cx, cy, dx, dy, ax, ay);
        double cdB = Geometry2D.Orient(cx, cy, dx, dy, bx, by);
        if (Math.Abs(cdA) <= eps || Math.Abs(cdB) <= eps)
            return false;

        return (cdA > 0) != (cdB > 0);
    }

    private sealed class ConstraintSpatialIndex
    {
        private readonly Bounds2D[] _bounds;
        private readonly bool[] _valid;
        private readonly SpatialHashGrid2D _grid;
        private readonly SpatialHashGrid2D.QueryScratch _scratch;
        private readonly List<int> _queryCandidates = new(16);
        private readonly List<int> _candidates = new(16);
        private readonly double[] _xy;
        private readonly int[] _segments;

        public int Count { get; }

        private ConstraintSpatialIndex(
            double[] xy,
            int[] segments,
            Bounds2D[] bounds,
            bool[] valid,
            SpatialHashGrid2D grid,
            int count)
        {
            _xy = xy;
            _segments = segments;
            _bounds = bounds;
            _valid = valid;
            _grid = grid;
            _scratch = new SpatialHashGrid2D.QueryScratch(bounds.Length);
            Count = count;
        }

        public static ConstraintSpatialIndex Build(double[] xy, int[] segments)
        {
            int vertexCount = xy.Length / 2;
            int segmentCount = segments.Length / 2;
            var bounds = new Bounds2D[segmentCount];
            var valid = new bool[segmentCount];

            if (segmentCount == 0 || vertexCount == 0)
            {
                return new ConstraintSpatialIndex(
                    xy,
                    segments,
                    bounds,
                    valid,
                    SpatialHashGrid2D.Build(bounds, valid),
                    0);
            }

            int validCount = 0;

            for (int i = 0; i < segmentCount; i++)
            {
                int a = segments[i * 2];
                int b = segments[i * 2 + 1];
                if ((uint)a >= (uint)vertexCount || (uint)b >= (uint)vertexCount || a == b)
                    continue;

                double ax = xy[a * 2];
                double ay = xy[a * 2 + 1];
                double bx = xy[b * 2];
                double by = xy[b * 2 + 1];
                if ((Math.Abs(ax - bx) + Math.Abs(ay - by)) <= 1e-12)
                    continue;

                bounds[i] = new Bounds2D(
                    Math.Min(ax, bx),
                    Math.Max(ax, bx),
                    Math.Min(ay, by),
                    Math.Max(ay, by));
                valid[i] = true;
                validCount++;
            }

            if (validCount == 0)
            {
                return new ConstraintSpatialIndex(
                    xy,
                    segments,
                    bounds,
                    valid,
                    SpatialHashGrid2D.Build(bounds, valid),
                    0);
            }

            return new ConstraintSpatialIndex(
                xy,
                segments,
                bounds,
                valid,
                SpatialHashGrid2D.Build(bounds, valid),
                validCount);
        }

        public IReadOnlyList<int> Query(double ax, double ay, double bx, double by)
        {
            _candidates.Clear();
            if (Count == 0)
                return _candidates;

            Bounds2D queryBounds = new(
                Math.Min(ax, bx),
                Math.Max(ax, bx),
                Math.Min(ay, by),
                Math.Max(ay, by));
            _grid.GatherCandidates(queryBounds, _queryCandidates, _scratch);
            foreach (int segIndex in _queryCandidates)
            {
                if (!_valid[segIndex] || !_bounds[segIndex].Intersects(queryBounds))
                    continue;

                _candidates.Add(segIndex);
            }

            return _candidates;
        }

        public void GetSegment(int segIndex, out double ax, out double ay, out double bx, out double by)
        {
            int a = _segments[segIndex * 2];
            int b = _segments[segIndex * 2 + 1];
            ax = _xy[a * 2];
            ay = _xy[a * 2 + 1];
            bx = _xy[b * 2];
            by = _xy[b * 2 + 1];
        }
    }
}
