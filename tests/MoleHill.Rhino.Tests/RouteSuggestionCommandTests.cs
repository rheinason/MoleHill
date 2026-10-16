using MoleHill.Core.Analysis;
using MoleHill.Core.Routing;
using MoleHill.Rhino.Services;
using MoleHill.Shared;
using Rhino;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>The managed half of mhSuggestRoute: its remembered options and every word it prints or stores.</summary>
public sealed class RouteSuggestionCommandTests
{
    private static readonly ModelUnitContext Metres = ModelUnitContext.FromUnitSystem(UnitSystem.Meters);
    private static readonly ModelUnitContext Millimetres = ModelUnitContext.FromUnitSystem(UnitSystem.Millimeters);
    private static readonly RouteMetrics Sample = new(523.4, 0.049, 0.021, 12.04, 9.46, 0.6, 0.4);

    [Fact]
    public void Settings_CorridorWidth_KeepsItsPhysicalSizeAcrossModelUnits()
    {
        RouteSuggestionSettings inMetres = RouteSuggestionSettings.Load(Metres);
        inMetres.CorridorWidth = 6.0;
        inMetres.CellSize = 1.5;
        inMetres.Save(Metres);

        RouteSuggestionSettings inMillimetres = RouteSuggestionSettings.Load(Millimetres);

        Assert.Equal(6000.0, inMillimetres.CorridorWidth, 9);
        Assert.Equal(1500.0, inMillimetres.CellSize, 9);
        Assert.Equal(6000.0, inMillimetres.ToRequest(new[] { (0.0, 0.0), (1.0, 1.0) }, 0.05, 0.01).CorridorWidth, 9);

        inMetres.CorridorWidth = RouteSuggestionSettings.DefaultCorridorWidthMeters;
        inMetres.CellSize = 0.0;
        inMetres.Save(Metres);
    }

    [Fact]
    public void Settings_Validate_NamesTheOptionToChange()
    {
        var settings = new RouteSuggestionSettings { CorridorWidth = 4.0 };
        Assert.Null(settings.Validate(0.08));
        Assert.Contains("MaxGrade", settings.Validate(0.0));

        settings.DistanceWeight = settings.SteepnessWeight = settings.EarthworksWeight = 0.0;
        Assert.Contains("Distance, Steepness and Earthworks", settings.Validate(0.08));

        settings.DistanceWeight = 1.0;
        settings.CorridorWidth = 0.0;
        Assert.Contains("CorridorWidth", settings.Validate(0.08));
    }

    [Fact]
    public void Settings_ResolveCellSize_AutomaticScalesAndExplicitIsBounded()
    {
        var automatic = new RouteSuggestionSettings();
        (double metres, bool coarsenedMetres) = automatic.ResolveCellSize(2000, 2000, 1500);
        (double millimetres, _) = automatic.ResolveCellSize(2_000_000, 2_000_000, 1_500_000);
        Assert.False(coarsenedMetres);
        Assert.Equal(metres * 1000.0, millimetres, 6);

        var tooFine = new RouteSuggestionSettings { CellSize = 0.01 };
        (double cell, bool coarsened) = tooFine.ResolveCellSize(2000, 2000, 1500);
        Assert.True(coarsened);
        Assert.True((2000 / cell) * (2000 / cell) <= RouteHeightGrid.MaximumNodeCount);
    }

    [Fact]
    public void Settings_ToRequest_ClampsAlternatives()
    {
        var settings = new RouteSuggestionSettings { CorridorWidth = 4.0, AlternativeCount = 9 };
        Assert.Equal(RouteSuggester.MaxAlternatives, settings.ToRequest(new[] { (0.0, 0.0), (1.0, 1.0) }, 0.05, 0.01).AlternativeCount);
    }

    [Fact]
    public void Describe_WithComplexity_ShowsTheTotalThenItsParts()
    {
        Assert.Equal(
            "Route 1: 523.40 m, max grade 4.9%, mean 2.1%, cut 12.0 m³, fill 9.5 m³, complexity 5.6 (plan 4.3, profile 1.3)",
            RouteSuggestionReadout.Describe(1, Sample, RouteFilletSummary.None, 0.0, Metres, SlopeAnalyzer.SlopeUnit.Percent,
                new RouteComplexity(4.3, 1.3)));
    }

    [Fact]
    public void Describe_ReadsInModelUnitsAndTheUsersSlopeUnit()
    {
        Assert.Equal(
            "Route 1: 523.40 m, max grade 4.9%, mean 2.1%, cut 12.0 m³, fill 9.5 m³",
            RouteSuggestionReadout.Describe(1, Sample, RouteFilletSummary.None, 0.0, Metres, SlopeAnalyzer.SlopeUnit.Percent));
        Assert.Equal(
            "Route 2: 523.40 mm, max grade 1:20.4, mean 1:47.6, cut 12.0 mm³, fill 9.5 mm³",
            RouteSuggestionReadout.Describe(2, Sample, RouteFilletSummary.None, 0.0, Millimetres, SlopeAnalyzer.SlopeUnit.Ratio));
        Assert.Equal("49.0‰", RouteSuggestionReadout.Slope(0.049, SlopeAnalyzer.SlopeUnit.Promille));
    }

    [Fact]
    public void ObjectNameAndUserText_DescribeTheRoute()
    {
        Assert.Equal(
            "Suggested route 3 (523 m, max 4.9%)",
            RouteSuggestionReadout.ObjectName(3, Sample, Metres, SlopeAnalyzer.SlopeUnit.Percent));

        var text = RouteSuggestionReadout.UserText(3, Sample, 0.05, 4.0, new RouteFilletSummary(8.5, 2), 10.0, Metres, SlopeAnalyzer.SlopeUnit.Percent)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal("3", text["MoleHill.Route.Rank"]);
        Assert.Equal("523.40 m", text["MoleHill.Route.Length"]);
        Assert.Equal("4.9%", text["MoleHill.Route.MaxGrade"]);
        Assert.Equal("5.0%", text["MoleHill.Route.GradeLimit"]);
        Assert.Equal("12.0 m³", text["MoleHill.Route.Cut"]);
        Assert.Equal("4.00 m", text["MoleHill.Route.CorridorWidth"]);
        Assert.Equal("10.00 m", text["MoleHill.Route.RadiusLimit"]);
        Assert.Equal("8.50 m", text["MoleHill.Route.MinRadius"]);
        Assert.Equal("2", text["MoleHill.Route.TightCorners"]);
        Assert.All(text.Keys, key => Assert.StartsWith(RouteSuggestionReadout.UserTextPrefix, key));
    }

    [Fact]
    public void DescribeFailure_TooSteep_GivesTheRequiredGradeAndWhatToChange()
    {
        RouteSuggestionResult result = RouteSuggestionResult.Fail(RouteFailure.ExceedsMaxGrade, leg: 1, requiredGrade: 0.125);

        string message = RouteSuggestionReadout.DescribeFailure(result, 0.08, SlopeAnalyzer.SlopeUnit.Percent);

        Assert.Contains("within 8.0%", message);
        Assert.Contains("needs 12.5%", message);
        Assert.Contains("point 2 and point 3", message);
        Assert.Contains("MaxGrade", message);
    }

    [Fact]
    public void DescribeFailure_EveryFailureHasAMessage()
    {
        foreach (RouteFailure failure in Enum.GetValues<RouteFailure>().Where(value => value != RouteFailure.None))
        {
            string message = RouteSuggestionReadout.DescribeFailure(RouteSuggestionResult.Fail(failure, 0, 0, 0.1), 0.05, SlopeAnalyzer.SlopeUnit.Percent);
            Assert.False(string.IsNullOrWhiteSpace(message));
            Assert.NotEqual("No route was found.", message);
        }
    }

    [Fact]
    public void Describe_MinimumRadius_ReportsTheSmallestAndTheCornersThatCouldNotTakeIt()
    {
        Assert.EndsWith(
            ", min radius 10.00 m",
            RouteSuggestionReadout.Describe(1, Sample, new RouteFilletSummary(10.0, 0), 10.0, Metres, SlopeAnalyzer.SlopeUnit.Percent));
        Assert.EndsWith(
            ", min radius 4.25 m (2 corners under 10.00 m where a wider curve costs more than allowed or leaves the terrain)",
            RouteSuggestionReadout.Describe(1, Sample, new RouteFilletSummary(4.25, 2), 10.0, Metres, SlopeAnalyzer.SlopeUnit.Percent));
        Assert.EndsWith(
            ", min radius 0.00 m (1 corner under 10.00 m kept tighter because a wider curve would break the grade)",
            RouteSuggestionReadout.Describe(1, Sample, new RouteFilletSummary(0.0, 1, 0, 1), 10.0, Metres, SlopeAnalyzer.SlopeUnit.Percent));
        Assert.EndsWith(
            " (4 corners under 10.00 m: 2 where a wider curve costs more than allowed or leaves the terrain, " +
            "1 kept tighter because a wider curve would break the grade, 1 turning back on themselves)",
            RouteSuggestionReadout.Describe(1, Sample, new RouteFilletSummary(0.0, 4, 1, 1), 10.0, Metres, SlopeAnalyzer.SlopeUnit.Percent));
        Assert.Equal(string.Empty, RouteSuggestionReadout.DescribeRadius(new RouteFilletSummary(4.0, 1), 0.0, Metres));
        Assert.Equal(string.Empty, RouteSuggestionReadout.DescribeRadius(RouteFilletSummary.None, 10.0, Metres));
    }

    [Fact]
    public void Settings_MinimumRadius_DefaultsToTenMetresInAnyUnit()
    {
        RouteSuggestionSettings inMillimetres = RouteSuggestionSettings.Load(Millimetres);
        Assert.Equal(RouteSuggestionSettings.DefaultMinimumRadiusMeters * 1000.0, inMillimetres.MinimumRadius, 9);
        Assert.Equal(inMillimetres.MinimumRadius, inMillimetres.ToRequest(new[] { (0.0, 0.0), (1.0, 1.0) }, 0.05, 0.01).MinimumRadius, 9);
    }

    [Fact]
    public void OutputLayer_RememberedPerDocument_FallsBackToCurrentWhenDeleted()
    {
        uint document = 900_001, other = 900_002;
        var layer = Guid.NewGuid();
        RouteOutputLayer.Remember(document, layer);

        Assert.Equal(layer, RouteOutputLayer.Remembered(document));
        Assert.Equal(Guid.Empty, RouteOutputLayer.Remembered(other));

        Assert.Equal(layer, RouteOutputLayer.Resolve(layer, id => id == layer, out bool kept));
        Assert.False(kept);
        Assert.Equal(Guid.Empty, RouteOutputLayer.Resolve(layer, _ => false, out bool fellBack));
        Assert.True(fellBack);
        Assert.Equal(Guid.Empty, RouteOutputLayer.Resolve(Guid.Empty, _ => false, out bool current));
        Assert.False(current);
    }

    [Fact]
    public void OutputLayer_OptionValue_IsOneWord()
    {
        Assert.Equal("Current", RouteOutputLayer.OptionValue(null));
        Assert.Equal("Road_centrelines", RouteOutputLayer.OptionValue("Road centrelines"));
        Assert.Equal("Layer", RouteOutputLayer.OptionValue("::"));
    }
}
