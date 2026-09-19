namespace MoleHill.Rhino.Services;

/// <summary>
/// How long an edit waits before its rebuild is dispatched.
///
/// This was a flat 500 ms on every edit, which is most of the wait on any terrain that rebuilds
/// quickly: measured 2026-09-19, a fully cached rebuild of the trailer-ramp fixture spent 3 ms
/// computing and 530 ms in debounce. The delay exists to coalesce a drag into one rebuild, so the
/// useful scale is the cost of the rebuild being avoided, not a constant.
///
/// The rule is therefore "wait about as long as the last build took", clamped. The lower bound stays
/// above a drag's event spacing so a drag still coalesces instead of queueing a rebuild per sample;
/// the upper bound is the old constant, so no terrain ever waits longer than it does today and slow
/// terrains keep exactly their current protection against discarded work.
/// </summary>
internal static class TerrainDebouncePolicy
{
    /// <summary>The historical flat delay, still used when nothing has been measured yet.</summary>
    public const int MaxFinalDebounceMs = 500;

    /// <summary>
    /// Floor, in milliseconds. A slider drag emits roughly every 16-40 ms, so this has to sit above
    /// that spacing or each sample would dispatch its own rebuild.
    /// </summary>
    public const int MinFinalDebounceMs = 60;

    /// <summary>
    /// Resolves the debounce for the next edit from the previous final build's worker duration.
    /// A null duration means this terrain has not completed a final build in this session - the first
    /// build after a document load, say - so it falls back to the old constant rather than guessing.
    /// </summary>
    public static int ResolveFinalDebounceMs(TimeSpan? lastFinalDuration)
    {
        if (lastFinalDuration is not { } last)
            return MaxFinalDebounceMs;

        double milliseconds = last.TotalMilliseconds;
        if (double.IsNaN(milliseconds) || milliseconds <= MinFinalDebounceMs)
            return MinFinalDebounceMs;

        return milliseconds >= MaxFinalDebounceMs
            ? MaxFinalDebounceMs
            : (int)Math.Round(milliseconds);
    }
}
