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

        Assert.Equal(10, component.Params.Input.Count);
        Assert.Equal(5, component.Params.Output.Count);
        Assert.Equal("Wall Curves", component.Params.Input[1].Name);
        Assert.Equal("Max Wall Width", component.Params.Input[2].Name);
        Assert.Equal("Wall Breps", component.Params.Output[1].Name);
        Assert.Equal("Pairs", component.Params.Output[2].Name);
        Assert.Equal("Terrain", component.Params.Output[4].Name);

        // The grading inputs were appended after Terrain rather than inserted before it, because a
        // saved definition binds its wires by index: the first four must not move.
        Assert.Equal("Terrain", component.Params.Input[3].Name);
        Assert.Equal("Grade Terrain", component.Params.Input[4].Name);
        Assert.Equal("Fill Slope", component.Params.Input[5].Name);
        Assert.Equal("Max Distance", component.Params.Input[9].Name);
    }
}
