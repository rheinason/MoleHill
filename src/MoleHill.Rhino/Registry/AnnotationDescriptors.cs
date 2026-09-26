using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using AnnotationParam = MoleHill.Rhino.Registry.ParameterDescriptor<MoleHill.Rhino.Model.AnnotationDefinition>;
using RhinoObjectType = Rhino.DocObjects.ObjectType;

namespace MoleHill.Rhino.Registry;

// One descriptor per concrete annotation type — content that describes the terrain (contours, spot
// labels, callouts, sections). Grouped in a single file for brevity; reflection discovery in
// AnnotationTypeRegistry treats each class independently. Discriminators are unchanged from when these
// types lived in the analysis family, so saved documents still load.
//
// Parameters cover every field the schema card builder can express; anything left out here (summaries,
// the insertion-origin picker) stays in the panel's bespoke before/after hooks (MoleHillPanel.Annotations.cs).

internal static class AnnotationParameterCatalog
{
    public static readonly IReadOnlyList<(string Key, string Label)> SlopeUnitOptions = new[]
    {
        (AnalysisFormatting.GetSlopeUnitKey(SlopeAnalyzer.SlopeUnit.Percent), "Percent"),
        (AnalysisFormatting.GetSlopeUnitKey(SlopeAnalyzer.SlopeUnit.Promille), "Promille"),
        (AnalysisFormatting.GetSlopeUnitKey(SlopeAnalyzer.SlopeUnit.Ratio), "Ratio"),
        (AnalysisFormatting.GetSlopeUnitKey(SlopeAnalyzer.SlopeUnit.Degrees), "Degrees"),
    };

    /// <summary>
    /// The vertical scale row, shared by every section type. Landform sections are read for a metre or two
    /// of relief across a hundred of length, so at true scale the interesting part collapses to a hairline;
    /// exaggerating the vertical is how the drawing is made readable, and it belongs on all three section
    /// types rather than two of them.
    /// </summary>
    public static AnnotationParam VerticalExaggeration() =>
        AnnotationParam.Number(
            "VerticalExaggeration", "Vertical Exaggeration",
            a => ((TerrainSectionAnnotationDefinitionBase)a).VerticalExaggeration,
            (a, v) => ((TerrainSectionAnnotationDefinitionBase)a).VerticalExaggeration = Math.Max(0.1, v),
            "Vertical scale relative to horizontal. 1 draws the section true to shape; 5 or 10 stretches " +
            "elevations so gentle ground reads. Affects the drawing only, never the terrain.",
            min: 0.1,
            decimalPlaces: 2);

    public static AnnotationParam SlopeUnitChoice(string help) =>
        AnnotationParam.Choice(
            "Unit", "Units", SlopeUnitOptions,
            a => AnalysisFormatting.GetSlopeUnitKey(GetUnit(a)),
            (a, v) => SetUnit(a, AnalysisFormatting.ParseSlopeUnit(v ?? "percent")),
            help);

    private static SlopeAnalyzer.SlopeUnit GetUnit(AnnotationDefinition a) => a switch
    {
        CurveSlopeLabelAnnotationDefinition d => d.Unit,
        PointSlopeLabelAnnotationDefinition d => d.Unit,
        SlopeArrowAnnotationDefinition d => d.Unit,
        ReportTableAnnotationDefinition d => d.Unit,
        _ => SlopeAnalyzer.SlopeUnit.Percent
    };

    private static void SetUnit(AnnotationDefinition a, SlopeAnalyzer.SlopeUnit unit)
    {
        switch (a)
        {
            case CurveSlopeLabelAnnotationDefinition d: d.Unit = unit; break;
            case PointSlopeLabelAnnotationDefinition d: d.Unit = unit; break;
            case SlopeArrowAnnotationDefinition d: d.Unit = unit; break;
            case ReportTableAnnotationDefinition d: d.Unit = unit; break;
        }
    }

    /// <summary>The shared tail of every block-attribute analysis card: value format, prefix/suffix,
    /// block scale, output layer, and color. Mirrors the former AddBlockAttributeAnalysisRows helper.</summary>
    public static IEnumerable<AnnotationParam> BlockAttributeTail<TAnalysis>(string formatHelp)
        where TAnalysis : BlockAttributeAnnotationDefinition
    {
        yield return AnnotationParam.Choice(
            "ValueFormat", "Decimals", null,
            a => ((TAnalysis)a).ValueFormat,
            (a, v) => ((TAnalysis)a).ValueFormat = v ?? "F1",
            formatHelp,
            optionsFor: a => AnalysisFormatting.GetValueFormatOptions(((TAnalysis)a).ValueFormat));
        yield return AnnotationParam.Text(
            "AttributePrefix", "Prefix",
            a => ((TAnalysis)a).AttributePrefix,
            (a, v) => ((TAnalysis)a).AttributePrefix = v ?? string.Empty,
            "Text prepended to the formatted value when filling the DISPLAY block attribute.",
            trim: false);
        yield return AnnotationParam.Text(
            "AttributeSuffix", "Suffix",
            a => ((TAnalysis)a).AttributeSuffix,
            (a, v) => ((TAnalysis)a).AttributeSuffix = v ?? string.Empty,
            "Text appended after the formatted value and unit when filling the DISPLAY block attribute.",
            trim: false);
        yield return AnnotationParam.Number(
            "BlockScale", "Block Scale",
            a => ((TAnalysis)a).BlockScale,
            (a, v) => ((TAnalysis)a).BlockScale = v,
            "Scale factor for inserted annotation blocks.",
            min: 0.01);
        yield return AnnotationParam.Color(
            "ColorArgb", "Color",
            a => ((TAnalysis)a).ColorArgb,
            (a, v) => ((TAnalysis)a).ColorArgb = v,
            "Explicit display and bake color for generated annotation blocks. Clear to take the colour from the layer its role routes to.",
            fallbackColor: (terrain, _) => AnalysisFormatting.ResolveLayerColorArgb(
                AnalysisFormatting.GetRoleLayerPath(terrain, LayerRole.Markers)),
            defaultText: (terrain, _) => AnalysisFormatting.GetRoleColorText(terrain, LayerRole.Markers));
    }
}

internal sealed class ContourAnnotationDescriptor : AnnotationTypeDescriptor
{
    public override string Kind => "contour";
    public override Type DefinitionType => typeof(ContourAnnotationDefinition);
    public override string TypeLabel => "Contours";
    public override string MenuLabel => "Contours";
    public override string IconLabel => "CT";
    public override string? IconName => "AnContour";
    public override int AccentArgb => unchecked((int)0xFF00796B);
    public override string Subtitle => "Contour lines at fixed intervals";
    public override int SortOrder => 0;
    public override AnnotationDefinition Create() => new ContourAnnotationDefinition();

    public override IReadOnlyList<AnnotationParam> Parameters { get; } = new[]
    {
        AnnotationParam.Number(
            "Interval", "Interval",
            a => ((ContourAnnotationDefinition)a).Interval,
            (a, v) => ((ContourAnnotationDefinition)a).Interval = Math.Max(double.Epsilon, v),
            "Vertical spacing between generated contour levels.",
            min: 0.0, incrementalCommit: true),
        AnnotationParam.Number(
            "StartZ", "Start Z",
            a => ((ContourAnnotationDefinition)a).StartZ,
            (a, v) => ((ContourAnnotationDefinition)a).StartZ = v,
            "Base elevation offset from which contour levels are stepped.",
            min: null, incrementalCommit: true),
        AnnotationParam.Number(
            "MajorEveryNth", "Major Every Nth",
            a => ((ContourAnnotationDefinition)a).MajorEveryNth,
            (a, v) => ((ContourAnnotationDefinition)a).MajorEveryNth = Math.Max(1, (int)Math.Round(v)),
            "Every Nth level is a major (index) contour. 5 is the usual survey convention; 1 makes every contour major.",
            min: 1, decimalPlaces: 0, incrementalCommit: true),
        AnnotationParam.Bool(
            "SeparateMajorMinorLayers", "Split Major / Minor",
            a => ((ContourAnnotationDefinition)a).SeparateMajorMinorLayers,
            (a, v) => ((ContourAnnotationDefinition)a).SeparateMajorMinorLayers = v,
            "Send major and minor contours to separate Contours::Major and Contours::Minor sublayers so print width and linetype are controlled per layer in Rhino.",
            incrementalCommit: true),
        AnnotationParam.Color(
            "ColorArgb", "Color",
            a => ((ContourAnnotationDefinition)a).ColorArgb,
            (a, v) => ((ContourAnnotationDefinition)a).ColorArgb = v,
            "Explicit display and bake color for generated contour curves. Clear to take the colour from the layer its role routes to.",
            fallbackColor: (terrain, _) => AnalysisFormatting.ResolveLayerColorArgb(
                AnalysisFormatting.GetRoleLayerPath(terrain, LayerRole.Contours)),
            defaultText: (terrain, _) => AnalysisFormatting.GetRoleColorText(terrain, LayerRole.Contours),
            incrementalCommit: true),
        AnnotationParam.Bool(
            "ShowLabels", "Label Contours",
            a => ((ContourAnnotationDefinition)a).ShowLabels,
            (a, v) => ((ContourAnnotationDefinition)a).ShowLabels = v,
            "Place elevation text along generated contour curves.",
            incrementalCommit: true),
        AnnotationParam.Number(
            "LabelInterval", "Label Interval",
            a => ((ContourAnnotationDefinition)a).LabelInterval,
            (a, v) => ((ContourAnnotationDefinition)a).LabelInterval = Math.Max(0.0, v),
            "Spacing between repeated labels along each contour. 0 places one label per contour curve.",
            min: 0.0, incrementalCommit: true),
        AnnotationParam.Number(
            "LabelEveryNth", "Label Every Nth",
            a => ((ContourAnnotationDefinition)a).LabelEveryNth,
            (a, v) => ((ContourAnnotationDefinition)a).LabelEveryNth = Math.Max(1, (int)Math.Round(v)),
            "Label only every Nth contour level (index contours). 1 labels every level.",
            min: 1, decimalPlaces: 0, incrementalCommit: true),
        AnnotationParam.Choice(
            "LabelFormat", "Label Decimals", null,
            a => ((ContourAnnotationDefinition)a).LabelFormat,
            (a, v) => ((ContourAnnotationDefinition)a).LabelFormat = v ?? "F2",
            "Number of decimal places shown in contour elevation labels.",
            incrementalCommit: true,
            optionsFor: a => AnalysisFormatting.GetValueFormatOptions(((ContourAnnotationDefinition)a).LabelFormat)),
    };
}

internal sealed class CurveElevationLabelAnnotationDescriptor : AnnotationTypeDescriptor
{
    public override string Kind => "curve-elevation-label";
    public override Type DefinitionType => typeof(CurveElevationLabelAnnotationDefinition);
    public override string TypeLabel => "Spot Heights (Curve)";
    public override string MenuLabel => "Spot Heights (Curve)";
    public override string IconLabel => "CE";
    public override string? IconName => "AnCurveElevation";
    public override int AccentArgb => unchecked((int)0xFF1565C0);
    public override string Subtitle => "Elevation labels sampled along a curve";
    public override int SortOrder => 1;
    public override AnnotationDefinition Create() => new CurveElevationLabelAnnotationDefinition();

    public override IReadOnlyList<AnnotationParam> Parameters { get; } = new[]
    {
        AnnotationParam.Sources(
            "Sources", "Sources",
            a => ((CurveElevationLabelAnnotationDefinition)a).Sources,
            RhinoObjectType.Curve,
            "Curve objects or layers sampled along the terrain at regular stations for elevation labels."),
        AnnotationParam.Number(
            "Interval", "Interval",
            a => ((CurveElevationLabelAnnotationDefinition)a).Interval,
            (a, v) => ((CurveElevationLabelAnnotationDefinition)a).Interval = Math.Max(double.Epsilon, v),
            "Distance along each source curve between elevation sample stations.",
            min: 0.0),
    }.Concat(AnnotationParameterCatalog.BlockAttributeTail<CurveElevationLabelAnnotationDefinition>(
        "Number of decimal places shown in curve elevation labels.")).ToArray();
}

internal sealed class CurveSlopeLabelAnnotationDescriptor : AnnotationTypeDescriptor
{
    public override string Kind => "curve-slope-label";
    public override Type DefinitionType => typeof(CurveSlopeLabelAnnotationDefinition);
    public override string TypeLabel => "Spot Slope (Curve)";
    public override string MenuLabel => "Spot Slope (Curve)";
    public override string IconLabel => "C%";
    public override string? IconName => "AnCurveSlope";
    public override int AccentArgb => unchecked((int)0xFF2E7D32);
    public override string Subtitle => "Slope labels sampled along a curve";
    public override int SortOrder => 2;
    public override AnnotationDefinition Create() => new CurveSlopeLabelAnnotationDefinition();

    public override IReadOnlyList<AnnotationParam> Parameters { get; } = new[]
    {
        AnnotationParam.Sources(
            "Sources", "Sources",
            a => ((CurveSlopeLabelAnnotationDefinition)a).Sources,
            RhinoObjectType.Curve,
            "Curve objects or layers projected to the terrain before grade is sampled."),
        AnnotationParam.Number(
            "Interval", "Interval",
            a => ((CurveSlopeLabelAnnotationDefinition)a).Interval,
            (a, v) => ((CurveSlopeLabelAnnotationDefinition)a).Interval = Math.Max(double.Epsilon, v),
            "Distance along each source curve between sampled slope spans.",
            min: 0.0),
        AnnotationParameterCatalog.SlopeUnitChoice(
            "Show terrain-projected curve slope labels as percent, promille, ratio, or degrees."),
        AnnotationParam.Bool(
            "FlipDirection", "Flip Arrow",
            a => ((CurveSlopeLabelAnnotationDefinition)a).FlipDirection,
            (a, v) => ((CurveSlopeLabelAnnotationDefinition)a).FlipDirection = v,
            "Rotate slope arrows 180 degrees to match alternate office conventions."),
    }.Concat(AnnotationParameterCatalog.BlockAttributeTail<CurveSlopeLabelAnnotationDefinition>(
        "Number of decimal places shown in curve slope labels.")).ToArray();
}

internal sealed class ProjectedElevationLabelAnnotationDescriptor : AnnotationTypeDescriptor
{
    public override string Kind => "projected-elevation-label";
    public override Type DefinitionType => typeof(ProjectedElevationLabelAnnotationDefinition);
    public override string TypeLabel => "Spot Heights (Points)";
    public override string MenuLabel => "Spot Heights (Points)";
    public override string IconLabel => "PZ";
    public override string? IconName => "AnProjElevation";
    public override int AccentArgb => unchecked((int)0xFF1565C0);
    public override string Subtitle => "Elevation labels at picked points";
    public override int SortOrder => 3;
    public override AnnotationDefinition Create() => new ProjectedElevationLabelAnnotationDefinition();

    public override IReadOnlyList<AnnotationParam> Parameters { get; } = new[]
    {
        AnnotationParam.Sources(
            "Sources", "Sources",
            a => ((ProjectedElevationLabelAnnotationDefinition)a).Sources,
            RhinoObjectType.Point | RhinoObjectType.Curve,
            "Point objects and curve edit points projected to the terrain for elevation labels."),
    }.Concat(AnnotationParameterCatalog.BlockAttributeTail<ProjectedElevationLabelAnnotationDefinition>(
        "Number of decimal places shown in projected elevation labels.")).ToArray();
}

internal sealed class PointSlopeLabelAnnotationDescriptor : AnnotationTypeDescriptor
{
    public override string Kind => "point-slope-label";
    public override Type DefinitionType => typeof(PointSlopeLabelAnnotationDefinition);
    public override string TypeLabel => "Spot Slope (Points)";
    public override string MenuLabel => "Spot Slope (Points)";
    public override string IconLabel => "P%";
    public override string? IconName => "AnPointSlope";
    public override int AccentArgb => unchecked((int)0xFF0288D1);
    public override string Subtitle => "Slope labels at picked points";
    public override int SortOrder => 4;
    public override AnnotationDefinition Create() => new PointSlopeLabelAnnotationDefinition();

    public override IReadOnlyList<AnnotationParam> Parameters { get; } = new[]
    {
        AnnotationParam.Sources(
            "Sources", "Sources",
            a => ((PointSlopeLabelAnnotationDefinition)a).Sources,
            RhinoObjectType.Point | RhinoObjectType.Curve,
            "Point objects and curve edit points projected to the terrain before local slope is sampled."),
        AnnotationParameterCatalog.SlopeUnitChoice(
            "Show terrain slope labels as percent, promille, ratio, or degrees."),
        AnnotationParam.Bool(
            "FlipDirection", "Flip Arrow",
            a => ((PointSlopeLabelAnnotationDefinition)a).FlipDirection,
            (a, v) => ((PointSlopeLabelAnnotationDefinition)a).FlipDirection = v,
            "Rotate slope arrows 180 degrees to match alternate office conventions."),
    }.Concat(AnnotationParameterCatalog.BlockAttributeTail<PointSlopeLabelAnnotationDefinition>(
        "Number of decimal places shown in point slope labels.")).ToArray();
}

internal sealed class SlopeArrowAnnotationDescriptor : AnnotationTypeDescriptor
{
    public override string Kind => "slope-arrows";
    public override Type DefinitionType => typeof(SlopeArrowAnnotationDefinition);
    public override string TypeLabel => "Flow Arrows";
    public override string MenuLabel => "Flow Arrows";
    public override string IconLabel => "FA";
    public override string? IconName => "AnFlowArrows";
    public override int AccentArgb => unchecked((int)0xFF0288D1);
    public override string Subtitle => "Downhill arrows on a grid";
    public override int SortOrder => 5;
    public override AnnotationDefinition Create() => new SlopeArrowAnnotationDefinition();

    public override IReadOnlyList<AnnotationParam> Parameters { get; } = new[]
    {
        AnnotationParam.Sources(
            "Sources", "Sources",
            a => ((SlopeArrowAnnotationDefinition)a).Sources,
            RhinoObjectType.Curve,
            "Optional closed boundary curves limiting where flow arrows are placed. Leave empty to cover the whole terrain."),
        AnnotationParam.Number(
            "GridSpacing", "Grid Spacing",
            a => ((SlopeArrowAnnotationDefinition)a).GridSpacing,
            (a, v) => ((SlopeArrowAnnotationDefinition)a).GridSpacing = Math.Max(double.Epsilon, v),
            "Spacing of the sampling grid across the terrain. Smaller spacing = more arrows.",
            min: 0.0),
        AnnotationParameterCatalog.SlopeUnitChoice(
            "Show flow-arrow slope labels as percent, promille, ratio, or degrees."),
        AnnotationParam.Bool(
            "FlipDirection", "Flip Arrow",
            a => ((SlopeArrowAnnotationDefinition)a).FlipDirection,
            (a, v) => ((SlopeArrowAnnotationDefinition)a).FlipDirection = v,
            "Rotate arrows 180 degrees (point uphill instead of downhill)."),
    }.Concat(AnnotationParameterCatalog.BlockAttributeTail<SlopeArrowAnnotationDefinition>(
        "Number of decimal places shown on flow-arrow slope labels.")).ToArray();
}

internal sealed class GradeBetweenPointsAnnotationDescriptor : AnnotationTypeDescriptor
{
    public override string Kind => "grade-callout";
    public override Type DefinitionType => typeof(GradeBetweenPointsAnnotationDefinition);
    public override string TypeLabel => "Grade Callout";
    public override string MenuLabel => "Grade Callout";
    public override string IconLabel => "1:n";
    public override string? IconName => "AnGradeCallout";
    public override int AccentArgb => unchecked((int)0xFF00838F);
    public override string Subtitle => "Grade between two points (1:n + %)";
    public override int SortOrder => 6;
    public override AnnotationDefinition Create() => new GradeBetweenPointsAnnotationDefinition();

    public override IReadOnlyList<AnnotationParam> Parameters { get; } = new[]
    {
        AnnotationParam.Sources(
            "Sources", "Sources",
            a => ((GradeBetweenPointsAnnotationDefinition)a).Sources,
            RhinoObjectType.Curve,
            "Lines whose two endpoints define the grade. Each line emits one callout."),
        AnnotationParam.Choice(
            "ValueFormat", "Decimals", null,
            a => ((GradeBetweenPointsAnnotationDefinition)a).ValueFormat,
            (a, v) => ((GradeBetweenPointsAnnotationDefinition)a).ValueFormat = v ?? "F1",
            "Number of decimal places shown on the percentage part of the callout.",
            optionsFor: a => AnalysisFormatting.GetValueFormatOptions(((GradeBetweenPointsAnnotationDefinition)a).ValueFormat)),
        AnnotationParam.Text(
            "AttributePrefix", "Prefix",
            a => ((GradeBetweenPointsAnnotationDefinition)a).AttributePrefix,
            (a, v) => ((GradeBetweenPointsAnnotationDefinition)a).AttributePrefix = v ?? string.Empty,
            "Text prepended to the grade callout.",
            trim: false),
        AnnotationParam.Text(
            "AttributeSuffix", "Suffix",
            a => ((GradeBetweenPointsAnnotationDefinition)a).AttributeSuffix,
            (a, v) => ((GradeBetweenPointsAnnotationDefinition)a).AttributeSuffix = v ?? string.Empty,
            "Text appended to the grade callout.",
            trim: false),
        AnnotationParam.Color(
            "ColorArgb", "Color",
            a => ((GradeBetweenPointsAnnotationDefinition)a).ColorArgb,
            (a, v) => ((GradeBetweenPointsAnnotationDefinition)a).ColorArgb = v,
            "Explicit display and bake color for the callout. Clear to take the colour from the layer its role routes to.",
            fallbackColor: (terrain, _) => AnalysisFormatting.ResolveLayerColorArgb(
                AnalysisFormatting.GetRoleLayerPath(terrain, LayerRole.Labels)),
            defaultText: (terrain, _) => AnalysisFormatting.GetRoleColorText(terrain, LayerRole.Labels)),
    };
}

internal sealed class TerrainSectionAnnotationDescriptor : AnnotationTypeDescriptor
{
    public override string Kind => "terrain-section";
    public override Type DefinitionType => typeof(TerrainSectionAnnotationDefinition);
    public override string TypeLabel => "Section Cut";
    public override string MenuLabel => "Section Cut";
    public override string IconLabel => "TS";
    public override string? IconName => "AnTerrainSection";
    public override int AccentArgb => unchecked((int)0xFF7B1FA2);
    public override string Subtitle => "Profile at the cut line";
    public override int SortOrder => 7;
    public override AnnotationDefinition Create() => new TerrainSectionAnnotationDefinition();

    public override IReadOnlyList<AnnotationParam> Parameters { get; } = new[]
    {
        AnnotationParameterCatalog.VerticalExaggeration(),
        AnnotationParam.Number(
            "StationTickInterval", "Station Tick Interval",
            a => ((TerrainSectionAnnotationDefinition)a).StationTickInterval,
            (a, v) => ((TerrainSectionAnnotationDefinition)a).StationTickInterval = Math.Max(0.0, v),
            "Spacing between station tick marks along the profile baseline. 0 disables ticks.",
            min: 0.0),
        AnnotationParam.Number(
            "ElevationGridInterval", "Elevation Grid Interval",
            a => ((TerrainSectionAnnotationDefinition)a).ElevationGridInterval,
            (a, v) => ((TerrainSectionAnnotationDefinition)a).ElevationGridInterval = Math.Max(0.0, v),
            "Vertical spacing of horizontal grid lines drawn on the section. 0 = auto.",
            min: 0.0),
        AnnotationParam.Bool(
            "ShowStationTicks", "Show Station Ticks",
            a => ((TerrainSectionAnnotationDefinition)a).ShowStationTicks,
            (a, v) => ((TerrainSectionAnnotationDefinition)a).ShowStationTicks = v,
            "Draw tick marks at each station along the profile baseline."),
        AnnotationParam.Bool(
            "ShowElevationGrid", "Show Elevation Grid",
            a => ((TerrainSectionAnnotationDefinition)a).ShowElevationGrid,
            (a, v) => ((TerrainSectionAnnotationDefinition)a).ShowElevationGrid = v,
            "Draw horizontal grid lines at each elevation increment."),
        AnnotationParam.Bool(
            "ShowStationLabels", "Show Station Labels",
            a => ((TerrainSectionAnnotationDefinition)a).ShowStationLabels,
            (a, v) => ((TerrainSectionAnnotationDefinition)a).ShowStationLabels = v,
            "Print station distance text below each tick."),
    };

    public override string? DescribeBlocker(TerrainDefinition terrain, AnnotationDefinition annotation)
    {
        var section = (TerrainSectionAnnotationDefinitionBase)annotation;
        if (!section.Sources.HasReferences)
            return "Needs a curve to cut along — set “Sources” below.";

        // With no explicit reference a section shades against the terrain's initial triangulation, so the
        // only real gap is a terrain nothing has graded yet.
        if (section.ShowCutFillRegions
            && !section.HasCutFillReference
            && !AnalysisPrerequisites.HasElevationChangingModifier(terrain))
        {
            return AnalysisPrerequisites.NothingToCompareMessage;
        }

        return null;
    }

    public override string? DescribeBasis(TerrainDefinition terrain, AnnotationDefinition annotation)
    {
        var section = (TerrainSectionAnnotationDefinitionBase)annotation;
        return section.ShowCutFillRegions
            ? AnalysisPrerequisites.DescribeBasis(section.HasCutFillReference)
            : null;
    }

}

internal sealed class CrossSectionStationAnnotationDescriptor : AnnotationTypeDescriptor
{
    public override string Kind => "cross-section-station";
    public override Type DefinitionType => typeof(CrossSectionStationAnnotationDefinition);
    public override string TypeLabel => "Cross-Sections";
    public override string MenuLabel => "Cross-Sections";
    public override string IconLabel => "XS";
    public override string? IconName => "AnCrossSection";
    public override int AccentArgb => unchecked((int)0xFF8E24AA);
    public override string Subtitle => "Unrolled cuts at stations, in a grid";
    public override int SortOrder => 8;
    public override AnnotationDefinition Create() => new CrossSectionStationAnnotationDefinition();

    public override IReadOnlyList<AnnotationParam> Parameters { get; } = new[]
    {
        AnnotationParam.Number(
            "StationInterval", "Station Interval",
            a => ((CrossSectionStationAnnotationDefinition)a).StationInterval,
            (a, v) => ((CrossSectionStationAnnotationDefinition)a).StationInterval = Math.Max(double.Epsilon, v),
            "Distance between cross-section stations along the alignment.",
            min: 0.0),
        AnnotationParam.Number(
            "CrossSectionWidth", "Cross-Section Width",
            a => ((CrossSectionStationAnnotationDefinition)a).CrossSectionWidth,
            (a, v) => ((CrossSectionStationAnnotationDefinition)a).CrossSectionWidth = Math.Max(double.Epsilon, v),
            "Total perpendicular width of each cross-section cut, centered on the alignment.",
            min: 0.0),
        AnnotationParameterCatalog.VerticalExaggeration(),
        AnnotationParam.Number(
            "GridColumns", "Grid Columns",
            a => ((CrossSectionStationAnnotationDefinition)a).GridColumns,
            (a, v) => ((CrossSectionStationAnnotationDefinition)a).GridColumns = Math.Max(1, (int)Math.Round(v)),
            "Number of columns in the unrolled cross-section grid layout.",
            min: 1, decimalPlaces: 0),
        AnnotationParam.Number(
            "GridCellWidth", "Grid Cell Width",
            a => ((CrossSectionStationAnnotationDefinition)a).GridCellWidth,
            (a, v) => ((CrossSectionStationAnnotationDefinition)a).GridCellWidth = Math.Max(0.0, v),
            "Override cell width for the grid layout. 0 = auto.",
            min: 0.0),
        AnnotationParam.Number(
            "GridCellHeight", "Grid Cell Height",
            a => ((CrossSectionStationAnnotationDefinition)a).GridCellHeight,
            (a, v) => ((CrossSectionStationAnnotationDefinition)a).GridCellHeight = Math.Max(0.0, v),
            "Override cell height for the grid layout. 0 = auto.",
            min: 0.0),
        AnnotationParam.Bool(
            "ShowCutLinesOnTerrain", "Cut Lines on Terrain",
            a => ((CrossSectionStationAnnotationDefinition)a).ShowCutLinesOnTerrain,
            (a, v) => ((CrossSectionStationAnnotationDefinition)a).ShowCutLinesOnTerrain = v,
            "Draw the perpendicular cut polylines on the terrain at each station."),
        AnnotationParam.Bool(
            "LabelStations", "Label Stations",
            a => ((CrossSectionStationAnnotationDefinition)a).LabelStations,
            (a, v) => ((CrossSectionStationAnnotationDefinition)a).LabelStations = v,
            "Print station distance text on each unrolled cross-section."),
        AnnotationParam.Bool(
            "ShowElevationGrid", "Show Elevation Grid",
            a => ((CrossSectionStationAnnotationDefinition)a).ShowElevationGrid,
            (a, v) => ((CrossSectionStationAnnotationDefinition)a).ShowElevationGrid = v,
            "Draw horizontal grid lines on each unrolled cross-section."),
        AnnotationParam.Number(
            "ElevationGridInterval", "Elevation Grid Interval",
            a => ((CrossSectionStationAnnotationDefinition)a).ElevationGridInterval,
            (a, v) => ((CrossSectionStationAnnotationDefinition)a).ElevationGridInterval = Math.Max(0.0, v),
            "Vertical spacing of grid lines on the unrolled cross-sections. 0 = auto.",
            min: 0.0),
    };

    public override string? DescribeBlocker(TerrainDefinition terrain, AnnotationDefinition annotation)
    {
        var section = (TerrainSectionAnnotationDefinitionBase)annotation;
        if (!section.Sources.HasReferences)
            return "Needs an alignment curve to sample — set “Sources” below.";

        // With no explicit reference a section shades against the terrain's initial triangulation, so the
        // only real gap is a terrain nothing has graded yet.
        if (section.ShowCutFillRegions
            && !section.HasCutFillReference
            && !AnalysisPrerequisites.HasElevationChangingModifier(terrain))
        {
            return AnalysisPrerequisites.NothingToCompareMessage;
        }

        return null;
    }

    public override string? DescribeBasis(TerrainDefinition terrain, AnnotationDefinition annotation)
    {
        var section = (TerrainSectionAnnotationDefinitionBase)annotation;
        return section.ShowCutFillRegions
            ? AnalysisPrerequisites.DescribeBasis(section.HasCutFillReference)
            : null;
    }

}

internal sealed class LongitudinalSectionAnnotationDescriptor : AnnotationTypeDescriptor
{
    public override string Kind => "longitudinal-section";
    public override Type DefinitionType => typeof(LongitudinalSectionAnnotationDefinition);
    public override string TypeLabel => "Section Along Curve";
    public override string MenuLabel => "Section Along Curve";
    public override string IconLabel => "LS";
    public override string? IconName => "AnLongSection";
    public override int AccentArgb => unchecked((int)0xFF5E35B1);
    public override string Subtitle => "Unrolled profile that follows a curve";
    public override int SortOrder => 9;
    public override AnnotationDefinition Create() => new LongitudinalSectionAnnotationDefinition();

    public override IReadOnlyList<AnnotationParam> Parameters { get; } = new[]
    {
        AnnotationParam.Number(
            "SampleInterval", "Sample Interval",
            a => ((LongitudinalSectionAnnotationDefinition)a).SampleInterval,
            (a, v) => ((LongitudinalSectionAnnotationDefinition)a).SampleInterval = Math.Max(double.Epsilon, v),
            "Distance between elevation samples along the curve.",
            min: 0.0),
        AnnotationParameterCatalog.VerticalExaggeration(),
        AnnotationParam.Bool(
            "ShowBaseline", "Show Baseline",
            a => ((LongitudinalSectionAnnotationDefinition)a).ShowBaseline,
            (a, v) => ((LongitudinalSectionAnnotationDefinition)a).ShowBaseline = v,
            "Draw the horizontal baseline (zero elevation reference) under the profile."),
        AnnotationParam.Bool(
            "ShowElevationGrid", "Show Elevation Grid",
            a => ((LongitudinalSectionAnnotationDefinition)a).ShowElevationGrid,
            (a, v) => ((LongitudinalSectionAnnotationDefinition)a).ShowElevationGrid = v,
            "Draw horizontal grid lines at each elevation increment."),
        AnnotationParam.Number(
            "ElevationGridInterval", "Elevation Grid Interval",
            a => ((LongitudinalSectionAnnotationDefinition)a).ElevationGridInterval,
            (a, v) => ((LongitudinalSectionAnnotationDefinition)a).ElevationGridInterval = Math.Max(0.0, v),
            "Vertical spacing of horizontal grid lines. 0 = auto.",
            min: 0.0),
        AnnotationParam.Bool(
            "ShowStationLabels", "Show Station Labels",
            a => ((LongitudinalSectionAnnotationDefinition)a).ShowStationLabels,
            (a, v) => ((LongitudinalSectionAnnotationDefinition)a).ShowStationLabels = v,
            "Print station distance text along the baseline."),
        AnnotationParam.Number(
            "StationLabelInterval", "Station Label Interval",
            a => ((LongitudinalSectionAnnotationDefinition)a).StationLabelInterval,
            (a, v) => ((LongitudinalSectionAnnotationDefinition)a).StationLabelInterval = Math.Max(0.0, v),
            "Spacing between station labels. 0 = auto (~quarter of total length).",
            min: 0.0),
    };

    public override string? DescribeBlocker(TerrainDefinition terrain, AnnotationDefinition annotation)
    {
        var section = (TerrainSectionAnnotationDefinitionBase)annotation;
        if (!section.Sources.HasReferences)
            return "Needs a curve to sample along — set “Sources” below.";

        // With no explicit reference a section shades against the terrain's initial triangulation, so the
        // only real gap is a terrain nothing has graded yet.
        if (section.ShowCutFillRegions
            && !section.HasCutFillReference
            && !AnalysisPrerequisites.HasElevationChangingModifier(terrain))
        {
            return AnalysisPrerequisites.NothingToCompareMessage;
        }

        return null;
    }

    public override string? DescribeBasis(TerrainDefinition terrain, AnnotationDefinition annotation)
    {
        var section = (TerrainSectionAnnotationDefinitionBase)annotation;
        return section.ShowCutFillRegions
            ? AnalysisPrerequisites.DescribeBasis(section.HasCutFillReference)
            : null;
    }

}

/// <summary>
/// The quantity summary drawn into the model — Civil 3D's inserted cut/fill table, as a live annotation.
///
/// It is the only annotation that draws nothing of its own: every figure comes from what the rest of the
/// build measured, which is also why it has no sources row. Its blocker says so plainly, because a card
/// that renders an empty table is indistinguishable from one whose terrain holds nothing.
/// </summary>
internal sealed class ReportTableAnnotationDescriptor : AnnotationTypeDescriptor
{
    public override string Kind => "report-table";
    public override Type DefinitionType => typeof(ReportTableAnnotationDefinition);
    public override string TypeLabel => "Report Table";
    public override string MenuLabel => "Report Table";
    public override string IconLabel => "RT";
    public override string? IconName => "AnReportTable";
    public override int AccentArgb => unchecked((int)0xFF37474F);
    public override string Subtitle => "Measured quantities, drawn as a table";
    public override int SortOrder => 10;
    public override AnnotationDefinition Create() => new ReportTableAnnotationDefinition();

    public override IReadOnlyList<AnnotationParam> Parameters { get; } = new[]
    {
        AnnotationParam.Bool(
            "IncludeOverview", "Terrain",
            a => ((ReportTableAnnotationDefinition)a).IncludeOverview,
            (a, v) => ((ReportTableAnnotationDefinition)a).IncludeOverview = v,
            "Terrain name, the date the figures were measured, surface area and elevation range."),
        AnnotationParam.Bool(
            "IncludeZones", "Zone Schedule",
            a => ((ReportTableAnnotationDefinition)a).IncludeZones,
            (a, v) => ((ReportTableAnnotationDefinition)a).IncludeZones = v,
            "Area, levels, slope and earthwork per zone, with a totals row."),
        AnnotationParam.Bool(
            "IncludeEarthworks", "Earthworks",
            a => ((ReportTableAnnotationDefinition)a).IncludeEarthworks,
            (a, v) => ((ReportTableAnnotationDefinition)a).IncludeEarthworks = v,
            "Cut, fill and net volume from each Earthworks analysis on this terrain."),
        AnnotationParam.Bool(
            "IncludePonding", "Ponding",
            a => ((ReportTableAnnotationDefinition)a).IncludePonding,
            (a, v) => ((ReportTableAnnotationDefinition)a).IncludePonding = v,
            "Pond count, impounded volume, depth and water area from each Ponding analysis."),
        AnnotationParam.Bool(
            "IncludeCatchments", "Catchments",
            a => ((ReportTableAnnotationDefinition)a).IncludeCatchments,
            (a, v) => ((ReportTableAnnotationDefinition)a).IncludeCatchments = v,
            "Basin and closed-depression counts from each Catchments analysis."),
        AnnotationParameterCatalog.SlopeUnitChoice(
            "Unit the drawn slope columns are written in. This one belongs to the drawing, so it is " +
            "stored with the terrain — unlike the slope unit you type in, which is a per-user preference."),
        AnnotationParam.Bool(
            "ShowGridLines", "Rules",
            a => ((ReportTableAnnotationDefinition)a).ShowGridLines,
            (a, v) => ((ReportTableAnnotationDefinition)a).ShowGridLines = v,
            "Draw a rule under each heading row and below each table. Off leaves text only."),
        AnnotationParam.Number(
            "ColumnGap", "Column Gap",
            a => ((ReportTableAnnotationDefinition)a).ColumnGap,
            (a, v) => ((ReportTableAnnotationDefinition)a).ColumnGap = Math.Max(0.25, v),
            "Space between columns, as a multiple of the text height — so the table stays proportioned " +
            "when the annotation style is rescaled.",
            min: 0.25, decimalPlaces: 2),
        AnnotationParam.Number(
            "RowSpacing", "Row Spacing",
            a => ((ReportTableAnnotationDefinition)a).RowSpacing,
            (a, v) => ((ReportTableAnnotationDefinition)a).RowSpacing = Math.Max(1.0, v),
            "Line spacing, as a multiple of the text height.",
            min: 1.0, decimalPlaces: 2),
        AnnotationParam.Color(
            "ColorArgb", "Color",
            a => ((ReportTableAnnotationDefinition)a).ColorArgb,
            (a, v) => ((ReportTableAnnotationDefinition)a).ColorArgb = v,
            "Override colour for the table. Unset draws it in the Report Table layer's colour."),
    };

    public override string? DescribeBlocker(TerrainDefinition terrain, AnnotationDefinition annotation)
    {
        var table = (ReportTableAnnotationDefinition)annotation;
        bool anythingSelected = table.IncludeOverview || table.IncludeZones ||
            table.IncludeEarthworks || table.IncludePonding || table.IncludeCatchments;
        if (!anythingSelected)
            return "Nothing is selected to report — switch on at least one section below.";

        // A report draws what other cards measured. Saying which card is missing is more use than an
        // empty table, which reads as "this terrain has no quantities".
        bool hasSource = terrain.Zones.Count > 0 || terrain.Analyses.Count > 0;
        return hasSource
            ? null
            : "Nothing measures this terrain yet — add a zone, or an Earthworks, Ponding or Catchments analysis.";
    }

    public override string? DescribeBasis(TerrainDefinition terrain, AnnotationDefinition annotation) =>
        "Figures come from the last build, in model units. mhExportTerrainReport writes the same report as CSV.";
}
