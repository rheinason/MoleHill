using MoleHill.Core.Analysis;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class CurveReviewThresholdInputTests
{
    // The rule kind is internal, so the theory carries it as an int.
    [Theory]
    [InlineData((int)CurveReviewRuleKind.MaximumGrade, "")]
    [InlineData((int)CurveReviewRuleKind.MaximumGrade, "   ")]
    [InlineData((int)CurveReviewRuleKind.MaximumGrade, "steep")]
    [InlineData((int)CurveReviewRuleKind.MinimumPlanRadius, "")]
    [InlineData((int)CurveReviewRuleKind.VerticalGradeChange, "abc")]
    [InlineData((int)CurveReviewRuleKind.TerrainCoverage, "")]
    public void TryParse_EmptyOrInvalidText_IsRejectedRatherThanReadAsZero(int kind, string text)
    {
        Assert.False(CurveReviewThresholdInput.TryParse((CurveReviewRuleKind)kind, text, SlopeAnalyzer.SlopeUnit.Percent, out _));
    }

    [Theory]
    [InlineData("12", SlopeAnalyzer.SlopeUnit.Percent, 12.0)]
    [InlineData("120", SlopeAnalyzer.SlopeUnit.Promille, 12.0)]
    [InlineData("1:8", SlopeAnalyzer.SlopeUnit.Percent, 12.5)]
    [InlineData("8", SlopeAnalyzer.SlopeUnit.Ratio, 12.5)]
    [InlineData("45deg", SlopeAnalyzer.SlopeUnit.Percent, 100.0)]
    [InlineData("-12%", SlopeAnalyzer.SlopeUnit.Percent, 12.0)]
    public void TryParse_MaximumGrade_ReadsAnySlopeUnitAsPercent(string text, SlopeAnalyzer.SlopeUnit unit, double expected)
    {
        Assert.True(CurveReviewThresholdInput.TryParse(CurveReviewRuleKind.MaximumGrade, text, unit, out double percent));
        Assert.Equal(expected, percent, 6);
    }

    [Theory]
    [InlineData(12.0, SlopeAnalyzer.SlopeUnit.Percent, "12")]
    [InlineData(12.5, SlopeAnalyzer.SlopeUnit.Promille, "125")]
    [InlineData(12.5, SlopeAnalyzer.SlopeUnit.Ratio, "1:8")]
    public void Format_MaximumGrade_ShowsTheUserSlopeUnit(double percent, SlopeAnalyzer.SlopeUnit unit, string expected)
    {
        // Whole-number cases, so the expectation holds under a comma-decimal culture too.
        Assert.Equal(expected, CurveReviewThresholdInput.Format(CurveReviewRuleKind.MaximumGrade, percent, unit));
    }

    [Fact]
    public void TryParse_VerticalGradeChange_StaysInPercentagePoints()
    {
        // A grade difference is not a slope a unit can re-express, so the slope preference does not apply.
        Assert.True(CurveReviewThresholdInput.TryParse(
            CurveReviewRuleKind.VerticalGradeChange, "10", SlopeAnalyzer.SlopeUnit.Degrees, out double points));
        Assert.Equal(10.0, points, 9);
    }
}
