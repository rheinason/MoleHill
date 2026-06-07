using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

internal static class ConstraintFirstGradingEngine
{
    private const double TargetFaceMultiplier = 2.5;
    private const double WarningFaceMultiplier = 4.0;
    private const double HardFaceMultiplier = 8.0;
    private const int DensityGuardCoarseRetryLimit = 3;

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
        out string? errorMessage)
    {
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
            diagnostics.Add($"{operation} constraint network normalized with {normalizedSplitCount:N0} intersection split(s).");

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
                diagnostics.Add($"{operation} constraint-first rebuild retried with coarser constraint spacing ({effectiveEdgeLength:G4} -> {coarseEdgeLength:G4}).");
                remesh = coarseRemesh;
                effectiveEdgeLength = coarseEdgeLength;
            }
        }

        if (!remesh.Success)
        {
            errorMessage = remesh.Warning ?? $"{operation} constraint-first topology rebuild failed.";
            AddFailureDiagnostic(operation, errorMessage, structuredDiagnostics);
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
                diagnostics.Add($"{operation} density guard selected coarser valid topology ({initialDensityMultiplier:0.##}x -> {densityMultiplier:0.##}x input faces).");
        }

        if (topologyFaceCount > hardFaceBudget)
        {
            errorMessage = $"{operation} constraint-first topology exceeded density budget ({topologyFaceCount:N0} faces; budget {hardFaceBudget:N0}).";
            AddFailureDiagnostic(operation, errorMessage, structuredDiagnostics);
            return null;
        }

        var topology = MeshTopologyValidator.AnalyzeBoundaryGraph(remesh.Faces, topologyFaceCount);
        if (!topology.HasSingleClosedBoundaryLoop)
        {
            errorMessage =
                $"{operation} constraint-first topology rejected: boundary edges={topology.BoundaryEdgeCount:N0}, boundary components={topology.BoundaryComponentCount:N0}, open chains={topology.HasOpenBoundaryChains}, nonmanifold edges={topology.NonManifoldEdgeCount:N0}.";
            AddFailureDiagnostic(operation, errorMessage, structuredDiagnostics);
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
            AddFailureDiagnostic(operation, errorMessage, structuredDiagnostics);
            return null;
        }

        if (gradedVertices.Length < topologyVertexCount * 3)
        {
            errorMessage = $"{operation} Z evaluation returned too few vertex values.";
            AddFailureDiagnostic(operation, errorMessage, structuredDiagnostics);
            return null;
        }

        ApplyPreservedConstraintElevations(gradedVertices, topologyVertexCount, constraints, effectiveTolerance);

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
                operation: operation));
        }
        else if (densityMultiplier > TargetFaceMultiplier)
        {
            diagnostics.Add($"{operation} density note: output face count is {densityMultiplier:0.##}x the input face count.");
        }

        if (remesh.UsedBoundaryAndGuideSeedFallback)
            diagnostics.Add($"{operation} used boundary, hard-constraint, and coarse guide seeds to avoid inherited topology over-refinement.");
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

    private static void ApplyPreservedConstraintElevations(
        double[] vertices,
        int vertexCount,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> constraints,
        double tolerance)
    {
        double snapTolerance = Math.Max(tolerance * 4.0, 1e-8);
        double snapToleranceSq = snapTolerance * snapTolerance;
        foreach (SurfaceRemesher.ConstraintPolyline constraint in constraints)
        {
            if (!constraint.PreserveInputElevation ||
                constraint.PointCount < 2 ||
                constraint.Points.Length < constraint.PointCount * 3)
            {
                continue;
            }

            int segmentCount = constraint.IsClosed ? constraint.PointCount : constraint.PointCount - 1;
            for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
            {
                double x = vertices[vertexIndex * 3];
                double y = vertices[vertexIndex * 3 + 1];
                for (int segmentIndex = 0; segmentIndex < segmentCount; segmentIndex++)
                {
                    int nextIndex = (segmentIndex + 1) % constraint.PointCount;
                    double ax = constraint.Points[segmentIndex * 3];
                    double ay = constraint.Points[segmentIndex * 3 + 1];
                    double az = constraint.Points[segmentIndex * 3 + 2];
                    double bx = constraint.Points[nextIndex * 3];
                    double by = constraint.Points[nextIndex * 3 + 1];
                    double bz = constraint.Points[nextIndex * 3 + 2];
                    if (!TryProjectToSegment(x, y, ax, ay, bx, by, out double t, out double distanceSq) ||
                        distanceSq > snapToleranceSq)
                    {
                        continue;
                    }

                    vertices[vertexIndex * 3 + 2] = az + ((bz - az) * t);
                    break;
                }
            }
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

    private static void AddFailureDiagnostic(string operation, string message, List<GradingDiagnostic> diagnostics)
    {
        diagnostics.Add(GradingDiagnostic.Warning(
            $"{DiagnosticPrefix(operation)}.constraint_first.failed",
            message,
            operation: operation));
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
