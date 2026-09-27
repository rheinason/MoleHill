// Gradient compliance stage: level-area checks summarized for the analysis card and the report.
using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;

namespace MoleHill.Rhino.Services;

internal sealed partial class TerrainBuildService
{
    /// <summary>
    /// Summarizes a gradient compliance card: how much level area was checked, how much of it is over the
    /// limit, and the steepest ground found. Values the card did not measure stay null, so "no level areas
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
        if (analysis.Rules.LevelAreaMode == GradientRuleMode.Off)
            return summary;

        GradientComplianceAnalyzer.Result result = GradientComplianceEvaluator.Evaluate(
            vertices,
            vertexCount,
            faces,
            faceCount,
            TerrainBuildSnapshotResolver.ResolveCurves(snapshot, analysis.LevelAreas),
            analysis,
            snapshot.ModelAbsoluteTolerance,
            shouldCancel);

        if (result.MaxSlopeRatio is not { } steepest)
            return summary;

        summary.LevelAreaCheckedArea = result.CheckedArea;
        summary.LevelAreaExceedingArea = result.ExceedingArea;
        summary.LevelAreaSteepestSlopeDegrees = Math.Atan(steepest) * 180.0 / Math.PI;
        return summary;
    }
}
