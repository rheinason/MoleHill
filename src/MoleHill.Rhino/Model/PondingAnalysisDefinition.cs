namespace MoleHill.Rhino.Model;

/// <summary>
/// Where the terrain holds water: closed depressions, how deep and how much, with the shoreline drawn at
/// the level each one overflows at.
/// </summary>
/// <remarks>
/// The one analysis in the tab that reports a mistake rather than describing the design. Everything else
/// says what was drawn; this says the ground does not drain. That is why it is worth leaving switched on,
/// and why the depth threshold matters so much — a check that reports a 2 mm numerical dimple as a pond
/// is a check people turn off, after which it catches nothing at all.
/// </remarks>
public sealed class PondingAnalysisDefinition : DrainageAnalysisDefinition
{
    /// <summary>
    /// Depressions shallower than this are not reported. A model length, because depth is the figure a
    /// reader can judge: "50 mm standing water" means something, where a volume or an area threshold
    /// would have to be re-derived for every site.
    /// </summary>
    [ModelLength]
    public double MinimumDepth { get; set; } = 0.05;

    /// <summary>Draw each pond's shoreline at the level it overflows at.</summary>
    public bool ShowOutlines { get; set; } = true;

    /// <summary>Mark where each pond overflows — the low point of the lip it crosses first.</summary>
    public bool ShowSpillPoints { get; set; } = true;

    /// <summary>Explicit colour for the shorelines. Null takes the colour from the role's layer.</summary>
    public int? OutlineColorArgb { get; set; }

    /// <summary>Explicit colour for the spill markers. Null takes the colour from the role's layer.</summary>
    public int? SpillPointColorArgb { get; set; }

    public PondingAnalysisDefinition()
    {
        Label = "Ponding";

        // Depth below the water surface, so the range runs from zero and the palette reads as "deeper".
        PalettePreset = "cool-warm";
        AutoColorRange = true;
    }
}
