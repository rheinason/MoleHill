using System.Diagnostics;
using MoleHill.Core.Engine;
using Xunit;
using Xunit.Abstractions;

namespace MoleHill.Core.Tests;

/// <summary>
/// Coarse perf guard for <see cref="IsotropicRemesher"/> on a grading-scale mesh. Set
/// <c>MOLEHILL_REMESH_PHASE_PROFILE=1</c> to rerun the operator loop and report per-phase allocation.
/// </summary>
public class IsotropicRemesherBenchTests
{
    private readonly ITestOutputHelper _output;

    public IsotropicRemesherBenchTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Remesh_TwelveThousandFaces_CompletesInReasonableTime()
    {
        const int n = 80;
        const int iterations = 5;
        var vertices = new double[(n + 1) * (n + 1) * 3];
        for (int j = 0; j <= n; j++)
        {
            for (int i = 0; i <= n; i++)
            {
                int v = (j * (n + 1)) + i;
                double x = i * 0.5, y = j * 0.5;
                uint h = (uint)(v * 2654435761u);
                double jx = i > 0 && i < n ? (((h & 0xFFFF) / 65535.0) - 0.5) * 0.3 : 0;
                double jy = j > 0 && j < n ? ((((h >> 16) & 0xFFFF) / 65535.0) - 0.5) * 0.3 : 0;
                vertices[v * 3] = x + jx;
                vertices[v * 3 + 1] = y + jy;
                vertices[v * 3 + 2] = Math.Sin(x * 0.3) * Math.Cos(y * 0.25);
            }
        }

        var faces = new int[n * n * 6];
        int f = 0;
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                int v00 = (j * (n + 1)) + i;
                faces[f++] = v00; faces[f++] = v00 + 1; faces[f++] = v00 + n + 2;
                faces[f++] = v00; faces[f++] = v00 + n + 2; faces[f++] = v00 + n + 1;
            }
        }

        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();
        var result = IsotropicRemesher.Remesh(vertices, faces, Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
            new IsotropicRemesher.Options
            {
                TargetEdgeLength = 1.2,
                CreaseAngleDeg = 30,
                Tolerance = 0.01,
                Iterations = iterations
            });
        sw.Stop();
        long allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

        _output.WriteLine($"faces {faces.Length / 3} -> {result.Faces.Length / 3}; " +
                           $"splits={result.Splits} collapses={result.Collapses} flips={result.Flips} relaxed={result.RelaxedVertices}; " +
                           $"elapsed {sw.ElapsedMilliseconds} ms; allocated {allocatedBytes:N0} bytes; {result.Timing}");
        WritePhaseAllocationProfile(vertices, faces, 1.2, iterations, result);
        Assert.True(result.Success, result.Warning);
        Assert.True(sw.ElapsedMilliseconds < 15000, $"remesh took {sw.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// Grading-shaped stress: a coarse 14 m field with a dense 0.4 m corridor band, remeshed to a 3 m
    /// target — the outer region split-refines heavily while the corridor collapse-coarsens heavily.
    /// </summary>
    [Fact]
    public void Remesh_CoarseFieldWithDenseCorridor_CompletesInReasonableTime()
    {
        const int iterations = 5;
        var xs = new List<double>();
        for (double x = 0; x <= 280; x += 14) xs.Add(x);
        var ys = new List<double>();
        for (double y = 0; y < 98; y += 14) ys.Add(y);
        for (double y = 98; y <= 112; y += 0.4) ys.Add(y);   // dense corridor band
        for (double y = 126; y <= 224; y += 14) ys.Add(y);

        int nx = xs.Count, ny = ys.Count;
        var vertices = new double[nx * ny * 3];
        for (int j = 0; j < ny; j++)
        {
            for (int i = 0; i < nx; i++)
            {
                int v = (j * nx) + i;
                vertices[v * 3] = xs[i];
                vertices[v * 3 + 1] = ys[j];
                vertices[v * 3 + 2] = Math.Sin(xs[i] * 0.05) * 4.0 + (ys[j] * 0.02);
            }
        }

        var faces = new int[(nx - 1) * (ny - 1) * 6];
        int f = 0;
        for (int j = 0; j < ny - 1; j++)
        {
            for (int i = 0; i < nx - 1; i++)
            {
                int v00 = (j * nx) + i;
                faces[f++] = v00; faces[f++] = v00 + 1; faces[f++] = v00 + nx + 1;
                faces[f++] = v00; faces[f++] = v00 + nx + 1; faces[f++] = v00 + nx;
            }
        }

        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();
        var result = IsotropicRemesher.Remesh(vertices, faces, Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
            new IsotropicRemesher.Options
            {
                TargetEdgeLength = 3.0,
                CreaseAngleDeg = 30,
                Tolerance = 0.01,
                Iterations = iterations
            });
        sw.Stop();
        long allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;

        _output.WriteLine($"faces {faces.Length / 3} -> {result.Faces.Length / 3}; " +
                           $"splits={result.Splits} collapses={result.Collapses} flips={result.Flips} relaxed={result.RelaxedVertices}; " +
                           $"elapsed {sw.ElapsedMilliseconds} ms; allocated {allocatedBytes:N0} bytes; {result.Timing}");
        WritePhaseAllocationProfile(vertices, faces, 3.0, iterations, result);
        Assert.True(result.Success, result.Warning);
        Assert.True(sw.ElapsedMilliseconds < 15000, $"remesh took {sw.ElapsedMilliseconds} ms");
    }

    private void WritePhaseAllocationProfile(
        double[] vertices,
        int[] faces,
        double target,
        int iterations,
        IsotropicRemesher.Result expected)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("MOLEHILL_REMESH_PHASE_PROFILE"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        FeaturePolylineGraph graph = FeaturePolylineGraph.Build(
            vertices,
            faces,
            faces.Length / 3,
            Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
            creaseAngleDeg: 30.0,
            wallFaceMinSlopeDeg: 0.0,
            tolerance: 0.01,
            minCreaseChainLength: target * 3.0);
        var projection = new MoleHill.Core.Grading.TerrainFaceGrid(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            target * 0.5);
        var state = new IsotropicRemesher.MeshState(vertices, faces, graph);

        long collapseBytes = 0;
        long splitBytes = 0;
        long flipBytes = 0;
        long relaxBytes = 0;
        int totalCollapses = 0;
        int totalSplits = 0;
        int totalFlips = 0;
        int totalRelaxed = 0;
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            int collapses = IsotropicRemesher.CollapseShortEdges(state, target, projection);
            collapseBytes += GC.GetAllocatedBytesForCurrentThread() - before;

            before = GC.GetAllocatedBytesForCurrentThread();
            int splits = IsotropicRemesher.SplitLongEdges(state, target * 4.0 / 3.0, projection);
            splitBytes += GC.GetAllocatedBytesForCurrentThread() - before;

            before = GC.GetAllocatedBytesForCurrentThread();
            int flips = IsotropicRemesher.FlipForQuality(state);
            flipBytes += GC.GetAllocatedBytesForCurrentThread() - before;

            before = GC.GetAllocatedBytesForCurrentThread();
            int relaxed = IsotropicRemesher.RelaxAndProject(state, target, projection);
            relaxBytes += GC.GetAllocatedBytesForCurrentThread() - before;

            totalCollapses += collapses;
            totalSplits += splits;
            totalFlips += flips;
            totalRelaxed += relaxed;
            if (collapses == 0 && splits == 0 && flips == 0 && relaxed == 0)
                break;
        }

        Assert.Equal(expected.Collapses, totalCollapses);
        Assert.Equal(expected.Splits, totalSplits);
        Assert.Equal(expected.Flips, totalFlips);
        Assert.Equal(expected.RelaxedVertices, totalRelaxed);
        _output.WriteLine(
            $"phase allocations: collapse {collapseBytes:N0}, split {splitBytes:N0}, " +
            $"flip {flipBytes:N0}, relax {relaxBytes:N0} bytes");
    }
}
