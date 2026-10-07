using System.Diagnostics;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using MoleHill.Rhino.Model;
using MoleHill.Shared;
using Rhino.Geometry;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

// Constraint and mesh-topology utilities: constraint preparation/insertion, boundary-loop analysis, tiny-face cleanup, and mesh remapping.
internal sealed partial class TerrainBuildService
{
    internal static bool IsTopologyInsertionBoundarySafe(
        MeshTopologyValidator.BoundaryGraphAnalysis inputBoundary,
        MeshTopologyValidator.BoundaryGraphAnalysis outputBoundary,
        out string message)
    {
        if (inputBoundary.HasSingleClosedBoundaryLoop && !outputBoundary.HasSingleClosedBoundaryLoop)
        {
            message = "it would break the original single closed terrain boundary.";
            return false;
        }

        if (!inputBoundary.HasOpenBoundaryChains && outputBoundary.HasOpenBoundaryChains)
        {
            message = "it would create open naked-edge chains.";
            return false;
        }

        if (outputBoundary.BoundaryComponentCount > inputBoundary.BoundaryComponentCount)
        {
            message = $"it would create extra boundary loops ({inputBoundary.BoundaryComponentCount} -> {outputBoundary.BoundaryComponentCount}).";
            return false;
        }

        message = string.Empty;
        return true;
    }

    internal static bool TopologyChanged(
        double[] inputVertices,
        int inputVertexCount,
        int[] inputFaces,
        int inputFaceCount,
        double[] outputVertices,
        int outputVertexCount,
        int[] outputFaces,
        int outputFaceCount,
        double tolerance)
    {
        if (outputVertexCount != inputVertexCount || outputFaceCount != inputFaceCount)
            return true;

        double tolSq = Math.Max(Math.Abs(tolerance), double.Epsilon);
        tolSq *= tolSq;
        for (int i = 0; i < inputVertexCount; i++)
        {
            double dx = inputVertices[i * 3] - outputVertices[i * 3];
            double dy = inputVertices[i * 3 + 1] - outputVertices[i * 3 + 1];
            double dz = inputVertices[i * 3 + 2] - outputVertices[i * 3 + 2];
            if ((dx * dx) + (dy * dy) + (dz * dz) > tolSq)
                return true;
        }

        for (int i = 0; i < inputFaceCount * 3; i++)
        {
            if (inputFaces[i] != outputFaces[i])
                return true;
        }

        return false;
    }

    internal static void ApplyPreservedConstraintElevations(
        double[] vertices,
        IReadOnlyList<ConstraintPolyline> constraints,
        double tolerance)
    {
        double matchTolerance = Math.Max(Math.Abs(tolerance), double.Epsilon);
        double matchToleranceSquared = matchTolerance * matchTolerance;
        int vertexCount = vertices.Length / 3;
        for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
        {
            double x = vertices[vertexIndex * 3];
            double y = vertices[vertexIndex * 3 + 1];
            if (TryGetPreservedConstraintElevation(x, y, constraints, matchToleranceSquared, out double z))
                vertices[vertexIndex * 3 + 2] = z;
        }
    }

    private static bool TryGetPreservedConstraintElevation(
        double x,
        double y,
        IReadOnlyList<ConstraintPolyline> constraints,
        double maxDistanceSquared,
        out double z)
    {
        z = 0.0;
        bool found = false;
        double bestDistanceSquared = maxDistanceSquared;
        foreach (ConstraintPolyline constraint in constraints)
        {
            if (!constraint.PreserveInputElevation || constraint.PointCount < 2)
                continue;

            // A closed constraint's last segment runs back to its first point; without it, every vertex
            // on that side of a closed wall rail kept the terrain's height instead of the rail's.
            int segmentCount = constraint.IsClosed && constraint.PointCount > 2 ? constraint.PointCount : constraint.PointCount - 1;
            for (int segment = 0; segment < segmentCount; segment++)
            {
                if (!TryProjectToConstraintSegment(
                        constraint,
                        segment,
                        (segment + 1) % constraint.PointCount,
                        x,
                        y,
                        bestDistanceSquared,
                        out double candidateZ,
                        out double distanceSquared))
                {
                    continue;
                }

                bestDistanceSquared = distanceSquared;
                z = candidateZ;
                found = true;
            }
        }

        return found;
    }

    private static bool TryProjectToConstraintSegment(
        ConstraintPolyline constraint,
        int startPointIndex,
        int endPointIndex,
        double x,
        double y,
        double maxDistanceSquared,
        out double z,
        out double distanceSquared)
    {
        double ax = constraint.Points[startPointIndex * 3];
        double ay = constraint.Points[startPointIndex * 3 + 1];
        double az = constraint.Points[startPointIndex * 3 + 2];
        double bx = constraint.Points[endPointIndex * 3];
        double by = constraint.Points[endPointIndex * 3 + 1];
        double bz = constraint.Points[endPointIndex * 3 + 2];
        double dx = bx - ax;
        double dy = by - ay;
        double lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared <= 1e-12)
        {
            z = 0.0;
            distanceSquared = double.PositiveInfinity;
            return false;
        }

        double t = (((x - ax) * dx) + ((y - ay) * dy)) / lengthSquared;
        if (t < 0.0 || t > 1.0)
        {
            z = 0.0;
            distanceSquared = double.PositiveInfinity;
            return false;
        }

        double closestX = ax + (dx * t);
        double closestY = ay + (dy * t);
        double offsetX = x - closestX;
        double offsetY = y - closestY;
        distanceSquared = (offsetX * offsetX) + (offsetY * offsetY);
        if (distanceSquared > maxDistanceSquared)
        {
            z = 0.0;
            return false;
        }

        z = az + ((bz - az) * t);
        return true;
    }

    internal static double DistanceSquared2D(Point3d a, Point3d b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return (dx * dx) + (dy * dy);
    }

    internal static RhinoMesh RebuildMeshWithConstraints(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        IReadOnlyList<ConstraintPolyline> constraints,
        double requestedEdgeLength,
        double maxArea,
        double minAngle,
        string label,
        TerrainBuildResult build,
        out bool keptInputMesh,
        bool preferReducedInteriorSeed = false,
        bool addReducedInteriorGuideSeeds = true,
        bool addConstraintCorridorSeeds = true,
        double? toleranceOverride = null,
        bool protectSharpEdges = true,
        bool recordDetailedTimings = false,
        double vertexMergeTolerance = 0.0,
        double preserveCreaseAngleDeg = 0.0)
    {
        keptInputMesh = false;
        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var originalVertices, out _, out var originalFaces, out _, out var errorMessage))
        {
            build.Diagnostics.Add(errorMessage ?? $"Could not extract mesh data for {label.ToLowerInvariant()}.");
            return mesh;
        }

        double tolerance = toleranceOverride ?? GetToleranceProfile(snapshot, terrain).RemeshConstraintTolerance;
        var remeshCoreTimer = Stopwatch.StartNew();
        var remeshResult = SurfaceRemesher.Remesh(
            originalVertices,
            originalFaces,
            constraints,
            new SurfaceRemesher.Options
            {
                Tolerance = tolerance,
                RequestedEdgeLength = requestedEdgeLength,
                MaxArea = maxArea,
                MinAngle = minAngle,
                ProtectSharpEdges = protectSharpEdges,
                PreferReducedInteriorSeed = preferReducedInteriorSeed,
                AddReducedInteriorGuideSeeds = addReducedInteriorGuideSeeds,
                AddConstraintCorridorSeeds = addConstraintCorridorSeeds,
                // When no quality params are set, RequestedEdgeLength controls only constraint
                // pre-densification spacing — do not use it to drive Steiner interior refinement.
                ConstraintInsertionOnly = maxArea <= 0 && minAngle <= 0,
                VertexMergeTolerance = vertexMergeTolerance,
                PreserveCreaseAngleDeg = preserveCreaseAngleDeg
            });
        remeshCoreTimer.Stop();
        if (recordDetailedTimings)
        {
            build.RecordTiming(
                $"{label} SurfaceRemesher",
                remeshCoreTimer.Elapsed,
                remeshResult.Success ? "success" : "failed",
                StageTimingDiagnosticThresholdMs);
        }

        if (remeshResult.Profile is not null)
        {
            string remeshTimingReport = remeshResult.Profile.FormatReport($"{label} remesh timing");
            build.Diagnostics.Add(remeshTimingReport);
            Debug.WriteLine(remeshTimingReport);
        }

        if (!remeshResult.Success)
        {
            keptInputMesh = remeshResult.ReturnedInputMesh;
            if (remeshResult.ReturnedInputMesh)
            {
                build.Diagnostics.Add(
                    $"{label} remesh attempted breakline insertion but kept the incoming mesh unchanged; grading will continue on the existing topology.");
            }
            build.Diagnostics.Add(remeshResult.Warning ?? $"{label} triangulation failed.");
            return mesh;
        }

        if (remeshResult.AddedProtectedVertices > 0)
            build.Diagnostics.Add($"{label} added {remeshResult.AddedProtectedVertices} protected-edge vertices before triangulation.");

        if (remeshResult.UsedBoundaryAndGuideSeedFallback)
            build.Diagnostics.Add($"{label} retried from boundary, hard-constraint, and coarse interior guide seeds because carried mesh vertices prevented refinement.");

        if (!string.IsNullOrWhiteSpace(remeshResult.Warning))
            build.Diagnostics.Add(remeshResult.Warning);

        var meshBuildTimer = Stopwatch.StartNew();
        RhinoMesh rawMesh = BuildMeshFromArrays(remeshResult.Vertices, remeshResult.Faces);
        meshBuildTimer.Stop();
        if (recordDetailedTimings)
        {
            build.RecordTiming(
                $"{label} Mesh Build",
                meshBuildTimer.Elapsed,
                $"{rawMesh.Vertices.Count:N0} verts, {rawMesh.Faces.Count:N0} faces",
                StageTimingDiagnosticThresholdMs);
        }

        var cleanupTimer = Stopwatch.StartNew();
        RhinoMesh cleanedMesh = CleanTinyFaces(rawMesh, tolerance, label, build);
        cleanupTimer.Stop();
        if (recordDetailedTimings)
        {
            build.RecordTiming(
                $"{label} Tiny Cleanup",
                cleanupTimer.Elapsed,
                ReferenceEquals(cleanedMesh, rawMesh)
                    ? "unchanged"
                    : $"{cleanedMesh.Vertices.Count:N0} verts, {cleanedMesh.Faces.Count:N0} faces",
                StageTimingDiagnosticThresholdMs);
        }

        return cleanedMesh;
    }

    internal static string DescribeTopologyCounts(int inputVertexCount, int inputFaceCount, int outputVertexCount, int outputFaceCount)
    {
        return $"{inputVertexCount:N0} verts/{inputFaceCount:N0} faces -> {outputVertexCount:N0} verts/{outputFaceCount:N0} faces";
    }

    internal static List<ConstraintPolyline> CombineConstraints(
        IReadOnlyList<ConstraintPolyline> persistentConstraints,
        IReadOnlyList<ConstraintPolyline> additionalConstraints)
    {
        var seen = new HashSet<ConstraintSignature>();
        var result = new List<ConstraintPolyline>(persistentConstraints.Count + additionalConstraints.Count);
        AppendUniqueConstraints(result, seen, persistentConstraints);
        AppendUniqueConstraints(result, seen, additionalConstraints);
        return result;
    }

    private static void AppendUniqueConstraints(
        List<ConstraintPolyline> destination,
        HashSet<ConstraintSignature> seen,
        IReadOnlyList<ConstraintPolyline> constraints)
    {
        foreach (var constraint in constraints)
        {
            if (constraint.PointCount < 2)
                continue;

            if (seen.Add(CreateConstraintSignature(constraint)))
                destination.Add(constraint);
        }
    }

    private static ConstraintSignature CreateConstraintSignature(ConstraintPolyline constraint)
    {
        var fingerprint = new FingerprintBuilder();
        fingerprint.Add(constraint.PointCount);
        fingerprint.Add(constraint.IsClosed);
        fingerprint.Add(constraint.PreserveInputElevation);

        int pointValueCount = Math.Min(constraint.Points.Length, constraint.PointCount * 3);
        for (int i = 0; i < pointValueCount; i++)
            fingerprint.Add(constraint.Points[i]);

        return new ConstraintSignature(
            fingerprint.ToUInt64(),
            constraint.PointCount,
            constraint.IsClosed,
            constraint.PreserveInputElevation);
    }

    internal static List<ConstraintPolyline> CreateConstraintPolylines(
        IReadOnlyList<Curve> curves,
        double tolerance,
        bool preserveInputElevation,
        double requestedEdgeLength = 0.0,
        double maxArea = 0.0)
    {
        if (requestedEdgeLength <= 0.0 && maxArea <= 0.0)
        {
            return CreateConstraintPolylines(
                TerrainTriangulationInputBuilder.CreateFlattenedPolylines(curves, tolerance),
                preserveInputElevation);
        }

        var result = new List<ConstraintPolyline>();
        foreach (var curve in curves)
        {
            if (curve == null)
                continue;

            if (!RhinoSourceResolver.TryGetPolyline(curve, tolerance, requireClosed: false, requestedEdgeLength, maxArea, out var polyline))
                continue;

            result.Add(RhinoGeometryConversions.ToConstraintPolyline(polyline, curve.IsClosed, preserveInputElevation));
        }

        return result;
    }

    internal static List<ConstraintPolyline> CreateConstraintPolylines(
        IReadOnlyList<TerrainTriangulationInputBuilder.FlattenedPolyline> polylines,
        bool preserveInputElevation)
    {
        var result = new List<ConstraintPolyline>(polylines.Count);
        foreach (TerrainTriangulationInputBuilder.FlattenedPolyline polyline in polylines)
        {
            if (polyline.Points.Length < 6)
                continue;

            result.Add(new ConstraintPolyline(
                polyline.Points,
                polyline.Points.Length / 3,
                polyline.IsClosed,
                preserveInputElevation));
        }

        return result;
    }

    /// <summary>
    /// Constraints from preprocessed points, keeping each source polyline's closed flag.
    /// <paramref name="processed"/> is aligned with <paramref name="sources"/>; a null entry was dropped.
    /// </summary>
    internal static List<ConstraintPolyline> CreateConstraintPolylines(
        IReadOnlyList<TerrainTriangulationInputBuilder.FlattenedPolyline> sources,
        IReadOnlyList<double[]?> processed,
        bool preserveInputElevation)
    {
        var result = new List<ConstraintPolyline>(processed.Count);
        for (int i = 0; i < processed.Count && i < sources.Count; i++)
        {
            double[]? points = processed[i];
            if (points == null || points.Length < 6)
                continue;

            result.Add(new ConstraintPolyline(
                points,
                points.Length / 3,
                sources[i].IsClosed,
                preserveInputElevation));
        }

        return result;
    }

    private static List<double[]> CreateFlatPolylines(IReadOnlyList<Curve> curves, double tolerance)
    {
        return TerrainTriangulationInputBuilder.CreateFlatPolylines(curves, tolerance);
    }

    private static List<double[]> CreateFlatPolylines(IReadOnlyList<ConstraintPolyline> constraints)
    {
        var result = new List<double[]>(constraints.Count);
        foreach (var constraint in constraints)
        {
            if (constraint.PointCount < 2)
                continue;

            result.Add((double[])constraint.Points.Clone());
        }

        return result;
    }

    private static double[] ToFlatPolyline(Polyline polyline)
    {
        return TerrainTriangulationInputBuilder.ToFlatPolyline(polyline);
    }

    private static TinBoundaryPreparer.BoundaryPolyline[] CreateBoundaryPolylines(RhinoMesh mesh, double tolerance)
    {
        var nakedEdges = mesh.GetNakedEdges();
        if (nakedEdges == null || nakedEdges.Length == 0)
            return Array.Empty<TinBoundaryPreparer.BoundaryPolyline>();

        var result = new List<TinBoundaryPreparer.BoundaryPolyline>(nakedEdges.Length);
        foreach (var polyline in nakedEdges)
        {
            if (polyline.Count < 2)
                continue;

            bool isClosed = polyline.IsClosed || polyline[0].DistanceTo(polyline[^1]) <= tolerance;
            result.Add(new TinBoundaryPreparer.BoundaryPolyline(ToFlatPolyline(polyline), polyline.Count, isClosed));
        }

        return result.ToArray();
    }

    private static TinBoundaryPreparer.BoundaryPolyline[] CombineBoundaryPolylines(
        IReadOnlyList<TinBoundaryPreparer.BoundaryPolyline> primary,
        IReadOnlyList<TinBoundaryPreparer.BoundaryPolyline> additional)
    {
        if (primary.Count == 0 && additional.Count == 0)
            return Array.Empty<TinBoundaryPreparer.BoundaryPolyline>();

        var result = new TinBoundaryPreparer.BoundaryPolyline[primary.Count + additional.Count];
        for (int i = 0; i < primary.Count; i++)
            result[i] = primary[i];
        for (int i = 0; i < additional.Count; i++)
            result[primary.Count + i] = additional[i];
        return result;
    }

    internal static RhinoMesh BuildMeshFromArrays(double[] vertices, int[] faces) =>
        RhinoGeometryConversions.BuildMesh(vertices, vertices.Length / 3, faces, faces.Length / 3);

    internal static RhinoMesh FinalizeGradingMesh(RhinoMesh mesh, string sourceLabel, TerrainBuildResult build)
    {
        // Every caller passes BuildMesh output, which is normalized already. Normalizing again cannot
        // change it and cost a second UnifyNormals plus array read-back (~60 ms on 111k faces).
        if (!RhinoGeometryConversions.IsNormalizedMesh(mesh))
            RhinoGeometryConversions.NormalizeMeshInPlace(mesh);
        build.Diagnostics.Add($"{sourceLabel} skipped tiny-face deletion; grading output must not introduce holes.");
        return mesh;
    }

    private static RhinoMesh CleanTinyFaces(RhinoMesh mesh, double tolerance, string sourceLabel, TerrainBuildResult build)
    {
        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out _, out var faces, out int faceCount, out _))
            return mesh;

        TinyFaceCleanupResult cleanup = ComputeTinyFaceCleanup(vertices, faces, faceCount, tolerance);
        if (!cleanup.HasChanges)
        {
            if (cleanup.BlockedFaceCount > 0)
                build.Diagnostics.Add($"{sourceLabel} kept the pre-cleanup mesh because tiny-face cleanup would create extra boundary loops or open naked-edge chains.");
            return mesh;
        }

        if (cleanup.BlockedFaceCount > 0)
        {
            build.Diagnostics.Add(
                $"{sourceLabel} removed {cleanup.RemovedFaceCount} tiny faces and kept {cleanup.BlockedFaceCount} because removing them would create extra boundary loops or open naked-edge chains.");
        }
        else
        {
            build.Diagnostics.Add($"{sourceLabel} removed {cleanup.RemovedFaceCount} tiny faces.");
        }

        return BuildRemappedMesh(vertices, new List<int>(cleanup.Faces));
    }

    internal static TinyFaceCleanupResult ComputeTinyFaceCleanup(
        double[] vertices,
        int[] faces,
        int faceCount,
        double tolerance)
    {
        var originalTopology = MeshTopologyValidator.AnalyzeBoundaryGraph(faces, faceCount);
        double medianEdgeLength = ComputeMedianUndirectedEdgeLength(vertices, faces, faceCount);
        double geometryFloor = Math.Max(medianEdgeLength * 1e-12, double.Epsilon);
        double effectiveCleanupTolerance = Math.Min(
            Math.Max(Math.Abs(tolerance), geometryFloor),
            Math.Max(medianEdgeLength * 0.01, geometryFloor));
        double minEdgeLength = Math.Max(effectiveCleanupTolerance * 2.0, geometryFloor);
        double minEdgeLengthSquared = minEdgeLength * minEdgeLength;
        double minProjectedArea = Math.Max(
            effectiveCleanupTolerance * effectiveCleanupTolerance * 2.0,
            geometryFloor * geometryFloor);
        Dictionary<long, int> originalEdgeCounts = IndexedMeshTools.CountFaceEdges(faces, faceCount);

        var candidates = new List<(int FaceIndex, double Area, double SmallestEdgeSquared, double MinProjectedAltitude)>();
        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            int a = faces[faceIndex * 3];
            int b = faces[faceIndex * 3 + 1];
            int c = faces[faceIndex * 3 + 2];

            double ax = vertices[a * 3];
            double ay = vertices[a * 3 + 1];
            double az = vertices[a * 3 + 2];
            double bx = vertices[b * 3];
            double by = vertices[b * 3 + 1];
            double bz = vertices[b * 3 + 2];
            double cx = vertices[c * 3];
            double cy = vertices[c * 3 + 1];
            double cz = vertices[c * 3 + 2];

            double abx = bx - ax;
            double aby = by - ay;
            double abz = bz - az;
            double bcx = cx - bx;
            double bcy = cy - by;
            double bcz = cz - bz;
            double cax = ax - cx;
            double cay = ay - cy;
            double caz = az - cz;

            double l0Squared = (abx * abx) + (aby * aby) + (abz * abz);
            double l1Squared = (bcx * bcx) + (bcy * bcy) + (bcz * bcz);
            double l2Squared = (cax * cax) + (cay * cay) + (caz * caz);
            double area = Math.Abs((bx - ax) * (cy - ay) - (by - ay) * (cx - ax)) * 0.5;
            double smallestEdgeSquared = Math.Min(l0Squared, Math.Min(l1Squared, l2Squared));
            double projectedL0Squared = (abx * abx) + (aby * aby);
            double projectedL1Squared = (bcx * bcx) + (bcy * bcy);
            double projectedL2Squared = (cax * cax) + (cay * cay);
            double longestProjectedEdgeSquared = Math.Max(projectedL0Squared, Math.Max(projectedL1Squared, projectedL2Squared));
            double shortestProjectedEdgeSquared = Math.Min(projectedL0Squared, Math.Min(projectedL1Squared, projectedL2Squared));
            double longestProjectedEdge = Math.Sqrt(longestProjectedEdgeSquared);
            double minProjectedAltitude = longestProjectedEdge > geometryFloor
                ? (2.0 * area) / longestProjectedEdge
                : 0.0;
            double projectedDuplicateTolerance = effectiveCleanupTolerance * 0.5;
            bool projectedDuplicate = shortestProjectedEdgeSquared <= projectedDuplicateTolerance * projectedDuplicateTolerance;
            bool projectedSliver =
                longestProjectedEdge > effectiveCleanupTolerance * 8.0 &&
                minProjectedAltitude < Math.Max(effectiveCleanupTolerance * 0.5, longestProjectedEdge * 0.001);

            bool shortBoundaryEdge = HasShortBoundaryEdge(
                originalEdgeCounts,
                a,
                b,
                c,
                l0Squared,
                l1Squared,
                l2Squared,
                minEdgeLengthSquared);
            bool hasBoundaryEdge = FaceHasBoundaryEdge(originalEdgeCounts, a, b, c);

            if (shortBoundaryEdge ||
                area < minProjectedArea ||
                (projectedDuplicate && shortBoundaryEdge) ||
                (projectedSliver && hasBoundaryEdge))
                candidates.Add((faceIndex, area, smallestEdgeSquared, minProjectedAltitude));
        }

        if (candidates.Count == 0)
            return new TinyFaceCleanupResult(faces, 0, 0);

        candidates.Sort(static (left, right) =>
        {
            int compare = left.Area.CompareTo(right.Area);
            if (compare != 0)
                return compare;

            int altitudeCompare = left.MinProjectedAltitude.CompareTo(right.MinProjectedAltitude);
            if (altitudeCompare != 0)
                return altitudeCompare;

            return left.SmallestEdgeSquared.CompareTo(right.SmallestEdgeSquared);
        });

        bool[] keepFace = new bool[faceCount];
        Array.Fill(keepFace, true);

        int removedCount = 0;
        int blockedCount = 0;
        bool enforceSingleClosedBoundaryLoop = HasSingleClosedBoundaryLoopIgnoringNonManifoldEdges(originalTopology);
        Dictionary<long, int>? edgeCounts = enforceSingleClosedBoundaryLoop
            ? new Dictionary<long, int>(originalEdgeCounts, IndexedMeshTools.EdgeKeyComparer.Instance)
            : null;

        foreach (var candidate in candidates)
        {
            if (enforceSingleClosedBoundaryLoop &&
                FaceHasNoCurrentBoundaryEdges(edgeCounts!, faces, candidate.FaceIndex))
            {
                blockedCount++;
                continue;
            }

            keepFace[candidate.FaceIndex] = false;
            if (enforceSingleClosedBoundaryLoop)
            {
                ApplyFaceEdgeCountDelta(edgeCounts!, faces, candidate.FaceIndex, -1);
                var proposedTopology = AnalyzeBoundaryGraphFromEdgeCounts(edgeCounts!);
                if (!HasSingleClosedBoundaryLoopIgnoringNonManifoldEdges(proposedTopology))
                {
                    ApplyFaceEdgeCountDelta(edgeCounts!, faces, candidate.FaceIndex, 1);
                    keepFace[candidate.FaceIndex] = true;
                    blockedCount++;
                    continue;
                }
            }

            removedCount++;
        }

        if (removedCount == 0)
            return new TinyFaceCleanupResult(faces, 0, blockedCount);

        return new TinyFaceCleanupResult(
            BuildFilteredFaces(faces, faceCount, keepFace, removedCount),
            removedCount,
            blockedCount);
    }

    private static bool HasShortBoundaryEdge(
        IReadOnlyDictionary<long, int> edgeCounts,
        int a,
        int b,
        int c,
        double abLengthSquared,
        double bcLengthSquared,
        double caLengthSquared,
        double minLengthSquared)
    {
        return (abLengthSquared < minLengthSquared && IsBoundaryEdge(edgeCounts, a, b)) ||
               (bcLengthSquared < minLengthSquared && IsBoundaryEdge(edgeCounts, b, c)) ||
               (caLengthSquared < minLengthSquared && IsBoundaryEdge(edgeCounts, c, a));
    }

    private static bool IsBoundaryEdge(IReadOnlyDictionary<long, int> edgeCounts, int a, int b)
    {
        return edgeCounts.TryGetValue(IndexedMeshTools.GetEdgeKey(a, b), out int count) && count == 1;
    }

    private static bool FaceHasBoundaryEdge(IReadOnlyDictionary<long, int> edgeCounts, int a, int b, int c)
    {
        return IsBoundaryEdge(edgeCounts, a, b) ||
               IsBoundaryEdge(edgeCounts, b, c) ||
               IsBoundaryEdge(edgeCounts, c, a);
    }

    private static bool HasSingleClosedBoundaryLoopIgnoringNonManifoldEdges(MeshTopologyValidator.BoundaryGraphAnalysis topology)
    {
        return topology.BoundaryEdgeCount > 0 &&
               topology.BoundaryComponentCount == 1 &&
               !topology.HasOpenBoundaryChains;
    }


    private static bool FaceHasNoCurrentBoundaryEdges(Dictionary<long, int> edgeCounts, int[] faces, int faceIndex)
    {
        int a = faces[faceIndex * 3];
        int b = faces[faceIndex * 3 + 1];
        int c = faces[faceIndex * 3 + 2];
        return GetEdgeCount(edgeCounts, a, b) != 1 &&
               GetEdgeCount(edgeCounts, b, c) != 1 &&
               GetEdgeCount(edgeCounts, c, a) != 1;
    }

    private static int GetEdgeCount(Dictionary<long, int> edgeCounts, int a, int b)
    {
        edgeCounts.TryGetValue(IndexedMeshTools.GetEdgeKey(a, b), out int count);
        return count;
    }

    private static void ApplyFaceEdgeCountDelta(Dictionary<long, int> edgeCounts, int[] faces, int faceIndex, int delta)
    {
        int a = faces[faceIndex * 3];
        int b = faces[faceIndex * 3 + 1];
        int c = faces[faceIndex * 3 + 2];
        IncrementEdgeCount(edgeCounts, a, b, delta);
        IncrementEdgeCount(edgeCounts, b, c, delta);
        IncrementEdgeCount(edgeCounts, c, a, delta);
    }

    private static void IncrementEdgeCount(Dictionary<long, int> edgeCounts, int a, int b, int delta)
    {
        long key = IndexedMeshTools.GetEdgeKey(a, b);
        edgeCounts.TryGetValue(key, out int count);
        count += delta;
        if (count == 0)
            edgeCounts.Remove(key);
        else
            edgeCounts[key] = count;
    }

    private static MeshTopologyValidator.BoundaryGraphAnalysis AnalyzeBoundaryGraphFromEdgeCounts(
        IReadOnlyDictionary<long, int> edgeCounts)
    {
        var adjacency = new Dictionary<int, List<int>>();
        var degree = new Dictionary<int, int>();
        int boundaryEdgeCount = 0;
        int nonManifoldEdgeCount = 0;

        foreach (var pair in edgeCounts)
        {
            if (pair.Value > 2)
            {
                nonManifoldEdgeCount++;
                continue;
            }

            if (pair.Value != 1)
                continue;

            boundaryEdgeCount++;
            int a = (int)(pair.Key >> 32);
            int b = (int)(pair.Key & 0xFFFFFFFFL);
            AddBoundaryNeighbor(adjacency, degree, a, b);
            AddBoundaryNeighbor(adjacency, degree, b, a);
        }

        if (boundaryEdgeCount == 0)
            return new MeshTopologyValidator.BoundaryGraphAnalysis(0, 0, 0, HasOpenBoundaryChains: true, nonManifoldEdgeCount);

        bool hasOpenBoundaryChains = degree.Values.Any(value => value != 2);
        int boundaryComponentCount = 0;
        var visited = new HashSet<int>();

        foreach (int start in adjacency.Keys)
        {
            if (!visited.Add(start))
                continue;

            boundaryComponentCount++;
            var stack = new Stack<int>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                int current = stack.Pop();
                foreach (int next in adjacency[current])
                {
                    if (visited.Add(next))
                        stack.Push(next);
                }
            }
        }

        return new MeshTopologyValidator.BoundaryGraphAnalysis(
            boundaryEdgeCount,
            degree.Count,
            boundaryComponentCount,
            hasOpenBoundaryChains,
            nonManifoldEdgeCount);
    }

    private static void AddBoundaryNeighbor(
        Dictionary<int, List<int>> adjacency,
        Dictionary<int, int> degree,
        int from,
        int to)
    {
        if (!adjacency.TryGetValue(from, out var neighbors))
        {
            neighbors = new List<int>(2);
            adjacency[from] = neighbors;
        }

        neighbors.Add(to);
        degree[from] = degree.GetValueOrDefault(from) + 1;
    }

    private static double ComputeMedianUndirectedEdgeLength(double[] vertices, int[] faces, int faceCount)
    {
        var edgeLengths = new List<double>(faceCount * 3);
        var seen = IndexedMeshTools.CreateEdgeKeySet(faceCount * 2);

        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            int a = faces[faceIndex * 3];
            int b = faces[faceIndex * 3 + 1];
            int c = faces[faceIndex * 3 + 2];
            AddEdgeLength(a, b);
            AddEdgeLength(b, c);
            AddEdgeLength(c, a);
        }

        if (edgeLengths.Count == 0)
            return 0.0;

        edgeLengths.Sort();
        int middle = edgeLengths.Count / 2;
        return edgeLengths.Count % 2 == 0
            ? (edgeLengths[middle - 1] + edgeLengths[middle]) * 0.5
            : edgeLengths[middle];

        void AddEdgeLength(int a, int b)
        {
            long key = IndexedMeshTools.GetEdgeKey(a, b);
            if (!seen.Add(key))
                return;

            double dx = vertices[a * 3] - vertices[b * 3];
            double dy = vertices[a * 3 + 1] - vertices[b * 3 + 1];
            double dz = vertices[a * 3 + 2] - vertices[b * 3 + 2];
            edgeLengths.Add(Math.Sqrt(dx * dx + dy * dy + dz * dz));
        }
    }

    private static RhinoMesh BuildRemappedMesh(double[] vertices, List<int> faces)
    {
        var used = faces.Distinct().ToList();
        var remap = new Dictionary<int, int>(used.Count);
        var compactVertices = new double[used.Count * 3];

        for (int i = 0; i < used.Count; i++)
        {
            remap[used[i]] = i;
            compactVertices[i * 3] = vertices[used[i] * 3];
            compactVertices[i * 3 + 1] = vertices[used[i] * 3 + 1];
            compactVertices[i * 3 + 2] = vertices[used[i] * 3 + 2];
        }

        var compactFaces = new int[faces.Count];
        for (int i = 0; i < faces.Count; i++)
            compactFaces[i] = remap[faces[i]];

        return RhinoGeometryConversions.BuildMesh(compactVertices, used.Count, compactFaces, compactFaces.Length / 3);
    }

    private static int[] BuildFilteredFaces(int[] faces, int faceCount, bool[] keepFace, int removedFaceCount)
    {
        var filteredFaces = new int[(faceCount - removedFaceCount) * 3];
        int outputIndex = 0;
        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            if (!keepFace[faceIndex])
                continue;

            filteredFaces[outputIndex++] = faces[faceIndex * 3];
            filteredFaces[outputIndex++] = faces[faceIndex * 3 + 1];
            filteredFaces[outputIndex++] = faces[faceIndex * 3 + 2];
        }

        return filteredFaces;
    }

    private static T MeasureStage<T>(
        TerrainBuildResult build,
        string stage,
        Func<T> action,
        Func<T, string?> detailFactory,
        int diagnosticThresholdMs = StageTimingDiagnosticThresholdMs)
    {
        var timer = Stopwatch.StartNew();
        T result = action();
        timer.Stop();
        build.RecordTiming(stage, timer.Elapsed, detailFactory(result), diagnosticThresholdMs);
        return result;
    }
}
