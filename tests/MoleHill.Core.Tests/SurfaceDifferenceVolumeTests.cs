using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class SurfaceDifferenceVolumeTests
{
    // Original: a V valley, z = |x - 50| / 10, over 100 x 100, split along the valley floor.
    private static readonly double[] ValleyVertices =
    {
        0, 0, 5, 50, 0, 0, 100, 0, 5,
        0, 100, 5, 50, 100, 0, 100, 100, 5
    };

    private static readonly int[] ValleyFaces = { 0, 1, 4, 0, 4, 3, 1, 2, 5, 1, 5, 4 };

    private static ReferenceSurface Valley() => new(new IndexedTriMesh(ValleyVertices, 6, ValleyFaces, 4));

    [Fact]
    public void Measure_FlatFaceAcrossACrease_IntegratesTheCreaseNotTheCorners()
    {
        // Graded: one flat plane at z = 2 as two triangles whose corners all sit 3 below the ground.
        double[] graded = { 0, 0, 2, 100, 0, 2, 100, 100, 2, 0, 100, 2 };
        int[] gradedFaces = { 0, 1, 2, 0, 2, 3 };

        SurfaceDifference volume = SurfaceDifferenceVolume.Measure(graded, gradedFaces, 2, Valley(), 0.01);

        // Within |x-50| < 20 the plane is above the ground (fill 4000); beyond it, below (cut 9000).
        Assert.Equal(4000.0, volume.Fill, 6);
        Assert.Equal(9000.0, volume.Cut, 6);
        Assert.Equal(0, volume.FoldFaceCount);
    }

    [Fact]
    public void Measure_SameSurfaceTriangulatedTwoWays_GivesTheSameVolume()
    {
        // A tilted plane z = 1 + x/50 over the valley, once on the other diagonal and once fanned from an
        // interior point: the old corner and centroid rules gave a different figure for each.
        double Z(double x) => 1.0 + x / 50.0;
        double[] diagonal = { 0, 0, Z(0), 100, 0, Z(100), 100, 100, Z(100), 0, 100, Z(0) };
        int[] diagonalFaces = { 0, 1, 3, 1, 2, 3 };
        double[] fan = { 0, 0, Z(0), 100, 0, Z(100), 100, 100, Z(100), 0, 100, Z(0), 37, 61, Z(37) };
        int[] fanFaces = { 0, 1, 4, 1, 2, 4, 2, 3, 4, 3, 0, 4 };

        SurfaceDifference first = SurfaceDifferenceVolume.Measure(diagonal, diagonalFaces, 2, Valley(), 0.01);
        SurfaceDifference second = SurfaceDifferenceVolume.Measure(fan, fanFaces, 4, Valley(), 0.01);

        Assert.Equal(first.Cut, second.Cut, 6);
        Assert.Equal(first.Fill, second.Fill, 6);
        Assert.True(first.Cut > 0 && first.Fill > 0);
    }

    [Fact]
    public void Measure_ReferenceRaisedByOne_FillsItsPlanArea()
    {
        double[] raised = (double[])ValleyVertices.Clone();
        for (int i = 2; i < raised.Length; i += 3)
            raised[i] += 1.0;

        SurfaceDifference volume = SurfaceDifferenceVolume.Measure(raised, ValleyFaces, 4, Valley(), 0.01);

        Assert.Equal(10000.0, volume.Fill, 6);
        Assert.Equal(0.0, volume.Cut, 9);
        Assert.Equal(1.0, volume.AbsMax, 9);
    }

    [Fact]
    public void Measure_FaceOnTheOriginal_IsZero()
    {
        SurfaceDifference volume = SurfaceDifferenceVolume.Measure(ValleyVertices, ValleyFaces, 4, Valley(), 0.01);

        Assert.Equal(0.0, volume.Cut);
        Assert.Equal(0.0, volume.Fill);
    }

    [Fact]
    public void Measure_GroundOutsideTheReference_CountsAsUnchanged()
    {
        // Half of this square lies beyond the valley's x = 100 edge.
        double[] graded = { 50, 0, 10, 150, 0, 10, 150, 100, 10, 50, 100, 10 };
        int[] faces = { 0, 1, 2, 0, 2, 3 };

        SurfaceDifference volume = SurfaceDifferenceVolume.Measure(graded, faces, 2, Valley(), 0.01);

        // Over 50..100 the ground rises 0..5, so the fill is 50 * 100 * (10 - 2.5).
        Assert.Equal(37500.0, volume.Fill, 6);
    }

    [Fact]
    public void Measure_PerFaceShares_SumToTheTotal()
    {
        double[] graded = { 0, 0, 2, 100, 0, 2, 100, 100, 2, 0, 100, 2 };
        int[] faces = { 0, 1, 2, 0, 2, 3 };
        var cut = new double[2];
        var fill = new double[2];

        SurfaceDifference volume = SurfaceDifferenceVolume.Measure(graded, faces, 2, Valley(), 0.01, faceCut: cut, faceFill: fill);

        Assert.Equal(volume.Cut, cut.Sum(), 9);
        Assert.Equal(volume.Fill, fill.Sum(), 9);
    }

    [Fact]
    public void AccumulateLinear_SingleSign_IsAreaTimesMean()
    {
        double cut = 0, fill = 0;
        SurfaceDifferenceVolume.AccumulateLinear(3.0, -1.0, -2.0, -3.0, ref cut, ref fill);

        Assert.Equal(0.0, fill);
        Assert.Equal(6.0, cut, 9);
    }

    [Fact]
    public void AccumulateLinear_SignChange_SplitsAtTheZeroLine()
    {
        // d = (1, -1, -1) over area 6: the +1 corner owns 1/4 of the area (f = 1 / (2 * 2)) at mean 1/3.
        double cut = 0, fill = 0;
        SurfaceDifferenceVolume.AccumulateLinear(6.0, 1.0, -1.0, -1.0, ref cut, ref fill);

        Assert.Equal(6.0 * 0.25 / 3.0, fill, 9);
        // Total signed volume is area * mean = 6 * (-1/3) = -2, so cut = 2 + fill.
        Assert.Equal(2.0 + fill, cut, 9);
    }
}
