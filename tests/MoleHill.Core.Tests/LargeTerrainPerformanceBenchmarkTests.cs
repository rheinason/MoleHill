using System.Diagnostics;
using MoleHill.Core.Analysis;
using Xunit;
using Xunit.Abstractions;

namespace MoleHill.Core.Tests;

public class LargeTerrainPerformanceBenchmarkTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Performance")]
    public void GradePath_LargeTerrain20260705Benchmark_ReportsShapeTimingAndTopology()
    {
        if (!IsPerfEnabled())
        {
            output.WriteLine("Set MOLEHILL_PERF=1 to run the large terrain Grade Path benchmark.");
            return;
        }

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        TerrainGradePathLarge20260705CopiedCaseTests.CopiedCaseRun run =
            TerrainGradePathLarge20260705CopiedCaseTests.RunCase();
        stopwatch.Stop();
        long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();

        output.WriteLine($"Grade Path core: {stopwatch.Elapsed.TotalMilliseconds:0.0} ms");
        output.WriteLine($"Allocation delta: {Math.Max(0, allocatedAfter - allocatedBefore):N0} bytes");
        output.WriteLine($"Shape: {run.InputVertexCount:N0}/{run.InputFaceCount:N0} -> {run.OutputVertexCount:N0}/{run.OutputFaceCount:N0}");
        output.WriteLine("Bundle-recorded Grade Path output was 8,392 verts / 16,511 faces.");
        output.WriteLine(
            $"Topology: boundaryEdges={run.BoundaryEdgeCount:N0}, boundaryVertices={run.BoundaryVertexCount:N0}, " +
            $"components={run.BoundaryComponentCount:N0}, nonmanifold={run.NonManifoldEdgeCount:N0}, openChains={run.HasOpenBoundaryChains}");

        Assert.Equal(6341, run.InputVertexCount);
        Assert.Equal(12411, run.InputFaceCount);
        Assert.InRange(run.OutputVertexCount, 8380, 8405);
        Assert.InRange(run.OutputFaceCount, 16480, 16540);
    }

    [Fact]
    [Trait("Category", "Performance")]
    public void MeshHeightProjector_LargeReferenceBenchmark_ReportsLinearLookupTiming()
    {
        if (!IsPerfEnabled())
        {
            output.WriteLine("Set MOLEHILL_PERF=1 to run the MeshHeightProjector benchmark.");
            return;
        }

        const int gridSize = 224;
        BuildRegularGrid(gridSize, out double[] vertices, out int vertexCount, out int[] faces, out int faceCount);

        var buildStopwatch = Stopwatch.StartNew();
        var projector = new MeshHeightProjector(vertices, vertexCount, faces, faceCount);
        buildStopwatch.Stop();

        int sampleCount = Math.Min(100_000, faceCount);
        int misses = 0;
        double checksum = 0.0;
        var queryStopwatch = Stopwatch.StartNew();
        for (int faceIndex = 0; faceIndex < sampleCount; faceIndex++)
        {
            int a = faces[faceIndex * 3];
            int b = faces[faceIndex * 3 + 1];
            int c = faces[faceIndex * 3 + 2];
            double x = (vertices[a * 3] + vertices[b * 3] + vertices[c * 3]) / 3.0;
            double y = (vertices[a * 3 + 1] + vertices[b * 3 + 1] + vertices[c * 3 + 1]) / 3.0;
            if (projector.TryProjectZ(x, y, 0.0, 1e-6, out double z, out var status) &&
                status == MeshHeightProjector.ProjectionStatus.Projected)
            {
                checksum += z;
            }
            else
            {
                misses++;
            }
        }
        queryStopwatch.Stop();

        output.WriteLine($"Reference mesh: {vertexCount:N0} verts / {faceCount:N0} faces");
        output.WriteLine($"Projector build: {buildStopwatch.Elapsed.TotalMilliseconds:0.0} ms");
        output.WriteLine($"Projector queries: {queryStopwatch.Elapsed.TotalMilliseconds:0.0} ms for {sampleCount:N0} samples");
        output.WriteLine($"Misses: {misses:N0}; checksum={checksum:G17}");

        Assert.Equal(0, misses);
    }

    private static bool IsPerfEnabled() =>
        string.Equals(Environment.GetEnvironmentVariable("MOLEHILL_PERF"), "1", StringComparison.Ordinal);

    private static void BuildRegularGrid(int gridSize, out double[] vertices, out int vertexCount, out int[] faces, out int faceCount)
    {
        vertexCount = (gridSize + 1) * (gridSize + 1);
        vertices = new double[vertexCount * 3];
        for (int y = 0; y <= gridSize; y++)
        {
            for (int x = 0; x <= gridSize; x++)
            {
                int index = (y * (gridSize + 1)) + x;
                vertices[index * 3] = x;
                vertices[index * 3 + 1] = y;
                vertices[index * 3 + 2] = Math.Sin(x * 0.03) + Math.Cos(y * 0.04);
            }
        }

        faceCount = gridSize * gridSize * 2;
        faces = new int[faceCount * 3];
        int face = 0;
        for (int y = 0; y < gridSize; y++)
        {
            for (int x = 0; x < gridSize; x++)
            {
                int v00 = (y * (gridSize + 1)) + x;
                int v10 = v00 + 1;
                int v01 = v00 + gridSize + 1;
                int v11 = v01 + 1;

                faces[face * 3] = v00;
                faces[face * 3 + 1] = v10;
                faces[face * 3 + 2] = v11;
                face++;

                faces[face * 3] = v00;
                faces[face * 3 + 1] = v11;
                faces[face * 3 + 2] = v01;
                face++;
            }
        }
    }
}
