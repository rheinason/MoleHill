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

// Analysis & annotation tab: card + body builders, section/insertion/palette/range editors.
public sealed partial class MoleHillPanel
{
    private Panel CreateAnalysisCard(TerrainDefinition terrain, AnalysisDefinition analysis, bool isActive, bool isAnnotationCard)
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
                    var visibleAnalyses = isAnnotationCard
                        ? t.Analyses.Where(IsAnnotationAnalysis)
                        : t.Analyses.Where(item => !IsAnnotationAnalysis(item));

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
            var selectedTerrain = doc == null ? null : _controller.GetSelectedTerrain(doc);
            if (isAnnotationCard)
                RebuildAnnotationLayout(selectedTerrain);
            else
                RebuildAnalysisLayout(selectedTerrain);
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
        var layout = new DynamicLayout { DefaultSpacing = new Size(6, 6), Padding = new Padding(10, 8, 10, 8) };
        TerrainAnalysisSummary? summary = GetAnalysisSummary(terrain, analysis.Id);

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
                layout.AddRow(CreateAnalysisColorSettings(terrain, slope));
                break;
            case ElevationAnalysisDefinition elevation:
                layout.AddRow(CreateAnalysisColorSettings(terrain, elevation));
                break;
            case CutFillAnalysisDefinition cutFill:
                layout.AddRow(CreateAnalysisColorSettings(terrain, cutFill));
                break;

            case TerrainSectionAnalysisDefinition terrainSection:
                AddTerrainSectionCommonRows(layout, terrain, terrainSection,
                    "Curves used as cut lines through the terrain. Each curve produces one profile.");
                break;

            case CrossSectionStationAnalysisDefinition crossSection:
                AddTerrainSectionCommonRows(layout, terrain, crossSection,
                    "Alignment curve sampled at regular stations. The first curve resolved is used.");
                break;

            case LongitudinalSectionAnalysisDefinition longitudinal:
                AddTerrainSectionCommonRows(layout, terrain, longitudinal,
                    "Curve sampled along its length. Terrain elevation is read at each sample.");
                break;
        }
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

            case SlopeAnalysisDefinition slope:
                if (summary != null)
                {
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Min / Avg / Max",
                        $"{FormatSlopeSummaryValue(summary.SlopeMinPercent, slope.Unit)} / {FormatSlopeSummaryValue(summary.SlopeAveragePercent, slope.Unit)} / {FormatSlopeSummaryValue(summary.SlopeMaxPercent, slope.Unit)}",
                        "Current terrain slope summary from the last build."));
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Mapped",
                        $"{FormatSlopeSummaryValue(summary.SlopeDisplayLowPercent, slope.Unit)} to {FormatSlopeSummaryValue(summary.SlopeDisplayHighPercent, slope.Unit)}",
                        "Actual slope range currently mapped across the selected palette."));
                }
                // Legend shows the actual mapped range from the last build
                {
                    string sLow  = summary != null
                        ? FormatSlopeValue(ConvertPercentToSlopeUnit(summary.SlopeDisplayLowPercent, slope.Unit), slope.Unit)
                        : FormatSlopeValue(slope.RangeLow, slope.Unit);
                    string sHigh = summary != null
                        ? FormatSlopeValue(ConvertPercentToSlopeUnit(summary.SlopeDisplayHighPercent, slope.Unit), slope.Unit)
                        : FormatSlopeValue(slope.RangeHigh, slope.Unit);
                    layout.AddRow(CreateSlopeLegendView(
                        SlopePreviewPaletteCatalog.Resolve(slope.PalettePreset),
                        displayLowLabel: sLow,
                        displayHighLabel: sHigh,
                        mode: slope.ColorMode,
                        interval: summary == null || slope.ColorInterval <= 0.0
                            ? 0.1
                            : ConvertSlopeValue(slope.ColorInterval, slope.Unit, SlopeAnalyzer.SlopeUnit.Percent) /
                              Math.Max(1e-12, summary.SlopeDisplayHighPercent - summary.SlopeDisplayLowPercent)));
                }
                break;

            case ElevationAnalysisDefinition elevation:
                if (summary != null)
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Area",
                        FormatArea(summary.SurfaceArea),
                        "Terrain surface area from the last build."));
                {
                    // Actual low/high Z driven by either the configured range or auto-fit from last build
                    var a = summary;
                    double eLow  = a != null && elevation.AutoColorRange ? a.ElevationMinZ : elevation.RangeLow;
                    double eHigh = a != null && elevation.AutoColorRange ? a.ElevationMaxZ : elevation.RangeHigh;
                    layout.AddRow(CreateSlopeLegendView(
                        SlopePreviewPaletteCatalog.Resolve(elevation.PalettePreset),
                        displayLowLabel:  a != null ? $"{eLow:F1}" : "Low Z",
                        displayHighLabel: a != null ? $"{eHigh:F1}" : "High Z",
                        mode: elevation.ColorMode,
                        interval: AnalysisColorMapper.ResolveInterval(eLow, eHigh, elevation.ColorInterval) / Math.Max(1e-12, eHigh - eLow)));
                }
                break;

            case CutFillAnalysisDefinition cutFill:
                if (summary != null)
                {
                    layout.AddRow(CreateReadOnlyValueRow("Cut / Fill / Net",
                        $"{summary.CutVolume:F2} / {summary.FillVolume:F2} / {summary.NetVolume:F2}",
                        "Current earthworks summary from the last build."));
                }
                {
                    var a = summary;
                    double absMax = a?.CutFillDisplayAbsMax ?? Math.Max(Math.Abs(cutFill.RangeLow), Math.Abs(cutFill.RangeHigh));
                    string cfLow  = a != null ? $"{-absMax:F2}" : "Cut";
                    string cfHigh = a != null ? $"+{absMax:F2}" : "Fill";
                    layout.AddRow(CreateSlopeLegendView(
                        SlopePreviewPaletteCatalog.Resolve(cutFill.PalettePreset),
                        displayLowLabel: cfLow,
                        displayHighLabel: cfHigh,
                        mode: cutFill.ColorMode,
                        interval: AnalysisColorMapper.ResolveInterval(-absMax, absMax, cutFill.ColorInterval) / Math.Max(1e-12, absMax * 2.0)));
                }
                break;

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

            case CurveElevationLabelAnalysisDefinition curveElevation:
            {
                if (summary != null)
                {
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Curves / Labels",
                        $"{summary.SampleSourceCount} curve(s) -> {summary.GeneratedOutputCount} label(s)",
                        "Curve sources resolved and elevation annotation blocks emitted by the last build."));
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Min / Max",
                        summary.GeneratedOutputCount > 0
                            ? $"{FormatAnalysisValue(summary.SampleMinValue, curveElevation.ValueFormat)} / {FormatAnalysisValue(summary.SampleMaxValue, curveElevation.ValueFormat)}"
                            : "No samples",
                        "Terrain elevations sampled along the source curves during the last build."));
                }
                else
                {
                    layout.AddRow(CreateSelectableSummaryEditor(
                        "Summary",
                        "Rebuild required",
                        "Rebuild the terrain to generate curve elevation annotation blocks.",
                        minHeight: 42));
                }

                break;
            }

            case CurveSlopeLabelAnalysisDefinition curveSlope:
            {
                if (summary != null)
                {
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Curves / Labels",
                        $"{summary.SampleSourceCount} curve(s) -> {summary.GeneratedOutputCount} label(s)",
                        "Curve sources resolved and annotation blocks emitted by the last build."));
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Min / Avg / Max",
                        summary.GeneratedOutputCount > 0
                            ? $"{FormatSlopeValue(summary.SampleMinValue, curveSlope.Unit)} / {FormatSlopeValue(summary.SampleAverageValue, curveSlope.Unit)} / {FormatSlopeValue(summary.SampleMaxValue, curveSlope.Unit)}"
                            : "No samples",
                        "Terrain-projected curve slope values from the last build."));
                }
                else
                {
                    layout.AddRow(CreateSelectableSummaryEditor(
                        "Summary",
                        "Rebuild required",
                        "Rebuild the terrain to generate curve slope annotation blocks.",
                        minHeight: 42));
                }

                break;
            }

            case ProjectedElevationLabelAnalysisDefinition projectedElevation:
            {
                if (summary != null)
                {
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Sources / Labels",
                        $"{summary.SampleSourceCount} source(s) -> {summary.GeneratedOutputCount} label(s)",
                        "Point and curve sources resolved and annotation blocks emitted by the last build."));
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Min / Max",
                        summary.GeneratedOutputCount > 0
                            ? $"{FormatAnalysisValue(summary.SampleMinValue, projectedElevation.ValueFormat)} / {FormatAnalysisValue(summary.SampleMaxValue, projectedElevation.ValueFormat)}"
                            : "No samples",
                        "Projected terrain elevations from the last build."));
                }
                else
                {
                    layout.AddRow(CreateSelectableSummaryEditor(
                        "Summary",
                        "Rebuild required",
                        "Rebuild the terrain to generate projected elevation annotation blocks.",
                        minHeight: 42));
                }

                break;
            }

            case PointSlopeLabelAnalysisDefinition pointSlope:
            {
                if (summary != null)
                {
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Points / Labels",
                        $"{summary.SampleSourceCount} point(s) -> {summary.GeneratedOutputCount} label(s)",
                        "Point sources resolved and annotation blocks emitted by the last build."));
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Min / Avg / Max",
                        summary.GeneratedOutputCount > 0
                            ? $"{FormatSlopeValue(summary.SampleMinValue, pointSlope.Unit)} / {FormatSlopeValue(summary.SampleAverageValue, pointSlope.Unit)} / {FormatSlopeValue(summary.SampleMaxValue, pointSlope.Unit)}"
                            : "No samples",
                        "Local terrain slope values sampled at the projected points."));
                }
                else
                {
                    layout.AddRow(CreateSelectableSummaryEditor(
                        "Summary",
                        "Rebuild required",
                        "Rebuild the terrain to generate point slope annotation blocks.",
                        minHeight: 42));
                }

                break;
            }

            case SlopeArrowAnalysisDefinition slopeArrows:
            {

                if (summary != null)
                {
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Arrows",
                        $"{summary.GeneratedOutputCount} arrow(s)",
                        "Flow arrows emitted across the terrain by the last build."));
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Min / Avg / Max",
                        summary.GeneratedOutputCount > 0
                            ? $"{FormatSlopeValue(summary.SampleMinValue, slopeArrows.Unit)} / {FormatSlopeValue(summary.SampleAverageValue, slopeArrows.Unit)} / {FormatSlopeValue(summary.SampleMaxValue, slopeArrows.Unit)}"
                            : "No samples",
                        "Slope magnitudes sampled across the grid during the last build."));
                }
                else
                {
                    layout.AddRow(CreateSelectableSummaryEditor(
                        "Summary",
                        "Rebuild required",
                        "Rebuild the terrain to generate flow arrows.",
                        minHeight: 42));
                }

                break;
            }

            case GradeBetweenPointsAnalysisDefinition gradeCallout:
            {
                if (summary != null)
                {
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Lines / Callouts",
                        $"{summary.SampleSourceCount} line(s) -> {summary.GeneratedOutputCount} callout(s)",
                        "Source lines resolved and grade callouts emitted by the last build."));
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Min / Avg / Max %",
                        summary.GeneratedOutputCount > 0
                            ? $"{FormatAnalysisValue(summary.SampleMinValue, gradeCallout.ValueFormat)} / {FormatAnalysisValue(summary.SampleAverageValue, gradeCallout.ValueFormat)} / {FormatAnalysisValue(summary.SampleMaxValue, gradeCallout.ValueFormat)}"
                            : "No samples",
                        "Grade percentages computed for the source lines during the last build."));
                }
                else
                {
                    layout.AddRow(CreateSelectableSummaryEditor(
                        "Summary",
                        "Rebuild required",
                        "Rebuild the terrain to generate grade callouts.",
                        minHeight: 42));
                }

                break;
            }

            case ContourAnalysisDefinition contour:
            {
                if (summary != null)
                {
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Curves",
                        $"{summary.ContourCurveCount} curve(s) across {summary.ContourLevelCount} level(s)",
                        "Contour output generated from the last terrain build."));
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Levels",
                        summary.ContourLevelCount > 0
                            ? $"{summary.ContourFirstLevel:G4} to {summary.ContourLastLevel:G4}"
                            : "No contour levels intersected the terrain",
                        "First and last contour elevations emitted by the last build."));
                }
                else
                {
                    layout.AddRow(CreateSelectableSummaryEditor(
                        "Summary",
                        "Rebuild required",
                        "Rebuild the terrain to generate contour curves.",
                        minHeight: 42));
                }
                break;
            }

            case TerrainSectionAnalysisDefinition:
            {
                if (summary != null)
                {
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Cuts / Terrains / C-F",
                        $"{summary.SampleSourceCount} / {summary.SectionTerrainCount} / {summary.SectionCutRegionCount}-{summary.SectionFillRegionCount}",
                        $"Cut curves, available terrain profiles, and cut-fill regions from the last build ({summary.GeneratedOutputCount} objects)."));
                }
                else
                {
                    layout.AddRow(CreateSelectableSummaryEditor(
                        "Summary",
                        "Rebuild required",
                        "Rebuild the terrain to generate the section profile.",
                        minHeight: 42));
                }
                break;
            }

            case CrossSectionStationAnalysisDefinition:
            {
                if (summary != null)
                {
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Alignments / Terrains / C-F",
                        $"{summary.SampleSourceCount} / {summary.SectionTerrainCount} / {summary.SectionCutRegionCount}-{summary.SectionFillRegionCount}",
                        $"Alignments, available terrain profiles, and cut-fill regions from the last build ({summary.GeneratedOutputCount} objects)."));
                }
                else
                {
                    layout.AddRow(CreateSelectableSummaryEditor(
                        "Summary",
                        "Rebuild required",
                        "Rebuild the terrain to generate cross-sections.",
                        minHeight: 42));
                }
                break;
            }

            case LongitudinalSectionAnalysisDefinition:
            {
                if (summary != null)
                {
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Curves / Terrains / C-F",
                        $"{summary.SampleSourceCount} / {summary.SectionTerrainCount} / {summary.SectionCutRegionCount}-{summary.SectionFillRegionCount}",
                        $"Curves, available terrain profiles, and cut-fill regions from the last build ({summary.GeneratedOutputCount} objects)."));
                }
                else
                {
                    layout.AddRow(CreateSelectableSummaryEditor(
                        "Summary",
                        "Rebuild required",
                        "Rebuild the terrain to generate the longitudinal section.",
                        minHeight: 42));
                }
                break;
            }
        }

    }

    private static string FormatArea(double value)
    {
        ModelUnitContext unitContext = ModelUnitContext.FromDocument(RhinoDoc.ActiveDoc);
        if (!unitContext.IsSupported)
            unitContext = ModelUnitContext.FromUnitSystem(UnitSystem.Meters);
        return unitContext.FormatArea(value);
    }

    private void AddTerrainSectionCommonRows(
        DynamicLayout layout,
        TerrainDefinition terrain,
        TerrainSectionAnalysisDefinitionBase analysis,
        string sourceHelp)
    {
        void MutateSection(Action<TerrainSectionAnalysisDefinitionBase> apply) =>
            MutateAnalysis(terrain.TerrainId, analysis.Id, item =>
            {
                if (item is TerrainSectionAnalysisDefinitionBase section)
                    apply(section);
            }, scheduleRebuild: true);

        layout.AddRow(CreateSourceEditor(
            "Sources",
            analysis.Sources,
            apply => MutateSection(item => apply(item.Sources)),
            RhinoObjectType.Curve,
            doc => _controller.GetSelectedLayerPaths(doc),
            sourceHelp));
        layout.AddRow(CreateTerrainSectionTerrainEditor(terrain, analysis));
        layout.AddRow(CreateCheckEditor(
            "Cut / Fill",
            analysis.ShowCutFillRegions,
            value => MutateSection(item => item.ShowCutFillRegions = value),
            "Shade cut and fill between the proposed terrain and the selected reference terrain."));
        layout.AddRow(CreateOptionalColorEditor(
            "Cut Color",
            analysis.CutColorArgb,
            value => MutateSection(item => item.CutColorArgb = value ?? TerrainSectionAnalysisDefinitionBase.DefaultCutColorArgb),
            "Color used where proposed terrain is below the reference terrain.",
            TerrainSectionAnalysisDefinitionBase.DefaultCutColorArgb,
            "Default cut red"));
        layout.AddRow(CreateOptionalColorEditor(
            "Fill Color",
            analysis.FillColorArgb,
            value => MutateSection(item => item.FillColorArgb = value ?? TerrainSectionAnalysisDefinitionBase.DefaultFillColorArgb),
            "Color used where proposed terrain is above the reference terrain.",
            TerrainSectionAnalysisDefinitionBase.DefaultFillColorArgb,
            "Default fill blue"));
        layout.AddRow(CreateNumericEditor(
            "Fill Opacity %",
            analysis.CutFillOpacityPercent,
            value => MutateSection(item => item.CutFillOpacityPercent = Math.Clamp((int)Math.Round(value), 0, 100)),
            decimalPlaces: 0,
            help: "Opacity of generated cut and fill regions.",
            minValue: 0.0,
            maxValue: 100.0));
        layout.AddRow(CreateInsertionOriginEditor(terrain, analysis));
        layout.AddRow(CreateNumericEditor(
            "Text Height",
            analysis.TextHeight,
            value => MutateSection(item => item.TextHeight = Math.Max(double.Epsilon, value)),
            decimalPlaces: 3,
            help: "Height of station and elevation labels printed on the section.",
            minValue: 0.0));
        layout.AddRow(CreateLayerAssignmentEditor(
            "Output Layer",
            analysis.OutputLayerPath,
            path => MutateSection(item => item.OutputLayerPath = path),
            "Layer used for generated section geometry. Leave empty to use the terrain annotation layer."));
        layout.AddRow(CreateOptionalColorEditor(
            "Proposed Color",
            analysis.ColorArgb,
            value => MutateSection(item => item.ColorArgb = value),
            "Color for the proposed profile, grid, ticks, and labels. Clear to use the terrain or output-layer color.",
            ResolveLayerColorArgb(analysis.OutputLayerPath ?? terrain.AnnotationLayerPath),
            GetAnalysisOutputColorText(terrain, analysis.OutputLayerPath)));
    }

    private Control CreateTerrainSectionTerrainEditor(
        TerrainDefinition owner,
        TerrainSectionAnalysisDefinitionBase analysis)
    {
        RhinoDoc? doc = RhinoDoc.ActiveDoc;
        IReadOnlyList<TerrainDefinition> terrains = doc == null
            ? Array.Empty<TerrainDefinition>()
            : _controller.GetTerrains(doc);
        var selectedIds = analysis.ComparisonTerrainIds.ToHashSet();
        var list = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };

        list.Items.Add(CreateTerrainSectionChoiceRow(owner.TerrainId, owner, isOwner: true, isSelected: true, isAvailable: true, analysis));
        foreach (TerrainDefinition terrain in terrains.Where(item => item.TerrainId != owner.TerrainId))
            list.Items.Add(CreateTerrainSectionChoiceRow(
                owner.TerrainId,
                terrain,
                isOwner: false,
                isSelected: selectedIds.Contains(terrain.TerrainId),
                isAvailable: doc != null && _controller.HasCompletedFinalTerrainMesh(doc, terrain.TerrainId),
                analysis));

        foreach (Guid missingId in analysis.ComparisonTerrainIds.Where(id => terrains.All(t => t.TerrainId != id)))
        {
            list.Items.Add(new Label
            {
                Text = $"Unavailable terrain ({missingId})",
                TextColor = UiTheme.WarningText,
                Wrap = WrapMode.Word
            });
        }

        var referenceOptions = new List<(Guid? Id, string Label)> { (null, "None") };
        referenceOptions.AddRange(terrains
            .Where(item => item.TerrainId != owner.TerrainId && selectedIds.Contains(item.TerrainId))
            .Select(item => ((Guid?)item.TerrainId, item.Name)));
        var referenceDropDown = new DropDown { Width = UiMetrics.DropDown, Enabled = referenceOptions.Count > 1 };
        foreach (var option in referenceOptions)
            referenceDropDown.Items.Add(new ListItem { Text = option.Label });
        int selectedReferenceIndex = referenceOptions.FindIndex(
            option => option.Id == analysis.CutFillReferenceTerrainId);
        referenceDropDown.SelectedIndex = Math.Max(0, selectedReferenceIndex);
        ApplyHelp(referenceDropDown, "Terrain treated as existing ground for cut/fill shading.");
        referenceDropDown.SelectedIndexChanged += (_, _) =>
        {
            if (_isRefreshing)
                return;
            int index = referenceDropDown.SelectedIndex;
            if (index < 0 || index >= referenceOptions.Count)
                return;
            MutateAnalysis(owner.TerrainId, analysis.Id, item =>
            {
                if (item is TerrainSectionAnalysisDefinitionBase section)
                    section.CutFillReferenceTerrainId = referenceOptions[index].Id;
            }, scheduleRebuild: true);
            RefreshUi();
        };

        list.Items.Add(new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items =
            {
                new Label { Text = "Reference", TextColor = UiTheme.MutedText },
                referenceDropDown
            }
        });

        const string help = "Terrains drawn in this section. The owning terrain is always proposed; select other terrains for comparison.";
        return new PropertyRow(CreateHelpLabel("Terrains", help, 0), list, expandWidget: true);
    }

    private Control CreateTerrainSectionChoiceRow(
        Guid ownerTerrainId,
        TerrainDefinition terrain,
        bool isOwner,
        bool isSelected,
        bool isAvailable,
        TerrainSectionAnalysisDefinitionBase analysis)
    {
        var check = new CheckBox
        {
            Text = isOwner
                ? $"{terrain.Name} (Proposed)"
                : isAvailable ? terrain.Name : $"{terrain.Name} (awaiting final build)",
            Checked = isSelected,
            Enabled = !isOwner,
            TextColor = isAvailable || isOwner ? UiTheme.PrimaryText : UiTheme.WarningText
        };
        var swatch = new Panel
        {
            Size = new Size(12, 12),
            BackgroundColor = ToEtoColor(System.Drawing.Color.FromArgb(
                isOwner && analysis.ColorArgb.HasValue ? analysis.ColorArgb.Value : terrain.TerrainColorArgb))
        };

        if (!isOwner)
        {
            check.CheckedChanged += (_, _) =>
            {
                if (_isRefreshing)
                    return;
                bool selected = check.Checked == true;
                MutateAnalysis(
                    ownerTerrainId,
                    analysis.Id,
                    item =>
                    {
                        if (item is not TerrainSectionAnalysisDefinitionBase section)
                            return;
                        if (selected && !section.ComparisonTerrainIds.Contains(terrain.TerrainId))
                            section.ComparisonTerrainIds.Add(terrain.TerrainId);
                        else if (!selected)
                        {
                            section.ComparisonTerrainIds.RemoveAll(id => id == terrain.TerrainId);
                            if (section.CutFillReferenceTerrainId == terrain.TerrainId)
                                section.CutFillReferenceTerrainId = null;
                        }
                    },
                    scheduleRebuild: true);
                RefreshUi();
            };
        }

        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items = { swatch, check }
        };
    }

    private Control CreateInsertionOriginEditor(
        TerrainDefinition terrain,
        TerrainSectionAnalysisDefinitionBase analysis)
    {
        string text = analysis.HasInsertionPlane
            ? $"({analysis.InsertionOriginX:F2}, {analysis.InsertionOriginY:F2}, {analysis.InsertionOriginZ:F2})"
            : "(auto: offset from terrain bbox)";

        var summary = new Label
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            TextColor = analysis.HasInsertionPlane ? UiTheme.PrimaryText : UiTheme.MutedText,
            Wrap = WrapMode.None
        };
        ApplyHelp(summary, "Insertion origin where the laid-out section is placed. World X/Z axes are used for direction.");

        var pickButton = MakeInlineButton("Pick", (_, _) =>
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null)
                return;

            var gp = new RhinoGetPoint();
            gp.SetCommandPrompt("Pick section insertion origin");
            if (gp.Get() != RhinoGetResult.Point)
                return;

            RhinoPoint3d picked = gp.Point();
            MutateAnalysis(terrain.TerrainId, analysis.Id, item =>
            {
                if (item is TerrainSectionAnalysisDefinitionBase section)
                {
                    section.InsertionOriginX = picked.X;
                    section.InsertionOriginY = picked.Y;
                    section.InsertionOriginZ = picked.Z;
                    section.InsertionXAxisX = 1.0;
                    section.InsertionXAxisY = 0.0;
                    section.InsertionXAxisZ = 0.0;
                    section.InsertionYAxisX = 0.0;
                    section.InsertionYAxisY = 1.0;
                    section.InsertionYAxisZ = 0.0;
                    section.HasInsertionPlane = true;
                }
            }, scheduleRebuild: true);
            RefreshUi();
        }, "Pick the origin point where the laid-out section will be placed.");

        var resetButton = MakeInlineButton("Auto", (_, _) =>
        {
            MutateAnalysis(terrain.TerrainId, analysis.Id, item =>
            {
                if (item is TerrainSectionAnalysisDefinitionBase section)
                    section.HasInsertionPlane = false;
            }, scheduleRebuild: true);
            RefreshUi();
        }, "Clear the insertion origin and let the section auto-position next to the terrain.");

        var fields = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayoutItem(summary, expand: true),
                pickButton,
                resetButton
            }
        };

        return new PropertyRow(
            CreateHelpLabel("Insertion", "Insertion origin where the laid-out section is placed.", 0),
            fields,
            expandWidget: true);
    }
}
