using MoleHill.Rhino.Model;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class GeometryInputModifierDefinitionTests
{
    [Fact]
    public void FromTriangulate_CopiesBoundaryPeelSettings()
    {
        var source = new TriangulateModifierDefinition
        {
            PeelBoundaryTriangles = false,
            MaxBoundaryEdgeLength = 25.0,
            MaxBoundaryAngleDegrees = 165.0,
            MaxBoundarySlopeDegrees = 70.0
        };

        AddGeometryModifierDefinition copy = AddGeometryModifierDefinition.FromTriangulate(source);

        Assert.False(copy.PeelBoundaryTriangles);
        Assert.Equal(25.0, copy.MaxBoundaryEdgeLength);
        Assert.Equal(165.0, copy.MaxBoundaryAngleDegrees);
        Assert.Equal(70.0, copy.MaxBoundarySlopeDegrees);
    }
}
