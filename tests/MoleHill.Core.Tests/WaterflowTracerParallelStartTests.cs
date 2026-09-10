using MoleHill.Core.Analysis;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// Traces from independent starts may run together, but the result must be indistinguishable from the
/// serial loop: same path order, same points, same rejected count, same cancellation behaviour. The
/// oracle is the serial path itself, reached by staying under the parallel threshold.
/// </summary>
public class WaterflowTracerParallelStartTests
{
    [Fact]
    public void Trace_ManyStarts_MatchesTheSerialResultExactly()
    {
        (double[] vertices, int[] faces) = Bowl(60);

        // Below the threshold this runs serially, above it in parallel. Same starts either way.
        double[] fewStarts = Starts(6);
        double[] manyStarts = Starts(400);

        WaterflowTracer.Result serialFew = Trace(vertices, faces, fewStarts);
        WaterflowTracer.Result parallelMany = Trace(vertices, faces, manyStarts);
        WaterflowTracer.Result serialSubsetOfMany = TraceOneByOne(vertices, faces, manyStarts);

        Assert.NotEmpty(serialFew.Paths);
        AssertSameResult(serialSubsetOfMany, parallelMany);
    }

    [Fact]
    public void Trace_RepeatedRuns_AreIdentical()
    {
        (double[] vertices, int[] faces) = Bowl(60);
        double[] starts = Starts(400);

        WaterflowTracer.Result first = Trace(vertices, faces, starts);
        WaterflowTracer.Result second = Trace(vertices, faces, starts);

        AssertSameResult(first, second);
    }

    [Fact]
    public void Trace_StartsOffTheMesh_AreRejectedAndDoNotShiftTheOthers()
    {
        (double[] vertices, int[] faces) = Bowl(40);

        var mixed = new List<double>();
        var onMeshOnly = new List<double>();
        double[] onMesh = Starts(200, extent: 39.0);
        for (int i = 0; i < onMesh.Length / 2; i++)
        {
            if (i % 3 == 0)
            {
                mixed.Add(-500.0);
                mixed.Add(-500.0);
            }

            mixed.Add(onMesh[i * 2]);
            mixed.Add(onMesh[(i * 2) + 1]);
            onMeshOnly.Add(onMesh[i * 2]);
            onMeshOnly.Add(onMesh[(i * 2) + 1]);
        }

        WaterflowTracer.Result withRejects = Trace(vertices, faces, mixed.ToArray());
        WaterflowTracer.Result withoutRejects = Trace(vertices, faces, onMeshOnly.ToArray());

        Assert.True(withRejects.RejectedStartCount > 0);
        Assert.Equal(0, withoutRejects.RejectedStartCount);
        AssertSamePaths(withoutRejects, withRejects);
    }

    [Fact]
    public void Trace_Cancelled_ThrowsOperationCanceledEvenOnTheParallelPath()
    {
        (double[] vertices, int[] faces) = Bowl(60);
        double[] starts = Starts(400);

        Assert.Throws<OperationCanceledException>(() => WaterflowTracer.Trace(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            starts,
            starts.Length / 2,
            new WaterflowTracer.Options { CancellationRequested = () => true }));
    }

    [Fact]
    public void Trace_NoStarts_ReturnsNothing()
    {
        (double[] vertices, int[] faces) = Bowl(20);

        WaterflowTracer.Result result = Trace(vertices, faces, Array.Empty<double>());

        Assert.Empty(result.Paths);
        Assert.Equal(0, result.RejectedStartCount);
    }

    private static WaterflowTracer.Result Trace(double[] vertices, int[] faces, double[] startXy)
    {
        return WaterflowTracer.Trace(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            startXy,
            startXy.Length / 2,
            new WaterflowTracer.Options { MaxSteps = 5_000 });
    }

    /// <summary>Serial oracle: one start per call keeps every trace on the serial path.</summary>
    private static WaterflowTracer.Result TraceOneByOne(double[] vertices, int[] faces, double[] startXy)
    {
        var paths = new List<WaterflowTracer.Path>();
        int rejected = 0;
        for (int i = 0; i < startXy.Length / 2; i++)
        {
            WaterflowTracer.Result single = Trace(vertices, faces, new[] { startXy[i * 2], startXy[(i * 2) + 1] });
            paths.AddRange(single.Paths);
            rejected += single.RejectedStartCount;
        }

        return new WaterflowTracer.Result { Paths = paths, RejectedStartCount = rejected };
    }

    private static void AssertSameResult(WaterflowTracer.Result expected, WaterflowTracer.Result actual)
    {
        Assert.Equal(expected.RejectedStartCount, actual.RejectedStartCount);
        AssertSamePaths(expected, actual);
    }

    private static void AssertSamePaths(WaterflowTracer.Result expected, WaterflowTracer.Result actual)
    {
        Assert.Equal(expected.Paths.Count, actual.Paths.Count);
        for (int i = 0; i < expected.Paths.Count; i++)
        {
            WaterflowTracer.Path a = expected.Paths[i];
            WaterflowTracer.Path b = actual.Paths[i];
            Assert.Equal(a.PointCount, b.PointCount);
            Assert.Equal(a.ReachedBoundary, b.ReachedBoundary);
            Assert.Equal(a.TerminatedAtSink, b.TerminatedAtSink);
            Assert.Equal(a.PlanLength, b.PlanLength, 12);
            for (int k = 0; k < a.PointCount * 3; k++)
                Assert.Equal(a.PointsXyz[k], b.PointsXyz[k], 12);
        }
    }

    /// <summary>Starts well inside an <paramref name="extent"/>-wide sheet, so none is rejected.</summary>
    private static double[] Starts(int count, double extent = 60.0)
    {
        var random = new Random(90909);
        double span = extent - 5.0;
        var starts = new double[count * 2];
        for (int i = 0; i < count; i++)
        {
            starts[i * 2] = 2.0 + (random.NextDouble() * span);
            starts[(i * 2) + 1] = 2.0 + (random.NextDouble() * span);
        }

        return starts;
    }

    /// <summary>A dished sheet, so traces run for many steps rather than leaving immediately.</summary>
    private static (double[] Vertices, int[] Faces) Bowl(int n)
    {
        double centre = (n - 1) / 2.0;
        var vertices = new double[n * n * 3];
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                int v = (j * n) + i;
                double dx = i - centre;
                double dy = j - centre;
                vertices[v * 3] = i;
                vertices[(v * 3) + 1] = j;
                vertices[(v * 3) + 2] = ((dx * dx) + (dy * dy)) * 0.01;
            }
        }

        var faces = new int[(n - 1) * (n - 1) * 6];
        int f = 0;
        for (int j = 0; j < n - 1; j++)
        {
            for (int i = 0; i < n - 1; i++)
            {
                int v00 = (j * n) + i;
                int v10 = v00 + 1;
                int v01 = v00 + n;
                int v11 = v01 + 1;
                faces[f++] = v00; faces[f++] = v10; faces[f++] = v11;
                faces[f++] = v00; faces[f++] = v11; faces[f++] = v01;
            }
        }

        return (vertices, faces);
    }
}
