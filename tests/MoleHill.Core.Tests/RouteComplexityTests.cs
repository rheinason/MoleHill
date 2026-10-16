using MoleHill.Core.Engine;
using MoleHill.Core.Routing;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class RouteComplexityTests
{
    [Fact]
    public void Measure_Straight_ScoresZero()
    {
        var spans = new[] { RouteSpan.Line(0, 0, 0, 100, 0, 5) };

        RouteComplexity score = RouteComplexity.Measure(spans, 10.0, 0.1);

        Assert.Equal(0.0, score.Total, 9);
    }

    [Fact]
    public void Measure_OneBendAtTheReferenceRadius_ScoresTwo()
    {
        // Straight, a 90-degree arc of radius 10 (one rational quadratic, weight cos 45°), straight.
        double w = Math.Cos(Math.PI / 4);
        var spans = new[]
        {
            RouteSpan.Line(0, 0, 0, 50, 0, 0),
            new RouteSpan(true, 50, 0, 0, 60, 0, 0, 60, 10, 0, w),
            RouteSpan.Line(60, 10, 0, 60, 60, 0),
        };

        RouteComplexity score = RouteComplexity.Measure(spans, 10.0, 0.1);

        Assert.Equal(2.0, score.Plan, 6);
        Assert.Equal(0.0, score.Profile, 9);
    }

    [Fact]
    public void Measure_SharpCorner_CountsAsABend()
    {
        var spans = new[] { RouteSpan.Line(0, 0, 0, 50, 0, 0), RouteSpan.Line(50, 0, 0, 50, 50, 0) };

        Assert.Equal(2.0, RouteComplexity.Measure(spans, 10.0, 0.1).Plan, 9);
    }

    [Fact]
    public void Measure_LevelThenTheLimit_ScoresOneInProfile()
    {
        var spans = new[] { RouteSpan.Line(0, 0, 0, 50, 0, 0), RouteSpan.Line(50, 0, 0, 100, 0, 5) };

        Assert.Equal(1.0, RouteComplexity.Measure(spans, 10.0, 0.1).Profile, 9);
    }

    [Fact]
    public void Suggest_Simplicity_FindsAPlainerRouteThanFollowingTheGround()
    {
        // The 18% hillside of RouteSwitchbackTests: the plainest of the alternatives gets plainer.
        RouteHeightGrid grid = Hillside(out RouteRequest baseRequest);

        RouteSuggestionResult close = RouteSuggester.Suggest(grid, baseRequest);
        RouteSuggestionResult plain = RouteSuggester.Suggest(grid, new RouteRequest
        {
            Points = baseRequest.Points,
            MaxGrade = baseRequest.MaxGrade,
            CorridorWidth = baseRequest.CorridorWidth,
            MinimumRadius = baseRequest.MinimumRadius,
            AlternativeCount = baseRequest.AlternativeCount,
            ProfileTolerance = baseRequest.ProfileTolerance,
            Simplicity = 2.0,
        });

        Assert.True(close.Succeeded && plain.Succeeded);
        double closest = close.Alternatives.Min(route => route.Complexity.Total);
        double plainest = plain.Alternatives.Min(route => route.Complexity.Total);
        Assert.True(plainest <= closest, $"plainest {plainest:F1} at Simplicity 2, {closest:F1} at 0");
        Assert.All(plain.Alternatives, route =>
        {
            Assert.True(route.Metrics.MaxGrade <= 0.10 * (1 + 1e-9));
            Assert.True(route.Fillet.SmallestRadius >= 10.0 * (1 - 1e-6) || route.Fillet.ReducedCorners == route.Fillet.GradeLimitedCorners + route.Fillet.ReversalCorners);
        });
    }

    private static RouteHeightGrid Hillside(out RouteRequest request)
    {
        const int side = 161;
        const double spacing = 2.0;
        var vertices = new double[side * side * 3];
        for (int r = 0; r < side; r++)
        {
            for (int c = 0; c < side; c++)
            {
                int v = (r * side) + c;
                vertices[v * 3] = c * spacing;
                vertices[(v * 3) + 1] = r * spacing;
                vertices[(v * 3) + 2] = 0.18 * r * spacing;
            }
        }

        var faces = new int[(side - 1) * (side - 1) * 6];
        int f = 0;
        for (int r = 0; r < side - 1; r++)
        {
            for (int c = 0; c < side - 1; c++)
            {
                int a = (r * side) + c;
                faces[f++] = a; faces[f++] = a + 1; faces[f++] = a + side + 1;
                faces[f++] = a; faces[f++] = a + side + 1; faces[f++] = a + side;
            }
        }

        request = new RouteRequest
        {
            Points = new[] { (160.0, 30.0), (160.0, 280.0) },
            MaxGrade = 0.10,
            CorridorWidth = 4.0,
            MinimumRadius = 10.0,
            AlternativeCount = 3,
            ProfileTolerance = 0.1,
        };
        (double minX, double minY, double maxX, double maxY) = RouteSuggester.SearchDomain(request.Points);
        double cell = RouteHeightGrid.AutomaticCellSize(maxX - minX, maxY - minY, RouteSuggester.DirectSpan(request.Points));
        return RouteHeightGrid.FromMesh(IndexedTriMesh.FromArrays(vertices, faces), minX, minY, maxX, maxY, cell);
    }
}
