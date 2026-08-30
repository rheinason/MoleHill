using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
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

        Assert.Equal(new[] { "TinMesh", "DemSurface", "Points", "Breaklines", "Contours", "Boundary", "ContourMode" }, keys);
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

    [Fact]
    public void DemSurface_SerializesAsTriangulateBaseInput()
    {
        Guid surfaceId = Guid.NewGuid();
        var triangulate = new TriangulateModifierDefinition
        {
            DemSurface = new SourceReferenceSet { ObjectIds = [surfaceId] },
            DemElevationScale = 0.3048,
            DemSourceFileName = "survey-dem.tif"
        };
        var terrain = new TerrainDefinition { Modifiers = [triangulate] };

        string json = TerrainSerializer.Serialize([terrain]);
        TerrainDefinition restored = Assert.Single(TerrainSerializer.Deserialize(json));
        TriangulateModifierDefinition restoredTriangulate = Assert.IsType<TriangulateModifierDefinition>(restored.Modifiers[0]);

        Assert.Equal(surfaceId, Assert.Single(restoredTriangulate.DemSurface.ObjectIds));
        Assert.Equal(0.3048, restoredTriangulate.DemElevationScale);
        Assert.Equal("survey-dem.tif", restoredTriangulate.DemSourceFileName);
    }

    [Fact]
    public void DemElevationScale_DocumentUnitScaling_RemainsConsistentWithMovedSurface()
    {
        var triangulate = new TriangulateModifierDefinition { DemElevationScale = 0.3048 };

        TerrainUnitScaler.Scale(triangulate, 10.0);

        Assert.Equal(3.048, triangulate.DemElevationScale, 12);
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
