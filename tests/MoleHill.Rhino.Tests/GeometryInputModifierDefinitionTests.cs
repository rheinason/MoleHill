using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class GeometryInputModifierDefinitionTests
{
    [Fact]
    public void TriangulateParameters_PlaceContourModeAfterSourceRows()
    {
        string[] keys = new TriangulateModifierDescriptor()
            .Parameters
            .Select(static parameter => parameter.Key)
            .ToArray();

        Assert.Equal(new[] { "TinMesh", "Points", "Breaklines", "Contours", "Boundary", "ContourMode" }, keys);
    }

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

    [Theory]
    [InlineData(TriangulateModifierDefinition.AutoContourMode, 249_999, true)]
    [InlineData(TriangulateModifierDefinition.AutoContourMode, 250_000, false)]
    [InlineData(TriangulateModifierDefinition.ConstrainedContourMode, 2_000_000, true)]
    [InlineData(TriangulateModifierDefinition.VerticesOnlyContourMode, 10, false)]
    public void ShouldConstrainContours_ModeAndSize_ReturnsExpectedResult(
        string mode,
        int contourVertexCount,
        bool expected)
    {
        var modifier = new TriangulateModifierDefinition { ContourMode = mode };

        Assert.Equal(expected, modifier.ShouldConstrainContours(contourVertexCount));
    }
}
