using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// The two longest-running Core stages observe cancellation inside their loops, so a superseded build
/// stops instead of finishing its remaining rounds while holding whole-mesh buffers. Cancelling throws
/// <see cref="OperationCanceledException"/> rather than returning a partial result: there is no valid
/// output to return, and throwing makes it impossible to publish one to a cache by mistake.
/// </summary>
public class CoreStageCancellationTests
{
    private static readonly IReadOnlyList<ConstraintPolyline> NoConstraints =
        Array.Empty<ConstraintPolyline>();

    [Fact]
    public void Probe_None_NeverCancels()
    {
        CancellationProbe.None.ThrowIfCancelled();
        for (int i = 0; i < CancellationProbe.DefaultInterval * 3; i++)
            CancellationProbe.None.ThrowIfCancelledOften();

        Assert.False(CancellationProbe.None.CanCancel);
    }

    [Fact]
    public void Probe_ThrowIfCancelled_ConsultsTheCallbackEveryTime()
    {
        int calls = 0;
        var probe = new CancellationProbe(() => ++calls > 2);

        probe.ThrowIfCancelled();
        probe.ThrowIfCancelled();
        Assert.Throws<OperationCanceledException>(() => probe.ThrowIfCancelled());
        Assert.Equal(3, calls);
    }

    [Fact]
    public void Probe_ThrowIfCancelledOften_ConsultsTheCallbackAtItsInterval()
    {
        int calls = 0;
        var probe = new CancellationProbe(() => false, interval: 10);

        for (int i = 0; i < 100; i++)
        {
            probe.ThrowIfCancelledOften();
            calls = i;
        }

        Assert.Equal(99, calls);
    }

    [Fact]
    public void Probe_ThrowIfCancelledOften_EventuallyThrows()
    {
        var probe = new CancellationProbe(() => true, interval: 4);

        Assert.Throws<OperationCanceledException>(() =>
        {
            for (int i = 0; i < 1000; i++)
                probe.ThrowIfCancelledOften();
        });
    }

    [Fact]
    public void Remesh_CancelledImmediately_Throws()
    {
        (double[] vertices, int[] faces) = Sheet(60);

        Assert.Throws<OperationCanceledException>(() => IsotropicRemesher.Remesh(
            vertices,
            faces,
            NoConstraints,
            new IsotropicRemesher.Options
            {
                TargetEdgeLength = 1.0,
                Tolerance = 0.001,
                Iterations = 5,
                ShouldCancel = () => true
            }));
    }

    [Fact]
    public void Remesh_CancelledPartWay_StopsBeforeFinishingItsIterations()
    {
        (double[] vertices, int[] faces) = Sheet(60);
        int observations = 0;

        Assert.Throws<OperationCanceledException>(() => IsotropicRemesher.Remesh(
            vertices,
            faces,
            NoConstraints,
            new IsotropicRemesher.Options
            {
                TargetEdgeLength = 0.5,
                Tolerance = 0.001,
                Iterations = 5,
                ShouldCancel = () => ++observations > 3
            }));

        // Cancellation is observed at a phase or round boundary well inside the run, not only after
        // every iteration has completed.
        Assert.True(observations > 3);
    }

    [Fact]
    public void Remesh_NeverCancelled_IsUnaffectedByTheProbe()
    {
        (double[] vertices, int[] faces) = Sheet(40);

        IsotropicRemesher.Result withProbe = Run(vertices, faces, () => false);
        IsotropicRemesher.Result withoutProbe = Run(vertices, faces, null);

        Assert.True(withProbe.Success, withProbe.Warning);
        Assert.Equal(withoutProbe.Vertices, withProbe.Vertices);
        Assert.Equal(withoutProbe.Faces, withProbe.Faces);
    }

    [Fact]
    public void SplitPreservingTopology_CancelledImmediately_Throws()
    {
        (double[] vertices, int[] faces) = Sheet(40);
        var areas = new[] { Boundary(5.0, 5.0, 25.0, 25.0) };

        Assert.Throws<OperationCanceledException>(() => MeshAreaSplitter.SplitPreservingTopology(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            areas,
            1e-6,
            out _,
            () => true));
    }

    [Fact]
    public void SplitPreservingTopology_NeverCancelled_MatchesTheUncancellableCall()
    {
        (double[] vertices, int[] faces) = Sheet(40);
        var areas = new[] { Boundary(5.0, 5.0, 25.0, 25.0) };

        MeshAreaSplitter.SplitResult? withProbe = MeshAreaSplitter.SplitPreservingTopology(
            vertices, vertices.Length / 3, faces, faces.Length / 3, areas, 1e-6, out _, () => false);
        MeshAreaSplitter.SplitResult? withoutProbe = MeshAreaSplitter.SplitPreservingTopology(
            vertices, vertices.Length / 3, faces, faces.Length / 3, areas, 1e-6, out _);

        Assert.NotNull(withProbe);
        Assert.NotNull(withoutProbe);
        Assert.Equal(withoutProbe!.Vertices, withProbe!.Vertices);
        Assert.Equal(withoutProbe.Faces, withProbe.Faces);
        Assert.Equal(withoutProbe.FaceAreaIndex, withProbe.FaceAreaIndex);
    }

    private static IsotropicRemesher.Result Run(double[] vertices, int[] faces, Func<bool>? shouldCancel)
    {
        return IsotropicRemesher.Remesh(
            (double[])vertices.Clone(),
            (int[])faces.Clone(),
            NoConstraints,
            new IsotropicRemesher.Options
            {
                TargetEdgeLength = 1.5,
                Tolerance = 0.001,
                Iterations = 3,
                ShouldCancel = shouldCancel
            });
    }

    private static MeshAreaSplitter.AreaBoundary Boundary(double minX, double minY, double maxX, double maxY)
    {
        return new MeshAreaSplitter.AreaBoundary(
            new[] { minX, minY, maxX, minY, maxX, maxY, minX, maxY },
            4);
    }

    private static (double[] Vertices, int[] Faces) Sheet(int n)
    {
        var vertices = new double[n * n * 3];
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                int v = (j * n) + i;
                vertices[v * 3] = i;
                vertices[(v * 3) + 1] = j;
                vertices[(v * 3) + 2] = Math.Sin(i * 0.2) + Math.Cos(j * 0.15);
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
