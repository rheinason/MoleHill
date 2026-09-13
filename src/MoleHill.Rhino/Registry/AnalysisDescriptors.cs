using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using RhinoObjectType = Rhino.DocObjects.ObjectType;
using AnalysisParam = MoleHill.Rhino.Registry.ParameterDescriptor<MoleHill.Rhino.Model.AnalysisDefinition>;

namespace MoleHill.Rhino.Registry;

// One descriptor per concrete analysis type — content that evaluates the terrain, producing a
// measurement: a number, or a colour mapped onto the mesh. Grouped in a single file for brevity;
// reflection discovery in AnalysisTypeRegistry treats each class independently.
//
// Types that describe rather than evaluate live in AnnotationDescriptors.cs.

internal sealed class EarthworkAnalysisDescriptor : AnalysisTypeDescriptor
{
    public override string Kind => "earthwork";
    public override Type DefinitionType => typeof(EarthworkAnalysisDefinition);
    public override string TypeLabel => "Earthworks";
    public override string MenuLabel => "Earthworks";
    public override string IconLabel => "EW";
    public override string? IconName => "AnEarthwork";
    public override int AccentArgb => unchecked((int)0xFF8D6E63);
    public override string Subtitle => "Refs + summary";
    public override int SortOrder => 0;
    public override AnalysisDefinition Create() => new EarthworkAnalysisDefinition();

    public override IReadOnlyList<AnalysisParam> Parameters { get; } = new[]
    {
        AnalysisParam.Sources(
            "Reference", "Compare To",
            a => ((EarthworkAnalysisDefinition)a).Reference,
            RhinoObjectType.Mesh | RhinoObjectType.Brep | RhinoObjectType.Extrusion),
        AnalysisParam.Sources(
            "Boundary", "Boundary",
            a => ((EarthworkAnalysisDefinition)a).Boundary,
            RhinoObjectType.Curve),
    };

    public override string? DescribeBlocker(TerrainDefinition terrain, AnalysisDefinition analysis)
    {
        var earthwork = (EarthworkAnalysisDefinition)analysis;
        return earthwork.Reference.HasReferences || earthwork.ReferenceTerrainId.HasValue ||
               AnalysisPrerequisites.HasElevationChangingModifier(terrain)
            ? null
            : AnalysisPrerequisites.NothingToCompareMessage;
    }

    public override string? DescribeBasis(TerrainDefinition terrain, AnalysisDefinition analysis)
    {
        var earthwork = (EarthworkAnalysisDefinition)analysis;
        return AnalysisPrerequisites.DescribeBasis(earthwork.Reference.HasReferences || earthwork.ReferenceTerrainId.HasValue);
    }
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
    public override string Subtitle => "Slope preview";
    public override string? ActiveSubtitle => "Preview colors";
    public override int SortOrder => 1;
    public override AnalysisDefinition Create() => new SlopeAnalysisDefinition();

    public override IReadOnlyList<AnalysisParam> Parameters { get; } = new[]
    {
        AnalysisParam.ColorRamp(
            "Colours the terrain preview by slope. Drag the ramp's stops, the band interval, or the mapped range."),
    };
}

internal sealed class AspectAnalysisDescriptor : AnalysisTypeDescriptor
{
    public override string Kind => "aspect";
    public override Type DefinitionType => typeof(AspectAnalysisDefinition);
    public override string TypeLabel => "Aspect";
    public override string MenuLabel => "Aspect";
    public override string IconLabel => "N";
    public override int AccentArgb => unchecked((int)0xFF00897B);
    public override string Subtitle => "Which way it faces";
    public override string? ActiveSubtitle => "Preview colors";
    public override int SortOrder => 2;
    public override AnalysisDefinition Create() => new AspectAnalysisDefinition();

    public override IReadOnlyList<AnalysisParam> Parameters { get; } = new[]
    {
        AnalysisParam.ColorRamp(
            "Colours the terrain preview by the compass direction it drains towards, through a wheel that " +
            "closes on itself so north has no seam. The range is the full compass and cannot be narrowed."),
        AnalysisParam.Slope(
            "FlatSlopeThreshold", "Flat Below",
            a => ((AspectAnalysisDefinition)a).FlatSlopeThresholdDegrees,
            (a, v) => ((AspectAnalysisDefinition)a).FlatSlopeThresholdDegrees = v,
            "Ground flatter than this has no direction to report and is drawn neutral grey. Raise it to " +
            "stop survey noise on a level pad reading as a hillside."),
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
    public override string Subtitle => "Elevation preview";
    public override string? ActiveSubtitle => "Preview colors";
    public override int SortOrder => 3;
    public override AnalysisDefinition Create() => new ElevationAnalysisDefinition();

    public override IReadOnlyList<AnalysisParam> Parameters { get; } = new[]
    {
        AnalysisParam.ColorRamp(
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
    public override string Subtitle => "Signed delta preview";
    public override string? ActiveSubtitle => "Preview colors";
    public override int SortOrder => 4;
    public override AnalysisDefinition Create() => new CutFillAnalysisDefinition();

    public override IReadOnlyList<AnalysisParam> Parameters { get; } = new[]
    {
        AnalysisParam.Sources(
            "Reference", "Compare To",
            a => ((CutFillAnalysisDefinition)a).Reference,
            RhinoObjectType.Mesh | RhinoObjectType.Brep | RhinoObjectType.Extrusion),
        AnalysisParam.Sources(
            "Boundary", "Boundary",
            a => ((CutFillAnalysisDefinition)a).Boundary,
            RhinoObjectType.Curve),
        AnalysisParam.ColorRamp(
            "Colours the terrain preview by cut and fill depth. The range stays symmetric about zero, so unchanged ground sits mid-ramp."),
        AnalysisParam.Bool(
            "ShowBalanceLine", "Balance Line",
            a => ((CutFillAnalysisDefinition)a).ShowBalanceLine,
            (a, v) => ((CutFillAnalysisDefinition)a).ShowBalanceLine = v,
            "Draw the line where the delta crosses zero — where cut meets fill.",
            rebuildAfterCommit: true),
        AnalysisParam.Color(
            "BalanceLineColorArgb", "Balance Color",
            a => ((CutFillAnalysisDefinition)a).BalanceLineColorArgb,
            (a, v) => ((CutFillAnalysisDefinition)a).BalanceLineColorArgb = v,
            "Explicit display and bake colour for the balance line. Clear to take the colour from the layer its role routes to.",
            fallbackColor: (terrain, _) => AnalysisFormatting.ResolveLayerColorArgb(
                AnalysisFormatting.GetRoleLayerPath(terrain, LayerRole.BalanceLine)),
            defaultText: (terrain, _) => AnalysisFormatting.GetRoleColorText(terrain, LayerRole.BalanceLine),
            visibleWhen: a => ((CutFillAnalysisDefinition)a).ShowBalanceLine),
        AnalysisParam.Bool(
            "ShowDeltaContours", "Delta Contours",
            a => ((CutFillAnalysisDefinition)a).ShowDeltaContours,
            (a, v) => ((CutFillAnalysisDefinition)a).ShowDeltaContours = v,
            "Draw contours of the depth itself, so “cut deeper than 1 m” is a line rather than a shade.",
            rebuildAfterCommit: true),
        AnalysisParam.Number(
            "DeltaContourInterval", "Delta Interval",
            a => ((CutFillAnalysisDefinition)a).DeltaContourInterval,
            (a, v) => ((CutFillAnalysisDefinition)a).DeltaContourInterval = Math.Max(0.0, v),
            "Depth between delta contours. Levels step out from zero both ways, so 0.5 draws at ±0.5, ±1.0 and so on.",
            min: 0.0,
            unit: ParameterUnit.ModelLength,
            rebuildAfterCommit: true,
            visibleWhen: a => ((CutFillAnalysisDefinition)a).ShowDeltaContours),
        AnalysisParam.Color(
            "DeltaContourColorArgb", "Contour Color",
            a => ((CutFillAnalysisDefinition)a).DeltaContourColorArgb,
            (a, v) => ((CutFillAnalysisDefinition)a).DeltaContourColorArgb = v,
            "Explicit display and bake colour for the delta contours. Clear to take the colour from the layer its role routes to.",
            fallbackColor: (terrain, _) => AnalysisFormatting.ResolveLayerColorArgb(
                AnalysisFormatting.GetRoleLayerPath(terrain, LayerRole.CutFillContours)),
            defaultText: (terrain, _) => AnalysisFormatting.GetRoleColorText(terrain, LayerRole.CutFillContours),
            visibleWhen: a => ((CutFillAnalysisDefinition)a).ShowDeltaContours),
    };

    public override string? DescribeBlocker(TerrainDefinition terrain, AnalysisDefinition analysis)
    {
        // No reference is the normal case, not a missing input: cut/fill compares this terrain's initial
        // triangulation against the finished modifier stack, which is what "how much did my grading move"
        // means. It only has nothing to say when nothing has moved the ground.
        var cutFill = (CutFillAnalysisDefinition)analysis;
        return cutFill.Reference.HasReferences || cutFill.ReferenceTerrainId.HasValue ||
               AnalysisPrerequisites.HasElevationChangingModifier(terrain)
            ? null
            : AnalysisPrerequisites.NothingToCompareMessage;
    }

    public override string? DescribeBasis(TerrainDefinition terrain, AnalysisDefinition analysis)
    {
        var cutFill = (CutFillAnalysisDefinition)analysis;
        return AnalysisPrerequisites.DescribeBasis(cutFill.Reference.HasReferences || cutFill.ReferenceTerrainId.HasValue);
    }

}

internal sealed class WaterflowAnalysisDescriptor : AnalysisTypeDescriptor
{
    public override string Kind => "waterflow";
    public override Type DefinitionType => typeof(WaterflowAnalysisDefinition);
    public override string TypeLabel => "Waterflow from Points";
    public override string MenuLabel => "Waterflow from Points";
    public override string IconLabel => "WF";
    public override int AccentArgb => unchecked((int)0xFF1565C0);
    public override string Subtitle => "Downhill paths from point sources";
    public override int SortOrder => 5;
    public override AnalysisDefinition Create() => new WaterflowAnalysisDefinition();

    public override IReadOnlyList<AnalysisParam> Parameters { get; } = new[]
    {
        AnalysisParam.Sources(
            "Sources", "Points",
            a => ((WaterflowAnalysisDefinition)a).Sources,
            RhinoObjectType.Point,
            "Point objects or layers used as waterflow starts."),
        AnalysisParam.Number(
            "MaxLength", "Max Length",
            a => ((WaterflowAnalysisDefinition)a).MaxLength,
            (a, v) => ((WaterflowAnalysisDefinition)a).MaxLength = Math.Max(0.0, v),
            "Maximum plan length of each path. Set to 0 to continue to the terrain edge or a local sink.",
            min: 0.0),
        AnalysisParam.Color(
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

/// <summary>
/// Catchments: which ground drains to which outlet.
/// </summary>
/// <remarks>
/// No <c>ColorRamp</c> row, and that is the point rather than an omission. A ramp maps a measurement onto
/// a continuum, so neighbouring colours mean neighbouring values; a catchment index means nothing of the
/// sort, and stretching a ramp across the basins would give the picture a relationship the data does not
/// have. Basins are coloured categorically instead, from <c>CategoricalPalette</c>. Waterflow is already
/// a card with no ramp, so a card without one is the existing shape, not a new one.
/// </remarks>
internal sealed class CatchmentAnalysisDescriptor : AnalysisTypeDescriptor
{
    public override string Kind => "catchment";
    public override Type DefinitionType => typeof(CatchmentAnalysisDefinition);
    public override string TypeLabel => "Catchments";
    public override string MenuLabel => "Catchments";
    public override string IconLabel => "CA";
    public override int AccentArgb => unchecked((int)0xFF00897B);
    public override string Subtitle => "Where water drains to";
    public override string? ActiveSubtitle => "Preview colors";
    public override int SortOrder => 6;
    public override AnalysisDefinition Create() => new CatchmentAnalysisDefinition();

    public override IReadOnlyList<AnalysisParam> Parameters { get; } = new[]
    {
        AnalysisParam.Slope(
            "FlatSlopeThreshold", "Flat Below",
            a => ((CatchmentAnalysisDefinition)a).FlatSlopeThresholdDegrees,
            (a, v) => ((CatchmentAnalysisDefinition)a).FlatSlopeThresholdDegrees = v,
            "Ground flatter than this drains as one region rather than face by face. Raise it if a level " +
            "pad breaks up into slivers; lower it if two areas that drain differently are being merged.",
            rebuildAfterCommit: true),
        AnalysisParam.Number(
            "MinimumBasinArea", "Merge Below",
            a => ((CatchmentAnalysisDefinition)a).MinimumBasinAreaPercent,
            (a, v) => ((CatchmentAnalysisDefinition)a).MinimumBasinAreaPercent = Math.Clamp(v, 0.0, 100.0),
            "Catchments smaller than this share of the terrain are absorbed into the one they spill into. " +
            "A share rather than an area, so the same setting works on a plot and on a quarry. Zero keeps " +
            "every catchment, which on a survey means hundreds of slivers along the low edge.",
            min: 0.0,
            max: 100.0,
            decimalPlaces: 2,
            unit: ParameterUnit.Percent,
            rebuildAfterCommit: true),
        AnalysisParam.Bool(
            "ShowBoundaries", "Boundaries",
            a => ((CatchmentAnalysisDefinition)a).ShowBoundaries,
            (a, v) => ((CatchmentAnalysisDefinition)a).ShowBoundaries = v,
            "Draw each catchment's boundary as a closed polygon.",
            rebuildAfterCommit: true),
        AnalysisParam.Color(
            "BoundaryColorArgb", "Boundary Color",
            a => ((CatchmentAnalysisDefinition)a).BoundaryColorArgb,
            (a, v) => ((CatchmentAnalysisDefinition)a).BoundaryColorArgb = v,
            "Explicit display and bake colour for catchment boundaries. Clear to take the colour from the " +
            "layer its role routes to.",
            fallbackColor: (terrain, _) => AnalysisFormatting.ResolveLayerColorArgb(
                AnalysisFormatting.GetRoleLayerPath(terrain, LayerRole.Catchments)),
            defaultText: (terrain, _) => AnalysisFormatting.GetRoleColorText(terrain, LayerRole.Catchments),
            visibleWhen: a => ((CatchmentAnalysisDefinition)a).ShowBoundaries),
        AnalysisParam.Bool(
            "ShowFlowPaths", "Flow Paths",
            a => ((CatchmentAnalysisDefinition)a).ShowFlowPaths,
            (a, v) => ((CatchmentAnalysisDefinition)a).ShowFlowPaths = v,
            "Draw each catchment's longest flow path, from its high point down to its outlet.",
            rebuildAfterCommit: true),
        AnalysisParam.Color(
            "FlowPathColorArgb", "Path Color",
            a => ((CatchmentAnalysisDefinition)a).FlowPathColorArgb,
            (a, v) => ((CatchmentAnalysisDefinition)a).FlowPathColorArgb = v,
            "Explicit display and bake colour for catchment flow paths. Clear to take the colour from the " +
            "layer its role routes to.",
            fallbackColor: (terrain, _) => AnalysisFormatting.ResolveLayerColorArgb(
                AnalysisFormatting.GetRoleLayerPath(terrain, LayerRole.CatchmentFlowPaths)),
            defaultText: (terrain, _) => AnalysisFormatting.GetRoleColorText(terrain, LayerRole.CatchmentFlowPaths),
            visibleWhen: a => ((CatchmentAnalysisDefinition)a).ShowFlowPaths),
    };
}
