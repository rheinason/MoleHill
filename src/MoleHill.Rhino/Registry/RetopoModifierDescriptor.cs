using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;
using RhinoObjectType = Rhino.DocObjects.ObjectType;
using ModifierParam = MoleHill.Rhino.Registry.ParameterDescriptor<MoleHill.Rhino.Model.ModifierDefinition>;

namespace MoleHill.Rhino.Registry;

internal sealed class RetopoModifierDescriptor : ModifierTypeDescriptor
{
    public override string Kind => "retopo";
    public override Type DefinitionType => typeof(RetopoModifierDefinition);
    public override string DisplayName => "Retopo";
    public override string IconName => "ModRetopo";
    public override int SortOrder => 4;
    public override string Subtitle => "Quads that follow the terrain's creases";
    public override ModifierDefinition Create(UnitSystem unitSystem) => new RetopoModifierDefinition();
    public override void RunBuildStage(ModifierBuildContext context) => TerrainBuildService.RunRetopoStage(context);

    public override IReadOnlyList<ModifierParam> Parameters { get; } = new[]
    {
        ModifierParam.Sources(
            "Constraints", "Breaklines",
            m => ((RetopoModifierDefinition)m).Constraints,
            RhinoObjectType.Curve,
            "Curves (road edges, ridges) the quad flow follows, in addition to creases detected from the mesh and the mesh border."),
        ModifierParam.Bool(
            "Quads", "Quads",
            m => ((RetopoModifierDefinition)m).Quads,
            (m, v) => ((RetopoModifierDefinition)m).Quads = v,
            "Replace the terrain with a quad-dominant mesh that flows along creases and breaklines (field-aligned remesh + triangle pairing; uses Edge Length as the quad size). Hole-free by construction; retaining walls pass through untouched. Retopo can be placed anywhere in the modifier stack. Off = keep the input mesh and only preview the field."),
        ModifierParam.Bool(
            "ShowField", "Show Field",
            m => ((RetopoModifierDefinition)m).ShowField,
            (m, v) => ((RetopoModifierDefinition)m).ShowField = v,
            "Draw the computed cross-field as a flow-cross overlay (short crossed segments along the two quad directions, colored by direction) so you can confirm the flow runs along creases and breaklines. Off = no overlay."),
        ModifierParam.Number(
            "CreaseAngle", "Crease Angle",
            m => ((RetopoModifierDefinition)m).CreaseAngle,
            (m, v) => ((RetopoModifierDefinition)m).CreaseAngle = v,
            "Align the field to interior creases (batter toes, slope breaks) folding at least this many degrees. Around 20-35 catches batter toes; 0 = align to the mesh border and breaklines only.",
            unit: ParameterUnit.Degrees),
        ModifierParam.Number(
            "TargetEdgeLength", "Edge Length",
            m => ((RetopoModifierDefinition)m).TargetEdgeLength,
            (m, v) => ((RetopoModifierDefinition)m).TargetEdgeLength = v,
            "Target quad size for the extracted quad-dominant mesh. 0 auto-derives from the mesh's extent and density. Preview builds run at twice this size.",
            unit: ParameterUnit.ModelLength),
    };
}
