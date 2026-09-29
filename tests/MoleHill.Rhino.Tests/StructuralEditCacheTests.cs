using System.Reflection;
using System.Text.Json;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using Rhino;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// Edits that cannot change the terrain must not rebuild it: an empty card, a renamed card, a result the
/// build writes back. At the 1 m park an empty card below Triangulate cost 92 s and renaming Remesh 37 s.
/// See docs/incremental-rebuild-design-2026-09-29.md, Layer 0.
/// </summary>
public class StructuralEditCacheTests
{
    [Fact]
    public void InertReason_GradePadWithNoResolvedBoundaries_SaysWhy()
    {
        var pad = new GradePadModifierDefinition();
        pad.Boundaries.ObjectIds.Add(Guid.NewGuid()); // referenced, but the object is gone

        Assert.Equal("Not applied — no boundaries selected.", InertReason(pad, CreateSnapshot()));
    }

    [Fact]
    public void InertReason_GradePadWithAResolvedBoundary_IsApplied()
    {
        var pad = new GradePadModifierDefinition();
        TerrainBuildSnapshot snapshot = CreateSnapshot();
        Resolve(snapshot, pad.Boundaries);

        Assert.Null(InertReason(pad, snapshot));
    }

    [Fact]
    public void InertReason_ProjectToWithATargetTerrain_IsApplied()
    {
        var projectTo = new ProjectToModifierDefinition { TargetTerrainId = Guid.NewGuid() };

        Assert.Null(InertReason(projectTo, CreateSnapshot()));
        Assert.NotNull(InertReason(new ProjectToModifierDefinition(), CreateSnapshot()));
    }

    [Fact]
    public void InertReason_ModifiersThatActWithoutSources_AreNeverInert()
    {
        TerrainBuildSnapshot snapshot = CreateSnapshot();

        Assert.Null(InertReason(new RemeshModifierDefinition(), snapshot));
        Assert.Null(InertReason(new SmoothModifierDefinition(), snapshot));
        Assert.Null(InertReason(new SimplifyModifierDefinition(), snapshot));
        // Sculpt reads its own cached stage mesh to start a session, so an empty sculpt must still run.
        Assert.Null(InertReason(new SculptModifierDefinition(), snapshot));
    }

    [Fact]
    public void FingerprintContract_RenamingACard_DoesNotChangeWhatIsHashed()
    {
        var remesh = new RemeshModifierDefinition { Label = "Remesh" };
        byte[] before = JsonSerializer.SerializeToUtf8Bytes(remesh, remesh.GetType(), TerrainBuildService.FingerprintJsonOptions);
        remesh.Label = "Remesh (renamed)";
        byte[] after = JsonSerializer.SerializeToUtf8Bytes(remesh, remesh.GetType(), TerrainBuildService.FingerprintJsonOptions);

        Assert.Equal(before, after);
    }

    [Fact]
    public void FingerprintContract_BuildInputs_StillChangeWhatIsHashed()
    {
        var remesh = new RemeshModifierDefinition { EdgeLength = 1.0 };
        byte[] before = JsonSerializer.SerializeToUtf8Bytes(remesh, remesh.GetType(), TerrainBuildService.FingerprintJsonOptions);
        remesh.EdgeLength = 2.0;
        byte[] after = JsonSerializer.SerializeToUtf8Bytes(remesh, remesh.GetType(), TerrainBuildService.FingerprintJsonOptions);

        Assert.NotEqual(before, after);
    }

    /// <summary>
    /// A value a stage computes and writes back onto its own definition must not feed that stage's
    /// fingerprint, or every real build is followed by one spurious re-run. The stair did exactly this.
    /// </summary>
    [Fact]
    public void ComputedProperties_OnEveryModifierType_AreNotBuildInputs()
    {
        var offenders = TerrainTypeRegistry.Modifiers
            .Select(descriptor => descriptor.DefinitionType)
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Where(property => property.Name.StartsWith("Computed", StringComparison.Ordinal))
            .Where(property => property.GetCustomAttribute<NotBuildInputAttribute>(inherit: true) == null)
            .Select(property => $"{property.DeclaringType!.Name}.{property.Name}")
            .Distinct()
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void StageKey_IsTheSameWhereverTheCardSits()
    {
        var pad = new GradePadModifierDefinition();
        var terrain = new TerrainDefinition();
        terrain.Modifiers.Add(pad);
        string before = TerrainStageKey.CreateModifier(pad);
        terrain.Modifiers.Insert(0, new GradePathModifierDefinition());

        Assert.Equal(before, TerrainStageKey.CreateModifier(terrain.Modifiers[1]));
    }

    private static string? InertReason(ModifierDefinition modifier, TerrainBuildSnapshot snapshot) =>
        TerrainTypeRegistry.ForModifierType(modifier.GetType())!.InertReason(modifier, snapshot);

    private static TerrainBuildSnapshot CreateSnapshot() => new()
    {
        Terrain = new TerrainDefinition(),
        ModelAbsoluteTolerance = 0.001,
        ModelUnitSystem = UnitSystem.Meters
    };

    private static void Resolve(TerrainBuildSnapshot snapshot, SourceReferenceSet set)
    {
        var id = Guid.NewGuid();
        set.ObjectIds.Add(id);
        snapshot.SourceObjects[set] = new List<ResolvedSourceObject>
        {
            new() { ObjectId = id, Geometry = null! }
        };
    }
}
