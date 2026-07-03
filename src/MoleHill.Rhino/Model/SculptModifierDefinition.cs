namespace MoleHill.Rhino.Model;

/// <summary>
/// Interactive Z-sculpting modifier. Persists a sparse world-XY displacement field (see
/// <see cref="SculptTile"/>) that the build stage adds onto whatever mesh arrives from the stage
/// below — so sculpt strokes survive upstream re-triangulation and multiple sculpt modifiers stack.
/// Brush settings (radius, strength, falloff) are session preferences, deliberately NOT stored here:
/// the stage fingerprint serializes this whole definition, and UI-only state would invalidate the
/// build cache on every tweak.
/// </summary>
public sealed class SculptModifierDefinition : ModifierDefinition
{
    /// <summary>When true, the build stage refines terrain triangles under the sculpted region to
    /// <see cref="DetailSize"/> before displacing, so brushes always have vertex resolution.</summary>
    public bool DynTopo { get; set; } = true;

    /// <summary>DynTopo target edge length in model units.</summary>
    public double DetailSize { get; set; } = 0.25;

    /// <summary>
    /// Displacement-field sample spacing. 0 = automatic (DetailSize / 2) until the first stroke is
    /// committed, at which point the session pins the actual value here. Pinning is load-bearing:
    /// tile payloads store no spacing of their own, so reinterpreting them at a different cell size
    /// (e.g. because DetailSize changed) would rescale the whole sculpt toward the world origin.
    /// </summary>
    public double CellSize { get; set; }

    /// <summary>Tile payload format version. 1 = base64(deflate(64x64 little-endian float32)).</summary>
    public int FieldVersion { get; set; } = 1;

    public List<SculptTile> Tiles { get; set; } = new();

    public SculptModifierDefinition()
    {
        Label = "Sculpt";
    }

    /// <summary>Field sample spacing actually used. A pinned CellSize always wins — existing tiles
    /// must never be reinterpreted at a different spacing (see <see cref="CellSize"/>).</summary>
    public double EffectiveCellSize => CellSize > 0 ? CellSize : DetailSize * 0.5;

    public override IEnumerable<SourceReferenceSet> EnumerateSourceSets()
    {
        yield break;
    }
}

/// <summary>One 64x64-sample tile of the sculpt displacement field at tile grid index (I, J).
/// <see cref="D"/> is the base64-wrapped payload in the <see cref="SculptModifierDefinition.FieldVersion"/> format.</summary>
public sealed class SculptTile
{
    public int I { get; set; }

    public int J { get; set; }

    public string D { get; set; } = string.Empty;
}
