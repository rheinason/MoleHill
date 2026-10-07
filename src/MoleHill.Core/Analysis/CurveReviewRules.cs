// Defines configurable curve-review rule modes, settings and evaluated results.
namespace MoleHill.Core.Analysis;

internal enum CurveReviewRuleMode
{
    Off,
    Report,
    Warn
}

internal enum CurveReviewRuleKind
{
    MaximumGrade,
    MinimumPlanRadius,
    VerticalGradeChange,
    TerrainCoverage
}

internal sealed class CurveReviewRuleSettings
{
    public CurveReviewRuleMode MaximumGradeMode { get; set; } = CurveReviewRuleMode.Warn;
    public double MaximumGradePercent { get; set; } = 12.0;
    public CurveReviewRuleMode MinimumRadiusMode { get; set; } = CurveReviewRuleMode.Warn;
    public double MinimumRadius { get; set; } = 25.0;
    public CurveReviewRuleMode VerticalBreakMode { get; set; } = CurveReviewRuleMode.Report;
    public double VerticalBreakThresholdPercent { get; set; } = 10.0;
    public CurveReviewRuleMode TerrainCoverageMode { get; set; } = CurveReviewRuleMode.Warn;
    public double MinimumTerrainCoveragePercent { get; set; } = 90.0;

    public CurveReviewRuleSettings Duplicate() => (CurveReviewRuleSettings)MemberwiseClone();
}

internal readonly record struct CurveReviewCheckResult(
    CurveReviewRuleKind Kind,
    CurveReviewRuleMode Mode,
    double Actual,
    double Threshold,
    bool IsAvailable,
    bool IsViolation,
    int Occurrences = 0,
    bool HasPlanCorners = false)
{
    public bool IsWarning => IsAvailable && IsViolation && Mode == CurveReviewRuleMode.Warn;
}

internal static class CurveReviewRuleEvaluator
{
    public static IReadOnlyList<CurveReviewCheckResult> Evaluate(
        CurveReviewRuleSettings rules,
        double maximumAbsoluteGrade,
        int gradeRunCount,
        double minimumPlanRadius,
        int radiusRunCount,
        double maximumVerticalGradeChange,
        int verticalBreakCount,
        double terrainCoveragePercent,
        int terrainMisses,
        int planCornerCount = 0)
    {
        return new[]
        {
            new CurveReviewCheckResult(
                CurveReviewRuleKind.MaximumGrade, rules.MaximumGradeMode, maximumAbsoluteGrade,
                rules.MaximumGradePercent, true, maximumAbsoluteGrade > rules.MaximumGradePercent, gradeRunCount),
            new CurveReviewCheckResult(
                CurveReviewRuleKind.MinimumPlanRadius, rules.MinimumRadiusMode, minimumPlanRadius,
                rules.MinimumRadius, planCornerCount > 0 || !double.IsNaN(minimumPlanRadius),
                planCornerCount > 0 || (!double.IsNaN(minimumPlanRadius) && minimumPlanRadius < rules.MinimumRadius),
                radiusRunCount + planCornerCount,
                planCornerCount > 0),
            new CurveReviewCheckResult(
                CurveReviewRuleKind.VerticalGradeChange, rules.VerticalBreakMode, maximumVerticalGradeChange,
                rules.VerticalBreakThresholdPercent, true,
                maximumVerticalGradeChange > rules.VerticalBreakThresholdPercent, verticalBreakCount),
            new CurveReviewCheckResult(
                CurveReviewRuleKind.TerrainCoverage, rules.TerrainCoverageMode, terrainCoveragePercent,
                rules.MinimumTerrainCoveragePercent, double.IsFinite(terrainCoveragePercent),
                double.IsFinite(terrainCoveragePercent) && terrainCoveragePercent < rules.MinimumTerrainCoveragePercent,
                terrainMisses)
        };
    }
}
