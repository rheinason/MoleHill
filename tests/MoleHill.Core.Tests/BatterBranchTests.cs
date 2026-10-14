using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

public class BatterBranchTests
{
    [Fact]
    public void FromGroundDifferences_GroundBelowEdgeThenFarAbove_IsFill()
    {
        // The verge-then-terrace section: 0.13 below at the edge, 0.5 above further out.
        double branch = BatterBranch.FromGroundDifferences(new[] { -0.13, 0.2, 0.5, 0.5 }, 0.001);

        Assert.Equal(-1.0, branch);
    }

    [Fact]
    public void FromGroundDifferences_AtGradeAtEdgeThenAbove_IsCut()
    {
        double branch = BatterBranch.FromGroundDifferences(new[] { 0.0005, 0.3, -2.0 }, 0.001);

        Assert.Equal(1.0, branch);
    }

    [Fact]
    public void FromGroundDifferences_AllWithinTolerance_IsZero()
    {
        double branch = BatterBranch.FromGroundDifferences(new[] { 0.0005, -0.0009, 0.0 }, 0.001);

        Assert.Equal(0.0, branch);
    }

    [Fact]
    public void AtEdge_ZeroTolerance_IsSignOfDifference()
    {
        Assert.Equal(1.0, BatterBranch.AtEdge(1.0, 0.9, 0.0));
        Assert.Equal(-1.0, BatterBranch.AtEdge(0.9, 1.0, 0.0));
        Assert.Equal(0.0, BatterBranch.AtEdge(1.0, 1.0, 0.0));
    }
}
