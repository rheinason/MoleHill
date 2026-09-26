using Eto.Forms;
using MoleHill.Rhino.Model;
using Rhino;

namespace MoleHill.Rhino.UI;

// Card edits back to the controller. Every card changes the selected terrain through these, so undo
// records, saving and rebuild scheduling are decided in one place.
public sealed partial class MoleHillPanel : Panel
{
    private void MutateSelectedTerrain(
        Action<TerrainDefinition> mutator,
        bool scheduleRebuild = true,
        bool deferDocumentSave = false,
        bool suppressImmediateUiRefresh = false)
    {
        var doc = RhinoDoc.ActiveDoc;
        var terrain = doc == null ? null : _controller.GetSelectedTerrain(doc);
        if (doc == null || terrain == null)
            return;

        _controller.MutateTerrain(
            doc, terrain.TerrainId, mutator, scheduleRebuild, deferDocumentSave, suppressImmediateUiRefresh);
    }

    /// <summary>
    /// A display setting being dragged: recolour the viewport, but do not serialize the document or
    /// rebuild the panel on every tick.
    ///
    /// Both are ruinously expensive to do per slider step — the save writes the whole terrain state as
    /// JSON into the .3dm, and the StateChanged it raises rebuilds every card in the panel. That is why
    /// the line-weight and opacity sliders felt like they were dragging through treacle. The save is
    /// debounced instead, so the value still persists a moment after the drag stops.
    /// </summary>
    private void MutateSelectedTerrainLive(Action<TerrainDefinition> mutator)
    {
        MutateSelectedTerrain(
            mutator,
            scheduleRebuild: false,
            deferDocumentSave: true,
            suppressImmediateUiRefresh: true);

        var doc = RhinoDoc.ActiveDoc;
        var terrain = doc == null ? null : _controller.GetSelectedTerrain(doc);
        if (doc != null && terrain != null)
            _controller.RefreshTerrainDisplay(doc, terrain.TerrainId);
    }

    private void MutateModifier(
        Guid terrainId,
        Guid modifierId,
        Action<ModifierDefinition> mutator,
        bool scheduleRebuild = true,
        bool deferDocumentSave = false,
        bool suppressImmediateUiRefresh = false)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        _controller.MutateTerrain(doc, terrainId, terrain =>
        {
            var modifier = terrain.Modifiers.FirstOrDefault(item => item.Id == modifierId);
            if (modifier != null)
                mutator(modifier);
        }, scheduleRebuild, deferDocumentSave, suppressImmediateUiRefresh);
    }

    private void MutateObjectDefinition(
        Guid terrainId,
        Guid definitionId,
        Action<TerrainObjectDefinition> mutator,
        bool scheduleRebuild = true,
        bool deferDocumentSave = false,
        bool suppressImmediateUiRefresh = false)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        _controller.MutateTerrain(doc, terrainId, terrain =>
        {
            var definition = terrain.Objects.FirstOrDefault(item => item.Id == definitionId);
            if (definition != null)
                mutator(definition);
        }, scheduleRebuild, deferDocumentSave, suppressImmediateUiRefresh);
    }

    private void MutateAnalysis(
        Guid terrainId,
        Guid analysisId,
        Action<AnalysisDefinition> mutator,
        bool scheduleRebuild = false,
        bool deferDocumentSave = false,
        bool suppressImmediateUiRefresh = false)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        _controller.MutateTerrain(doc, terrainId, terrain =>
        {
            var analysis = terrain.Analyses.FirstOrDefault(item => item.Id == analysisId);
            if (analysis != null)
                mutator(analysis);
        }, scheduleRebuild, deferDocumentSave, suppressImmediateUiRefresh);
    }

    private void MutateAndRefreshAnalysis(Guid terrainId, Guid analysisId, Action<AnalysisDefinition> mutator)
    {
        MutateAnalysis(terrainId, analysisId, mutator, scheduleRebuild: false);
        RefreshTerrainPreview(terrainId);
    }

    /// <summary>
    /// The mid-gesture version: recolour the preview but do not save the document or raise StateChanged.
    ///
    /// A plain analysis mutation saves immediately, and that save raises StateChanged, which rebuilds the
    /// card. Mid-drag that destroys the control the user is dragging — the gesture dies on its first
    /// mouse-move and the edit looks like it did nothing. Ramp handles and scrub fields therefore commit
    /// through here while the mouse is down and call <see cref="MutateAndRefreshAnalysis"/> once on
    /// release, the same live-scrub split the slider rows use.
    /// </summary>
    private void MutateAndRefreshAnalysisLive(Guid terrainId, Guid analysisId, Action<AnalysisDefinition> mutator)
    {
        MutateAnalysis(
            terrainId,
            analysisId,
            mutator,
            scheduleRebuild: false,
            deferDocumentSave: true,
            suppressImmediateUiRefresh: true);
        RefreshTerrainPreview(terrainId);
    }

    private void RefreshTerrainPreview(Guid terrainId)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc != null)
            _controller.RefreshTerrainDisplay(doc, terrainId);
    }
}
