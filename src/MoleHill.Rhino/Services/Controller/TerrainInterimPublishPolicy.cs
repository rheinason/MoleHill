namespace MoleHill.Rhino.Services;

/// <summary>
/// Whether a build should show its finished terrain mesh before its dependent outputs settle.
///
/// `TerrainBuildService.Build` assigns `PrimaryMesh` before the final-only output stages but returns
/// only after all of them, so the mesh a user is waiting for can sit finished behind work that merely
/// describes it. Measured 2026-09-19 on a 244k-face terrain with drainage analyses enabled: the mesh
/// was ready at 698 ms and the user waited 7,464 ms, with Ponding and Catchments alone accounting for
/// 5.1 s.
///
/// Publishing early is not free - it copies the mesh so later stages cannot mutate what is already on
/// screen, costs an extra redraw, and puts visibly stale outputs beside fresh geometry. On the small
/// fixture the dependent outputs were 0.5 ms, where all of that would be pure loss. So it is gated on
/// the previous build having actually spent meaningful time on outputs.
/// </summary>
internal static class TerrainInterimPublishPolicy
{
    /// <summary>
    /// The dependent-output cost, in milliseconds, above which showing the mesh early pays for the copy
    /// and the extra redraw. Below roughly this, the outputs land within a frame or two of the geometry
    /// and an interim publication would only make the drawing flicker between stale and fresh.
    /// </summary>
    public const int MinDependentOutputsMs = 150;

    /// <summary>
    /// Decides from the *peak* output cost seen for this terrain, not the last one. Keying on the last
    /// build is wrong in a way that shows up immediately: a build whose analyses all hit the stage cache
    /// measures ~3 ms, which would switch early publication off again right before the next expensive
    /// edit - observed live on 2026-09-19. The costs are also asymmetric. A wrong "yes" buys one mesh
    /// copy and one redraw, about 6 ms measured; a wrong "no" costs the user seconds of staring at the
    /// previous terrain. So bias toward publishing.
    ///
    /// A null duration means no final build has completed for this terrain in this session, so there is
    /// nothing to show early against and no measurement to justify it - the first build publishes once,
    /// as before.
    /// </summary>
    public static bool ShouldPublishGeometryEarly(
        TerrainBuildMode mode,
        TimeSpan? peakDependentOutputsDuration,
        bool hasPreviousDisplayState)
    {
        if (mode != TerrainBuildMode.Final || !hasPreviousDisplayState)
            return false;

        return peakDependentOutputsDuration is { } peak &&
               peak.TotalMilliseconds >= MinDependentOutputsMs;
    }
}
