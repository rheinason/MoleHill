using System.Diagnostics;
using MoleHill.Core.Grading;
using MoleHill.Core.Engine;
using MoleHill.Rhino.Model;
using MoleHill.Shared;
using Rhino.Geometry;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

/// <summary>
/// The Retaining Wall modifier's graded mode. The rails have already been inserted as breaklines and
/// their elevations forced, so this only adds what the breakline-only mode leaves undone: a batter
/// running away from each rail out to daylight.
/// </summary>
/// <remarks>
/// Each rail grades one way only — away from its partner — so the wall face itself is never buried and
/// never has terrain pushed through it. That direction is not the rail curve's own plan normal, so it
/// is handed to Core explicitly rather than derived.
/// </remarks>
internal sealed partial class TerrainBuildService
{
    private static RhinoMesh ApplyRetainingWallGrading(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        RetainingWallModifierDefinition modifier,
        IReadOnlyList<RetainingWallPlannerCore.PlannedWall> walls,
        double wallTolerance,
        TerrainBuildResult build,
        TerrainBuildMode mode,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> barrierConstraints)
    {
        if (!modifier.GradesTerrain || walls.Count == 0)
            return mesh;

        if (!RhinoGeometryConversions.TryExtractMeshData(
                mesh,
                out double[] vertices,
                out int vertexCount,
                out int[] faces,
                out int faceCount,
                out string? errorMessage))
        {
            build.Diagnostics.Add(errorMessage ?? "Could not extract mesh data for retaining wall grading.");
            return mesh;
        }

        List<PathGrader.PathDefinition> railGrades = RetainingWallGradePlanner.Build(
            walls.Where(w => IsWallStripUsable(w.Rails, wallTolerance, out _)).ToList(),
            BuildRetainingWallGradeOptions(modifier, wallTolerance));
        if (railGrades.Count == 0)
        {
            build.Diagnostics.Add("Retaining Wall grading found no rail to batter from.");
            return mesh;
        }

        var coreTimer = Stopwatch.StartNew();
        GradingResult? gradingResult = PathGrader.Grade(
            vertices,
            vertexCount,
            faces,
            faceCount,
            railGrades.ToArray(),
            // The terrain's existing constraints act as barriers, exactly as Grade Pad's lock curves do.
            // This wall's own rails are deliberately not among them: grading runs before they are
            // inserted, so a rail never becomes a barrier standing on its own batter's foot.
            barrierConstraints,
            out string? warning,
            wallTolerance,
            preferSplitKeep: TerrainBuildHeuristics.ShouldPreferSplitKeepGradePath(
                mode,
                barrierConstraints.Count > 0,
                faceCount));
        coreTimer.Stop();

        if (gradingResult == null)
        {
            build.RecordTiming("Retaining Wall Grading", coreTimer.Elapsed, "failed", StageTimingDiagnosticThresholdMs);
            build.Diagnostics.Add(warning ?? "Retaining Wall grading failed; the wall breaklines were kept.");
            return mesh;
        }

        build.RecordTiming(
            "Retaining Wall Grading",
            coreTimer.Elapsed,
            DescribeTopologyCounts(vertexCount, faceCount, gradingResult.VertexCount, gradingResult.FaceCount),
            StageTimingDiagnosticThresholdMs);
        if (!string.IsNullOrWhiteSpace(warning))
            build.Diagnostics.Add(warning);
        build.AddGradingDiagnostics(gradingResult);

        return FinalizeGradingMesh(
            RhinoGeometryConversions.BuildMesh(gradingResult.Vertices, gradingResult.VertexCount, gradingResult.Faces, gradingResult.FaceCount),
            "Retaining Wall",
            build);
    }

    /// <summary>
    /// Translates the card's slope rows into the shared planner's options. Per-side overrides only
    /// apply when the card asks for an asymmetric section; otherwise they stay parked on the
    /// definition and never reach Core, exactly as Grade Path treats its width edges.
    /// </summary>
    private static RetainingWallGradePlanner.Options BuildRetainingWallGradeOptions(
        RetainingWallModifierDefinition modifier,
        double wallTolerance) => new()
    {
        FillAngleDeg = modifier.SlopeAngle,
        CutAngleDeg = modifier.CutSlopeAngle,
        Toe = modifier.UseAsymmetricSides
            ? new RetainingWallGradePlanner.SideSlopes(modifier.ToeCutSlopeAngle, modifier.ToeFillSlopeAngle)
            : RetainingWallGradePlanner.SideSlopes.Inherit,
        Top = modifier.UseAsymmetricSides
            ? new RetainingWallGradePlanner.SideSlopes(modifier.TopCutSlopeAngle, modifier.TopFillSlopeAngle)
            : RetainingWallGradePlanner.SideSlopes.Inherit,
        MaxDistance = modifier.MaxDistance,
        Tolerance = wallTolerance,
    };
}
