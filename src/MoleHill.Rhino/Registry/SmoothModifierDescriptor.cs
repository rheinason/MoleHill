using static MoleHill.Rhino.Registry.ModifierSummaryFormatting;
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
    public override string Subtitle => "Smooth heights or round creases, keep plan";
    public override ModifierDefinition Create(UnitSystem unitSystem) => new SmoothModifierDefinition
    {
        RoundingLength = ModelUnits.FromMeters(2.0, unitSystem)
    };
    public override void RunBuildStage(ModifierBuildContext context) => SmoothStage.Run(context);

    public override string Summarize(ModifierDefinition modifier)
    {
        var s = (SmoothModifierDefinition)modifier;
        return SmoothModes.IsRound(s)
            ? $"Round Creases | Rounding Length {s.RoundingLength:G4} | Crease Angle {s.CreaseAngle:G4} deg | {CountSources(s.Breaklines)} protect curves"
            : $"{s.Iterations} iterations | Strength {s.Strength:G3} | {CountSources(s.Breaklines)} protect curves";
    }

    public override string? InertReason(ModifierDefinition modifier, TerrainBuildSnapshot snapshot)
    {
        var s = (SmoothModifierDefinition)modifier;
        return SmoothModes.IsRound(s) && !(s.RoundingLength > 0.0)
            ? "Set a Rounding Length to round creases."
            : null;
    }

    private static readonly IReadOnlyList<(string Key, string Label)> ModeOptions = new[]
    {
        (SmoothModes.Smooth, "Smooth"),
        (SmoothModes.Round, "Round Creases"),
    };

    private static bool IsSmooth(ModifierDefinition m) => !SmoothModes.IsRound((SmoothModifierDefinition)m);

    private static bool IsRound(ModifierDefinition m) => SmoothModes.IsRound((SmoothModifierDefinition)m);

    public override IReadOnlyList<ModifierParam> Parameters { get; } = new[]
    {
        ModifierParam.Choice(
            "Mode", "Mode", ModeOptions,
            m => ((SmoothModifierDefinition)m).Mode,
            (m, v) => ((SmoothModifierDefinition)m).Mode = v ?? SmoothModes.Smooth,
            "Smooth evens out heights everywhere it reaches. Round Creases leaves the ground alone except across its creases - batter crests, toes and slope breaks - which it rounds with a curve of the Rounding Length: crests drop, toes rise, and the plan never moves."),
        ModifierParam.Sources(
            "Boundaries", "Boundaries",
            m => ((SmoothModifierDefinition)m).Boundaries,
            RhinoObjectType.Curve,
            "Closed curves that limit the edit to the terrain inside them. Leave empty to edit the whole terrain. Rounding fades out over half the Rounding Length approaching a boundary."),
        ModifierParam.Sources(
            "Breaklines", "Protect",
            m => ((SmoothModifierDefinition)m).Breaklines,
            RhinoObjectType.Curve,
            "Curves that protect their height from the edit while the terrain around them changes. Smooth: how firmly is set by Protect Hold. Round Creases: creases along them stay sharp and rounding fades out approaching them - protect a kerb, a road edge or a pad edge that must stay crisp."),
        ModifierParam.Slider(
            "Iterations", "Iterations",
            m => ((SmoothModifierDefinition)m).Iterations,
            (m, v) => ((SmoothModifierDefinition)m).Iterations = (int)Math.Round(v),
            softMin: 0.0, softMax: 12.0, decimalPlaces: 0, hardMin: 0.0, liveScrub: true,
            help: "How many smoothing passes to run. Scrub for quick changes, or type larger values directly when you need more than the slider's soft range.",
            visibleWhen: IsSmooth),
        ModifierParam.Slider(
            "Strength", "Strength",
            m => ((SmoothModifierDefinition)m).Strength,
            (m, v) => ((SmoothModifierDefinition)m).Strength = v,
            softMin: 0.0, softMax: 1.0, decimalPlaces: 3, hardMin: 0.0, hardMax: 1.0, liveScrub: true,
            help: "How strongly each pass moves vertex heights. Scrub within the usual 0-1 range, or type a value directly if you need something unusual.",
            visibleWhen: IsSmooth),
        ModifierParam.Slider(
            "BreaklineFixity", "Protect Hold",
            m => ((SmoothModifierDefinition)m).BreaklineFixity,
            (m, v) => ((SmoothModifierDefinition)m).BreaklineFixity = v,
            softMin: 0.0, softMax: 1.0, decimalPlaces: 3, hardMin: 0.0, hardMax: 1.0, liveScrub: true,
            help: "How firmly protected curves hold their height against smoothing: 1 keeps them exactly as they are, 0 smooths them like any other vertex. Scrub in the common range, or type a precise value directly.",
            visibleWhen: IsSmooth),
        ModifierParam.Number(
            "RoundingLength", "Rounding Length",
            m => ((SmoothModifierDefinition)m).RoundingLength,
            (m, v) => ((SmoothModifierDefinition)m).RoundingLength = v,
            "The plan width of the rounding across each crease, half on either side - the length of a road's vertical curve. A crease whose grade changes by g drops or rises by g x length / 8 on the crease itself. Creases shorter than this length are left as surface detail. Triangles too coarse to carry the curve beside a crease are split first, so no Remesh is needed.",
            min: 0.0,
            unit: ParameterUnit.ModelLength,
            liveScrub: true,
            visibleWhen: IsRound),
        ModifierParam.Number(
            "CreaseAngle", "Crease Angle",
            m => ((SmoothModifierDefinition)m).CreaseAngle,
            (m, v) => ((SmoothModifierDefinition)m).CreaseAngle = v,
            "Edges where the terrain folds at least this many degrees are creases and get rounded. Keep it low: a 1:3 batter meets level ground at only 18 degrees. Raise it to leave gentle slope breaks alone.",
            min: 0.1,
            max: 179.0,
            unit: ParameterUnit.Degrees,
            visibleWhen: IsRound),
    };
}
