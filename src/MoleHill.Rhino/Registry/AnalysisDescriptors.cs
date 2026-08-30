using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using RhinoObjectType = Rhino.DocObjects.ObjectType;

namespace MoleHill.Rhino.Registry;

// One descriptor per concrete analysis/annotation type. Grouped in a single file for brevity; reflection
// discovery in AnalysisTypeRegistry treats each class independently. Metadata mirrors the former panel
// switches verbatim (kind/label/icon/accent/annotation/subtitle); discriminators are unchanged.
//
// Parameters cover every field the schema card builder can express; anything left out here (summaries,
// legends, the slope-unit-with-range-conversion editor, the insertion-origin picker) stays in the panel's
// AppendBespokeAnalysisRowsBefore/After hooks (MoleHillPanel.Analysis.cs).

internal static class AnalysisParameterCatalog
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
    public static AnalysisParameterDescriptor VerticalExaggeration() =>
        AnalysisParameterDescriptor.Number(
            "VerticalExaggeration", "Vertical Exaggeration",
            a => ((TerrainSectionAnalysisDefinitionBase)a).VerticalExaggeration,
            (a, v) => ((TerrainSectionAnalysisDefinitionBase)a).VerticalExaggeration = Math.Max(0.1, v),
            "Vertical scale relative to horizontal. 1 draws the section true to shape; 5 or 10 stretches " +
            "elevations so gentle ground reads. Affects the drawing only, never the terrain.",
            min: 0.1,
            decimalPlaces: 2);

    public static AnalysisParameterDescriptor SlopeUnitChoice(string help) =>
        AnalysisParameterDescriptor.Choice(
            "Unit", "Units", SlopeUnitOptions,
            a => AnalysisFormatting.GetSlopeUnitKey(GetUnit(a)),
            (a, v) => SetUnit(a, AnalysisFormatting.ParseSlopeUnit(v ?? "percent")),
            help);

    private static SlopeAnalyzer.SlopeUnit GetUnit(AnalysisDefinition a) => a switch
    {
        CurveSlopeLabelAnalysisDefinition d => d.Unit,
        PointSlopeLabelAnalysisDefinition d => d.Unit,
        SlopeArrowAnalysisDefinition d => d.Unit,
        _ => SlopeAnalyzer.SlopeUnit.Percent
    };

    private static void SetUnit(AnalysisDefinition a, SlopeAnalyzer.SlopeUnit unit)
    {
        switch (a)
        {
            case CurveSlopeLabelAnalysisDefinition d: d.Unit = unit; break;
            case PointSlopeLabelAnalysisDefinition d: d.Unit = unit; break;
            case SlopeArrowAnalysisDefinition d: d.Unit = unit; break;
        }
    }

    /// <summary>The shared tail of every block-attribute analysis card: value format, prefix/suffix,
    /// block scale, output layer, and color. Mirrors the former AddBlockAttributeAnalysisRows helper.</summary>
    public static IEnumerable<AnalysisParameterDescriptor> BlockAttributeTail<TAnalysis>(string formatHelp)
        where TAnalysis : BlockAttributeAnalysisDefinition
    {
        yield return AnalysisParameterDescriptor.Choice(
            "ValueFormat", "Decimals", null,
            a => ((TAnalysis)a).ValueFormat,
            (a, v) => ((TAnalysis)a).ValueFormat = v ?? "F1",
            formatHelp,
            optionsFor: a => AnalysisFormatting.GetValueFormatOptions(((TAnalysis)a).ValueFormat));
        yield return AnalysisParameterDescriptor.Text(
            "AttributePrefix", "Prefix",
            a => ((TAnalysis)a).AttributePrefix,
            (a, v) => ((TAnalysis)a).AttributePrefix = v ?? string.Empty,
            "Text prepended to the formatted value when filling the DISPLAY block attribute.",
            trim: false);
        yield return AnalysisParameterDescriptor.Text(
            "AttributeSuffix", "Suffix",
            a => ((TAnalysis)a).AttributeSuffix,
            (a, v) => ((TAnalysis)a).AttributeSuffix = v ?? string.Empty,
            "Text appended after the formatted value and unit when filling the DISPLAY block attribute.",
            trim: false);
        yield return AnalysisParameterDescriptor.Number(
            "BlockScale", "Block Scale",
            a => ((TAnalysis)a).BlockScale,
            (a, v) => ((TAnalysis)a).BlockScale = v,
            "Scale factor for inserted annotation blocks.",
            min: 0.01);
        yield return AnalysisParameterDescriptor.Color(
            "ColorArgb", "Color",
            a => ((TAnalysis)a).ColorArgb,
            (a, v) => ((TAnalysis)a).ColorArgb = v,
            "Explicit display and bake color for generated annotation blocks. Clear to take the colour from the layer its role routes to.",
            fallbackColor: (terrain, _) => AnalysisFormatting.ResolveLayerColorArgb(
                AnalysisFormatting.GetRoleLayerPath(terrain, LayerRole.Markers)),
            defaultText: (terrain, _) => AnalysisFormatting.GetRoleColorText(terrain, LayerRole.Markers));
    }
}

internal sealed class EarthworkAnalysisDescriptor : AnalysisTypeDescriptor
{
    public override string Kind => "earthwork";
    public override Type DefinitionType => typeof(EarthworkAnalysisDefinition);
    public override string TypeLabel => "Earthworks";
    public override string MenuLabel => "Earthworks";
    public override string IconLabel => "EW";
    public override string? IconName => "AnEarthwork";
    public override int AccentArgb => unchecked((int)0xFF8D6E63);
    public override bool IsAnnotation => false;
    public override string Subtitle => "Refs + summary";
    public override int SortOrder => 0;
    public override AnalysisDefinition Create() => new EarthworkAnalysisDefinition();

    public override IReadOnlyList<AnalysisParameterDescriptor> Parameters { get; } = new[]
    {
        AnalysisParameterDescriptor.Sources(
            "Reference", "Compare To",
            a => ((EarthworkAnalysisDefinition)a).Reference,
            RhinoObjectType.Mesh | RhinoObjectType.Brep | RhinoObjectType.Extrusion),
        AnalysisParameterDescriptor.Sources(
            "Boundary", "Boundary",
            a => ((EarthworkAnalysisDefinition)a).Boundary,
            RhinoObjectType.Curve),
    };
}

internal sealed class SlopeAnalysisDescriptor : AnalysisTypeDescriptor
{
    public override string Kind => "slope";
    public override Type DefinitionType => typeof(SlopeAnalysisDefinition);
    public override string TypeLabel => "Slope";
    public override string MenuLabel => "Slope";
    public override string IconLabel => "%";
    public override string? IconName => "AnSlope";
    public override int AccentArgb => unchecked((int)0xFF43A047);
    public override bool IsAnnotation => false;
    public override string Subtitle => "Slope preview";
    public override string? ActiveSubtitle => "Preview colors";
    public override int SortOrder => 1;
    public override AnalysisDefinition Create() => new SlopeAnalysisDefinition();

    public override IReadOnlyList<AnalysisParameterDescriptor> Parameters { get; } = new[]
    {
        AnalysisParameterDescriptor.ColorRamp(
            "Colours the terrain preview by slope. Drag the ramp's stops, the band interval, or the mapped range."),
    };
}

internal sealed class ElevationAnalysisDescriptor : AnalysisTypeDescriptor
{
    public override string Kind => "elevation";
    public override Type DefinitionType => typeof(ElevationAnalysisDefinition);
    public override string TypeLabel => "Elevation";
    public override string MenuLabel => "Elevation";
    public override string IconLabel => "Z";
    public override string? IconName => "AnElevation";
    public override int AccentArgb => unchecked((int)0xFF1E88E5);
    public override bool IsAnnotation => false;
    public override string Subtitle => "Elevation preview";
    public override string? ActiveSubtitle => "Preview colors";
    public override int SortOrder => 2;
    public override AnalysisDefinition Create() => new ElevationAnalysisDefinition();

    public override IReadOnlyList<AnalysisParameterDescriptor> Parameters { get; } = new[]
    {
        AnalysisParameterDescriptor.ColorRamp(
            "Colours the terrain preview by elevation. Drag the ramp's stops, the band interval, or the mapped range."),
    };
}

internal sealed class CutFillAnalysisDescriptor : AnalysisTypeDescriptor
{
    public override string Kind => "cut-fill";
    public override Type DefinitionType => typeof(CutFillAnalysisDefinition);
    public override string TypeLabel => "Cut / Fill";
    public override string MenuLabel => "Cut / Fill";
    public override string IconLabel => "+/-";
    public override string? IconName => "AnCutFill";
    public override int AccentArgb => unchecked((int)0xFFEF6C00);
    public override bool IsAnnotation => false;
    public override string Subtitle => "Signed delta preview";
    public override string? ActiveSubtitle => "Preview colors";
    public override int SortOrder => 3;
    public override AnalysisDefinition Create() => new CutFillAnalysisDefinition();

    public override IReadOnlyList<AnalysisParameterDescriptor> Parameters { get; } = new[]
    {
        AnalysisParameterDescriptor.Sources(
            "Reference", "Compare To",
            a => ((CutFillAnalysisDefinition)a).Reference,
            RhinoObjectType.Mesh | RhinoObjectType.Brep | RhinoObjectType.Extrusion),
        AnalysisParameterDescriptor.Sources(
            "Boundary", "Boundary",
            a => ((CutFillAnalysisDefinition)a).Boundary,
            RhinoObjectType.Curve),
        AnalysisParameterDescriptor.ColorRamp(
            "Colours the terrain preview by cut and fill depth. The range stays symmetric about zero, so unchanged ground sits mid-ramp."),
    };

    public override string? DescribeBlocker(TerrainDefinition terrain, AnalysisDefinition analysis)
    {
        // No reference is the normal case, not a missing input: cut/fill compares this terrain's initial
        // triangulation against the finished modifier stack, which is what "how much did my grading move"
        // means. It only has nothing to say when nothing has moved the ground.
        var cutFill = (CutFillAnalysisDefinition)analysis;
        return cutFill.Reference.HasReferences || AnalysisPrerequisites.HasElevationChangingModifier(terrain)
            ? null
            : AnalysisPrerequisites.NothingToCompareMessage;
    }

    public override string? DescribeBasis(TerrainDefinition terrain, AnalysisDefinition analysis) =>
        AnalysisPrerequisites.DescribeBasis(((CutFillAnalysisDefinition)analysis).Reference.HasReferences);

}

internal sealed class ContourAnalysisDescriptor : AnalysisTypeDescriptor
{
    public override string Kind => "contour";
    public override Type DefinitionType => typeof(ContourAnalysisDefinition);
    public override string TypeLabel => "Contours";
    public override string MenuLabel => "Contours";
    public override string IconLabel => "CT";
    public override string? IconName => "AnContour";
    public override int AccentArgb => unchecked((int)0xFF00796B);
    public override bool IsAnnotation => true;
    public override string Subtitle => "Contour lines at fixed intervals";
    public override int SortOrder => 0;
    public override AnalysisDefinition Create() => new ContourAnalysisDefinition();

    public override IReadOnlyList<AnalysisParameterDescriptor> Parameters { get; } = new[]
    {
        AnalysisParameterDescriptor.Number(
            "Interval", "Interval",
            a => ((ContourAnalysisDefinition)a).Interval,
            (a, v) => ((ContourAnalysisDefinition)a).Interval = Math.Max(double.Epsilon, v),
            "Vertical spacing between generated contour levels.",
            min: 0.0, incrementalCommit: true),
        AnalysisParameterDescriptor.Number(
            "StartZ", "Start Z",
            a => ((ContourAnalysisDefinition)a).StartZ,
            (a, v) => ((ContourAnalysisDefinition)a).StartZ = v,
            "Base elevation offset from which contour levels are stepped.",
            min: null, incrementalCommit: true),
        AnalysisParameterDescriptor.Number(
            "MajorEveryNth", "Major Every Nth",
            a => ((ContourAnalysisDefinition)a).MajorEveryNth,
            (a, v) => ((ContourAnalysisDefinition)a).MajorEveryNth = Math.Max(1, (int)Math.Round(v)),
            "Every Nth level is a major (index) contour. 5 is the usual survey convention; 1 makes every contour major.",
            min: 1, decimalPlaces: 0, incrementalCommit: true),
        AnalysisParameterDescriptor.Bool(
            "SeparateMajorMinorLayers", "Split Major / Minor",
            a => ((ContourAnalysisDefinition)a).SeparateMajorMinorLayers,
            (a, v) => ((ContourAnalysisDefinition)a).SeparateMajorMinorLayers = v,
            "Send major and minor contours to separate Contours::Major and Contours::Minor sublayers so print width and linetype are controlled per layer in Rhino.",
            incrementalCommit: true),
        AnalysisParameterDescriptor.Color(
            "ColorArgb", "Color",
            a => ((ContourAnalysisDefinition)a).ColorArgb,
            (a, v) => ((ContourAnalysisDefinition)a).ColorArgb = v,
            "Explicit display and bake color for generated contour curves. Clear to take the colour from the layer its role routes to.",
            fallbackColor: (terrain, _) => AnalysisFormatting.ResolveLayerColorArgb(
                AnalysisFormatting.GetRoleLayerPath(terrain, LayerRole.Contours)),
            defaultText: (terrain, _) => AnalysisFormatting.GetRoleColorText(terrain, LayerRole.Contours),
            incrementalCommit: true),
        AnalysisParameterDescriptor.Bool(
            "ShowLabels", "Label Contours",
            a => ((ContourAnalysisDefinition)a).ShowLabels,
            (a, v) => ((ContourAnalysisDefinition)a).ShowLabels = v,
            "Place elevation text along generated contour curves.",
            incrementalCommit: true),
        AnalysisParameterDescriptor.Number(
            "LabelInterval", "Label Interval",
            a => ((ContourAnalysisDefinition)a).LabelInterval,
            (a, v) => ((ContourAnalysisDefinition)a).LabelInterval = Math.Max(0.0, v),
            "Spacing between repeated labels along each contour. 0 places one label per contour curve.",
            min: 0.0, incrementalCommit: true),
        AnalysisParameterDescriptor.Number(
            "LabelEveryNth", "Label Every Nth",
            a => ((ContourAnalysisDefinition)a).LabelEveryNth,
            (a, v) => ((ContourAnalysisDefinition)a).LabelEveryNth = Math.Max(1, (int)Math.Round(v)),
            "Label only every Nth contour level (index contours). 1 labels every level.",
            min: 1, decimalPlaces: 0, incrementalCommit: true),
        AnalysisParameterDescriptor.Number(
            "LabelTextHeight", "Label Height",
            a => ((ContourAnalysisDefinition)a).LabelTextHeight,
            (a, v) => ((ContourAnalysisDefinition)a).LabelTextHeight = Math.Max(double.Epsilon, v),
            "Text height of contour labels in model units. Used only when this analysis does not follow the terrain annotation style.",
            min: 0.0, incrementalCommit: true),
        AnalysisParameterDescriptor.Choice(
            "LabelFormat", "Label Decimals", null,
            a => ((ContourAnalysisDefinition)a).LabelFormat,
            (a, v) => ((ContourAnalysisDefinition)a).LabelFormat = v ?? "F2",
            "Number of decimal places shown in contour elevation labels.",
            incrementalCommit: true,
            optionsFor: a => AnalysisFormatting.GetValueFormatOptions(((ContourAnalysisDefinition)a).LabelFormat)),
    };
}

internal sealed class WaterflowAnalysisDescriptor : AnalysisTypeDescriptor
{
    public override string Kind => "waterflow";
    public override Type DefinitionType => typeof(WaterflowAnalysisDefinition);
    public override string TypeLabel => "Waterflow from Points";
    public override string MenuLabel => "Waterflow from Points";
    public override string IconLabel => "WF";
    public override int AccentArgb => unchecked((int)0xFF1565C0);
    public override bool IsAnnotation => false;
    public override string Subtitle => "Downhill paths from point sources";
    public override int SortOrder => 4;
    public override AnalysisDefinition Create() => new WaterflowAnalysisDefinition();

    public override IReadOnlyList<AnalysisParameterDescriptor> Parameters { get; } = new[]
    {
        AnalysisParameterDescriptor.Sources(
            "Sources", "Points",
            a => ((WaterflowAnalysisDefinition)a).Sources,
            RhinoObjectType.Point,
            "Point objects or layers used as waterflow starts."),
        AnalysisParameterDescriptor.Number(
            "MaxLength", "Max Length",
            a => ((WaterflowAnalysisDefinition)a).MaxLength,
            (a, v) => ((WaterflowAnalysisDefinition)a).MaxLength = Math.Max(0.0, v),
            "Maximum plan length of each path. Set to 0 to continue to the terrain edge or a local sink.",
            min: 0.0),
        AnalysisParameterDescriptor.Color(
            "ColorArgb", "Color",
            a => ((WaterflowAnalysisDefinition)a).ColorArgb,
            (a, v) => ((WaterflowAnalysisDefinition)a).ColorArgb = v,
            "Explicit display and bake color for waterflow curves. Clear to take the colour from the layer its role routes to.",
            fallbackColor: (terrain, _) => AnalysisFormatting.ResolveLayerColorArgb(
                AnalysisFormatting.GetRoleLayerPath(terrain, LayerRole.Waterflow)),
            defaultText: (terrain, _) => AnalysisFormatting.GetRoleColorText(terrain, LayerRole.Waterflow)),
    };

    public override string? DescribeBlocker(TerrainDefinition terrain, AnalysisDefinition analysis)
    {
        var waterflow = (WaterflowAnalysisDefinition)analysis;
        return waterflow.Sources.HasReferences
            ? null
            : "Needs start points to trace from — set “Points” below.";
    }

}

internal sealed class CurveElevationLabelAnalysisDescriptor : AnalysisTypeDescriptor
{
    public override string Kind => "curve-elevation-label";
    public override Type DefinitionType => typeof(CurveElevationLabelAnalysisDefinition);
    public override string TypeLabel => "Spot Heights (Curve)";
    public override string MenuLabel => "Spot Heights (Curve)";
    public override string IconLabel => "CE";
    public override string? IconName => "AnCurveElevation";
    public override int AccentArgb => unchecked((int)0xFF1565C0);
    public override bool IsAnnotation => true;
    public override string Subtitle => "Elevation labels sampled along a curve";
    public override int SortOrder => 1;
    public override AnalysisDefinition Create() => new CurveElevationLabelAnalysisDefinition();

    public override IReadOnlyList<AnalysisParameterDescriptor> Parameters { get; } = new[]
    {
        AnalysisParameterDescriptor.Sources(
            "Sources", "Sources",
            a => ((CurveElevationLabelAnalysisDefinition)a).Sources,
            RhinoObjectType.Curve,
            "Curve objects or layers sampled along the terrain at regular stations for elevation labels."),
        AnalysisParameterDescriptor.Number(
            "Interval", "Interval",
            a => ((CurveElevationLabelAnalysisDefinition)a).Interval,
            (a, v) => ((CurveElevationLabelAnalysisDefinition)a).Interval = Math.Max(double.Epsilon, v),
            "Distance along each source curve between elevation sample stations.",
            min: 0.0),
    }.Concat(AnalysisParameterCatalog.BlockAttributeTail<CurveElevationLabelAnalysisDefinition>(
        "Number of decimal places shown in curve elevation labels.")).ToArray();
}

internal sealed class CurveSlopeLabelAnalysisDescriptor : AnalysisTypeDescriptor
{
    public override string Kind => "curve-slope-label";
    public override Type DefinitionType => typeof(CurveSlopeLabelAnalysisDefinition);
    public override string TypeLabel => "Spot Slope (Curve)";
    public override string MenuLabel => "Spot Slope (Curve)";
    public override string IconLabel => "C%";
    public override string? IconName => "AnCurveSlope";
    public override int AccentArgb => unchecked((int)0xFF2E7D32);
    public override bool IsAnnotation => true;
    public override string Subtitle => "Slope labels sampled along a curve";
    public override int SortOrder => 2;
    public override AnalysisDefinition Create() => new CurveSlopeLabelAnalysisDefinition();

    public override IReadOnlyList<AnalysisParameterDescriptor> Parameters { get; } = new[]
    {
        AnalysisParameterDescriptor.Sources(
            "Sources", "Sources",
            a => ((CurveSlopeLabelAnalysisDefinition)a).Sources,
            RhinoObjectType.Curve,
            "Curve objects or layers projected to the terrain before grade is sampled."),
        AnalysisParameterDescriptor.Number(
            "Interval", "Interval",
            a => ((CurveSlopeLabelAnalysisDefinition)a).Interval,
            (a, v) => ((CurveSlopeLabelAnalysisDefinition)a).Interval = Math.Max(double.Epsilon, v),
            "Distance along each source curve between sampled slope spans.",
            min: 0.0),
        AnalysisParameterCatalog.SlopeUnitChoice(
            "Show terrain-projected curve slope labels as percent, promille, ratio, or degrees."),
        AnalysisParameterDescriptor.Bool(
            "FlipDirection", "Flip Arrow",
            a => ((CurveSlopeLabelAnalysisDefinition)a).FlipDirection,
            (a, v) => ((CurveSlopeLabelAnalysisDefinition)a).FlipDirection = v,
            "Rotate slope arrows 180 degrees to match alternate office conventions."),
    }.Concat(AnalysisParameterCatalog.BlockAttributeTail<CurveSlopeLabelAnalysisDefinition>(
        "Number of decimal places shown in curve slope labels.")).ToArray();
}

internal sealed class ProjectedElevationLabelAnalysisDescriptor : AnalysisTypeDescriptor
{
    public override string Kind => "projected-elevation-label";
    public override Type DefinitionType => typeof(ProjectedElevationLabelAnalysisDefinition);
    public override string TypeLabel => "Spot Heights (Points)";
    public override string MenuLabel => "Spot Heights (Points)";
    public override string IconLabel => "PZ";
    public override string? IconName => "AnProjElevation";
    public override int AccentArgb => unchecked((int)0xFF1565C0);
    public override bool IsAnnotation => true;
    public override string Subtitle => "Elevation labels at picked points";
    public override int SortOrder => 3;
    public override AnalysisDefinition Create() => new ProjectedElevationLabelAnalysisDefinition();

    public override IReadOnlyList<AnalysisParameterDescriptor> Parameters { get; } = new[]
    {
        AnalysisParameterDescriptor.Sources(
            "Sources", "Sources",
            a => ((ProjectedElevationLabelAnalysisDefinition)a).Sources,
            RhinoObjectType.Point | RhinoObjectType.Curve,
            "Point objects and curve edit points projected to the terrain for elevation labels."),
    }.Concat(AnalysisParameterCatalog.BlockAttributeTail<ProjectedElevationLabelAnalysisDefinition>(
        "Number of decimal places shown in projected elevation labels.")).ToArray();
}

internal sealed class PointSlopeLabelAnalysisDescriptor : AnalysisTypeDescriptor
{
    public override string Kind => "point-slope-label";
    public override Type DefinitionType => typeof(PointSlopeLabelAnalysisDefinition);
    public override string TypeLabel => "Spot Slope (Points)";
    public override string MenuLabel => "Spot Slope (Points)";
    public override string IconLabel => "P%";
    public override string? IconName => "AnPointSlope";
    public override int AccentArgb => unchecked((int)0xFF0288D1);
    public override bool IsAnnotation => true;
    public override string Subtitle => "Slope labels at picked points";
    public override int SortOrder => 4;
    public override AnalysisDefinition Create() => new PointSlopeLabelAnalysisDefinition();

    public override IReadOnlyList<AnalysisParameterDescriptor> Parameters { get; } = new[]
    {
        AnalysisParameterDescriptor.Sources(
            "Sources", "Sources",
            a => ((PointSlopeLabelAnalysisDefinition)a).Sources,
            RhinoObjectType.Point,
            "Point objects or layers projected to the terrain before local slope is sampled."),
        AnalysisParameterCatalog.SlopeUnitChoice(
            "Show terrain slope labels as percent, promille, ratio, or degrees."),
        AnalysisParameterDescriptor.Bool(
            "FlipDirection", "Flip Arrow",
            a => ((PointSlopeLabelAnalysisDefinition)a).FlipDirection,
            (a, v) => ((PointSlopeLabelAnalysisDefinition)a).FlipDirection = v,
            "Rotate slope arrows 180 degrees to match alternate office conventions."),
    }.Concat(AnalysisParameterCatalog.BlockAttributeTail<PointSlopeLabelAnalysisDefinition>(
        "Number of decimal places shown in point slope labels.")).ToArray();
}

internal sealed class SlopeArrowAnalysisDescriptor : AnalysisTypeDescriptor
{
    public override string Kind => "slope-arrows";
    public override Type DefinitionType => typeof(SlopeArrowAnalysisDefinition);
    public override string TypeLabel => "Flow Arrows";
    public override string MenuLabel => "Flow Arrows";
    public override string IconLabel => "FA";
    public override string? IconName => "AnFlowArrows";
    public override int AccentArgb => unchecked((int)0xFF0288D1);
    public override bool IsAnnotation => true;
    public override string Subtitle => "Downhill arrows on a grid";
    public override int SortOrder => 5;
    public override AnalysisDefinition Create() => new SlopeArrowAnalysisDefinition();

    public override IReadOnlyList<AnalysisParameterDescriptor> Parameters { get; } = new[]
    {
        AnalysisParameterDescriptor.Sources(
            "Sources", "Sources",
            a => ((SlopeArrowAnalysisDefinition)a).Sources,
            RhinoObjectType.Curve,
            "Optional closed boundary curves limiting where flow arrows are placed. Leave empty to cover the whole terrain."),
        AnalysisParameterDescriptor.Number(
            "GridSpacing", "Grid Spacing",
            a => ((SlopeArrowAnalysisDefinition)a).GridSpacing,
            (a, v) => ((SlopeArrowAnalysisDefinition)a).GridSpacing = Math.Max(double.Epsilon, v),
            "Spacing of the sampling grid across the terrain. Smaller spacing = more arrows.",
            min: 0.0),
        AnalysisParameterCatalog.SlopeUnitChoice(
            "Show flow-arrow slope labels as percent, promille, ratio, or degrees."),
        AnalysisParameterDescriptor.Bool(
            "FlipDirection", "Flip Arrow",
            a => ((SlopeArrowAnalysisDefinition)a).FlipDirection,
            (a, v) => ((SlopeArrowAnalysisDefinition)a).FlipDirection = v,
            "Rotate arrows 180 degrees (point uphill instead of downhill)."),
    }.Concat(AnalysisParameterCatalog.BlockAttributeTail<SlopeArrowAnalysisDefinition>(
        "Number of decimal places shown on flow-arrow slope labels.")).ToArray();
}

internal sealed class GradeBetweenPointsAnalysisDescriptor : AnalysisTypeDescriptor
{
    public override string Kind => "grade-callout";
    public override Type DefinitionType => typeof(GradeBetweenPointsAnalysisDefinition);
    public override string TypeLabel => "Grade Callout";
    public override string MenuLabel => "Grade Callout";
    public override string IconLabel => "1:n";
    public override string? IconName => "AnGradeCallout";
    public override int AccentArgb => unchecked((int)0xFF00838F);
    public override bool IsAnnotation => true;
    public override string Subtitle => "Grade between two points (1:n + %)";
    public override int SortOrder => 6;
    public override AnalysisDefinition Create() => new GradeBetweenPointsAnalysisDefinition();

    public override IReadOnlyList<AnalysisParameterDescriptor> Parameters { get; } = new[]
    {
        AnalysisParameterDescriptor.Sources(
            "Sources", "Sources",
            a => ((GradeBetweenPointsAnalysisDefinition)a).Sources,
            RhinoObjectType.Curve,
            "Lines whose two endpoints define the grade. Each line emits one callout."),
        AnalysisParameterDescriptor.Choice(
            "ValueFormat", "Decimals", null,
            a => ((GradeBetweenPointsAnalysisDefinition)a).ValueFormat,
            (a, v) => ((GradeBetweenPointsAnalysisDefinition)a).ValueFormat = v ?? "F1",
            "Number of decimal places shown on the percentage part of the callout.",
            optionsFor: a => AnalysisFormatting.GetValueFormatOptions(((GradeBetweenPointsAnalysisDefinition)a).ValueFormat)),
        AnalysisParameterDescriptor.Text(
            "AttributePrefix", "Prefix",
            a => ((GradeBetweenPointsAnalysisDefinition)a).AttributePrefix,
            (a, v) => ((GradeBetweenPointsAnalysisDefinition)a).AttributePrefix = v ?? string.Empty,
            "Text prepended to the grade callout.",
            trim: false),
        AnalysisParameterDescriptor.Text(
            "AttributeSuffix", "Suffix",
            a => ((GradeBetweenPointsAnalysisDefinition)a).AttributeSuffix,
            (a, v) => ((GradeBetweenPointsAnalysisDefinition)a).AttributeSuffix = v ?? string.Empty,
            "Text appended to the grade callout.",
            trim: false),
        AnalysisParameterDescriptor.Number(
            "TextHeight", "Text Height",
            a => ((GradeBetweenPointsAnalysisDefinition)a).TextHeight,
            (a, v) => ((GradeBetweenPointsAnalysisDefinition)a).TextHeight = Math.Max(double.Epsilon, v),
            "Text height of the callout label, and the size of the downhill arrow.",
            min: 0.0),
        AnalysisParameterDescriptor.Color(
            "ColorArgb", "Color",
            a => ((GradeBetweenPointsAnalysisDefinition)a).ColorArgb,
            (a, v) => ((GradeBetweenPointsAnalysisDefinition)a).ColorArgb = v,
            "Explicit display and bake color for the callout. Clear to take the colour from the layer its role routes to.",
            fallbackColor: (terrain, _) => AnalysisFormatting.ResolveLayerColorArgb(
                AnalysisFormatting.GetRoleLayerPath(terrain, LayerRole.Labels)),
            defaultText: (terrain, _) => AnalysisFormatting.GetRoleColorText(terrain, LayerRole.Labels)),
    };
}

internal sealed class TerrainSectionAnalysisDescriptor : AnalysisTypeDescriptor
{
    public override string Kind => "terrain-section";
    public override Type DefinitionType => typeof(TerrainSectionAnalysisDefinition);
    public override string TypeLabel => "Section Cut";
    public override string MenuLabel => "Section Cut";
    public override string IconLabel => "TS";
    public override string? IconName => "AnTerrainSection";
    public override int AccentArgb => unchecked((int)0xFF7B1FA2);
    public override bool IsAnnotation => true;
    public override string Subtitle => "Profile at the cut line";
    public override int SortOrder => 7;
    public override AnalysisDefinition Create() => new TerrainSectionAnalysisDefinition();

    public override IReadOnlyList<AnalysisParameterDescriptor> Parameters { get; } = new[]
    {
        AnalysisParameterCatalog.VerticalExaggeration(),
        AnalysisParameterDescriptor.Number(
            "StationTickInterval", "Station Tick Interval",
            a => ((TerrainSectionAnalysisDefinition)a).StationTickInterval,
            (a, v) => ((TerrainSectionAnalysisDefinition)a).StationTickInterval = Math.Max(0.0, v),
            "Spacing between station tick marks along the profile baseline. 0 disables ticks.",
            min: 0.0),
        AnalysisParameterDescriptor.Number(
            "ElevationGridInterval", "Elevation Grid Interval",
            a => ((TerrainSectionAnalysisDefinition)a).ElevationGridInterval,
            (a, v) => ((TerrainSectionAnalysisDefinition)a).ElevationGridInterval = Math.Max(0.0, v),
            "Vertical spacing of horizontal grid lines drawn on the section. 0 = auto.",
            min: 0.0),
        AnalysisParameterDescriptor.Bool(
            "ShowStationTicks", "Show Station Ticks",
            a => ((TerrainSectionAnalysisDefinition)a).ShowStationTicks,
            (a, v) => ((TerrainSectionAnalysisDefinition)a).ShowStationTicks = v,
            "Draw tick marks at each station along the profile baseline."),
        AnalysisParameterDescriptor.Bool(
            "ShowElevationGrid", "Show Elevation Grid",
            a => ((TerrainSectionAnalysisDefinition)a).ShowElevationGrid,
            (a, v) => ((TerrainSectionAnalysisDefinition)a).ShowElevationGrid = v,
            "Draw horizontal grid lines at each elevation increment."),
        AnalysisParameterDescriptor.Bool(
            "ShowStationLabels", "Show Station Labels",
            a => ((TerrainSectionAnalysisDefinition)a).ShowStationLabels,
            (a, v) => ((TerrainSectionAnalysisDefinition)a).ShowStationLabels = v,
            "Print station distance text below each tick."),
    };

    public override string? DescribeBlocker(TerrainDefinition terrain, AnalysisDefinition analysis)
    {
        var section = (TerrainSectionAnalysisDefinitionBase)analysis;
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

    public override string? DescribeBasis(TerrainDefinition terrain, AnalysisDefinition analysis)
    {
        var section = (TerrainSectionAnalysisDefinitionBase)analysis;
        return section.ShowCutFillRegions
            ? AnalysisPrerequisites.DescribeBasis(section.HasCutFillReference)
            : null;
    }

}

internal sealed class CrossSectionStationAnalysisDescriptor : AnalysisTypeDescriptor
{
    public override string Kind => "cross-section-station";
    public override Type DefinitionType => typeof(CrossSectionStationAnalysisDefinition);
    public override string TypeLabel => "Cross-Sections";
    public override string MenuLabel => "Cross-Sections";
    public override string IconLabel => "XS";
    public override string? IconName => "AnCrossSection";
    public override int AccentArgb => unchecked((int)0xFF8E24AA);
    public override bool IsAnnotation => true;
    public override string Subtitle => "Unrolled cuts at stations, in a grid";
    public override int SortOrder => 8;
    public override AnalysisDefinition Create() => new CrossSectionStationAnalysisDefinition();

    public override IReadOnlyList<AnalysisParameterDescriptor> Parameters { get; } = new[]
    {
        AnalysisParameterDescriptor.Number(
            "StationInterval", "Station Interval",
            a => ((CrossSectionStationAnalysisDefinition)a).StationInterval,
            (a, v) => ((CrossSectionStationAnalysisDefinition)a).StationInterval = Math.Max(double.Epsilon, v),
            "Distance between cross-section stations along the alignment.",
            min: 0.0),
        AnalysisParameterDescriptor.Number(
            "CrossSectionWidth", "Cross-Section Width",
            a => ((CrossSectionStationAnalysisDefinition)a).CrossSectionWidth,
            (a, v) => ((CrossSectionStationAnalysisDefinition)a).CrossSectionWidth = Math.Max(double.Epsilon, v),
            "Total perpendicular width of each cross-section cut, centered on the alignment.",
            min: 0.0),
        AnalysisParameterCatalog.VerticalExaggeration(),
        AnalysisParameterDescriptor.Number(
            "GridColumns", "Grid Columns",
            a => ((CrossSectionStationAnalysisDefinition)a).GridColumns,
            (a, v) => ((CrossSectionStationAnalysisDefinition)a).GridColumns = Math.Max(1, (int)Math.Round(v)),
            "Number of columns in the unrolled cross-section grid layout.",
            min: 1, decimalPlaces: 0),
        AnalysisParameterDescriptor.Number(
            "GridCellWidth", "Grid Cell Width",
            a => ((CrossSectionStationAnalysisDefinition)a).GridCellWidth,
            (a, v) => ((CrossSectionStationAnalysisDefinition)a).GridCellWidth = Math.Max(0.0, v),
            "Override cell width for the grid layout. 0 = auto.",
            min: 0.0),
        AnalysisParameterDescriptor.Number(
            "GridCellHeight", "Grid Cell Height",
            a => ((CrossSectionStationAnalysisDefinition)a).GridCellHeight,
            (a, v) => ((CrossSectionStationAnalysisDefinition)a).GridCellHeight = Math.Max(0.0, v),
            "Override cell height for the grid layout. 0 = auto.",
            min: 0.0),
        AnalysisParameterDescriptor.Bool(
            "ShowCutLinesOnTerrain", "Cut Lines on Terrain",
            a => ((CrossSectionStationAnalysisDefinition)a).ShowCutLinesOnTerrain,
            (a, v) => ((CrossSectionStationAnalysisDefinition)a).ShowCutLinesOnTerrain = v,
            "Draw the perpendicular cut polylines on the terrain at each station."),
        AnalysisParameterDescriptor.Bool(
            "LabelStations", "Label Stations",
            a => ((CrossSectionStationAnalysisDefinition)a).LabelStations,
            (a, v) => ((CrossSectionStationAnalysisDefinition)a).LabelStations = v,
            "Print station distance text on each unrolled cross-section."),
        AnalysisParameterDescriptor.Bool(
            "ShowElevationGrid", "Show Elevation Grid",
            a => ((CrossSectionStationAnalysisDefinition)a).ShowElevationGrid,
            (a, v) => ((CrossSectionStationAnalysisDefinition)a).ShowElevationGrid = v,
            "Draw horizontal grid lines on each unrolled cross-section."),
        AnalysisParameterDescriptor.Number(
            "ElevationGridInterval", "Elevation Grid Interval",
            a => ((CrossSectionStationAnalysisDefinition)a).ElevationGridInterval,
            (a, v) => ((CrossSectionStationAnalysisDefinition)a).ElevationGridInterval = Math.Max(0.0, v),
            "Vertical spacing of grid lines on the unrolled cross-sections. 0 = auto.",
            min: 0.0),
    };

    public override string? DescribeBlocker(TerrainDefinition terrain, AnalysisDefinition analysis)
    {
        var section = (TerrainSectionAnalysisDefinitionBase)analysis;
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

    public override string? DescribeBasis(TerrainDefinition terrain, AnalysisDefinition analysis)
    {
        var section = (TerrainSectionAnalysisDefinitionBase)analysis;
        return section.ShowCutFillRegions
            ? AnalysisPrerequisites.DescribeBasis(section.HasCutFillReference)
            : null;
    }

}

internal sealed class LongitudinalSectionAnalysisDescriptor : AnalysisTypeDescriptor
{
    public override string Kind => "longitudinal-section";
    public override Type DefinitionType => typeof(LongitudinalSectionAnalysisDefinition);
    public override string TypeLabel => "Section Along Curve";
    public override string MenuLabel => "Section Along Curve";
    public override string IconLabel => "LS";
    public override string? IconName => "AnLongSection";
    public override int AccentArgb => unchecked((int)0xFF5E35B1);
    public override bool IsAnnotation => true;
    public override string Subtitle => "Unrolled profile that follows a curve";
    public override int SortOrder => 9;
    public override AnalysisDefinition Create() => new LongitudinalSectionAnalysisDefinition();

    public override IReadOnlyList<AnalysisParameterDescriptor> Parameters { get; } = new[]
    {
        AnalysisParameterDescriptor.Number(
            "SampleInterval", "Sample Interval",
            a => ((LongitudinalSectionAnalysisDefinition)a).SampleInterval,
            (a, v) => ((LongitudinalSectionAnalysisDefinition)a).SampleInterval = Math.Max(double.Epsilon, v),
            "Distance between elevation samples along the curve.",
            min: 0.0),
        AnalysisParameterCatalog.VerticalExaggeration(),
        AnalysisParameterDescriptor.Bool(
            "ShowBaseline", "Show Baseline",
            a => ((LongitudinalSectionAnalysisDefinition)a).ShowBaseline,
            (a, v) => ((LongitudinalSectionAnalysisDefinition)a).ShowBaseline = v,
            "Draw the horizontal baseline (zero elevation reference) under the profile."),
        AnalysisParameterDescriptor.Bool(
            "ShowElevationGrid", "Show Elevation Grid",
            a => ((LongitudinalSectionAnalysisDefinition)a).ShowElevationGrid,
            (a, v) => ((LongitudinalSectionAnalysisDefinition)a).ShowElevationGrid = v,
            "Draw horizontal grid lines at each elevation increment."),
        AnalysisParameterDescriptor.Number(
            "ElevationGridInterval", "Elevation Grid Interval",
            a => ((LongitudinalSectionAnalysisDefinition)a).ElevationGridInterval,
            (a, v) => ((LongitudinalSectionAnalysisDefinition)a).ElevationGridInterval = Math.Max(0.0, v),
            "Vertical spacing of horizontal grid lines. 0 = auto.",
            min: 0.0),
        AnalysisParameterDescriptor.Bool(
            "ShowStationLabels", "Show Station Labels",
            a => ((LongitudinalSectionAnalysisDefinition)a).ShowStationLabels,
            (a, v) => ((LongitudinalSectionAnalysisDefinition)a).ShowStationLabels = v,
            "Print station distance text along the baseline."),
        AnalysisParameterDescriptor.Number(
            "StationLabelInterval", "Station Label Interval",
            a => ((LongitudinalSectionAnalysisDefinition)a).StationLabelInterval,
            (a, v) => ((LongitudinalSectionAnalysisDefinition)a).StationLabelInterval = Math.Max(0.0, v),
            "Spacing between station labels. 0 = auto (~quarter of total length).",
            min: 0.0),
    };

    public override string? DescribeBlocker(TerrainDefinition terrain, AnalysisDefinition analysis)
    {
        var section = (TerrainSectionAnalysisDefinitionBase)analysis;
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

    public override string? DescribeBasis(TerrainDefinition terrain, AnalysisDefinition analysis)
    {
        var section = (TerrainSectionAnalysisDefinitionBase)analysis;
        return section.ShowCutFillRegions
            ? AnalysisPrerequisites.DescribeBasis(section.HasCutFillReference)
            : null;
    }

}
