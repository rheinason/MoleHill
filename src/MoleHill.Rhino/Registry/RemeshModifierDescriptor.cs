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
        Mode = "isotropic",
        CreaseAngle = 30.0
    };
    public override void RunBuildStage(ModifierBuildContext context) => TerrainBuildService.RunRemeshStage(context);

    private static readonly IReadOnlyList<(string Key, string Label)> ModeOptions = new[]
    {
        ("isotropic", "Isotropic (best quality)"),
        ("rebuild", "Full Rebuild (classic, wall-safe)"),
        ("local", "Local Refine (preserve topology)"),
    };

    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = new[]
    {
        ParameterDescriptor.Sources(
            "Constraints", "Constraints",
            m => ((RemeshModifierDefinition)m).Constraints,
            RhinoObjectType.Curve),
        ParameterDescriptor.Choice(
            "Mode", "Algorithm", ModeOptions,
            m => ((RemeshModifierDefinition)m).Mode,
            (m, v) => ((RemeshModifierDefinition)m).Mode = v ?? "isotropic",
            "Isotropic regularizes the whole terrain to even triangles (best overall quality, can be slow on very large terrains and may occasionally cross retaining walls on shallow wall angles). Full Rebuild is the classic constrained-Delaunay re-triangulation — never crosses a wall or constraint, coarser triangle shapes. Local Refine only splits/flips triangles in place, preserving existing topology exactly — fastest and safest on huge terrains, coarsest quality."),
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
        ParameterDescriptor.Number(
            "MinAngle", "Min Angle",
            m => ((RemeshModifierDefinition)m).MinAngle,
            (m, v) => ((RemeshModifierDefinition)m).MinAngle = v,
            "Full Rebuild only: minimum triangle angle in degrees. The constrained-Delaunay refinement splits skinny triangles until none is sharper than this. Around 20-30 gives well-shaped triangles; above ~34 the refinement may not terminate. Leave at 0 for no angle constraint.",
            max: 34.0,
            visibleWhen: IsRebuild),
        ParameterDescriptor.Number(
            "MaxArea", "Max Area",
            m => ((RemeshModifierDefinition)m).MaxArea,
            (m, v) => ((RemeshModifierDefinition)m).MaxArea = v,
            "Full Rebuild only: maximum triangle area. The refinement inserts points until every triangle is under this area, capping triangle size independently of Edge Length. Leave at 0 for no area constraint.",
            visibleWhen: IsRebuild),
    };

    private static bool IsRebuild(ModifierDefinition m) =>
        string.Equals(((RemeshModifierDefinition)m).Mode, "rebuild", StringComparison.OrdinalIgnoreCase);
}
