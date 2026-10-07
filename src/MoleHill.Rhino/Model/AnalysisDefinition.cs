using MoleHill.Core.Analysis;

namespace MoleHill.Rhino.Model;

// JSON polymorphism is registry-driven (Services/TerrainJsonTypeResolver reads AnalysisTypeRegistry),
// not [JsonDerivedType] — registering an AnalysisTypeDescriptor is enough. Discriminators are unchanged.
public abstract class AnalysisDefinition : ITerrainContentItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Label { get; set; } = "Analysis";

    public bool IsEnabled { get; set; } = true;

    public int SchemaVersion { get; set; } = 2;

    public string PalettePreset { get; set; } = ColorRampPresets.DefaultKey;

    /// <summary>
    /// Per-stop overrides from the ramp editor. Empty means "use <see cref="PalettePreset"/> verbatim",
    /// which is what every pre-ramp-editor document says, so those keep drawing exactly as before.
    /// </summary>
    public List<AnalysisColorStopState> PaletteStops { get; set; } = new();

    [UnitFree("A slope or aspect colour range is unitless; Elevation and Cut/Fill opt in with [ModelLengthMembers].")]
    public double RangeLow { get; set; }

    [UnitFree("A slope or aspect colour range is unitless; Elevation and Cut/Fill opt in with [ModelLengthMembers].")]
    public double RangeHigh { get; set; }

    /// <summary>Whether the preview interpolates the palette or classifies values into bands.</summary>
    public AnalysisColorMapper.Mode ColorMode { get; set; } = AnalysisColorMapper.Mode.Gradient;

    /// <summary>Width of a stepped color band in the analysis unit. Zero selects an automatic step.</summary>
    [UnitFree("A slope or aspect colour range is unitless; Elevation and Cut/Fill opt in with [ModelLengthMembers].")]
    public double ColorInterval { get; set; }

    /// <summary>When true, the display range is derived from the latest analysis result.</summary>
    public bool AutoColorRange { get; set; } = true;

    /// <summary>
    /// The ramp this analysis is coloured with. Every path that paints — the preview mesh, the sculpt
    /// colorizer, the panel card — resolves through here, so the legend and the mesh cannot drift apart
    /// (the same invariant <see cref="AnalysisRange"/> keeps for the range).
    /// </summary>
    public ColorRamp ResolveRamp()
    {
        if (PaletteStops.Count == 0)
            return ColorRampPresets.ResolveRamp(PalettePreset);

        var stops = new SlopeAnalyzer.ColorStop[PaletteStops.Count];
        for (int i = 0; i < PaletteStops.Count; i++)
        {
            var state = PaletteStops[i];
            stops[i] = new SlopeAnalyzer.ColorStop(
                state.Position,
                (byte)((state.ColorArgb >> 16) & 0xFF),
                (byte)((state.ColorArgb >> 8) & 0xFF),
                (byte)(state.ColorArgb & 0xFF));
        }

        return new ColorRamp(stops);
    }

    /// <summary>
    /// Stores an edited ramp. Writing the stops always sets the override, including when they happen to
    /// match a preset: the user edited this ramp, and a later change to the preset's definition must not
    /// silently rewrite their work.
    /// </summary>
    public void SetRamp(ColorRamp ramp)
    {
        var stops = new List<AnalysisColorStopState>(ramp.Count);
        foreach (var stop in ramp.Stops)
        {
            stops.Add(new AnalysisColorStopState
            {
                Position = stop.Position,
                ColorArgb = unchecked((int)0xFF000000) | (stop.R << 16) | (stop.G << 8) | stop.B
            });
        }

        PaletteStops = stops;
    }

    /// <summary>Drops the override so the named preset drives the ramp again.</summary>
    public void ApplyPreset(string presetKey)
    {
        PalettePreset = ColorRampPresets.Resolve(presetKey).Key;
        PaletteStops.Clear();
    }

    public virtual IEnumerable<SourceReferenceSet> EnumerateSourceSets()
    {
        yield break;
    }

    /// <summary>
    /// Load-time normalization for this definition's own fields (null-coalescing, clamps), run once after
    /// deserialization and after the schema migrations. The palette fields every analysis shares are
    /// normalized by the serializer, not here. Not version-gated; the default does nothing.
    /// </summary>
    public virtual void NormalizeAfterLoad()
    {
    }
}
