using MoleHill.Core.Engine;
using MoleHill.Core.Processing;
using Xunit;
using Xunit.Abstractions;

namespace MoleHill.Core.Tests;

/// <summary>
/// A breakline is stationed to the data around it. Measured before the change, a five-point ridge
/// breakline between two contours 1 m away (0.1 m sampling) kept its 5 vertices, each fanning to up to
/// 135 contour vertices, worst angle 0.27°, crossing edges up to 5.4 m.
/// </summary>
public class TerrainConstraintPreprocessorNeighbourTests
{
    private const double Tolerance = 0.01;
    private readonly ITestOutputHelper _output;

    public TerrainConstraintPreprocessorNeighbourTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(1.0, 0.10, 5)]
    [InlineData(0.3, 0.05, 6)]
    [InlineData(3.0, 0.25, 4)]
    public void Process_SparseBreaklineBetweenDenseContours_StationsToTheGap(double gap, double contourSpacing, int breaklinePoints)
    {
        (double[] breakline, double[][] contours) = ContourFlankedRidge(gap, contourSpacing, breaklinePoints);

        List<double[]> result = TerrainConstraintPreprocessor.Process(new[] { breakline }, contours, Tolerance);

        double maxGap = MaxStationGap(result[0]);
        Assert.InRange(maxGap, gap * 0.5, gap * 1.6);

        // Baseline: the same scene with the breakline as authored, which is what shipped before.
        TinQuality raw = MeasureAlongLine(new[] { breakline }.Concat(result.Skip(1)).ToList(), Array.Empty<double>());
        TinQuality quality = MeasureAlongLine(result, Array.Empty<double>());
        _output.WriteLine($"gap {gap}: {result[0].Length / 3} stations, max gap {maxGap:0.###}; raw {raw}; stationed {quality}");
        Assert.True(quality.MaxValence * 2 <= raw.MaxValence, $"raw {raw}; stationed {quality}");
        Assert.True(quality.MinAngle > raw.MinAngle, $"raw {raw}; stationed {quality}");
        Assert.True(quality.LongestCrossingEdge <= gap * 2.5, quality.ToString());
        Assert.Equal(0.0, quality.MaxZDeviation, 9);
    }

    [Fact]
    public void Process_TwoAndFourPointBreaklinesOnTheSameLine_AreStationedAlike()
    {
        (double[] four, double[][] contours) = ContourFlankedRidge(1.0, 0.1, 4);
        double[] two = { four[0], four[1], four[2], four[^3], four[^2], four[^1] };

        int fourStations = TerrainConstraintPreprocessor.Process(new[] { four }, contours, Tolerance)[0].Length / 3;
        int twoStations = TerrainConstraintPreprocessor.Process(new[] { two }, contours, Tolerance)[0].Length / 3;

        // Formerly 4 vs 361: one borrowed the contour median, the other kept its own.
        Assert.InRange(twoStations, fourStations - 3, fourStations + 3);
    }

    [Fact]
    public void Process_SparseBreaklineThroughDenseSpotSamples_IsStationed()
    {
        var spots = new List<double>();
        for (double x = 0; x <= 40; x += 0.5)
            for (double y = -5; y <= 5; y += 0.5)
                spots.AddRange(new[] { x, y + 0.25, 0.0 });
        double[] breakline = { 2, 0, 3, 38, 0, 5 };

        List<double[]> result = TerrainConstraintPreprocessor.Process(
            new[] { breakline }, Array.Empty<double[]>(), Tolerance, spots.ToArray());

        Assert.InRange(MaxStationGap(result[0]), 0.2, 0.8);
        TinQuality raw = MeasureAlongLine(new[] { breakline }, spots.ToArray());
        TinQuality quality = MeasureAlongLine(result, spots.ToArray());
        _output.WriteLine($"spots: raw {raw}; stationed {quality}");
        Assert.True(quality.MaxValence * 2 <= raw.MaxValence, $"raw {raw}; stationed {quality}");
        Assert.Equal(0.0, quality.MaxZDeviation, 9);
    }

    [Fact]
    public void Process_BreaklineInAVoid_IsNotWorsened()
    {
        // The trailer-ramp shape: three sparse rows, two short interior breaklines in a 26 m gap. Blanket
        // densification of the rows raised the hub valence 7 -> 12 and halved the median angle here.
        double[][] breaklines =
        {
            Row(-14.376288), Row(12.29018), Row(30.238869),
            new[] { 11.0, 0, 2.075, 11.0, 8, 2.075 },
            new[] { 17.703857, 8, 2.4772315, 17.703857, 0, 2.4772315 }
        };

        List<double[]> processed = TerrainConstraintPreprocessor.Process(breaklines, Array.Empty<double[]>(), Tolerance);
        TinQuality raw = Measure(breaklines);
        TinQuality stationed = Measure(processed);
        _output.WriteLine($"raw {raw}; stationed {stationed}");

        Assert.True(stationed.MaxValence <= raw.MaxValence, $"raw {raw}; stationed {stationed}");
        Assert.True(stationed.MinAngle >= raw.MinAngle - 1e-6, $"raw {raw}; stationed {stationed}");

        static double[] Row(double y) =>
            new[] { 0, y, 2.95, 2.0114703, y, 2.8695412, 11.059209, y, 2.6433477, 26, y, 2.2698278 };
    }

    [Fact]
    public void Process_BreaklineCrossingDenseContours_StaysBounded()
    {
        var contours = new List<double[]>();
        for (int c = 0; c < 10; c++)
        {
            var contour = new List<double>();
            for (double y = -20; y <= 20; y += 0.1)
                contour.AddRange(new[] { c * 4.0 + 1.0, y, c * 0.5 });
            contours.Add(contour.ToArray());
        }

        double[] breakline = { 0, 0, 0, 40, 0, 5 };
        double[] stationed = TerrainConstraintPreprocessor.Process(new[] { breakline }, contours, Tolerance)[0];

        // Clearance to contours 4 m apart is at most 2 m; each crossing may add a short run near it.
        Assert.InRange(stationed.Length / 3, 20, 200);
    }

    [Fact]
    public void Process_LargeSiteContoursWithOneBreakline_LeavesContoursUntouched()
    {
        var contours = new List<double[]>();
        for (int c = 0; c < 100; c++)
        {
            var contour = new double[200 * 3];
            for (int i = 0; i < 200; i++)
            {
                contour[i * 3] = i * 50.0;
                contour[i * 3 + 1] = c * 50.0 + 25.0;
                contour[i * 3 + 2] = c;
            }
            contours.Add(contour);
        }

        double[] breakline = { 0, 0, 0, 9950, 4950, 10 };
        List<double[]> result = TerrainConstraintPreprocessor.Process(new[] { breakline }, contours, 0.0125);

        Assert.Equal(20_000, result.Skip(1).Sum(static c => c.Length / 3));
        Assert.InRange(result[0].Length / 3, 100, 20_000);
    }

    private static (double[] Breakline, double[][] Contours) ContourFlankedRidge(double gap, double spacing, int breaklinePoints)
    {
        const double length = 40.0;
        var a = new List<double>();
        var b = new List<double>();
        for (double x = 0; x <= length + 1e-9; x += spacing)
        {
            double wiggle = 0.05 * Math.Sin(x * 3.1);
            a.AddRange(new[] { x, -gap + wiggle, 0.0 });
            b.AddRange(new[] { x, gap + wiggle, 0.0 });
        }

        var breakline = new double[breaklinePoints * 3];
        for (int i = 0; i < breaklinePoints; i++)
        {
            double t = i / (double)(breaklinePoints - 1);
            breakline[i * 3] = 2.0 + t * (length - 4.0);
            breakline[i * 3 + 2] = 3.0 + 2.0 * t;
        }

        return (breakline, new[] { a.ToArray(), b.ToArray() });
    }

    private static double MaxStationGap(double[] polyline)
    {
        double max = 0;
        for (int i = 1; i < polyline.Length / 3; i++)
        {
            double dx = polyline[i * 3] - polyline[i * 3 - 3], dy = polyline[i * 3 + 1] - polyline[i * 3 - 2];
            max = Math.Max(max, Math.Sqrt((dx * dx) + (dy * dy)));
        }
        return max;
    }

    private readonly record struct TinQuality(int MaxValence, double MinAngle, double LongestCrossingEdge, double MaxZDeviation)
    {
        public override string ToString() =>
            $"max valence {MaxValence}, min angle {MinAngle:0.00}°, longest crossing edge {LongestCrossingEdge:0.00}, z dev {MaxZDeviation:0.######}";
    }

    private static TinResult Triangulate(IReadOnlyList<double[]> polylines, double[] spots)
    {
        var data = BreaklineDiscretizer.Process(polylines);
        var merged = PointCloudProcessor.Merge(spots, spots.Length / 3, data, Tolerance);
        TinResult? result = new TinEngine().Build(merged.XyCoords, merged.ZValues, merged.Segments,
            QualitySettings.None, out string? error, useConvexHull: true, includeEdgeTopology: false);
        Assert.True(result != null, error);
        return result!;
    }

    /// <summary>Whole-mesh valence and min angle.</summary>
    private static TinQuality Measure(IReadOnlyList<double[]> polylines)
    {
        TinResult r = Triangulate(polylines, Array.Empty<double>());
        var valence = new int[r.VertexCount];
        double minAngle = 180;
        for (int f = 0; f < r.FaceCount; f++)
        {
            for (int k = 0; k < 3; k++) valence[r.Faces[f * 3 + k]]++;
            minAngle = Math.Min(minAngle, MinAngle(r, f));
        }
        return new TinQuality(valence.Max(), minAngle, 0, 0);
    }

    /// <summary>Faces touching the y = 0 breakline: valence, crossing-edge length and Z along it.</summary>
    private static TinQuality MeasureAlongLine(IReadOnlyList<double[]> polylines, double[] spots)
    {
        TinResult r = Triangulate(polylines, spots);
        var onLine = new bool[r.VertexCount];
        for (int i = 0; i < r.VertexCount; i++) onLine[i] = Math.Abs(r.Vertices[i * 3 + 1]) < 1e-9;
        var valence = new int[r.VertexCount];
        double minAngle = 180, longest = 0;
        for (int f = 0; f < r.FaceCount; f++)
        {
            int[] v = { r.Faces[f * 3], r.Faces[f * 3 + 1], r.Faces[f * 3 + 2] };
            if (!onLine[v[0]] && !onLine[v[1]] && !onLine[v[2]]) continue;
            foreach (int k in v) if (onLine[k]) valence[k]++;
            minAngle = Math.Min(minAngle, MinAngle(r, f));
            for (int e = 0; e < 3; e++)
            {
                int p = v[e], q = v[(e + 1) % 3];
                if (onLine[p] == onLine[q]) continue;
                double dx = r.Vertices[p * 3] - r.Vertices[q * 3], dy = r.Vertices[p * 3 + 1] - r.Vertices[q * 3 + 1];
                longest = Math.Max(longest, Math.Sqrt((dx * dx) + (dy * dy)));
            }
        }

        double maxDeviation = 0;
        for (int i = 0; i < r.VertexCount; i++)
        {
            double x = r.Vertices[i * 3];
            if (onLine[i] && x >= 2.0 && x <= 38.0)
                maxDeviation = Math.Max(maxDeviation, Math.Abs(r.Vertices[i * 3 + 2] - (3.0 + 2.0 * ((x - 2.0) / 36.0))));
        }

        return new TinQuality(valence.Max(), minAngle, longest, maxDeviation);
    }

    private static double MinAngle(TinResult r, int f)
    {
        double best = 180;
        for (int i = 0; i < 3; i++)
        {
            int a = r.Faces[f * 3 + i], b = r.Faces[f * 3 + (i + 1) % 3], c = r.Faces[f * 3 + (i + 2) % 3];
            double ux = r.Vertices[b * 3] - r.Vertices[a * 3], uy = r.Vertices[b * 3 + 1] - r.Vertices[a * 3 + 1];
            double wx = r.Vertices[c * 3] - r.Vertices[a * 3], wy = r.Vertices[c * 3 + 1] - r.Vertices[a * 3 + 1];
            best = Math.Min(best, Math.Abs(Math.Atan2((ux * wy) - (uy * wx), (ux * wx) + (uy * wy))) * 180 / Math.PI);
        }
        return best;
    }
}
