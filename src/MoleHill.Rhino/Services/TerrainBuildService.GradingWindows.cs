using MoleHill.Core.Engine;
using MoleHill.Core.Grading;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Windowed grading for the Grade Pad stage (<see cref="GradingWindows"/>): each group of pads whose reach
/// overlaps is graded on the faces under its window, and a window whose input is unchanged since the last
/// run is taken from the stage's memo. An edit to the terrain away from a pad, or to one pad, re-grades only
/// the windows it touches, and the result is exactly a cold build's.
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
}
