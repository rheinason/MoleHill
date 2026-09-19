namespace MoleHill.Rhino.Services;

/// <summary>
/// How long an edit waits before its rebuild is dispatched.
///
/// The delay exists to coalesce a gesture - a slider drag emitting a value every 16-40 ms must not
/// start a rebuild per sample. It does **not** exist to slow down an isolated edit, and the original
/// flat 500 ms trailing delay did exactly that: measured 2026-09-19, a fully cached rebuild of the
/// trailer-ramp fixture spent 3 ms computing and 530 ms waiting, so nothing could ever feel immediate
/// no matter how fast the build got.
///
/// So this is a **leading-edge** rule, not a trailing one. The first edit after a quiet period
/// dispatches immediately; further edits are rate-limited to one dispatch per interval, and the
/// interval is the cost of the build being repeated. An isolated edit therefore waits nothing at all,
/// while a drag still collapses into as few rebuilds as the build cost justifies.
///
/// The rate limit, not a fixed delay, is also what keeps this safe: a terrain whose rebuild takes
/// 400 ms dispatches at most every 400 ms during a drag, exactly as the old constant intended, while a
/// terrain that rebuilds in 3 ms is free to keep up with the pointer.
/// </summary>
internal static class TerrainDebouncePolicy
{
    /// <summary>
    /// Upper bound on the interval between dispatches during a gesture. The historical flat delay; a
    /// slow terrain is throttled to this and no more, so nothing regresses against the old behaviour.
    /// </summary>
    public const int MaxIntervalMs = 500;

    /// <summary>
    /// Lower bound on that interval. A slider drag emits roughly every 16-40 ms, so this has to sit
    /// above that spacing or each sample would dispatch its own rebuild.
    /// </summary>
    public const int MinIntervalMs = 60;

    /// <summary>
    /// The minimum spacing between two dispatches for one terrain, taken from the previous final
    /// build's worker duration. A null duration means nothing has been measured yet, so this falls back
    /// to the old constant rather than guessing.
    /// </summary>
    public static int ResolveIntervalMs(TimeSpan? lastFinalDuration)
    {
        if (lastFinalDuration is not { } last)
            return MaxIntervalMs;

        double milliseconds = last.TotalMilliseconds;
        if (double.IsNaN(milliseconds) || milliseconds <= MinIntervalMs)
            return MinIntervalMs;

        return milliseconds >= MaxIntervalMs ? MaxIntervalMs : (int)Math.Round(milliseconds);
    }

    /// <summary>
    /// How long this edit should wait before being dispatched.
    ///
    /// Zero when the terrain has not dispatched recently - the isolated-edit case, which is most edits
    /// and the only one that can ever feel immediate. Otherwise the remainder of the interval since the
    /// last dispatch, which is what collapses a drag.
    /// </summary>
    /// <param name="lastFinalDuration">Previous final build's worker duration, or null if unmeasured.</param>
    /// <param name="sinceLastDispatch">Time since this terrain last dispatched a build, or null if it never has.</param>
    public static int ResolveDelayMs(TimeSpan? lastFinalDuration, TimeSpan? sinceLastDispatch)
    {
        if (sinceLastDispatch is not { } since)
            return 0;

        int intervalMs = ResolveIntervalMs(lastFinalDuration);
        double elapsed = since.TotalMilliseconds;
        if (double.IsNaN(elapsed) || elapsed >= intervalMs)
            return 0;

        return (int)Math.Ceiling(intervalMs - elapsed);
    }
}
