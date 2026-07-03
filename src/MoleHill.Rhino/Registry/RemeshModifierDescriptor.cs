using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;
using RhinoObjectType = Rhino.DocObjects.ObjectType;

namespace MoleHill.Rhino.Registry;

internal sealed class RemeshModifierDescriptor : ModifierTypeDescriptor
{
    public override string Kind => "remesh";
    public override Type DefinitionType => typeof(RemeshModifierDefinition);
    public override string DisplayName => "Remesh";
    public override string IconName => "ModRemesh";
    public override int SortOrder => 2;
    public override string Subtitle => "Isotropic remesh (feature-preserving)";
    public override ModifierDefinition Create(UnitSystem unitSystem) => new RemeshModifierDefinition
    {
        CreaseAngle = 30.0
    };
    public override void RunBuildStage(ModifierBuildContext context) => TerrainBuildService.RunRemeshStage(context);

    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = new[]
    {
        ParameterDescriptor.Sources(
            "Constraints", "Constraints",
            m => ((RemeshModifierDefinition)m).Constraints,
            RhinoObjectType.Curve),
        ParameterDescriptor.Number(
            "EdgeLength", "Edge Length",
            m => ((RemeshModifierDefinition)m).EdgeLength,
            (m, v) => ((RemeshModifierDefinition)m).EdgeLength = v,
            "Target edge length: the remesh regularizes the whole terrain toward even triangles of this size while keeping every vertex exactly on the surface. Smaller = denser, larger = coarser. Leave at 0 to keep the mesh's own density (auto-derived from its median edge length). Preview builds run at twice this length."),
        ParameterDescriptor.Number(
            "CreaseAngle", "Crease Angle",
            m => ((RemeshModifierDefinition)m).CreaseAngle,
            (m, v) => ((RemeshModifierDefinition)m).CreaseAngle = v,
            "Preserve creases: feature edges (batter toes, slope breaks) folding at least this many degrees are pinned — vertices slide only along them and no edge flips across. Detected from the mesh each pass and never persisted as breaklines. Around 20-35 catches toe lines; leave at 0 to disable."),
    };
}
