using MoleHill.Core.Engine;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// The graded-rail adoption check. Near a kerb's pointed end the top rail traced along the toe's own
/// vertices; pinning them to the top's height lifted the toe onto the wall top.
/// </summary>
public class RetainingWallRailAdoptionTests
{
    private static ConstraintPolyline Rail(params double[] xyz) => new(xyz, xyz.Length / 3, false, PreserveInputElevation: true);

    [Fact]
    public void FindSharedRailVertex_TwoRailsThroughOneVertexAtTwoHeights_ReportsIt()
    {
        var toe = Rail(0, 0, 0, 1, 0, 0, 2, 0, 0);
        var top = Rail(0, 0.05, 0.15, 1, 0, 0.15, 2, 0.05, 0.15);

        Assert.Equal((1.0, 0.0), RetainingWallStage.FindSharedRailVertex(new[] { toe, top }, 0.001));
    }

    [Fact]
    public void FindSharedRailVertex_RailsMeetingAtOneHeight_IsFine()
    {
        // Two walls joined at a corner share the corner vertex at the same height.
        var a = Rail(0, 0, 1, 1, 0, 1);
        var b = Rail(1, 0, 1, 1, 1, 1);

        Assert.Null(RetainingWallStage.FindSharedRailVertex(new[] { a, b }, 0.001));
    }

    [Fact]
    public void FindSharedRailVertex_SeparateRails_IsFine()
    {
        var toe = Rail(0, 0, 0, 1, 0, 0);
        var top = Rail(0, 0.05, 0.15, 1, 0.05, 0.15);

        Assert.Null(RetainingWallStage.FindSharedRailVertex(new[] { toe, top }, 0.001));
    }
}
