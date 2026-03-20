using MoleHill.Core.Analysis;

namespace MoleHill.Rhino.Services;

internal sealed class SlopePreviewPalette
{
    public required string Key { get; init; }

    public required string Label { get; init; }

    public required SlopeAnalyzer.ColorStop[] Stops { get; init; }
}

internal static class SlopePreviewPaletteCatalog
{
    public const string DefaultKey = "terrain-spectrum";

    private static readonly SlopePreviewPalette[] Palettes =
    {
        new()
        {
            Key = DefaultKey,
            Label = "Terrain Spectrum",
            Stops = new SlopeAnalyzer.ColorStop[]
            {
                new(0.00, 44, 123, 182),
                new(0.20, 42, 181, 125),
                new(0.45, 175, 221, 66),
                new(0.68, 253, 174, 50),
                new(1.00, 215, 25, 28)
            }
        },
        new()
        {
            Key = "viridis",
            Label = "Viridis",
            Stops = new SlopeAnalyzer.ColorStop[]
            {
                new(0.00, 68, 1, 84),
                new(0.25, 59, 82, 139),
                new(0.50, 33, 145, 140),
                new(0.75, 94, 201, 98),
                new(1.00, 253, 231, 37)
            }
        },
        new()
        {
            Key = "magma",
            Label = "Magma",
            Stops = new SlopeAnalyzer.ColorStop[]
            {
                new(0.00, 0, 0, 4),
                new(0.25, 81, 18, 124),
                new(0.50, 183, 55, 121),
                new(0.75, 251, 140, 60),
                new(1.00, 252, 253, 191)
            }
        },
        new()
        {
            Key = "cool-warm",
            Label = "Cool Warm",
            Stops = new SlopeAnalyzer.ColorStop[]
            {
                new(0.00, 49, 54, 149),
                new(0.35, 69, 117, 180),
                new(0.50, 224, 243, 248),
                new(0.70, 244, 165, 130),
                new(1.00, 165, 0, 38)
            }
        }
    };

    public static IReadOnlyList<SlopePreviewPalette> All => Palettes;

    public static SlopePreviewPalette Resolve(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return Palettes[0];

        return Palettes.FirstOrDefault(
                   palette => string.Equals(palette.Key, key, StringComparison.OrdinalIgnoreCase))
               ?? Palettes[0];
    }
}
