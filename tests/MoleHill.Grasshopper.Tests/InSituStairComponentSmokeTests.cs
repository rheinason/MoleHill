using MoleHill.Grasshopper.Components;
using Xunit;

namespace MoleHill.Grasshopper.Tests;

public class InSituStairComponentSmokeTests
{
    [Fact]
    public void InSituStairComponent_RegistersExpectedParams()
    {
        var component = new InSituStairComponent();

        Assert.Equal("In-Situ Stair", component.Name);
        Assert.Equal("InSituStair", component.NickName);
        Assert.NotEqual(Guid.Empty, component.ComponentGuid);

        Assert.Equal(5, component.Params.Input.Count);
        Assert.Equal(8, component.Params.Output.Count);
        Assert.Equal("Reference Surface", component.Params.Input[1].Name);
        Assert.Equal("Stair Breps", component.Params.Output[1].Name);
        Assert.Equal("Tread Depths", component.Params.Output[2].Name);
        Assert.Equal("Step Counts", component.Params.Output[3].Name);
        Assert.Equal("Warning", component.Params.Output[7].Name);
    }
}
