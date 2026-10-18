using MoleHill.Core.Processing;
using MoleHill.Shared;

namespace MoleHill.Rhino.Services;

/// <summary>The one async extraction runner for prepared scan ground, DEM meshes and built terrains.</summary>
internal static class TerrainCurveExtractionService
{
    public static async Task<TerrainCurveExtractionPrepared> RecoverAsync(TerrainCurveExtractionPrepared prepared,
        TerrainCurveExtractionSettings settings, ModelUnitContext units, CancellationToken token, Action<string> progress)
    {
        if (prepared.Reference.IsEmpty) throw new InvalidOperationException("The reference has no connected terrain faces.");
        var recovery = await Task.Run(() => TerrainCurveRecovery.RecoverBest(prepared.Reference, new()
        {
            VerticalTolerance = settings.Tolerance, PlanTolerance = settings.PlanTolerance,
            MaximumCurves = settings.Contours ? settings.MaxContours : 0,
            FixedBreaklines = prepared.FixedBreaklines, Region = prepared.Region,
            FitContours = true, ShouldCancel = () => token.IsCancellationRequested,
            ReportProgress = (round, miss) => progress($"Curves, round {round}: {miss:G4} {units.Abbreviation} miss"),
        }, (TerrainCurveRecovery.LandformMode)settings.RidgesValleys), token);
        token.ThrowIfCancellationRequested();
        if (recovery.Termination is TerrainCurveRecovery.Termination.UnsupportedInput or TerrainCurveRecovery.Termination.TriangulationFailed)
            throw new InvalidOperationException(recovery.Diagnostic);
        if (!settings.Contours)
            recovery = new() { Borders = recovery.Borders, Contours = Array.Empty<TerrainCurveRecovery.RecoveredContour>(),
                LandformLines = recovery.LandformLines, SpotPoints = Array.Empty<double>(), MaximumDeviation = recovery.MaximumDeviation,
                Rounds = recovery.Rounds, Termination = recovery.Termination, Diagnostic = "Contours and spots disabled.", EvaluatedVertexCount = recovery.EvaluatedVertexCount };
        return prepared with { Recovery = recovery };
    }
}
