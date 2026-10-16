// Curvature stage: landform class areas summarized for the analysis card and the report.
using MoleHill.Core.Analysis;
using MoleHill.Core.Engine;
using MoleHill.Rhino.Model;

namespace MoleHill.Rhino.Services;

internal sealed partial class TerrainBuildService
{
    /// <summary>
    /// Measures the terrain's curvature at the card's landform size and totals the plan area of each
    /// landform class. The class areas do not depend on what the card displays, so switching between
    /// landforms and a curvature value never re-runs this. Nothing measured leaves the landform fields
    /// null, so "too sparse to fit" never reads as "all planar".
    /// </summary>
    internal static TerrainAnalysisSummary BuildCurvatureSummary(
        IndexedTriMesh terrain,
        CurvatureAnalysisDefinition analysis,
        double surfaceArea,
        Func<bool>? shouldCancel)
    {
        var summary = new TerrainAnalysisSummary { AnalysisId = analysis.Id, SurfaceArea = surfaceArea };

        double landformSize = LandformSize.Resolve(analysis.FeatureSize, terrain);
        CurvatureField field = CurvatureAnalyzer.Measure(terrain, landformSize, shouldCancel);
        sbyte[] classes = LandformClassifier.Classify(field, analysis.FlatnessTolerance);
        double[] areas = LandformClassifier.SumPlanAreas(classes, field.PlanAreas, out double measured);
        if (!(measured > 0.0))
            return summary;

        var shares = new double[areas.Length];
        for (int i = 0; i < areas.Length; i++)
            shares[i] = areas[i] / measured;

        summary.LandformMeasuredArea = measured;
        summary.LandformSize = landformSize;
        summary.LandformClassShares = shares;
        return summary;
    }
}
