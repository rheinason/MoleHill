using MoleHill.Grasshopper.Components;
using Xunit;

namespace MoleHill.Grasshopper.Tests;

public class GradeLineComponentSmokeTests
{
    [Fact]
    public void GradeLineComponent_RegistersExpectedParams()
    {
        var component = new GradeLineComponent();

        Assert.Equal("Grade Line", component.Name);
        Assert.Equal("GradeLine", component.NickName);
        Assert.NotEqual(Guid.Empty, component.ComponentGuid);

        Assert.Equal(10, component.Params.Input.Count);
        Assert.Equal(5, component.Params.Output.Count);
        Assert.Equal("Lines", component.Params.Input[1].Name);
        Assert.Equal("Fill Slope", component.Params.Input[2].Name);
        Assert.Equal("Cut Slope", component.Params.Input[3].Name);

        // The four per-side overrides are what make an asymmetric section reachable from Grasshopper.
        Assert.Equal("Left Cut", component.Params.Input[5].Name);
        Assert.Equal("Left Fill", component.Params.Input[6].Name);
        Assert.Equal("Right Cut", component.Params.Input[7].Name);
        Assert.Equal("Right Fill", component.Params.Input[8].Name);
        Assert.Equal("Terrain", component.Params.Input[9].Name);
        Assert.Equal("Terrain", component.Params.Output[4].Name);
    }

    /// <summary>A new component GUID must not collide with one Grasshopper already knows.</summary>
    [Fact]
    public void GradeLineComponent_HasItsOwnGuid()
    {
        Assert.NotEqual(new GradePathComponent().ComponentGuid, new GradeLineComponent().ComponentGuid);
    }
}
