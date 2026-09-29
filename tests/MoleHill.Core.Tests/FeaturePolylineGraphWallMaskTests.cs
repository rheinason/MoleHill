using MoleHill.Core.Engine;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class FeaturePolylineGraphWallMaskTests
{
    private const double WallSlope = 70.0;
    private const double Tolerance = 0.01;

    [Fact]
    public void BuildFrozenFaceMask_StrayCollinearCapOnTerrain_IsNotAWall()
    {
        // A zero-area cap along a straight line (apex 1 µm off its long edge) between two flat faces.
        // Its normal is pure rounding and reads as vertical; frozen, it pinned the whole line and let
        // wall bisection densify it far below the remesh target.
        double[] vertices =
        [
            0, 0, 0,        // 0 A
            1, 0, 1e-6,     // 1 B (on A-C)
            2, 0, 0,        // 2 C
            1, 2, 0,        // 3 D
            1, -2, 0,       // 4 E
        ];
        int[] faces = [0, 1, 2, 2, 3, 0, 0, 4, 1, 1, 4, 2];

        bool[] legacy = FeaturePolylineGraph.BuildFrozenFaceMask(vertices, faces, 4, WallSlope);
        bool[] frozen = FeaturePolylineGraph.BuildFrozenFaceMask(vertices, faces, 4, WallSlope, Tolerance);

        Assert.True(legacy[0]);
        Assert.DoesNotContain(true, frozen);
    }

    [Fact]
    public void BuildFrozenFaceMask_ThinSliverOnRealWall_StaysFrozen()
    {
        // A vertical 2 x 2 wall panel with a zero-area cap on its top edge: the sliver is part of the
        // wall, so it keeps the wall's classification.
        double[] vertices =
        [
            0, 0, 0,          // 0
            2, 0, 0,          // 1
            2, 0, 2,          // 2
            0, 0, 2,          // 3
            1, 0, 2 + 1e-6,   // 4 on edge 3-2
        ];
        int[] faces = [0, 1, 2, 0, 2, 3, 3, 4, 2];

        bool[] frozen = FeaturePolylineGraph.BuildFrozenFaceMask(vertices, faces, 3, WallSlope, Tolerance);

        Assert.All(frozen, Assert.True);
    }

    [Fact]
    public void BuildFrozenFaceMask_ThinChainReachingAWall_StaysFrozen()
    {
        // Two caps in a chain: only the first touches the wall, the second is connected through it.
        double[] vertices =
        [
            0, 0, 0,            // 0
            2, 0, 0,            // 1
            2, 0, 2,            // 2
            0, 0, 2,            // 3
            1, 0, 2 + 1e-6,     // 4 on edge 3-2
            0.5, 0, 2 + 1e-6,   // 5 on edge 3-4
        ];
        int[] faces = [0, 1, 2, 0, 2, 3, 3, 4, 2, 3, 5, 4];

        bool[] frozen = FeaturePolylineGraph.BuildFrozenFaceMask(vertices, faces, 4, WallSlope, Tolerance);

        Assert.All(frozen, Assert.True);
    }

    [Fact]
    public void BuildFrozenFaceMask_ThinButRealWallFace_IsNotReleasedWhenAboveTolerance()
    {
        // A low wall panel 5 cm high: thin, but well above the tolerance, so it is still a wall.
        double[] vertices = [0, 0, 0, 10, 0, 0, 10, 0, 0.05];
        int[] faces = [0, 1, 2];

        bool[] frozen = FeaturePolylineGraph.BuildFrozenFaceMask(vertices, faces, 1, WallSlope, Tolerance);

        Assert.True(frozen[0]);
    }
}
