using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;
using ModifierParam = MoleHill.Rhino.Registry.ParameterDescriptor<MoleHill.Rhino.Model.ModifierDefinition>;
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

    // Source rows, then the shared boundary-peel rows. The panel positions the peel rows itself, inside
    // its "Peel Border" group.
    public override IReadOnlyList<ModifierParam> Parameters { get; } = new ModifierParam[]
    {
        ModifierParam.Sources(
            "Points", "Points",
            m => ((AddGeometryModifierDefinition)m).Points,
            RhinoObjectType.Point | RhinoObjectType.PointSet),
        ModifierParam.Sources(
            "Breaklines", "Breaklines",
            m => ((AddGeometryModifierDefinition)m).Breaklines,
            RhinoObjectType.Curve),
        ModifierParam.Sources(
            "Contours", "Contours",
            m => ((AddGeometryModifierDefinition)m).Contours,
            RhinoObjectType.Curve),
    }.Concat(GeometryInputParameterCatalog.BoundaryPeel).ToArray();
}
