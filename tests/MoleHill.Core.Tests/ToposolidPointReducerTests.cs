using MoleHill.Core.Interop;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class ToposolidPointReducerTests
{
    [Fact]
    public void TryReduce_GridSurface_IsDeterministicAndRespectsBudget()
    {
        double[] source = Grid(41, 41, (x, y) => Math.Sin(x * 0.12) + Math.Cos(y * 0.08));
        double[] critical =
        {
            0.0, 0.0, 1.0,
            40.0, 0.0, Math.Sin(4.8) + 1.0,
            40.0, 40.0, Math.Sin(4.8) + Math.Cos(3.2),
            0.0, 40.0, Math.Cos(3.2)
        };

        Assert.True(ToposolidPointReducer.TryReduce(
            source, source.Length / 3, critical, critical.Length / 3, 100, 0.01, 0.001,
            out var first, out string? firstError), firstError);
        Assert.True(ToposolidPointReducer.TryReduce(
            source, source.Length / 3, critical, critical.Length / 3, 100, 0.01, 0.001,
            out var second, out string? secondError), secondError);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.InRange(first!.PointCount, 3, 100);
        Assert.Equal(first.Vertices, second!.Vertices);
        Assert.Equal(first.MaximumMeasuredVerticalError, second.MaximumMeasuredVerticalError);
        foreach (double[] point in Chunks(critical))
            Assert.Contains(Chunks(first.Vertices), candidate => candidate.SequenceEqual(point));
    }

    [Fact]
    public void TryReduce_CriticalSetExceedsBudget_ReturnsFailure()
    {
        double[] source = Grid(3, 3, (x, y) => x + y);

        bool success = ToposolidPointReducer.TryReduce(
            source, 9, source, 9, 8, 0.1, 0.001, out _, out string? error);

        Assert.False(success);
        Assert.Contains("budget", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryReduce_ConflictingCriticalElevations_ReturnsFailure()
    {
        double[] source = Grid(2, 2, (x, y) => x + y);
        double[] critical = { 0.0, 0.0, 0.0, 0.0, 0.0, 2.0, 1.0, 0.0, 1.0 };

        bool success = ToposolidPointReducer.TryReduce(
            source, 4, critical, 3, 10, 0.1, 0.001, out _, out string? error);

        Assert.False(success);
        Assert.Contains("conflicting elevations", error, StringComparison.OrdinalIgnoreCase);
    }

    private static double[] Grid(int xCount, int yCount, Func<double, double, double> elevation)
    {
        var result = new double[xCount * yCount * 3];
        int offset = 0;
        for (int y = 0; y < yCount; y++)
        {
            for (int x = 0; x < xCount; x++)
            {
                result[offset++] = x;
                result[offset++] = y;
                result[offset++] = elevation(x, y);
            }
        }

        return result;
    }

    private static IReadOnlyList<double[]> Chunks(double[] values)
    {
        var result = new List<double[]>(values.Length / 3);
        for (int index = 0; index < values.Length; index += 3)
            result.Add(new[] { values[index], values[index + 1], values[index + 2] });
        return result;
    }
}
