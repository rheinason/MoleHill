namespace MoleHill.Rhino.Services;

/// <summary>
/// What happens to a build that a newer edit arrives on top of.
///
/// Until now the answer was the same in both directions and it made continuous input impossible:
/// <c>RequestRebuild</c> cancelled the running build on every new request, and
/// <c>CompleteBackgroundBuild</c> discarded any result whose version had been overtaken. During a
/// drag every sample therefore killed the evaluation the previous sample started, and any evaluation
/// that did survive was thrown away unpublished - so the terrain showed nothing at all until input
/// stopped, however cheap the build was. Measured 2026-09-19, a warm rail edit on a 2,694-face terrain
/// costs 16 ms against a 66 ms input-to-visible target: the work to show a frame every sample was
/// already there and was being discarded.
///
/// So this policy separates the two decisions the old code conflated:
///
/// - **Let the running build finish** when finishing it is cheaper than the cadence a gesture is
///   already rate-limited to. <see cref="TerrainDebouncePolicy"/> throttles a drag to one dispatch per
///   build duration, capped at <see cref="TerrainDebouncePolicy.MaxIntervalMs"/>, so a build at or
///   under that cap finishes within an interval the gesture was going to spend waiting anyway. Above
///   the cap the terrain would fall progressively further behind the pointer, and cancelling is right.
/// - **Publish a superseded result** as a preview frame rather than discarding it. The geometry is
///   exact for the input it was given; what it is not is *current*, which is exactly what
///   <c>IsPreview</c> means to every consumer that already refuses it - bake, the interop mesh
///   accessors and the Grasshopper bridge all go through <c>TerrainSnapshotEligibility</c>.
///
/// Publication is monotonic and age-limited. Monotonic because a slower build finishing after a faster
/// one would otherwise walk the terrain backwards on screen; age-limited because a frame old enough to
/// predate several samples is misleading rather than helpful, and because a build may have started
/// before its duration was ever measured.
/// </summary>
internal static class TerrainSupersededBuildPolicy
{
    /// <summary>
    /// Longest build that is allowed to run to completion once superseded, and the oldest result that
    /// may still be published. Both are <see cref="TerrainDebouncePolicy.MaxIntervalMs"/> because that
    /// is already the worst-case dispatch cadence a gesture accepts; a build inside it costs the
    /// gesture nothing it was not already spending.
    /// </summary>
    public const int MaxFinishableMs = TerrainDebouncePolicy.MaxIntervalMs;

    /// <summary>
    /// Whether a newer request should cancel the build already running.
    ///
    /// A terrain with no measured duration yet is cancelled as before: the first build of a session is
    /// the one most likely to be slow, and guessing it is cheap would delay the newest edit by an
    /// unbounded amount.
    /// </summary>
    /// <param name="lastFinalDuration">Previous final build's worker duration, or null if unmeasured.</param>
    public static bool ShouldCancelRunningBuild(TimeSpan? lastFinalDuration)
    {
        if (lastFinalDuration is not { } last)
            return true;

        double milliseconds = last.TotalMilliseconds;
        if (double.IsNaN(milliseconds))
            return true;

        return milliseconds > MaxFinishableMs;
    }

    /// <summary>
    /// Whether a completed build that a newer request has overtaken should still be shown as a preview
    /// frame.
    /// </summary>
    /// <param name="hasMesh">Whether the result actually carries a terrain mesh.</param>
    /// <param name="buildElapsed">The result's worker duration, used as a lower bound on its age.</param>
    /// <param name="resultVersion">The superseded result's build version.</param>
    /// <param name="displayedGeometryRevision">The version currently on screen.</param>
    public static bool ShouldPublishSupersededResult(
        bool hasMesh,
        TimeSpan buildElapsed,
        long resultVersion,
        long displayedGeometryRevision)
    {
        if (!hasMesh)
            return false;

        // Monotonic: never replace a newer frame with an older one.
        if (resultVersion <= displayedGeometryRevision)
            return false;

        double milliseconds = buildElapsed.TotalMilliseconds;
        return !double.IsNaN(milliseconds) && milliseconds <= MaxFinishableMs;
    }
}
