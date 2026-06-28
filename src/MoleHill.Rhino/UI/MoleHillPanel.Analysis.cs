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
        string typeLabel = GetAnalysisTypeLabel(analysis);
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

        var copyButton = MakeMiniButton("Copy", (_, _) =>
        {
            DuplicateAnalysis(terrain.TerrainId, analysis.Id);
            RefreshTerrainPreview(terrain.TerrainId);
        }, "Duplicate this analysis card.", width: 46);
        var deleteButton = MakeMiniButton("Del", (_, _) =>
        {
            RemoveAnalysis(terrain.TerrainId, analysis.Id);
            RefreshTerrainPreview(terrain.TerrainId);
        }, "Delete this analysis card.", width: 38);

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
            StatusControls = new Control[]
            {
                CreateCardStatusLabel(typeLabel),
                badge
            },
            ActionControls = new Control[]
            {
                copyButton,
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

        switch (analysis)
        {
            case EarthworkAnalysisDefinition earthwork:
                layout.AddRow(CreateSourceEditor("Compare To", earthwork.Reference,
                    apply => MutateAnalysis(terrain.TerrainId, earthwork.Id, item => apply(((EarthworkAnalysisDefinition)item).Reference), scheduleRebuild: true),
                    RhinoObjectType.Mesh | RhinoObjectType.Brep | RhinoObjectType.Extrusion,
                    doc => _controller.GetSelectedLayerPaths(doc)));
                layout.AddRow(CreateSourceEditor("Boundary", earthwork.Boundary,
                    apply => MutateAnalysis(terrain.TerrainId, earthwork.Id, item => apply(((EarthworkAnalysisDefinition)item).Boundary), scheduleRebuild: true),
                    RhinoObjectType.Curve,
                    doc => _controller.GetSelectedLayerPaths(doc)));
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
                layout.AddRow(CreateSlopeUnitEditor(terrain.TerrainId, slope));
                layout.AddRow(CreateAnalysisPaletteEditor(
                    terrain.TerrainId,
                    slope,
                    "Color ramp used for the slope analysis preview."));
                layout.AddRow(CreateAnalysisRangeEditor(
                    $"Low {GetSlopeUnitSuffixLabel(slope.Unit)}",
                    slope.RangeLow,
                    value => MutateAndRefreshAnalysis(terrain.TerrainId, slope.Id, item => item.RangeLow = value),
                    "Values at or below this slope use the low end of the selected palette."));
                layout.AddRow(CreateAnalysisRangeEditor(
                    $"High {GetSlopeUnitSuffixLabel(slope.Unit)}",
                    slope.RangeHigh,
                    value => MutateAndRefreshAnalysis(terrain.TerrainId, slope.Id, item => item.RangeHigh = value),
                    "Values at or above this slope use the high end of the selected palette. Leave at 0 to auto-fit."));
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
                layout.AddRow(CreateAnalysisPaletteEditor(
                    terrain.TerrainId,
                    elevation,
                    "Color ramp used for the elevation analysis preview."));
                layout.AddRow(CreateAnalysisRangeEditor(
                    "Low Z",
                    elevation.RangeLow,
                    value => MutateAndRefreshAnalysis(terrain.TerrainId, elevation.Id, item => item.RangeLow = value),
                    "Values at or below this elevation use the low end of the selected palette. Set to 0 to auto-fit."));
                layout.AddRow(CreateAnalysisRangeEditor(
                    "High Z",
                    elevation.RangeHigh,
                    value => MutateAndRefreshAnalysis(terrain.TerrainId, elevation.Id, item => item.RangeHigh = value),
                    "Values at or above this elevation use the high end of the selected palette. Set to 0 to auto-fit."));
                if (summary != null)
                    layout.AddRow(CreateReadOnlyValueRow("Area", $"{summary.SurfaceArea:F2} sq units", "Terrain surface area from the last build."));
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
                layout.AddRow(CreateSourceEditor("Compare To", cutFill.Reference,
                    apply => MutateAnalysis(terrain.TerrainId, cutFill.Id, item => apply(((CutFillAnalysisDefinition)item).Reference), scheduleRebuild: true),
                    RhinoObjectType.Mesh | RhinoObjectType.Brep | RhinoObjectType.Extrusion,
                    doc => _controller.GetSelectedLayerPaths(doc)));
                layout.AddRow(CreateSourceEditor("Boundary", cutFill.Boundary,
                    apply => MutateAnalysis(terrain.TerrainId, cutFill.Id, item => apply(((CutFillAnalysisDefinition)item).Boundary), scheduleRebuild: true),
                    RhinoObjectType.Curve,
                    doc => _controller.GetSelectedLayerPaths(doc)));
                layout.AddRow(CreateAnalysisPaletteEditor(
                    terrain.TerrainId,
                    cutFill,
                    "Color ramp used for cut/fill analysis. Auto-fits symmetrically to the largest delta."));
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
                void MutateCurveElevation(Action<CurveElevationLabelAnalysisDefinition> apply)
                {
                    MutateAnalysis(
                        terrain.TerrainId,
                        curveElevation.Id,
                        item => apply((CurveElevationLabelAnalysisDefinition)item),
                        scheduleRebuild: true);
                }

                AddBlockAttributeAnalysisRows(
                    layout,
                    terrain,
                    curveElevation,
                    RhinoObjectType.Curve,
                    MutateCurveElevation,
                    "Curve objects or layers sampled along the terrain at regular stations for elevation labels.",
                    "Number of decimal places shown in curve elevation labels.",
                    extraRows: extraLayout =>
                    {
                        extraLayout.AddRow(CreateNumericEditor(
                            "Interval",
                            curveElevation.Interval,
                            value => MutateCurveElevation(item => item.Interval = Math.Max(0.01, value)),
                            decimalPlaces: 3,
                            help: "Distance along each source curve between elevation sample stations.",
                            minValue: 0.01));
                    });

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
                void MutateCurveSlope(Action<CurveSlopeLabelAnalysisDefinition> apply)
                {
                    MutateAnalysis(
                        terrain.TerrainId,
                        curveSlope.Id,
                        item => apply((CurveSlopeLabelAnalysisDefinition)item),
                        scheduleRebuild: true);
                }

                AddBlockAttributeAnalysisRows(
                    layout,
                    terrain,
                    curveSlope,
                    RhinoObjectType.Curve,
                    MutateCurveSlope,
                    "Curve objects or layers projected to the terrain before grade is sampled.",
                    "Number of decimal places shown in curve slope labels.",
                    extraRows: extraLayout =>
                    {
                        extraLayout.AddRow(CreateNumericEditor(
                            "Interval",
                            curveSlope.Interval,
                            value => MutateCurveSlope(item => item.Interval = Math.Max(0.01, value)),
                            decimalPlaces: 3,
                            help: "Distance along each source curve between sampled slope spans.",
                            minValue: 0.01));
                        extraLayout.AddRow(CreateSlopeUnitDropDown(
                            curveSlope.Unit,
                            unit => MutateCurveSlope(item => item.Unit = unit),
                            "Show terrain-projected curve slope labels as percent, promille, ratio, or degrees."));
                        extraLayout.AddRow(CreateCheckEditor(
                            "Flip Arrow",
                            curveSlope.FlipDirection,
                            value => MutateCurveSlope(item => item.FlipDirection = value),
                            "Rotate slope arrows 180 degrees to match alternate office conventions."));
                    });

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
                void MutateProjectedElevation(Action<ProjectedElevationLabelAnalysisDefinition> apply)
                {
                    MutateAnalysis(
                        terrain.TerrainId,
                        projectedElevation.Id,
                        item => apply((ProjectedElevationLabelAnalysisDefinition)item),
                        scheduleRebuild: true);
                }

                AddBlockAttributeAnalysisRows(
                    layout,
                    terrain,
                    projectedElevation,
                    RhinoObjectType.Point | RhinoObjectType.Curve,
                    MutateProjectedElevation,
                    "Point objects and curve edit points projected to the terrain for elevation labels.",
                    "Number of decimal places shown in projected elevation labels.");

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
                void MutatePointSlope(Action<PointSlopeLabelAnalysisDefinition> apply)
                {
                    MutateAnalysis(
                        terrain.TerrainId,
                        pointSlope.Id,
                        item => apply((PointSlopeLabelAnalysisDefinition)item),
                        scheduleRebuild: true);
                }

                AddBlockAttributeAnalysisRows(
                    layout,
                    terrain,
                    pointSlope,
                    RhinoObjectType.Point,
                    MutatePointSlope,
                    "Point objects or layers projected to the terrain before local slope is sampled.",
                    "Number of decimal places shown in point slope labels.",
                    extraRows: extraLayout =>
                    {
                        extraLayout.AddRow(CreateSlopeUnitDropDown(
                            pointSlope.Unit,
                            unit => MutatePointSlope(item => item.Unit = unit),
                            "Show terrain slope labels as percent, promille, ratio, or degrees."));
                        extraLayout.AddRow(CreateCheckEditor(
                            "Flip Arrow",
                            pointSlope.FlipDirection,
                            value => MutatePointSlope(item => item.FlipDirection = value),
                            "Rotate slope arrows 180 degrees to match alternate office conventions."));
                    });

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
                void MutateSlopeArrows(Action<SlopeArrowAnalysisDefinition> apply) =>
                    MutateAnalysis(terrain.TerrainId, slopeArrows.Id, item => apply((SlopeArrowAnalysisDefinition)item), scheduleRebuild: true);

                AddBlockAttributeAnalysisRows(
                    layout,
                    terrain,
                    slopeArrows,
                    RhinoObjectType.Curve,
                    MutateSlopeArrows,
                    "Optional closed boundary curves limiting where flow arrows are placed. Leave empty to cover the whole terrain.",
                    "Number of decimal places shown on flow-arrow slope labels.",
                    extraRows: extraLayout =>
                    {
                        extraLayout.AddRow(CreateNumericEditor(
                            "Grid Spacing",
                            slopeArrows.GridSpacing,
                            value => MutateSlopeArrows(item => item.GridSpacing = Math.Max(0.01, value)),
                            decimalPlaces: 3,
                            help: "Spacing of the sampling grid across the terrain. Smaller spacing = more arrows.",
                            minValue: 0.01));
                        extraLayout.AddRow(CreateSlopeUnitDropDown(
                            slopeArrows.Unit,
                            unit => MutateSlopeArrows(item => item.Unit = unit),
                            "Show flow-arrow slope labels as percent, promille, ratio, or degrees."));
                        extraLayout.AddRow(CreateCheckEditor(
                            "Flip Arrow",
                            slopeArrows.FlipDirection,
                            value => MutateSlopeArrows(item => item.FlipDirection = value),
                            "Rotate arrows 180 degrees (point uphill instead of downhill)."));
                    });

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
                void MutateGrade(Action<GradeBetweenPointsAnalysisDefinition> apply) =>
                    MutateAnalysis(terrain.TerrainId, gradeCallout.Id, item => apply((GradeBetweenPointsAnalysisDefinition)item), scheduleRebuild: true);

                layout.AddRow(CreateSourceEditor(
                    "Sources",
                    gradeCallout.Sources,
                    apply => MutateGrade(item => apply(item.Sources)),
                    RhinoObjectType.Curve,
                    doc => _controller.GetSelectedLayerPaths(doc),
                    "Lines whose two endpoints define the grade. Each line emits one callout."));
                layout.AddRow(CreateValueFormatEditor(
                    "Decimals",
                    gradeCallout.ValueFormat,
                    format => MutateGrade(item => item.ValueFormat = format),
                    "Number of decimal places shown on the percentage part of the callout."));
                layout.AddRow(CreateCommittedTextEditor(
                    "Prefix",
                    gradeCallout.AttributePrefix,
                    text => MutateGrade(item => item.AttributePrefix = text),
                    "Text prepended to the grade callout.",
                    trim: false));
                layout.AddRow(CreateCommittedTextEditor(
                    "Suffix",
                    gradeCallout.AttributeSuffix,
                    text => MutateGrade(item => item.AttributeSuffix = text),
                    "Text appended to the grade callout.",
                    trim: false));
                layout.AddRow(CreateNumericEditor(
                    "Text Height",
                    gradeCallout.TextHeight,
                    value => MutateGrade(item => item.TextHeight = Math.Max(0.001, value)),
                    decimalPlaces: 3,
                    help: "Text height of the callout label, and the size of the downhill arrow.",
                    minValue: 0.001));
                layout.AddRow(CreateLayerAssignmentEditor(
                    "Output Layer",
                    gradeCallout.OutputLayerPath,
                    path => MutateGrade(item => item.OutputLayerPath = path),
                    "Layer used for the callout line, arrow, and text. Leave empty to use the terrain annotation layer."));
                layout.AddRow(CreateOptionalColorEditor(
                    "Color",
                    gradeCallout.ColorArgb,
                    value => MutateGrade(item => item.ColorArgb = value),
                    "Explicit display and bake color for the callout. Clear to use the output layer color.",
                    ResolveLayerColorArgb(gradeCallout.OutputLayerPath ?? terrain.AnnotationLayerPath),
                    GetAnalysisOutputColorText(terrain, gradeCallout.OutputLayerPath)));

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
                Guid capturedContourTerrainId = terrain.TerrainId;
                Guid capturedContourId = contour.Id;
                layout.AddRow(CreateNumericEditor(
                    "Interval",
                    contour.Interval,
                    value =>
                    {
                        MutateAnalysis(capturedContourTerrainId, capturedContourId, item => ((ContourAnalysisDefinition)item).Interval = Math.Max(0.01, value), scheduleRebuild: false);
                        var doc = RhinoDoc.ActiveDoc;
                        if (doc != null)
                            _controller.RebuildContourAnalysis(doc, capturedContourTerrainId, capturedContourId);
                    },
                    decimalPlaces: 3,
                    help: "Vertical spacing between generated contour levels.",
                    minValue: 0.01));
                layout.AddRow(CreateNumericEditor(
                    "Start Z",
                    contour.StartZ,
                    value =>
                    {
                        MutateAnalysis(capturedContourTerrainId, capturedContourId, item => ((ContourAnalysisDefinition)item).StartZ = value, scheduleRebuild: false);
                        var doc = RhinoDoc.ActiveDoc;
                        if (doc != null)
                            _controller.RebuildContourAnalysis(doc, capturedContourTerrainId, capturedContourId);
                    },
                    decimalPlaces: 3,
                    help: "Base elevation offset from which contour levels are stepped.",
                    minValue: null));
                layout.AddRow(CreateLayerAssignmentEditor(
                    "Output Layer",
                    contour.OutputLayerPath,
                    path =>
                    {
                        MutateAnalysis(capturedContourTerrainId, capturedContourId, item => ((ContourAnalysisDefinition)item).OutputLayerPath = path, scheduleRebuild: false);
                        var doc = RhinoDoc.ActiveDoc;
                        if (doc != null)
                            _controller.RebuildContourAnalysis(doc, capturedContourTerrainId, capturedContourId);
                    },
                    "Layer used for generated contour curves. Leave empty to use the terrain annotation layer."));
                string defaultColorText = string.IsNullOrWhiteSpace(contour.OutputLayerPath)
                    ? string.IsNullOrWhiteSpace(terrain.AnnotationLayerPath)
                        ? $"By Layer ({TerrainDefinition.DefaultAnnotationLayerPath})"
                        : $"By Layer ({GetLeafLayerName(terrain.AnnotationLayerPath!)})"
                    : $"By Layer ({GetLeafLayerName(contour.OutputLayerPath)})";
                layout.AddRow(CreateOptionalColorEditor(
                    "Color",
                    contour.ColorArgb,
                    value =>
                    {
                        MutateAnalysis(capturedContourTerrainId, capturedContourId, item => ((ContourAnalysisDefinition)item).ColorArgb = value, scheduleRebuild: false);
                        var doc = RhinoDoc.ActiveDoc;
                        if (doc != null)
                            _controller.RefreshContourColor(doc, capturedContourTerrainId, capturedContourId, value);
                    },
                    "Explicit display and bake color for generated contour curves. Clear to use the output layer color.",
                    ResolveLayerColorArgb(contour.OutputLayerPath ?? terrain.AnnotationLayerPath),
                    defaultColorText));

                void MutateContour(Action<ContourAnalysisDefinition> apply)
                {
                    MutateAnalysis(capturedContourTerrainId, capturedContourId, item => apply((ContourAnalysisDefinition)item), scheduleRebuild: false);
                    var doc = RhinoDoc.ActiveDoc;
                    if (doc != null)
                        _controller.RebuildContourAnalysis(doc, capturedContourTerrainId, capturedContourId);
                }

                layout.AddRow(CreateCheckEditor(
                    "Label Contours",
                    contour.ShowLabels,
                    value => MutateContour(item => item.ShowLabels = value),
                    "Place elevation text along generated contour curves."));
                layout.AddRow(CreateNumericEditor(
                    "Label Interval",
                    contour.LabelInterval,
                    value => MutateContour(item => item.LabelInterval = Math.Max(0.0, value)),
                    decimalPlaces: 3,
                    help: "Spacing between repeated labels along each contour. 0 places one label per contour curve.",
                    minValue: 0.0));
                layout.AddRow(CreateNumericEditor(
                    "Label Every Nth",
                    contour.LabelEveryNth,
                    value => MutateContour(item => item.LabelEveryNth = Math.Max(1, (int)Math.Round(value))),
                    decimalPlaces: 0,
                    help: "Label only every Nth contour level (index contours). 1 labels every level.",
                    minValue: 1));
                layout.AddRow(CreateNumericEditor(
                    "Label Height",
                    contour.LabelTextHeight,
                    value => MutateContour(item => item.LabelTextHeight = Math.Max(0.001, value)),
                    decimalPlaces: 3,
                    help: "Text height of contour labels in model units.",
                    minValue: 0.001));
                layout.AddRow(CreateValueFormatEditor(
                    "Label Decimals",
                    contour.LabelFormat,
                    format => MutateContour(item => item.LabelFormat = format),
                    "Number of decimal places shown in contour elevation labels."));

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

            case TerrainSectionAnalysisDefinition terrainSection:
            {
                void MutateSection(Action<TerrainSectionAnalysisDefinition> apply) =>
                    MutateAnalysis(terrain.TerrainId, terrainSection.Id, item => apply((TerrainSectionAnalysisDefinition)item), scheduleRebuild: true);

                AddTerrainSectionCommonRows(layout, terrain, terrainSection,
                    "Curves used as cut lines through the terrain. Each curve produces one profile.");

                layout.AddRow(CreateNumericEditor(
                    "Station Tick Interval",
                    terrainSection.StationTickInterval,
                    value => MutateSection(item => item.StationTickInterval = Math.Max(0.0, value)),
                    decimalPlaces: 3,
                    help: "Spacing between station tick marks along the profile baseline. 0 disables ticks.",
                    minValue: 0.0));
                layout.AddRow(CreateNumericEditor(
                    "Elevation Grid Interval",
                    terrainSection.ElevationGridInterval,
                    value => MutateSection(item => item.ElevationGridInterval = Math.Max(0.0, value)),
                    decimalPlaces: 3,
                    help: "Vertical spacing of horizontal grid lines drawn on the section. 0 disables the grid.",
                    minValue: 0.0));
                layout.AddRow(CreateCheckEditor(
                    "Show Station Ticks",
                    terrainSection.ShowStationTicks,
                    value => MutateSection(item => item.ShowStationTicks = value),
                    "Draw tick marks at each station along the profile baseline."));
                layout.AddRow(CreateCheckEditor(
                    "Show Elevation Grid",
                    terrainSection.ShowElevationGrid,
                    value => MutateSection(item => item.ShowElevationGrid = value),
                    "Draw horizontal grid lines at each elevation increment."));
                layout.AddRow(CreateCheckEditor(
                    "Show Station Labels",
                    terrainSection.ShowStationLabels,
                    value => MutateSection(item => item.ShowStationLabels = value),
                    "Print station distance text below each tick."));

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

            case CrossSectionStationAnalysisDefinition crossSection:
            {
                void MutateCrossSection(Action<CrossSectionStationAnalysisDefinition> apply) =>
                    MutateAnalysis(terrain.TerrainId, crossSection.Id, item => apply((CrossSectionStationAnalysisDefinition)item), scheduleRebuild: true);

                AddTerrainSectionCommonRows(layout, terrain, crossSection,
                    "Alignment curve sampled at regular stations. The first curve resolved is used.");

                layout.AddRow(CreateNumericEditor(
                    "Station Interval",
                    crossSection.StationInterval,
                    value => MutateCrossSection(item => item.StationInterval = Math.Max(0.01, value)),
                    decimalPlaces: 3,
                    help: "Distance between cross-section stations along the alignment.",
                    minValue: 0.01));
                layout.AddRow(CreateNumericEditor(
                    "Cross-Section Width",
                    crossSection.CrossSectionWidth,
                    value => MutateCrossSection(item => item.CrossSectionWidth = Math.Max(0.01, value)),
                    decimalPlaces: 3,
                    help: "Total perpendicular width of each cross-section cut, centered on the alignment.",
                    minValue: 0.01));
                layout.AddRow(CreateNumericEditor(
                    "Vertical Exaggeration",
                    crossSection.VerticalExaggeration,
                    value => MutateCrossSection(item => item.VerticalExaggeration = Math.Max(0.1, value)),
                    decimalPlaces: 3,
                    help: "Vertical scale factor applied to the unrolled cross-section profiles. 1.0 = true scale.",
                    minValue: 0.1));
                layout.AddRow(CreateNumericEditor(
                    "Grid Columns",
                    crossSection.GridColumns,
                    value => MutateCrossSection(item => item.GridColumns = Math.Max(1, (int)Math.Round(value))),
                    decimalPlaces: 0,
                    help: "Number of columns in the unrolled cross-section grid layout.",
                    minValue: 1));
                layout.AddRow(CreateNumericEditor(
                    "Grid Cell Width",
                    crossSection.GridCellWidth,
                    value => MutateCrossSection(item => item.GridCellWidth = Math.Max(0.0, value)),
                    decimalPlaces: 3,
                    help: "Override cell width for the grid layout. 0 = auto.",
                    minValue: 0.0));
                layout.AddRow(CreateNumericEditor(
                    "Grid Cell Height",
                    crossSection.GridCellHeight,
                    value => MutateCrossSection(item => item.GridCellHeight = Math.Max(0.0, value)),
                    decimalPlaces: 3,
                    help: "Override cell height for the grid layout. 0 = auto.",
                    minValue: 0.0));
                layout.AddRow(CreateCheckEditor(
                    "Cut Lines on Terrain",
                    crossSection.ShowCutLinesOnTerrain,
                    value => MutateCrossSection(item => item.ShowCutLinesOnTerrain = value),
                    "Draw the perpendicular cut polylines on the terrain at each station."));
                layout.AddRow(CreateCheckEditor(
                    "Label Stations",
                    crossSection.LabelStations,
                    value => MutateCrossSection(item => item.LabelStations = value),
                    "Print station distance text on each unrolled cross-section."));
                layout.AddRow(CreateCheckEditor(
                    "Show Elevation Grid",
                    crossSection.ShowElevationGrid,
                    value => MutateCrossSection(item => item.ShowElevationGrid = value),
                    "Draw horizontal grid lines on each unrolled cross-section."));
                layout.AddRow(CreateNumericEditor(
                    "Elevation Grid Interval",
                    crossSection.ElevationGridInterval,
                    value => MutateCrossSection(item => item.ElevationGridInterval = Math.Max(0.0, value)),
                    decimalPlaces: 3,
                    help: "Vertical spacing of grid lines on the unrolled cross-sections. 0 disables.",
                    minValue: 0.0));

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

            case LongitudinalSectionAnalysisDefinition longitudinal:
            {
                void MutateLongitudinal(Action<LongitudinalSectionAnalysisDefinition> apply) =>
                    MutateAnalysis(terrain.TerrainId, longitudinal.Id, item => apply((LongitudinalSectionAnalysisDefinition)item), scheduleRebuild: true);

                AddTerrainSectionCommonRows(layout, terrain, longitudinal,
                    "Curve sampled along its length. Terrain elevation is read at each sample.");

                layout.AddRow(CreateNumericEditor(
                    "Sample Interval",
                    longitudinal.SampleInterval,
                    value => MutateLongitudinal(item => item.SampleInterval = Math.Max(0.01, value)),
                    decimalPlaces: 3,
                    help: "Distance between elevation samples along the curve.",
                    minValue: 0.01));
                layout.AddRow(CreateNumericEditor(
                    "Vertical Exaggeration",
                    longitudinal.VerticalExaggeration,
                    value => MutateLongitudinal(item => item.VerticalExaggeration = Math.Max(0.1, value)),
                    decimalPlaces: 3,
                    help: "Vertical scale factor applied to the unrolled profile. 1.0 = true scale.",
                    minValue: 0.1));
                layout.AddRow(CreateCheckEditor(
                    "Show Baseline",
                    longitudinal.ShowBaseline,
                    value => MutateLongitudinal(item => item.ShowBaseline = value),
                    "Draw the horizontal baseline (zero elevation reference) under the profile."));
                layout.AddRow(CreateCheckEditor(
                    "Show Elevation Grid",
                    longitudinal.ShowElevationGrid,
                    value => MutateLongitudinal(item => item.ShowElevationGrid = value),
                    "Draw horizontal grid lines at each elevation increment."));
                layout.AddRow(CreateNumericEditor(
                    "Elevation Grid Interval",
                    longitudinal.ElevationGridInterval,
                    value => MutateLongitudinal(item => item.ElevationGridInterval = Math.Max(0.0, value)),
                    decimalPlaces: 3,
                    help: "Vertical spacing of horizontal grid lines. 0 disables.",
                    minValue: 0.0));
                layout.AddRow(CreateCheckEditor(
                    "Show Station Labels",
                    longitudinal.ShowStationLabels,
                    value => MutateLongitudinal(item => item.ShowStationLabels = value),
                    "Print station distance text along the baseline."));
                layout.AddRow(CreateNumericEditor(
                    "Station Label Interval",
                    longitudinal.StationLabelInterval,
                    value => MutateLongitudinal(item => item.StationLabelInterval = Math.Max(0.0, value)),
                    decimalPlaces: 3,
                    help: "Spacing between station labels. 0 = auto (~quarter of total length).",
                    minValue: 0.0));

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

        return layout;
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

        var pickButton = MakeMiniButton("Pick", (_, _) =>
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
        }, "Pick the origin point where the laid-out section will be placed.", width: 46);

        var resetButton = MakeMiniButton("Auto", (_, _) =>
        {
            MutateAnalysis(terrain.TerrainId, analysis.Id, item =>
            {
                if (item is TerrainSectionAnalysisDefinitionBase section)
                    section.HasInsertionPlane = false;
            }, scheduleRebuild: true);
            RefreshUi();
        }, "Clear the insertion origin and let the section auto-position next to the terrain.", width: 46);

        var fields = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items =
            {
                new StackLayoutItem(summary, expand: true),
                pickButton,
                resetButton
            }
        };

        if (UseStackedFormRows())
        {
            return new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                Padding = new Padding(0, 3),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    CreateHelpLabel("Insertion", "Insertion origin where the laid-out section is placed.", 0),
                    fields
                }
            };
        }

        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Items =
            {
                CreateHelpLabel("Insertion", "Insertion origin where the laid-out section is placed.", NumericLabelWidth),
                new StackLayoutItem(fields, expand: true)
            }
        };
    }

    private Control CreateAnalysisPaletteEditor(Guid terrainId, AnalysisDefinition analysis, string help)
    {
        var paletteOptions = SlopePreviewPaletteCatalog.All
            .Select(item => (item.Key, item.Label))
            .ToList();
        return CreateDropDownEditor(
            "Palette",
            paletteOptions,
            analysis.PalettePreset,
            value => MutateAndRefreshAnalysis(terrainId, analysis.Id, item => item.PalettePreset = value),
            help);
    }

    private Control CreateAnalysisRangeEditor(string label, double value, Action<double> onChanged, string help)
    {
        return CreateNumericEditor(label, value, onChanged, decimalPlaces: 2, help: help);
    }

}
