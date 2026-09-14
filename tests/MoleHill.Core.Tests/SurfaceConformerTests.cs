using MoleHill.Core.Processing;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class SurfaceConformerTests
{
    private static readonly double[] TargetVertices =
    [
        -20, -20, 10,
         20, -20, 10,
         20,  20, 10,
        -20,  20, 10
    ];

    private static readonly int[] TargetFaces = [0, 1, 2, 0, 2, 3];

    [Fact]
    public void Conform_NoBoundary_BlendsEveryCoveredVertex()
    {
        double[] result = Conform([0, 0, 0, 2, 2, 2], [], strength: 0.5, feather: 0.0);

        Assert.Equal(5.0, result[2], 10);
        Assert.Equal(6.0, result[5], 10);
    }

    [Fact]
    public void Conform_DonutBoundary_LeavesHoleAndExteriorUntouched()
    {
        double[] result = Conform(
            [0, 0, 0, 3, 0, 0, 8, 0, 0],
            [Square(0, 0, 10), Square(0, 0, 4)],
            strength: 1.0,
            feather: 0.0);

        Assert.Equal(0.0, result[2], 10);
        Assert.Equal(10.0, result[5], 10);
        Assert.Equal(0.0, result[8], 10);
    }

    [Fact]
    public void Conform_Feather_FadesInwardAtOuterAndHoleEdges()
    {
        double[] result = Conform(
            [4.5, 0, 0, 2.5, 0, 0],
            [Square(0, 0, 10), Square(0, 0, 4)],
            strength: 1.0,
            feather: 1.0);

        Assert.Equal(5.0, result[2], 10);
        Assert.Equal(5.0, result[5], 10);
    }

    [Fact]
    public void Conform_TargetWithoutCoverage_PreservesVertex()
    {
        double[] result = Conform([30, 30, 4], [], strength: 1.0, feather: 0.0);

        Assert.Equal(4.0, result[2], 10);
    }

    private static double[] Conform(double[] vertices, IReadOnlyList<double[]> loops, double strength, double feather) =>
        SurfaceConformer.Conform(
            vertices,
            vertices.Length / 3,
            TargetVertices,
            TargetVertices.Length / 3,
            TargetFaces,
            TargetFaces.Length / 3,
            loops,
            strength,
            feather,
            1e-6);

    private static double[] Square(double cx, double cy, double size)
    {
        double h = size / 2.0;
        return [cx - h, cy - h, cx + h, cy - h, cx + h, cy + h, cx - h, cy + h];
    }
}
