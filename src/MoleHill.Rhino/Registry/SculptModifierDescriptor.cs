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

    // 1 m default: fine enough for landscape work, coarse enough that DynTopo refinement and the
    // per-stroke rebuild stay interactive on large terrains (0.25 m was visibly laggy in the field).
    public override ModifierDefinition Create(UnitSystem unitSystem) => new SculptModifierDefinition
    {
        DetailSize = ModelUnits.FromMeters(1.0, unitSystem),
    };

    public override void RunBuildStage(ModifierBuildContext context) => TerrainBuildService.RunSculptStage(context);

    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = new[]
    {
        ParameterDescriptor.Bool(
            "DynTopo", "DynTopo",
            m => ((SculptModifierDefinition)m).DynTopo,
            (m, v) => ((SculptModifierDefinition)m).DynTopo = v,
            help: "Refine terrain triangles under sculpted areas to the Detail size, so brushes always have vertex resolution. Off = brushes only move the vertices the mesh already has."),
        ParameterDescriptor.Slider(
            "DetailSize", "Detail",
            m => ((SculptModifierDefinition)m).DetailSize,
            (m, v) => ((SculptModifierDefinition)m).DetailSize = Math.Max(v, 1e-4),
            softMin: 0.05, softMax: 2.0, decimalPlaces: 3, hardMin: 1e-4,
            help: "Target edge length for DynTopo refinement (model units). Smaller = finer sculpt detail and denser mesh. Changing it re-triangulates the sculpted region, so it does not live-scrub."),
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
