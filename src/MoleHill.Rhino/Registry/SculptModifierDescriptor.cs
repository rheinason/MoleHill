using static MoleHill.Rhino.Registry.ModifierSummaryFormatting;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;
using ModifierParam = MoleHill.Rhino.Registry.ParameterDescriptor<MoleHill.Rhino.Model.ModifierDefinition>;
using RhinoObjectType = Rhino.DocObjects.ObjectType;

namespace MoleHill.Rhino.Registry;

internal sealed class SculptModifierDescriptor : ModifierTypeDescriptor
{
    public override string Kind => "sculpt";
    public override Type DefinitionType => typeof(SculptModifierDefinition);
    public override string DisplayName => "Sculpt";
    public override string IconName => "ModSculpt";
    public override int SortOrder => 4;
    public override string Subtitle => "Sculpt heights by hand";

    // Hidden detail scale retained for field cell-size and session radius defaults. DynTopo is disabled
    // and hidden until the refinement path is stable on real graded terrain.
    public override ModifierDefinition Create(UnitSystem unitSystem) => new SculptModifierDefinition
    {
        DynTopo = false,
        DetailSize = ModelUnits.FromMeters(1.0, unitSystem),
        ConstraintFeather = ModelUnits.FromMeters(1.0, unitSystem),
    };

    public override void RunBuildStage(ModifierBuildContext context) => SculptStage.Run(context);

    public override string Summarize(ModifierDefinition modifier)
    {
        var sculpt = (SculptModifierDefinition)modifier;
        return sculpt.Tiles.Count == 0
            ? "no strokes"
            : $"{sculpt.Tiles.Count} tiles | {CountSources(sculpt.Constraints)} protect curves";
    }

    public override IReadOnlyList<ModifierParam> Parameters { get; } = new[]
    {
        ModifierParam.Sources(
            "Constraints", "Protect",
            m => ((SculptModifierDefinition)m).Constraints,
            RhinoObjectType.Curve,
            "Curves that protect terrain from sculpting. A closed curve protects everything inside it; an open curve protects the terrain along it. Selecting the source of an earlier Grade Path protects that path's full width."),
        ModifierParam.Number(
            "ConstraintFeather", "Feather",
            m => ((SculptModifierDefinition)m).ConstraintFeather,
            (m, v) => ((SculptModifierDefinition)m).ConstraintFeather = v,
            "Distance beyond each protected area over which sculpting fades back in. Leave at 0 to use the Sculpt detail size.",
            min: 0.0),
        ModifierParam.ReadOnly(
            "Field", "Stored Sculpt",
            m =>
            {
                var sculpt = (SculptModifierDefinition)m;
                return sculpt.Tiles.Count == 0
                    ? "empty"
                    : $"{sculpt.Tiles.Count} tiles, ~{SculptFieldCodec.EstimateKilobytes(sculpt):N0} KB";
            },
            help: "The sculpt strokes stored on this modifier, saved with the document as height-offset tiles."),
    };
}
