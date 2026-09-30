using MoleHill.Core.Engine;
using MoleHill.Core.Grading;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Windowed grading for the Grade Pad and Grade Path stages (<see cref="GradingWindows"/>): each group of
/// items is graded on the faces within its reach, and a window whose input is unchanged since the last run
/// is taken from the stage's memo. An edit to the terrain away from the items, or to one of them, re-grades
/// only the windows it touches, and the result is exactly a cold build's.
/// </summary>
internal sealed partial class TerrainBuildService
{
    private static GradingResult? GradePadsWindowed(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PadGrader.PadBoundary[] pads,
        PadGrader.LockCurve[] locks,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> hardConstraints,
        double tolerance,
        double detailSize,
        TerrainRuntimeCache runtimeCache,
        string memoKey,
        List<string> diagnostics,
        out string? warning,
        out IReadOnlyList<OutputPolyline> failureOutputPolylines,
        out IReadOnlyList<GradingDiagnostic> failureDiagnostics)
    {
        runtimeCache.GradingWindowMemos.TryGetValue(memoKey, out GradingWindows.Memo? previous);
        var next = new GradingWindows.Memo();
        GradingResult? result = PadGrader.GradeWindowed(
            vertices, vertexCount, faces, faceCount, pads, locks, hardConstraints, tolerance, detailSize,
            previous, next, diagnostics, out warning, out failureOutputPolylines, out failureDiagnostics);
        runtimeCache.GradingWindowMemos[memoKey] = next;
        return result;
    }

    private static GradingResult? GradePathsWindowed(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PathGrader.PathDefinition[] paths,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> hardConstraints,
        double tolerance,
        bool preferSplitKeep,
        TerrainRuntimeCache runtimeCache,
        string memoKey,
        List<string> diagnostics,
        out string? warning)
    {
        runtimeCache.GradingWindowMemos.TryGetValue(memoKey, out GradingWindows.Memo? previous);
        var next = new GradingWindows.Memo();
        GradingResult? result = PathGrader.GradeWindowed(
            vertices, vertexCount, faces, faceCount, paths, hardConstraints, tolerance, preferSplitKeep,
            previous, next, diagnostics, out warning);
        runtimeCache.GradingWindowMemos[memoKey] = next;
        return result;
    }
}
