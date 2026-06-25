using System;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;
using RhinoObjectType = Rhino.DocObjects.ObjectType;

namespace MoleHill.Rhino.Registry;

internal sealed class SmoothModifierDescriptor : ModifierTypeDescriptor
{
    public override string Kind => "smooth";
    public override Type DefinitionType => typeof(SmoothModifierDefinition);
    public override string DisplayName => "Smooth";
    public override string IconName => "ModSmooth";
    public override int SortOrder => 3;
    public override string Subtitle => "Z-only smoothing";
    public override ModifierDefinition Create(UnitSystem unitSystem) => new SmoothModifierDefinition();
    public override void RunBuildStage(ModifierBuildContext context) => TerrainBuildService.RunSmoothStage(context);

    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = new[]
    {
        ParameterDescriptor.Sources(
            "Boundaries", "Boundaries",
            m => ((SmoothModifierDefinition)m).Boundaries,
            RhinoObjectType.Curve),
        ParameterDescriptor.Sources(
            "Breaklines", "Breaklines",
            m => ((SmoothModifierDefinition)m).Breaklines,
            RhinoObjectType.Curve),
        ParameterDescriptor.Slider(
            "Iterations", "Iterations",
            m => ((SmoothModifierDefinition)m).Iterations,
            (m, v) => ((SmoothModifierDefinition)m).Iterations = (int)Math.Round(v),
            softMin: 0.0, softMax: 12.0, decimalPlaces: 0, hardMin: 0.0, liveScrub: true,
            help: "How many Z-only smoothing passes to run. Scrub for quick changes, or type larger values directly when you need more than the slider's soft range."),
        ParameterDescriptor.Slider(
            "Strength", "Strength",
            m => ((SmoothModifierDefinition)m).Strength,
            (m, v) => ((SmoothModifierDefinition)m).Strength = v,
            softMin: 0.0, softMax: 1.0, decimalPlaces: 3, hardMin: 0.0, hardMax: 1.0, liveScrub: true,
            help: "How strongly each pass moves vertex Z. Scrub within the usual 0-1 range, or type a value directly if you need something unusual."),
        ParameterDescriptor.Slider(
            "BreaklineFixity", "Fixity",
            m => ((SmoothModifierDefinition)m).BreaklineFixity,
            (m, v) => ((SmoothModifierDefinition)m).BreaklineFixity = v,
            softMin: 0.0, softMax: 1.0, decimalPlaces: 3, hardMin: 0.0, hardMax: 1.0, liveScrub: true,
            help: "How strongly breaklines resist smoothing. Scrub in the common range, or type a precise value directly."),
    };
}
