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
    public override string Subtitle => "Constraint-preserving remesh";
    public override ModifierDefinition Create(UnitSystem unitSystem) => new RemeshModifierDefinition
    {
        LocalRefine = true,
        CreaseAngle = 30.0
    };
    public override void RunBuildStage(ModifierBuildContext context) => TerrainBuildService.RunRemeshStage(context);

    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = new[]
    {
        ParameterDescriptor.Sources(
            "Constraints", "Constraints",
            m => ((RemeshModifierDefinition)m).Constraints,
            RhinoObjectType.Curve),
        ParameterDescriptor.Bool(
            "LocalRefine", "Local refine",
            m => ((RemeshModifierDefinition)m).LocalRefine,
            (m, v) => ((RemeshModifierDefinition)m).LocalRefine = v,
            "Local refine: keep the input topology and flow lines, split only genuinely-coarse triangles in place on the surface, and flip toward a regular, even mesh — a render-quality base for Smooth. Respects creases (see Crease Angle). Off = the classic global-Delaunay remesh."),
        ParameterDescriptor.Number(
            "EdgeLength", "Edge Length",
            m => ((RemeshModifierDefinition)m).EdgeLength,
            (m, v) => ((RemeshModifierDefinition)m).EdgeLength = v,
            "Target triangle edge length. Smaller values make denser meshes; larger values make coarser meshes. Leave at 0 to let Max Area drive remeshing."),
        ParameterDescriptor.Number(
            "MaxArea", "Max Area",
            m => ((RemeshModifierDefinition)m).MaxArea,
            (m, v) => ((RemeshModifierDefinition)m).MaxArea = v,
            "Maximum triangle area. Smaller values create finer remeshes; large values keep larger faces. Leave at 0 to disable this limit."),
        ParameterDescriptor.Number(
            "MinAngle", "Min Angle",
            m => ((RemeshModifierDefinition)m).MinAngle,
            (m, v) => ((RemeshModifierDefinition)m).MinAngle = v,
            "Minimum triangle angle in degrees. Around 20-30 is moderate quality; pushing high can overconstrain or fail on awkward meshes."),
        ParameterDescriptor.Number(
            "MergeDistance", "Merge Distance",
            m => ((RemeshModifierDefinition)m).MergeDistance,
            (m, v) => ((RemeshModifierDefinition)m).MergeDistance = v,
            "Merge by distance: collapse near-duplicate vertices closer than this (e.g. batter-toe pinches) instead of protecting the tiny edge between them. Leave at 0 to disable. Keep it well below the typical edge length."),
        ParameterDescriptor.Number(
            "CreaseAngle", "Crease Angle",
            m => ((RemeshModifierDefinition)m).CreaseAngle,
            (m, v) => ((RemeshModifierDefinition)m).CreaseAngle = v,
            "Preserve creases: feature edges (batter toes, slope breaks) folding at least this many degrees are kept smooth through the remesh. Detected from the mesh each pass and never persisted as breaklines. Around 20-35 catches toe lines; leave at 0 to disable."),
    };
}
