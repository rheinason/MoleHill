using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

public static partial class PadGrader
{
    public static double[] ApplyGradingZ(
        double[] topologyVertices,
        int vertexCount,
        PadBoundary[] pads,
        LockCurve[]? lockCurves = null)
    {
        lockCurves ??= Array.Empty<LockCurve>();
        ValidateApplyGradingZInputs(topologyVertices, vertexCount, pads, lockCurves);

        if (pads.Length == 0)
            return (double[])topologyVertices.Clone();

        pads = OrderPadsForOwnership(pads);

        PreparedBarriers barriers = lockCurves.Length > 0
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
        lockCurves ??= Array.Empty<LockCurve>();
        ValidateApplyGradingZInputs(topologyVertices, vertexCount, faces, faceCount, pads, lockCurves);

        if (pads.Length == 0)
            return (double[])topologyVertices.Clone();

        pads = OrderPadsForOwnership(pads);

        PreparedBarriers barriers = lockCurves.Length > 0
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
        ValidateApplyGradingZInputs(topologyVertices, vertexCount, pads, Array.Empty<LockCurve>());

        if (pads.Length == 0)
            return (double[])topologyVertices.Clone();

        pads = OrderPadsForOwnership(pads);

        var gradedVertices = (double[])topologyVertices.Clone();
        ApplyGradingToVertices(gradedVertices, topologyVertices, vertexCount, pads, barriers);
        return gradedVertices;
    }

    private static double[] ApplyGradingZWithDefaultCornerFans(
        double[] topologyVertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PadBoundary[] pads,
        LockCurve[]? lockCurves,
        int defaultCornerFanSegments)
    {
        lockCurves ??= Array.Empty<LockCurve>();
        ValidateApplyGradingZInputs(topologyVertices, vertexCount, faces, faceCount, pads, lockCurves);

        if (pads.Length == 0)
            return (double[])topologyVertices.Clone();

        pads = OrderPadsForOwnership(pads);
        PreparedBarriers barriers = lockCurves.Length > 0
            ? GradingBarriers.BuildFromLockCurves(lockCurves)
            : PreparedBarriers.Empty;
        bool hasBoundaryLoop = TryBuildBoundaryLoop(topologyVertices, faces, faceCount, out double[] boundaryLoop, out int boundaryVertexCount);
        var terrainFaceGrid = new TerrainFaceGrid(topologyVertices, vertexCount, faces, faceCount);
        var gradedVertices = (double[])topologyVertices.Clone();
        ApplyGradingToVerticesWithSections(
            gradedVertices,
            topologyVertices,
            vertexCount,
            pads,
            barriers,
            terrainFaceGrid,
            hasBoundaryLoop,
            boundaryLoop,
            boundaryVertexCount,
            tolerance: 1e-3,
            keepShoulderOnBatterPlane: false,
            defaultCornerFanSegments);
        return gradedVertices;
    }

    public static ConstraintSet CreateConstraints(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PadBoundary[] pads,
        LockCurve[]? lockCurves,
        double modelTolerance = GradingTolerances.DefaultModelTolerance,
        bool includeTransitionStationConstraints = false)
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

        if (!GradingInputValidator.ValidateLockCurves(lockCurves, out string? lockCurveError))
        {
            GradingDiagnostic diagnostic = GradingDiagnostic.Warning(
                "grade_pad.input.invalid_lock_curve",
                lockCurveError ?? "Invalid lock curve.",
                operation: "grade_pad");
            return new ConstraintSet
            {
                Constraints = Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
                SuggestedEdgeLength = 0.0,
                Diagnostics = [diagnostic.Message],
                StructuredDiagnostics = [diagnostic]
            };
        }

        if (!ValidatePads(pads, out string? padError))
        {
            GradingDiagnostic diagnostic = GradingDiagnostic.Warning(
                "grade_pad.input.invalid_pad",
                padError ?? "Invalid pad boundary.",
                operation: "grade_pad");
            return new ConstraintSet
            {
                Constraints = Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
                SuggestedEdgeLength = 0.0,
                Diagnostics = [diagnostic.Message],
                StructuredDiagnostics = [diagnostic]
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
            double[] transitionBoundaryXy = padLoop.XyVertices;
            int transitionBoundaryVertexCount = padLoop.VertexCount;
            bool hasShoulderLoop = false;
            string? skipReason = null;
            double[] shoulderXy = Array.Empty<double>();

            if (includeTransitionStationConstraints &&
                lockCurves.Length == 0 &&
                TryBuildExpandedOffsetPolygon(
                    padLoop.XyVertices,
                    padLoop.VertexCount,
                    shoulderDistances,
                    pad.CornerFanSegments > 0 ? pad.CornerFanSegments : 6,
                    faceGridForConstraints,
                    pad,
                    out double[] fanBoundaryXy,
                    out double[] fanShoulderXy,
                    out string? fanSkipReason))
            {
                transitionBoundaryXy = fanBoundaryXy;
                transitionBoundaryVertexCount = fanBoundaryXy.Length / 2;
                shoulderXy = fanShoulderXy;
                hasShoulderLoop = true;
            }
            else
            {
                hasShoulderLoop = TryBuildShoulderLoop(
                    padLoop.XyVertices,
                    padLoop.VertexCount,
                    shoulderDistances,
                    hasBoundaryLoop ? boundaryLoop : null,
                    hasBoundaryLoop ? boundaryVertexCount : 0,
                    dedupTol,
                    out shoulderXy,
                    out skipReason);
            }

            if (hasShoulderLoop)
            {
                int shoulderVertexCount = shoulderXy.Length / 2;
                if (includeTransitionStationConstraints &&
                    (transitionBoundaryVertexCount != padLoop.VertexCount ||
                    !ReferenceEquals(transitionBoundaryXy, padLoop.XyVertices))
                    )
                {
                    constraints.Add(new SurfaceRemesher.ConstraintPolyline(
                        CreateConstraintPoints(transitionBoundaryXy, transitionBoundaryVertexCount),
                        transitionBoundaryVertexCount,
                        IsClosed: true,
                        PreserveInputElevation: false));
                }

                constraints.Add(new SurfaceRemesher.ConstraintPolyline(
                    CreateConstraintPoints(shoulderXy, shoulderVertexCount),
                    shoulderVertexCount,
                    IsClosed: true,
                    PreserveInputElevation: false));
                if (includeTransitionStationConstraints)
                {
                    AddPadTransitionStationConstraints(
                        constraints,
                        transitionBoundaryXy,
                        transitionBoundaryVertexCount,
                        shoulderXy,
                        shoulderVertexCount,
                        dedupTol);
                }
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

    private static void ValidateApplyGradingZInputs(
        double[] topologyVertices,
        int vertexCount,
        PadBoundary[] pads,
        IReadOnlyList<LockCurve> lockCurves)
    {
        if (!GradingInputValidator.ValidateVertexArray(topologyVertices, vertexCount, "Topology", out string? errorMessage))
            throw new ArgumentException(errorMessage, nameof(topologyVertices));

        ValidatePadAndLockInputs(pads, lockCurves);
    }

    private static void AddPadTransitionStationConstraints(
        List<SurfaceRemesher.ConstraintPolyline> constraints,
        double[] padLoopXy,
        int padLoopVertexCount,
        double[] shoulderXy,
        int shoulderVertexCount,
        double tolerance)
    {
        if (padLoopVertexCount < 3 ||
            shoulderVertexCount != padLoopVertexCount ||
            padLoopXy.Length < padLoopVertexCount * 2 ||
            shoulderXy.Length < shoulderVertexCount * 2)
        {
            return;
        }

        int maxStationCount = 256;
        int stride = Math.Max(1, (int)Math.Ceiling(padLoopVertexCount / (double)maxStationCount));
        for (int i = 0; i < padLoopVertexCount; i += stride)
        {
            double ax = padLoopXy[i * 2];
            double ay = padLoopXy[i * 2 + 1];
            double bx = shoulderXy[i * 2];
            double by = shoulderXy[i * 2 + 1];
            double dx = bx - ax;
            double dy = by - ay;
            if ((dx * dx) + (dy * dy) <= tolerance * tolerance)
                continue;

            constraints.Add(new SurfaceRemesher.ConstraintPolyline(
                new[] { ax, ay, 0.0, bx, by, 0.0 },
                PointCount: 2,
                IsClosed: false,
                PreserveInputElevation: false));
        }
    }

    private static void ValidateApplyGradingZInputs(
        double[] topologyVertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PadBoundary[] pads,
        IReadOnlyList<LockCurve> lockCurves)
    {
        if (!GradingInputValidator.ValidateTerrainMesh(topologyVertices, vertexCount, faces, faceCount, out string? errorMessage))
            throw new ArgumentException(errorMessage, nameof(topologyVertices));

        ValidatePadAndLockInputs(pads, lockCurves);
    }

    private static void ValidatePadAndLockInputs(
        PadBoundary[] pads,
        IReadOnlyList<LockCurve> lockCurves)
    {
        if (!GradingInputValidator.ValidatePadBoundaries(pads, out string? errorMessage, requireAny: false))
            throw new ArgumentException(errorMessage, nameof(pads));

        if (!GradingInputValidator.ValidateLockCurves(lockCurves, out errorMessage))
            throw new ArgumentException(errorMessage, nameof(lockCurves));
    }

    private static bool ValidatePads(PadBoundary[] pads, out string? errorMessage)
    {
        return GradingInputValidator.ValidatePadBoundaries(pads, out errorMessage);
    }


}
