using Eto.Drawing;
using Eto.Forms;
using MoleHill.Rhino.Model;
using Rhino;

namespace MoleHill.Rhino.UI;

// Drag-and-drop reordering for the modifier, analysis and annotation stacks: card and separator drop
// targets, and the index arithmetic for a drop onto a separator.
public sealed partial class MoleHillPanel : Panel
{
    private void WireAnalysisCardDragDrop(Control box, Guid terrainId, Guid analysisId)
    {
        box.AllowDrop = true;
        var cardHighlight = Color.FromArgb(100, 120, 200, 255);

        box.DragEnter += (_, e) =>
        {
            if (!e.Data.Contains("analysis-drag")) return;
            e.Effects = DragEffects.Move;
            if (_dragOverAnalysisId.HasValue && _analysisStripMap.TryGetValue(_dragOverAnalysisId.Value, out var prevStrip))
                if (_analysisStripColors.TryGetValue(_dragOverAnalysisId.Value, out var prevColor))
                    prevStrip.BackgroundColor = prevColor;
            _dragOverAnalysisId = analysisId;
            if (_analysisStripMap.TryGetValue(analysisId, out var strip))
                strip.BackgroundColor = cardHighlight;
            ClearAllSepHighlights(_analysisSepMap);
            if (_analysisSepMap.TryGetValue(analysisId, out var sep))
                sep.BackgroundColor = UiTheme.SepHighlight;
        };

        box.DragLeave += (_, _) =>
        {
            if (_dragOverAnalysisId != analysisId)
                return;

            if (_analysisStripMap.TryGetValue(analysisId, out var strip))
                if (_analysisStripColors.TryGetValue(analysisId, out var origColor))
                    strip.BackgroundColor = origColor;
            _dragOverAnalysisId = null;
            ClearAllSepHighlights(_analysisSepMap);
        };

        box.DragDrop += (_, e) =>
        {
            if (!e.Data.Contains("analysis-drag")) return;
            var idStr = e.Data.GetString("analysis-drag");
            if (!Guid.TryParse(idStr, out var sourceId)) return;

            if (_analysisStripMap.TryGetValue(analysisId, out var strip))
                if (_analysisStripColors.TryGetValue(analysisId, out var origColor))
                    strip.BackgroundColor = origColor;
            _dragOverAnalysisId = null;
            ClearAllSepHighlights(_analysisSepMap);

            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return;
            _controller.MutateTerrain(doc, terrainId, t => MoveAnalysisToDisplaySeparator(t, sourceId, analysisId), scheduleRebuild: false);
            RefreshTerrainPreview(terrainId);
        };
    }

    private void WireAnalysisSepDragDrop(Panel outerSep, Panel innerSep, Guid terrainId, Guid insertBeforeId)
    {
        outerSep.AllowDrop = true;

        outerSep.DragEnter += (_, e) =>
        {
            if (!e.Data.Contains("analysis-drag")) return;
            e.Effects = DragEffects.Move;
            if (_dragOverAnalysisId.HasValue && _analysisStripMap.TryGetValue(_dragOverAnalysisId.Value, out var prevStrip))
                if (_analysisStripColors.TryGetValue(_dragOverAnalysisId.Value, out var prevColor))
                    prevStrip.BackgroundColor = prevColor;
            _dragOverAnalysisId = null;
            ClearAllSepHighlights(_analysisSepMap);
            innerSep.BackgroundColor = UiTheme.SepHighlight;
        };

        outerSep.DragLeave += (_, _) => innerSep.BackgroundColor = Colors.Transparent;

        outerSep.DragDrop += (_, e) =>
        {
            if (!e.Data.Contains("analysis-drag")) return;
            var idStr = e.Data.GetString("analysis-drag");
            if (!Guid.TryParse(idStr, out var sourceId)) return;

            innerSep.BackgroundColor = Colors.Transparent;
            ClearAllSepHighlights(_analysisSepMap);

            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return;
            _controller.MutateTerrain(doc, terrainId, t => MoveAnalysisToDisplaySeparator(t, sourceId, insertBeforeId), scheduleRebuild: false);
            RefreshTerrainPreview(terrainId);
        };
    }

    private void WireAnnotationCardDragDrop(Control box, Guid terrainId, Guid annotationId)
    {
        box.AllowDrop = true;
        var cardHighlight = Color.FromArgb(100, 120, 200, 255);

        box.DragEnter += (_, e) =>
        {
            if (!e.Data.Contains("annotation-drag")) return;
            e.Effects = DragEffects.Move;
            if (_dragOverAnnotationId.HasValue && _annotationStripMap.TryGetValue(_dragOverAnnotationId.Value, out var prevStrip))
                if (_annotationStripColors.TryGetValue(_dragOverAnnotationId.Value, out var prevColor))
                    prevStrip.BackgroundColor = prevColor;
            _dragOverAnnotationId = annotationId;
            if (_annotationStripMap.TryGetValue(annotationId, out var strip))
                strip.BackgroundColor = cardHighlight;
            ClearAllSepHighlights(_annotationSepMap);
            if (_annotationSepMap.TryGetValue(annotationId, out var sep))
                sep.BackgroundColor = UiTheme.SepHighlight;
        };

        box.DragLeave += (_, _) =>
        {
            if (_dragOverAnnotationId != annotationId)
                return;

            if (_annotationStripMap.TryGetValue(annotationId, out var strip))
                if (_annotationStripColors.TryGetValue(annotationId, out var origColor))
                    strip.BackgroundColor = origColor;
            _dragOverAnnotationId = null;
            ClearAllSepHighlights(_annotationSepMap);
        };

        box.DragDrop += (_, e) =>
        {
            if (!e.Data.Contains("annotation-drag")) return;
            var idStr = e.Data.GetString("annotation-drag");
            if (!Guid.TryParse(idStr, out var sourceId)) return;

            if (_annotationStripMap.TryGetValue(annotationId, out var strip))
                if (_annotationStripColors.TryGetValue(annotationId, out var origColor))
                    strip.BackgroundColor = origColor;
            _dragOverAnnotationId = null;
            ClearAllSepHighlights(_annotationSepMap);

            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return;
            _controller.MutateTerrain(doc, terrainId, t => MoveAnnotationToDisplaySeparator(t, sourceId, annotationId), scheduleRebuild: false);
            RefreshTerrainPreview(terrainId);
        };
    }

    private void WireAnnotationSepDragDrop(Panel outerSep, Panel innerSep, Guid terrainId, Guid insertBeforeId)
    {
        outerSep.AllowDrop = true;

        outerSep.DragEnter += (_, e) =>
        {
            if (!e.Data.Contains("annotation-drag")) return;
            e.Effects = DragEffects.Move;
            if (_dragOverAnnotationId.HasValue && _annotationStripMap.TryGetValue(_dragOverAnnotationId.Value, out var prevStrip))
                if (_annotationStripColors.TryGetValue(_dragOverAnnotationId.Value, out var prevColor))
                    prevStrip.BackgroundColor = prevColor;
            _dragOverAnnotationId = null;
            ClearAllSepHighlights(_annotationSepMap);
            innerSep.BackgroundColor = UiTheme.SepHighlight;
        };

        outerSep.DragLeave += (_, _) => innerSep.BackgroundColor = Colors.Transparent;

        outerSep.DragDrop += (_, e) =>
        {
            if (!e.Data.Contains("annotation-drag")) return;
            var idStr = e.Data.GetString("annotation-drag");
            if (!Guid.TryParse(idStr, out var sourceId)) return;

            innerSep.BackgroundColor = Colors.Transparent;
            ClearAllSepHighlights(_annotationSepMap);

            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return;
            _controller.MutateTerrain(doc, terrainId, t => MoveAnnotationToDisplaySeparator(t, sourceId, insertBeforeId), scheduleRebuild: false);
            RefreshTerrainPreview(terrainId);
        };
    }

    private static void MoveAnalysisToDisplaySeparator(TerrainDefinition terrain, Guid sourceId, Guid insertBeforeId)
    {
        if (sourceId == insertBeforeId)
            return;

        int fromIdx = terrain.Analyses.FindIndex(analysis => analysis.Id == sourceId);
        if (fromIdx < 0)
            return;

        var item = terrain.Analyses[fromIdx];
        terrain.Analyses.RemoveAt(fromIdx);

        if (insertBeforeId == Guid.Empty)
        {
            terrain.Analyses.Add(item);
            return;
        }

        int insertIdx = terrain.Analyses.FindIndex(analysis => analysis.Id == insertBeforeId);
        terrain.Analyses.Insert(insertIdx >= 0 ? insertIdx : terrain.Analyses.Count, item);
    }

    private static void MoveAnnotationToDisplaySeparator(TerrainDefinition terrain, Guid sourceId, Guid insertBeforeId)
    {
        if (sourceId == insertBeforeId)
            return;

        int fromIdx = terrain.Annotations.FindIndex(annotation => annotation.Id == sourceId);
        if (fromIdx < 0)
            return;

        var item = terrain.Annotations[fromIdx];
        terrain.Annotations.RemoveAt(fromIdx);

        if (insertBeforeId == Guid.Empty)
        {
            terrain.Annotations.Add(item);
            return;
        }

        int insertIdx = terrain.Annotations.FindIndex(annotation => annotation.Id == insertBeforeId);
        terrain.Annotations.Insert(insertIdx >= 0 ? insertIdx : terrain.Annotations.Count, item);
    }

    // ── Drag-and-drop: modifier cards ────────────────────────────────────

    private void WireModifierCardDragDrop(Control box, Guid terrainId, Guid modifierId)
    {
        box.AllowDrop = true;
        var cardHighlight = Color.FromArgb(100, 120, 200, 255);

        box.DragEnter += (_, e) =>
        {
            if (!e.Data.Contains("modifier-drag")) return;
            e.Effects = DragEffects.Move;
            if (_dragOverModifierId.HasValue && _modifierStripMap.TryGetValue(_dragOverModifierId.Value, out var prevStrip))
                if (_modifierStripColors.TryGetValue(_dragOverModifierId.Value, out var prevColor))
                    prevStrip.BackgroundColor = prevColor;
            _dragOverModifierId = modifierId;
            if (_modifierStripMap.TryGetValue(modifierId, out var strip))
                strip.BackgroundColor = cardHighlight;
            ClearAllSepHighlights(_modifierSepMap);
            if (_modifierSepMap.TryGetValue(modifierId, out var sep))
                sep.BackgroundColor = UiTheme.SepHighlight;
        };

        box.DragLeave += (_, _) =>
        {
            if (_dragOverModifierId == modifierId)
            {
                if (_modifierStripMap.TryGetValue(modifierId, out var strip))
                    if (_modifierStripColors.TryGetValue(modifierId, out var origColor))
                        strip.BackgroundColor = origColor;
                _dragOverModifierId = null;
                ClearAllSepHighlights(_modifierSepMap);
            }
        };

        box.DragDrop += (_, e) =>
        {
            if (!e.Data.Contains("modifier-drag")) return;
            var idStr = e.Data.GetString("modifier-drag");
            if (!Guid.TryParse(idStr, out var sourceId)) return;

            if (_modifierStripMap.TryGetValue(modifierId, out var strip))
                if (_modifierStripColors.TryGetValue(modifierId, out var origColor))
                    strip.BackgroundColor = origColor;
            _dragOverModifierId = null;
            ClearAllSepHighlights(_modifierSepMap);

            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return;
            _controller.MutateTerrain(doc, terrainId, t => MoveModifierToDisplaySeparator(t, sourceId, modifierId));
        };
    }

    private void WireModifierSepDragDrop(Panel outerSep, Panel innerSep, Guid terrainId, Guid insertBeforeId)
    {
        outerSep.AllowDrop = true;

        outerSep.DragEnter += (_, e) =>
        {
            if (!e.Data.Contains("modifier-drag")) return;
            e.Effects = DragEffects.Move;
            if (_dragOverModifierId.HasValue && _modifierStripMap.TryGetValue(_dragOverModifierId.Value, out var prevStrip))
                if (_modifierStripColors.TryGetValue(_dragOverModifierId.Value, out var prevColor))
                    prevStrip.BackgroundColor = prevColor;
            _dragOverModifierId = null;
            ClearAllSepHighlights(_modifierSepMap);
            innerSep.BackgroundColor = UiTheme.SepHighlight;
        };

        outerSep.DragLeave += (_, _) => innerSep.BackgroundColor = Colors.Transparent;

        outerSep.DragDrop += (_, e) =>
        {
            if (!e.Data.Contains("modifier-drag")) return;
            var idStr = e.Data.GetString("modifier-drag");
            if (!Guid.TryParse(idStr, out var sourceId)) return;

            innerSep.BackgroundColor = Colors.Transparent;
            ClearAllSepHighlights(_modifierSepMap);

            var doc = RhinoDoc.ActiveDoc;
            if (doc == null) return;
            _controller.MutateTerrain(doc, terrainId, t => MoveModifierToDisplaySeparator(t, sourceId, insertBeforeId));
        };
    }

    private static void MoveModifierToDisplaySeparator(TerrainDefinition terrain, Guid sourceId, Guid insertAboveId)
    {
        if (sourceId == insertAboveId)
            return;

        int fromIdx = terrain.Modifiers.FindIndex(modifier => modifier.Id == sourceId);
        if (fromIdx < 0)
            return;

        var item = terrain.Modifiers[fromIdx];
        terrain.Modifiers.RemoveAt(fromIdx);

        if (insertAboveId == Guid.Empty)
        {
            // Tail separator = visual bottom. Display order is reversed, so model index 0 is the bottom card.
            terrain.Modifiers.Insert(0, item);
            return;
        }

        int anchorIdx = terrain.Modifiers.FindIndex(modifier => modifier.Id == insertAboveId);
        int insertIdx = anchorIdx >= 0
            ? Math.Min(anchorIdx + 1, terrain.Modifiers.Count)
            : terrain.Modifiers.Count;
        terrain.Modifiers.Insert(insertIdx, item);
    }

    private static void ClearAllSepHighlights(Dictionary<Guid, Panel> sepMap)
    {
        foreach (var sep in sepMap.Values)
            sep.BackgroundColor = Colors.Transparent;
    }
}
