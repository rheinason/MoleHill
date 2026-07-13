using MoleHill.Rhino.Services;
using Rhino.DocObjects;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class RhinoSourceResolverTests
{
    [Theory]
    [InlineData(ActiveSpace.ModelSpace, true)]
    [InlineData(ActiveSpace.PageSpace, false)]
    public void IsSupportedSourceSpace_AcceptsOnlyModelSpace(ActiveSpace space, bool expected)
    {
        Assert.Equal(expected, RhinoSourceResolver.IsSupportedSourceSpace(space));
    }

    [Theory]
    [InlineData(ActiveSpace.ModelSpace, true)]
    [InlineData(ActiveSpace.PageSpace, false)]
    public void IsSupportedOrientSpace_AcceptsOnlyModelSpace(ActiveSpace space, bool expected)
    {
        Assert.Equal(expected, ProjectBaseCPlaneService.IsSupportedOrientSpace(space));
    }
}
