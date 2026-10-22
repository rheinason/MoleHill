using System.Drawing;

namespace MoleHill.Rhino.Services;

/// <summary>
/// One name, layer, colour and preview weight per extraction output, shared by the floating preview and the
/// kept layer tree so the two cannot drift: walls, kerbs, buildings and spot heights each previewed in another
/// colour than they were kept in. Soft outlines are ochre, not green, so they never read as contours; paved
/// outlines, which carry the paving as breaklines, draw heaviest.
/// <para><b>Layers are the terrain's own input tree</b> (<see cref="Model.TerrainLayerNaming.InputRoot"/>): every
/// line a terrain must keep is on <c>Input::Breaklines</c>, whatever found it, so "which layer is breaklines" has one
/// answer and a terrain of the same name reads the output without reassigning anything. The colour is the
/// object's, so walls, kerbs and paved edges stay distinguishable on that one layer. What only describes the site
/// (buildings, soft outlines, the check mesh) goes under <c>Context</c>, which no terrain source reads.</para>
/// </summary>
internal static class TerrainCurveExtractionStyle
{
    /// <summary><paramref name="Layer"/> is relative to the terrain root (<c>Input::Breaklines</c>);
    /// <paramref name="Weight"/> is the preview line thickness, or the point size for points.</summary>
    public readonly record struct Style(string Name, string Layer, Color Colour, int Weight);

    public const string Breaklines = "Input::Breaklines";

    public static readonly Style Border = new("Border", "Input::Boundary", Color.FromArgb(35, 31, 32), 1);
    /// <summary>The terrain's own folds (batter crests and toes, pad edges, wall tops and toes), held as breaklines.</summary>
    public static readonly Style Creases = new("Crease", Breaklines, Color.FromArgb(178, 34, 34), 2);
    public static readonly Style Contours = new("Contour", "Input::Contours", Color.ForestGreen, 2);
    public static readonly Style Spots = new("Spot", "Input::Spots", Color.FromArgb(25, 157, 73), 6);
    public static readonly Style PavedAreas = new("Paved outline", Breaklines, Color.FromArgb(60, 70, 95), 3);
    public static readonly Style SoftAreas = new("Soft outline", "Context::Soft outlines", Color.FromArgb(190, 140, 20), 2);
    /// <summary>Flat area fills beneath the mesh for zone input; lighter than the outlines they fill.</summary>
    public static readonly Style PavedZones = new("Paved area", "Input::Zones::Paved", Color.FromArgb(165, 172, 190), 1);
    public static readonly Style SoftZones = new("Soft area", "Input::Zones::Soft", Color.FromArgb(228, 205, 140), 1);
    public static readonly Style Walls = new("Wall", Breaklines, Color.FromArgb(110, 85, 65), 3);
    public static readonly Style Kerbs = new("Kerb", Breaklines, Color.FromArgb(160, 95, 35), 2);
    public static readonly Style PavingEdges = new("Paving edge", Breaklines, Color.FromArgb(160, 95, 35), 2);
    public static readonly Style Buildings = new("Building", "Context::Buildings", Color.FromArgb(170, 135, 110), 1);
    public static readonly Style Ground = new("Scan ground", "Context::Ground check", Color.FromArgb(160, 160, 160), 1);
}
