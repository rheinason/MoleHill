namespace MoleHill.Rhino.Services;

internal enum TerrainDispatchOutcome
{
    /// <summary>Start the build and drop the request.</summary>
    Dispatch,

    /// <summary>Leave the request queued: an active sculpt stroke owns the preview mesh.</summary>
    DeferForSculpt,

    /// <summary>The document or terrain is gone; drop the terrain's rebuild state.</summary>
    DropTargetGone,

    /// <summary>Leave the request queued: the terrain is still building.</summary>
    DeferBusy,

    /// <summary>The user declined this version at the slow-build warning; drop the request.</summary>
    DropSkipped
}

internal enum TerrainCompletionOutcome
{
    /// <summary>A reset or newer worker took over; the result is not this terrain's current build.</summary>
    DiscardStaleGeneration,

    /// <summary>A newer request overtook a build that ran to completion: try to show it as a preview frame.</summary>
    SupersededMayPublish,

    /// <summary>A newer request overtook a build that was cancelled or failed.</summary>
    SupersededDiscard,

    Cancelled,
    Failed,
    Apply
}

/// <summary>
/// The scheduler's decisions, free of Rhino, clocks and shared state, so the event sequences the
/// controller feeds them can be tested deterministically. The controller keeps the effects: it reads
/// state, asks here what to do, and does it. Extracted one transition at a time from the
/// <c>TerrainController</c> partials; see the 2026-10-07 review, F06.
/// </summary>
internal static class TerrainBuildSchedulingPolicy
{
    /// <summary>
    /// Index of the request to dispatch next, or -1. The oldest due request wins; on a tie a Preview goes
    /// before a Final; any remaining tie keeps the list's order.
    /// </summary>
    public static int SelectDue(IReadOnlyList<(DateTime DueAtUtc, TerrainBuildMode Mode)> requests, DateTime nowUtc)
    {
        int best = -1;
        for (int index = 0; index < requests.Count; index++)
        {
            var candidate = requests[index];
            if (candidate.DueAtUtc > nowUtc)
                continue;

            if (best < 0 || IsBefore(candidate, requests[best]))
                best = index;
        }

        return best;
    }

    private static bool IsBefore((DateTime DueAtUtc, TerrainBuildMode Mode) a, (DateTime DueAtUtc, TerrainBuildMode Mode) b)
    {
        if (a.DueAtUtc != b.DueAtUtc)
            return a.DueAtUtc < b.DueAtUtc;
        return a.Mode == TerrainBuildMode.Preview && b.Mode != TerrainBuildMode.Preview;
    }

    /// <summary>What to do with the due request the selector picked.</summary>
    public static TerrainDispatchOutcome DecideDispatch(
        bool sculptStrokeActive,
        bool targetExists,
        bool terrainBusy,
        long skippedVersion,
        long requestVersion)
    {
        if (sculptStrokeActive)
            return TerrainDispatchOutcome.DeferForSculpt;
        if (!targetExists)
            return TerrainDispatchOutcome.DropTargetGone;
        if (terrainBusy)
            return TerrainDispatchOutcome.DeferBusy;
        return skippedVersion >= requestVersion ? TerrainDispatchOutcome.DropSkipped : TerrainDispatchOutcome.Dispatch;
    }

    /// <summary>How a finished build is to be closed out.</summary>
    public static TerrainCompletionOutcome DecideCompletion(
        long resultGeneration,
        long currentGeneration,
        long resultVersion,
        long requestedVersion,
        bool wasCanceled,
        bool hasError,
        bool hasBuild)
    {
        if (resultGeneration != currentGeneration)
            return TerrainCompletionOutcome.DiscardStaleGeneration;

        if (requestedVersion > resultVersion)
        {
            return !wasCanceled && !hasError && hasBuild
                ? TerrainCompletionOutcome.SupersededMayPublish
                : TerrainCompletionOutcome.SupersededDiscard;
        }

        if (wasCanceled)
            return TerrainCompletionOutcome.Cancelled;
        return hasError || !hasBuild ? TerrainCompletionOutcome.Failed : TerrainCompletionOutcome.Apply;
    }
}
