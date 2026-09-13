using System.Globalization;
using System.Linq;
using Eto.Drawing;
using Eto.Forms;
using MoleHill.Core.Analysis;
using MoleHill.Core.Grading;
using MoleHill.Core.Scattering;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using MoleHill.Shared;
using Rhino;
using Rhino.UI;
using RhinoObjectType = Rhino.DocObjects.ObjectType;
using RhinoPoint3d = Rhino.Geometry.Point3d;
using RhinoGetPoint = Rhino.Input.Custom.GetPoint;
using RhinoGetResult = Rhino.Input.GetResult;

namespace MoleHill.Rhino.UI;

// Analysis tab: card + body builders, palette/range editors. Annotation cards live in
// MoleHillPanel.Annotations.cs.
public sealed partial class MoleHillPanel
{
    private Panel CreateAnalysisCard(TerrainDefinition terrain, AnalysisDefinition analysis, bool isActive)
    {
        bool collapsed = _collapsedAnalyses.Contains(analysis.Id);
        var collapseLabel = CreateCollapseChevron(collapsed);

        var nameBox = new TextBox { Text = analysis.Label };
        StyleTextBox(nameBox);
        ApplyHelp(nameBox, "Friendly analysis name shown in the panel.");
        BindCommittedText(nameBox, () => analysis.Label, text =>
            MutateAnalysis(terrain.TerrainId, analysis.Id, item => item.Label = text, scheduleRebuild: false));

        var enabledCheck = new CheckBox
        {
            Checked = analysis.IsEnabled
        };
        ApplyHelp(enabledCheck, "Enable or disable this analysis card without deleting it.");
        enabledCheck.CheckedChanged += (_, _) =>
        {
            MutateAnalysis(terrain.TerrainId, analysis.Id, item => item.IsEnabled = enabledCheck.Checked == true, scheduleRebuild: false);
            RefreshTerrainPreview(terrain.TerrainId);
        };

        string kind = GetAnalysisKind(analysis);
        bool supportsPreview = TerrainAnalysisPreviewBuilder.SupportsTerrainPreview(analysis);
        bool producesOutput = TerrainAnalysisPreviewBuilder.ProducesGeneratedOutput(analysis);
        string statusText = isActive
            ? "Preview"
            : analysis.IsEnabled
                ? supportsPreview ? "Enabled" : producesOutput ? "Output" : "Summary"
                : "Disabled";
        var badge = CreateCardStatusLabel(statusText, isActive ? UiTheme.ActiveBadge : UiTheme.MutedText);

        var handle = CreateReorderHandle(analysis.Id, "analysis-drag", "Drag to reorder this analysis.");

        var accent = AnalysisTypeColor(kind);
        var iconPlate = CreateIconPlate(accent,
            CreateCardIconControl(GetAnalysisIconName(analysis), GetAnalysisIconLabel(analysis)));

        Control titleBlock = CreateCardTitleBlock(
            collapsed,
            analysis.Label,
            AppendRuntimeDiagnosticSummary(
                GetAnalysisCollapsedSummary(terrain, analysis),
                terrain,
                new RuntimeOverlayOwner(RuntimeOverlayOwnerKind.Analysis, analysis.Id)),
            nameBox,
            GetAnalysisSubtitle(analysis, isActive));

        var duplicateButton = MakeDuplicateIconButton(() =>
        {
            DuplicateAnalysis(terrain.TerrainId, analysis.Id);
            RefreshTerrainPreview(terrain.TerrainId);
        }, "Duplicate this analysis card.");
        var deleteButton = MakeDeleteIconButton(() =>
        {
            RemoveAnalysis(terrain.TerrainId, analysis.Id);
            RefreshTerrainPreview(terrain.TerrainId);
        }, "Delete this analysis card.");

        void ToggleCollapsed(bool ctrlHeld)
        {
            bool nowCollapsed = !_collapsedAnalyses.Contains(analysis.Id);
            if (ctrlHeld)
            {
                var doc2 = RhinoDoc.ActiveDoc;
                var t = doc2 == null ? null : _controller.GetSelectedTerrain(doc2);
                if (t != null)
                {
                    var visibleAnalyses = t.Analyses;

                    if (nowCollapsed)
                    {
                        foreach (var item in visibleAnalyses)
                            _collapsedAnalyses.Add(item.Id);
                    }
                    else
                    {
                        foreach (var item in visibleAnalyses)
                            _collapsedAnalyses.Remove(item.Id);
                    }
                }
            }
            else
            {
                if (nowCollapsed)
                    _collapsedAnalyses.Add(analysis.Id);
                else
                    _collapsedAnalyses.Remove(analysis.Id);
            }

            var doc = RhinoDoc.ActiveDoc;
            RebuildAnalysisLayout(doc == null ? null : _controller.GetSelectedTerrain(doc));
        }

        return CreateSharedCardShell(new SharedCardShellOptions
        {
            Handle = handle,
            CollapseControl = collapseLabel,
            IconPlate = iconPlate,
            EnabledControl = enabledCheck,
            TitleBlock = titleBlock,
            StatusControls = new Control[] { badge },
            ActionControls = new Control[]
            {
                duplicateButton,
                deleteButton
            },
            ToggleCollapsed = ToggleCollapsed,
            Collapsed = collapsed,
            Body = collapsed ? null : CreateAnalysisBody(terrain, analysis)
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
                layout.AddRow(CreateAnalysisReferenceTerrainEditor(terrain, referenceComparison));
                break;

        }
    }

    /// <summary>
    /// Lets Earthworks/Cut-Fill compare against another MoleHill terrain's finished mesh directly, without
    /// requiring it to be baked to Rhino geometry first. Sits above the schema-generated "Reference" row,
    /// which takes precedence when it has objects or layers assigned.
    /// </summary>
    private Control CreateAnalysisReferenceTerrainEditor(TerrainDefinition owner, ReferenceComparisonAnalysisDefinition analysis)
    {
        RhinoDoc? doc = RhinoDoc.ActiveDoc;
        IReadOnlyList<TerrainDefinition> terrains = doc == null
            ? Array.Empty<TerrainDefinition>()
            : _controller.GetTerrains(doc);

        var options = new List<(string Key, string Label)> { ("", "None — estimate from base triangulation") };
        options.AddRange(terrains
            .Where(item => item.TerrainId != owner.TerrainId)
            .Select(item => (item.TerrainId.ToString(), item.Name)));

        string selectedKey = analysis.ReferenceTerrainId?.ToString() ?? "";
        return CreateDropDownEditor(
            "Compare To Terrain",
            options,
            selectedKey,
            value => MutateAnalysis(owner.TerrainId, analysis.Id, item =>
            {
                if (item is ReferenceComparisonAnalysisDefinition compare)
                    compare.ReferenceTerrainId = string.IsNullOrEmpty(value) ? null : Guid.Parse(value);
            }, scheduleRebuild: true),
            "Another terrain's finished mesh to compare against, without baking it to Rhino geometry first. " +
            "Ignored when “Reference” below has objects or layers assigned - those take precedence.");
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
    /// Rows that can't be expressed by the parameter schema (custom-draw escape hatch): computed summaries,
    /// legends, and read-only stats from the last terrain build. Runs after the schema-generated rows so
    /// row order matches the former hand-written cards.
    /// </summary>
    private void AppendBespokeAnalysisRowsAfter(DynamicLayout layout, TerrainDefinition terrain, AnalysisDefinition analysis, TerrainAnalysisSummary? summary)
    {
        switch (analysis)
        {
            case EarthworkAnalysisDefinition earthwork:
                if (summary != null)
                {
                    string summaryText =
                        $"Cut: {FormatVolume(summary.CutVolume)}{Environment.NewLine}" +
                        $"Fill: {FormatVolume(summary.FillVolume)}{Environment.NewLine}" +
                        $"Net: {FormatVolume(summary.NetVolume)}{Environment.NewLine}" +
                        $"Mode: {(summary.EarthworkIsEstimated ? "Estimated from terrain delta" : "Exact")}";
                    layout.AddRow(CreateSelectableSummaryEditor(
                        "Summary",
                        summaryText,
                        "Earthwork summary from the last terrain build. Click into the field to select and copy values."));
                }
                else
                {
                    layout.AddRow(CreateSelectableSummaryEditor(
                        "Summary",
                        "Rebuild required",
                        "Rebuild the terrain to populate earthwork values.",
                        minHeight: 42));
                }
                break;

            // The mapped range and the ramp itself are the colour card's job now; what is left here is the
            // statistic the card cannot know — what the terrain actually measured on the last build.
            case SlopeAnalysisDefinition slope:
            {
                if (summary != null)
                {
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Min / Avg / Max",
                        $"{FormatSlopeSummaryValue(summary.SlopeMinPercent, slope.Unit)} / {FormatSlopeSummaryValue(summary.SlopeAveragePercent, slope.Unit)} / {FormatSlopeSummaryValue(summary.SlopeMaxPercent, slope.Unit)}",
                        "Current terrain slope summary from the last build."));
                }

                break;
            }

            case AspectAnalysisDefinition:
            {
                if (summary != null)
                {
                    string dominant = summary.AspectDominantBearing is { } bearing
                        ? $"{AspectAnalyzer.SectorName(bearing)} ({bearing:F0}°)"
                        : "None — no mean direction";
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Facing",
                        dominant,
                        "The plan-area-weighted mean direction the terrain drains towards, from the last build."));

                    string flat = summary.AspectFaceCount > 0
                        ? $"{summary.AspectFlatFaceCount:N0} of {summary.AspectFaceCount:N0} " +
                          $"({(double)summary.AspectFlatFaceCount / summary.AspectFaceCount:P0})"
                        : "—";
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Flat",
                        flat,
                        "Faces too flat to have an aspect. These are drawn neutral grey, not given a direction."));
                }

                break;
            }

            case ElevationAnalysisDefinition elevation:
            {
                if (summary != null)
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Area",
                        FormatArea(summary.SurfaceArea),
                        "Terrain surface area from the last build."));

                break;
            }

            case CutFillAnalysisDefinition cutFill:
            {
                if (summary != null)
                {
                    layout.AddRow(CreateReadOnlyValueRow("Cut / Fill / Net",
                        $"{summary.CutVolume:F2} / {summary.FillVolume:F2} / {summary.NetVolume:F2}",
                        "Current earthworks summary from the last build."));

                    if (cutFill.DrawsDeltaOutput)
                    {
                        string balance = summary.CutFillBalanceCurveCount > 0
                            ? $"{summary.CutFillBalanceCurveCount:N0} curve(s)"
                            : cutFill.ShowBalanceLine ? "None — no sign change" : "Off";
                        layout.AddRow(CreateReadOnlyValueRow(
                            "Drawn",
                            $"{summary.CutFillDeltaContourCount:N0} delta contour(s) | balance: {balance}",
                            "Delta lines drawn on the last build. A balance line only exists where the " +
                            "delta changes sign — a site that is all fill has none."));
                    }
                }

                break;
            }

            case WaterflowAnalysisDefinition waterflow:
                if (summary != null)
                {
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Points / Paths",
                        $"{summary.SampleSourceCount} point(s) -> {summary.GeneratedOutputCount} path(s)",
                        "Waterflow paths traced from the point sources across the final terrain."));
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Ends",
                        $"{summary.WaterflowBoundaryCount} boundary | {summary.WaterflowSinkCount} sink | {summary.WaterflowRejectedCount} outside",
                        "How the point paths ended during the last terrain build."));
                }
                else
                {
                    layout.AddRow(CreateSelectableSummaryEditor(
                        "Summary",
                        "Rebuild required",
                        "Rebuild the terrain to generate waterflow paths.",
                        minHeight: 42));
                }
                break;

        }

    }

    private static string FormatArea(double value)
    {
        ModelUnitContext unitContext = ModelUnitContext.FromDocument(RhinoDoc.ActiveDoc);
        if (!unitContext.IsSupported)
            unitContext = ModelUnitContext.FromUnitSystem(UnitSystem.Meters);
        return unitContext.FormatArea(value);
    }

}
