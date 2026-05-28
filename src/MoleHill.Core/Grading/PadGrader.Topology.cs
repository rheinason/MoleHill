using TriangleNet;
using TriangleNet.Geometry;
using TriangleNet.Meshing;
using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

public static partial class PadGrader
{
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
        lockCurves ??= Array.Empty<LockCurve>();

        if (!GradingInputValidator.ValidateTerrainMesh(vertices, vertexCount, faces, faceCount, out warningOrError))
            return false;

        if (!ValidatePads(pads, out warningOrError))
            return false;

        pads = OrderPadsForOwnership(pads);

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

        pads = OrderPadsForOwnership(pads);

        PreparedBarriers barriers = lockCurves != null && lockCurves.Length > 0
            ? GradingBarriers.BuildFromLockCurves(lockCurves)
            : PreparedBarriers.Empty;

        var gradedVertices = (double[])topologyVertices.Clone();
        ApplyGradingToVertices(gradedVertices, topologyVertices, vertexCount, pads, barriers);
        return gradedVertices;
    }

    public static double[] ApplyGradingZ(
        double[] topologyVertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PadBoundary[] pads,
        LockCurve[]? lockCurves = null)
    {
        if (pads.Length == 0)
            return (double[])topologyVertices.Clone();

        pads = OrderPadsForOwnership(pads);

        PreparedBarriers barriers = lockCurves != null && lockCurves.Length > 0
            ? GradingBarriers.BuildFromLockCurves(lockCurves)
            : PreparedBarriers.Empty;
        if (pads.Any(static pad => PolygonHasConcaveVertex(pad.XyVertices, pad.VertexCount)))
        {
            var fallbackGradedVertices = (double[])topologyVertices.Clone();
            ApplyGradingToVertices(fallbackGradedVertices, topologyVertices, vertexCount, pads, barriers);
            return fallbackGradedVertices;
        }

        bool hasBoundaryLoop = TryBuildBoundaryLoop(topologyVertices, faces, faceCount, out double[] boundaryLoop, out int boundaryVertexCount);
        var TerrainFaceGrid = new TerrainFaceGrid(topologyVertices, vertexCount, faces, faceCount);
        var gradedVertices = (double[])topologyVertices.Clone();
        ApplyGradingToVerticesWithSections(
            gradedVertices,
            topologyVertices,
            vertexCount,
            pads,
            barriers,
            TerrainFaceGrid,
            hasBoundaryLoop,
            boundaryLoop,
            boundaryVertexCount,
            tolerance: 1e-3);
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

        pads = OrderPadsForOwnership(pads);

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
        LockCurve[]? lockCurves,
        double modelTolerance = GradingTolerances.DefaultModelTolerance)
    {
        double dedupTol = GradingTolerances.ModelToleranceOrDefault(modelTolerance);
        lockCurves ??= Array.Empty<LockCurve>();
        if (!GradingInputValidator.ValidateTerrainMesh(vertices, vertexCount, faces, faceCount, out string? terrainError))
        {
            GradingDiagnostic diagnostic = GradingDiagnostic.Warning(
                "grade_pad.input.invalid_terrain",
                terrainError ?? "Invalid terrain mesh.",
                operation: "grade_pad");
            return new ConstraintSet
            {
                Constraints = Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
                SuggestedEdgeLength = 0.0,
                Diagnostics = [diagnostic.Message],
                StructuredDiagnostics = [diagnostic]
            };
        }

        if (!ValidatePads(pads, out _))
        {
            return new ConstraintSet
            {
                Constraints = Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
                SuggestedEdgeLength = 0.0,
                Diagnostics = Array.Empty<string>(),
                StructuredDiagnostics = Array.Empty<GradingDiagnostic>()
            };
        }

        pads = OrderPadsForOwnership(pads);

        bool hasBoundaryLoop = TryBuildBoundaryLoop(vertices, faces, faceCount, out var boundaryLoop, out int boundaryVertexCount);
        var constraints = new List<SurfaceRemesher.ConstraintPolyline>(pads.Length * 3 + lockCurves.Length);
        var diagnostics = new GradingDiagnosticCollector();
        double suggestedEdgeLength = double.MaxValue;
        var coincidenceSnapper = new ConstraintCoincidenceSnapper(
            vertices,
            vertexCount,
            faces,
            faceCount,
            Math.Max(dedupTol, GradingTolerances.ConstraintSnapTolerance(dedupTol)));

        var faceGridForConstraints = new TerrainFaceGrid(vertices, vertexCount, faces, faceCount);
        for (int padIndex = 0; padIndex < pads.Length; padIndex++)
        {
            PadBoundary pad = pads[padIndex];
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

                if (TryBuildProtectedStitchLoop(
                        shoulderXy,
                        pad.StitchApronDistance,
                        hasBoundaryLoop ? boundaryLoop : null,
                        hasBoundaryLoop ? boundaryVertexCount : 0,
                        dedupTol,
                        out double[] stitchXy,
                        out string? stitchSkipReason))
                {
                    int stitchVertexCount = stitchXy.Length / 2;
                    constraints.Add(new SurfaceRemesher.ConstraintPolyline(
                        CreateConstraintPoints(stitchXy, stitchVertexCount),
                        stitchVertexCount,
                        IsClosed: true,
                        PreserveInputElevation: false));
                    suggestedEdgeLength = UpdateSuggestedEdgeLength(suggestedEdgeLength, stitchXy, stitchVertexCount, stride: 2, isClosed: true);
                }
                else if (!string.IsNullOrWhiteSpace(stitchSkipReason))
                {
                    diagnostics.AddWarning(
                        "grade_pad.stitch_loop.skipped",
                        stitchSkipReason!,
                        operation: "Grade Pad",
                        targetIndex: padIndex);
                }
            }
            else if (!string.IsNullOrWhiteSpace(skipReason))
            {
                diagnostics.AddWarning(
                    "grade_pad.shoulder_loop.skipped",
                    skipReason!,
                    operation: "Grade Pad",
                    targetIndex: padIndex);
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

        for (int i = 0; i < constraints.Count; i++)
            constraints[i] = coincidenceSnapper.SnapConstraintPolyline(constraints[i]);

        return new ConstraintSet
        {
            Constraints = constraints.ToArray(),
            SuggestedEdgeLength = suggestedEdgeLength < double.MaxValue ? suggestedEdgeLength : 0.0,
            Diagnostics = diagnostics.ToMessages(),
            StructuredDiagnostics = diagnostics.ToStructuredDiagnostics()
        };
    }

    private static bool ValidatePads(PadBoundary[] pads, out string? errorMessage)
    {
        errorMessage = null;

        if (pads == null)
        {
            errorMessage = "No pad boundaries provided.";
            return false;
        }

        if (pads.Length == 0)
        {
            errorMessage = "No pad boundaries provided.";
            return false;
        }

        foreach (var pad in pads)
        {
            if (pad == null ||
                pad.VertexCount < 3)
            {
                errorMessage = "Each pad must have at least 3 valid vertices.";
                return false;
            }

            long requiredPadXyValues = (long)pad.VertexCount * 2;
            long requiredPadBoundaryValues = (long)pad.VertexCount * 3;
            if (requiredPadXyValues > int.MaxValue ||
                requiredPadBoundaryValues > int.MaxValue ||
                !GradingInputValidator.ValidateFiniteValues(
                    pad.XyVertices,
                    (int)requiredPadXyValues,
                    "Each pad must have at least 3 valid vertices.",
                    "Pad coordinates must contain only finite values.",
                    out errorMessage) ||
                !GradingInputValidator.ValidateFiniteValues(
                    pad.BoundaryVertices,
                    (int)requiredPadBoundaryValues,
                    "Each pad must have at least 3 valid vertices.",
                    "Pad boundary vertices must contain only finite values.",
                    out errorMessage))
            {
                return false;
            }

            if (!double.IsFinite(pad.PlaneXCoeff) ||
                !double.IsFinite(pad.PlaneYCoeff) ||
                !double.IsFinite(pad.PlaneConstant) ||
                !double.IsFinite(pad.SlopeAngleDeg) ||
                !double.IsFinite(pad.MaxDistance) ||
                !double.IsFinite(pad.StitchApronDistance) ||
                pad.MaxDistance < 0.0 ||
                pad.StitchApronDistance < 0.0)
            {
                errorMessage = "Each pad must define valid finite grading parameters.";
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

        static bool IsInsideControlledRegion(double x, double y, IReadOnlyList<double[]> controlLoops)
        {
            foreach (double[] loop in controlLoops)
            {
                int loopVertexCount = loop.Length / 2;
                if (loopVertexCount >= 3 && PointInPolygon(x, y, loop, loopVertexCount))
                    return true;
            }

            return false;
        }

        var xyList = new List<double>(vertexCount * 2);
        var zList = new List<double>(vertexCount);
        var segList = new List<(int a, int b)>();
        var coincidenceSnapper = new ConstraintCoincidenceSnapper(
            vertices,
            vertexCount,
            faces,
            faceCount,
            Math.Max(dedupTol, GradingTolerances.ConstraintSnapTolerance(dedupTol)));

        var vertHash = new SpatialVertexHash(dedupTol);

        var TerrainFaceGrid = new TerrainFaceGrid(vertices, vertexCount, faces, faceCount);
        bool hasBoundaryLoop = TryBuildBoundaryLoop(vertices, faces, faceCount, out var boundaryLoop, out int boundaryVertexCount);
        var controlledLoops = new List<double[]>(pads.Length * 2);

        foreach (var pad in pads)
        {
            double[] initialDistances = ComputePadBoundaryDistances(pad.XyVertices, pad.VertexCount, TerrainFaceGrid, pad);
            double maxDistance = 0.0;
            foreach (double distance in initialDistances)
                maxDistance = Math.Max(maxDistance, distance);

            double segmentLength = ComputePadConstraintSegmentLength(maxDistance);
            var padLoop = BuildClosedConstraintLoop(pad.XyVertices, pad.VertexCount, segmentLength, dedupTol);
            controlledLoops.Add(padLoop.XyVertices);

            double[] shoulderDistances = ComputePadBoundaryDistances(padLoop.XyVertices, padLoop.VertexCount, TerrainFaceGrid, pad);
            if (TryBuildShoulderLoop(
                padLoop.XyVertices,
                padLoop.VertexCount,
                shoulderDistances,
                hasBoundaryLoop ? boundaryLoop : null,
                hasBoundaryLoop ? boundaryVertexCount : 0,
                dedupTol,
                out var shoulderXy,
                out _))
            {
                controlledLoops.Add(shoulderXy);
                if (TryBuildProtectedStitchLoop(
                        shoulderXy,
                        pad.StitchApronDistance,
                        hasBoundaryLoop ? boundaryLoop : null,
                        hasBoundaryLoop ? boundaryVertexCount : 0,
                        dedupTol,
                        out double[] stitchXy,
                        out _))
                {
                    controlledLoops.Add(stitchXy);
                }
            }
        }

        var originalIndexMap = new Dictionary<int, int>(vertexCount);

        int AddOriginalVertex(int originalIndex, bool forceInclude = false)
        {
            if (originalIndexMap.TryGetValue(originalIndex, out int existing))
                return existing;

            double x = vertices[originalIndex * 3];
            double y = vertices[originalIndex * 3 + 1];
            if (!forceInclude && IsInsideControlledRegion(x, y, controlledLoops))
                return -1;

            int near = vertHash.FindNearest(xyList, x, y, dedupTol);
            if (near >= 0)
            {
                originalIndexMap.Add(originalIndex, near);
                return near;
            }

            int idx = zList.Count;
            xyList.Add(x);
            xyList.Add(y);
            zList.Add(vertices[originalIndex * 3 + 2]);
            vertHash.Insert(idx, x, y);
            originalIndexMap.Add(originalIndex, idx);
            return idx;
        }

        for (int i = 0; i < vertexCount; i++)
            AddOriginalVertex(i);

        int AddVertex(double x, double y)
        {
            coincidenceSnapper.SnapPoint(x, y, out x, out y);
            int near = vertHash.FindNearest(xyList, x, y, dedupTol);
            if (near >= 0)
                return near;

            int idx = zList.Count;
            xyList.Add(x);
            xyList.Add(y);
            zList.Add(TerrainFaceGrid.InterpolateZ(x, y));
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
                int mappedA = AddOriginalVertex(a, forceInclude: true);
                int mappedB = AddOriginalVertex(b, forceInclude: true);
                if (mappedA >= 0 && mappedB >= 0 && mappedA != mappedB)
                    segList.Add((mappedA, mappedB));
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
            double[] shoulderDistances = ComputePadBoundaryDistances(pad.XyVertices, pad.VertexCount, TerrainFaceGrid, pad);
            double shoulderDistance = 0; foreach (double d in shoulderDistances) if (d > shoulderDistance) shoulderDistance = d;
            double segmentLength = ComputePadConstraintSegmentLength(shoulderDistance);
            var padLoop = BuildClosedConstraintLoop(pad.XyVertices, pad.VertexCount, segmentLength, dedupTol);
            // Re-sample distances at padLoop resolution (which may have more vertices than the original pad)
            shoulderDistances = ComputePadBoundaryDistances(padLoop.XyVertices, padLoop.VertexCount, TerrainFaceGrid, pad);
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
                TerrainFaceGrid,
                segList,
                dedupTol,
                hasBoundaryLoop ? boundaryLoop : null,
                hasBoundaryLoop ? boundaryVertexCount : 0,
                padBarriers,
                padBarrierScratch,
                padBarrierCandidates);
            if (shoulderXy != null &&
                TryBuildProtectedStitchLoop(
                    shoulderXy,
                    pad.StitchApronDistance,
                    hasBoundaryLoop ? boundaryLoop : null,
                    hasBoundaryLoop ? boundaryVertexCount : 0,
                    dedupTol,
                    out double[] stitchXy,
                    out _))
            {
                AddClosedLoopSegments(stitchXy, stitchXy.Length / 2, AddVertex, segList);
            }
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
                    coincidenceSnapper.SnapPoint(lx, ly, out lx, out ly);

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
                        zList.Add(TerrainFaceGrid.InterpolateZ(lx, ly));
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
                : TerrainFaceGrid.InterpolateZ(x, y);

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
}
