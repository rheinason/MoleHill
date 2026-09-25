using System.Diagnostics;
using MoleHill.Core.Grading;
using MoleHill.Rhino.Model;
using Rhino.Geometry;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Grade Line: the corridor grader at width zero. The drawn curve is the footprint, so this stage is
/// the Grade Path stage without any of the width apparatus — no rails, no edge matching, one design
/// line published as a breakline instead of two road edges.
/// </summary>
internal sealed partial class TerrainBuildService
{
    private const string GradeLineTopologyTag = "Line";

    private static RhinoMesh ApplyGradeLine(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        GradeLineModifierDefinition modifier,
        TerrainBuildResult build,
        TerrainRuntimeCache runtimeCache,
        int modifierIndex,
        string stageKey,
        TerrainBuildMode mode)
    {
        // The counts must come from the same extraction as the arrays: TryExtractMeshData normalizes
        // a copy, so mesh.Vertices.Count would describe a different mesh than the arrays do.
        if (!RhinoGeometryConversions.TryExtractMeshData(
                mesh,
                out double[] vertices,
                out int vertexCount,
                out int[] faces,
                out int faceCount,
                out string? errorMessage))
        {
            build.Diagnostics.Add(errorMessage ?? "Could not extract mesh data for grade line.");
            return mesh;
        }

        TerrainTolerancePolicy.Profile toleranceProfile = GetToleranceProfile(snapshot, terrain);
        double curveTolerance = toleranceProfile.CurveChordTolerance;
        double gradeLineTolerance = toleranceProfile.GradePathTolerance;

        PathGrader.PathDefinition[] lines = ResolveGradeLineDefinitions(snapshot, modifier, curveTolerance);
        string topologyStageKey = TerrainStageKey.CreateGradingTopology(stageKey, GradeLineTopologyTag);
        if (lines.Length == 0)
        {
            build.Diagnostics.Add("Grade Line has no valid design lines.");
            runtimeCache.GradingTopologyEntries[topologyStageKey] = BuildPathTopologySummary(
                vertices,
                vertexCount,
                faces,
                faceCount,
                gradingResult: null,
                Array.Empty<GradingPatch>(),
                Array.Empty<string>());
            return mesh;
        }

        List<GradingPatch> patchSummaries = BuildPathPatchSummaries(lines);
        List<string> dirtyStageKeys = runtimeCache.FindIntersectingGradingStageKeys(
            TerrainRuntimeCache.GetStagePrefix(mode),
            modifierIndex,
            patchSummaries);
        if (dirtyStageKeys.Count > 0)
        {
            runtimeCache.InvalidateStages(dirtyStageKeys);
            build.Diagnostics.Add(
                $"Grade Line invalidated {dirtyStageKeys.Count} overlapping downstream grading stage(s): {string.Join(", ", dirtyStageKeys.Select(TerrainStageKey.GetBase))}.");
        }

        // Same rule as Grade Path: only a hard constraint that meets this line's corridor may reorder
        // the tiers. The corridor constraints are built only when the answer could change.
        bool hasInteractingHardConstraints =
            TerrainBuildHeuristics.ShouldPreferSplitKeepGradePath(mode, hasInteractingHardConstraints: true, faceCount) &&
            build.PersistentHardConstraints.Count > 0 &&
            AnalyzeHardConstraintConflicts(
                PathGrader.CreateConstraints(vertices, vertexCount, faces, faceCount, lines, gradeLineTolerance).Constraints,
                build.PersistentHardConstraints,
                gradeLineTolerance).HasConflicts;

        var coreTimer = Stopwatch.StartNew();
        GradingResult? gradingResult = PathGrader.Grade(
            vertices,
            vertexCount,
            faces,
            faceCount,
            lines,
            build.PersistentHardConstraints,
            out string? warning,
            gradeLineTolerance,
            preferSplitKeep: TerrainBuildHeuristics.ShouldPreferSplitKeepGradePath(
                mode,
                hasInteractingHardConstraints,
                faceCount));
        coreTimer.Stop();

        if (gradingResult == null)
        {
            build.RecordTiming("Grade Line Core", coreTimer.Elapsed, "failed", StageTimingDiagnosticThresholdMs);
            build.Diagnostics.Add(warning ?? "Grade Line failed.");
            runtimeCache.GradingTopologyEntries[topologyStageKey] = BuildPathTopologySummary(
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
            "Grade Line Core",
            coreTimer.Elapsed,
            DescribeTopologyCounts(vertexCount, faceCount, gradingResult.VertexCount, gradingResult.FaceCount),
            StageTimingDiagnosticThresholdMs);
        if (!string.IsNullOrWhiteSpace(warning))
            build.Diagnostics.Add(warning);
        build.AddGradingDiagnostics(gradingResult);

        // The design line becomes a hard constraint so later modifiers respect it — which is what
        // lets stacked Grade Lines on offset feature lines build a compound cross-section.
        AddOutputPolylinesAsBreaklines(gradingResult.OutputPolylines, build);
        runtimeCache.GradingTopologyEntries[topologyStageKey] = BuildPathTopologySummary(
            vertices,
            vertexCount,
            faces,
            faceCount,
            gradingResult,
            patchSummaries,
            build.Diagnostics);
        return FinalizeGradingMesh(
            RhinoGeometryConversions.BuildMesh(gradingResult.Vertices, gradingResult.VertexCount, gradingResult.Faces, gradingResult.FaceCount),
            "Grade Line",
            build);
    }

    private static PathGrader.PathDefinition[] ResolveGradeLineDefinitions(
        TerrainBuildSnapshot snapshot,
        GradeLineModifierDefinition modifier,
        double curveTolerance)
    {
        var lines = new List<PathGrader.PathDefinition>();
        double cutSlope = modifier.CutSlopeAngle > 0.0 ? modifier.CutSlopeAngle : modifier.SlopeAngle;

        // Per-side overrides only apply when the card asks for an asymmetric section; otherwise the
        // parked values stay on the definition but never reach Core, exactly as Grade Path treats its
        // width edges.
        double leftCut = modifier.UseAsymmetricSides ? modifier.LeftCutSlopeAngle : 0.0;
        double leftFill = modifier.UseAsymmetricSides ? modifier.LeftFillSlopeAngle : 0.0;
        double rightCut = modifier.UseAsymmetricSides ? modifier.RightCutSlopeAngle : 0.0;
        double rightFill = modifier.UseAsymmetricSides ? modifier.RightFillSlopeAngle : 0.0;

        foreach (Curve curve in TerrainBuildSnapshotResolver.ResolveCurves(snapshot, modifier.Lines))
        {
            // A line has no width to set the sampling density, so the authored vertices are kept and
            // Core resamples from the batter reach it actually computes.
            if (!RhinoSourceResolver.TryGetPolyline(
                    curve,
                    curveTolerance,
                    requireClosed: false,
                    requestedEdgeLength: 0.0,
                    maxArea: 0.0,
                    out Polyline polyline) ||
                polyline.Count < 2)
            {
                continue;
            }

            var xy = new double[polyline.Count * 2];
            var z = new double[polyline.Count];
            for (int i = 0; i < polyline.Count; i++)
            {
                xy[i * 2] = polyline[i].X;
                xy[(i * 2) + 1] = polyline[i].Y;
                z[i] = polyline[i].Z;
            }

            lines.Add(new PathGrader.PathDefinition(
                xy,
                z,
                polyline.Count,
                width: 0.0,
                slopeAngleDeg: cutSlope,
                maxDistance: modifier.MaxDistance,
                fillSlopeAngleDeg: modifier.SlopeAngle,
                isClosed: curve.IsClosed,
                leftCutSlopeAngleDeg: leftCut,
                leftFillSlopeAngleDeg: leftFill,
                rightCutSlopeAngleDeg: rightCut,
                rightFillSlopeAngleDeg: rightFill));
        }

        return lines.ToArray();
    }
}
