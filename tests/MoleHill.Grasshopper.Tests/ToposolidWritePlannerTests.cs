using MoleHill.Revit.Planning;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Grasshopper.Tests;

public class ToposolidWritePlannerTests
{
    private const double Tolerance = 0.00256; // Revit's ShortCurveTolerance, in feet.

    [Fact]
    public void TryPlan_MetreInput_ConvertsOnceToInternalFeet()
    {
        Assert.True(ToposolidWritePlanner.TryPlan(new[] { Package("a") }, Tolerance, out var plans, out var errors), string.Join("; ", errors));

        PlannedToposolid plan = Assert.Single(plans);
        PlannedLoop loop = Assert.Single(plan.Loops);
        Assert.Equal(4, loop.Vertices.Count);
        Assert.Equal(10.0 / 0.3048, loop.Vertices[1].X, 9);
        Assert.Equal(2.0 / 0.3048, plan.Points[0].Z, 9);
    }

    [Fact]
    public void TryPlan_MillimetreInput_ScalesByMetersPerUnit()
    {
        PreparationInput input = Package("a") with { MetersPerUnit = 0.001 };

        Assert.True(ToposolidWritePlanner.TryPlan(new[] { input }, Tolerance, out var plans, out _));

        Assert.Equal(10.0 * 0.001 / 0.3048, plans[0].Loops[0].Vertices[1].X, 12);
    }

    [Fact]
    public void TryPlan_RepeatedKey_RejectsTheWholeRun()
    {
        Assert.False(ToposolidWritePlanner.TryPlan(new[] { Package("a"), Package("a") }, Tolerance, out var plans, out var errors));

        Assert.Empty(plans);
        Assert.Contains(errors, error => error.Contains("more than once", StringComparison.Ordinal));
    }

    [Fact]
    public void TryPlan_EmptyKey_IsRejected()
    {
        Assert.False(ToposolidWritePlanner.TryPlan(new[] { Package("") }, Tolerance, out _, out var errors));

        Assert.Contains(errors, error => error.Contains("key is empty", StringComparison.Ordinal));
    }

    [Fact]
    public void TryPlan_NonPositiveMetersPerUnit_IsRejected()
    {
        Assert.False(ToposolidWritePlanner.TryPlan(new[] { Package("a") with { MetersPerUnit = 0.0 } }, Tolerance, out _, out var errors));

        Assert.Contains(errors, error => error.Contains("Meters Per Unit", StringComparison.Ordinal));
    }

    [Fact]
    public void TryPlan_OpenOrUnreadableProfile_IsRejected()
    {
        var open = new[] { new Point3d(0, 0, 0), new Point3d(10, 0, 0), new Point3d(10, 10, 0), new Point3d(0, 10, 0) };

        Assert.False(ToposolidWritePlanner.TryPlan(new[] { Package("a") with { Profiles = new Point3d[]?[] { open } } }, Tolerance, out _, out var openErrors));
        Assert.False(ToposolidWritePlanner.TryPlan(new[] { Package("a") with { Profiles = new Point3d[]?[] { null } } }, Tolerance, out _, out var nullErrors));

        Assert.Contains(openErrors, error => error.Contains("not a closed polyline", StringComparison.Ordinal));
        Assert.Contains(nullErrors, error => error.Contains("not a closed polyline", StringComparison.Ordinal));
    }

    [Fact]
    public void TryPlan_SegmentBelowShortCurveTolerance_IsRejected()
    {
        var sliver = Square(10.0).ToList();
        sliver.Insert(2, new Point3d(10.0, 10.0 - 0.0005, 0.0)); // 0.5 mm, below Revit's ~0.78 mm.

        Assert.False(ToposolidWritePlanner.TryPlan(new[] { Package("a") with { Profiles = new Point3d[]?[] { sliver.ToArray() } } }, Tolerance, out _, out var errors));

        Assert.Contains(errors, error => error.Contains("short-curve tolerance", StringComparison.Ordinal));
    }

    [Fact]
    public void TryPlan_TooFewPoints_IsRejected()
    {
        PreparationInput input = Package("a") with { Points = new[] { new Point3d(1, 1, 1), new Point3d(2, 2, 2) } };

        Assert.False(ToposolidWritePlanner.TryPlan(new[] { input }, Tolerance, out _, out var errors));

        Assert.Contains(errors, error => error.Contains("three elevation points", StringComparison.Ordinal));
    }

    [Fact]
    public void TryPlan_SameZoneKeyOnTwoTerrains_GivesEachHostItsOwnSubdivisionIdentity()
    {
        var zone = new SubdivisionInput("zone-1", "Lawn", "fp-zone", new Point3d[]?[] { Square(4.0) });
        PreparationInput first = Package("north") with { Subdivisions = new[] { zone } };
        PreparationInput second = Package("south") with { Subdivisions = new[] { zone } };

        Assert.True(ToposolidWritePlanner.TryPlan(new[] { first, second }, Tolerance, out var plans, out var errors), string.Join("; ", errors));

        Assert.Equal("north::zone-1", plans[0].Subdivisions[0].IdentityKey);
        Assert.Equal("south::zone-1", plans[1].Subdivisions[0].IdentityKey);
    }

    [Fact]
    public void TryPlan_RepeatedSubdivisionKeyWithinOneTerrain_IsRejected()
    {
        var zone = new SubdivisionInput("zone-1", "Lawn", "fp", new Point3d[]?[] { Square(4.0) });
        PreparationInput input = Package("a") with { Subdivisions = new[] { zone, zone } };

        Assert.False(ToposolidWritePlanner.TryPlan(new[] { input }, Tolerance, out _, out var errors));

        Assert.Contains(errors, error => error.Contains("subdivision key", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, null, "f1", nameof(ToposolidWriteAction.Create))]
    [InlineData(true, "f1", "f1", nameof(ToposolidWriteAction.Keep))]
    [InlineData(true, "f0", "f1", nameof(ToposolidWriteAction.Replace))]
    [InlineData(true, null, "f1", nameof(ToposolidWriteAction.Replace))]
    public void Decide_ComparesFingerprints(bool exists, string? existing, string current, string expected)
    {
        Assert.Equal(expected, ToposolidWritePlanner.Decide(exists, existing, current).ToString());
    }

    [Fact]
    public void DecideSubdivision_ReplacedHost_ReplacesAnUnchangedSubdivision()
    {
        Assert.Equal(ToposolidWriteAction.Keep, ToposolidWritePlanner.DecideSubdivision(true, "f", onCurrentHost: true, "f"));
        Assert.Equal(ToposolidWriteAction.Replace, ToposolidWritePlanner.DecideSubdivision(true, "f", onCurrentHost: false, "f"));
        Assert.Equal(ToposolidWriteAction.Create, ToposolidWritePlanner.DecideSubdivision(false, null, onCurrentHost: false, "f"));
    }

    private static PreparationInput Package(string key) => new(
        key,
        "Terrain",
        "fp-" + key,
        1.0,
        new Point3d[]?[] { Square(10.0) },
        new[] { new Point3d(1, 1, 2), new Point3d(9, 1, 3), new Point3d(5, 9, 4) },
        Array.Empty<SubdivisionInput>());

    private static Point3d[] Square(double size) => new[]
    {
        new Point3d(0, 0, 0),
        new Point3d(size, 0, 0),
        new Point3d(size, size, 0),
        new Point3d(0, size, 0),
        new Point3d(0, 0, 0)
    };
}
