namespace MoleHill.Rhino.Services;

/// <summary>
/// When to stop and ask before a rebuild. The answer is a modal dialog, so a wrong "yes" interrupts every
/// edit: measured 2026-10-01, a 100,911-face terrain with one retaining wall asked before **every** rail edit
/// although its last rebuild took 0.07 s, because the old rule warned on face count alone (40,000 for a final
/// build) whenever a grading card was enabled, whatever the terrain had just measured.
///
/// So a measured duration decides. Once a build of this mode has run, warn only when it was actually slow.
/// Before anything is measured (a freshly opened document) the face count is the only evidence, and its
/// threshold reflects what builds cost now: that terrain built cold in 0.57 s, and the 1.6 million face
/// park probe builds its whole stack in about 25 s.
/// </summary>
internal static class TerrainSlowBuildWarningPolicy
{
    public const double PreviewThresholdSeconds = 1.5;
    public const double FinalThresholdSeconds = 5.0;

    /// <summary>Face count above which an unmeasured preview with expensive cards warns.</summary>
    public const int UnmeasuredPreviewFaceThreshold = 250_000;

    /// <summary>Face count above which an unmeasured final build with expensive cards warns.</summary>
    public const int UnmeasuredFinalFaceThreshold = 1_000_000;

    public static bool ShouldWarn(
        bool warningsEnabled,
        TerrainBuildMode mode,
        TimeSpan? lastDuration,
        int? displayedFaceCount,
        int displayedVertexCount,
        bool hasExpensiveModifier,
        out string? warning)
    {
        warning = null;
        if (!warningsEnabled)
            return false;

        bool preview = mode == TerrainBuildMode.Preview;
        if (lastDuration is { } measured)
        {
            if (measured.TotalSeconds < (preview ? PreviewThresholdSeconds : FinalThresholdSeconds))
                return false;

            warning = preview
                ? $"The last preview for this terrain took {measured.TotalSeconds:0.##} s. Continue with another live preview?"
                : $"The last exact rebuild for this terrain took {measured.TotalSeconds:0.##} s. Continue with another full rebuild?";
            return true;
        }

        if (displayedFaceCount is not { } faces || !hasExpensiveModifier ||
            faces < (preview ? UnmeasuredPreviewFaceThreshold : UnmeasuredFinalFaceThreshold))
            return false;

        warning = preview
            ? $"This terrain currently has {displayedVertexCount:N0} verts and {faces:N0} faces with expensive live modifiers enabled. Preview may take a while. Continue?"
            : $"This terrain currently has {displayedVertexCount:N0} verts and {faces:N0} faces with expensive modifiers enabled. The exact rebuild may take a while. Continue?";
        return true;
    }
}
