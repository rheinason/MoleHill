using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;
using RhinoObjectType = Rhino.DocObjects.ObjectType;

namespace MoleHill.Rhino.Registry;

internal sealed class AddGeometryModifierDescriptor : ModifierTypeDescriptor
{
    public override string Kind => "add-geometry";
    public override Type DefinitionType => typeof(AddGeometryModifierDefinition);
    public override string DisplayName => "Add Geometry";
    public override string IconName => "ModAddGeometry";
    public override int SortOrder => 1;
    public override string Subtitle => "Add source geometry";
    public override ModifierDefinition Create(UnitSystem unitSystem) => new AddGeometryModifierDefinition();
    public override void RunBuildStage(ModifierBuildContext context) => TerrainBuildService.RunAddGeometryStage(context);

    // Source rows only. The boundary-peel block is appended via the panel's bespoke-rows hook.
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = new[]
    {
        ParameterDescriptor.Sources(
            "Points", "Points",
            m => ((AddGeometryModifierDefinition)m).Points,
            RhinoObjectType.Point | RhinoObjectType.PointSet),
        ParameterDescriptor.Sources(
            "Breaklines", "Breaklines",
            m => ((AddGeometryModifierDefinition)m).Breaklines,
            RhinoObjectType.Curve),
        ParameterDescriptor.Sources(
            "Contours", "Contours",
            m => ((AddGeometryModifierDefinition)m).Contours,
            RhinoObjectType.Curve),
        ParameterDescriptor.Sources(
            "Boundary", "Boundary",
            m => ((AddGeometryModifierDefinition)m).Boundary,
            RhinoObjectType.Curve),
    };
}
