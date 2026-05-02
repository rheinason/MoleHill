using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

public class MeshSmootherTests
{
    [Fact]
    public void Smooth_PlanarSlope_PreservesInteriorVertexOnIrregularMesh()
    {
        var vertices = new[]
        {
            0.0, 0.0, 0.0,
            2.0, 0.0, 2.0,
            2.0, 1.0, 3.0,
            0.0, 2.0, 2.0,
            0.7, 0.6, 1.3
        };
        var faces = new[]
        {
            0, 1, 4,
            1, 2, 4,
            2, 3, 4,
            3, 0, 4
        };

        var smoothed = MeshSmoother.Smooth(
            vertices,
            5,
            faces,
            4,
            Array.Empty<(double[] xyVerts, int vertCount, double strength)>(),
            1.0,
            Array.Empty<(double[] xyPts, int ptCount)>(),
            0.0,
            1e-6,
            1);

        Assert.Equal(1.3, smoothed[14], 6);
    }

    [Fact]
    public void Smooth_MultipleIterationsIncreaseInteriorDisplacement()
    {
        var vertices = new[]
        {
            0.0, 0.0, 0.0,
            1.0, 0.0, 0.0,
            1.0, 1.0, 0.0,
            0.0, 1.0, 0.0,
            0.5, 0.5, 1.0
        };
        var faces = new[]
        {
            0, 1, 4,
            1, 2, 4,
            2, 3, 4,
            3, 0, 4
        };

        var onePass = MeshSmoother.Smooth(
            vertices,
            5,
            faces,
            4,
            Array.Empty<(double[] xyVerts, int vertCount, double strength)>(),
            0.2,
            Array.Empty<(double[] xyPts, int ptCount)>(),
            0.0,
            1e-6,
            1);

        var threePasses = MeshSmoother.Smooth(
            vertices,
            5,
            faces,
            4,
            Array.Empty<(double[] xyVerts, int vertCount, double strength)>(),
            0.2,
            Array.Empty<(double[] xyPts, int ptCount)>(),
            0.0,
            1e-6,
            3);

        Assert.Equal(0.0, onePass[2], 6);
        Assert.Equal(0.0, onePass[5], 6);
        Assert.Equal(0.0, onePass[8], 6);
        Assert.Equal(0.0, onePass[11], 6);
        Assert.True(onePass[14] < 1.0);
        Assert.True(threePasses[14] < onePass[14]);
    }

    [Fact]
    public void SmoothPrepared_MatchesDirectSmoothOutput()
    {
        var vertices = new[]
        {
            0.0, 0.0, 0.0,
            1.0, 0.0, 0.0,
            1.0, 1.0, 0.0,
            0.0, 1.0, 0.0,
            0.5, 0.5, 1.0
        };
        var faces = new[]
        {
            0, 1, 4,
            1, 2, 4,
            2, 3, 4,
            3, 0, 4
        };

        var direct = MeshSmoother.Smooth(
            vertices,
            5,
            faces,
            4,
            Array.Empty<(double[] xyVerts, int vertCount, double strength)>(),
            0.35,
            Array.Empty<(double[] xyPts, int ptCount)>(),
            0.25,
            1e-6,
            2);

        var prepared = MeshSmoother.Prepare(
            vertices,
            5,
            faces,
            4,
            Array.Empty<(double[] xyVerts, int vertCount)>(),
            Array.Empty<(double[] xyPts, int ptCount)>(),
            1e-6);

        var cached = MeshSmoother.SmoothPrepared(
            vertices,
            prepared,
            0.35,
            0.25,
            2);

        Assert.Equal(direct.Length, cached.Length);
        for (int i = 0; i < direct.Length; i++)
            Assert.Equal(direct[i], cached[i], 6);
    }

    [Fact]
    public void SmoothPrepared_BreaklineFixityZero_DoesNotFreezeBreaklineVertex()
    {
        var vertices = new[]
        {
            0.0, 0.0, 0.0,
            1.0, 0.0, 0.0,
            1.0, 1.0, 0.0,
            0.0, 1.0, 0.0,
            0.5, 0.5, 1.0
        };
        var faces = new[]
        {
            0, 1, 4,
            1, 2, 4,
            2, 3, 4,
            3, 0, 4
        };
        var breaklines = new[]
        {
            (xyPts: new[] { 0.0, 0.5, 1.0, 0.5 }, ptCount: 2)
        };

        var preparedWithoutBreaklines = MeshSmoother.Prepare(
            vertices,
            5,
            faces,
            4,
            Array.Empty<(double[] xyVerts, int vertCount)>(),
            Array.Empty<(double[] xyPts, int ptCount)>(),
            1e-6);

        var preparedWithBreaklines = MeshSmoother.Prepare(
            vertices,
            5,
            faces,
            4,
            Array.Empty<(double[] xyVerts, int vertCount)>(),
            breaklines,
            1e-6);

        var withoutBreaklines = MeshSmoother.SmoothPrepared(
            vertices,
            preparedWithoutBreaklines,
            0.35,
            0.0,
            1);

        var withZeroFixity = MeshSmoother.SmoothPrepared(
            vertices,
            preparedWithBreaklines,
            0.35,
            0.0,
            1);

        Assert.True(withZeroFixity[14] < vertices[14]);
        Assert.Equal(withoutBreaklines[14], withZeroFixity[14], 6);
    }

    [Fact]
    public void Smooth_WithBoundaryAndBreakline_MatchesPreparedOutput()
    {
        var vertices = new[]
        {
            0.0, 0.0, 0.0,
            1.0, 0.0, 0.0,
            1.0, 1.0, 0.0,
            0.0, 1.0, 0.0,
            0.5, 0.5, 1.0
        };
        var faces = new[]
        {
            0, 1, 4,
            1, 2, 4,
            2, 3, 4,
            3, 0, 4
        };
        var boundaryWithStrength = new[]
        {
            (xyVerts: new[] { 0.1, 0.1, 0.9, 0.1, 0.9, 0.9, 0.1, 0.9 }, vertCount: 4, strength: 0.35)
        };
        var boundary = new[]
        {
            (xyVerts: new[] { 0.1, 0.1, 0.9, 0.1, 0.9, 0.9, 0.1, 0.9 }, vertCount: 4)
        };
        var breaklines = new[]
        {
            (xyPts: new[] { 0.0, 0.5, 1.0, 0.5 }, ptCount: 2)
        };

        var direct = MeshSmoother.Smooth(
            vertices,
            5,
            faces,
            4,
            boundaryWithStrength,
            1.0,
            breaklines,
            0.25,
            1e-6,
            2);

        var prepared = MeshSmoother.Prepare(
            vertices,
            5,
            faces,
            4,
            boundary,
            breaklines,
            1e-6);

        var cached = MeshSmoother.SmoothPrepared(
            vertices,
            prepared,
            0.35,
            0.25,
            2);

        Assert.Equal(direct.Length, cached.Length);
        for (int i = 0; i < direct.Length; i++)
            Assert.Equal(direct[i], cached[i], 6);
    }

    [Fact]
    public void Prepare_MarksOnlyNakedEdgeVerticesAsMeshBoundary()
    {
        var vertices = new[]
        {
            0.0, 0.0, 0.0,
            1.0, 0.0, 0.0,
            1.0, 1.0, 0.0,
            0.0, 1.0, 0.0,
            0.5, 0.5, 1.0
        };
        var faces = new[]
        {
            0, 1, 4,
            1, 2, 4,
            2, 3, 4,
            3, 0, 4
        };

        var prepared = MeshSmoother.Prepare(
            vertices,
            5,
            faces,
            4,
            Array.Empty<(double[] xyVerts, int vertCount)>(),
            Array.Empty<(double[] xyPts, int ptCount)>(),
            1e-6);

        Assert.True(prepared.IsMeshBoundary[0]);
        Assert.True(prepared.IsMeshBoundary[1]);
        Assert.True(prepared.IsMeshBoundary[2]);
        Assert.True(prepared.IsMeshBoundary[3]);
        Assert.False(prepared.IsMeshBoundary[4]);
    }

    [Fact]
    public void Prepare_ClosedBreaklineWithoutDuplicateEndpoint_MarksClosingSegment()
    {
        var vertices = new[]
        {
            0.0, 0.0, 0.0,
            2.0, 0.0, 0.0,
            2.0, 2.0, 0.0,
            0.0, 2.0, 0.0,
            1.0, 1.0, 1.0,
            0.0, 1.0, 2.0
        };
        var faces = new[]
        {
            0, 1, 4,
            1, 2, 4,
            2, 3, 4,
            3, 0, 4,
            0, 4, 5,
            4, 3, 5
        };
        var breaklines = new[]
        {
            new MeshSmoother.BreaklinePolyline(
                new[] { 0.0, 0.0, 2.0, 0.0, 2.0, 2.0, 0.0, 2.0 },
                PointCount: 4,
                IsClosed: true)
        };

        var prepared = MeshSmoother.Prepare(
            vertices,
            6,
            faces,
            6,
            Array.Empty<(double[] xyVerts, int vertCount)>(),
            breaklines,
            1e-6);

        Assert.True(prepared.IsOnBreakline[5]);
    }
}
