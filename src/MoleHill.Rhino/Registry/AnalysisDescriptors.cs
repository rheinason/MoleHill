using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
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
