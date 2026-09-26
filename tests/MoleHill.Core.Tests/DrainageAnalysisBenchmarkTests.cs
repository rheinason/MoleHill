using System.Diagnostics;
using MoleHill.Core.Analysis;
using MoleHill.Core.Engine;
using Xunit;
using Xunit.Abstractions;

namespace MoleHill.Core.Tests;

/// <summary>
/// Splits the drainage analyses into their phases on the hosted lane's analysis-heavy surface (a 350 x 350
/// grid, 243k faces). The hosted lane measures each analysis whole and inside Rhino; this says which part
/// of it the time is in, which the lane cannot.
/// </summary>
public class DrainageAnalysisBenchmarkTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Performance")]
    public void Drainage_AnalysisHeavySurface_ReportsPhaseTimings()
    {
        if (!PerformanceLane.ShouldRun(output, "drainage analysis phases"))
            return;

        DrainageTestTerrain.Mesh mesh = DrainageTestTerrain.Create(350);
        output.WriteLine($"{mesh.VertexCount:N0} vertices, {mesh.FaceCount:N0} faces");

        // One untimed pass so JIT is not charged to the first phase.
        DrainageBasinAnalyzer.Analyze(mesh.Vertices, mesh.VertexCount, mesh.Faces, mesh.FaceCount);

        for (int run = 0; run < 3; run++)
        {
            var timer = Stopwatch.StartNew();
            FaceAdjacency.Build(mesh.Faces, mesh.FaceCount, mesh.VertexCount);
            double adjacencyMs = Lap(timer);

            WaterflowTracer.Result traced = WaterflowTracer.Trace(
                mesh.Vertices, mesh.VertexCount, mesh.Faces, mesh.FaceCount,
                new double[] { 116.7, 116.7, 233.3, 116.7, 116.7, 233.3, 233.3, 233.3 }, 4);
            double waterflowMs = Lap(timer);

            BasinGraph graph = DrainageBasinAnalyzer.Analyze(mesh.Vertices, mesh.VertexCount, mesh.Faces, mesh.FaceCount);
            double routeMs = Lap(timer);

            IReadOnlyList<PondingSolver.Pond> ponds = PondingSolver.Solve(graph, mesh.Vertices, mesh.VertexCount, mesh.Faces);
            double pondingMs = Lap(timer);

            output.WriteLine(
                $"run {run + 1}: FaceAdjacency {adjacencyMs,7:N1} ms | Waterflow 4 starts {waterflowMs,7:N1} ms | " +
                $"route {routeMs,7:N1} ms ({graph.SinkBasinCount:N0} sinks) | Ponding solve {pondingMs,7:N1} ms ({ponds.Count:N0} ponds) | " +
                $"{traced.Paths.Count} paths");
        }
    }

    private static double Lap(Stopwatch timer)
    {
        double ms = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        return ms;
    }
}
