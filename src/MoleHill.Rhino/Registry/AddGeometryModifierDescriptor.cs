using static MoleHill.Rhino.Registry.ModifierSummaryFormatting;
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
    public override string Subtitle => "Add points, breaklines and contours";
    public override ModifierDefinition Create(UnitSystem unitSystem) => new AddGeometryModifierDefinition();
    public override void RunBuildStage(ModifierBuildContext context) => TerrainBuildService.RunAddGeometryStage(context);

    public override string Summarize(ModifierDefinition modifier)
    {
        var a = (AddGeometryModifierDefinition)modifier;
        return $"{CountSources(a.Points)} points | {CountSources(a.Breaklines)} breaklines | " +
            $"{CountSources(a.Contours)} contours";
    }

    public override string? InertReason(ModifierDefinition modifier, TerrainBuildSnapshot snapshot) =>
        modifier is AddGeometryModifierDefinition m && NoneResolve(snapshot, m.EnumerateSourceSets().ToArray()) ? "Not applied — no geometry selected." : null;

    // Source rows, then the shared boundary-peel rows. The panel positions the peel rows itself, inside
    // its "Peel Border" group.
    public override IReadOnlyList<ModifierParam> Parameters { get; } = new ModifierParam[]
    {
        ModifierParam.Sources(
            "Points", "Points",
            m => ((AddGeometryModifierDefinition)m).Points,
            RhinoObjectType.Point | RhinoObjectType.PointSet,
            "Points that become terrain vertices at their own height."),
        ModifierParam.Sources(
            "Breaklines", "Breaklines",
            m => ((AddGeometryModifierDefinition)m).Breaklines,
            RhinoObjectType.Curve,
            "Curves whose edges the mesh keeps. Each vertex sets the height along the edge."),
        ModifierParam.Sources(
            "Contours", "Contours",
            m => ((AddGeometryModifierDefinition)m).Contours,
            RhinoObjectType.Curve,
            "Contour curves. Their vertices set terrain height."),
    }.Concat(GeometryInputParameterCatalog.BoundaryPeel).ToArray();
}
