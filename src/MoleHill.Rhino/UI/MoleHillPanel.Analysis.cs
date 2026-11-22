using System.Globalization;
using Eto.Drawing;
using Eto.Forms;
using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using MoleHill.Shared;
using Rhino;

namespace MoleHill.Rhino.UI;

// Analysis tab: card + body builders, palette/range editors. Annotation cards live in
// MoleHillPanel.Annotations.cs.
public sealed partial class MoleHillPanel
{
    private Panel CreateAnalysisCard(TerrainDefinition terrain, AnalysisDefinition analysis, bool isActive)
    {
        Guid terrainId = terrain.TerrainId, id = analysis.Id;
        bool supportsPreview = TerrainAnalysisPreviewBuilder.SupportsTerrainPreview(analysis);
        bool producesOutput = TerrainAnalysisPreviewBuilder.ProducesGeneratedOutput(analysis);
        string statusText = isActive
            ? "Preview"
            : analysis.IsEnabled
                ? supportsPreview ? "Enabled" : producesOutput ? "Output" : "Summary"
                : "Disabled";

        return CreateCard(new CardSpec
        {
            Stack = _analysisCards,
            Id = id,
            Name = analysis.Label,
            NameHelp = "Friendly analysis name shown in the panel.",
            Rename = text => MutateAnalysis(terrainId, id, item => item.Label = text, scheduleRebuild: false),
            IsEnabled = analysis.IsEnabled,
            EnableHelp = "Enable or disable this analysis card without deleting it.",
            SetEnabled = value =>
            {
                MutateAnalysis(terrainId, id, item => item.IsEnabled = value, scheduleRebuild: false);
                RefreshTerrainPreview(terrainId);
            },
            TypeName = GetAnalysisTypeLabel(analysis),
            TypeIcon = CreateCardIconControl(GetAnalysisIconName(analysis), GetAnalysisIconLabel(analysis)),
            Summary = AppendRuntimeDiagnosticSummary(
                GetAnalysisCollapsedSummary(terrain, analysis),
                terrain,
                new RuntimeOverlayOwner(RuntimeOverlayOwnerKind.Analysis, id)),
            Description = GetAnalysisSubtitle(analysis, isActive),
            HandleHelp = "Drag to reorder this analysis.",
            Status = CreateCardStatusLabel(statusText, isActive ? UiTheme.ActiveBadge : UiTheme.MutedText),
            Duplicate = (() =>
            {
                DuplicateAnalysis(terrainId, id);
                RefreshTerrainPreview(terrainId);
            }, "Duplicate this analysis card."),
            Delete = (() =>
            {
                RemoveAnalysis(terrainId, id);
                RefreshTerrainPreview(terrainId);
            }, "Delete this analysis card."),
            Body = () => CreateAnalysisBody(terrain, analysis)
        });
    }

    private Control CreateAnalysisBody(TerrainDefinition terrain, AnalysisDefinition analysis)
    {
        var layout = UiLayouts.CardBody();
        TerrainAnalysisSummary? summary = GetAnalysisSummary(terrain, analysis.Id);

        // Above the controls, not below them: if this analysis cannot do anything yet, that is the first
        // thing to say, and it names which control to reach for. Otherwise state what it is measuring —
        // a good default is invisible, and a card that says nothing about it reads as unconfigured.
        var descriptor = AnalysisTypeRegistry.ForType(analysis.GetType());
        string? blocker = descriptor?.DescribeBlocker(terrain, analysis);
        if (blocker != null)
            layout.AddRow(CreateWarningRow(blocker));
        else if (descriptor?.DescribeBasis(terrain, analysis) is { } basis)
            layout.AddRow(CreateNoteRow(basis));

        AppendBespokeAnalysisRowsBefore(layout, terrain, analysis);
        TryBuildSchemaAnalysisBody(layout, terrain, analysis);
        AppendBespokeAnalysisRowsAfter(layout, terrain, analysis, summary);
        Control? diagnosticsRow = CreateRuntimeDiagnosticsRow(
            terrain,
            new RuntimeOverlayOwner(RuntimeOverlayOwnerKind.Analysis, analysis.Id));
        if (diagnosticsRow != null)
            layout.AddRow(diagnosticsRow);

        return layout;
    }

    /// <summary>
    /// Rows that must run before the schema-generated rows to preserve the original card layout: the slope
    /// unit selector (converts Range values when changed, so it can't be a plain schema row) and the shared
    /// terrain-section block (sources + insertion origin + text height + output layer + color — the
    /// insertion-origin picker needs an interactive GetPoint, so the whole block stays bespoke).
    /// </summary>
    private void AppendBespokeAnalysisRowsBefore(DynamicLayout layout, TerrainDefinition terrain, AnalysisDefinition analysis)
    {
        switch (analysis)
        {
            case SlopeAnalysisDefinition slope:
                layout.AddRow(CreateSlopeUnitEditor(terrain.TerrainId, slope));
                break;

            case ReferenceComparisonAnalysisDefinition referenceComparison:
                AddAnalysisCompareTerrainRows(layout, terrain, referenceComparison);
                break;

        }
    }

    /// <summary>
    /// Lets Earthworks/Cut-Fill compare against another MoleHill terrain's finished mesh directly, without
    /// requiring it to be baked to Rhino geometry first, and says when that terrain cannot be compared
    /// against as it stands. Sits above the schema-generated "Compare To" row, which takes precedence when
    /// it has objects or layers assigned.
    /// </summary>
    private void AddAnalysisCompareTerrainRows(
        DynamicLayout layout,
        TerrainDefinition owner,
        ReferenceComparisonAnalysisDefinition analysis)
    {
        // The same rule heads the comparison rows on the section cards, so the block reads as one thing.
        layout.AddRow(CreateSectionRule("Compare"));
        layout.AddRow(CreateCompareTerrainEditor(
            owner,
            analysis.ReferenceTerrainId,
            id => MutateAnalysis(owner.TerrainId, analysis.Id, item =>
            {
                if (item is ReferenceComparisonAnalysisDefinition compare)
                    compare.ReferenceTerrainId = id;
            }, scheduleRebuild: true),
            CompareTerrainHelp + " Ignored when “Compare To” below has objects or layers assigned - those take precedence."));

        if (analysis.ReferenceTerrainId is not { } referenceId)
            return;

        if (analysis.Reference.HasReferences)
        {
            layout.AddRow(CreateNoteRow("Not used: the “Compare To” objects below take precedence over a terrain."));
            return;
        }

        if (CreateCompareTerrainStatusRow(
                owner,
                referenceId,
                "cut and fill are measured against this terrain's own base triangulation instead") is { } status)
        {
            layout.AddRow(status);
        }
    }

    /// <summary>An inline caution on a card: a setting is on but cannot take effect yet.</summary>
    /// <summary>
    /// A quiet statement of fact about how the card is set up. Muted and unadorned, so it reads as an
    /// answer to "what is this measuring?" rather than as something demanding attention.
    /// </summary>
    private static Control CreateNoteRow(string message)
    {
        var label = UiControls.Label(message, UiLabelRole.Meta, WrapMode.Word);
        label.VerticalAlignment = VerticalAlignment.Top;
        return label;
    }

    /// <summary>
    /// An inline notice on a card: a setting is on but cannot take effect, or a prerequisite is missing.
    /// Given the warning surface so it reads as a notice rather than a stray sentence between controls.
    /// </summary>
    private static Control CreateWarningRow(string message)
    {
        return new Panel
        {
            BackgroundColor = UiTheme.WarningBackground,
            Padding = new Padding(UiMetrics.SpaceMedium, UiMetrics.SpaceSmall),
            Content = new Label
            {
                Text = message,
                TextColor = UiTheme.WarningText,
                Wrap = WrapMode.Word
            }
        };
    }

    /// <summary>
    /// Hatch patterns offered on a section card: MoleHill's built-ins plus whatever the document already
    /// defines, so an office standard pattern is selectable without leaving the panel.
    /// </summary>
    private static AnalysisRange ResolveLegendRange(
        TerrainAnalysisSummary? summary,
        AnalysisDefinition analysis,
        RangeShape shape)
    {
        if (summary?.DisplayRangeLow is { } low && summary.DisplayRangeHigh is { } high && high > low)
            return new AnalysisRange(low, high, analysis.AutoColorRange);

        if (!analysis.AutoColorRange)
            return AnalysisRange.FromRequested(analysis.RangeLow, analysis.RangeHigh, shape);

        // No colouring yet: show the configured bounds if they are usable, otherwise a neutral placeholder.
        return AnalysisRange.FromRequested(analysis.RangeLow, analysis.RangeHigh, shape) with { IsAuto = true };
    }

    /// <summary>
    /// Rows drawn after the schema-generated rows: the read-only stats from the last terrain build, which
    /// each descriptor supplies as data.
    /// </summary>
    private void AppendBespokeAnalysisRowsAfter(DynamicLayout layout, TerrainDefinition terrain, AnalysisDefinition analysis, TerrainAnalysisSummary? summary)
    {
        // Nothing here needs a live control: what the last build measured is data on the descriptor
        // (AnalysisTypeDescriptor.DescribeResult), drawn by the same generic renderer annotations use.
        AddResultRows(
            layout,
            AnalysisTypeRegistry.ForType(analysis.GetType())?.DescribeResult(analysis, summary, ResultFormats));
    }

    private static string FormatArea(double value)
    {
        ModelUnitContext unitContext = ModelUnitContext.FromDocument(RhinoDoc.ActiveDoc);
        if (!unitContext.IsSupported)
            unitContext = ModelUnitContext.FromUnitSystem(UnitSystem.Meters);
        return unitContext.FormatArea(value);
    }

    private void RemoveAnalysis(Guid terrainId, Guid analysisId)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        _controller.MutateTerrain(doc, terrainId, terrain =>
        {
            terrain.Analyses.RemoveAll(item => item.Id == analysisId);
        }, scheduleRebuild: false);
        RefreshUi();
    }

    private void AddAnalysis(string kind)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        IReadOnlyList<TerrainDefinition> documentTerrains = _controller.GetTerrains(doc);
        MutateSelectedTerrain(terrain =>
        {
            AnalysisDefinition? analysis = AnalysisTypeRegistry.Create(
                kind,
                MoleHill.Shared.ModelUnitContext.FromDocument(doc));
            if (analysis is GradientComplianceAnalysisDefinition compliance &&
                GradientRulePresets.FindDocumentStandard(terrain, documentTerrains) is { } standard)
            {
                compliance.Rules = standard;
            }

            if (analysis != null)
                terrain.Analyses.Insert(0, analysis);
        }, scheduleRebuild: false);

        var terrain = _controller.GetSelectedTerrain(doc);
        if (terrain != null)
        {
            RefreshUi();
            RefreshTerrainPreview(terrain.TerrainId);
        }
    }

    private static TerrainAnalysisSummary? GetAnalysisSummary(TerrainDefinition terrain, Guid analysisId)
    {
        return terrain.LastAnalysisResults.FirstOrDefault(item => item.AnalysisId == analysisId);
    }

    private static string GetAnalysisTypeLabel(AnalysisDefinition analysis) =>
        AnalysisTypeRegistry.ForType(analysis.GetType())?.TypeLabel ?? "Analysis";

    private static string GetAnalysisKind(AnalysisDefinition analysis) =>
        AnalysisTypeRegistry.ForType(analysis.GetType())?.Kind ?? string.Empty;

    private static Color AnalysisTypeColor(string kind) =>
        UiTheme.TypeAccent(AnalysisTypeRegistry.ForKind(kind)?.AccentArgb);

    private static string GetAnalysisIconLabel(AnalysisDefinition analysis) =>
        AnalysisTypeRegistry.ForType(analysis.GetType())?.IconLabel ?? "A";

    private static string? GetAnalysisIconName(AnalysisDefinition analysis) =>
        AnalysisTypeRegistry.ForType(analysis.GetType())?.IconName;

    private static string GetAnalysisSubtitle(AnalysisDefinition analysis, bool isActive)
    {
        var descriptor = AnalysisTypeRegistry.ForType(analysis.GetType());
        if (descriptor == null)
            return "Analysis";

        return isActive && descriptor.ActiveSubtitle != null ? descriptor.ActiveSubtitle : descriptor.Subtitle;
    }

    private static string GetAnalysisCollapsedSummary(TerrainDefinition terrain, AnalysisDefinition analysis)
    {
        TerrainAnalysisSummary? summary = GetAnalysisSummary(terrain, analysis.Id);
        return analysis switch
        {
            EarthworkAnalysisDefinition earthwork => summary != null
                ? $"Net {FormatVolume(summary.NetVolume)} | {(summary.EarthworkIsEstimated ? "Estimated" : "Exact")}"
                : $"{CountReferences(earthwork.Reference)} refs | {CountReferences(earthwork.Boundary)} bounds",
            SlopeAnalysisDefinition slope => $"{FormatSlopeValue(slope.RangeLow, slope.Unit)} to {(slope.RangeHigh > slope.RangeLow ? FormatSlopeValue(slope.RangeHigh, slope.Unit) : "Auto")}",
            ElevationAnalysisDefinition elevation => $"{elevation.RangeLow:G4} to {(elevation.RangeHigh > elevation.RangeLow ? elevation.RangeHigh.ToString("G4") : "Auto")}",
            CutFillAnalysisDefinition cutFill => summary != null
                ? $"{summary.CutVolume:F2} / {summary.FillVolume:F2} / {summary.NetVolume:F2}"
                : $"{CountReferences(cutFill.Reference)} refs | {CountReferences(cutFill.Boundary)} bounds",
            WaterflowAnalysisDefinition waterflow => summary != null
                ? $"{summary.GeneratedOutputCount} paths | {summary.WaterflowBoundaryCount} boundary"
                : $"{CountReferences(waterflow.Sources)} refs | downhill paths",
            _ => string.Empty
        };
    }

    private void DuplicateAnalysis(Guid terrainId, Guid analysisId)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc != null)
        {
            _controller.DuplicateAnalysis(doc, terrainId, analysisId);
            RefreshUi();
        }
    }

    private Control CreateSlopeUnitEditor(Guid terrainId, SlopeAnalysisDefinition slope)
    {
        var options = new[]
        {
            (AnalysisFormatting.GetSlopeUnitKey(SlopeAnalyzer.SlopeUnit.Percent), "Percent"),
            (AnalysisFormatting.GetSlopeUnitKey(SlopeAnalyzer.SlopeUnit.Promille), "Promille"),
            (AnalysisFormatting.GetSlopeUnitKey(SlopeAnalyzer.SlopeUnit.Ratio), "Ratio"),
            (AnalysisFormatting.GetSlopeUnitKey(SlopeAnalyzer.SlopeUnit.Degrees), "Degrees")
        };

        return CreateDropDownEditor(
            "Units",
            options,
            AnalysisFormatting.GetSlopeUnitKey(slope.Unit),
            value =>
            {
                var nextUnit = AnalysisFormatting.ParseSlopeUnit(value);
                if (nextUnit == slope.Unit)
                    return;

                double rangeLow = ConvertSlopeValue(slope.RangeLow, slope.Unit, nextUnit);
                double rangeHigh = ConvertSlopeValue(slope.RangeHigh, slope.Unit, nextUnit);
                double interval = ConvertSlopeValue(slope.ColorInterval, slope.Unit, nextUnit);
                MutateAndRefreshAnalysis(terrainId, slope.Id, item =>
                {
                    if (item is not SlopeAnalysisDefinition target)
                        return;

                    target.Unit = nextUnit;
                    target.RangeLow = rangeLow;
                    target.RangeHigh = rangeHigh;
                    target.ColorInterval = interval;
                });
            },
            "Show slope values as percent, promille, rise/run ratio, or degrees.");
    }

    private static string FormatSlopeValue(double value, SlopeAnalyzer.SlopeUnit unit) =>
        AnalysisFormatting.FormatSlopeValue(value, unit);

    private static string FormatAnalysisValue(double value, string? format) =>
        AnalysisFormatting.FormatValue(value, format);

    private static double ConvertSlopeValue(double value, SlopeAnalyzer.SlopeUnit fromUnit, SlopeAnalyzer.SlopeUnit toUnit) =>
        AnalysisFormatting.ConvertSlopeValue(value, fromUnit, toUnit);
}
