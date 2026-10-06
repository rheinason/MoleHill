using MoleHill.Rhino.Model;
using Rhino;

namespace MoleHill.Rhino.Services;

/// <summary>
/// The document's north, in one place.
///
/// North is Rhino's, not MoleHill's: <c>mhSetSunNorth</c> writes <c>doc.Lights.Sun.North</c> and the sun
/// reads it, so an aspect analysis must use that same angle rather than introduce a second north that
/// could disagree with the shadows. Both the preview path (which holds a <see cref="RhinoDoc"/>) and the
/// build path (which holds a snapshot taken on the UI thread) resolve it through here.
/// </summary>
internal static class DocumentNorth
{
    /// <summary>North when a document cannot be reached: +Y, which is what every plan drawing assumes.</summary>
    public const double DefaultAzimuthDegrees = 90.0;

    /// <summary>
    /// The direction of north as an angle in degrees counter-clockwise from +X — the convention
    /// <c>Sun.North</c> stores and <c>mhSetSunNorth</c> writes.
    /// </summary>
    public static double AzimuthDegrees(RhinoDoc? doc)
    {
        double north = doc?.Lights.Sun.North ?? DefaultAzimuthDegrees;
        return double.IsFinite(north) ? north : DefaultAzimuthDegrees;
    }

    /// <summary>
    /// Terrains whose output reads north and that rebuild on their own: a live-updating terrain with an
    /// enabled aspect analysis. North lives on the document, so changing it touches no definition and
    /// nothing else would schedule their rebuild. A terrain with live update off keeps its manual contract
    /// and picks the new north up on its next Rebuild.
    /// </summary>
    public static IReadOnlyList<Guid> TerrainsToRefreshAfterChange(IEnumerable<TerrainDefinition> terrains) =>
        terrains
            .Where(terrain => terrain.LiveUpdateEnabled &&
                              terrain.Analyses.Any(analysis => analysis is AspectAnalysisDefinition { IsEnabled: true }))
            .Select(terrain => terrain.TerrainId)
            .ToList();
}
