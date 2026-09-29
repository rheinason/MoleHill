using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;
using ModifierParam = MoleHill.Rhino.Registry.ParameterDescriptor<MoleHill.Rhino.Model.ModifierDefinition>;
using RhinoObjectType = Rhino.DocObjects.ObjectType;

namespace MoleHill.Rhino.Registry;

internal sealed class SmoothModifierDescriptor : ModifierTypeDescriptor
{
    public override string Kind => "smooth";
    public override Type DefinitionType => typeof(SmoothModifierDefinition);
    public override string DisplayName => "Smooth";
    public override string IconName => "ModSmooth";
    public override int SortOrder => 3;
    public override string Subtitle => "Smooth heights, keep plan";
    public override ModifierDefinition Create(UnitSystem unitSystem) => new SmoothModifierDefinition();
    public override void RunBuildStage(ModifierBuildContext context) => TerrainBuildService.RunSmoothStage(context);

    public override IReadOnlyList<ModifierParam> Parameters { get; } = new[]
    {
        ModifierParam.Sources(
            "Boundaries", "Boundaries",
            m => ((SmoothModifierDefinition)m).Boundaries,
            RhinoObjectType.Curve,
            "Closed curves that limit smoothing to the terrain inside them. Leave empty to smooth the whole terrain."),
        ModifierParam.Sources(
            "Breaklines", "Protect",
            m => ((SmoothModifierDefinition)m).Breaklines,
            RhinoObjectType.Curve,
            "Curves that protect their height from smoothing while the terrain around them is smoothed. How firmly is set by Protect Hold."),
        ModifierParam.Slider(
            "Iterations", "Iterations",
            m => ((SmoothModifierDefinition)m).Iterations,
            (m, v) => ((SmoothModifierDefinition)m).Iterations = (int)Math.Round(v),
            softMin: 0.0, softMax: 12.0, decimalPlaces: 0, hardMin: 0.0, liveScrub: true,
            help: "How many smoothing passes to run. Scrub for quick changes, or type larger values directly when you need more than the slider's soft range."),
        ModifierParam.Slider(
            "Strength", "Strength",
            m => ((SmoothModifierDefinition)m).Strength,
            (m, v) => ((SmoothModifierDefinition)m).Strength = v,
            softMin: 0.0, softMax: 1.0, decimalPlaces: 3, hardMin: 0.0, hardMax: 1.0, liveScrub: true,
            help: "How strongly each pass moves vertex heights. Scrub within the usual 0-1 range, or type a value directly if you need something unusual."),
        ModifierParam.Slider(
            "BreaklineFixity", "Protect Hold",
            m => ((SmoothModifierDefinition)m).BreaklineFixity,
            (m, v) => ((SmoothModifierDefinition)m).BreaklineFixity = v,
            softMin: 0.0, softMax: 1.0, decimalPlaces: 3, hardMin: 0.0, hardMax: 1.0, liveScrub: true,
            help: "How firmly protected curves hold their height against smoothing: 1 keeps them exactly as they are, 0 smooths them like any other vertex. Scrub in the common range, or type a precise value directly."),
    };
}
