namespace MoleHill.Rhino.Services;

/// <summary>
/// What a terrain's build is doing right now, for the collapsed Status header: whether one is running or
/// waiting, which pipeline stage it is in, and for how long. Null from the controller when the terrain is
/// idle.
/// </summary>
internal sealed record TerrainBuildActivity(
    bool IsRunning,
    TerrainBuildMode Mode,
    string? Stage,
    int? Step,
    int? StepCount,
    TimeSpan Elapsed)
{
    /// <summary>"Building · Remesh (3 of 9) · 12 s", or "Rebuild queued" before a build starts.</summary>
    public string Format()
    {
        if (!IsRunning)
            return "Rebuild queued";

        string verb = Mode == TerrainBuildMode.Preview ? "Previewing" : "Building";
        string stage = string.IsNullOrWhiteSpace(Stage)
            ? string.Empty
            : Step is { } step && StepCount is { } count && count > 0
                ? $" · {Stage} ({Math.Min(step, count)} of {count})"
                : $" · {Stage}";
        return $"{verb}{stage} · {FormatElapsed(Elapsed)}";
    }

    internal static string FormatElapsed(TimeSpan elapsed) => elapsed.TotalMinutes >= 1.0
        ? $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}"
        : $"{Math.Max(0, (int)elapsed.TotalSeconds)} s";
}
