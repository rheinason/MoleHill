using MoleHill.Core.Engine;
using MoleHill.Core.Routing;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// The case reported live on 2026-10-16: on rolling ground at the command's defaults (automatic cell,
/// 10 m minimum radius, 10% grade) the routes came back as sub-metre segments with corners of 0.1–2 m.
/// A 240 m square of fractal ground, 16 m of relief, surveyed at 2 m.
/// </summary>
public sealed class RouteHillySiteTests
{
    private const double MaxGrade = 0.10;
    private const double MinimumRadius = 10.0;

    [Fact]
    public void Suggest_HillySiteAtDefaults_HonoursTheRadiusWithFewBends()
    {
        (RouteHeightGrid grid, RouteRequest request) = Site();

        RouteSuggestionResult result = RouteSuggester.Suggest(grid, request);

        Assert.True(result.Succeeded, result.Failure.ToString());
        Assert.NotEmpty(result.Alternatives);
        foreach (RouteAlternative route in result.Alternatives)
        {
            Assert.Equal(0, route.Fillet.ReducedCorners);
            Assert.True(route.Fillet.SmallestRadius >= MinimumRadius - 1e-6, $"smallest radius {route.Fillet.SmallestRadius}");
            Assert.True(FineMaxGrade(route.Spans) <= MaxGrade * (1 + 1e-6), "the curve is steeper than the limit somewhere");

            int bends = 0;
            bool inArc = false;
            var straights = new List<double>();
            foreach (RouteSpan span in route.Spans)
            {
                if (span.IsArc)
                {
                    bends += inArc ? 0 : 1;
                    inArc = true;
                    continue;
                }

                inArc = false;
                straights.Add(Math.Sqrt(Math.Pow(span.EndX - span.StartX, 2) + Math.Pow(span.EndY - span.StartY, 2)));
            }

            straights.Sort();
            double perHundred = bends / route.Metrics.PlanLength * 100.0;
            Assert.True(perHundred <= 10.0, $"{bends} bends in {route.Metrics.PlanLength:F0} m");
            Assert.True(straights[straights.Count / 2] >= 2.0, $"median straight {straights[straights.Count / 2]:F2} m");
        }
    }

    private static (RouteHeightGrid Grid, RouteRequest Request) Site()
    {
        const int side = 121;
        const double spacing = 2.0;
        var vertices = new double[side * side * 3];
        for (int r = 0; r < side; r++)
        {
            for (int c = 0; c < side; c++)
            {
                int v = (r * side) + c;
                vertices[v * 3] = c * spacing;
                vertices[(v * 3) + 1] = r * spacing;
                vertices[(v * 3) + 2] = Fbm(c * spacing, r * spacing);
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
            Points = new[] { (20.0, 30.0), (210.0, 200.0) },
            MaxGrade = MaxGrade,
            CorridorWidth = 4.0,
            MinimumRadius = MinimumRadius,
            AlternativeCount = 3,
            // What the command passes: 0.1 m in a metre model.
            ProfileTolerance = 0.1,
        };
        (double minX, double minY, double maxX, double maxY) = RouteSuggester.SearchDomain(request.Points);
        double cell = RouteHeightGrid.AutomaticCellSize(maxX - minX, maxY - minY, RouteSuggester.DirectSpan(request.Points));
        return (RouteHeightGrid.FromMesh(IndexedTriMesh.FromArrays(vertices, faces), minX, minY, maxX, maxY, cell), request);
    }

    private static double FineMaxGrade(IReadOnlyList<RouteSpan> spans)
    {
        double steepest = 0.0;
        foreach (RouteSpan span in spans)
        {
            (double X, double Y, double Z) previous = span.Evaluate(0.0);
            for (int k = 1; k <= 64; k++)
            {
                (double X, double Y, double Z) point = span.Evaluate(k / 64.0);
                double run = Math.Sqrt(Math.Pow(point.X - previous.X, 2) + Math.Pow(point.Y - previous.Y, 2));
                if (run > 1e-6)
                    steepest = Math.Max(steepest, Math.Abs(point.Z - previous.Z) / run);
                previous = point;
            }
        }

        return steepest;
    }

    // Five octaves of value noise: deterministic, no library.
    private static double Fbm(double x, double y)
    {
        double sum = 0.0, amplitude = 16.0, frequency = 1.0 / 80.0;
        for (int octave = 0; octave < 5; octave++)
        {
            sum += amplitude * Noise(x * frequency, y * frequency, octave);
            amplitude *= 0.5;
            frequency *= 2.0;
        }

        return sum;
    }

    private static double Noise(double x, double y, int seed)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        double sx = fx * fx * (3 - (2 * fx)), sy = fy * fy * (3 - (2 * fy));
        double a = Hash(x0, y0, seed), b = Hash(x0 + 1, y0, seed), c = Hash(x0, y0 + 1, seed), d = Hash(x0 + 1, y0 + 1, seed);
        return a + ((b - a) * sx) + ((c - a) * sy) + ((a - b - c + d) * sx * sy);
    }

    private static double Hash(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)((x * 374761393) + (y * 668265263) + (seed * 2147483647));
            h = (h ^ (h >> 13)) * 1274126177;
            return ((h ^ (h >> 16)) & 0xFFFFFF) / (double)0xFFFFFF;
        }
    }
}
