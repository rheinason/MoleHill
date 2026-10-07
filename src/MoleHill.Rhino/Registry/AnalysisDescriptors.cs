using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using AnalysisParam = MoleHill.Rhino.Registry.ParameterDescriptor<MoleHill.Rhino.Model.AnalysisDefinition>;
using RhinoObjectType = Rhino.DocObjects.ObjectType;

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

    public override TerrainAnalysisSummary? Build(AnalysisBuildContext c, AnalysisDefinition analysis) =>
        TerrainBuildService.BuildEarthworkSummary(c.Snapshot, c.FallbackBaseMesh, c.CurrentMesh, c.Vertices, c.Faces, (EarthworkAnalysisDefinition)analysis, c.SurfaceArea, c.ElevationMinZ, c.ElevationMaxZ, c.Build, c.ReferenceComparisonCache, c.ReferenceProjectionCache, c.ShouldCancel);

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

    public override TerrainAnalysisSummary? Build(AnalysisBuildContext c, AnalysisDefinition analysis) =>
        TerrainBuildService.BuildSlopeSummary(c.Vertices, c.VertexCount, c.Faces, c.FaceCount, (SlopeAnalysisDefinition)analysis, c.SurfaceArea);

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

    public override TerrainAnalysisSummary? Build(AnalysisBuildContext c, AnalysisDefinition analysis) =>
        TerrainBuildService.BuildAspectSummary(c.Snapshot, c.Vertices, c.Faces, (AspectAnalysisDefinition)analysis, c.SurfaceArea);

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

    public override TerrainAnalysisSummary? Build(AnalysisBuildContext c, AnalysisDefinition analysis) =>
        new TerrainAnalysisSummary
        {
            AnalysisId = analysis.Id,
            SurfaceArea = c.SurfaceArea,
            ElevationMinZ = c.ElevationMinZ,
            ElevationMaxZ = c.ElevationMaxZ
        };

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

    public override TerrainAnalysisSummary? Build(AnalysisBuildContext c, AnalysisDefinition analysis) =>
        TerrainBuildService.BuildCutFillSummary(c.Snapshot, c.FallbackBaseMesh, c.CurrentMesh, c.Vertices, c.Faces, (CutFillAnalysisDefinition)analysis, c.SurfaceArea, c.ElevationMinZ, c.ElevationMaxZ, c.Build, c.ReferenceComparisonCache, c.ReferenceProjectionCache, c.ShouldCancel);

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

    public override TerrainAnalysisSummary? Build(AnalysisBuildContext c, AnalysisDefinition analysis) =>
        TerrainBuildService.BuildWaterflowSummary(c.Snapshot, c.Vertices, c.VertexCount, c.Faces, c.FaceCount, (WaterflowAnalysisDefinition)analysis, c.Build, c.ShouldCancel);

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

    public override TerrainAnalysisSummary? Build(AnalysisBuildContext c, AnalysisDefinition analysis) =>
        TerrainBuildService.BuildCatchmentSummary(c.Snapshot, c.Vertices, c.Faces, (CatchmentAnalysisDefinition)analysis, c.Build, c.BasinGraphCache, c.ShouldCancel);

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

/// <summary>
/// Ponding: the depressions the terrain holds water in, measured.
/// </summary>
/// <remarks>
/// This one carries a ramp, where its sibling <see cref="CatchmentAnalysisDescriptor"/> deliberately does
/// not. The difference is not arbitrary: ponded depth is a *measurement* on a continuum, so near values
/// should read as near colours and a legend naming the ends means something — everything a catchment
/// index is not.
/// </remarks>
internal sealed class PondingAnalysisDescriptor : AnalysisTypeDescriptor
{
    public override string Kind => "ponding";
    public override Type DefinitionType => typeof(PondingAnalysisDefinition);
    public override string TypeLabel => "Ponding";
    public override string MenuLabel => "Ponding";
    public override string IconLabel => "PD";
    public override int AccentArgb => unchecked((int)0xFFD32F2F);
    public override string Subtitle => "Where water stands";
    public override string? ActiveSubtitle => "Preview colors";
    public override int SortOrder => 7;
    public override AnalysisDefinition Create() => new PondingAnalysisDefinition();

    public override TerrainAnalysisSummary? Build(AnalysisBuildContext c, AnalysisDefinition analysis) =>
        TerrainBuildService.BuildPondingSummary(c.Snapshot, c.Vertices, c.Faces, (PondingAnalysisDefinition)analysis, c.Build, c.BasinGraphCache, c.ShouldCancel);

    public override IReadOnlyList<AnalysisParam> Parameters { get; } = new[]
    {
        AnalysisParam.ColorRamp(
            "Colours the terrain preview by how deep the water stands. Ground that drains is left alone."),
        AnalysisParam.Number(
            "MinimumDepth", "Ignore Below",
            a => ((PondingAnalysisDefinition)a).MinimumDepth,
            (a, v) => ((PondingAnalysisDefinition)a).MinimumDepth = Math.Max(0.0, v),
            "Depressions shallower than this are not reported. A survey-derived surface always has a few " +
            "millimetres of dimple in it, and reporting those as ponds is how a useful check becomes one " +
            "nobody looks at.",
            min: 0.0,
            unit: ParameterUnit.ModelLength,
            rebuildAfterCommit: true),
        AnalysisParam.Slope(
            "FlatSlopeThreshold", "Flat Below",
            a => ((PondingAnalysisDefinition)a).FlatSlopeThresholdDegrees,
            (a, v) => ((PondingAnalysisDefinition)a).FlatSlopeThresholdDegrees = v,
            "Ground flatter than this drains as one region rather than face by face. Shared with the " +
            "Catchments card — set them alike and the terrain is only routed once.",
            rebuildAfterCommit: true),
        AnalysisParam.Bool(
            "ShowOutlines", "Shorelines",
            a => ((PondingAnalysisDefinition)a).ShowOutlines,
            (a, v) => ((PondingAnalysisDefinition)a).ShowOutlines = v,
            "Draw each pond's edge at the level it overflows at.",
            rebuildAfterCommit: true),
        AnalysisParam.Color(
            "OutlineColorArgb", "Shoreline Color",
            a => ((PondingAnalysisDefinition)a).OutlineColorArgb,
            (a, v) => ((PondingAnalysisDefinition)a).OutlineColorArgb = v,
            "Explicit display and bake colour for shorelines. Clear to take the colour from the layer its " +
            "role routes to.",
            fallbackColor: (terrain, _) => AnalysisFormatting.ResolveLayerColorArgb(
                AnalysisFormatting.GetRoleLayerPath(terrain, LayerRole.Ponding)),
            defaultText: (terrain, _) => AnalysisFormatting.GetRoleColorText(terrain, LayerRole.Ponding),
            visibleWhen: a => ((PondingAnalysisDefinition)a).ShowOutlines),
        AnalysisParam.Bool(
            "ShowSpillPoints", "Spill Points",
            a => ((PondingAnalysisDefinition)a).ShowSpillPoints,
            (a, v) => ((PondingAnalysisDefinition)a).ShowSpillPoints = v,
            "Mark where each pond overflows. That is where to cut a channel, so it is usually the first " +
            "thing wanted after finding the pond.",
            rebuildAfterCommit: true),
        AnalysisParam.Color(
            "SpillPointColorArgb", "Spill Color",
            a => ((PondingAnalysisDefinition)a).SpillPointColorArgb,
            (a, v) => ((PondingAnalysisDefinition)a).SpillPointColorArgb = v,
            "Explicit display and bake colour for spill markers. Clear to take the colour from the layer " +
            "its role routes to.",
            fallbackColor: (terrain, _) => AnalysisFormatting.ResolveLayerColorArgb(
                AnalysisFormatting.GetRoleLayerPath(terrain, LayerRole.PondingSpillPoints)),
            defaultText: (terrain, _) => AnalysisFormatting.GetRoleColorText(terrain, LayerRole.PondingSpillPoints),
            visibleWhen: a => ((PondingAnalysisDefinition)a).ShowSpillPoints),
    };
}

/// <summary>
/// Gradient compliance: accessibility gradient rules checked against a standard the project sets.
/// </summary>
/// <remarks>
/// No <c>ColorRamp</c> row, for the reason Catchments has none: the colours are verdicts (pass, reported,
/// failed), not positions on a continuum. The standard is a dropdown of presets that copies its limits
/// into the card, and every limit stays editable below it; see <see cref="GradientRuleSet"/>.
/// </remarks>
internal sealed class GradientComplianceAnalysisDescriptor : AnalysisTypeDescriptor
{
    public override string Kind => "gradient-compliance";
    public override Type DefinitionType => typeof(GradientComplianceAnalysisDefinition);
    public override string TypeLabel => "Gradient Compliance";
    public override string MenuLabel => "Gradient Compliance";
    public override string IconLabel => "GC";
    public override int AccentArgb => unchecked((int)0xFF6A1B9A);
    public override string Subtitle => "Accessibility gradient rules";
    public override string? ActiveSubtitle => "Preview colors";
    public override int SortOrder => 8;
    public override AnalysisDefinition Create() => new GradientComplianceAnalysisDefinition();

    public override TerrainAnalysisSummary? Build(AnalysisBuildContext c, AnalysisDefinition analysis) =>
        TerrainBuildService.BuildGradientComplianceSummary(c.Snapshot, c.Vertices, c.VertexCount, c.Faces, c.FaceCount, (GradientComplianceAnalysisDefinition)analysis, c.ShouldCancel);

    private static GradientRuleSet Rules(AnalysisDefinition analysis) =>
        ((GradientComplianceAnalysisDefinition)analysis).Rules;

    private static readonly IReadOnlyList<(string Key, string Label)> RuleModeOptions = new[]
    {
        (nameof(GradientRuleMode.Off), "Off"),
        (nameof(GradientRuleMode.Report), "Report"),
        (nameof(GradientRuleMode.Warn), "Warn"),
    };

    public override IReadOnlyList<AnalysisParam> Parameters { get; } = new[]
    {
        AnalysisParam.Choice(
            "Standard", "Standard",
            null,
            a => Rules(a).PresetKey ?? GradientRulePresets.CustomKey,
            (a, key) => ((GradientComplianceAnalysisDefinition)a).Rules = ApplyStandard(Rules(a), key),
            "The accessibility standard the limits below come from. Choosing one copies its limits into " +
            "this card, so a later plug-in update cannot change a project's verdict. Every limit stays " +
            "editable; an edited standard is marked as modified.",
            rebuildAfterCommit: true,
            optionsFor: a => StandardOptions(Rules(a))),
        AnalysisParam.Sources(
            "LevelAreas", "Level Areas",
            a => ((GradientComplianceAnalysisDefinition)a).LevelAreas,
            RhinoObjectType.Curve,
            "Closed curves around landings, turning spaces and other areas that must be level in every " +
            "direction. A curve inside another makes a hole."),
        AnalysisParam.Choice(
            "LevelAreaMode", "Level Rule",
            RuleModeOptions,
            a => Rules(a).LevelAreaMode.ToString(),
            (a, key) =>
            {
                if (Enum.TryParse(key, out GradientRuleMode mode))
                {
                    Rules(a).LevelAreaMode = mode;
                    GradientRulePresets.RefreshModified(Rules(a));
                }
            },
            "Off ignores level areas. Report shows ground over the limit in amber, as information. Warn " +
            "shows it in red, as a failure.",
            rebuildAfterCommit: true),
        AnalysisParam.Slope(
            "LevelAreaMaxSlope", "Level Limit",
            a => Rules(a).LevelAreaMaxSlopeDegrees,
            (a, v) =>
            {
                Rules(a).LevelAreaMaxSlopeDegrees = v;
                GradientRulePresets.RefreshModified(Rules(a));
            },
            "The steepest a level area may be in any direction, and the slope below which a stretch of " +
            "route counts as a landing. Typed in any slope unit: 1:48, 2.08% and 1.19deg are the same limit.",
            rebuildAfterCommit: true,
            // Shown while either rule is on: with level areas off it still decides which stretches of a
            // route are landings, and a limit that steers the result must never be hidden.
            visibleWhen: a => Rules(a).LevelAreaMode != GradientRuleMode.Off || Rules(a).RouteMode != GradientRuleMode.Off),
        AnalysisParam.Sources(
            "Routes", "Routes",
            a => ((GradientComplianceAnalysisDefinition)a).Routes,
            RhinoObjectType.Curve,
            "Curves along the centre of accessible routes. Slope along a route is its running slope and " +
            "is allowed up to the ramp limit; slope across it is cross slope. Drawing direction does not " +
            "matter."),
        AnalysisParam.Number(
            "RouteWidth", "Route Width",
            a => ((GradientComplianceAnalysisDefinition)a).RouteWidth,
            (a, v) => ((GradientComplianceAnalysisDefinition)a).RouteWidth = Math.Max(0.0, v),
            "Width of the corridor checked along each route, centred on the curve.",
            min: 0.0,
            unit: ParameterUnit.ModelLength,
            rebuildAfterCommit: true),
        AnalysisParam.Choice(
            "RouteMode", "Route Rule",
            RuleModeOptions,
            a => Rules(a).RouteMode.ToString(),
            (a, key) =>
            {
                if (Enum.TryParse(key, out GradientRuleMode mode))
                {
                    Rules(a).RouteMode = mode;
                    GradientRulePresets.RefreshModified(Rules(a));
                }
            },
            "Off ignores routes. Report shows running or cross slope over the limit in amber; Warn shows " +
            "it in red. Ramps within the limit show blue either way: allowed, but not a walk.",
            rebuildAfterCommit: true),
        RouteSlope("WalkMaxSlope", "Walk Limit",
            r => r.WalkMaxSlopeDegrees, (r, v) => r.WalkMaxSlopeDegrees = v,
            "The steepest running slope that is still a walk. Steeper, up to the ramp limit, is a ramp."),
        RouteSlope("RampMaxSlope", "Ramp Limit",
            r => r.RampMaxSlopeDegrees, (r, v) => r.RampMaxSlopeDegrees = v,
            "The steepest running slope allowed at all. Steeper fails."),
        RouteSlope("CrossMaxSlope", "Cross Limit",
            r => r.CrossMaxSlopeDegrees, (r, v) => r.CrossMaxSlopeDegrees = v,
            "The steepest a route may fall across its direction of travel."),
        RouteLength("LandingMinLength", "Landing Min",
            r => r.LandingMinLength, (r, v) => r.LandingMinLength = v,
            "The shortest level stretch of a route that counts as a landing. Landings are found along the " +
            "routes, not drawn: a stretch within the level limit for at least this long. A shorter flat " +
            "does not end the run it sits in."),
        RouteLength("WalkMaxRise", "Walk Max Rise",
            r => r.WalkMaxRise, (r, v) => r.WalkMaxRise = v,
            "The most a walk may climb between landings. Zero means no limit, as under ADA."),
        RouteLength("RampMaxRise", "Ramp Max Rise",
            r => r.RampMaxRise, (r, v) => r.RampMaxRise = v,
            "The most a ramp run may climb between landings. Zero means no limit."),
        AnalysisParam.GoingTable(
            "RampGoingLimits", "Ramp Goings",
            "The longest ramp run allowed at each gradient: a ramp at this gradient or gentler may run up " +
            "to this far between landings. Leave the table empty for no going limit. With Interpolate on, " +
            "gradients between two rows get a going between theirs, as Approved Document M allows.",
            visibleWhen: a => Rules(a).RouteMode != GradientRuleMode.Off),
        AnalysisParam.Number(
            "MeasurementLength", "Measure Over",
            a => ((GradientComplianceAnalysisDefinition)a).MeasurementLength,
            (a, v) => ((GradientComplianceAnalysisDefinition)a).MeasurementLength = Math.Max(0.0, v),
            "The length the gradient is averaged over, like a level laid on the ground. Zero measures " +
            "each triangle alone, which fails survey-derived landings a level would pass. Not part of the " +
            "standard: none states one.",
            min: 0.0,
            unit: ParameterUnit.ModelLength),
    };

    public override string? DescribeBlocker(TerrainDefinition terrain, AnalysisDefinition analysis)
    {
        var compliance = (GradientComplianceAnalysisDefinition)analysis;
        bool levelOn = compliance.Rules.LevelAreaMode != GradientRuleMode.Off;
        bool routesOn = compliance.Rules.RouteMode != GradientRuleMode.Off;
        if (!levelOn && !routesOn)
            return "Every rule is off — set “Level Rule” or “Route Rule” below.";

        return (levelOn && compliance.LevelAreas.HasReferences) || (routesOn && compliance.Routes.HasReferences)
            ? null
            : "Needs something to check — set “Level Areas” or “Routes” below.";
    }

    private static AnalysisParam RouteLength(
        string key,
        string label,
        Func<GradientRuleSet, double> get,
        Action<GradientRuleSet, double> set,
        string help) =>
        AnalysisParam.Number(
            key, label,
            a => get(Rules(a)),
            (a, v) =>
            {
                set(Rules(a), Math.Max(0.0, v));
                GradientRulePresets.RefreshModified(Rules(a));
            },
            help,
            min: 0.0,
            unit: ParameterUnit.ModelLength,
            rebuildAfterCommit: true,
            visibleWhen: a => Rules(a).RouteMode != GradientRuleMode.Off);

    private static AnalysisParam RouteSlope(
        string key,
        string label,
        Func<GradientRuleSet, double> get,
        Action<GradientRuleSet, double> set,
        string help) =>
        AnalysisParam.Slope(
            key, label,
            a => get(Rules(a)),
            (a, v) =>
            {
                set(Rules(a), v);
                GradientRulePresets.RefreshModified(Rules(a));
            },
            help,
            rebuildAfterCommit: true,
            visibleWhen: a => Rules(a).RouteMode != GradientRuleMode.Off);


    public override string? DescribeBasis(TerrainDefinition terrain, AnalysisDefinition analysis)
    {
        GradientRuleSet rules = Rules(analysis);
        GradientRulePresets.Preset? preset = GradientRulePresets.Find(rules.PresetKey);
        if (preset == null)
            return "Custom limits, not taken from a built-in standard.";

        return rules.IsModified
            ? $"Based on {preset.Source} Edited since."
            : preset.Source;
    }

    private static IReadOnlyList<(string Key, string Label)> StandardOptions(GradientRuleSet rules)
    {
        var options = new List<(string Key, string Label)>(GradientRulePresets.All.Count + 1);
        foreach (GradientRulePresets.Preset preset in GradientRulePresets.All)
        {
            bool edited = rules.IsModified && string.Equals(rules.PresetKey, preset.Key, StringComparison.Ordinal);
            options.Add((preset.Key, edited ? $"{preset.Label} (modified)" : preset.Label));
        }

        options.Add((GradientRulePresets.CustomKey, "Custom"));
        return options;
    }

    /// <summary>
    /// Choosing a preset replaces every limit with the preset's. Choosing Custom keeps the current limits
    /// and drops their provenance: the numbers the user sees stay put, they just stop claiming a source.
    /// </summary>
    internal static GradientRuleSet ApplyStandard(GradientRuleSet current, string? key)
    {
        GradientRulePresets.Preset? preset = GradientRulePresets.Find(key);
        if (preset != null)
            return GradientRulePresets.CreateScaled(preset, current.ModelUnitsPerMeter);

        GradientRuleSet custom = current.Clone();
        custom.PresetKey = null;
        custom.IsModified = false;
        return custom;
    }
}
