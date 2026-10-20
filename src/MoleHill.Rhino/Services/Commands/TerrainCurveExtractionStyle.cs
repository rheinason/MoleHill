using System.Drawing;

namespace MoleHill.Rhino.Services;

/// <summary>
/// One layer name, colour and preview weight per extraction output, shared by the floating preview and the
/// kept layer tree so the two cannot drift: walls, kerbs, buildings and spot heights each previewed in another
/// colour than they were kept in. Soft outlines are ochre, not green, so they never read as contours; paved
/// outlines, which carry the paving as breaklines, draw heaviest.
/// </summary>
internal static class TerrainCurveExtractionStyle
{
    /// <summary><paramref name="Weight"/> is the preview line thickness, or the point size for points.</summary>
    public readonly record struct Style(string Layer, Color Colour, int Weight);

    public static readonly Style Border = new("Border", Color.FromArgb(35, 31, 32), 1);
    public static readonly Style RidgesValleys = new("Ridges and valleys", Color.FromArgb(178, 34, 34), 2);
    public static readonly Style Contours = new("Contours", Color.ForestGreen, 2);
    public static readonly Style Spots = new("Spot heights", Color.FromArgb(25, 157, 73), 6);
    public static readonly Style PavedAreas = new("Paved areas", Color.FromArgb(60, 70, 95), 3);
    public static readonly Style SoftAreas = new("Soft areas", Color.FromArgb(190, 140, 20), 2);
    /// <summary>Flat area fills beneath the mesh for zone input; lighter than the outlines they fill.</summary>
    public static readonly Style PavedZones = new("Paved zones", Color.FromArgb(165, 172, 190), 1);
    public static readonly Style SoftZones = new("Soft zones", Color.FromArgb(228, 205, 140), 1);
    public static readonly Style Walls = new("Walls and edges", Color.FromArgb(110, 85, 65), 3);
    public static readonly Style Kerbs = new("Kerbs", Color.FromArgb(160, 95, 35), 2);
    public static readonly Style PavingEdges = new("Paving edges", Color.FromArgb(160, 95, 35), 2);
    public static readonly Style Buildings = new("Buildings", Color.FromArgb(170, 135, 110), 1);
    public static readonly Style Ground = new("Ground (check)", Color.FromArgb(160, 160, 160), 1);
}
