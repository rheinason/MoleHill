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

        // The source rows, then Contour Mode, then the shared boundary-peel rows. The panel re-positions
        // DemSurface, ContourMode and the peel rows into its own groups, but this order is the schema's
        // contract for every other consumer.
        Assert.Equal(
            new[]
            {
                "TinMesh", "DemSurface", "Points", "Breaklines", "Contours", "Boundary", "ContourMode",
                "PeelBoundaryTriangles", "MaxBoundaryEdgeLength", "MaxBoundaryAngleDegrees",
                "MaxBoundarySlopeDegrees",
            },
            keys);
    }

    /// <summary>
    /// Both geometry-input modifiers declare the peel rows from the one shared catalog, so the two cards
    /// cannot drift apart — and, being declared, the rows are covered by the schema guards that caught
    /// nothing while they were hand-written panel code.
    /// </summary>
    [Fact]
    public void BothGeometryInputModifiers_DeclareTheSameBoundaryPeelRows()
    {
        string[] triangulate = new TriangulateModifierDescriptor().Parameters
            .Select(static parameter => parameter.Key)
            .Where(static key => GeometryInputParameterCatalog.BoundaryPeelKeys.Contains(key))
            .ToArray();
        string[] addGeometry = new AddGeometryModifierDescriptor().Parameters
            .Select(static parameter => parameter.Key)
            .Where(static key => GeometryInputParameterCatalog.BoundaryPeelKeys.Contains(key))
            .ToArray();

        Assert.Equal(GeometryInputParameterCatalog.BoundaryPeelKeys, triangulate);
        Assert.Equal(GeometryInputParameterCatalog.BoundaryPeelKeys, addGeometry);
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
    public void EnsureBaseModifier_DoesNotMoveUserPlacedAddGeometry()
    {
        var triangulate = new TriangulateModifierDefinition();
        var remesh = new RemeshModifierDefinition();
        var addGeometry = new AddGeometryModifierDefinition();
        var terrain = new TerrainDefinition
        {
            Modifiers = new List<ModifierDefinition> { triangulate, remesh, addGeometry }
        };

        terrain.EnsureBaseModifier();

        Assert.Same(triangulate, terrain.Modifiers[0]);
        Assert.Same(remesh, terrain.Modifiers[1]);
        Assert.Same(addGeometry, terrain.Modifiers[2]);
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
