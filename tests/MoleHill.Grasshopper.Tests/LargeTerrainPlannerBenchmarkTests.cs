using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace MoleHill.Grasshopper.Tests;

public class LargeTerrainPlannerBenchmarkTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Performance")]
    public void RetainingWallPlanner_LargeTerrain20260705Benchmark_ReportsTiming()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("MOLEHILL_PERF"), "1", StringComparison.Ordinal))
        {
            output.WriteLine("Set MOLEHILL_PERF=1 to run the retaining-wall planner benchmark.");
            return;
        }

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        var plan = TerrainRetainingWallPlannerLarge20260705CopiedCaseTests.RunCase(buildSolids: false);
        stopwatch.Stop();
        long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();

        output.WriteLine($"Retaining wall planner: {stopwatch.Elapsed.TotalMilliseconds:0.0} ms");
        output.WriteLine($"Allocation delta: {Math.Max(0, allocatedAfter - allocatedBefore):N0} bytes");
        output.WriteLine($"Walls: {plan.Walls.Count:N0}; pair lines: {plan.PairLines.Count:N0}; report entries: {plan.Report.Count:N0}");
        output.WriteLine(
            $"Planner timing: preprocess={plan.Timing.Preprocess.TotalMilliseconds:0.0} ms, " +
            $"pairing={plan.Timing.Pairing.TotalMilliseconds:0.0} ms, " +
            $"interactions={plan.Timing.Interactions.TotalMilliseconds:0.0} ms, " +
            $"walls={plan.Timing.Walls.TotalMilliseconds:0.0} ms, " +
            $"total={plan.Timing.Total.TotalMilliseconds:0.0} ms");

        Assert.True(plan.Walls.Count > 0);
    }
}
