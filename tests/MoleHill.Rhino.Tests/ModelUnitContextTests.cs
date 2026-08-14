using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using MoleHill.Shared;
using Rhino;
using Xunit;

namespace MoleHill.Rhino.Tests;

public sealed class ModelUnitContextTests
{
    [Theory]
    [InlineData(UnitSystem.Meters, 1.0)]
    [InlineData(UnitSystem.Millimeters, 1000.0)]
    [InlineData(UnitSystem.Centimeters, 100.0)]
    [InlineData(UnitSystem.Feet, 3.280839895013123)]
    [InlineData(UnitSystem.Inches, 39.37007874015748)]
    [InlineData(UnitSystem.Kilometers, 0.001)]
    public void FromMeters_KnownUnit_ReturnsEquivalentModelLength(UnitSystem units, double expected)
    {
        ModelUnitContext context = ModelUnitContext.FromUnitSystem(units);

        Assert.True(context.IsSupported);
        Assert.Equal(expected, context.FromMeters(1.0), 10);
        Assert.Equal(1.0, context.ToMeters(expected), 10);
    }

    [Theory]
    [InlineData(UnitSystem.None)]
    [InlineData(UnitSystem.Unset)]
    public void FromUnitSystem_Unitless_ReturnsUnsupported(UnitSystem units)
    {
        Assert.False(ModelUnitContext.FromUnitSystem(units).IsSupported);
    }

    [Fact]
    public void Factories_CustomUnits_ConvertPhysicalDefaults()
    {
        var context = new ModelUnitContext(UnitSystem.CustomUnits, 0.5, "cu", 0.002);

        var path = Assert.IsType<GradePathModifierDefinition>(
            TerrainTypeRegistry.CreateModifier("grade-path", context));
        var wall = Assert.IsType<RetainingWallModifierDefinition>(
            TerrainTypeRegistry.CreateModifier("retaining-wall", context));
        var contour = Assert.IsType<ContourAnalysisDefinition>(
            AnalysisTypeRegistry.Create("contour", context));
        var scatter = Assert.IsType<ScatterObjectDefinition>(
            ObjectTypeRegistry.Create("scatter", context));

        Assert.Equal(4.0, path.Width, 10);
        Assert.Equal(2.0, wall.MaxWallWidth, 10);
        Assert.Equal(2.0, contour.Interval, 10);
        Assert.Equal(2.0, scatter.Spacing, 10);
        Assert.Equal(0.025, scatter.PerAreaDensity, 10);
    }

    [Fact]
    public void CommandOptionCache_Length_RetainsPhysicalValueAcrossUnits()
    {
        string key = "test.length." + Guid.NewGuid();
        ModelUnitContext millimetres = ModelUnitContext.FromUnitSystem(UnitSystem.Millimeters);
        ModelUnitContext feet = ModelUnitContext.FromUnitSystem(UnitSystem.Feet);

        Assert.Equal(2000.0, CommandOptionCache.GetLength(key, millimetres, 2.0), 10);

        CommandOptionCache.SetLength(key, millimetres, 1000.0);

        Assert.Equal(3.280839895013123, CommandOptionCache.GetLength(key, feet, 2.0), 10);
    }

    [Fact]
    public void Deserialize_LegacyTerrainWithCustomUnits_UsesPhysicalMigrationDefaults()
    {
        string json = $$"""
            {
              "schemaVersion": 23,
              "terrains": [
                {
                  "schemaVersion": 23,
                  "terrainId": "{{Guid.NewGuid()}}",
                  "name": "Legacy",
                  "globalTolerance": 0.0
                }
              ]
            }
            """;
        var context = new ModelUnitContext(UnitSystem.CustomUnits, 0.5, "cu", 0.002);

        TerrainDefinition terrain = Assert.Single(TerrainSerializer.Deserialize(json, context));

        Assert.Equal(0.5, terrain.GlobalTolerance, 10);
        Assert.Equal(TerrainDefinition.CurrentSchemaVersion, terrain.SchemaVersion);
    }
}
