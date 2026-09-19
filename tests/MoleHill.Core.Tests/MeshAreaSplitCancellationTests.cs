using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// R03. The zone splitter's expensive phases — spatial index construction, boundary preparation,
/// face mapping, the shared-edge registry — must observe cancellation *after they have started*, not
/// only at the phase boundaries around them. A check that can only fire before a phase begins leaves
/// a superseded build running that phase to completion while it holds whole-mesh buffers.
///
/// The face mapping is a <c>Parallel.For</c>, so its cancellation has a second requirement: it must
/// reach the host as an <see cref="OperationCanceledException"/> rather than as the
/// <see cref="AggregateException"/> the TPL would otherwise wrap it in, which the host would read as
/// an ordinary build failure.
/// </summary>
public class MeshAreaSplitCancellationTests
{
    /// <summary>
    /// A callback that stays quiet for the first <paramref name="quietConsultations"/> consultations
    /// and cancels afterwards. Set well above the number of phase-boundary checks, so a throw proves a
    /// probe *inside* a loop fired, not one at the entrance to a phase.
    /// </summary>
    private sealed class CancelAfter
    {
        private readonly int _quiet;
        private int _consultations;

        public CancelAfter(int quietConsultations) => _quiet = quietConsultations;

        public int Consultations => Volatile.Read(ref _consultations);

        public bool ShouldCancel() => Interlocked.Increment(ref _consultations) > _quiet;
    }

    private static void BuildGrid(int side, out double[] vertices, out int vertexCount, out int[] faces, out int faceCount)
    {
        vertexCount = side * side;
        vertices = new double[vertexCount * 3];
        for (int j = 0; j < side; j++)
        {
            for (int i = 0; i < side; i++)
            {
                int v = (j * side) + i;
                vertices[v * 3] = i;
                vertices[(v * 3) + 1] = j;
                vertices[(v * 3) + 2] = Math.Sin(i * 0.05) * 2.0;
            }
        }

        faceCount = (side - 1) * (side - 1) * 2;
        faces = new int[faceCount * 3];
        int f = 0;
        for (int j = 0; j < side - 1; j++)
        {
            for (int i = 0; i < side - 1; i++)
            {
                int v00 = (j * side) + i;
                int v10 = v00 + 1;
                int v01 = v00 + side;
                int v11 = v01 + 1;
                faces[f++] = v00; faces[f++] = v10; faces[f++] = v11;
                faces[f++] = v00; faces[f++] = v11; faces[f++] = v01;
            }
        }
    }

    /// <summary>A dense ring of boundary vertices, so boundary preparation and mapping both do work.</summary>
    private static MeshAreaSplitter.AreaBoundary[] RingAreas(int side, int boundaryVertices)
    {
        double extent = side - 1;
        double centre = extent * 0.5;
        double radius = extent * 0.35;
        var xy = new double[boundaryVertices * 2];
        for (int i = 0; i < boundaryVertices; i++)
        {
            double angle = (i * 2.0 * Math.PI) / boundaryVertices;
            xy[i * 2] = centre + (Math.Cos(angle) * radius);
            xy[(i * 2) + 1] = centre + (Math.Sin(angle) * radius);
        }

        return new[] { new MeshAreaSplitter.AreaBoundary(xy, boundaryVertices) };
    }

    private static MeshAreaSplitter.SplitResult? Split(int side, Func<bool>? shouldCancel, out string? errorMessage)
    {
        BuildGrid(side, out double[] vertices, out int vertexCount, out int[] faces, out int faceCount);
        return MeshAreaTopologySplitter.Split(
            vertices,
            vertexCount,
            faces,
            faceCount,
            RingAreas(side, 256),
            1e-6,
            out errorMessage,
            shouldCancel);
    }

    [Fact]
    public void Split_WithoutCancellation_StillProducesAResult()
    {
        MeshAreaSplitter.SplitResult? result = Split(96, shouldCancel: null, out string? errorMessage);

        Assert.NotNull(result);
        Assert.True(string.IsNullOrWhiteSpace(errorMessage), errorMessage);
    }

    [Fact]
    public void Split_CancelledBeforeAnyWork_ThrowsWithoutProducingAResult()
    {
        Assert.Throws<OperationCanceledException>(
            () => Split(96, shouldCancel: () => true, out _));
    }

    /// <summary>
    /// How many times a full, uncancelled split consults the callback. Measured rather than assumed:
    /// the probes only consult once per interval, and the parallel mapping's share depends on how the
    /// partitioner splits the faces, so a hardcoded threshold would be a guess in both directions.
    /// </summary>
    private static int MeasureConsultations(int side)
    {
        int consultations = 0;
        MeshAreaSplitter.SplitResult? result = Split(
            side,
            () => { Interlocked.Increment(ref consultations); return false; },
            out _);

        Assert.NotNull(result);
        return Volatile.Read(ref consultations);
    }

    [Theory]
    [InlineData(96, 0.2)]
    [InlineData(96, 0.4)]
    [InlineData(160, 0.2)]
    [InlineData(160, 0.4)]
    public void Split_CancelledPartWayThrough_ThrowsOperationCanceled(int side, double fraction)
    {
        int total = MeasureConsultations(side);

        // Comfortably more than the handful of unconditional phase-boundary checks, so cancelling at a
        // fraction of it must be caught by a probe INSIDE a loop rather than at the entrance to a phase.
        Assert.True(total >= 30, $"Only {total} consultations across a whole split; the loops are barely probed.");

        var canceller = new CancelAfter((int)(total * fraction));

        // Whatever phase it lands in - index construction, boundary preparation, the parallel face
        // mapping or the registry - the host must see cancellation, never Parallel.For's
        // AggregateException wrapper, which it would read as an ordinary build failure.
        Exception exception = Record.Exception(() => Split(side, canceller.ShouldCancel, out _))!;

        Assert.NotNull(exception);
        Assert.IsType<OperationCanceledException>(exception);
    }

    [Fact]
    public void SpatialHashGrid_Build_ObservesCancellationWhileIndexing()
    {
        var bounds = new Bounds2D[200_000];
        for (int i = 0; i < bounds.Length; i++)
        {
            double x = i % 500;
            double y = i / 500;
            bounds[i] = new Bounds2D(x, x + 0.5, y, y + 0.5);
        }

        var canceller = new CancelAfter(2);
        var probe = new CancellationProbe(canceller.ShouldCancel, interval: 16);

        Assert.Throws<OperationCanceledException>(
            () => SpatialHashGrid2D.Build(bounds, valid: null, probe));
    }

    [Fact]
    public void SpatialHashGrid_Build_WithoutAProbe_IsUnchanged()
    {
        var bounds = new Bounds2D[64];
        for (int i = 0; i < bounds.Length; i++)
            bounds[i] = new Bounds2D(i, i + 1, i, i + 1);

        SpatialHashGrid2D withoutProbe = SpatialHashGrid2D.Build(bounds);
        SpatialHashGrid2D withNoneProbe = SpatialHashGrid2D.Build(bounds, valid: null, CancellationProbe.None);

        Assert.Equal(withoutProbe.ItemCount, withNoneProbe.ItemCount);

        var scratch = new SpatialHashGrid2D.QueryScratch(bounds.Length);
        var a = new List<int>();
        var b = new List<int>();
        withoutProbe.GatherCandidates(new Bounds2D(10, 12, 10, 12), a, scratch);
        withNoneProbe.GatherCandidates(new Bounds2D(10, 12, 10, 12), b, scratch);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Probe_Fork_GivesEachWorkerItsOwnCountdown()
    {
        int consultations = 0;
        var probe = new CancellationProbe(() => { Interlocked.Increment(ref consultations); return false; }, interval: 4);

        CancellationProbe first = probe.Fork();
        CancellationProbe second = probe.Fork();

        // Three ticks each: below the interval, so a per-fork countdown consults nobody. A shared
        // countdown would already have reached four.
        for (int i = 0; i < 3; i++)
        {
            first.ThrowIfCancelledOften();
            second.ThrowIfCancelledOften();
        }

        Assert.Equal(0, Volatile.Read(ref consultations));
    }

    [Fact]
    public void Probe_Fork_OfNone_StaysNone()
    {
        Assert.Same(CancellationProbe.None, CancellationProbe.None.Fork());
        Assert.False(CancellationProbe.None.Fork(8).CanCancel);
    }
}
