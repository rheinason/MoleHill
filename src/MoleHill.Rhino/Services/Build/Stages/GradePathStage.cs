using System.Diagnostics;
using MoleHill.Core.Analysis;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Shared;
using Rhino.Geometry;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

internal static class GradePathStage
{
    internal static void Run(ModifierBuildContext c)
    {
        var gradePath = (GradePathModifierDefinition)c.Modifier;
        c.UsedStageKeys.Add(TerrainStageKey.CreateGradingTopology(c.StageKey, "Path"));
        RhinoMesh? input = c.CurrentMesh;
        c.CurrentMesh = TerrainBuildService.ExecuteCachedMeshStage(
            c.Build,
            c.RuntimeCache,
            c.StageKey,
            "Grade Path",
            TerrainBuildService.ComputeModifierStageFingerprint(c.Snapshot, c.Terrain, gradePath, c.CurrentMeshFingerprint),
            () => input == null ? TerrainBuildService.WarnMissingMesh(c.Build, gradePath.Label) : ApplyGradePath(c.Snapshot, c.Terrain, input, gradePath, c.Build, c.RuntimeCache, c.Index, c.StageKey, c.Mode),
            result => TerrainBuildService.DescribeModifierMeshResult(gradePath.Label, result),
            out ulong fingerprint,
            c.ShouldCancel);
        c.CurrentMeshFingerprint = fingerprint;
    }

    private static RhinoMesh ApplyGradePath(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        GradePathModifierDefinition modifier,
        TerrainBuildResult build,
        TerrainRuntimeCache runtimeCache,
        int modifierIndex,
        string stageKey,
        TerrainBuildMode mode)
    {
        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out int vertexCount, out var faces, out int faceCount, out var errorMessage))
        {
            build.Diagnostics.Add(errorMessage ?? "Could not extract mesh data for grade path.");
            return mesh;
        }

        if (modifier.Width <= 0)
        {
            build.Diagnostics.Add("Grade Path width must be positive.");
            return mesh;
        }

        TerrainTolerancePolicy.Profile toleranceProfile = TerrainBuildService.GetToleranceProfile(snapshot, terrain);
        double curveTolerance = toleranceProfile.CurveChordTolerance;
        double gradePathTolerance = toleranceProfile.GradePathTolerance;
        var pathResolveTimer = Stopwatch.StartNew();
        IReadOnlyList<ConstraintPolyline> pathBarriers = TerrainBuildService.UpstreamBreaklines(build, modifier.GradeThroughBreaklines);
        ResolvedGradePathInputs resolvedInputs = ResolveGradePathInputs(snapshot, vertices, vertexCount, faces, faceCount, modifier, pathBarriers, curveTolerance, gradePathTolerance);
        pathResolveTimer.Stop();
        build.RecordTiming(
            "Grade Path Resolve",
            pathResolveTimer.Elapsed,
            $"{resolvedInputs.Paths.Length:N0} paths; constraints {resolvedInputs.ConstraintElapsed.TotalSeconds:0.##} s of it",
            TerrainBuildService.StageTimingDiagnosticThresholdMs);
        build.RecordTiming(
            "Grade Path Constraints",
            resolvedInputs.ConstraintElapsed,
            $"{resolvedInputs.Constraints.Length:N0} constraints over {vertexCount:N0} verts",
            TerrainBuildService.StageTimingDiagnosticThresholdMs);
        foreach (VariablePathWidthResolver.Diagnostic diagnostic in resolvedInputs.WidthDiagnostics)
            build.Diagnostics.Add(diagnostic.Message);
        if (resolvedInputs.BarrierStops > 0)
        {
            build.Diagnostics.Add(
                $"Grade Path stopped at {resolvedInputs.BarrierStops:N0} crossing(s) of a hard constraint" +
                (resolvedInputs.PiecesLeftToConstraints > 0
                    ? $"; {resolvedInputs.PiecesLeftToConstraints:N0} piece(s) inside a graded area were left to it."
                    : "."));
        }
        AddGradePathWidthDiagnosticOverlays(mesh, modifier, resolvedInputs.WidthDiagnostics, gradePathTolerance, build);
        if (resolvedInputs.Paths.Length == 0)
        {
            build.Diagnostics.Add("Grade Path has no valid paths.");
            runtimeCache.GradingTopologyEntries[TerrainStageKey.CreateGradingTopology(stageKey, "Path")] =
                TerrainBuildService.BuildPathTopologySummary(
                    vertices,
                    vertexCount,
                    faces,
                    faceCount,
                    gradingResult: null,
                    Array.Empty<GradingPatch>(),
                    Array.Empty<string>());
            return mesh;
        }

        List<GradingPatch> patchSummaries = TerrainBuildService.BuildPathPatchSummaries(resolvedInputs.Paths);
        List<string> dirtyStageKeys = runtimeCache.FindIntersectingGradingStageKeys(
            TerrainRuntimeCache.GetStagePrefix(mode),
            terrain.Modifiers,
            modifierIndex,
            patchSummaries);
        if (dirtyStageKeys.Count > 0)
        {
            runtimeCache.InvalidateStages(dirtyStageKeys);
            build.Diagnostics.Add(
                $"Grade Path invalidated {dirtyStageKeys.Count} overlapping downstream grading stage(s): {string.Join(", ", dirtyStageKeys.Select(TerrainStageKey.GetBase))}.");
        }

        // A hard constraint elsewhere on the terrain (a distant Grade Pad boundary) says nothing about
        // whether this corridor's explicit assembly will be rejected, so only one that crosses or
        // overlaps the path's own constraints may reorder the tiers.
        bool hasInteractingHardConstraints = false;
        if (pathBarriers.Count > 0 && resolvedInputs.Constraints.Length > 0)
        {
            var conflictSummary = TerrainBuildService.AnalyzeHardConstraintConflicts(resolvedInputs.Constraints, pathBarriers, gradePathTolerance);
            hasInteractingHardConstraints = conflictSummary.HasConflicts;
            build.Diagnostics.Add(conflictSummary.CreateSummaryMessage());
            if (conflictSummary.CreateSampleMessage() is string sampleMessage)
                build.Diagnostics.Add(sampleMessage);
        }

        bool preferSplitKeep = TerrainBuildHeuristics.ShouldPreferSplitKeepGradePath(
            mode,
            hasInteractingHardConstraints,
            faceCount);
        string topologyStageKey = TerrainStageKey.CreateGradingTopology(stageKey, "Path");
        var coreTimer = Stopwatch.StartNew();
        GradingResult? gradingResult = GradePathsWindowed(
            vertices,
            vertexCount,
            faces,
            faceCount,
            resolvedInputs.Paths,
            pathBarriers,
            gradePathTolerance,
            preferSplitKeep,
            runtimeCache,
            stageKey,
            build.Diagnostics,
            out string? warning);
        coreTimer.Stop();
        runtimeCache.CoreCaseRecorder?.RecordPath(
            modifier.Label,
            vertices,
            vertexCount,
            faces,
            faceCount,
            resolvedInputs.Paths,
            pathBarriers,
            gradePathTolerance,
            preferSplitKeep,
            gradingResult != null,
            gradingResult?.VertexCount,
            gradingResult?.FaceCount,
            warning);

        if (gradingResult == null)
        {
            build.RecordTiming("Grade Path Core", coreTimer.Elapsed, "failed", TerrainBuildService.StageTimingDiagnosticThresholdMs);
            build.Diagnostics.Add(warning ?? "Grade Path failed.");
            runtimeCache.GradingTopologyEntries[topologyStageKey] = TerrainBuildService.BuildPathTopologySummary(
                vertices,
                vertexCount,
                faces,
                faceCount,
                gradingResult: null,
                patchSummaries,
                build.Diagnostics);
            return mesh;
        }

        build.RecordTiming(
            "Grade Path Core",
            coreTimer.Elapsed,
            TerrainBuildService.DescribeTopologyCounts(vertexCount, faceCount, gradingResult.VertexCount, gradingResult.FaceCount),
            TerrainBuildService.StageTimingDiagnosticThresholdMs);
        if (!string.IsNullOrWhiteSpace(warning))
            build.Diagnostics.Add(warning);
        build.AddGradingDiagnostics(gradingResult);

        if (modifier.GradeThroughBreaklines)
        {
            TerrainBuildService.DropRegradedBreaklines(
                build, gradingResult.Vertices, gradingResult.VertexCount, gradingResult.Faces, gradingResult.FaceCount,
                snapshot.ModelAbsoluteTolerance, snapshot.ModelUnitSystem, "Grade Path");
        }

        TerrainBuildService.AddOutputPolylinesAsBreaklines(gradingResult.OutputPolylines, build);
        AddPersistentElevationConstraints(build, resolvedInputs.Constraints);
        runtimeCache.GradingTopologyEntries[topologyStageKey] = TerrainBuildService.BuildPathTopologySummary(
            vertices,
            vertexCount,
            faces,
            faceCount,
            gradingResult,
            patchSummaries,
            build.Diagnostics);
        return TerrainBuildService.FinalizeGradingMesh(
            RhinoGeometryConversions.BuildMesh(gradingResult.Vertices, gradingResult.VertexCount, gradingResult.Faces, gradingResult.FaceCount),
            "Grade Path",
            build);
    }

    private static void AddPersistentElevationConstraints(
        TerrainBuildResult build,
        IReadOnlyList<ConstraintPolyline> constraints)
    {
        List<ConstraintPolyline> preservedConstraints = TerrainBuildService.CreatePreservedElevationConstraints(constraints);
        if (preservedConstraints.Count == 0)
            return;

        List<ConstraintPolyline> mergedConstraints = TerrainBuildService.CombineConstraints(build.PersistentElevationConstraints, preservedConstraints);
        build.PersistentElevationConstraints.Clear();
        build.PersistentElevationConstraints.AddRange(mergedConstraints);
    }

    private static void AddGradePathWidthDiagnosticOverlays(
        RhinoMesh mesh,
        GradePathModifierDefinition modifier,
        IReadOnlyList<VariablePathWidthResolver.Diagnostic> diagnostics,
        double tolerance,
        TerrainBuildResult build)
    {
        BoundingBox bounds = mesh.GetBoundingBox(true);
        for (int i = 0; i < diagnostics.Count; i++)
        {
            VariablePathWidthResolver.Diagnostic diagnostic = diagnostics[i];
            if (diagnostic.X is not double x || diagnostic.Y is not double y || !double.IsFinite(x) || !double.IsFinite(y))
                continue;

            var query = new Point3d(x, y, bounds.IsValid ? bounds.Center.Z : 0.0);
            Point3d anchor = TerrainMeshProjection.TryProjectPointAlongWorldZ(mesh, query, tolerance, out Point3d projected)
                ? projected
                : query;
            bool warning = diagnostic.Code is not "grade_path.variable_edge.matched";
            string shortLabel = diagnostic.Code switch
            {
                "grade_path.variable_edge.matched" => "Width edge matched",
                "grade_path.variable_edge.partial" => "Partial width edge",
                "grade_path.variable_edge.ambiguous_path" or "grade_path.variable_edge.ambiguous_side" => "Ambiguous width edge",
                _ => "Unmatched width edge"
            };
            build.RuntimeOverlays.Add(new RuntimeOverlayItem
            {
                StableId = $"grade-path-width:{modifier.Id:N}:{diagnostic.Code}:{diagnostic.EdgeSourceIndex?.ToString() ?? "x"}:{i}",
                Owner = new RuntimeOverlayOwner(RuntimeOverlayOwnerKind.Modifier, modifier.Id),
                Severity = warning ? RuntimeOverlaySeverity.Warning : RuntimeOverlaySeverity.Information,
                Code = diagnostic.Code,
                Message = diagnostic.Message,
                ShortLabel = shortLabel,
                Primitives = new List<RuntimeOverlayPrimitive>
                {
                    RuntimeOverlayPrimitive.Marker(anchor, size: 7),
                    RuntimeOverlayPrimitive.Dot(anchor, shortLabel)
                }
            });
        }
    }

    private static ResolvedGradePathInputs ResolveGradePathInputs(
        TerrainBuildSnapshot snapshot,
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        GradePathModifierDefinition modifier,
        IReadOnlyList<ConstraintPolyline> hardConstraints,
        double curveTolerance,
        double gradePathTolerance)
    {
        (ResolvedGradePathDefinition[] resolvedDefinitions, IReadOnlyList<VariablePathWidthResolver.Diagnostic> widthDiagnostics) =
            TerrainBuildService.ResolveGradePathDefinitions(snapshot, modifier, curveTolerance, gradePathTolerance);
        // Stop the paths at hard constraints here rather than leave it to the grader, so the elevation
        // constraints persisted for later stages describe the paths that were graded, not ones crossing a pad.
        PathGrader.PathDefinition[] pathArray = PathGrader.StopPathsAtHardConstraints(
            resolvedDefinitions.Select(static item => item.Definition).ToArray(),
            hardConstraints,
            gradePathTolerance,
            out int barrierStops,
            out int piecesLeftToConstraints);
        var constraintTimer = Stopwatch.StartNew();
        var constraintSet = pathArray.Length == 0
            ? new PathGrader.ConstraintSet
            {
                Constraints = Array.Empty<ConstraintPolyline>(),
                SuggestedEdgeLength = 0.0
            }
            : PathGrader.CreateConstraints(vertices, vertexCount, faces, faceCount, pathArray, gradePathTolerance);
        constraintTimer.Stop();

        return new ResolvedGradePathInputs
        {
            Paths = pathArray,
            Constraints = constraintSet.Constraints,
            SuggestedEdgeLength = constraintSet.SuggestedEdgeLength,
            WidthDiagnostics = widthDiagnostics,
            BarrierStops = barrierStops,
            PiecesLeftToConstraints = piecesLeftToConstraints,
            ConstraintElapsed = constraintTimer.Elapsed
        };
    }

    private static GradingResult? GradePathsWindowed(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PathGrader.PathDefinition[] paths,
        IReadOnlyList<ConstraintPolyline> hardConstraints,
        double tolerance,
        bool preferSplitKeep,
        TerrainRuntimeCache runtimeCache,
        string memoKey,
        List<string> diagnostics,
        out string? warning)
    {
        runtimeCache.GradingWindowMemos.TryGetValue(memoKey, out GradingWindows.Memo? previous);
        var next = new GradingWindows.Memo();
        GradeOutcome outcome = PathGrader.GradeWindowed(
            new PathGradeRequest
            {
                Terrain = new IndexedTriMesh(vertices, vertexCount, faces, faceCount),
                Paths = paths,
                HardConstraints = hardConstraints,
                ModelTolerance = tolerance,
                PreferSplitKeep = preferSplitKeep
            },
            previous, next, diagnostics);
        runtimeCache.GradingWindowMemos[memoKey] = next;
        warning = outcome.ErrorMessage;
        return outcome.Result;
    }

    private sealed class ResolvedGradePathInputs
    {
        public required PathGrader.PathDefinition[] Paths { get; init; }

        public required ConstraintPolyline[] Constraints { get; init; }

        public required double SuggestedEdgeLength { get; init; }

        public IReadOnlyList<VariablePathWidthResolver.Diagnostic> WidthDiagnostics { get; init; } =
            Array.Empty<VariablePathWidthResolver.Diagnostic>();

        /// <summary>Crossings of a hard constraint the paths were stopped at before their constraints were made.</summary>
        public int BarrierStops { get; init; }

        /// <summary>Path pieces inside a closed hard constraint (a graded pad), left to it.</summary>
        public int PiecesLeftToConstraints { get; init; }

        /// <summary>How long <c>PathGrader.CreateConstraints</c> took; see the Pad equivalent.</summary>
        public TimeSpan ConstraintElapsed { get; init; }
    }
}
