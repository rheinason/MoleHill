using TriangleNet;
using TriangleNet.Geometry;
using TriangleNet.Meshing;
using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

/// <summary>
/// Grades a terrain mesh by assigning a planar finished surface inside
/// boundary curves, with controlled slope transitions.
/// Re-triangulates the entire mesh with pad boundaries as constrained edges.
/// </summary>
public static class PadGrader
{
    private readonly record struct PadInfluenceBounds(
        PadBoundary Pad,
        double MinX,
        double MaxX,
        double MinY,
        double MaxY,
        double InfluenceMinX,
        double InfluenceMaxX,
        double InfluenceMinY,
        double InfluenceMaxY);

    public sealed class ConstraintSet
    {
        public required SurfaceRemesher.ConstraintPolyline[] Constraints { get; init; }

        public required double SuggestedEdgeLength { get; init; }

        public required string[] Diagnostics { get; init; }
    }

    public sealed class PadBoundary
    {
        public double[] XyVertices { get; }
        public int VertexCount { get; }
        public double[] BoundaryVertices { get; }
        public double PlaneXCoeff { get; }
        public double PlaneYCoeff { get; }
        public double PlaneConstant { get; }
        public double SlopeAngleDeg { get; }
        public double MaxDistance { get; }

        public PadBoundary(double[] xyVertices, int vertexCount, double targetZ,
            double slopeAngleDeg = 33.0, double maxDistance = 0.0)
        {
            XyVertices = (double[])xyVertices.Clone();
            VertexCount = vertexCount;
            BoundaryVertices = BuildBoundaryVertices(XyVertices, vertexCount, targetZ);
            PlaneXCoeff = 0.0;
            PlaneYCoeff = 0.0;
            PlaneConstant = targetZ;
            SlopeAngleDeg = Math.Max(0.1, Math.Min(89.9, slopeAngleDeg));
            MaxDistance = maxDistance;
        }

        public static PadBoundary CreatePlanar(
            double[] boundaryVertices,
            int vertexCount,
            double planeXCoeff,
            double planeYCoeff,
            double planeConstant,
            double slopeAngleDeg = 33.0,
            double maxDistance = 0.0)
        {
            return new PadBoundary(
                ExtractXyVertices(boundaryVertices, vertexCount),
                boundaryVertices,
                vertexCount,
                planeXCoeff,
                planeYCoeff,
                planeConstant,
                slopeAngleDeg,
                maxDistance);
        }

        public double EvaluateZ(double x, double y) => PlaneXCoeff * x + PlaneYCoeff * y + PlaneConstant;

        private PadBoundary(
            double[] xyVertices,
            double[] boundaryVertices,
            int vertexCount,
            double planeXCoeff,
            double planeYCoeff,
            double planeConstant,
            double slopeAngleDeg,
            double maxDistance)
        {
            XyVertices = (double[])xyVertices.Clone();
            BoundaryVertices = (double[])boundaryVertices.Clone();
            VertexCount = vertexCount;
            PlaneXCoeff = planeXCoeff;
            PlaneYCoeff = planeYCoeff;
            PlaneConstant = planeConstant;
            SlopeAngleDeg = Math.Max(0.1, Math.Min(89.9, slopeAngleDeg));
            MaxDistance = maxDistance;
        }

        private static double[] BuildBoundaryVertices(double[] xyVertices, int vertexCount, double targetZ)
        {
            var boundaryVertices = new double[vertexCount * 3];
            for (int i = 0; i < vertexCount; i++)
            {
                boundaryVertices[i * 3] = xyVertices[i * 2];
                boundaryVertices[i * 3 + 1] = xyVertices[i * 2 + 1];
                boundaryVertices[i * 3 + 2] = targetZ;
            }

            return boundaryVertices;
        }

        private static double[] ExtractXyVertices(double[] boundaryVertices, int vertexCount)
        {
            var xyVertices = new double[vertexCount * 2];
            for (int i = 0; i < vertexCount; i++)
            {
                xyVertices[i * 2] = boundaryVertices[i * 3];
                xyVertices[i * 2 + 1] = boundaryVertices[i * 3 + 1];
            }

            return xyVertices;
        }
    }

    public sealed class LockCurve
    {
        public double[] XyVertices { get; }
        public int VertexCount { get; }

        public LockCurve(double[] xyVertices, int vertexCount)
        {
            XyVertices = xyVertices;
            VertexCount = vertexCount;
        }
    }

    private sealed class PadTopologyResult
    {
        public required double[] Vertices { get; init; }
        public required int VertexCount { get; init; }
        public required int[] Faces { get; init; }
        public required int FaceCount { get; init; }
        public required OutputPolyline[] PadPolylines { get; init; }
    }

    /// <summary>
    /// Apply pad grading to a terrain mesh.
    /// Each pad carries its own slope angle and max distance.
    /// Later pads in the array override earlier ones in overlapping zones.
    /// </summary>
    public static GradingResult? Grade(
        double[] vertices, int vertexCount,
        int[] faces, int faceCount,
        PadBoundary[] pads,
        LockCurve[]? lockCurves,
        double maxArea,
        double minAngle,
        out string? errorMessage)
    {
        errorMessage = null;

        if (!ValidatePads(pads, out errorMessage))
            return null;

        PadTopologyResult? topology = TryTriangulatePadTopology(
            vertices,
            vertexCount,
            faces,
            faceCount,
            pads,
            lockCurves,
            maxArea,
            minAngle,
            out string? topologyMessage);

        if (topology == null)
        {
            errorMessage = topologyMessage ?? "Triangulation failed.";
            return null;
        }

        if (!string.IsNullOrWhiteSpace(topologyMessage))
            errorMessage = topologyMessage;

        PreparedBarriers gradingBarriers = lockCurves != null && lockCurves.Length > 0
            ? GradingBarriers.BuildFromLockCurves(lockCurves)
            : PreparedBarriers.Empty;
        double[] gradedVertices = ApplyGradingZWithBarriers(topology.Vertices, topology.VertexCount, pads, gradingBarriers);
        return BuildResult(topology.Vertices, topology.VertexCount, topology.Faces, topology.FaceCount, gradedVertices, topology.PadPolylines);
    }

    public static bool TryTriangulateTopology(
        double[] vertices, int vertexCount,
        int[] faces, int faceCount,
        PadBoundary[] pads,
        LockCurve[]? lockCurves,
        double maxArea,
        double minAngle,
        out double[] topologyVertices,
        out int topologyVertexCount,
        out int[] topologyFaces,
        out int topologyFaceCount,
        out string? warningOrError)
    {
        topologyVertices = Array.Empty<double>();
        topologyVertexCount = 0;
        topologyFaces = Array.Empty<int>();
        topologyFaceCount = 0;
        warningOrError = null;

        if (!ValidatePads(pads, out warningOrError))
            return false;

        PadTopologyResult? topology = TryTriangulatePadTopology(
            vertices,
            vertexCount,
            faces,
            faceCount,
            pads,
            lockCurves,
            maxArea,
            minAngle,
            out warningOrError);

        if (topology == null)
            return false;

        topologyVertices = topology.Vertices;
        topologyVertexCount = topology.VertexCount;
        topologyFaces = topology.Faces;
        topologyFaceCount = topology.FaceCount;
        return true;
    }

    public static double[] ApplyGradingZ(
        double[] topologyVertices,
        int vertexCount,
        PadBoundary[] pads,
        LockCurve[]? lockCurves = null)
    {
        if (pads.Length == 0)
            return (double[])topologyVertices.Clone();

        PreparedBarriers barriers = lockCurves != null && lockCurves.Length > 0
            ? GradingBarriers.BuildFromLockCurves(lockCurves)
            : PreparedBarriers.Empty;

        var gradedVertices = (double[])topologyVertices.Clone();
        ApplyGradingToVertices(gradedVertices, topologyVertices, vertexCount, pads, barriers);
        return gradedVertices;
    }

    internal static double[] ApplyGradingZWithBarriers(
        double[] topologyVertices,
        int vertexCount,
        PadBoundary[] pads,
        PreparedBarriers barriers)
    {
        if (pads.Length == 0)
            return (double[])topologyVertices.Clone();

        var gradedVertices = (double[])topologyVertices.Clone();
        ApplyGradingToVertices(gradedVertices, topologyVertices, vertexCount, pads, barriers);
        return gradedVertices;
    }

    public static ConstraintSet CreateConstraints(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PadBoundary[] pads,
        LockCurve[]? lockCurves)
    {
        const double dedupTol = 1e-3;
        if (!ValidatePads(pads, out _))
        {
            return new ConstraintSet
            {
                Constraints = Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
                SuggestedEdgeLength = 0.0,
                Diagnostics = Array.Empty<string>()
            };
        }

        bool hasBoundaryLoop = TryBuildBoundaryLoop(vertices, faces, faceCount, out var boundaryLoop, out int boundaryVertexCount);
        var constraints = new List<SurfaceRemesher.ConstraintPolyline>(pads.Length * 2 + (lockCurves?.Length ?? 0));
        var diagnostics = new List<string>();
        double suggestedEdgeLength = double.MaxValue;

        var faceGridForConstraints = new FaceGrid(vertices, vertexCount, faces, faceCount);
        foreach (var pad in pads)
        {
            double shoulderDistance = ComputePadTransitionDistance(vertices, vertexCount, pad);
            double segmentLength = ComputePadConstraintSegmentLength(shoulderDistance);
            var padLoop = BuildClosedConstraintLoop(pad.XyVertices, pad.VertexCount, segmentLength, dedupTol);
            constraints.Add(new SurfaceRemesher.ConstraintPolyline(
                CreateConstraintPoints(padLoop.XyVertices, padLoop.VertexCount),
                padLoop.VertexCount,
                IsClosed: true,
                PreserveInputElevation: false));
            suggestedEdgeLength = UpdateSuggestedEdgeLength(suggestedEdgeLength, padLoop.XyVertices, padLoop.VertexCount, stride: 2, isClosed: true);

            double[] shoulderDistances = ComputePadBoundaryDistances(padLoop.XyVertices, padLoop.VertexCount, faceGridForConstraints, pad);
            if (TryBuildShoulderLoop(
                padLoop.XyVertices,
                padLoop.VertexCount,
                shoulderDistances,
                hasBoundaryLoop ? boundaryLoop : null,
                hasBoundaryLoop ? boundaryVertexCount : 0,
                dedupTol,
                out var shoulderXy,
                out string? skipReason))
            {
                int shoulderVertexCount = shoulderXy.Length / 2;
                constraints.Add(new SurfaceRemesher.ConstraintPolyline(
                    CreateConstraintPoints(shoulderXy, shoulderVertexCount),
                    shoulderVertexCount,
                    IsClosed: true,
                    PreserveInputElevation: false));
                suggestedEdgeLength = Math.Min(suggestedEdgeLength, segmentLength);
                suggestedEdgeLength = UpdateSuggestedEdgeLength(suggestedEdgeLength, shoulderXy, shoulderVertexCount, stride: 2, isClosed: true);
            }
            else if (!string.IsNullOrWhiteSpace(skipReason))
            {
                diagnostics.Add(skipReason!);
            }
        }

        if (lockCurves != null)
        {
            foreach (var lockCurve in lockCurves)
            {
                if (lockCurve.VertexCount < 2 || lockCurve.XyVertices.Length < lockCurve.VertexCount * 2)
                    continue;

                var points = new double[lockCurve.VertexCount * 3];
                for (int i = 0; i < lockCurve.VertexCount; i++)
                {
                    points[i * 3] = lockCurve.XyVertices[i * 2];
                    points[i * 3 + 1] = lockCurve.XyVertices[i * 2 + 1];
                }

                constraints.Add(new SurfaceRemesher.ConstraintPolyline(
                    points,
                    lockCurve.VertexCount,
                    IsClosed: false,
                    PreserveInputElevation: false));
                suggestedEdgeLength = UpdateSuggestedEdgeLength(suggestedEdgeLength, points, lockCurve.VertexCount, stride: 3, isClosed: false);
            }
        }

        return new ConstraintSet
        {
            Constraints = constraints.ToArray(),
            SuggestedEdgeLength = suggestedEdgeLength < double.MaxValue ? suggestedEdgeLength : 0.0,
            Diagnostics = diagnostics.ToArray()
        };
    }

    private static bool ValidatePads(PadBoundary[] pads, out string? errorMessage)
    {
        errorMessage = null;

        if (pads.Length == 0)
        {
            errorMessage = "No pad boundaries provided.";
            return false;
        }

        foreach (var pad in pads)
        {
            if (pad.VertexCount < 3 ||
                pad.XyVertices.Length < pad.VertexCount * 2 ||
                pad.BoundaryVertices.Length < pad.VertexCount * 3)
            {
                errorMessage = "Each pad must have at least 3 valid vertices.";
                return false;
            }

            if (!double.IsFinite(pad.PlaneXCoeff) ||
                !double.IsFinite(pad.PlaneYCoeff) ||
                !double.IsFinite(pad.PlaneConstant))
            {
                errorMessage = "Each pad must define a valid finished plane.";
                return false;
            }
        }

        return true;
    }

    private static PadTopologyResult? TryTriangulatePadTopology(
        double[] vertices, int vertexCount,
        int[] faces, int faceCount,
        PadBoundary[] pads,
        LockCurve[]? lockCurves,
        double maxArea,
        double minAngle,
        out string? warningOrError)
    {
        warningOrError = null;
        const double dedupTol = 1e-3;

        var xyList = new List<double>(vertexCount * 2);
        var zList = new List<double>(vertexCount);
        var segList = new List<(int a, int b)>();

        var vertHash = new SpatialHash(dedupTol);

        for (int i = 0; i < vertexCount; i++)
        {
            double x = vertices[i * 3];
            double y = vertices[i * 3 + 1];
            xyList.Add(x);
            xyList.Add(y);
            zList.Add(vertices[i * 3 + 2]);
            vertHash.Insert(i, x, y);
        }

        var faceGrid = new FaceGrid(vertices, vertexCount, faces, faceCount);
        bool hasBoundaryLoop = TryBuildBoundaryLoop(vertices, faces, faceCount, out var boundaryLoop, out int boundaryVertexCount);

        int AddVertex(double x, double y)
        {
            int near = vertHash.FindNearest(xyList, x, y, dedupTol);
            if (near >= 0)
                return near;

            int idx = zList.Count;
            xyList.Add(x);
            xyList.Add(y);
            zList.Add(faceGrid.InterpolateZ(x, y));
            vertHash.Insert(idx, x, y);
            return idx;
        }

        // Add mesh boundary edges as constraints (keeps triangulation within original mesh).
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

        foreach (var kvp in edgeFaceCount)
        {
            if (kvp.Value == 1)
            {
                int a = (int)(kvp.Key >> 32);
                int b = (int)(kvp.Key & 0xFFFFFFFFL);
                segList.Add((a, b));
            }
        }

        // Build barriers from lock curves so shoulder rings are clipped at hard constraints.
        PreparedBarriers padBarriers = lockCurves != null && lockCurves.Length > 0
            ? GradingBarriers.BuildFromLockCurves(lockCurves)
            : PreparedBarriers.Empty;
        var padBarrierScratch = new SpatialHashGrid2D.QueryScratch(Math.Max(padBarriers.Segments.Length, 1));
        var padBarrierCandidates = new List<int>(8);

        var padPolylines = new List<OutputPolyline>(pads.Length);

        foreach (var pad in pads)
        {
            double[] shoulderDistances = ComputePadBoundaryDistances(pad.XyVertices, pad.VertexCount, faceGrid, pad);
            double shoulderDistance = 0; foreach (double d in shoulderDistances) if (d > shoulderDistance) shoulderDistance = d;
            double segmentLength = ComputePadConstraintSegmentLength(shoulderDistance);
            var padLoop = BuildClosedConstraintLoop(pad.XyVertices, pad.VertexCount, segmentLength, dedupTol);
            // Re-sample distances at padLoop resolution (which may have more vertices than the original pad)
            shoulderDistances = ComputePadBoundaryDistances(padLoop.XyVertices, padLoop.VertexCount, faceGrid, pad);
            AddClosedLoopSegments(padLoop.XyVertices, padLoop.VertexCount, AddVertex, segList);

            // Build pad boundary output polyline with graded Z (pad plane Z at each vertex)
            int loopN = padLoop.VertexCount;
            var padPolyXyz = new double[loopN * 3];
            for (int i = 0; i < loopN; i++)
            {
                double bx = padLoop.XyVertices[i * 2];
                double by = padLoop.XyVertices[i * 2 + 1];
                padPolyXyz[i * 3]     = bx;
                padPolyXyz[i * 3 + 1] = by;
                padPolyXyz[i * 3 + 2] = pad.EvaluateZ(bx, by);
            }
            padPolylines.Add(new OutputPolyline(padPolyXyz, loopN, isClosed: true));

            double[]? shoulderXy = AddPadShoulderConstraint(
                padLoop.XyVertices,
                padLoop.VertexCount,
                shoulderDistances,
                xyList,
                zList,
                vertHash,
                faceGrid,
                segList,
                dedupTol,
                hasBoundaryLoop ? boundaryLoop : null,
                hasBoundaryLoop ? boundaryVertexCount : 0,
                padBarriers,
                padBarrierScratch,
                padBarrierCandidates);
        }

        if (lockCurves != null)
        {
            foreach (var lc in lockCurves)
            {
                if (lc.VertexCount < 2 || lc.XyVertices.Length < lc.VertexCount * 2)
                    continue;

                var lcIndices = new int[lc.VertexCount];
                for (int i = 0; i < lc.VertexCount; i++)
                {
                    double lx = lc.XyVertices[i * 2];
                    double ly = lc.XyVertices[i * 2 + 1];

                    int near = vertHash.FindNearest(xyList, lx, ly, dedupTol);
                    if (near >= 0)
                    {
                        lcIndices[i] = near;
                    }
                    else
                    {
                        lcIndices[i] = zList.Count;
                        xyList.Add(lx);
                        xyList.Add(ly);
                        zList.Add(faceGrid.InterpolateZ(lx, ly));
                        vertHash.Insert(lcIndices[i], lx, ly);
                    }
                }

                for (int i = 0; i < lc.VertexCount - 1; i++)
                {
                    if (lcIndices[i] != lcIndices[i + 1])
                        segList.Add((lcIndices[i], lcIndices[i + 1]));
                }
            }
        }

        int totalVerts = zList.Count;
        if (totalVerts < 3)
        {
            warningOrError = "Too few vertices for triangulation.";
            return null;
        }

        TriangulationOutcome triangulation = TriangulationHelper.Triangulate(
            xyList,
            totalVerts,
            segList,
            maxArea,
            minAngle,
            convex: false);

        if (triangulation.Mesh == null)
        {
            warningOrError = triangulation.WarningMessage ?? "Triangulation failed.";
            return null;
        }

        if (!string.IsNullOrWhiteSpace(triangulation.WarningMessage))
            warningOrError = triangulation.WarningMessage;

        var extracted = TriangleNetExtractor.Extract(triangulation.Mesh);
        int outVertCount = extracted.VertexCount;
        int outFaceCount = extracted.FaceCount;

        var topologyVertices = new double[outVertCount * 3];
        for (int i = 0; i < outVertCount; i++)
        {
            double x = extracted.Xy[i * 2];
            double y = extracted.Xy[i * 2 + 1];
            int sourceId = extracted.SourceIds[i];

            double originalZ = sourceId >= 0 && sourceId < totalVerts
                ? zList[sourceId]
                : faceGrid.InterpolateZ(x, y);

            topologyVertices[i * 3] = x;
            topologyVertices[i * 3 + 1] = y;
            topologyVertices[i * 3 + 2] = originalZ;
        }

        var topologyFaces = extracted.Faces;
        var cullResult = TriangleBoundaryCuller.Cull(
            topologyVertices,
            outVertCount,
            topologyFaces,
            outFaceCount,
            xyList.ToArray(),
            IndexedMeshTools.FlattenSegments(segList),
            0);

        if (cullResult.Changed)
        {
            topologyVertices = IndexedMeshTools.CompactDoubleData(topologyVertices, 3, cullResult.NewToOld, cullResult.VertexCount);
            topologyFaces = cullResult.Faces;
            outVertCount = cullResult.VertexCount;
            outFaceCount = cullResult.FaceCount;
        }

        return new PadTopologyResult
        {
            Vertices = topologyVertices,
            VertexCount = outVertCount,
            Faces = topologyFaces,
            FaceCount = outFaceCount,
            PadPolylines = padPolylines.ToArray()
        };
    }

    private static void ApplyGradingToVertices(
        double[] gradedVertices,
        double[] originalVertices,
        int vertexCount,
        PadBoundary[] pads,
        PreparedBarriers barriers)
    {
        if (pads.Length == 0)
            return;

        double globalMinX = double.MaxValue;
        double globalMaxX = double.MinValue;
        double globalMinY = double.MaxValue;
        double globalMaxY = double.MinValue;
        var padBounds = new PadInfluenceBounds[pads.Length];
        var interiorBounds = new Bounds2D[pads.Length];
        var influenceBounds = new Bounds2D[pads.Length];

        for (int p = 0; p < pads.Length; p++)
        {
            var pad = pads[p];
            double minX = double.MaxValue;
            double maxX = double.MinValue;
            double minY = double.MaxValue;
            double maxY = double.MinValue;

            for (int i = 0; i < pad.VertexCount; i++)
            {
                double vx = pad.XyVertices[i * 2];
                double vy = pad.XyVertices[i * 2 + 1];
                if (vx < minX) minX = vx;
                if (vx > maxX) maxX = vx;
                if (vy < minY) minY = vy;
                if (vy > maxY) maxY = vy;
            }

            double transitionDistance = ComputePadTransitionDistance(originalVertices, vertexCount, pad);
            double influenceMinX = minX - transitionDistance;
            double influenceMaxX = maxX + transitionDistance;
            double influenceMinY = minY - transitionDistance;
            double influenceMaxY = maxY + transitionDistance;
            padBounds[p] = new PadInfluenceBounds(
                pad,
                minX,
                maxX,
                minY,
                maxY,
                influenceMinX,
                influenceMaxX,
                influenceMinY,
                influenceMaxY);
            interiorBounds[p] = new Bounds2D(minX, maxX, minY, maxY);
            influenceBounds[p] = new Bounds2D(influenceMinX, influenceMaxX, influenceMinY, influenceMaxY);

            if (influenceMinX < globalMinX) globalMinX = influenceMinX;
            if (influenceMaxX > globalMaxX) globalMaxX = influenceMaxX;
            if (influenceMinY < globalMinY) globalMinY = influenceMinY;
            if (influenceMaxY > globalMaxY) globalMaxY = influenceMaxY;
        }

        var interiorIndex = SpatialHashGrid2D.Build(interiorBounds);
        var influenceIndex = SpatialHashGrid2D.Build(influenceBounds);

        int barrierCount = Math.Max(barriers.Segments.Length, 1);
        System.Threading.Tasks.Parallel.For(
            0,
            vertexCount,
            () => (
                InteriorScratch: new SpatialHashGrid2D.QueryScratch(pads.Length),
                InfluenceScratch: new SpatialHashGrid2D.QueryScratch(pads.Length),
                BarrierScratch: new SpatialHashGrid2D.QueryScratch(barrierCount),
                InteriorCandidates: new List<int>(8),
                InfluenceCandidates: new List<int>(8),
                BarrierCandidates: new List<int>(8)),
            (i, _, state) =>
        {
            double px = gradedVertices[i * 3];
            double py = gradedVertices[i * 3 + 1];

            if (px < globalMinX || px > globalMaxX || py < globalMinY || py > globalMaxY)
                return state;

            interiorIndex.GatherCandidates(
                Bounds2D.FromPoint(px, py),
                state.InteriorCandidates,
                state.InteriorScratch);

            int insidePadIdx = -1;
            foreach (int p in state.InteriorCandidates)
            {
                var bounds = padBounds[p];
                if (px < bounds.MinX || px > bounds.MaxX || py < bounds.MinY || py > bounds.MaxY)
                    continue;

                if (PointInPolygon(px, py, bounds.Pad.XyVertices, bounds.Pad.VertexCount))
                    insidePadIdx = Math.Max(insidePadIdx, p);
            }

            if (insidePadIdx >= 0)
            {
                gradedVertices[i * 3 + 2] = pads[insidePadIdx].EvaluateZ(px, py);
                return state;
            }

            influenceIndex.GatherCandidates(
                Bounds2D.FromPoint(px, py),
                state.InfluenceCandidates,
                state.InfluenceScratch);

            double nearestDist = double.MaxValue;
            int nearestPadIdx = -1;
            double nearestBoundaryZ = 0.0;
            double nearestBoundaryPx = px;
            double nearestBoundaryPy = py;
            foreach (int p in state.InfluenceCandidates)
            {
                var bounds = padBounds[p];
                if (px < bounds.InfluenceMinX || px > bounds.InfluenceMaxX || py < bounds.InfluenceMinY || py > bounds.InfluenceMaxY)
                    continue;

                double dist = DistToBoundaryWithZ(px, py, bounds.Pad.BoundaryVertices, bounds.Pad.VertexCount,
                    out double boundaryZ, out double bpx, out double bpy);
                if (dist < nearestDist - 1e-12 ||
                    (Math.Abs(dist - nearestDist) <= 1e-12 && (nearestPadIdx < 0 || p < nearestPadIdx)))
                {
                    nearestDist = dist;
                    nearestPadIdx = p;
                    nearestBoundaryZ = boundaryZ;
                    nearestBoundaryPx = bpx;
                    nearestBoundaryPy = bpy;
                }
            }

            if (nearestPadIdx < 0)
                return state;

            // Skip grading if a barrier lies between this vertex and its nearest pad boundary point.
            if (barriers.Segments.Length > 0 &&
                GradingBarriers.IsCrossedByBarrier(
                    barriers, px, py, nearestBoundaryPx, nearestBoundaryPy,
                    state.BarrierScratch, state.BarrierCandidates))
                return state;

            var pad = pads[nearestPadIdx];
            double slopeRatio = Math.Tan(pad.SlopeAngleDeg * Math.PI / 180.0);
            double dz = originalVertices[i * 3 + 2] - nearestBoundaryZ;
            double absDz = Math.Abs(dz);

            double neededDist = slopeRatio > 1e-12 ? absDz / slopeRatio : double.MaxValue;
            if (pad.MaxDistance > 0)
                neededDist = Math.Min(neededDist, pad.MaxDistance);

            if (nearestDist >= neededDist)
                return state;

            double rise = nearestDist * slopeRatio;
            if (rise < absDz)
                gradedVertices[i * 3 + 2] = nearestBoundaryZ + Math.Sign(dz) * rise;
            return state;
        }, _ => { });
    }

    private static double ComputePadTransitionDistance(double[] vertices, int vertexCount, PadBoundary pad)
    {
        double slopeRatio = Math.Tan(pad.SlopeAngleDeg * Math.PI / 180.0);
        double maxZDiff = 0;
        for (int i = 0; i < vertexCount; i++)
        {
            double dz = Math.Abs(vertices[i * 3 + 2] - pad.EvaluateZ(vertices[i * 3], vertices[i * 3 + 1]));
            if (dz > maxZDiff)
                maxZDiff = dz;
        }

        double transitionDistance = slopeRatio > 1e-12 ? maxZDiff / slopeRatio : 100.0;
        if (pad.MaxDistance > 0)
            transitionDistance = Math.Min(transitionDistance, pad.MaxDistance);
        return transitionDistance;
    }

    private static double[]? AddPadShoulderConstraint(
        double[] padLoopXy,
        int padLoopVertexCount,
        double[] shoulderDistances,
        List<double> xyList,
        List<double> zList,
        SpatialHash vertHash,
        FaceGrid faceGrid,
        List<(int a, int b)> segList,
        double dedupTol,
        double[]? boundaryLoop,
        int boundaryVertexCount,
        PreparedBarriers barriers,
        SpatialHashGrid2D.QueryScratch barrierScratch,
        List<int> barrierCandidates)
    {
        if (!TryBuildShoulderLoop(
            padLoopXy,
            padLoopVertexCount,
            shoulderDistances,
            boundaryLoop,
            boundaryVertexCount,
            dedupTol,
            out var shoulderXy,
            out _))
        {
            return null;
        }

        int shoulderVertexCount = shoulderXy.Length / 2;
        var shoulderIndices = new int[shoulderVertexCount];
        for (int i = 0; i < shoulderVertexCount; i++)
        {
            double px = shoulderXy[i * 2];
            double py = shoulderXy[i * 2 + 1];

            int near = vertHash.FindNearest(xyList, px, py, dedupTol);
            if (near >= 0)
            {
                shoulderIndices[i] = near;
            }
            else
            {
                shoulderIndices[i] = zList.Count;
                xyList.Add(px);
                xyList.Add(py);
                zList.Add(faceGrid.InterpolateZ(px, py));
                vertHash.Insert(shoulderIndices[i], px, py);
            }
        }

        int AddVertex(double x, double y)
        {
            int near = vertHash.FindNearest(xyList, x, y, dedupTol);
            if (near >= 0)
                return near;
            int idx = zList.Count;
            xyList.Add(x);
            xyList.Add(y);
            zList.Add(faceGrid.InterpolateZ(x, y));
            vertHash.Insert(idx, x, y);
            return idx;
        }

        // Add shoulder ring segments, clipping each at the first barrier hit.
        // When clipped, the arc terminates at the barrier intersection — producing
        // open support runs instead of a single closed ring.
        for (int i = 0; i < shoulderVertexCount; i++)
        {
            int next = (i + 1) % shoulderVertexCount;
            double ax = shoulderXy[i * 2],    ay = shoulderXy[i * 2 + 1];
            double bx = shoulderXy[next * 2], by = shoulderXy[next * 2 + 1];

            bool clipped = GradingBarriers.TryClipSegment(
                barriers, ax, ay, bx, by,
                barrierScratch, barrierCandidates,
                out double cbx, out double cby);

            int startIdx = shoulderIndices[i];
            int endIdx = clipped ? AddVertex(cbx, cby) : shoulderIndices[next];

            if (startIdx != endIdx)
                segList.Add((startIdx, endIdx));
        }

        return shoulderXy;
    }

    /// <summary>
    /// For each vertex of the pad boundary, interpolates the terrain Z and computes the
    /// horizontal distance the slope transition needs to travel to reach the terrain surface.
    /// Capped at MaxDistance when set.
    /// </summary>
    private static double[] ComputePadBoundaryDistances(
        double[] padLoopXy,
        int padLoopVertexCount,
        FaceGrid faceGrid,
        PadBoundary pad)
    {
        double slopeRatio = Math.Tan(pad.SlopeAngleDeg * Math.PI / 180.0);
        var distances = new double[padLoopVertexCount];
        for (int i = 0; i < padLoopVertexCount; i++)
        {
            double bx = padLoopXy[i * 2];
            double by = padLoopXy[i * 2 + 1];
            double terrainZ = faceGrid.InterpolateZ(bx, by);
            double padZ = pad.EvaluateZ(bx, by);
            double dz = Math.Abs(terrainZ - padZ);
            double d = slopeRatio > 1e-12 ? dz / slopeRatio : 100.0;
            if (pad.MaxDistance > 0)
                d = Math.Min(d, pad.MaxDistance);
            distances[i] = d;
        }

        return distances;
    }

    private static bool TryBuildShoulderLoop(
        double[] padLoopXy,
        int padLoopVertexCount,
        double[] shoulderDistances,
        double[]? boundaryLoop,
        int boundaryVertexCount,
        double tolerance,
        out double[] shoulderXy,
        out string? skipReason)
    {
        shoulderXy = Array.Empty<double>();
        skipReason = null;
        double maxDist = 0;
        foreach (double d in shoulderDistances) if (d > maxDist) maxDist = d;
        if (maxDist <= tolerance)
            return false;

        if (!TryBuildOffsetPolygon(padLoopXy, padLoopVertexCount, shoulderDistances, out shoulderXy))
        {
            skipReason = "Grade Pad shoulder ring was skipped because the daylight offset could not be constructed cleanly.";
            return false;
        }

        if (boundaryLoop != null &&
            !AllPointsInsideOrOnBoundary(shoulderXy, padLoopVertexCount, boundaryLoop, boundaryVertexCount, tolerance))
        {
            shoulderXy = Array.Empty<double>();
            skipReason = "Grade Pad shoulder ring was skipped because the daylight offset reached the terrain boundary.";
            return false;
        }

        return true;
    }

    private static double UpdateSuggestedEdgeLength(
        double current,
        double[] points,
        int pointCount,
        int stride,
        bool isClosed)
    {
        if (pointCount < 2)
            return current;

        int segmentCount = isClosed ? pointCount : pointCount - 1;
        for (int i = 0; i < segmentCount; i++)
        {
            int next = (i + 1) % pointCount;
            double dx = points[next * stride] - points[i * stride];
            double dy = points[next * stride + 1] - points[i * stride + 1];
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length > 1e-9)
                current = Math.Min(current, length);
        }

        return current;
    }

    private static double ComputePadConstraintSegmentLength(double shoulderDistance)
    {
        if (shoulderDistance <= 1e-9)
            return 1.0;

        return Math.Clamp(shoulderDistance * 0.5, 0.5, 5.0);
    }

    private readonly record struct ConstraintLoop(double[] XyVertices, int VertexCount);

    private static ConstraintLoop BuildClosedConstraintLoop(double[] xyVertices, int vertexCount, double maxSegmentLength, double tolerance)
    {
        if (vertexCount < 3 || maxSegmentLength <= tolerance)
            return new ConstraintLoop((double[])xyVertices.Clone(), vertexCount);

        var points = new List<double>(vertexCount * 4);
        for (int i = 0; i < vertexCount; i++)
        {
            int next = (i + 1) % vertexCount;
            double ax = xyVertices[i * 2];
            double ay = xyVertices[i * 2 + 1];
            double bx = xyVertices[next * 2];
            double by = xyVertices[next * 2 + 1];
            double length = Math.Sqrt(((bx - ax) * (bx - ax)) + ((by - ay) * (by - ay)));
            int divisions = Math.Max(1, (int)Math.Ceiling(length / maxSegmentLength));

            for (int step = 0; step < divisions; step++)
            {
                double t = step / (double)divisions;
                AddLoopPoint(points, ax + ((bx - ax) * t), ay + ((by - ay) * t), tolerance);
            }
        }

        return new ConstraintLoop(points.ToArray(), points.Count / 2);
    }

    private static void AddLoopPoint(List<double> points, double x, double y, double tolerance)
    {
        if (points.Count >= 2)
        {
            double dx = x - points[^2];
            double dy = y - points[^1];
            if ((dx * dx) + (dy * dy) <= tolerance * tolerance)
                return;
        }

        points.Add(x);
        points.Add(y);
    }

    private static double[] CreateConstraintPoints(double[] xyVertices, int vertexCount)
    {
        var points = new double[vertexCount * 3];
        for (int i = 0; i < vertexCount; i++)
        {
            points[i * 3] = xyVertices[i * 2];
            points[i * 3 + 1] = xyVertices[i * 2 + 1];
        }

        return points;
    }

    private static void AddClosedLoopSegments(
        double[] xyVertices,
        int vertexCount,
        Func<double, double, int> addVertex,
        List<(int a, int b)> segList)
    {
        if (vertexCount < 3)
            return;

        int first = addVertex(xyVertices[0], xyVertices[1]);
        int previous = first;
        for (int i = 1; i < vertexCount; i++)
        {
            int current = addVertex(xyVertices[i * 2], xyVertices[i * 2 + 1]);
            if (previous != current)
                segList.Add((previous, current));
            previous = current;
        }

        if (previous != first)
            segList.Add((previous, first));
    }

    private static bool TryBuildOffsetPolygon(double[] polygonXy, int vertexCount, double[] distances, out double[] offsetXy)
    {
        offsetXy = Array.Empty<double>();
        if (vertexCount < 3 || distances.Length < vertexCount)
            return false;
        bool anyPositive = false;
        for (int i = 0; i < vertexCount; i++) if (distances[i] > 1e-9) { anyPositive = true; break; }
        if (!anyPositive) return false;

        double signedArea = 0;
        for (int i = 0; i < vertexCount; i++)
        {
            int next = (i + 1) % vertexCount;
            double x0 = polygonXy[i * 2];
            double y0 = polygonXy[i * 2 + 1];
            double x1 = polygonXy[next * 2];
            double y1 = polygonXy[next * 2 + 1];
            signedArea += x0 * y1 - x1 * y0;
        }

        if (Math.Abs(signedArea) < 1e-12)
            return false;

        bool ccw = signedArea > 0;
        offsetXy = new double[vertexCount * 2];

        for (int i = 0; i < vertexCount; i++)
        {
            int prev = (i + vertexCount - 1) % vertexCount;
            int next = (i + 1) % vertexCount;

            double x0 = polygonXy[prev * 2];
            double y0 = polygonXy[prev * 2 + 1];
            double x1 = polygonXy[i * 2];
            double y1 = polygonXy[i * 2 + 1];
            double x2 = polygonXy[next * 2];
            double y2 = polygonXy[next * 2 + 1];

            double dx0 = x1 - x0;
            double dy0 = y1 - y0;
            double dx1 = x2 - x1;
            double dy1 = y2 - y1;
            double len0 = Math.Sqrt(dx0 * dx0 + dy0 * dy0);
            double len1 = Math.Sqrt(dx1 * dx1 + dy1 * dy1);
            if (len0 < 1e-12 || len1 < 1e-12)
                return false;

            double n0x = ccw ? dy0 / len0 : -dy0 / len0;
            double n0y = ccw ? -dx0 / len0 : dx0 / len0;
            double n1x = ccw ? dy1 / len1 : -dy1 / len1;
            double n1y = ccw ? -dx1 / len1 : dx1 / len1;

            double d = distances[i];
            double line0x = x1 + n0x * d;
            double line0y = y1 + n0y * d;
            double line1x = x1 + n1x * d;
            double line1y = y1 + n1y * d;

            if (TryIntersectLines(line0x, line0y, dx0, dy0, line1x, line1y, dx1, dy1, out double ix, out double iy))
            {
                double offsetLen = Math.Sqrt((ix - x1) * (ix - x1) + (iy - y1) * (iy - y1));
                if (offsetLen <= d * 4.0 && !double.IsNaN(offsetLen) && !double.IsInfinity(offsetLen))
                {
                    offsetXy[i * 2] = ix;
                    offsetXy[i * 2 + 1] = iy;
                    continue;
                }
            }

            double bisX = n0x + n1x;
            double bisY = n0y + n1y;
            double bisLen = Math.Sqrt(bisX * bisX + bisY * bisY);
            if (bisLen < 1e-12)
            {
                bisX = n0x;
                bisY = n0y;
                bisLen = Math.Sqrt(bisX * bisX + bisY * bisY);
            }

            offsetXy[i * 2] = x1 + bisX / bisLen * d;
            offsetXy[i * 2 + 1] = y1 + bisY / bisLen * d;
        }

        return true;
    }

    internal static bool TryBuildBoundaryLoop(double[] vertices, int[] faces, int faceCount, out double[] boundaryXy, out int boundaryVertexCount)
    {
        boundaryXy = Array.Empty<double>();
        boundaryVertexCount = 0;

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

        var adjacency = new Dictionary<int, List<int>>();
        int segmentCount = 0;
        foreach (var pair in edgeFaceCount)
        {
            if (pair.Value != 1)
                continue;

            int a = (int)(pair.Key >> 32);
            int b = (int)(pair.Key & 0xFFFFFFFFL);
            AddBoundaryNeighbor(adjacency, a, b);
            AddBoundaryNeighbor(adjacency, b, a);
            segmentCount++;
        }

        if (segmentCount < 3 || adjacency.Count == 0)
            return false;

        foreach (var neighbors in adjacency.Values)
        {
            if (neighbors.Count != 2)
                return false;
        }

        int start = adjacency.Keys.Min();
        var order = new List<int>(adjacency.Count);
        int previous = -1;
        int current = start;

        while (true)
        {
            order.Add(current);
            var neighbors = adjacency[current];
            int next = neighbors[0] != previous ? neighbors[0] : neighbors[1];
            previous = current;
            current = next;

            if (current == start)
                break;

            if (order.Count > adjacency.Count)
                return false;
        }

        if (order.Count < 3 || order.Count != adjacency.Count)
            return false;

        boundaryVertexCount = order.Count;
        boundaryXy = new double[boundaryVertexCount * 2];
        for (int i = 0; i < boundaryVertexCount; i++)
        {
            int vertexIndex = order[i];
            boundaryXy[i * 2] = vertices[vertexIndex * 3];
            boundaryXy[i * 2 + 1] = vertices[vertexIndex * 3 + 1];
        }

        return true;
    }

    private static void AddBoundaryNeighbor(Dictionary<int, List<int>> adjacency, int from, int to)
    {
        if (!adjacency.TryGetValue(from, out var list))
        {
            list = new List<int>(2);
            adjacency[from] = list;
        }

        list.Add(to);
    }

    internal static bool AllPointsInsideOrOnBoundary(double[] xy, int vertexCount, double[] boundaryLoop, int boundaryVertexCount, double tolerance)
    {
        for (int i = 0; i < vertexCount; i++)
        {
            double px = xy[i * 2];
            double py = xy[i * 2 + 1];
            if (PointInPolygon(px, py, boundaryLoop, boundaryVertexCount))
                continue;

            if (DistToPolygon(px, py, boundaryLoop, boundaryVertexCount) <= tolerance)
                continue;

            return false;
        }

        return true;
    }

    private static bool TryIntersectLines(
        double ax, double ay, double adx, double ady,
        double bx, double by, double bdx, double bdy,
        out double ix, out double iy)
    {
        double denom = adx * bdy - ady * bdx;
        if (Math.Abs(denom) < 1e-12)
        {
            ix = 0;
            iy = 0;
            return false;
        }

        double t = ((bx - ax) * bdy - (by - ay) * bdx) / denom;
        ix = ax + t * adx;
        iy = ay + t * ady;
        return true;
    }

    internal static double DistToBoundaryWithZ(
        double px,
        double py,
        double[] boundaryVertices,
        int boundaryVertexCount,
        out double boundaryZ,
        out double closestBx,
        out double closestBy)
    {
        boundaryZ = 0;
        closestBx = px;
        closestBy = py;
        double minDist = double.MaxValue;

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
            double t = 0;
            double cx = ax;
            double cy = ay;
            if (lenSq > 1e-20)
            {
                t = Math.Clamp(((px - ax) * dx + (py - ay) * dy) / lenSq, 0.0, 1.0);
                cx = ax + t * dx;
                cy = ay + t * dy;
            }

            double dist = Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
            if (dist >= minDist)
                continue;

            minDist = dist;
            boundaryZ = az + (bz - az) * t;
            closestBx = cx;
            closestBy = cy;
        }

        return minDist;
    }

    private static GradingResult BuildResult(
        double[] originalVertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        double[] gradedVertices,
        IReadOnlyList<OutputPolyline>? outputPolylines = null)
    {
        double cutVol = 0;
        double fillVol = 0;
        for (int f = 0; f < faceCount; f++)
        {
            int i0 = faces[f * 3];
            int i1 = faces[f * 3 + 1];
            int i2 = faces[f * 3 + 2];

            double area2d = Math.Abs(
                (gradedVertices[i1 * 3] - gradedVertices[i0 * 3]) * (gradedVertices[i2 * 3 + 1] - gradedVertices[i0 * 3 + 1])
              - (gradedVertices[i2 * 3] - gradedVertices[i0 * 3]) * (gradedVertices[i1 * 3 + 1] - gradedVertices[i0 * 3 + 1]))
                * 0.5;

            double dz0 = gradedVertices[i0 * 3 + 2] - originalVertices[i0 * 3 + 2];
            double dz1 = gradedVertices[i1 * 3 + 2] - originalVertices[i1 * 3 + 2];
            double dz2 = gradedVertices[i2 * 3 + 2] - originalVertices[i2 * 3 + 2];
            double avgDz = (dz0 + dz1 + dz2) / 3.0;

            double vol = area2d * avgDz;
            if (vol > 0)
                fillVol += vol;
            else
                cutVol += -vol;
        }

        var daylightPts = new List<double>();
        var processedEdges = new HashSet<long>();

        for (int f = 0; f < faceCount; f++)
        {
            int i0 = faces[f * 3];
            int i1 = faces[f * 3 + 1];
            int i2 = faces[f * 3 + 2];
            CheckDaylightEdge(i0, i1, originalVertices, gradedVertices, processedEdges, daylightPts);
            CheckDaylightEdge(i1, i2, originalVertices, gradedVertices, processedEdges, daylightPts);
            CheckDaylightEdge(i2, i0, originalVertices, gradedVertices, processedEdges, daylightPts);
        }

        return new GradingResult(
            gradedVertices,
            vertexCount,
            (int[])faces.Clone(),
            faceCount,
            cutVol,
            fillVol,
            daylightPts.ToArray(),
            daylightPts.Count / 3,
            outputPolylines);
    }

    private static void CheckDaylightEdge(
        int a,
        int b,
        double[] originalVertices,
        double[] gradedVertices,
        HashSet<long> processed,
        List<double> pts)
    {
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        if (!processed.Add(key))
            return;

        double dzA = gradedVertices[a * 3 + 2] - originalVertices[a * 3 + 2];
        double dzB = gradedVertices[b * 3 + 2] - originalVertices[b * 3 + 2];
        const double threshold = 0.001;

        if ((dzA > threshold && dzB < -threshold) || (dzA < -threshold && dzB > threshold))
        {
            double t = dzA / (dzA - dzB);
            pts.Add(gradedVertices[a * 3] + t * (gradedVertices[b * 3] - gradedVertices[a * 3]));
            pts.Add(gradedVertices[a * 3 + 1] + t * (gradedVertices[b * 3 + 1] - gradedVertices[a * 3 + 1]));
            pts.Add(gradedVertices[a * 3 + 2] + t * (gradedVertices[b * 3 + 2] - gradedVertices[a * 3 + 2]));
        }
        else if (Math.Abs(dzA) <= threshold && Math.Abs(dzB) > threshold)
        {
            pts.Add(gradedVertices[a * 3]);
            pts.Add(gradedVertices[a * 3 + 1]);
            pts.Add(gradedVertices[a * 3 + 2]);
        }
        else if (Math.Abs(dzB) <= threshold && Math.Abs(dzA) > threshold)
        {
            pts.Add(gradedVertices[b * 3]);
            pts.Add(gradedVertices[b * 3 + 1]);
            pts.Add(gradedVertices[b * 3 + 2]);
        }
    }

    // Spatial data structures.

    internal sealed class SpatialHash
    {
        private readonly double _cellSize;
        private readonly double _invCell;
        private readonly Dictionary<long, List<int>> _grid = new();

        public SpatialHash(double tolerance)
        {
            _cellSize = Math.Max(tolerance * 2, 1e-10);
            _invCell = 1.0 / _cellSize;
        }

        public void Insert(int index, double x, double y)
        {
            long key = CellKey(x, y);
            if (!_grid.TryGetValue(key, out var list))
            {
                list = new List<int>();
                _grid[key] = list;
            }

            list.Add(index);
        }

        public int FindNearest(List<double> xyList, double px, double py, double tolerance)
        {
            double tolSq = tolerance * tolerance;
            long cx = (long)Math.Floor(px * _invCell);
            long cy = (long)Math.Floor(py * _invCell);

            for (long dx = -1; dx <= 1; dx++)
            {
                for (long dy = -1; dy <= 1; dy++)
                {
                    long key = PackKey(cx + dx, cy + dy);
                    if (!_grid.TryGetValue(key, out var indices))
                        continue;

                    foreach (int idx in indices)
                    {
                        double ex = xyList[idx * 2];
                        double ey = xyList[idx * 2 + 1];
                        double d2 = (px - ex) * (px - ex) + (py - ey) * (py - ey);
                        if (d2 < tolSq)
                            return idx;
                    }
                }
            }

            return -1;
        }

        private long CellKey(double x, double y) =>
            PackKey((long)Math.Floor(x * _invCell), (long)Math.Floor(y * _invCell));

        private static long PackKey(long cx, long cy) =>
            (cx * 0x100000001L) ^ (cy * 0x27d4eb2dL);
    }

    internal sealed class FaceGrid
    {
        private readonly double[] _verts;
        private readonly int[] _faces;
        private readonly Dictionary<long, List<int>> _grid;
        private readonly double _invCell;

        public FaceGrid(double[] vertices, int vertexCount, int[] faces, int faceCount)
        {
            _verts = vertices;
            _faces = faces;

            double minX = double.MaxValue;
            double maxX = double.MinValue;
            double minY = double.MaxValue;
            double maxY = double.MinValue;
            for (int i = 0; i < vertexCount; i++)
            {
                double x = vertices[i * 3];
                double y = vertices[i * 3 + 1];
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }

            double span = Math.Max(maxX - minX, maxY - minY);
            int gridRes = Math.Max(1, (int)Math.Sqrt(faceCount / 4.0));
            double cellSize = Math.Max(span / gridRes, 1e-6);
            _invCell = 1.0 / cellSize;

            _grid = new Dictionary<long, List<int>>(faceCount);
            for (int f = 0; f < faceCount; f++)
            {
                int i0 = faces[f * 3];
                int i1 = faces[f * 3 + 1];
                int i2 = faces[f * 3 + 2];
                double x0 = vertices[i0 * 3];
                double y0 = vertices[i0 * 3 + 1];
                double x1 = vertices[i1 * 3];
                double y1 = vertices[i1 * 3 + 1];
                double x2 = vertices[i2 * 3];
                double y2 = vertices[i2 * 3 + 1];

                long cMinX = (long)Math.Floor(Math.Min(x0, Math.Min(x1, x2)) * _invCell);
                long cMaxX = (long)Math.Floor(Math.Max(x0, Math.Max(x1, x2)) * _invCell);
                long cMinY = (long)Math.Floor(Math.Min(y0, Math.Min(y1, y2)) * _invCell);
                long cMaxY = (long)Math.Floor(Math.Max(y0, Math.Max(y1, y2)) * _invCell);

                for (long cy = cMinY; cy <= cMaxY; cy++)
                {
                    for (long cx = cMinX; cx <= cMaxX; cx++)
                    {
                        long key = (cx * 0x100000001L) ^ (cy * 0x27d4eb2dL);
                        if (!_grid.TryGetValue(key, out var list))
                        {
                            list = new List<int>();
                            _grid[key] = list;
                        }

                        list.Add(f);
                    }
                }
            }
        }

        public double InterpolateZ(double px, double py)
        {
            const double tol = 1e-4;
            long cx = (long)Math.Floor(px * _invCell);
            long cy = (long)Math.Floor(py * _invCell);

            for (long dx = -1; dx <= 1; dx++)
            {
                for (long dy = -1; dy <= 1; dy++)
                {
                    long key = ((cx + dx) * 0x100000001L) ^ ((cy + dy) * 0x27d4eb2dL);
                    if (!_grid.TryGetValue(key, out var faceIndices))
                        continue;

                    foreach (int f in faceIndices)
                    {
                        int i0 = _faces[f * 3];
                        int i1 = _faces[f * 3 + 1];
                        int i2 = _faces[f * 3 + 2];
                        double x0 = _verts[i0 * 3];
                        double y0 = _verts[i0 * 3 + 1];
                        double z0 = _verts[i0 * 3 + 2];
                        double x1 = _verts[i1 * 3];
                        double y1 = _verts[i1 * 3 + 1];
                        double z1 = _verts[i1 * 3 + 2];
                        double x2 = _verts[i2 * 3];
                        double y2 = _verts[i2 * 3 + 1];
                        double z2 = _verts[i2 * 3 + 2];

                        double denom = (y1 - y2) * (x0 - x2) + (x2 - x1) * (y0 - y2);
                        if (Math.Abs(denom) < 1e-12)
                            continue;

                        double w0 = ((y1 - y2) * (px - x2) + (x2 - x1) * (py - y2)) / denom;
                        double w1 = ((y2 - y0) * (px - x2) + (x0 - x2) * (py - y2)) / denom;
                        double w2 = 1.0 - w0 - w1;

                        if (w0 >= -tol && w1 >= -tol && w2 >= -tol)
                            return w0 * z0 + w1 * z1 + w2 * z2;
                    }
                }
            }

            return NearestVertexZ(px, py);
        }

        private double NearestVertexZ(double px, double py)
        {
            double nearestZ = 0;
            double nearestDistSq = double.MaxValue;
            int vCount = _verts.Length / 3;
            for (int i = 0; i < vCount; i++)
            {
                double dx = _verts[i * 3] - px;
                double dy = _verts[i * 3 + 1] - py;
                double distSq = dx * dx + dy * dy;
                if (distSq < nearestDistSq)
                {
                    nearestDistSq = distSq;
                    nearestZ = _verts[i * 3 + 2];
                }
            }

            return nearestZ;
        }
    }

    // Geometry helpers (public for cross-assembly use).

    public static bool PointInPolygon(double px, double py, double[] polyXy, int polyVertCount)
    {
        bool inside = false;
        for (int i = 0, j = polyVertCount - 1; i < polyVertCount; j = i++)
        {
            double xi = polyXy[i * 2];
            double yi = polyXy[i * 2 + 1];
            double xj = polyXy[j * 2];
            double yj = polyXy[j * 2 + 1];

            if (((yi > py) != (yj > py)) &&
                (px < (xj - xi) * (py - yi) / (yj - yi) + xi))
            {
                inside = !inside;
            }
        }

        return inside;
    }

    public static double DistToPolygon(double px, double py, double[] polyXy, int polyVertCount)
    {
        double minDist = double.MaxValue;
        for (int i = 0, j = polyVertCount - 1; i < polyVertCount; j = i++)
        {
            double dist = DistToSegment(
                px,
                py,
                polyXy[j * 2],
                polyXy[j * 2 + 1],
                polyXy[i * 2],
                polyXy[i * 2 + 1]);
            if (dist < minDist)
                minDist = dist;
        }

        return minDist;
    }

    private static double DistToSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax;
        double dy = by - ay;
        double lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-20)
            return Math.Sqrt((px - ax) * (px - ax) + (py - ay) * (py - ay));

        double t = Math.Max(0, Math.Min(1, ((px - ax) * dx + (py - ay) * dy) / lenSq));
        double cx = ax + t * dx;
        double cy = ay + t * dy;
        return Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
    }

    public static int FindNearVertex(List<double> xyList, double px, double py, double tolerance)
    {
        double tolSq = tolerance * tolerance;
        int count = xyList.Count / 2;
        for (int i = 0; i < count; i++)
        {
            double dx = xyList[i * 2] - px;
            double dy = xyList[i * 2 + 1] - py;
            if (dx * dx + dy * dy < tolSq)
                return i;
        }

        return -1;
    }

    private static void IncrEdge(Dictionary<long, int> dict, int a, int b)
    {
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        dict[key] = dict.GetValueOrDefault(key, 0) + 1;
    }

    public static double InterpolateZ(double[] vertices, int[] faces, int faceCount, double px, double py)
    {
        const double tol = 1e-4;
        for (int f = 0; f < faceCount; f++)
        {
            int i0 = faces[f * 3];
            int i1 = faces[f * 3 + 1];
            int i2 = faces[f * 3 + 2];
            double x0 = vertices[i0 * 3];
            double y0 = vertices[i0 * 3 + 1];
            double z0 = vertices[i0 * 3 + 2];
            double x1 = vertices[i1 * 3];
            double y1 = vertices[i1 * 3 + 1];
            double z1 = vertices[i1 * 3 + 2];
            double x2 = vertices[i2 * 3];
            double y2 = vertices[i2 * 3 + 1];
            double z2 = vertices[i2 * 3 + 2];

            double denom = (y1 - y2) * (x0 - x2) + (x2 - x1) * (y0 - y2);
            if (Math.Abs(denom) < 1e-12)
                continue;

            double w0 = ((y1 - y2) * (px - x2) + (x2 - x1) * (py - y2)) / denom;
            double w1 = ((y2 - y0) * (px - x2) + (x0 - x2) * (py - y2)) / denom;
            double w2 = 1.0 - w0 - w1;

            if (w0 >= -tol && w1 >= -tol && w2 >= -tol)
                return w0 * z0 + w1 * z1 + w2 * z2;
        }

        double nearestZ = 0;
        double nearestDistSq = double.MaxValue;
        int vCount = vertices.Length / 3;
        for (int i = 0; i < vCount; i++)
        {
            double dx = vertices[i * 3] - px;
            double dy = vertices[i * 3 + 1] - py;
            double distSq = dx * dx + dy * dy;
            if (distSq < nearestDistSq)
            {
                nearestDistSq = distSq;
                nearestZ = vertices[i * 3 + 2];
            }
        }

        return nearestZ;
    }
}
