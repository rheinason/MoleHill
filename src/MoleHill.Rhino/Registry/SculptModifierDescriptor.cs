using System;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;

namespace MoleHill.Rhino.Registry;

internal sealed class SculptModifierDescriptor : ModifierTypeDescriptor
{
    public override string Kind => "sculpt";
    public override Type DefinitionType => typeof(SculptModifierDefinition);
    public override string DisplayName => "Sculpt";
    public override string IconName => "ModSculpt";
    public override int SortOrder => 4;
    public override string Subtitle => "Interactive Z sculpting";

    // Hidden detail scale retained for field cell-size and session radius defaults. DynTopo is disabled
    // and hidden until the refinement path is stable on real graded terrain.
    public override ModifierDefinition Create(UnitSystem unitSystem) => new SculptModifierDefinition
    {
        DynTopo = false,
        DetailSize = ModelUnits.FromMeters(1.0, unitSystem),
    };

    public override void RunBuildStage(ModifierBuildContext context) => TerrainBuildService.RunSculptStage(context);

    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = new[]
    {
        ParameterDescriptor.ReadOnly(
            "Field", "Field",
            m =>
            {
                var sculpt = (SculptModifierDefinition)m;
                return sculpt.Tiles.Count == 0
                    ? "empty"
                    : $"{sculpt.Tiles.Count} tiles, ~{SculptFieldCodec.EstimateKilobytes(sculpt):N0} KB";
            },
            help: "Stored sculpt data: displacement tiles saved with the document."),
    };
}
