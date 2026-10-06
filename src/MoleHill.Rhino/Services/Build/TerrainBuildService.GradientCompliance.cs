// Gradient compliance stage: level-area checks summarized for the analysis card and the report.
using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;

namespace MoleHill.Rhino.Services;

internal sealed partial class TerrainBuildService
{
    /// <summary>
    /// Summarizes a gradient compliance card: how much level area and route corridor was checked, how much
    /// of it is over a limit, and the steepest ground found. Values the card did not measure stay null, so "no level areas
    /// set" never reads as "every landing passes".
    /// </summary>
    private static TerrainAnalysisSummary BuildGradientComplianceSummary(
        TerrainBuildSnapshot snapshot,
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        GradientComplianceAnalysisDefinition analysis,
        Func<bool>? shouldCancel)
    {
        var summary = new TerrainAnalysisSummary { AnalysisId = analysis.Id };
        if (analysis.Rules.LevelAreaMode == GradientRuleMode.Off && analysis.Rules.RouteMode == GradientRuleMode.Off)
            return summary;

        GradientComplianceEvaluator.Evaluation evaluation = GradientComplianceEvaluator.Evaluate(
            vertices,
            vertexCount,
            faces,
            faceCount,
            TerrainBuildSnapshotResolver.ResolveCurves(snapshot, analysis.LevelAreas),
            TerrainBuildSnapshotResolver.ResolveCurves(snapshot, analysis.Routes),
            analysis,
            snapshot.ModelAbsoluteTolerance,
            shouldCancel);

        if (evaluation.LevelAreas.MaxSlopeRatio is { } steepest)
        {
            summary.LevelAreaCheckedArea = evaluation.LevelAreas.CheckedArea;
            summary.LevelAreaExceedingArea = evaluation.LevelAreas.ExceedingArea;
            summary.LevelAreaSteepestSlopeDegrees = ToDegrees(steepest);
        }

        GradientComplianceAnalyzer.RouteResult routes = evaluation.Routes;
        if (routes.MaxRunningRatio is { } running && routes.MaxCrossRatio is { } cross)
        {
            summary.RouteCheckedArea = routes.CheckedArea;
            summary.RouteRampArea = routes.RampArea;
            summary.RouteRunningExceedingArea = routes.RunningExceedingArea;
            summary.RouteCrossExceedingArea = routes.CrossExceedingArea;
            summary.RouteSteepestRunningDegrees = ToDegrees(running);
            summary.RouteSteepestCrossDegrees = ToDegrees(cross);
        }

        if (evaluation.Runs.Count > 0)
        {
            var runs = evaluation.Runs
                .Where(run => run.Kind is RouteRunAnalyzer.RunKind.Walk or RouteRunAnalyzer.RunKind.Ramp)
                .ToList();
            summary.RouteRunCount = runs.Count;
            summary.RouteFailedRunCount = runs.Count(run => run.Failures != RouteRunAnalyzer.RunFailure.None);
            summary.RouteLandingCount = evaluation.Runs.Count(run => run.Kind == RouteRunAnalyzer.RunKind.Landing);
            summary.RouteLargestRunRise = runs.Count == 0 ? null : runs.Max(run => run.Rise);
        }

        return summary;
    }

    private static double ToDegrees(double ratio) => Math.Atan(ratio) * 180.0 / Math.PI;
}
