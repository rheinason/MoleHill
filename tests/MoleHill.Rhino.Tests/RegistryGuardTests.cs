using System;
using System.Collections.Generic;
using System.Linq;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using Rhino;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// Locks in the plug-and-play invariant for the four self-registering type families. If a definition
/// subtype is added without its descriptor, a Kind is duplicated, or the registry-driven JSON resolver
/// stops round-tripping a type, one of these fails — the automated net the registries otherwise lack
/// (the panel/serializer are only otherwise exercised by hand in Rhino).
/// </summary>
public class RegistryGuardTests
{
    // Legacy modifier types kept for backward-compatible deserialization only: the serializer migrates
    // them away (TerrainSerializer.MigrateZones), so they intentionally have no descriptor.
    private static readonly HashSet<Type> LegacyModifierTypes = new()
    {
        typeof(MeshAreasModifierDefinition),
        typeof(MeshCollageModifierDefinition),
    };

    private static IEnumerable<Type> ConcreteSubtypes(Type baseType) =>
        baseType.Assembly.GetTypes().Where(t => !t.IsAbstract && baseType.IsAssignableFrom(t) && t != baseType);

    [Fact]
    public void EveryModifierSubtype_HasDescriptor_OrIsLegacyShim()
    {
        var registered = TerrainTypeRegistry.Modifiers.Select(d => d.DefinitionType).ToHashSet();
        var missing = ConcreteSubtypes(typeof(ModifierDefinition))
            .Where(t => !LegacyModifierTypes.Contains(t) && !registered.Contains(t))
            .ToList();

        Assert.True(missing.Count == 0, "Modifier types without a ModifierTypeDescriptor: " + string.Join(", ", missing.Select(t => t.Name)));
    }

    [Fact]
    public void EveryObjectSubtype_HasDescriptor()
    {
        var registered = ObjectTypeRegistry.Objects.Select(d => d.DefinitionType).ToHashSet();
        var missing = ConcreteSubtypes(typeof(TerrainObjectDefinition)).Where(t => !registered.Contains(t)).ToList();
        Assert.True(missing.Count == 0, "Object types without an ObjectTypeDescriptor: " + string.Join(", ", missing.Select(t => t.Name)));
    }

    [Fact]
    public void EveryMarkerSubtype_HasDescriptor()
    {
        var registered = MarkerTypeRegistry.Markers.Select(d => d.DefinitionType).ToHashSet();
        var missing = ConcreteSubtypes(typeof(MarkerDefinition)).Where(t => !registered.Contains(t)).ToList();
        Assert.True(missing.Count == 0, "Marker types without a MarkerTypeDescriptor: " + string.Join(", ", missing.Select(t => t.Name)));
    }

    [Fact]
    public void EveryAnalysisSubtype_HasDescriptor()
    {
        var registered = AnalysisTypeRegistry.Analyses.Select(d => d.DefinitionType).ToHashSet();
        var missing = ConcreteSubtypes(typeof(AnalysisDefinition)).Where(t => !registered.Contains(t)).ToList();
        Assert.True(missing.Count == 0, "Analysis types without an AnalysisTypeDescriptor: " + string.Join(", ", missing.Select(t => t.Name)));
    }

    [Fact]
    public void AllKinds_AreUnique_PerFamily()
    {
        AssertUniqueKinds(TerrainTypeRegistry.Modifiers.Select(d => d.Kind), "modifier");
        AssertUniqueKinds(ObjectTypeRegistry.Objects.Select(d => d.Kind), "object");
        AssertUniqueKinds(MarkerTypeRegistry.Markers.Select(d => d.Kind), "marker");
        AssertUniqueKinds(AnalysisTypeRegistry.Analyses.Select(d => d.Kind), "analysis");
    }

    private static void AssertUniqueKinds(IEnumerable<string> kinds, string family)
    {
        var dups = kinds.GroupBy(k => k, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(dups.Count == 0, $"Duplicate {family} Kind(s): " + string.Join(", ", dups));
    }

    [Fact]
    public void DescriptorCreate_ReturnsItsDeclaredDefinitionType()
    {
        foreach (var d in TerrainTypeRegistry.Modifiers)
            Assert.IsType(d.DefinitionType, d.Create(UnitSystem.Meters));
        foreach (var d in ObjectTypeRegistry.Objects)
            Assert.IsType(d.DefinitionType, d.Create());
        foreach (var d in MarkerTypeRegistry.Markers)
            Assert.IsType(d.DefinitionType, d.Create());
        foreach (var d in AnalysisTypeRegistry.Analyses)
            Assert.IsType(d.DefinitionType, d.Create());
    }

    [Fact]
    public void DescriptorCreate_Remesh_DefaultsToAutoEdgeAndCreasePreservation()
    {
        var descriptor = TerrainTypeRegistry.Modifiers.Single(d => d.DefinitionType == typeof(RemeshModifierDefinition));

        var remesh = Assert.IsType<RemeshModifierDefinition>(descriptor.Create(UnitSystem.Meters));

        Assert.Equal("isotropic", remesh.Mode);
        Assert.Equal(0.0, remesh.EdgeLength, 9);
        Assert.Equal(30.0, remesh.CreaseAngle, 9);
    }

    [Fact]
    public void DescriptorCreate_Sculpt_DefaultsToDynTopoOffAndHidesTuningParameters()
    {
        var descriptor = TerrainTypeRegistry.Modifiers.Single(d => d.DefinitionType == typeof(SculptModifierDefinition));

        var sculpt = Assert.IsType<SculptModifierDefinition>(descriptor.Create(UnitSystem.Meters));

        Assert.False(new SculptModifierDefinition().DynTopo);
        Assert.False(sculpt.DynTopo);
        Assert.DoesNotContain(descriptor.Parameters, p => p.Key is "DynTopo" or "DetailSize");
    }

    [Fact]
    public void AllRegisteredTypes_RoundTripThroughSerializer_WithCorrectDiscriminators()
    {
        var terrain = new TerrainDefinition();
        terrain.Modifiers = TerrainTypeRegistry.Modifiers.Select(d => d.Create(UnitSystem.Meters)).ToList();
        terrain.Objects = ObjectTypeRegistry.Objects.Select(d => d.Create()).ToList();
        terrain.Markers = MarkerTypeRegistry.Markers.Select(d => d.Create()).ToList();
        terrain.Analyses = AnalysisTypeRegistry.Analyses.Select(d => d.Create()).ToList();

        string json = TerrainSerializer.Serialize(new[] { terrain });

        // Every Kind appears as a "$type" discriminator in the JSON.
        foreach (var kind in TerrainTypeRegistry.Modifiers.Select(d => d.Kind)
                     .Concat(ObjectTypeRegistry.Objects.Select(d => d.Kind))
                     .Concat(MarkerTypeRegistry.Markers.Select(d => d.Kind))
                     .Concat(AnalysisTypeRegistry.Analyses.Select(d => d.Kind)))
        {
            Assert.Contains($"\"$type\": \"{kind}\"", json);
        }

        var restored = TerrainSerializer.Deserialize(json).Single();

        AssertAllPresent(TerrainTypeRegistry.Modifiers.Select(d => d.DefinitionType), restored.Modifiers.Select(m => m.GetType()), "modifier");
        AssertAllPresent(ObjectTypeRegistry.Objects.Select(d => d.DefinitionType), restored.Objects.Select(o => o.GetType()), "object");
        AssertAllPresent(MarkerTypeRegistry.Markers.Select(d => d.DefinitionType), restored.Markers.Select(m => m.GetType()), "marker");
        AssertAllPresent(AnalysisTypeRegistry.Analyses.Select(d => d.DefinitionType), restored.Analyses.Select(a => a.GetType()), "analysis");
    }

    private static void AssertAllPresent(IEnumerable<Type> expected, IEnumerable<Type> actual, string family)
    {
        var actualSet = actual.ToHashSet();
        var missing = expected.Where(t => !actualSet.Contains(t)).ToList();
        Assert.True(missing.Count == 0, $"{family} types lost in round-trip: " + string.Join(", ", missing.Select(t => t.Name)));
    }

    [Fact]
    public void LegacyMeshAreasAndCollageDocument_StillDeserializes_AndMigratesAway()
    {
        const string legacyJson = """
        {
          "schemaVersion": 21,
          "terrains": [
            {
              "terrainId": "11111111-1111-1111-1111-111111111111",
              "name": "Legacy",
              "modifiers": [
                { "$type": "triangulate" },
                { "$type": "mesh-areas", "boundaries": { "objectIds": [], "layerPaths": [] } },
                { "$type": "mesh-collage", "zones": [ { "name": "Z1", "boundaries": { "objectIds": [], "layerPaths": [] }, "colorArgb": -1 } ] },
                { "$type": "remesh", "minAngle": 18.0 }
              ]
            }
          ]
        }
        """;

        var terrain = TerrainSerializer.Deserialize(legacyJson).Single();

        Assert.DoesNotContain(terrain.Modifiers, m => m is MeshAreasModifierDefinition or MeshCollageModifierDefinition);
        Assert.Contains(terrain.Modifiers, m => m is RemeshModifierDefinition);
        Assert.Contains(terrain.Zones, z => z.Name == "Z1");
    }

    [Fact]
    public void LegacyRemeshDocument_MaxAreaOnly_MigratesToEquivalentEdgeLength()
    {
        const string legacyJson = """
        {
          "schemaVersion": 21,
          "terrains": [
            {
              "terrainId": "22222222-2222-2222-2222-222222222222",
              "name": "Legacy Remesh",
              "schemaVersion": 21,
              "modifiers": [
                { "$type": "triangulate" },
                { "$type": "remesh", "localRefine": false, "minAngle": 20.0, "maxArea": 20.0, "mergeDistance": 0.05 }
              ]
            }
          ]
        }
        """;

        var terrain = TerrainSerializer.Deserialize(legacyJson).Single();
        var remesh = Assert.IsType<RemeshModifierDefinition>(terrain.Modifiers.Single(m => m is RemeshModifierDefinition));

        // maxArea 20 → edge of the equilateral triangle with that area: sqrt(20·4/√3) ≈ 6.796.
        Assert.Equal(Math.Sqrt(20.0 * 4.0 / Math.Sqrt(3.0)), remesh.EdgeLength, 6);
        Assert.Equal(0.0, remesh.MaxArea, 9);
        Assert.Equal(0.0, remesh.CreaseAngle, 9); // unknown legacy props (localRefine, minAngle, mergeDistance) are dropped
        Assert.Equal("isotropic", remesh.Mode);
    }

    [Fact]
    public void CurrentSchemaRemeshDocument_ExplicitEdgeLength_IsNotRemigrated()
    {
        string currentJson = """
        {
          "schemaVersion": 22,
          "terrains": [
            {
              "terrainId": "33333333-3333-3333-3333-333333333333",
              "name": "Current Remesh",
              "schemaVersion": 22,
              "modifiers": [
                { "$type": "triangulate" },
                { "$type": "remesh", "edgeLength": 2.5, "creaseAngle": 30.0 }
              ]
            }
          ]
        }
        """;

        var terrain = TerrainSerializer.Deserialize(currentJson).Single();
        var remesh = Assert.IsType<RemeshModifierDefinition>(terrain.Modifiers.Single(m => m is RemeshModifierDefinition));

        Assert.Equal(2.5, remesh.EdgeLength, 9);
        Assert.Equal(30.0, remesh.CreaseAngle, 9);
        Assert.Equal("isotropic", remesh.Mode);
    }

    [Fact]
    public void LegacyRemeshDocument_WithoutModeField_DefaultsToIsotropic()
    {
        string legacyJson = """
        {
          "schemaVersion": 22,
          "terrains": [
            {
              "terrainId": "44444444-4444-4444-4444-444444444444",
              "name": "Pre-Mode Remesh",
              "schemaVersion": 22,
              "modifiers": [
                { "$type": "triangulate" },
                { "$type": "remesh", "edgeLength": 3.0, "creaseAngle": 20.0 }
              ]
            }
          ]
        }
        """;

        var terrain = TerrainSerializer.Deserialize(legacyJson).Single();
        var remesh = Assert.IsType<RemeshModifierDefinition>(terrain.Modifiers.Single(m => m is RemeshModifierDefinition));

        Assert.Equal("isotropic", remesh.Mode);
        Assert.Equal(3.0, remesh.EdgeLength, 9);
    }
}
