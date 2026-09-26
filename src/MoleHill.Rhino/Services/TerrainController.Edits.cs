using System.Text.Json;
using MoleHill.Rhino.Model;
using Rhino;

namespace MoleHill.Rhino.Services;

// Definition edits: the modifier, analysis, annotation and object stacks, and MutateTerrain, the one
// path every panel edit takes (undo, save, rebuild scheduling, render-appearance refresh).
internal sealed partial class TerrainController
{
    private readonly record struct TerrainRenderAppearance(
        int TerrainColorArgb,
        int OutputTransparencyPercent,
        string? LayerTemplateName,
        bool ShowTerrainMesh,
        bool ShowZoneMeshes,
        bool ShowAnalysisOutputs)
    {
        public static TerrainRenderAppearance Capture(TerrainDefinition terrain) => new(
            terrain.TerrainColorArgb,
            terrain.OutputTransparencyPercent,
            terrain.LayerTemplateName,
            terrain.ShowTerrainMesh,
            terrain.ShowZoneMeshes,
            terrain.ShowAnalysisOutputs);
    }

    public void AddModifier(RhinoDoc doc, Guid terrainId, string modifierKind)
    {
        MutateTerrain(doc, terrainId, terrain =>
        {
            var modifier = Registry.TerrainTypeRegistry.CreateModifier(
                modifierKind,
                MoleHill.Shared.ModelUnitContext.FromDocument(doc));
            if (modifier != null)
                terrain.Modifiers.Add(modifier);
        });
    }

    public void RemoveModifier(RhinoDoc doc, Guid terrainId, Guid modifierId)
    {
        MutateTerrain(doc, terrainId, terrain =>
        {
            var modifier = terrain.Modifiers.FirstOrDefault(item => item.Id == modifierId);
            if (modifier == null)
                return;

            if (IsPinnedBaseTriangulate(terrain, modifier))
                return;

            terrain.Modifiers.Remove(modifier);
            terrain.EnsureBaseModifier();
        });
    }

    public void MoveModifier(RhinoDoc doc, Guid terrainId, Guid modifierId, int direction)
    {
        MutateTerrain(doc, terrainId, terrain =>
        {
            int index = terrain.Modifiers.FindIndex(item => item.Id == modifierId);
            if (index < 0)
                return;

            var modifier = terrain.Modifiers[index];
            if (IsPinnedBaseTriangulate(terrain, modifier))
                return;

            int minimumIndex = terrain.Modifiers.Count > 0 && terrain.Modifiers[0] is TriangulateModifierDefinition ? 1 : 0;
            int targetIndex = Math.Clamp(index + direction, minimumIndex, terrain.Modifiers.Count - 1);
            if (targetIndex == index)
                return;

            terrain.Modifiers.RemoveAt(index);
            terrain.Modifiers.Insert(targetIndex, modifier);
        });
    }

    public void DuplicateModifier(RhinoDoc doc, Guid terrainId, Guid modifierId)
    {
        MutateTerrain(doc, terrainId, terrain =>
        {
            int index = terrain.Modifiers.FindIndex(item => item.Id == modifierId);
            if (index < 0)
                return;

            var modifier = terrain.Modifiers[index];
            if (IsPinnedBaseTriangulate(terrain, modifier))
                return;

            var clone = CloneModifier(modifier);
            if (clone == null)
                return;

            clone.Id = Guid.NewGuid();
            clone.Label += " Copy";
            terrain.Modifiers.Insert(index + 1, clone);
        });
    }

    public void DuplicateAnalysis(RhinoDoc doc, Guid terrainId, Guid analysisId)
    {
        MutateTerrain(doc, terrainId, terrain =>
        {
            int index = terrain.Analyses.FindIndex(item => item.Id == analysisId);
            if (index < 0)
                return;

            var clone = CloneAnalysis(terrain.Analyses[index]);
            if (clone == null)
                return;

            clone.Id = Guid.NewGuid();
            clone.Label += " Copy";
            terrain.Analyses.Insert(index + 1, clone);
        }, scheduleRebuild: false);
    }

    public void DuplicateAnnotation(RhinoDoc doc, Guid terrainId, Guid annotationId)
    {
        MutateTerrain(doc, terrainId, terrain =>
        {
            int index = terrain.Annotations.FindIndex(item => item.Id == annotationId);
            if (index < 0)
                return;

            var clone = CloneAnnotation(terrain.Annotations[index]);
            if (clone == null)
                return;

            clone.Id = Guid.NewGuid();
            clone.Label += " Copy";
            terrain.Annotations.Insert(index + 1, clone);
        }, scheduleRebuild: false);
    }

    public void AddObjectDefinition(RhinoDoc doc, Guid terrainId, string objectKind)
    {
        MutateTerrain(doc, terrainId, terrain =>
        {
            TerrainObjectDefinition definition = Registry.ObjectTypeRegistry.Create(
                    objectKind,
                    MoleHill.Shared.ModelUnitContext.FromDocument(doc))
                ?? throw new InvalidOperationException($"Unknown object definition kind '{objectKind}'.");

            terrain.Objects.Insert(0, definition);
        });
    }

    public void DuplicateObjectDefinition(RhinoDoc doc, Guid terrainId, Guid objectDefinitionId)
    {
        MutateTerrain(doc, terrainId, terrain =>
        {
            int index = terrain.Objects.FindIndex(item => item.Id == objectDefinitionId);
            if (index < 0)
                return;

            var definition = terrain.Objects[index];
            var clone = CloneTerrainObjectDefinition(definition);
            if (clone == null)
                return;

            clone.Id = Guid.NewGuid();
            clone.Name += " Copy";
            clone.PlacementStates = new List<TerrainObjectPlacementState>();
            terrain.Objects.Insert(index + 1, clone);
        });
    }

    public void RemoveObjectDefinition(RhinoDoc doc, Guid terrainId, Guid objectDefinitionId)
    {
        var state = GetState(doc);
        var terrain = state.Terrains.FirstOrDefault(item => item.TerrainId == terrainId);
        if (terrain == null)
            return;

        var definition = terrain.Objects.FirstOrDefault(item => item.Id == objectDefinitionId);
        if (definition == null)
            return;

        using TerrainUndoTransaction? undo = BeginTerrainUndoTransaction(doc, "Remove MoleHill Object Definition");
        RestorePlacementStates(doc, definition.PlacementStates);
        terrain.Objects.Remove(definition);
        terrain.EnsureBaseModifier();

        bool shouldScheduleRebuild = terrain.LiveUpdateEnabled;
        Save(doc, state, raiseStateChanged: !shouldScheduleRebuild);
        if (shouldScheduleRebuild)
            ScheduleRebuild(doc, terrain.TerrainId);
    }

    public void MutateTerrain(
        RhinoDoc doc,
        Guid terrainId,
        Action<TerrainDefinition> mutator,
        bool scheduleRebuild = true,
        bool deferDocumentSave = false,
        bool suppressImmediateUiRefresh = false,
        string undoDescription = "Edit MoleHill Terrain")
    {
        if (!ModelUnitGuard.TryGet(doc, out MoleHill.Shared.ModelUnitContext unitContext))
            return;

        var state = GetState(doc);
        var terrain = state.Terrains.FirstOrDefault(item => item.TerrainId == terrainId);
        if (terrain == null)
            return;

        TerrainUndoTransaction? undoTransaction = null;
        if (deferDocumentSave)
            EnsurePendingTerrainEdit(doc, undoDescription);
        else if (!_pendingTerrainEdits.ContainsKey(doc.RuntimeSerialNumber))
            undoTransaction = BeginTerrainUndoTransaction(doc, undoDescription);

        try
        {
            string previousName = terrain.Name;
            int previousColorArgb = terrain.TerrainColorArgb;
            TerrainRenderAppearance previousRenderAppearance = TerrainRenderAppearance.Capture(terrain);
            mutator(terrain);
            terrain.EnsureBaseModifier();
            bool shouldScheduleRebuild = scheduleRebuild && terrain.LiveUpdateEnabled;
            if (deferDocumentSave)
                QueuePendingDocumentSave(doc.RuntimeSerialNumber, LiveEditSaveDebounceMs);
            else
                Save(doc, state, raiseStateChanged: !shouldScheduleRebuild && !suppressImmediateUiRefresh);

            if (shouldScheduleRebuild)
                ScheduleRebuild(doc, terrain.TerrainId, notify: !suppressImmediateUiRefresh);
            else if (deferDocumentSave)
            {
                if (!suppressImmediateUiRefresh)
                    RaiseStateChanged();
            }

            if (previousRenderAppearance != TerrainRenderAppearance.Capture(terrain))
                InvalidateTerrainRenderMeshes(doc, terrainId);

            if (!string.Equals(previousName, terrain.Name, StringComparison.Ordinal) ||
                previousColorArgb != terrain.TerrainColorArgb)
                ScheduleTerrainDependents(doc, state, terrain.TerrainId);
        }
        finally
        {
            undoTransaction?.Dispose();
            if (!deferDocumentSave &&
                _pendingTerrainEdits.TryGetValue(doc.RuntimeSerialNumber, out PendingTerrainEdit? pending) &&
                pending.Depth == 0)
            {
                CommitPendingTerrainEdit(doc);
            }
        }
    }

    /// <summary>
    /// Prompts the user to drag a rectangle and replaces the Triangulate Data Clip object references,
    /// preserving any layer references. Returns false if cancelled.
    /// </summary>
    public bool SetDataClipRectangle(RhinoDoc doc, Guid terrainId, Guid modifierId)
    {
        if (!ModelUnitGuard.TryGet(doc, out _))
            return false;

        var rc = global::Rhino.Input.RhinoGet.GetRectangle(out global::Rhino.Geometry.Point3d[] corners);
        if (rc != global::Rhino.Commands.Result.Success || corners == null || corners.Length < 4)
            return false;

        using TerrainUndoTransaction? undo = BeginTerrainUndoTransaction(doc, "Set MoleHill Data Clip");
        var polyline = new global::Rhino.Geometry.Polyline(new[] { corners[0], corners[1], corners[2], corners[3], corners[0] });
        var curve = new global::Rhino.Geometry.PolylineCurve(polyline);
        Guid id = doc.Objects.AddCurve(curve);
        if (id == Guid.Empty)
            return false;

        MutateTerrain(doc, terrainId, terrain =>
        {
            if (terrain.Modifiers.FirstOrDefault(modifier => modifier.Id == modifierId) is TriangulateModifierDefinition triangulate)
                triangulate.DataClipBoundaries.ReplaceObjects(new[] { id });
        });
        doc.Views.Redraw();
        return true;
    }

    private static bool IsPinnedBaseTriangulate(TerrainDefinition terrain, ModifierDefinition modifier)
    {
        return terrain.Modifiers.Count > 0 &&
               ReferenceEquals(terrain.Modifiers[0], modifier) &&
               modifier is TriangulateModifierDefinition;
    }

    private static ModifierDefinition? CloneModifier(ModifierDefinition modifier)
    {
        string json = JsonSerializer.Serialize(modifier, modifier.GetType());
        return JsonSerializer.Deserialize(json, modifier.GetType()) as ModifierDefinition;
    }

    private static TerrainObjectDefinition? CloneTerrainObjectDefinition(TerrainObjectDefinition definition)
    {
        string json = JsonSerializer.Serialize(definition, definition.GetType());
        return JsonSerializer.Deserialize(json, definition.GetType()) as TerrainObjectDefinition;
    }

    // Both directions must use the same options: SharedOptions writes camelCase names, which the
    // default (case-sensitive, PascalCase) options would silently fail to bind on the way back.
    private static AnnotationDefinition? CloneAnnotation(AnnotationDefinition annotation)
    {
        string json = JsonSerializer.Serialize(annotation, annotation.GetType(), TerrainSerializer.SharedOptions);
        return JsonSerializer.Deserialize(json, annotation.GetType(), TerrainSerializer.SharedOptions) as AnnotationDefinition;
    }

    private static AnalysisDefinition? CloneAnalysis(AnalysisDefinition analysis)
    {
        string json = JsonSerializer.Serialize(analysis, analysis.GetType(), TerrainSerializer.SharedOptions);
        return JsonSerializer.Deserialize(json, analysis.GetType(), TerrainSerializer.SharedOptions) as AnalysisDefinition;
    }
}
