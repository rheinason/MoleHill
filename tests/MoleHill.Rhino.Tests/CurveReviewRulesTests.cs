using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class CurveReviewRulesTests
{
    [Fact]
    public void Evaluate_ReportViolation_DoesNotBecomeWarning()
    {
        var rules = new CurveReviewRuleSettings
        {
            VerticalBreakMode = CurveReviewRuleMode.Report,
            VerticalBreakThresholdPercent = 5
        };

        IReadOnlyList<CurveReviewCheckResult> results = CurveReviewRuleEvaluator.Evaluate(
            rules, 0, 0, double.PositiveInfinity, 0, 12, 1, double.NaN, 0);

        CurveReviewCheckResult result = results.Single(item => item.Kind == CurveReviewRuleKind.VerticalGradeChange);
        Assert.True(result.IsViolation);
        Assert.False(result.IsWarning);
    }

    [Fact]
    public void Evaluate_StraightPlanRadius_IsAvailableAndPasses()
    {
        var rules = new CurveReviewRuleSettings { MinimumRadius = 25 };

        IReadOnlyList<CurveReviewCheckResult> results = CurveReviewRuleEvaluator.Evaluate(
            rules, 0, 0, double.PositiveInfinity, 0, 0, 0, 100, 0);

        CurveReviewCheckResult result = results.Single(item => item.Kind == CurveReviewRuleKind.MinimumPlanRadius);
        Assert.True(result.IsAvailable);
        Assert.False(result.IsViolation);
    }

    [Fact]
    public void Evaluate_NoTerrain_MakesCoverageUnavailable()
    {
        var rules = new CurveReviewRuleSettings();

        IReadOnlyList<CurveReviewCheckResult> results = CurveReviewRuleEvaluator.Evaluate(
            rules, 0, 0, 100, 0, 0, 0, double.NaN, 0);

        CurveReviewCheckResult result = results.Single(item => item.Kind == CurveReviewRuleKind.TerrainCoverage);
        Assert.False(result.IsAvailable);
        Assert.False(result.IsWarning);
    }

    [Fact]
    public void Evaluate_PlanCorner_ReportsCornerAsRadiusViolation()
    {
        var rules = new CurveReviewRuleSettings { MinimumRadius = 25 };

        IReadOnlyList<CurveReviewCheckResult> results = CurveReviewRuleEvaluator.Evaluate(
            rules, 0, 0, double.PositiveInfinity, 0, 0, 0, double.NaN, 0, planCornerCount: 1);

        CurveReviewCheckResult result = results.Single(item => item.Kind == CurveReviewRuleKind.MinimumPlanRadius);
        Assert.True(result.IsAvailable);
        Assert.True(result.IsViolation);
        Assert.True(result.HasPlanCorners);
        Assert.Equal(1, result.Occurrences);
    }
}
