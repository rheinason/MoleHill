using MoleHill.Core.Analysis;
using Xunit;
using Kind = MoleHill.Core.Analysis.RouteRunAnalyzer.RunKind;
using Failure = MoleHill.Core.Analysis.RouteRunAnalyzer.RunFailure;

namespace MoleHill.Core.Tests;

public class RouteRunAnalyzerTests
{
    /// <summary>
    /// A 0.5 m grid strip 2 m wide along +x, with every vertex at <paramref name="profile"/>(x). Breaks in
    /// the profile sit on grid lines, so the surface is exactly the profile along the route.
    /// </summary>
    private static MeshHeightProjector Strip(double length, Func<double, double> profile)
    {
        int nx = (int)Math.Round(length / 0.5);
        const int ny = 4;
        var vertices = new List<double>();
        for (int j = 0; j <= ny; j++)
            for (int i = 0; i <= nx; i++)
                vertices.AddRange(new[] { i * 0.5, j * 0.5, profile(i * 0.5) });

        var faces = new List<int>();
        for (int j = 0; j < ny; j++)
        {
            for (int i = 0; i < nx; i++)
            {
                int a = (j * (nx + 1)) + i;
                int b = a + 1;
                int c = a + nx + 1;
                int d = c + 1;
                faces.AddRange(new[] { a, b, d, a, d, c });
            }
        }

        return new MeshHeightProjector(vertices.ToArray(), vertices.Count / 3, faces.ToArray(), faces.Count / 3);
    }

    /// <summary>A piecewise-linear profile through (x, z) knots.</summary>
    private static Func<double, double> Profile(params (double X, double Z)[] knots) => x =>
    {
        for (int i = 1; i < knots.Length; i++)
        {
            if (x <= knots[i].X)
                return knots[i - 1].Z + ((knots[i].Z - knots[i - 1].Z) * (x - knots[i - 1].X) / (knots[i].X - knots[i - 1].X));
        }

        return knots[^1].Z;
    };

    /// <summary>ADA 2010: landings 1:48 and ≥ 1525 mm (§405.7), walks 1:20, ramp runs rise ≤ 760 mm (§405.6).</summary>
    private static readonly RouteRunAnalyzer.Options Ada = new()
    {
        LandingMaxRatio = 1.0 / 48.0,
        WalkMaxRatio = 1.0 / 20.0,
        LandingMinLength = 1.525,
        RampMaxRise = 0.76,
        StationSpacing = 0.1,
        SmoothingLength = 0.5,
        Tolerance = 0.001,
    };

    /// <summary>Approved Document M Vol 2: landings 1:60 and ≥ 1.5 m, 500 mm rise, Table 1 goings.</summary>
    private static readonly RouteRunAnalyzer.Options DocM = new()
    {
        LandingMaxRatio = 1.0 / 60.0,
        WalkMaxRatio = 1.0 / 20.0,
        LandingMinLength = 1.5,
        WalkMaxRise = 0.5,
        RampMaxRise = 0.5,
        RampGoingLimits = new[]
        {
            new RouteRunAnalyzer.GoingLimit(1.0 / 20.0, 10.0),
            new RouteRunAnalyzer.GoingLimit(1.0 / 15.0, 5.0),
            new RouteRunAnalyzer.GoingLimit(1.0 / 12.0, 2.0),
        },
        InterpolateGoing = true,
        StationSpacing = 0.1,
        SmoothingLength = 0.5,
        Tolerance = 0.001,
    };

    private static IReadOnlyList<RouteRunAnalyzer.Run> Analyze(MeshHeightProjector strip, double length, RouteRunAnalyzer.Options options) =>
        RouteRunAnalyzer.Analyze(strip, new[] { new[] { 0.0, 1.0, length, 1.0 } }, options);

    [Theory]
    [InlineData(1.0 / 14.0, true, 4.0)]
    [InlineData(1.0 / 19.0, true, 9.0)]
    [InlineData(1.0 / 14.0, false, 2.0)]
    [InlineData(1.0 / 30.0, true, 10.0)]
    public void AllowedGoing_ReproducesApprovedDocumentMsWorkedExamples(double ratio, bool interpolate, double expected)
    {
        Assert.Equal(expected, RouteRunAnalyzer.AllowedGoing(ratio, DocM.RampGoingLimits, interpolate), 9);
    }

    [Fact]
    public void Analyze_RampRisingTooFarWithoutALanding_FailsOnRise()
    {
        // 1:12.5 over 10 m: 800 mm of rise with no landing, against ADA's 760 mm.
        var strip = Strip(12, Profile((0, 0), (1, 0), (11, 0.8), (12, 0.8)));

        var ramp = Assert.Single(Analyze(strip, 12, Ada), run => run.Kind == Kind.Ramp);

        Assert.Equal(0.8, ramp.Rise, 3);
        Assert.True(ramp.Failures.HasFlag(Failure.RiseExceeded));
    }

    [Fact]
    public void Analyze_SameClimbBrokenByALanding_PassesAndFindsTheLanding()
    {
        // The same 800 mm, as two 400 mm runs either side of a 2 m level landing.
        var strip = Strip(16, Profile((0, 0), (1, 0), (6, 0.4), (8, 0.4), (13, 0.8), (16, 0.8)));

        var runs = Analyze(strip, 16, Ada);

        var ramps = runs.Where(run => run.Kind == Kind.Ramp).ToList();
        Assert.Equal(2, ramps.Count);
        Assert.All(ramps, ramp => Assert.Equal(Failure.None, ramp.Failures));
        Assert.All(ramps, ramp => Assert.Equal(0.4, ramp.Rise, 2));
        var middle = Assert.Single(runs, run => run.Kind == Kind.Landing && run.StartDistance > 3 && run.EndDistance < 12);
        Assert.Equal(2.0, middle.Going, 1);
    }

    [Fact]
    public void Analyze_LandingShorterThanTheMinimum_DoesNotBreakTheRun()
    {
        // A 1 m flat is not a landing under ADA's 1525 mm, so the 800 mm climb is still one run.
        var strip = Strip(15, Profile((0, 0), (1, 0), (6, 0.4), (7, 0.4), (12, 0.8), (15, 0.8)));

        var runs = Analyze(strip, 15, Ada);

        var ramp = Assert.Single(runs, run => run.Kind == Kind.Ramp);
        Assert.Equal(0.8, ramp.Rise, 2);
        Assert.True(ramp.Failures.HasFlag(Failure.RiseExceeded));
        Assert.DoesNotContain(runs, run => run.Kind == Kind.Landing && run.StartDistance > 3 && run.EndDistance < 12);
    }

    [Fact]
    public void Analyze_RampLongerThanItsGradientAllows_FailsOnGoing()
    {
        // 1:15 over 6 m rises 400 mm, within Doc M's 500 mm, but Table 1 allows a 1:15 flight only 5 m.
        var strip = Strip(9, Profile((0, 0), (1.5, 0), (7.5, 0.4), (9, 0.4)));

        var ramp = Assert.Single(Analyze(strip, 9, DocM), run => run.Kind == Kind.Ramp);

        Assert.Equal(Failure.GoingExceeded, ramp.Failures);
        Assert.Equal(5.0, ramp.AllowedGoing, 1);
    }

    [Fact]
    public void Analyze_WalkWithRiseLimit_FailsOnlyUnderTheStandardThatHasOne()
    {
        // 1:25 over 15 m: a walk rising 600 mm. Doc M wants a landing every 500 mm; ADA does not.
        var strip = Strip(18, Profile((0, 0), (1.5, 0), (16.5, 0.6), (18, 0.6)));

        var docM = Assert.Single(Analyze(strip, 18, DocM), run => run.Kind == Kind.Walk);
        var ada = Assert.Single(Analyze(strip, 18, Ada), run => run.Kind == Kind.Walk);

        Assert.True(docM.Failures.HasFlag(Failure.RiseExceeded));
        Assert.Equal(Failure.None, ada.Failures);
    }

    [Fact]
    public void Analyze_LevelRoute_IsOneLandingWithNothingToCheck()
    {
        var strip = Strip(6, _ => 0.0);

        var run = Assert.Single(Analyze(strip, 6, Ada));

        Assert.Equal(Kind.Landing, run.Kind);
        Assert.Equal(Failure.None, run.Failures);
    }
}
