using static MoleHill.Rhino.Registry.ModifierSummaryFormatting;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;
using RhinoObjectType = Rhino.DocObjects.ObjectType;
using ModifierParam = MoleHill.Rhino.Registry.ParameterDescriptor<MoleHill.Rhino.Model.ModifierDefinition>;

namespace MoleHill.Rhino.Registry;

internal sealed class RemeshModifierDescriptor : ModifierTypeDescriptor
{
    public override string Kind => "remesh";
    public override Type DefinitionType => typeof(RemeshModifierDefinition);
    public override string DisplayName => "Remesh";
    public override string IconName => "ModRemesh";
    public override int SortOrder => 2;
    public override string Subtitle => "Even triangles, creases kept";
    public override ModifierDefinition Create(UnitSystem unitSystem) => new RemeshModifierDefinition
    {
        Mode = "isotropic",
        CreaseAngle = 30.0
    };
    public override void RunBuildStage(ModifierBuildContext context) => TerrainBuildService.RunRemeshStage(context);

    public override string Summarize(ModifierDefinition modifier)
    {
        var r = (RemeshModifierDefinition)modifier;
        string remeshEdge = r.EdgeLength > 0 ? $"Edge Length {r.EdgeLength:G4}" : "Edge Length auto";
        return r.CreaseAngle > 0
            ? $"{remeshEdge} | Crease Angle {r.CreaseAngle:G4} deg"
            : remeshEdge;
    }

    private static readonly IReadOnlyList<(string Key, string Label)> ModeOptions = new[]
    {
        ("isotropic", "Isotropic (best quality)"),
        ("rebuild", "Full Rebuild (classic, wall-safe)"),
        ("local", "Local Refine (preserve topology)"),
    };

    public override IReadOnlyList<ModifierParam> Parameters { get; } = new[]
    {
        ModifierParam.Sources(
            "Constraints", "Breaklines",
            m => ((RemeshModifierDefinition)m).Constraints,
            RhinoObjectType.Curve,
            "Curves whose edges the remesh keeps. Creases detected from the mesh are kept as well (see Crease Angle)."),
        ModifierParam.Choice(
            "Mode", "Mode", ModeOptions,
            m => ((RemeshModifierDefinition)m).Mode,
            (m, v) => ((RemeshModifierDefinition)m).Mode = v ?? "isotropic",
            "Isotropic regularizes the whole terrain to even triangles (best overall quality, can be slow on very large terrains and may occasionally cross retaining walls on shallow wall angles). Full Rebuild is the classic constrained-Delaunay re-triangulation — never crosses a wall or breakline, coarser triangle shapes. Local Refine only splits/flips triangles in place, preserving existing topology exactly — fastest and safest on huge terrains, coarsest quality."),
        ModifierParam.Number(
            "EdgeLength", "Edge Length",
            m => ((RemeshModifierDefinition)m).EdgeLength,
            (m, v) => ((RemeshModifierDefinition)m).EdgeLength = v,
            "Target edge length: the remesh regularizes the whole terrain toward even triangles of this size while keeping every vertex exactly on the surface. Smaller = denser, larger = coarser. Leave at 0 to preserve the mesh's approximate face density across its plan area. Preview builds run at twice this length.",
            unit: ParameterUnit.ModelLength),
        ModifierParam.Number(
            "CreaseAngle", "Crease Angle",
            m => ((RemeshModifierDefinition)m).CreaseAngle,
            (m, v) => ((RemeshModifierDefinition)m).CreaseAngle = v,
            "Keep creases: edges where the mesh folds at least this many degrees (batter toes, slope breaks) are kept — vertices slide only along them and no edge flips across. Detected from the mesh each pass and never saved as breaklines. Around 20-35 catches toe lines; leave at 0 to disable.",
            unit: ParameterUnit.Degrees),
        ModifierParam.Number(
            "MinAngle", "Min Angle",
            m => ((RemeshModifierDefinition)m).MinAngle,
            (m, v) => ((RemeshModifierDefinition)m).MinAngle = v,
            "Full Rebuild only: minimum triangle angle in degrees. The constrained-Delaunay refinement splits skinny triangles until none is sharper than this. Around 20-30 gives well-shaped triangles; above ~34 the refinement may not terminate. Leave at 0 for no angle limit.",
            max: 34.0,
            unit: ParameterUnit.Degrees,
            visibleWhen: IsRebuild),
        ModifierParam.Number(
            "MaxArea", "Max Area",
            m => ((RemeshModifierDefinition)m).MaxArea,
            (m, v) => ((RemeshModifierDefinition)m).MaxArea = v,
            "Full Rebuild only: maximum triangle area. The refinement inserts points until every triangle is under this area, capping triangle size independently of Edge Length. Leave at 0 for no area limit.",
            visibleWhen: IsRebuild),
    };

    private static bool IsRebuild(ModifierDefinition m) =>
        string.Equals(((RemeshModifierDefinition)m).Mode, "rebuild", StringComparison.OrdinalIgnoreCase);
}
