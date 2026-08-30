namespace MoleHill.Core.Analysis;

/// <summary>
/// The built-in colour ramps, in Core so the Rhino panel, the analysis preview and the Grasshopper
/// components all name the same ramps by the same keys.
///
/// The keys are a persistence contract: <c>AnalysisDefinition.PalettePreset</c> stores one, so renaming or
/// removing a key silently changes how an existing document draws. Add freely; never rename.
/// </summary>
public static class ColorRampPresets
{
    public const string DefaultKey = "terrain-spectrum";

    private static readonly ColorRampPreset[] Presets =
    {
        Build("terrain-spectrum", "Terrain Spectrum", new SlopeAnalyzer.ColorStop[]
        {
            new(0.00, 44, 123, 182),
            new(0.20, 42, 181, 125),
            new(0.45, 175, 221, 66),
            new(0.68, 253, 174, 50),
            new(1.00, 215, 25, 28)
        }),
        Build("viridis", "Viridis", Even(
            (68, 1, 84), (59, 82, 139), (33, 145, 140), (94, 201, 98), (253, 231, 37))),
        Build("magma", "Magma", Even(
            (0, 0, 4), (81, 18, 124), (183, 55, 121), (251, 140, 60), (252, 253, 191))),
        Build("cool-warm", "Cool Warm", new SlopeAnalyzer.ColorStop[]
        {
            new(0.00, 49, 54, 149),
            new(0.35, 69, 117, 180),
            new(0.50, 224, 243, 248),
            new(0.70, 244, 165, 130),
            new(1.00, 165, 0, 38)
        }),
        Build("turbo", "Turbo", Even(
            (48, 18, 59), (67, 144, 254), (26, 199, 194), (194, 239, 52), (250, 188, 42), (201, 41, 3))),
        Build("terrain", "Terrain", Even(
            (39, 75, 109), (63, 127, 95), (159, 191, 115), (217, 192, 122), (143, 107, 74), (242, 242, 242))),
        Build("blackbody", "Blackbody", Even(
            (0, 0, 0), (143, 29, 0), (224, 96, 0), (255, 191, 71), (255, 255, 255))),
        Build("mono", "Mono", Even(
            (20, 20, 20), (240, 240, 240)))
    };

    public static IReadOnlyList<ColorRampPreset> All => Presets;

    /// <summary>The preset for a key. Unknown or missing keys fall back to the default rather than throw —
    /// a document from a newer build must still open.</summary>
    public static ColorRampPreset Resolve(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return Presets[0];

        return Presets.FirstOrDefault(
                   preset => string.Equals(preset.Key, key, StringComparison.OrdinalIgnoreCase))
               ?? Presets[0];
    }

    /// <summary>The ramp for a key, for callers that do not need the label.</summary>
    public static ColorRamp ResolveRamp(string? key) => Resolve(key).Ramp;

    /// <summary>True when the key names a built-in, which the preset store may not overwrite.</summary>
    public static bool IsBuiltIn(string? key) =>
        !string.IsNullOrWhiteSpace(key) &&
        Presets.Any(preset => string.Equals(preset.Key, key, StringComparison.OrdinalIgnoreCase));

    private static ColorRampPreset Build(string key, string label, IReadOnlyList<SlopeAnalyzer.ColorStop> stops) =>
        new() { Key = key, Label = label, Ramp = new ColorRamp(stops) };

    /// <summary>Spreads a list of colours evenly from 0 to 1 — how most published ramps are specified.</summary>
    private static SlopeAnalyzer.ColorStop[] Even(params (byte R, byte G, byte B)[] colours)
    {
        var stops = new SlopeAnalyzer.ColorStop[colours.Length];
        double last = colours.Length - 1;
        for (int i = 0; i < colours.Length; i++)
            stops[i] = new SlopeAnalyzer.ColorStop(i / last, colours[i].R, colours[i].G, colours[i].B);

        return stops;
    }
}
