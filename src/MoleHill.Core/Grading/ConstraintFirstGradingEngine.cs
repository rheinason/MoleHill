using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

internal static class ConstraintFirstGradingEngine
{
    private const double TargetFaceMultiplier = 2.5;
    private const double WarningFaceMultiplier = 4.0;
    private const double HardFaceMultiplier = 8.0;
    private const int DensityGuardCoarseRetryLimit = 3;

    private readonly record struct PreservedConstraintSegment(
        double Ax,
        double Ay,
        double Az,
        double Bx,
        double By,
        double Bz,
        int ConstraintIndex,
        int SegmentIndex);

    private readonly record struct PreservedConstraintSnapStats(
        int PreservedConstraintCount,
        int PreservedSegmentCount,
        int SnappedVertexCount);

    public delegate double[] ApplyGradingDelegate(double[] topologyVertices, int vertexCount, int[] faces, int faceCount);

    public delegate void AppendOutputDiagnosticsDelegate(
        double[] topologyVertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        double[] gradedVertices,
        List<string> diagnostics,
        List<GradingDiagnostic> structuredDiagnostics);

    public static GradingResult? TryBuild(
        string operation,
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> constraints,
        double requestedEdgeLength,
        double tolerance,
        ApplyGradingDelegate applyGrading,
        IReadOnlyList<OutputPolyline>? outputPolylines,
        IReadOnlyList<GradingPatch>? patchSummaries,
        IReadOnlyList<string>? preDiagnostics,
        IReadOnlyList<GradingDiagnostic>? preStructuredDiagnostics,
        AppendOutputDiagnosticsDelegate? appendOutputDiagnostics,
        out IReadOnlyList<GradingDiagnostic> failureDiagnostics,
        out string? errorMessage)
    {
        failureDiagnostics = Array.Empty<GradingDiagnostic>();
        errorMessage = null;

        double effectiveTolerance = GradingTolerances.ModelToleranceOrDefault(tolerance);
        double effectiveEdgeLength = ComputeEffectiveEdgeLength(vertices, vertexCount, requestedEdgeLength, effectiveTolerance);
        var diagnostics = new List<string>();
        if (preDiagnostics != null)
            diagnostics.AddRange(preDiagnostics.Where(static message => !string.IsNullOrWhiteSpace(message)));

        var structuredDiagnostics = new List<GradingDiagnostic>();
        if (preStructuredDiagnostics != null)
            structuredDiagnostics.AddRange(preStructuredDiagnostics);

        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> topologyConstraints =
            ConstraintNetworkNormalizer.SplitAtIntersections(
                constraints,
                effectiveTolerance,
                out int normalizedSplitCount);
        if (normalizedSplitCount > 0)
        {
            string normalizationMessage =
                $"{operation} constraint network normalized with {normalizedSplitCount:N0} intersection split(s).";
            diagnostics.Add(normalizationMessage);
            structuredDiagnostics.Add(GradingDiagnostic.Information(
                $"{DiagnosticPrefix(operation)}.constraint_network.normalized",
                normalizationMessage,
                operation: DiagnosticOperation(operation)));
        }

        bool hasOpenOrPreservedConstraints = topologyConstraints.Any(static constraint =>
            constraint.PreserveInputElevation || !constraint.IsClosed);

        SurfaceRemesher.Result remesh = Remesh(
            vertices,
            faces,
            topologyConstraints,
            effectiveTolerance,
            effectiveEdgeLength,
            preferReducedInteriorSeed: true,
            addReducedInteriorGuideSeeds: !hasOpenOrPreservedConstraints);

        if (!remesh.Success && effectiveEdgeLength > effectiveTolerance * 16.0)
        {
            double coarseEdgeLength = effectiveEdgeLength * 2.0;
            SurfaceRemesher.Result coarseRemesh = Remesh(
                vertices,
                faces,
                topologyConstraints,
                effectiveTolerance,
                coarseEdgeLength,
                preferReducedInteriorSeed: true,
                addReducedInteriorGuideSeeds: false);

            if (coarseRemesh.Success)
            {
                string coarseRetryMessage =
                    $"{operation} constraint-first rebuild retried with coarser constraint spacing ({effectiveEdgeLength:G4} -> {coarseEdgeLength:G4}).";
                diagnostics.Add(coarseRetryMessage);
                structuredDiagnostics.Add(GradingDiagnostic.Information(
                    $"{DiagnosticPrefix(operation)}.topology.coarse_retry",
                    coarseRetryMessage,
                    operation: DiagnosticOperation(operation)));
                remesh = coarseRemesh;
                effectiveEdgeLength = coarseEdgeLength;
            }
        }

        if (!remesh.Success)
        {
            errorMessage = remesh.Warning ?? $"{operation} constraint-first topology rebuild failed.";
            AddFailureDiagnostic(operation, errorMessage, structuredDiagnostics, out failureDiagnostics);
            return null;
        }

        int topologyVertexCount = remesh.Vertices.Length / 3;
        int topologyFaceCount = remesh.Faces.Length / 3;
        double densityMultiplier = faceCount > 0
            ? topologyFaceCount / (double)faceCount
            : 1.0;
        int hardFaceBudget = Math.Max((int)Math.Ceiling(faceCount * HardFaceMultiplier), faceCount + 25_000);

        if (densityMultiplier > WarningFaceMultiplier || topologyFaceCount > hardFaceBudget)
        {
            double initialDensityMultiplier = densityMultiplier;
            int initialTopologyFaceCount = topologyFaceCount;
            double guardEdgeLength = effectiveEdgeLength;

            for (int attempt = 0;
                 attempt < DensityGuardCoarseRetryLimit &&
                 (densityMultiplier > WarningFaceMultiplier || topologyFaceCount > hardFaceBudget);
                 attempt++)
            {
                guardEdgeLength *= 2.0;
                SurfaceRemesher.Result coarseRemesh = Remesh(
                    vertices,
                    faces,
                    topologyConstraints,
                    effectiveTolerance,
                    guardEdgeLength,
                    preferReducedInteriorSeed: true,
                    addReducedInteriorGuideSeeds: false);

                if (!coarseRemesh.Success || coarseRemesh.Faces.Length >= remesh.Faces.Length)
                    break;

                remesh = coarseRemesh;
                topologyVertexCount = remesh.Vertices.Length / 3;
                topologyFaceCount = remesh.Faces.Length / 3;
                densityMultiplier = faceCount > 0
                    ? topologyFaceCount / (double)faceCount
                    : 1.0;
                effectiveEdgeLength = guardEdgeLength;
            }

            if (topologyFaceCount < initialTopologyFaceCount)
            {
                string densityGuardMessage =
                    $"{operation} density guard selected coarser valid topology ({initialDensityMultiplier:0.##}x -> {densityMultiplier:0.##}x input faces).";
                diagnostics.Add(densityGuardMessage);
                structuredDiagnostics.Add(GradingDiagnostic.Information(
                    $"{DiagnosticPrefix(operation)}.density_guard.coarse_retry",
                    densityGuardMessage,
                    operation: DiagnosticOperation(operation)));
            }
        }

        if (topologyFaceCount > hardFaceBudget)
        {
            errorMessage = $"{operation} constraint-first topology exceeded density budget ({topologyFaceCount:N0} faces; budget {hardFaceBudget:N0}).";
            AddFailureDiagnostic(operation, errorMessage, structuredDiagnostics, out failureDiagnostics);
            return null;
        }

        var topology = MeshTopologyValidator.AnalyzeBoundaryGraph(remesh.Faces, topologyFaceCount);
        if (!topology.HasSingleClosedBoundaryLoop)
        {
            errorMessage =
                $"{operation} constraint-first topology rejected: boundary edges={topology.BoundaryEdgeCount:N0}, boundary components={topology.BoundaryComponentCount:N0}, open chains={topology.HasOpenBoundaryChains}, nonmanifold edges={topology.NonManifoldEdgeCount:N0}.";
            AddFailureDiagnostic(operation, errorMessage, structuredDiagnostics, out failureDiagnostics);
            return null;
        }

        double[] gradedVertices;
        try
        {
            gradedVertices = applyGrading(remesh.Vertices, topologyVertexCount, remesh.Faces, topologyFaceCount);
        }
        catch (Exception ex)
        {
            errorMessage = $"{operation} Z evaluation failed: {ex.Message}";
            AddFailureDiagnostic(operation, errorMessage, structuredDiagnostics, out failureDiagnostics);
            return null;
        }

        if (gradedVertices.Length < topologyVertexCount * 3)
        {
            errorMessage = $"{operation} Z evaluation returned too few vertex values.";
            AddFailureDiagnostic(operation, errorMessage, structuredDiagnostics, out failureDiagnostics);
            return null;
        }

        PreservedConstraintSnapStats preservedElevationStats =
            ApplyPreservedConstraintElevations(gradedVertices, topologyVertexCount, constraints, effectiveTolerance);
        if (preservedElevationStats.PreservedConstraintCount > 0)
        {
            string preservedElevationMessage =
                $"{operation} preserved-elevation constraints snapped {preservedElevationStats.SnappedVertexCount:N0} output vertex/vertices from {preservedElevationStats.PreservedSegmentCount:N0} segment(s) across {preservedElevationStats.PreservedConstraintCount:N0} constraint(s).";
            diagnostics.Add(preservedElevationMessage);
            structuredDiagnostics.Add(GradingDiagnostic.Information(
                $"{DiagnosticPrefix(operation)}.preserved_elevation.snap",
                preservedElevationMessage,
                operation: DiagnosticOperation(operation)));
        }

        diagnostics.Add($"{operation} constraint-first topology ({vertexCount:N0} verts/{faceCount:N0} faces -> {topologyVertexCount:N0} verts/{topologyFaceCount:N0} faces; density {densityMultiplier:0.##}x).");
        GradingDiagnostic topologySummary = GradingTopologyDiagnostics.BuildMeshSummary(
            $"{DiagnosticPrefix(operation)}.topology.summary",
            operation,
            vertexCount,
            faceCount,
            topologyVertexCount,
            topologyFaceCount,
            remesh.Faces,
            operation: DiagnosticOperation(operation));
        diagnostics.Add(topologySummary.Message);
        structuredDiagnostics.Add(topologySummary);
        appendOutputDiagnostics?.Invoke(
            remesh.Vertices,
            topologyVertexCount,
            remesh.Faces,
            topologyFaceCount,
            gradedVertices,
            diagnostics,
            structuredDiagnostics);
        if (densityMultiplier > WarningFaceMultiplier)
        {
            diagnostics.Add($"{operation} density warning: output face count is {densityMultiplier:0.##}x the input face count.");
            structuredDiagnostics.Add(GradingDiagnostic.Warning(
                $"{DiagnosticPrefix(operation)}.density.high",
                $"{operation} density warning: output face count is {densityMultiplier:0.##}x the input face count.",
                operation: DiagnosticOperation(operation)));
        }
        else if (densityMultiplier > TargetFaceMultiplier)
        {
            string densityNoteMessage = $"{operation} density note: output face count is {densityMultiplier:0.##}x the input face count.";
            diagnostics.Add(densityNoteMessage);
            structuredDiagnostics.Add(GradingDiagnostic.Information(
                $"{DiagnosticPrefix(operation)}.density.note",
                densityNoteMessage,
                operation: DiagnosticOperation(operation)));
        }

        if (remesh.UsedBoundaryAndGuideSeedFallback)
        {
            string seedFallbackMessage =
                $"{operation} used boundary, hard-constraint, and coarse guide seeds to avoid inherited topology over-refinement.";
            diagnostics.Add(seedFallbackMessage);
            structuredDiagnostics.Add(GradingDiagnostic.Information(
                $"{DiagnosticPrefix(operation)}.topology.seed_fallback",
                seedFallbackMessage,
                operation: DiagnosticOperation(operation)));
        }
        if (!string.IsNullOrWhiteSpace(remesh.Warning))
            diagnostics.Add(remesh.Warning!);

        return GradingResultBuilder.BuildFromXyz(
            remesh.Vertices,
            gradedVertices,
            topologyVertexCount,
            remesh.Faces,
            topologyFaceCount,
            outputPolylines,
            diagnostics,
            patchSummaries,
            structuredDiagnostics);
    }

    private static SurfaceRemesher.Result Remesh(
        double[] vertices,
        int[] faces,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> constraints,
        double tolerance,
        double requestedEdgeLength,
        bool preferReducedInteriorSeed,
        bool addReducedInteriorGuideSeeds)
    {
        return SurfaceRemesher.Remesh(
            vertices,
            faces,
            constraints,
            new SurfaceRemesher.Options
            {
                Tolerance = tolerance,
                RequestedEdgeLength = requestedEdgeLength,
                MaxArea = 0.0,
                MinAngle = 0.0,
                ProtectSharpEdges = false,
                ConstraintInsertionOnly = true,
                PreferReducedInteriorSeed = preferReducedInteriorSeed,
                AddReducedInteriorGuideSeeds = addReducedInteriorGuideSeeds,
                AddConstraintCorridorSeeds = false
            });
    }

    private static double ComputeEffectiveEdgeLength(
        double[] vertices,
        int vertexCount,
        double requestedEdgeLength,
        double tolerance)
    {
        double floor = Math.Max(tolerance * 12.0, 1e-6);
        if (vertexCount <= 1 || vertices.Length < vertexCount * 3)
            return Math.Max(requestedEdgeLength, floor);

        double minX = double.PositiveInfinity;
        double minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double maxY = double.NegativeInfinity;
        for (int i = 0; i < vertexCount; i++)
        {
            double x = vertices[i * 3];
            double y = vertices[i * 3 + 1];
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        double diagonal = Math.Sqrt(((maxX - minX) * (maxX - minX)) + ((maxY - minY) * (maxY - minY)));
        if (!double.IsFinite(diagonal) || diagonal <= 0.0)
            return Math.Max(requestedEdgeLength, floor);

        double scaleFloor = Math.Max(floor, diagonal / 200.0);
        return Math.Max(requestedEdgeLength, scaleFloor);
    }

    private static PreservedConstraintSnapStats ApplyPreservedConstraintElevations(
        double[] vertices,
        int vertexCount,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> constraints,
        double tolerance)
    {
        if (constraints.Count == 0 || vertexCount == 0)
            return default;

        double snapTolerance = Math.Max(tolerance * 4.0, 1e-8);
        double snapToleranceSq = snapTolerance * snapTolerance;
        PreservedConstraintSegmentIndex? index = PreservedConstraintSegmentIndex.Build(constraints, snapTolerance);
        if (index == null)
            return default;

        int snappedVertexCount = 0;
        for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
        {
            double x = vertices[vertexIndex * 3];
            double y = vertices[vertexIndex * 3 + 1];
            if (index.TryGetElevation(x, y, snapToleranceSq, out double z))
            {
                vertices[vertexIndex * 3 + 2] = z;
                snappedVertexCount++;
            }
        }

        return new PreservedConstraintSnapStats(
            index.PreservedConstraintCount,
            index.PreservedSegmentCount,
            snappedVertexCount);
    }

    private sealed class PreservedConstraintSegmentIndex
    {
        private readonly PreservedConstraintSegment[] _segments;
        private readonly SpatialHashGrid2D _grid;
        private readonly SpatialHashGrid2D.QueryScratch _scratch;
        private readonly List<int> _candidates;

        public int PreservedConstraintCount { get; }

        public int PreservedSegmentCount => _segments.Length;

        private PreservedConstraintSegmentIndex(PreservedConstraintSegment[] segments, int preservedConstraintCount, SpatialHashGrid2D grid)
        {
            _segments = segments;
            PreservedConstraintCount = preservedConstraintCount;
            _grid = grid;
            _scratch = new SpatialHashGrid2D.QueryScratch(segments.Length);
            _candidates = new List<int>(Math.Min(segments.Length, 32));
        }

        public bool TryGetElevation(double x, double y, double maxDistanceSquared, out double z)
        {
            z = 0.0;
            _grid.GatherCandidates(Bounds2D.FromPoint(x, y), _candidates, _scratch);
            if (_candidates.Count == 0)
                return false;

            bool found = false;
            int bestConstraintIndex = -1;
            int bestSegmentIndex = int.MaxValue;
            foreach (int candidateIndex in _candidates)
            {
                PreservedConstraintSegment segment = _segments[candidateIndex];
                if (!TryProjectToSegment(
                        x,
                        y,
                        segment.Ax,
                        segment.Ay,
                        segment.Bx,
                        segment.By,
                        out double t,
                        out double distanceSq) ||
                    distanceSq > maxDistanceSquared)
                {
                    continue;
                }

                if (segment.ConstraintIndex < bestConstraintIndex ||
                    (segment.ConstraintIndex == bestConstraintIndex && segment.SegmentIndex >= bestSegmentIndex))
                {
                    continue;
                }

                bestConstraintIndex = segment.ConstraintIndex;
                bestSegmentIndex = segment.SegmentIndex;
                z = segment.Az + ((segment.Bz - segment.Az) * t);
                found = true;
            }

            return found;
        }

        public static PreservedConstraintSegmentIndex? Build(
            IReadOnlyList<SurfaceRemesher.ConstraintPolyline> constraints,
            double snapTolerance)
        {
            var segments = new List<PreservedConstraintSegment>();
            var bounds = new List<Bounds2D>();
            int preservedConstraintCount = 0;
            for (int constraintIndex = 0; constraintIndex < constraints.Count; constraintIndex++)
            {
                SurfaceRemesher.ConstraintPolyline constraint = constraints[constraintIndex];
                if (!constraint.PreserveInputElevation ||
                    constraint.PointCount < 2 ||
                    constraint.Points.Length < constraint.PointCount * 3)
                {
                    continue;
                }

                preservedConstraintCount++;
                int segmentCount = constraint.IsClosed ? constraint.PointCount : constraint.PointCount - 1;
                for (int segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
                {
                    int nextIndex = (segmentIndex + 1) % constraint.PointCount;
                    double ax = constraint.Points[segmentIndex * 3];
                    double ay = constraint.Points[(segmentIndex * 3) + 1];
                    double az = constraint.Points[(segmentIndex * 3) + 2];
                    double bx = constraint.Points[nextIndex * 3];
                    double by = constraint.Points[(nextIndex * 3) + 1];
                    double bz = constraint.Points[(nextIndex * 3) + 2];
                    if (!double.IsFinite(ax) ||
                        !double.IsFinite(ay) ||
                        !double.IsFinite(az) ||
                        !double.IsFinite(bx) ||
                        !double.IsFinite(by) ||
                        !double.IsFinite(bz))
                    {
                        continue;
                    }

                    segments.Add(new PreservedConstraintSegment(
                        ax,
                        ay,
                        az,
                        bx,
                        by,
                        bz,
                        constraintIndex,
                        segmentIndex));
                    bounds.Add(new Bounds2D(
                        Math.Min(ax, bx) - snapTolerance,
                        Math.Max(ax, bx) + snapTolerance,
                        Math.Min(ay, by) - snapTolerance,
                        Math.Max(ay, by) + snapTolerance));
                }
            }

            if (segments.Count == 0)
                return null;

            return new PreservedConstraintSegmentIndex(segments.ToArray(), preservedConstraintCount, SpatialHashGrid2D.Build(bounds.ToArray()));
        }
    }

    private static bool TryProjectToSegment(
        double px,
        double py,
        double ax,
        double ay,
        double bx,
        double by,
        out double t,
        out double distanceSq)
    {
        double dx = bx - ax;
        double dy = by - ay;
        double lenSq = (dx * dx) + (dy * dy);
        if (lenSq <= 1e-20)
        {
            t = 0.0;
            double vx = px - ax;
            double vy = py - ay;
            distanceSq = (vx * vx) + (vy * vy);
            return true;
        }

        t = (((px - ax) * dx) + ((py - ay) * dy)) / lenSq;
        if (t < -1e-9 || t > 1.0 + 1e-9)
        {
            distanceSq = double.PositiveInfinity;
            return false;
        }

        t = Math.Clamp(t, 0.0, 1.0);
        double qx = ax + (dx * t);
        double qy = ay + (dy * t);
        double ox = px - qx;
        double oy = py - qy;
        distanceSq = (ox * ox) + (oy * oy);
        return true;
    }

    private static void AddFailureDiagnostic(
        string operation,
        string message,
        List<GradingDiagnostic> diagnostics,
        out IReadOnlyList<GradingDiagnostic> failureDiagnostics)
    {
        diagnostics.Add(GradingDiagnostic.Warning(
            $"{DiagnosticPrefix(operation)}.constraint_first.failed",
            message,
            operation: DiagnosticOperation(operation)));
        failureDiagnostics = diagnostics.ToArray();
    }

    private static string DiagnosticPrefix(string operation)
    {
        return operation
            .Trim()
            .ToLowerInvariant()
            .Replace(' ', '_')
            .Replace('-', '_');
    }

    private static string DiagnosticOperation(string operation)
    {
        return string.Equals(operation, "Grade Pad", StringComparison.Ordinal)
            ? "grade_pad"
            : operation;
    }
}
