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
        if (!RestoreTerrainObjectPlacements(doc, terrain))
        {
            RhinoApp.WriteLine("Some placed objects could not be restored. The terrain was retained; unlock or Release the objects before deleting it.");
            Save(doc, state);
            return;
        }
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
        // Detach releases placed source objects in their current pose.
        foreach (var definition in terrain.Objects)
            definition.PlacementStates.Clear();
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
        TerrainTransfer.RenewIdentity(clone);
        clone.Name = UniqueTerrainName(state, clone, terrain.Name + " Copy");
        clone.OutputObjectIds.Clear();
        clone.ZoneObjectIds.Clear();
        clone.AuxiliaryObjectIds.Clear();
        clone.MarkerObjectIds.Clear();
        clone.BakedObjectIds.Clear();
        clone.LastBuildMessage = null;
        clone.LastStructuredDiagnostics.Clear();
        clone.LastBuildUtc = null;

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

    /// <summary>The terrain's definition as a transfer file (layer paths only), or null if it is gone.</summary>
    public string? ExportTerrainDefinition(RhinoDoc doc, Guid terrainId)
    {
        TerrainDefinition? terrain = GetState(doc).Terrains.FirstOrDefault(item => item.TerrainId == terrainId);
        return terrain == null ? null : TerrainTransfer.Export(terrain, doc.ModelUnitSystem);
    }

    /// <summary>Reads a transfer file for this document, without adding anything (the import preview).</summary>
    public TerrainImportPreview? PreviewTerrainImport(RhinoDoc doc, string json)
    {
        if (!ModelUnitGuard.TryGet(doc, out var unitContext))
            return null;

        return TerrainTransfer.Read(
            json,
            unitContext,
            path => doc.Layers.FindByFullPath(path, RhinoMath.UnsetIntIndex) >= 0);
    }

    /// <summary>Adds a previewed import as a new terrain, selected and built like a new one.</summary>
    public TerrainDefinition? ImportTerrain(RhinoDoc doc, TerrainImportPreview preview)
    {
        if (!ModelUnitGuard.TryGet(doc, out _))
            return null;

        using TerrainUndoTransaction? undo = BeginTerrainUndoTransaction(doc, "Import MoleHill Terrain");
        var state = GetState(doc);
        TerrainDefinition terrain = preview.Terrain;
        terrain.Name = UniqueTerrainName(state, terrain, string.IsNullOrWhiteSpace(terrain.Name) ? NextTerrainName(state.Terrains) : terrain.Name);
        LayerRoleService.EnsureTemplateLayers(doc, terrain);

        state.Terrains.Add(terrain);
        state.SelectedTerrainId = terrain.TerrainId;
        Save(doc, state);

        if (terrain.LiveUpdateEnabled)
            ScheduleRebuild(doc, terrain.TerrainId);
        else
            RaiseStateChanged();

        return terrain;
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
