using MoleHill.Grasshopper.Components;
using Xunit;

namespace MoleHill.Grasshopper.Tests;

public class TinSurfaceComponentSmokeTests
{
    [Fact]
    public void TinSurfaceComponent_RegistersBoundaryPeelParams()
    {
        var component = new TinFromPointsAndBreaklines();

        Assert.Equal(7, component.Params.Input.Count);
        Assert.Equal("Max Edge", component.Params.Input[4].Name);
        Assert.Equal("Max Angle", component.Params.Input[5].Name);
        Assert.Equal("Slope Limit", component.Params.Input[6].Name);
    }
}
