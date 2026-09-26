using System.Globalization;
using Eto.Forms;
using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;

namespace MoleHill.Rhino.UI;

// The analysis card's colour block. One control replaces what used to be three separate things — the
// "Coloring & intervals" group, the read-only legend below it, and the "Mapped" summary row between them —
// on the principle that a legend you can edit needs no separate editor.
public sealed partial class MoleHillPanel
{
    /// <summary>
    /// Builds the ramp card for an analysis and wires its edits to the recolour path.
    ///
    /// Every callback commits through <see cref="MutateAndRefreshAnalysis"/>: colour is a display concern,
    /// so it recolours the preview mesh without scheduling a terrain rebuild. That is also what makes
    /// dragging a stop smooth — the panel is not rebuilding the card underneath the cursor, the control
    /// re-lays itself out.
    /// </summary>
    private Control CreateColorRampEditor(TerrainDefinition terrain, AnalysisDefinition analysis)
    {
        Guid terrainId = terrain.TerrainId;
        Guid analysisId = analysis.Id;

        TerrainAnalysisSummary? summary = GetAnalysisSummary(terrain, analysisId);
        RangeShape shape = TerrainAnalysisPreviewBuilder.GetRangeShape(analysis);

        // Mid-gesture edits recolour without saving or refreshing the panel; anything else is a normal
        // commit. Without the split, the save's StateChanged rebuilds this card on every mouse-move and
        // the drag dies on its first delta.
        void Commit(bool live, Action<AnalysisDefinition> apply)
        {
            if (live)
                MutateAndRefreshAnalysisLive(terrainId, analysisId, apply);
            else
                MutateAndRefreshAnalysis(terrainId, analysisId, apply);
        }

        var options = new ColorRampOptions
        {
            Title = string.IsNullOrWhiteSpace(analysis.Label) ? GetAnalysisTypeLabel(analysis) : analysis.Label,
            Ramp = analysis.ResolveRamp(),
            Range = ResolveLegendRange(summary, analysis, shape),
            Mode = analysis.ColorMode,
            Interval = analysis.ColorInterval,
            AutoRange = analysis.AutoColorRange,
            Shape = shape,
            Histogram = summary?.DistributionBins,
            HasData = summary?.DisplayRangeLow is not null && summary.DisplayRangeHigh is not null,
            FormatValue = ResolveAnalysisValueFormatter(analysis),
            Expanded = _expandedColorRamps.Contains(analysisId),
            SelectedIndex = _selectedColorRampStops.TryGetValue(analysisId, out int selected) ? selected : 0,

            OnRampChanged = (ramp, live) => Commit(live, item => item.SetRamp(ramp)),
            OnPresetPicked = key => Commit(false, item => item.ApplyPreset(key)),
            OnModeChanged = mode => Commit(false, item => item.ColorMode = mode),
            OnIntervalChanged = (interval, live) => Commit(
                live, item => item.ColorInterval = Math.Max(0.0, interval)),
            OnRangeChanged = (low, high, live) => Commit(live, item =>
            {
                item.RangeLow = low;
                item.RangeHigh = high;
            }),
            OnAutoRangeChanged = auto => MutateAndRefreshAnalysis(
                terrainId, analysisId, item => item.AutoColorRange = auto),

            // Expansion is a view preference, not document state: remembered for the session so a card
            // rebuild does not fold the ramp back up mid-edit, but never written to the .3dm.
            OnExpandedChanged = expanded =>
            {
                if (expanded)
                    _expandedColorRamps.Add(analysisId);
                else
                    _expandedColorRamps.Remove(analysisId);
            },

            OnSelectionChanged = index => _selectedColorRampStops[analysisId] = index
        };

        return new ColorRampControl(options);
    }

    /// <summary>
    /// How this analysis's values are written. Slope carries a unit the user chose; cut/fill wants an
    /// explicit sign so a legend reads "-2.50 / +2.50" rather than making you infer which end is which.
    /// </summary>
    private static Func<double, string> ResolveAnalysisValueFormatter(AnalysisDefinition analysis) => analysis switch
    {
        SlopeAnalysisDefinition slope => value => FormatSlopeValue(value, slope.Unit),
        CutFillAnalysisDefinition => value => value.ToString("+0.00;-0.00;0.00", CultureInfo.CurrentCulture),
        _ => value => value.ToString("F2", CultureInfo.CurrentCulture)
    };
}
