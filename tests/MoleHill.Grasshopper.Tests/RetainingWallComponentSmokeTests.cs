using MoleHill.Grasshopper.Components;
using Xunit;

namespace MoleHill.Grasshopper.Tests;

public class RetainingWallComponentSmokeTests
{
    [Fact]
    public void RetainingWallComponent_RegistersExpectedParams()
    {
        var component = new RetainingWallComponent();

        Assert.Equal("Retaining Wall", component.Name);
        Assert.Equal("RetainWall", component.NickName);
        Assert.NotEqual(Guid.Empty, component.ComponentGuid);

        Assert.Equal(3, component.Params.Input.Count);
        Assert.Equal(4, component.Params.Output.Count);
        Assert.Equal("Wall Curves", component.Params.Input[1].Name);
        Assert.Equal("Max Wall Width", component.Params.Input[2].Name);
        Assert.Equal("Wall Breps", component.Params.Output[1].Name);
        Assert.Equal("Pairs", component.Params.Output[2].Name);
    }
}
