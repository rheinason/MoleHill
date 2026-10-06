using System.Diagnostics;
using MoleHill.Core.Processing;
using Xunit;
using Xunit.Abstractions;

namespace MoleHill.Core.Tests;

public class SurfaceSimplifierBenchmarkTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Performance")]
    public void Simplify_RollingGrid180kFaces_ReportsReductionTimingAndAllocation()
    {
        if (!PerformanceLane.ShouldRun(output, "surface-simplifier")) return;

        int gridSize = ReadGridSize();
        BuildGrid(gridSize, out double[] vertices, out int[] faces);
        RunToleranceBenchmark($"analytic-{gridSize}", vertices, faces, Array.Empty<int>());
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void Simplify_RollingGrid180kFacesWithMandatoryDiagonal_ReportsReductionTimingAndAllocation()
    {
        if (!PerformanceLane.ShouldRun(output, "constrained surface-simplifier")) return;

        int gridSize = ReadGridSize();
        BuildGrid(gridSize, out double[] vertices, out int[] faces);
        int row = gridSize + 1;
        var requiredSegments = new int[gridSize * 2];
        for (int step = 0; step < gridSize; step++)
        {
            requiredSegments[step * 2] = (step * row) + step;
            requiredSegments[step * 2 + 1] = ((step + 1) * row) + step + 1;
        }
        RunToleranceBenchmark($"analytic-{gridSize}-diagonal", vertices, faces, requiredSegments);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void Simplify_RollingGrid180kFacesTo50kVertices_ReportsAchievedErrorAndResources()
    {
        if (!PerformanceLane.ShouldRun(output, "count-mode surface-simplifier")) return;

        int gridSize = ReadGridSize();
        BuildGrid(gridSize, out double[] vertices, out int[] faces);
        RunBenchmark(
            $"analytic-{gridSize}-count-50000", vertices, faces, Array.Empty<int>(),
            new SurfaceSimplifier.Options
            {
                Mode = SurfaceSimplifier.SimplificationMode.TargetVertexCount,
                TargetVertexCount = 50_000,
                NumericalTolerance = 1e-8
            },
            result => Assert.InRange(result.OutputVertexCount, 3, 50_000));
    }

    private void RunToleranceBenchmark(string seed, double[] vertices, int[] faces, int[] requiredSegments) =>
        RunBenchmark(
            seed, vertices, faces, requiredSegments,
            new SurfaceSimplifier.Options
            {
                MaximumDeviation = 0.05,
                NumericalTolerance = 1e-8
            },
            result => Assert.InRange(result.MaximumDeviation, 0.0, 0.05));

    private void RunBenchmark(
        string seed,
        double[] vertices,
        int[] faces,
        int[] requiredSegments,
        SurfaceSimplifier.Options options,
        Action<SurfaceSimplifier.Result> assertModeContract)
    {
        var phaseTicks = new long[Enum.GetValues<SurfaceSimplifier.Phase>().Length];
        var measuredOptions = new SurfaceSimplifier.Options
        {
            Mode = options.Mode,
            MaximumDeviation = options.MaximumDeviation,
            TargetVertexCount = options.TargetVertexCount,
            NumericalTolerance = options.NumericalTolerance,
            MaximumRounds = options.MaximumRounds,
            ShouldCancel = options.ShouldCancel,
            RecordPhase = (phase, elapsed) => phaseTicks[(int)phase] += elapsed.Ticks
        };
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long privateBefore = Process.GetCurrentProcess().PrivateMemorySize64;
        long peakPrivate = privateBefore;
        bool sampling = true;
        var sampler = new Thread(() =>
        {
            using Process process = Process.GetCurrentProcess();
            while (Volatile.Read(ref sampling))
            {
                process.Refresh();
                UpdateMaximum(ref peakPrivate, process.PrivateMemorySize64);
                Thread.Sleep(10);
            }
        }) { IsBackground = true, Name = "Surface simplifier memory sampler" };
        sampler.Start();
        var stopwatch = Stopwatch.StartNew();
        SurfaceSimplifier.Result result = SurfaceSimplifier.Simplify(
            vertices, faces, requiredSegments, measuredOptions);
        stopwatch.Stop();
        Volatile.Write(ref sampling, false);
        sampler.Join();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        long privateDelta = Process.GetCurrentProcess().PrivateMemorySize64 - privateBefore;
        long privatePeakDelta = peakPrivate - privateBefore;

        output.WriteLine(
            $"Rolling simplification seed={seed}: input={result.InputVertexCount:N0} vertices/{result.InputFaceCount:N0} faces; " +
            $"output={result.OutputVertexCount:N0} vertices/{result.OutputFaceCount:N0} faces; protected={result.ProtectedVertexCount:N0}; " +
            $"mandatorySegments={requiredSegments.Length / 2:N0}; " +
            $"error={result.MaximumDeviation:G17}; coverage={result.HasEqualDomain}; rounds={result.Rounds}; " +
            $"termination={result.Termination}; time={stopwatch.Elapsed.TotalMilliseconds:0.0}ms; " +
            $"allocated={allocated:N0}; privateDelta={privateDelta:N0}; privatePeakDelta={privatePeakDelta:N0}; " +
            $"preparation={TimeSpan.FromTicks(phaseTicks[(int)SurfaceSimplifier.Phase.ConstraintPreparation]).TotalMilliseconds:0.0}ms; " +
            $"triangulation={TimeSpan.FromTicks(phaseTicks[(int)SurfaceSimplifier.Phase.Triangulation]).TotalMilliseconds:0.0}ms; " +
            $"verification={TimeSpan.FromTicks(phaseTicks[(int)SurfaceSimplifier.Phase.Verification]).TotalMilliseconds:0.0}ms; " +
            $"refinement={TimeSpan.FromTicks(phaseTicks[(int)SurfaceSimplifier.Phase.Refinement]).TotalMilliseconds:0.0}ms");

        Assert.True(
            result.Termination is SurfaceSimplifier.TerminationReason.ToleranceSatisfied or
                SurfaceSimplifier.TerminationReason.TargetCountSatisfied,
            result.Diagnostic);
        Assert.True(result.Reduced, result.Diagnostic);
        Assert.True(result.HasEqualDomain);
        assertModeContract(result);
    }

    private static void UpdateMaximum(ref long target, long value)
    {
        long current = Volatile.Read(ref target);
        while (value > current)
        {
            long observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current)
                return;
            current = observed;
        }
    }

    private static int ReadGridSize()
    {
        string? value = Environment.GetEnvironmentVariable("MOLEHILL_SIMPLIFIER_GRID_SIZE");
        return int.TryParse(value, out int parsed) && parsed >= 2 ? parsed : 300;
    }

    private static void BuildGrid(int gridSize, out double[] vertices, out int[] faces)
    {
        int row = gridSize + 1;
        vertices = TestMeshes.GridVertices(
            row,
            row,
            1.0,
            static (x, y) => Math.Sin(x * 0.025) + Math.Cos(y * 0.021) + (Math.Sin((x + y) * 0.017) * 0.35));
        faces = TestMeshes.GridFaces(row, row);
    }
}
