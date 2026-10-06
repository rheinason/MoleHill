using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class FillSlopeFlipperTests
{
    // Quad a(0) b(1) w1(2) w2(3). Face (b, a, w2) is 0.1 wide and climbs 1 m across that width: a spike.
    private static readonly double[] Xyz = [0, 0, 0, 2, 0, 0, 1, 1, 0, 1, -0.1, 1];

    [Fact]
    public void Run_SpikeAcrossAFreeDiagonal_FlipsToTheFlatterOne()
    {
        int[] faces = [0, 1, 2, 1, 0, 3];

        int flips = FillSlopeFlipper.Run(Xyz, faces, 2, (_, _) => false);

        Assert.Equal(1, flips);
        Assert.Equal(new[] { 0, 3, 2, 3, 1, 2 }, faces);
        Assert.True(SignedPlanArea(faces, 0) > 0 && SignedPlanArea(faces, 1) > 0, "Winding must be kept.");
    }

    [Fact]
    public void Run_ConstrainedDiagonal_IsNeverFlipped()
    {
        int[] faces = [0, 1, 2, 1, 0, 3];

        Assert.Equal(0, FillSlopeFlipper.Run(Xyz, faces, 2, (u, v) => (u, v) is (0, 1) or (1, 0)));
        Assert.Equal(new[] { 0, 1, 2, 1, 0, 3 }, faces);
    }

    private static double SignedPlanArea(int[] f, int t)
    {
        int a = f[t * 3], b = f[(t * 3) + 1], c = f[(t * 3) + 2];
        return ((Xyz[b * 3] - Xyz[a * 3]) * (Xyz[(c * 3) + 1] - Xyz[(a * 3) + 1])) - ((Xyz[(b * 3) + 1] - Xyz[(a * 3) + 1]) * (Xyz[c * 3] - Xyz[a * 3]));
    }
}
