namespace MoleHill.Core.Analysis;

/// <summary>
/// Colours for things that are *categories*, not measurements — drainage basins being the first.
/// </summary>
/// <remarks>
/// Deliberately not a <see cref="ColorRamp"/>. A ramp maps a position on a continuum, so neighbouring
/// colours mean neighbouring values and a legend can name the ends. A basin index means nothing of the
/// sort: basin 4 is not "more" than basin 3, and stretching a ramp across the indices produces a picture
/// whose colours carry a relationship the data does not have. What a categorical map owes the reader is
/// the opposite property — that two adjacent regions never look alike — so the hues are spaced around
/// the wheel by a large step and cycle, and the sequence is fixed rather than fitted to the count.
/// </remarks>
public static class CategoricalPalette
{
    /// <summary>
    /// Twelve hues at full separation, mid-saturation so a mesh tinted with them still reads as ground.
    /// The order is chosen so consecutive entries contrast, which is what matters when adjacent basins
    /// usually get consecutive indices.
    /// </summary>
    private static readonly (byte R, byte G, byte B)[] Colors =
    {
        (0x42, 0x85, 0xF4), // blue
        (0xEA, 0x43, 0x35), // red
        (0x34, 0xA8, 0x53), // green
        (0xFB, 0xBC, 0x05), // amber
        (0x9C, 0x27, 0xB0), // purple
        (0x00, 0xAC, 0xC1), // cyan
        (0xFF, 0x70, 0x43), // deep orange
        (0x7C, 0xB3, 0x42), // lime
        (0xEC, 0x40, 0x7A), // pink
        (0x5C, 0x6B, 0xC0), // indigo
        (0x00, 0x89, 0x7B), // teal
        (0x8D, 0x6E, 0x63)  // brown
    };

    public static int Count => Colors.Length;

    /// <summary>
    /// Colour for a category index. Cycles, so any index is valid; a negative index — "not classified" —
    /// returns the neutral grey, which must not be mistaken for a category that happens to be grey.
    /// </summary>
    public static (byte R, byte G, byte B) ColorAt(int index)
    {
        if (index < 0)
            return (130, 130, 130);

        return Colors[index % Colors.Length];
    }
}
