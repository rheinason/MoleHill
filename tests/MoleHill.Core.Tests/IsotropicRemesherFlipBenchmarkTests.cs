using System.Diagnostics;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;
using Xunit.Abstractions;

namespace MoleHill.Core.Tests;

/// <summary>
/// The flip phase at terrain scale, dictionary-driven reference against <see cref="FlipEdgeIndex"/>, inside
/// the real operator loop so the states are the ones Remesh flips. Only the flip calls are timed. It also
/// asserts the two produce identical faces, so a speed-up here cannot be a behaviour change.
/// Opt in with <c>MOLEHILL_PERF=1</c>.
/// </summary>
public class IsotropicRemesherFlipBenchmarkTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Performance")]
    public void FlipForQuality_HalfMillionFaceLoop_IndexedMatchesReferenceAndReportsSpeed()
    {
        if (!PerformanceLane.ShouldRun(output, "isotropic remesh flip phase, reference vs indexed, ~500k faces"))
            return;

        const int n = 500;
        const double target = 1.1;
        const int iterations = 4;
        BuildJitteredGrid(n, out double[] vertices, out int[] faces);
        var projection = new TerrainFaceGrid(vertices, vertices.Length / 3, faces, faces.Length / 3, target * 0.5);
        FeaturePolylineGraph graph = FeaturePolylineGraph.Build(
            vertices, faces, faces.Length / 3, Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
            creaseAngleDeg: 30.0, wallFaceMinSlopeDeg: 0.0, tolerance: 0.01);
        var reference = new IsotropicRemesher.MeshState(vertices, faces, graph);
        var indexed = new IsotropicRemesher.MeshState(vertices, faces, graph);

        double referenceMs = 0, indexedMs = 0;
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            foreach (IsotropicRemesher.MeshState state in new[] { reference, indexed })
            {
                IsotropicRemesher.SplitLongEdges(state, target * 8.0 / 5.0, projection);
                IsotropicRemesher.CollapseShortEdges(state, target, projection);
            }

            var timer = Stopwatch.StartNew();
            int expected = IsotropicRemesherFlipReference.FlipForQuality(reference);
            referenceMs += timer.Elapsed.TotalMilliseconds;

            timer.Restart();
            int actual = IsotropicRemesher.FlipForQuality(indexed);
            indexedMs += timer.Elapsed.TotalMilliseconds;

            output.WriteLine($"iteration {iteration}: {reference.FaceCount:N0} faces, {expected:N0} flips");
            Assert.Equal(expected, actual);
            Assert.Equal(reference.Tris, indexed.Tris);

            IsotropicRemesher.RelaxAndProject(reference, target, projection);
            IsotropicRemesher.RelaxAndProject(indexed, target, projection);
        }

        output.WriteLine(
            $"flip phase over {iterations} iterations: reference {referenceMs:N0} ms, indexed {indexedMs:N0} ms " +
            $"({referenceMs / Math.Max(indexedMs, 1e-9):0.0}x)");
    }

    private static void BuildJitteredGrid(int n, out double[] vertices, out int[] faces)
    {
        vertices = new double[(n + 1) * (n + 1) * 3];
        for (int j = 0; j <= n; j++)
        {
            for (int i = 0; i <= n; i++)
            {
                int v = (j * (n + 1)) + i;
                uint h = (uint)v * 2654435761u;
                double jx = i > 0 && i < n ? (((h & 0xFFFF) / 65535.0) - 0.5) * 0.6 : 0;
                double jy = j > 0 && j < n ? ((((h >> 16) & 0xFFFF) / 65535.0) - 0.5) * 0.6 : 0;
                vertices[v * 3] = i + jx;
                vertices[v * 3 + 1] = j + jy;
                vertices[v * 3 + 2] = Math.Sin(i * 0.05) * 3.0 + Math.Cos(j * 0.07) * 2.0;
            }
        }

        faces = TestMeshes.GridFaces(n + 1, n + 1);
    }
}
