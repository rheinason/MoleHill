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
            GetAnalysisCollapsedSummary(terrain, analysis),
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
                        displayHighLabel: sHigh));
                }
                break;

            case ElevationAnalysisDefinition elevation:
                if (summary != null)
                    layout.AddRow(CreateReadOnlyValueRow(
                        "Area",
                        ModelUnits.FormatArea(summary.SurfaceArea, RhinoDoc.ActiveDoc?.ModelUnitSystem ?? UnitSystem.Meters),
                        "Terrain surface area from the last build."));
                {
                    // Actual low/high Z driven by either the configured range or auto-fit from last build
                    var a = summary;
                    double eLow  = (a != null && elevation.RangeLow == 0 && elevation.RangeHigh <= elevation.RangeLow)
                        ? a.ElevationMinZ : (elevation.RangeLow != 0 ? elevation.RangeLow : a?.ElevationMinZ ?? 0);
                    double eHigh = (a != null && elevation.RangeHigh <= elevation.RangeLow)
                        ? a.ElevationMaxZ : (elevation.RangeHigh > elevation.RangeLow ? elevation.RangeHigh : a?.ElevationMaxZ ?? 0);
                    layout.AddRow(CreateSlopeLegendView(
                        SlopePreviewPaletteCatalog.Resolve(elevation.PalettePreset),
                        displayLowLabel:  a != null ? $"{eLow:F1}" : "Low Z",
                        displayHighLabel: a != null ? $"{eHigh:F1}" : "High Z"));
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
                    double absMax = a?.CutFillDisplayAbsMax ?? 0.0;
                    string cfLow  = a != null ? $"{-absMax:F2}" : "Cut";
                    string cfHigh = a != null ? $"+{absMax:F2}" : "Fill";
                    layout.AddRow(CreateSlopeLegendView(
                        SlopePreviewPaletteCatalog.Resolve(cutFill.PalettePreset),
                        displayLowLabel: cfLow,
                        displayHighLabel: cfHigh));
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
                        "Cuts / Output",
                        $"{summary.SampleSourceCount} cut(s) -> {summary.GeneratedOutputCount} object(s)",
                        "Cut curves processed and section objects emitted by the last build."));
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
                        "Alignments / Output",
                        $"{summary.SampleSourceCount} alignment(s) -> {summary.GeneratedOutputCount} object(s)",
                        "Alignment curves processed and cross-section objects emitted by the last build."));
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
                        "Curves / Output",
                        $"{summary.SampleSourceCount} curve(s) -> {summary.GeneratedOutputCount} object(s)",
                        "Curves processed and longitudinal section objects emitted by the last build."));
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
        layout.AddRow(CreateInsertionOriginEditor(terrain, analysis));
        layout.AddRow(CreateNumericEditor(
            "Text Height",
            analysis.TextHeight,
            value => MutateSection(item => item.TextHeight = Math.Max(0.01, value)),
            decimalPlaces: 3,
            help: "Height of station and elevation labels printed on the section.",
            minValue: 0.01));
        layout.AddRow(CreateLayerAssignmentEditor(
            "Output Layer",
            analysis.OutputLayerPath,
            path => MutateSection(item => item.OutputLayerPath = path),
            "Layer used for generated section geometry. Leave empty to use the terrain annotation layer."));
        layout.AddRow(CreateOptionalColorEditor(
            "Color",
            analysis.ColorArgb,
            value => MutateSection(item => item.ColorArgb = value),
            "Display and bake color for generated section geometry. Clear to use the output layer color.",
            ResolveLayerColorArgb(analysis.OutputLayerPath ?? terrain.AnnotationLayerPath),
            GetAnalysisOutputColorText(terrain, analysis.OutputLayerPath)));
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
