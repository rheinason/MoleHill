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
}
