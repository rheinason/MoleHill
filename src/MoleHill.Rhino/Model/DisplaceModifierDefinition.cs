namespace MoleHill.Rhino.Model;

/// <summary>
/// Displace: vertical offsets added to the incoming terrain from procedural noise or a greyscale image,
/// laid out in world XY (never by vertex index or UV), so the edit stacks and survives a Remesh or Retopo
/// below it. Breaklines, wall rails, graded edges, walls and Protect curves keep their height; the offset
/// fades back in over <see cref="Feather"/> from them and from the drawn <see cref="Boundaries"/>.
/// </summary>
public sealed class DisplaceModifierDefinition : ModifierDefinition
{
    /// <summary><c>"noise"</c> or <c>"image"</c> (<see cref="DisplaceSources"/>).</summary>
    public string Source { get; set; } = DisplaceSources.Noise;

    /// <summary>Closed curves scoping the edit; empty means the whole terrain.</summary>
    public SourceReferenceSet Boundaries { get; set; } = new();

    /// <summary>Curves whose height the edit leaves alone, beside the breaklines it always holds.</summary>
    public SourceReferenceSet Protect { get; set; } = new();

    /// <summary>Plan distance over which the offset fades in from a boundary or a held line.</summary>
    [ModelLength]
    public double Feather { get; set; }

    /// <summary>Keep the terrain's outer edge at its height, fading in from it like a held line.</summary>
    public bool HoldBorder { get; set; } = true;

    /// <summary>
    /// The full range of the offset. Noise: lowest to highest, every offset within ±Height/2. Image: black
    /// to white, the neutral value at zero.
    /// </summary>
    [ModelLength]
    public double Height { get; set; }

    /// <summary>Counter-clockwise rotation of the pattern about its origin.</summary>
    [UnitFree("An angle in degrees; angles do not change with model units.")]
    public double Rotation { get; set; }

    /// <summary>World X of the pattern's origin (the image's lower-left corner).</summary>
    [ModelLength]
    public double OriginX { get; set; }

    /// <summary>World Y of the pattern's origin (the image's lower-left corner).</summary>
    [ModelLength]
    public double OriginY { get; set; }

    // ---- Noise ----

    /// <summary>Plan size of one bump of the coarsest octave.</summary>
    [ModelLength]
    public double NoiseSize { get; set; }

    public int Octaves { get; set; } = 4;

    [UnitFree("Each octave's weight relative to the one before, 0 to 1; unitless.")]
    public double Roughness { get; set; } = 0.5;

    public int Seed { get; set; } = 1;

    // ---- Image ----

    /// <summary>Full path of a greyscale image; its file is fingerprinted, so editing it rebuilds.</summary>
    public string ImagePath { get; set; } = string.Empty;

    /// <summary>
    /// An optional closed rectangle that places the image: its start point is the image's bottom-left
    /// corner, its bottom edge runs counter-clockwise from there (<c>MappingRectangle</c>), and its sides are
    /// the tile's width and height. When set it overrides <see cref="OriginX"/>, <see cref="OriginY"/>,
    /// <see cref="TileSizeX"/>, <see cref="TileSizeY"/> and <see cref="Rotation"/>, which are kept, unused,
    /// for when it is cleared. Image source only.
    /// </summary>
    public SourceReferenceSet Mapping { get; set; } = new();

    /// <summary>Plan width of one image along its own X axis.</summary>
    [ModelLength]
    public double TileSizeX { get; set; }

    /// <summary>Plan height of one image along its own Y axis.</summary>
    [ModelLength]
    public double TileSizeY { get; set; }

    /// <summary>The image value, 0 (black) to 1 (white), that means no offset.</summary>
    [UnitFree("An image value from 0 (black) to 1 (white); unitless.")]
    public double Neutral { get; set; } = 0.5;

    /// <summary><c>"repeat"</c>, <c>"clamp"</c> or <c>"once"</c> (<see cref="DisplaceSources"/>).</summary>
    public string Wrap { get; set; } = DisplaceSources.Repeat;

    /// <summary>
    /// Draw the image tiles, origin and top edge in the viewport. A build input on purpose: the frames are a
    /// guide the stage emits, so toggling re-runs this one stage (its output mesh is unchanged, so nothing
    /// below re-runs).
    /// </summary>
    public bool ShowMapping { get; set; } = true;

    public DisplaceModifierDefinition()
    {
        Label = "Displace";
    }

    public override IEnumerable<SourceReferenceSet> EnumerateSourceSets()
    {
        yield return Boundaries;
        yield return Protect;
        yield return Mapping;
    }

    public override void NormalizeAfterLoad()
    {
        base.NormalizeAfterLoad();
        Boundaries ??= new SourceReferenceSet();
        Protect ??= new SourceReferenceSet();
        Mapping ??= new SourceReferenceSet();
        Source ??= DisplaceSources.Noise;
        Wrap ??= DisplaceSources.Repeat;
        ImagePath ??= string.Empty;
        Feather = Math.Max(0.0, Feather);
    }
}
