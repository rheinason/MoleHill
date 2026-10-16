using MoleHill.Core.Engine;
using MoleHill.Core.Routing;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// The case reported live on 2026-10-16: a climb steeper than the limit allows in a straight line, so
/// the route has to be longer than the distance between its ends. The search, blind to direction, gained
/// that length by zig-zagging a cell or two at a time — 3 m legs on a 4 m road — and rounding then broke
/// the grade and left 34–74 corners sharp. With turns priced it switches back in long legs.
/// </summary>
public sealed class RouteSwitchbackTests
{
    private const double MaxGrade = 0.10;

    [Fact]
    public void Suggest_ClimbSteeperThanTheLimit_SwitchesBackInLongLegs()
    {
        // An 18% hillside, ends 45 m apart in height and 250 m in plan: at 10% the route needs 450 m.
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

        var request = new RouteRequest
        {
            Points = new[] { (160.0, 30.0), (160.0, 280.0) },
            MaxGrade = MaxGrade,
            CorridorWidth = 4.0,
            MinimumRadius = 10.0,
            AlternativeCount = 3,
            ProfileTolerance = 0.1,
        };
        (double minX, double minY, double maxX, double maxY) = RouteSuggester.SearchDomain(request.Points);
        double cell = RouteHeightGrid.AutomaticCellSize(maxX - minX, maxY - minY, RouteSuggester.DirectSpan(request.Points));
        RouteHeightGrid grid = RouteHeightGrid.FromMesh(IndexedTriMesh.FromArrays(vertices, faces), minX, minY, maxX, maxY, cell);

        RouteSuggestionResult result = RouteSuggester.Suggest(grid, request);

        Assert.True(result.Succeeded, result.Failure.ToString());
        foreach (RouteAlternative route in result.Alternatives)
        {
            Assert.True(route.Metrics.PlanLength >= 450.0 * (1 - 1e-6), $"{route.Metrics.PlanLength:F0} m cannot climb 45 m at 10%");
            Assert.True(route.Metrics.MaxGrade <= MaxGrade * (1 + 1e-9));

            var straights = route.Spans.Where(span => !span.IsArc)
                .Select(span => Math.Sqrt(Math.Pow(span.EndX - span.StartX, 2) + Math.Pow(span.EndY - span.StartY, 2)))
                .OrderBy(length => length)
                .ToList();
            int bends = 0;
            bool inArc = false;
            foreach (RouteSpan span in route.Spans)
            {
                bends += span.IsArc && !inArc ? 1 : 0;
                inArc = span.IsArc;
            }

            // The staircase was hundreds of 1–3 m straights; switchbacks are a handful of long legs.
            Assert.True(straights.Count <= 40, $"{straights.Count} straights");
            Assert.True(straights[straights.Count / 2] >= 10.0, $"median straight {straights[straights.Count / 2]:F1} m");
            Assert.True(bends <= 20, $"{bends} bends");
        }
    }
}
