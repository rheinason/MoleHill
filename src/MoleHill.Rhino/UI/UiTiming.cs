namespace MoleHill.Rhino.UI;

internal static class UiTiming
{
    public const double NumericCommitSeconds = 0.25;
    public const double SliderCommitSeconds = 0.12;

    /// <summary>
    /// Spacing between commits while a live-scrub slider is being dragged. Just under
    /// <c>TerrainDebouncePolicy.MinIntervalMs</c>, so the build scheduler, not the slider, sets the frame
    /// rate: it rate-limits dispatch to the last build's duration and lets an overtaken build finish.
    /// </summary>
    public const double SliderStreamSeconds = 0.05;
    public const double ToleranceCommitSeconds = 0.25;

    /// <summary>How often an open panel checks whether Rhino's theme has changed under it.</summary>
    public const double ThemeWatchSeconds = 2.0;
}
