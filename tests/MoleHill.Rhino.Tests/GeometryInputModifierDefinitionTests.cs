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
                "TinMesh", "DemSurface", "Points", "Breaklines", "Contours",
                "OuterBoundaries", "HideBoundaries", "ShowBoundaries", "DataClipBoundaries", "ContourMode",
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
    public void AddGeometryParameters_DoNotExposeBoundaryRoleRows()
    {
        string[] keys = new AddGeometryModifierDescriptor().Parameters
            .Select(parameter => parameter.Key)
            .ToArray();

        Assert.DoesNotContain("OuterBoundaries", keys);
        Assert.DoesNotContain("HideBoundaries", keys);
        Assert.DoesNotContain("ShowBoundaries", keys);
        Assert.DoesNotContain("DataClipBoundaries", keys);
    }

    [Fact]
    public void BoundaryRoles_RoundTripAllObjectAndLayerReferences()
    {
        Guid outerId = Guid.NewGuid();
        Guid hideId = Guid.NewGuid();
        var triangulate = new TriangulateModifierDefinition
        {
            OuterBoundaries = new SourceReferenceSet { ObjectIds = [outerId], LayerPaths = ["Survey::Outer"] },
            HideBoundaries = new SourceReferenceSet { ObjectIds = [hideId] },
            ShowBoundaries = new SourceReferenceSet { LayerPaths = ["Survey::Show"] },
            DataClipBoundaries = new SourceReferenceSet { LayerPaths = ["Survey::Clip"] }
        };

        string json = TerrainSerializer.Serialize([new TerrainDefinition { Modifiers = [triangulate] }]);
        TriangulateModifierDefinition restored = Assert.IsType<TriangulateModifierDefinition>(
            Assert.Single(TerrainSerializer.Deserialize(json)).Modifiers[0]);

        Assert.Equal(outerId, Assert.Single(restored.OuterBoundaries.ObjectIds));
        Assert.Equal("Survey::Outer", Assert.Single(restored.OuterBoundaries.LayerPaths));
        Assert.Equal(hideId, Assert.Single(restored.HideBoundaries.ObjectIds));
        Assert.Equal("Survey::Show", Assert.Single(restored.ShowBoundaries.LayerPaths));
        Assert.Equal("Survey::Clip", Assert.Single(restored.DataClipBoundaries.LayerPaths));
    }

    [Fact]
    public void LegacyBoundaries_MigrateFromTriangulateAndAddGeometryToOuterOnly()
    {
        Guid triangulateBoundary = Guid.NewGuid();
        Guid addBoundary = Guid.NewGuid();
        var terrain = new TerrainDefinition
        {
            SchemaVersion = 31,
            Modifiers =
            [
                new TriangulateModifierDefinition
                {
                    LegacyBoundary = new SourceReferenceSet { ObjectIds = [triangulateBoundary] }
                },
                new AddGeometryModifierDefinition
                {
                    LegacyBoundary = new SourceReferenceSet { ObjectIds = [addBoundary], LayerPaths = ["Old::Boundary"] }
                }
            ]
        };

        string json = TerrainSerializer.Serialize([terrain]);
        TerrainDefinition restoredTerrain = Assert.Single(TerrainSerializer.Deserialize(json));
        TriangulateModifierDefinition restored = Assert.IsType<TriangulateModifierDefinition>(restoredTerrain.Modifiers[0]);

        Assert.Equal(new[] { triangulateBoundary, addBoundary }, restored.OuterBoundaries.ObjectIds);
        Assert.Equal("Old::Boundary", Assert.Single(restored.OuterBoundaries.LayerPaths));
        Assert.False(restored.DataClipBoundaries.HasReferences);
        Assert.All(restoredTerrain.Modifiers.OfType<GeometryInputModifierDefinition>(), input => Assert.Null(input.LegacyBoundary));
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
