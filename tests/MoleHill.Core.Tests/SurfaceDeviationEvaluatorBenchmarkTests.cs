using System.Diagnostics;
using MoleHill.Core.Processing;
using Xunit;
using Xunit.Abstractions;

namespace MoleHill.Core.Tests;

public class SurfaceDeviationEvaluatorBenchmarkTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Performance")]
    public void Evaluate_RollingGrid_ReportsOverlayTimingAndAllocation()
    {
        if (!PerformanceLane.ShouldRun(output, "surface-deviation")) return;

        // 300 x 300 cells gives the default first release-scale checkpoint: 180,000 faces.
        // Override for the larger plan checkpoints, e.g. MOLEHILL_SURFACE_GRID_SIZE=707 (~1M faces).
        int gridSize = ReadGridSize();
        BuildGrid(gridSize, oppositeDiagonals: false, out double[] referenceVertices, out int[] referenceFaces);
        BuildGrid(gridSize, oppositeDiagonals: true, out double[] candidateVertices, out int[] candidateFaces);

        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var stopwatch = Stopwatch.StartNew();
        SurfaceDeviationEvaluator.Result result = SurfaceDeviationEvaluator.Evaluate(
            referenceVertices, referenceFaces, candidateVertices, candidateFaces);
        stopwatch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

        output.WriteLine(
            $"Rolling overlay: {referenceVertices.Length / 3:N0} vertices / {referenceFaces.Length / 3:N0} faces per mesh; " +
            $"pairs={result.TrianglePairsTested:N0}; time={stopwatch.Elapsed.TotalMilliseconds:0.0}ms; " +
            $"allocated={allocated:N0}; error={result.MaximumDeviation:G17}; coverage={result.HasEqualDomain}");

        Assert.True(result.IsValid, result.FailureReason);
        Assert.True(result.HasEqualDomain, result.FailureReason);
        Assert.True(result.MaximumDeviation > 0.0);
    }

    private static int ReadGridSize()
    {
        string? value = Environment.GetEnvironmentVariable("MOLEHILL_SURFACE_GRID_SIZE");
        return int.TryParse(value, out int parsed) && parsed >= 2 ? parsed : 300;
    }

    private static void BuildGrid(int gridSize, bool oppositeDiagonals, out double[] vertices, out int[] faces)
    {
        int row = gridSize + 1;
        vertices = new double[row * row * 3];
        for (int y = 0; y <= gridSize; y++)
        {
            for (int x = 0; x <= gridSize; x++)
            {
                int vertex = (y * row) + x;
                vertices[vertex * 3] = x;
                vertices[vertex * 3 + 1] = y;
                vertices[vertex * 3 + 2] = Math.Sin(x * 0.08) + Math.Cos(y * 0.06) + (Math.Sin((x + y) * 0.11) * 0.4);
            }
        }

        faces = new int[gridSize * gridSize * 6];
        int next = 0;
        for (int y = 0; y < gridSize; y++)
        {
            for (int x = 0; x < gridSize; x++)
            {
                int a = (y * row) + x;
                int b = a + 1;
                int d = a + row;
                int c = d + 1;
                if (oppositeDiagonals)
                {
                    faces[next++] = a; faces[next++] = b; faces[next++] = d;
                    faces[next++] = b; faces[next++] = c; faces[next++] = d;
                }
                else
                {
                    faces[next++] = a; faces[next++] = b; faces[next++] = c;
                    faces[next++] = a; faces[next++] = c; faces[next++] = d;
                }
            }
        }
    }
}
