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
        LockCurve[]? lockCurves = null,
        bool useNearestShoulderCandidate = false)
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
            tolerance: 1e-3,
            useNearestShoulderCandidate: useNearestShoulderCandidate);
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
            defaultCornerFanSegments,
            useNearestShoulderCandidate: true);
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
        var guidePolylines = new List<SurfaceRemesher.ConstraintPolyline>();
        var diagnostics = new GradingDiagnosticCollector();
        double suggestedEdgeLength = double.MaxValue;
        bool useCoupledProtectedUnionLoops =
            pads.Length > 1 &&
            lockCurves.Length == 0 &&
            !includeTransitionStationConstraints &&
            pads.Any(pad => pad.StitchApronDistance > dedupTol * 4.0);
        double[] padInfluenceDistances = new double[pads.Length];
        for (int i = 0; i < pads.Length; i++)
            padInfluenceDistances[i] = ComputePadTransitionDistance(vertices, vertexCount, pads[i]);
        var coupledShoulderLoops = new List<double[]>();
        var coupledStitchLoops = new List<double[]>();
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
            double segmentLength = ComputePadConstraintSegmentLength(shoulderDistance, dedupTol);
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

                if (useCoupledProtectedUnionLoops)
                {
                    coupledShoulderLoops.Add(shoulderXy);
                }
                else
                {
                    constraints.Add(new SurfaceRemesher.ConstraintPolyline(
                        CreateConstraintPoints(shoulderXy, shoulderVertexCount),
                        shoulderVertexCount,
                        IsClosed: true,
                        PreserveInputElevation: false));
                }
                if (includeTransitionStationConstraints)
                {
                    AddPadTransitionStationConstraints(
                        constraints,
                        padIndex,
                        pads,
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
                    if (useCoupledProtectedUnionLoops)
                    {
                        coupledStitchLoops.Add(stitchXy);
                    }
                    else
                    {
                        constraints.Add(new SurfaceRemesher.ConstraintPolyline(
                            CreateConstraintPoints(stitchXy, stitchVertexCount),
                            stitchVertexCount,
                            IsClosed: true,
                            PreserveInputElevation: false));
                        suggestedEdgeLength = UpdateSuggestedEdgeLength(suggestedEdgeLength, stitchXy, stitchVertexCount, stride: 2, isClosed: true);
                    }
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

        if (useCoupledProtectedUnionLoops)
        {
            AddUnionedCoupledPadLoops(
                constraints,
                diagnostics,
                "grade_pad.coupled.shoulder_union",
                "Grade Pad coupled protected shoulder loops unioned into shared topology constraints.",
                coupledShoulderLoops,
                dedupTol,
                ref suggestedEdgeLength);
            AddUnionedCoupledPadLoops(
                constraints,
                diagnostics,
                "grade_pad.coupled.stitch_union",
                "Grade Pad coupled protected stitch loops unioned into shared topology constraints.",
                coupledStitchLoops,
                dedupTol,
                ref suggestedEdgeLength);
            if (guidePolylines.Count > 0)
            {
                diagnostics.AddInformation(
                    "grade_pad.coupled.soft_guides",
                    $"Grade Pad retained {guidePolylines.Count:N0} per-pad shoulder loop(s) as soft topology guide seeds.",
                    operation: "Grade Pad");
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
            GuidePolylines = guidePolylines.ToArray(),
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
        int padIndex,
        PadBoundary[] pads,
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

        int maxStationCount = pads.Length > 1 ? 64 : 128;
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
            if (pads.Length > 1 &&
                StationCrossesOtherPadTop(ax, ay, bx, by, padIndex, pads, tolerance))
            {
                continue;
            }

            constraints.Add(new SurfaceRemesher.ConstraintPolyline(
                new[] { ax, ay, 0.0, bx, by, 0.0 },
                PointCount: 2,
                IsClosed: false,
                PreserveInputElevation: false));
        }
    }

    private static void AddUnionedCoupledPadLoops(
        List<SurfaceRemesher.ConstraintPolyline> constraints,
        GradingDiagnosticCollector diagnostics,
        string diagnosticCode,
        string diagnosticMessage,
        IReadOnlyList<double[]> loops,
        double tolerance,
        ref double suggestedEdgeLength)
    {
        if (loops.Count == 0)
            return;

        if (!ClipperGeometry.TryUnionClosedLoops(loops, tolerance, out List<double[]> unionLoops))
        {
            diagnostics.AddWarning(
                diagnosticCode + ".failed",
                diagnosticMessage + " Union failed; falling back to independent loops.",
                operation: "Grade Pad");
            foreach (double[] loop in loops)
                AddClosedConstraintLoop(constraints, loop, tolerance, ref suggestedEdgeLength);
            return;
        }

        foreach (double[] loop in unionLoops)
            AddClosedConstraintLoop(constraints, loop, tolerance, ref suggestedEdgeLength);

        diagnostics.AddInformation(
            diagnosticCode,
            diagnosticMessage,
            operation: "Grade Pad");
    }

    private static void AddClosedConstraintLoop(
        List<SurfaceRemesher.ConstraintPolyline> constraints,
        double[] loopXy,
        double tolerance,
        ref double suggestedEdgeLength)
    {
        int vertexCount = loopXy.Length / 2;
        if (vertexCount < 3)
            return;

        constraints.Add(new SurfaceRemesher.ConstraintPolyline(
            CreateConstraintPoints(loopXy, vertexCount),
            vertexCount,
            IsClosed: true,
            PreserveInputElevation: false));
        suggestedEdgeLength = UpdateSuggestedEdgeLength(suggestedEdgeLength, loopXy, vertexCount, stride: 2, isClosed: true);
    }
    private static bool StationCrossesOtherPadTop(
        double ax,
        double ay,
        double bx,
        double by,
        int padIndex,
        PadBoundary[] pads,
        double tolerance)
    {
        for (int otherIndex = 0; otherIndex < pads.Length; otherIndex++)
        {
            if (otherIndex == padIndex)
                continue;

            PadBoundary other = pads[otherIndex];
            if (other.VertexCount < 3)
                continue;

            if (SegmentEndpointOrMidpointInsidePad(ax, ay, bx, by, other))
                return true;

            for (int i = 0; i < other.VertexCount; i++)
            {
                int next = (i + 1) % other.VertexCount;
                double cx = other.XyVertices[i * 2];
                double cy = other.XyVertices[i * 2 + 1];
                double dx = other.XyVertices[next * 2];
                double dy = other.XyVertices[next * 2 + 1];
                if (SegmentsIntersectExcludingSharedEndpoints(ax, ay, bx, by, cx, cy, dx, dy, tolerance))
                    return true;
            }
        }

        return false;
    }

    private static bool SegmentEndpointOrMidpointInsidePad(
        double ax,
        double ay,
        double bx,
        double by,
        PadBoundary pad)
    {
        double mx = (ax + bx) * 0.5;
        double my = (ay + by) * 0.5;
        return PointInPolygon(ax, ay, pad.XyVertices, pad.VertexCount) ||
               PointInPolygon(bx, by, pad.XyVertices, pad.VertexCount) ||
               PointInPolygon(mx, my, pad.XyVertices, pad.VertexCount);
    }

    private static bool SegmentsIntersectExcludingSharedEndpoints(
        double ax,
        double ay,
        double bx,
        double by,
        double cx,
        double cy,
        double dx,
        double dy,
        double tolerance)
    {
        if (DistanceSquaredXY(ax, ay, cx, cy) <= tolerance * tolerance ||
            DistanceSquaredXY(ax, ay, dx, dy) <= tolerance * tolerance ||
            DistanceSquaredXY(bx, by, cx, cy) <= tolerance * tolerance ||
            DistanceSquaredXY(bx, by, dx, dy) <= tolerance * tolerance)
        {
            return false;
        }

        double o1 = Orientation(ax, ay, bx, by, cx, cy);
        double o2 = Orientation(ax, ay, bx, by, dx, dy);
        double o3 = Orientation(cx, cy, dx, dy, ax, ay);
        double o4 = Orientation(cx, cy, dx, dy, bx, by);
        double areaTolerance = Math.Max(tolerance, 1e-9) *
            Math.Max(Math.Sqrt(DistanceSquaredXY(ax, ay, bx, by)), Math.Sqrt(DistanceSquaredXY(cx, cy, dx, dy)));

        if (Math.Abs(o1) <= areaTolerance && PointOnSegment(cx, cy, ax, ay, bx, by, tolerance))
            return true;
        if (Math.Abs(o2) <= areaTolerance && PointOnSegment(dx, dy, ax, ay, bx, by, tolerance))
            return true;
        if (Math.Abs(o3) <= areaTolerance && PointOnSegment(ax, ay, cx, cy, dx, dy, tolerance))
            return true;
        if (Math.Abs(o4) <= areaTolerance && PointOnSegment(bx, by, cx, cy, dx, dy, tolerance))
            return true;

        return (o1 > 0.0) != (o2 > 0.0) &&
               (o3 > 0.0) != (o4 > 0.0);
    }

    private static bool PointOnSegment(
        double px,
        double py,
        double ax,
        double ay,
        double bx,
        double by,
        double tolerance)
    {
        if (px < Math.Min(ax, bx) - tolerance || px > Math.Max(ax, bx) + tolerance ||
            py < Math.Min(ay, by) - tolerance || py > Math.Max(ay, by) + tolerance)
        {
            return false;
        }

        double segmentLength = Math.Sqrt(DistanceSquaredXY(ax, ay, bx, by));
        if (segmentLength <= tolerance)
            return DistanceSquaredXY(px, py, ax, ay) <= tolerance * tolerance;

        return Math.Abs(Orientation(ax, ay, bx, by, px, py)) <= tolerance * segmentLength;
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
