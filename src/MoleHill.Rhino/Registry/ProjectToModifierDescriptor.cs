using static MoleHill.Rhino.Registry.ModifierSummaryFormatting;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;
using RhinoObjectType = Rhino.DocObjects.ObjectType;
using ModifierParam = MoleHill.Rhino.Registry.ParameterDescriptor<MoleHill.Rhino.Model.ModifierDefinition>;

namespace MoleHill.Rhino.Registry;

internal sealed class ProjectToModifierDescriptor : ModifierTypeDescriptor
{
    public override string Kind => "project-to";
    public override Type DefinitionType => typeof(ProjectToModifierDefinition);
    public override string DisplayName => "Project To";
    public override string IconName => "ModProjectTo";

    public override int AccentArgb => unchecked((int)0xFF00798C);

    public override int SortOrder => 9;
    public override string Subtitle => "Blend heights toward a target mesh";

    public override ModifierDefinition Create(UnitSystem unitSystem) => new ProjectToModifierDefinition
    {
        FeatherDistance = ModelUnits.FromMeters(1.0, unitSystem)
    };

    public override void RunBuildStage(ModifierBuildContext context) => ProjectToStage.Run(context);

    public override string Summarize(ModifierDefinition modifier)
    {
        var projectTo = (ProjectToModifierDefinition)modifier;
        string target = projectTo.TargetMesh.HasReferences
            ? "target mesh"
            : projectTo.TargetTerrainId.HasValue ? "target terrain" : "no target";
        return $"{target} · {Count(CountSources(projectTo.Boundaries), "boundary", "boundaries")} · Strength {projectTo.Strength:G3}";
    }

    public override string? InertReason(ModifierDefinition modifier, TerrainBuildSnapshot snapshot) =>
        modifier is ProjectToModifierDefinition m && NoneResolve(snapshot, m.TargetMesh) && m.TargetTerrainId == null ? "Not applied — no target selected." : null;

    public override IReadOnlyList<ModifierParam> Parameters { get; } = new[]
    {
        ModifierParam.TerrainReference(
            "TargetTerrain", "Target Terrain",
            m => ((ProjectToModifierDefinition)m).TargetTerrainId,
            (m, id) =>
            {
                var projectTo = (ProjectToModifierDefinition)m;
                projectTo.TargetTerrainId = id;
                if (id.HasValue)
                {
                    projectTo.TargetMesh.ReplaceObjects(Array.Empty<Guid>());
                    projectTo.TargetMesh.ReplaceLayers(Array.Empty<string>());
                }
            },
            "Another MoleHill terrain's latest completed final mesh. Choosing it clears Target Mesh.",
            meanwhile: "nothing is projected",
            noTerrainLabel: "None",
            // A terrain that already projects onto this one, directly or through others, would be a cycle.
            offers: (owner, all, candidate) => !DependsOn(all, candidate.TerrainId, owner.TerrainId, new HashSet<Guid>())),
        ModifierParam.Sources(
            "TargetMesh", "Target Mesh",
            m => ((ProjectToModifierDefinition)m).TargetMesh,
            RhinoObjectType.Mesh,
            "One Rhino mesh to project toward. Assigning it clears the terrain target.",
            singleObject: true,
            afterChanged: m =>
            {
                var projectTo = (ProjectToModifierDefinition)m;
                if (projectTo.TargetMesh.HasReferences)
                    projectTo.TargetTerrainId = null;
            }),
        ModifierParam.Sources(
            "Boundaries", "Boundaries",
            m => ((ProjectToModifierDefinition)m).Boundaries,
            RhinoObjectType.Curve,
            "Closed curves that limit the effect to the terrain inside them. Nested loops alternate included and excluded regions, so inner loops make donut holes. Leave empty to affect the whole terrain."),
        ModifierParam.Slider(
            "Strength", "Strength",
            m => ((ProjectToModifierDefinition)m).Strength,
            (m, v) => ((ProjectToModifierDefinition)m).Strength = v,
            softMin: 0.0, softMax: 1.0, hardMin: 0.0, hardMax: 1.0,
            decimalPlaces: 3, liveScrub: true,
            help: "How far each covered terrain vertex moves toward the target elevation."),
        ModifierParam.Number(
            "FeatherDistance", "Feather",
            m => ((ProjectToModifierDefinition)m).FeatherDistance,
            (m, v) => ((ProjectToModifierDefinition)m).FeatherDistance = v,
            "Distance inside every boundary edge over which the effect rises smoothly from zero to full strength.",
            min: 0.0, unit: ParameterUnit.ModelLength),
    };

    /// <summary>Whether <paramref name="terrainId"/> projects onto <paramref name="soughtTerrainId"/>, directly or through others.</summary>
    internal static bool DependsOn(
        IReadOnlyList<TerrainDefinition> terrains,
        Guid terrainId,
        Guid soughtTerrainId,
        HashSet<Guid> visited)
    {
        if (terrainId == soughtTerrainId)
            return true;
        if (!visited.Add(terrainId))
            return false;

        TerrainDefinition? terrain = terrains.FirstOrDefault(item => item.TerrainId == terrainId);
        if (terrain == null)
            return false;

        foreach (Guid targetId in terrain.Modifiers
                     .OfType<ProjectToModifierDefinition>()
                     .Where(item => item.IsEnabled && !item.TargetMesh.HasReferences && item.TargetTerrainId.HasValue)
                     .Select(item => item.TargetTerrainId!.Value))
        {
            if (DependsOn(terrains, targetId, soughtTerrainId, visited))
                return true;
        }

        return false;
    }
}
