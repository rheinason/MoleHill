using MoleHill.Rhino.Model;
using Rhino;

namespace MoleHill.Rhino.Services;

// Terrain lifecycle: create (from sources, points or a TIN mesh), duplicate, delete, and convert to
// plain Rhino geometry.
internal sealed partial class TerrainController
{
    public TerrainDefinition? CreateTerrain(RhinoDoc doc, bool seedFromSelection)
    {
        using TerrainUndoTransaction? undo = BeginTerrainUndoTransaction(doc, "Create MoleHill Terrain");
        return CreateTerrainCore(doc, seedFromSelection);
    }

    private TerrainDefinition? CreateTerrainCore(RhinoDoc doc, bool seedFromSelection)
    {
        if (!ModelUnitGuard.TryGet(doc, out var unitContext))
            return null;

        var state = GetState(doc);
        var terrain = new TerrainDefinition
        {
            Name = NextTerrainName(state.Terrains),
            GlobalTolerance = TerrainTolerancePolicy.DefaultDetailSize(unitContext)
        };
        terrain.EnsureBaseModifier();

        if (seedFromSelection && terrain.Modifiers[0] is TriangulateModifierDefinition triangulate)
        {
            triangulate.Points.ReplaceObjects(GetSelectedPointObjectIds(doc));
            triangulate.Breaklines.ReplaceObjects(GetSelectedCurveObjectIds(doc));
        }

        state.Terrains.Add(terrain);
        state.SelectedTerrainId = terrain.TerrainId;
        Save(doc, state);

        if (terrain.LiveUpdateEnabled)
            ScheduleRebuild(doc, terrain.TerrainId);
        else
            RaiseStateChanged();

        return terrain;
    }

    public TerrainDefinition? CreateTerrainFromPointIds(RhinoDoc doc, IEnumerable<Guid> pointIds, string? name = null)
    {
        using TerrainUndoTransaction? undo = BeginTerrainUndoTransaction(doc, "Create MoleHill Terrain");
        TerrainDefinition? terrain = CreateTerrainCore(doc, seedFromSelection: false);
        if (terrain == null)
            return null;

        if (!string.IsNullOrWhiteSpace(name))
            terrain.Name = UniqueTerrainName(GetState(doc), terrain, name);
        if (terrain.Modifiers[0] is TriangulateModifierDefinition triangulate)
            triangulate.Points.ReplaceObjects(pointIds);

        DocumentState state = GetState(doc);
        Save(doc, state);
        if (terrain.LiveUpdateEnabled)
            ScheduleRebuild(doc, terrain.TerrainId);
        else
            RaiseStateChanged();
        return terrain;
    }

    public TerrainDefinition? CreateTerrainFromTinMeshId(RhinoDoc doc, Guid meshId, string? name = null)
    {
        if (meshId == Guid.Empty || doc.Objects.FindId(meshId)?.Geometry is not global::Rhino.Geometry.Mesh)
            return null;

        using TerrainUndoTransaction? undo = BeginTerrainUndoTransaction(doc, "Create MoleHill Terrain");
        TerrainDefinition? terrain = CreateTerrainCore(doc, seedFromSelection: false);
        if (terrain == null)
            return null;

        if (!string.IsNullOrWhiteSpace(name))
            terrain.Name = UniqueTerrainName(GetState(doc), terrain, name);
        if (terrain.Modifiers[0] is TriangulateModifierDefinition triangulate)
            triangulate.TinMesh.ReplaceObjects([meshId]);

        DocumentState state = GetState(doc);
        Save(doc, state);
        if (terrain.LiveUpdateEnabled)
            ScheduleRebuild(doc, terrain.TerrainId);
        else
            RaiseStateChanged();
        return terrain;
    }

    public void DeleteTerrain(RhinoDoc doc, Guid terrainId)
    {
        var state = GetState(doc);
        var terrain = state.Terrains.FirstOrDefault(item => item.TerrainId == terrainId);
        if (terrain == null)
            return;

        using TerrainUndoTransaction? undo = BeginTerrainUndoTransaction(doc, "Delete MoleHill Terrain");
        RestoreTerrainObjectPlacements(doc, terrain);
        DeleteOwnedObjects(doc, terrain);
        PurgeOrphanedOwnedObjects(doc, terrain);
        TerrainLayerCleanup.RemoveEmptyLayers(doc, terrain.Name, LayerRoleService.GetTable(doc, terrain));
        RemoveRuntimeCache(doc.RuntimeSerialNumber, terrainId);
        RemoveRebuildState(doc.RuntimeSerialNumber, terrainId);
        state.Terrains.Remove(terrain);
        RemoveTerrainReferences(doc, state, terrainId);
        if (state.SelectedTerrainId == terrainId)
            state.SelectedTerrainId = state.Terrains.FirstOrDefault()?.TerrainId;

        Save(doc, state);
        NotifyRenderMeshesChanged(doc);
        doc.Views.Redraw();
    }

    public void ConvertToRhino(RhinoDoc doc, Guid terrainId)
    {
        if (!ModelUnitGuard.TryGet(doc, out MoleHill.Shared.ModelUnitContext unitContext))
            return;

        var state = GetState(doc);
        var terrain = state.Terrains.FirstOrDefault(item => item.TerrainId == terrainId);
        if (terrain == null)
            return;

        using TerrainUndoTransaction? undo = BeginTerrainUndoTransaction(doc, "Detach MoleHill Terrain");

        BakeTerrain(doc, terrainId);
        RestoreTerrainObjectPlacements(doc, terrain);
        RemoveRuntimeCache(doc.RuntimeSerialNumber, terrainId);
        RemoveRebuildState(doc.RuntimeSerialNumber, terrainId);
        state.Terrains.Remove(terrain);
        RemoveTerrainReferences(doc, state, terrainId);
        if (state.SelectedTerrainId == terrainId)
            state.SelectedTerrainId = state.Terrains.FirstOrDefault()?.TerrainId;

        Save(doc, state);
        NotifyRenderMeshesChanged(doc);
        doc.Views.Redraw();
    }

    public TerrainDefinition? DuplicateTerrain(RhinoDoc doc, Guid terrainId)
    {
        if (!ModelUnitGuard.TryGet(doc, out _))
            return null;

        var state = GetState(doc);
        var terrain = state.Terrains.FirstOrDefault(t => t.TerrainId == terrainId);
        if (terrain == null)
            return null!;

        using TerrainUndoTransaction? undo = BeginTerrainUndoTransaction(doc, "Duplicate MoleHill Terrain");
        var json = System.Text.Json.JsonSerializer.Serialize(terrain, TerrainSerializer.SharedOptions);
        var clone = System.Text.Json.JsonSerializer.Deserialize<TerrainDefinition>(json, TerrainSerializer.SharedOptions)!;
        clone.TerrainId = Guid.NewGuid();
        clone.Name = UniqueTerrainName(state, clone, terrain.Name + " Copy");
        clone.OutputObjectIds.Clear();
        clone.ZoneObjectIds.Clear();
        clone.AuxiliaryObjectIds.Clear();
        clone.MarkerObjectIds.Clear();
        clone.BakedObjectIds.Clear();
        clone.LastBuildMessage = null;
        clone.LastStructuredDiagnostics.Clear();
        clone.LastBuildUtc = null;

        foreach (var modifier in clone.Modifiers)
            modifier.Id = Guid.NewGuid();
        foreach (var marker in clone.Markers)
            marker.Id = Guid.NewGuid();
        foreach (var obj in clone.Objects)
        {
            obj.Id = Guid.NewGuid();
            obj.PlacementStates.Clear();
        }
        foreach (var zone in clone.Zones)
            zone.ZoneId = Guid.NewGuid();
        foreach (var analysis in clone.Analyses)
            analysis.Id = Guid.NewGuid();
        foreach (var annotation in clone.Annotations)
            annotation.Id = Guid.NewGuid();

        string templateName = terrain.LayerTemplateName ?? string.Empty;
        LayerRoleTable oldTable = LayerRoleService.GetTable(doc, templateName, terrain.Name);
        LayerRoleService.EnsureTemplateLayers(doc, clone);
        LayerRoleTable newTable = LayerRoleService.GetTable(doc, clone);
        Dictionary<Guid, Guid> copies = CopyOwnedInputs(doc, terrain, clone, oldTable, newTable);
        TerrainOwnership.Remap(clone, terrain.Name, oldTable, copies);

        state.Terrains.Add(clone);
        state.SelectedTerrainId = clone.TerrainId;
        Save(doc, state);

        if (clone.LiveUpdateEnabled)
            ScheduleRebuild(doc, clone.TerrainId);

        return clone;
    }

    private static string UniqueTerrainName(DocumentState state, TerrainDefinition terrain, string desired) =>
        TerrainLayerNaming.NextFreeName(
            desired,
            candidate => state.Terrains.Any(other =>
                other.TerrainId != terrain.TerrainId && TerrainLayerNaming.SameRoot(other.Name, candidate)));

    private static string NextTerrainName(IEnumerable<TerrainDefinition> terrains)
    {
        int index = 1;
        var names = terrains.Select(terrain => terrain.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        while (names.Contains($"Terrain {index}"))
            index++;

        return $"Terrain {index}";
    }
}
