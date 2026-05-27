using System.Diagnostics;
using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

public static class SurfaceStripGrader
{
    public readonly record struct TimingEntry(string Name, TimeSpan Elapsed);

    public sealed class TimingProfile
    {
        private readonly List<TimingEntry> _entries = new();

        public IReadOnlyList<TimingEntry> Entries => _entries;

        internal void AddPhase(string name, TimeSpan elapsed)
        {
            _entries.Add(new TimingEntry(name, elapsed));
        }

        public string FormatSummary()
        {
            return string.Join(
                ", ",
                _entries.Select(static entry => $"{entry.Name} {entry.Elapsed.TotalMilliseconds:0.#} ms"));
        }
    }

    public sealed class SurfaceDefinition
    {
        public double[] FootprintXy { get; }

        public int FootprintVertexCount { get; }

        public double[] BoundaryVertices { get; }

        public int BoundaryVertexCount { get; }

        public double PlaneXCoeff { get; }

        public double PlaneYCoeff { get; }

        public double PlaneConstant { get; }

        public double SlopeAngleDeg { get; }

        public double MaxDistance { get; }

        public SurfaceDefinition(
            double[] footprintXy,
            int footprintVertexCount,
            double[] boundaryVertices,
            int boundaryVertexCount,
            double planeXCoeff,
            double planeYCoeff,
            double planeConstant,
            double slopeAngleDeg = 33.0,
            double maxDistance = 0.0)
        {
            FootprintXy = footprintXy;
            FootprintVertexCount = footprintVertexCount;
            BoundaryVertices = boundaryVertices;
            BoundaryVertexCount = boundaryVertexCount;
            PlaneXCoeff = planeXCoeff;
            PlaneYCoeff = planeYCoeff;
            PlaneConstant = planeConstant;
            SlopeAngleDeg = Math.Max(0.1, Math.Min(89.9, slopeAngleDeg));
            MaxDistance = maxDistance;
        }

        public double EvaluateZ(double x, double y) => PlaneXCoeff * x + PlaneYCoeff * y + PlaneConstant;
    }

    public static GradingResult? Grade(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        SurfaceDefinition surface,
        out string? errorMessage)
    {
        return Grade(
            vertices,
            vertexCount,
            faces,
            faceCount,
            surface,
            Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
            out errorMessage);
    }

    public static GradingResult? Grade(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        SurfaceDefinition surface,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> barrierConstraints,
        out string? errorMessage)
    {
        return Grade(
            vertices,
            vertexCount,
            faces,
            faceCount,
            new[] { surface },
            barrierConstraints,
            out errorMessage);
    }

    public static GradingResult? Grade(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        IReadOnlyList<SurfaceDefinition> surfaces,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> barrierConstraints,
        out string? errorMessage)
    {
        return GradeCore(
            vertices,
            vertexCount,
            faces,
            faceCount,
            surfaces,
            barrierConstraints,
            out errorMessage,
            profile: null);
    }

    public static GradingResult? Grade(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        IReadOnlyList<SurfaceDefinition> surfaces,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> barrierConstraints,
        out string? errorMessage,
        out TimingProfile profile)
    {
        profile = new TimingProfile();
        return GradeCore(
            vertices,
            vertexCount,
            faces,
            faceCount,
            surfaces,
            barrierConstraints,
            out errorMessage,
            profile);
    }

    private static GradingResult? GradeCore(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        IReadOnlyList<SurfaceDefinition> surfaces,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> barrierConstraints,
        out string? errorMessage,
        TimingProfile? profile)
    {
        errorMessage = null;
        const double dedupTol = 1e-3;

        if (surfaces.Count == 0)
        {
            errorMessage = "At least one graded surface is required.";
            return null;
        }

        foreach (SurfaceDefinition surface in surfaces)
        {
            if (surface.FootprintVertexCount < 3)
            {
                errorMessage = "The graded surface footprint must contain at least 3 vertices.";
                return null;
            }

            if (surface.BoundaryVertexCount < 2)
            {
                errorMessage = "The graded surface boundary must contain at least 2 vertices.";
                return null;
            }
        }

        long setupStart = Stopwatch.GetTimestamp();
        var xyList = new List<double>(vertexCount * 2);
        var zList = new List<double>(vertexCount);
        var segList = new List<(int a, int b)>();
        var vertHash = new SpatialVertexHash(dedupTol);

        for (int i = 0; i < vertexCount; i++)
        {
            double x = vertices[i * 3];
            double y = vertices[i * 3 + 1];
            xyList.Add(x);
            xyList.Add(y);
            zList.Add(vertices[i * 3 + 2]);
            vertHash.Insert(i, x, y);
        }
        AddPhase(profile, "copy_input", setupStart);

        long prepStart = Stopwatch.GetTimestamp();
        var faceGrid = new TerrainFaceGrid(vertices, vertexCount, faces, faceCount);
        PreparedBarriers preparedBarriers = GradingBarriers.Build(barrierConstraints);
        bool hasBoundaryLoop = MeshBoundaryLoopBuilder.TryBuildBoundaryLoop(vertices, faces, faceCount, out var boundaryLoop, out int boundaryVertexCount);
        if (hasBoundaryLoop)
        {
            foreach (SurfaceDefinition surface in surfaces)
            {
                if (!BoundaryClipper.IsPolylineInsideBoundary(
                        surface.FootprintXy,
                        surface.FootprintVertexCount,
                        isClosed: true,
                        hasBoundaryLoop,
                        boundaryLoop,
                        boundaryVertexCount,
                        dedupTol))
                {
                    errorMessage = "The graded surface footprint must lie within the terrain boundary.";
                    return null;
                }
            }
        }

        AddBoundarySegments(faces, faceCount, segList);
        AddBarrierConstraints(barrierConstraints, xyList, zList, vertHash, segList, dedupTol);
        foreach (SurfaceDefinition surface in surfaces)
            AddPolygonConstraint(surface.FootprintXy, surface.FootprintVertexCount, xyList, zList, vertHash, faceGrid, segList, dedupTol);
        AddPhase(profile, "prepare_constraints", prepStart);

        int totalVerts = zList.Count;
        if (totalVerts < 3)
        {
            errorMessage = "Too few vertices for triangulation.";
            return null;
        }

        long triangulateStart = Stopwatch.GetTimestamp();
        TriangulationOutcome triangulation = TriangulationHelper.Triangulate(
            xyList,
            totalVerts,
            segList,
            0,
            0,
            convex: false);
        AddPhase(profile, "triangulate", triangulateStart);

        if (triangulation.Mesh == null)
        {
            errorMessage = triangulation.WarningMessage ?? "Triangulation failed.";
            return null;
        }

        if (!string.IsNullOrWhiteSpace(triangulation.WarningMessage))
            errorMessage = triangulation.WarningMessage;

        long extractStart = Stopwatch.GetTimestamp();
        var extracted = TriangleNetExtractor.Extract(triangulation.Mesh);
        int outVertCount = extracted.VertexCount;
        int outFaceCount = extracted.FaceCount;
        AddPhase(profile, "extract_mesh", extractStart);

        long interpolateStart = Stopwatch.GetTimestamp();
        var outXy = new double[outVertCount * 2];
        var origZ = new double[outVertCount];
        var newZ = new double[outVertCount];

        for (int i = 0; i < outVertCount; i++)
        {
            double x = extracted.Xy[i * 2];
            double y = extracted.Xy[i * 2 + 1];
            int sourceId = extracted.SourceIds[i];
            outXy[i * 2] = x;
            outXy[i * 2 + 1] = y;

            if (sourceId >= 0 && sourceId < totalVerts)
            {
                origZ[i] = zList[sourceId];
                newZ[i] = zList[sourceId];
            }
            else
            {
                double iz = faceGrid.InterpolateZ(x, y);
                origZ[i] = iz;
                newZ[i] = iz;
            }
        }
        AddPhase(profile, "interpolate_z", interpolateStart);

        long applySurfacesStart = Stopwatch.GetTimestamp();
        foreach (SurfaceDefinition surface in surfaces)
        {
            double[] passOrigZ = (double[])newZ.Clone();
            ApplySurfaceHeights(surface, outXy, passOrigZ, newZ, outVertCount, preparedBarriers);
        }
        AddPhase(profile, "apply_surfaces", applySurfacesStart);

        long buildVertsStart = Stopwatch.GetTimestamp();
        var finalVerts = new double[outVertCount * 3];
        for (int i = 0; i < outVertCount; i++)
        {
            finalVerts[i * 3] = outXy[i * 2];
            finalVerts[i * 3 + 1] = outXy[i * 2 + 1];
            finalVerts[i * 3 + 2] = newZ[i];
        }
        AddPhase(profile, "build_vertices", buildVertsStart);

        var finalFaces = extracted.Faces;
        long cullStart = Stopwatch.GetTimestamp();
        var cullResult = TriangleBoundaryCuller.Cull(
            finalVerts,
            outVertCount,
            finalFaces,
            outFaceCount,
            xyList.ToArray(),
            IndexedMeshTools.FlattenSegments(segList),
            0);

        if (cullResult.Changed)
        {
            outXy = IndexedMeshTools.CompactDoubleData(outXy, 2, cullResult.NewToOld, cullResult.VertexCount);
            origZ = IndexedMeshTools.CompactDoubleData(origZ, 1, cullResult.NewToOld, cullResult.VertexCount);
            newZ = IndexedMeshTools.CompactDoubleData(newZ, 1, cullResult.NewToOld, cullResult.VertexCount);
            finalVerts = IndexedMeshTools.CompactDoubleData(finalVerts, 3, cullResult.NewToOld, cullResult.VertexCount);
            finalFaces = cullResult.Faces;
            outVertCount = cullResult.VertexCount;
            outFaceCount = cullResult.FaceCount;
        }
        AddPhase(profile, "cull_boundary", cullStart);

        long volumeStart = Stopwatch.GetTimestamp();
        GradingVolumeMetrics volume = GradingResultBuilder.ComputeVolume(outXy, origZ, newZ, finalFaces, outFaceCount);
        AddPhase(profile, "volume", volumeStart);

        long daylightStart = Stopwatch.GetTimestamp();
        double[] daylightVertices = GradingResultBuilder.BuildDaylightVertices(outXy, origZ, newZ, finalFaces, outFaceCount);
        AddPhase(profile, "daylight", daylightStart);

        return new GradingResult(
            finalVerts,
            outVertCount,
            finalFaces,
            outFaceCount,
            volume.CutVolume,
            volume.FillVolume,
            daylightVertices,
            daylightVertices.Length / 3);
    }

    private static void AddPhase(TimingProfile? profile, string name, long startTimestamp)
    {
        profile?.AddPhase(name, Stopwatch.GetElapsedTime(startTimestamp));
    }

    private static void ApplySurfaceHeights(
        SurfaceDefinition surface,
        double[] outXy,
        double[] origZ,
        double[] newZ,
        int outVertCount,
        PreparedBarriers barriers)
    {
        double slopeRatio = Math.Tan(surface.SlopeAngleDeg * Math.PI / 180.0);
        SurfaceInfluenceBounds influenceBounds = ComputeSurfaceInfluenceBounds(surface, origZ, outVertCount, slopeRatio);
        var barrierScratch = barriers.Segments.Length > 0 ? new SpatialHashGrid2D.QueryScratch(Math.Max(barriers.Segments.Length, 1)) : null;
        var barrierCandidates = barriers.Segments.Length > 0 ? new List<int>(8) : null;

        for (int i = 0; i < outVertCount; i++)
        {
            double px = outXy[i * 2];
            double py = outXy[i * 2 + 1];

            if (influenceBounds.HasFiniteInfluence &&
                (px < influenceBounds.MinX || px > influenceBounds.MaxX || py < influenceBounds.MinY || py > influenceBounds.MaxY))
            {
                continue;
            }

            if (GradingGeometry2D.PointInPolygon(px, py, surface.FootprintXy, surface.FootprintVertexCount))
            {
                newZ[i] = surface.EvaluateZ(px, py);
                continue;
            }

            if (!TryFindNearestVisibleBoundaryLocation(
                    px,
                    py,
                    surface.BoundaryVertices,
                    surface.BoundaryVertexCount,
                    barriers,
                    barrierScratch,
                    barrierCandidates,
                    out double boundaryDistance,
                    out double boundaryZ))
            {
                continue;
            }

            double dz = origZ[i] - boundaryZ;
            double absDz = Math.Abs(dz);
            double neededDist = slopeRatio > 1e-12 ? absDz / slopeRatio : double.MaxValue;
            if (surface.MaxDistance > 0)
                neededDist = Math.Min(neededDist, surface.MaxDistance);

            if (boundaryDistance < neededDist)
            {
                double rise = boundaryDistance * slopeRatio;
                if (rise < absDz)
                    newZ[i] = boundaryZ + Math.Sign(dz) * rise;
            }
        }
    }

    private readonly record struct SurfaceInfluenceBounds(
        double MinX,
        double MaxX,
        double MinY,
        double MaxY,
        bool HasFiniteInfluence);

    private static SurfaceInfluenceBounds ComputeSurfaceInfluenceBounds(
        SurfaceDefinition surface,
        double[] origZ,
        int vertexCount,
        double slopeRatio)
    {
        if (vertexCount <= 0)
            return new SurfaceInfluenceBounds(0, 0, 0, 0, HasFiniteInfluence: false);

        double minX = double.MaxValue;
        double maxX = double.MinValue;
        double minY = double.MaxValue;
        double maxY = double.MinValue;
        double minBoundaryZ = double.MaxValue;
        double maxBoundaryZ = double.MinValue;

        for (int i = 0; i < surface.BoundaryVertexCount; i++)
        {
            double x = surface.BoundaryVertices[i * 3];
            double y = surface.BoundaryVertices[i * 3 + 1];
            double z = surface.BoundaryVertices[i * 3 + 2];
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
            if (z < minBoundaryZ) minBoundaryZ = z;
            if (z > maxBoundaryZ) maxBoundaryZ = z;
        }

        if (minX == double.MaxValue ||
            maxX == double.MinValue ||
            minY == double.MaxValue ||
            maxY == double.MinValue ||
            minBoundaryZ == double.MaxValue ||
            maxBoundaryZ == double.MinValue)
        {
            return new SurfaceInfluenceBounds(0, 0, 0, 0, HasFiniteInfluence: false);
        }

        double minOrigZ = double.MaxValue;
        double maxOrigZ = double.MinValue;
        for (int i = 0; i < vertexCount; i++)
        {
            double z = origZ[i];
            if (z < minOrigZ) minOrigZ = z;
            if (z > maxOrigZ) maxOrigZ = z;
        }

        double maxPossibleDz = Math.Max(
            Math.Max(Math.Abs(minOrigZ - minBoundaryZ), Math.Abs(minOrigZ - maxBoundaryZ)),
            Math.Max(Math.Abs(maxOrigZ - minBoundaryZ), Math.Abs(maxOrigZ - maxBoundaryZ)));

        double padding = slopeRatio > 1e-12
            ? maxPossibleDz / slopeRatio
            : double.MaxValue;
        if (surface.MaxDistance > 0)
            padding = Math.Min(padding, surface.MaxDistance);

        if (!double.IsFinite(padding))
            return new SurfaceInfluenceBounds(0, 0, 0, 0, HasFiniteInfluence: false);

        padding = Math.Max(padding, 0.0);
        return new SurfaceInfluenceBounds(
            minX - padding,
            maxX + padding,
            minY - padding,
            maxY + padding,
            HasFiniteInfluence: true);
    }

    private static bool TryFindNearestVisibleBoundaryLocation(
        double px,
        double py,
        double[] boundaryVertices,
        int boundaryVertexCount,
        PreparedBarriers barriers,
        SpatialHashGrid2D.QueryScratch? barrierScratch,
        List<int>? barrierCandidates,
        out double boundaryDistance,
        out double boundaryZ)
    {
        boundaryDistance = double.MaxValue;
        boundaryZ = 0.0;
        double bestDistanceSquared = double.MaxValue;

        for (int i = 0; i < boundaryVertexCount; i++)
        {
            int next = (i + 1) % boundaryVertexCount;
            double ax = boundaryVertices[i * 3];
            double ay = boundaryVertices[i * 3 + 1];
            double az = boundaryVertices[i * 3 + 2];
            double bx = boundaryVertices[next * 3];
            double by = boundaryVertices[next * 3 + 1];
            double bz = boundaryVertices[next * 3 + 2];

            double dx = bx - ax;
            double dy = by - ay;
            double lenSq = dx * dx + dy * dy;
            double t = 0.0;
            double cx = ax;
            double cy = ay;
            if (lenSq > 1e-20)
            {
                t = Math.Clamp(((px - ax) * dx + (py - ay) * dy) / lenSq, 0.0, 1.0);
                cx = ax + t * dx;
                cy = ay + t * dy;
            }

            double distSquared = ((px - cx) * (px - cx)) + ((py - cy) * (py - cy));
            if (distSquared >= bestDistanceSquared)
                continue;

            if (barriers.Segments.Length > 0 &&
                barrierScratch != null &&
                barrierCandidates != null &&
                GradingBarriers.IsCrossedByBarrier(
                    barriers,
                    px,
                    py,
                    cx,
                    cy,
                    barrierScratch,
                    barrierCandidates))
            {
                continue;
            }

            bestDistanceSquared = distSquared;
            boundaryZ = az + ((bz - az) * t);
        }

        if (bestDistanceSquared == double.MaxValue)
            return false;

        boundaryDistance = Math.Sqrt(bestDistanceSquared);
        return true;
    }

    private static void AddBoundarySegments(int[] faces, int faceCount, List<(int a, int b)> segList)
    {
        var edgeFaceCount = new Dictionary<long, int>(8, IndexedMeshTools.EdgeKeyComparer.Instance);
        for (int f = 0; f < faceCount; f++)
        {
            int a = faces[f * 3];
            int b = faces[f * 3 + 1];
            int c = faces[f * 3 + 2];
            IncrEdge(edgeFaceCount, a, b);
            IncrEdge(edgeFaceCount, b, c);
            IncrEdge(edgeFaceCount, c, a);
        }

        foreach (var pair in edgeFaceCount)
        {
            if (pair.Value != 1)
                continue;

            int a = (int)(pair.Key >> 32);
            int b = (int)(pair.Key & 0xFFFFFFFFL);
            segList.Add((a, b));
        }
    }

    private static void AddPolygonConstraint(
        double[] polygonXy,
        int vertexCount,
        List<double> xyList,
        List<double> zList,
        SpatialVertexHash vertHash,
        TerrainFaceGrid faceGrid,
        List<(int a, int b)> segList,
        double dedupTol)
    {
        var polygonIndices = new int[vertexCount];
        for (int i = 0; i < vertexCount; i++)
        {
            double px = polygonXy[i * 2];
            double py = polygonXy[i * 2 + 1];
            int near = vertHash.FindNearest(xyList, px, py, dedupTol);
            if (near >= 0)
            {
                polygonIndices[i] = near;
            }
            else
            {
                polygonIndices[i] = zList.Count;
                xyList.Add(px);
                xyList.Add(py);
                zList.Add(faceGrid.InterpolateZ(px, py));
                vertHash.Insert(polygonIndices[i], px, py);
            }
        }

        for (int i = 0; i < vertexCount; i++)
        {
            int a = polygonIndices[i];
            int b = polygonIndices[(i + 1) % vertexCount];
            if (a != b)
                segList.Add((a, b));
        }
    }

    private static void AddBarrierConstraints(
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> barrierConstraints,
        List<double> xyList,
        List<double> zList,
        SpatialVertexHash vertHash,
        List<(int a, int b)> segList,
        double dedupTol)
    {
        foreach (var constraint in barrierConstraints)
        {
            if (!constraint.PreserveInputElevation)
                continue;

            int pointCount = NormalizeConstraintPointCount(constraint, dedupTol);
            if (pointCount < 2)
                continue;

            var pointIndices = new int[pointCount];
            for (int i = 0; i < pointCount; i++)
            {
                double px = constraint.Points[i * 3];
                double py = constraint.Points[i * 3 + 1];
                double pz = constraint.Points[i * 3 + 2];
                int near = vertHash.FindNearest(xyList, px, py, dedupTol);
                if (near >= 0)
                {
                    pointIndices[i] = near;
                }
                else
                {
                    pointIndices[i] = zList.Count;
                    xyList.Add(px);
                    xyList.Add(py);
                    zList.Add(pz);
                    vertHash.Insert(pointIndices[i], px, py);
                }
            }

            for (int i = 0; i < pointCount - 1; i++)
            {
                int a = pointIndices[i];
                int b = pointIndices[i + 1];
                if (a != b)
                    segList.Add((a, b));
            }

            if (!constraint.IsClosed)
                continue;

            int last = pointIndices[pointCount - 1];
            int first = pointIndices[0];
            if (last != first)
                segList.Add((last, first));
        }
    }

    private static int NormalizeConstraintPointCount(SurfaceRemesher.ConstraintPolyline constraint, double tolerance)
    {
        int count = constraint.PointCount;
        if (!constraint.IsClosed || count < 2)
            return count;

        double lastX = constraint.Points[(count - 1) * 3];
        double lastY = constraint.Points[(count - 1) * 3 + 1];
        double firstX = constraint.Points[0];
        double firstY = constraint.Points[1];
        double dx = lastX - firstX;
        double dy = lastY - firstY;
        if (((dx * dx) + (dy * dy)) <= tolerance * tolerance)
            return count - 1;

        return count;
    }

    private static void IncrEdge(Dictionary<long, int> dict, int a, int b)
    {
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        dict[key] = dict.GetValueOrDefault(key, 0) + 1;
    }
}
