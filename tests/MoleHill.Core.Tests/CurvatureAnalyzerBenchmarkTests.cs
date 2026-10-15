using System.Diagnostics;
using MoleHill.Core.Analysis;
using MoleHill.Core.Engine;
using Xunit;
using Xunit.Abstractions;

namespace MoleHill.Core.Tests;

/// <summary>
/// Curvature measurement at terrain scale: a 250k-vertex (~500k-face) survey grid at three landform sizes.
/// The fit bins samples into cells a quarter of the radius wide, so the cost should stay roughly flat as
/// the radius grows; this is what shows whether it does. Opt in with <c>MOLEHILL_PERF=1</c>.
/// </summary>
public class CurvatureAnalyzerBenchmarkTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Performance")]
    public void Measure_QuarterMillionVertices_AtThreeLandformSizes()
    {
        if (!PerformanceLane.ShouldRun(output, "curvature measure + classify, 250k vertices"))
            return;

        var random = new Random(5);
        IndexedTriMesh mesh = CurvatureAnalyzerTests.Grid(
            249.5,
            1.0,
            (x, y) => (Math.Sin(x * 0.05) * 6.0) + (Math.Cos(y * 0.031) * 4.0) + ((random.NextDouble() - 0.5) * 0.05));
        output.WriteLine($"{mesh.VertexCount:N0} vertices, {mesh.FaceCount:N0} faces");

        // Warm-up so the first timing is not JIT.
        CurvatureAnalyzer.Measure(mesh, 5.0);

        foreach (double size in new[] { 1.0, 5.0, 20.0 })
        {
            var timer = Stopwatch.StartNew();
            CurvatureField field = CurvatureAnalyzer.Measure(mesh, size);
            double measureMs = timer.Elapsed.TotalMilliseconds;

            timer.Restart();
            sbyte[] classes = LandformClassifier.Classify(field, 100.0);
            double classifyMs = timer.Elapsed.TotalMilliseconds;

            int measured = classes.Count(c => c >= 0);
            output.WriteLine($"size {size,5:F1} m: measure {measureMs,8:N1} ms, classify {classifyMs,6:N1} ms, {measured:N0} faces measured");
            Assert.Equal(mesh.FaceCount, measured);
        }
    }
}
