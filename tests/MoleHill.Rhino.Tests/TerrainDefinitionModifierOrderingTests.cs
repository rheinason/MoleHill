using MoleHill.Rhino.Model;
using Xunit;

namespace MoleHill.Rhino.Tests;

public sealed class TerrainDefinitionModifierOrderingTests
{
    [Fact]
    public void EnsureBaseModifier_MovesRetopoAfterDownstreamStages()
    {
        var triangulate = new TriangulateModifierDefinition();
        var retopo = new RetopoModifierDefinition();
        var sculpt = new SculptModifierDefinition();
        var terrain = new TerrainDefinition
        {
            Modifiers = [triangulate, retopo, sculpt]
        };

        terrain.EnsureBaseModifier();

        Assert.Equal([triangulate, sculpt, retopo], terrain.Modifiers);
    }

    [Fact]
    public void EnsureBaseModifier_PreservesNonRetopoModifierOrder()
    {
        var triangulate = new TriangulateModifierDefinition();
        var smooth = new SmoothModifierDefinition();
        var retopo = new RetopoModifierDefinition();
        var sculpt = new SculptModifierDefinition();
        var terrain = new TerrainDefinition
        {
            Modifiers = [triangulate, smooth, retopo, sculpt]
        };

        terrain.EnsureBaseModifier();

        Assert.Equal([triangulate, smooth, sculpt, retopo], terrain.Modifiers);
    }
}
