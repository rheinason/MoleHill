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
            VerticalTolerance = settings.Tolerance,
            PlanTolerance = settings.PlanTolerance,
            // Without contours only the border is wanted: no policy, no curves.
            MaximumCurves = 0,
            Policy = !settings.Contours ? null : new TerrainRecoveryPolicy
            {
                InputsPerArea = settings.InputsPerHectare * units.ToSquareMeters(1) / 10000,
                ContourInterval = settings.ContourInterval, ReportActivity = progress,
                // A terrain's folds and walls are its own; a scan's walls are measured rails, and its creases noise.
                CreaseAngleDegrees = prepared.Scan == null ? 20 : 0,
                WallSlopeDegrees = prepared.Scan == null ? 70 : 0,
            },
            FixedBreaklines = prepared.FixedBreaklines,
            Region = prepared.Region,
            SecondaryRegion = prepared.SecondaryRegion,
            Unmeasured = prepared.Unmeasured,
            SecondaryTolerance = settings is ScanToTerrainSettings paved ? paved.PavedTolerance : 0,
            FitContours = true,
            ShouldCancel = () => token.IsCancellationRequested,
            ReportProgress = (round, miss) => progress($"Curves, round {round}: {miss:G4} {units.Abbreviation} miss"),
        }, TerrainCurveRecovery.LandformMode.No), token);
        token.ThrowIfCancellationRequested();
        if (recovery.Termination is TerrainCurveRecovery.Termination.UnsupportedInput or TerrainCurveRecovery.Termination.TriangulationFailed)
            throw new InvalidOperationException(recovery.Diagnostic);
        if (settings.Contours && prepared.Scan?.OriginalReference is { } original)
            recovery.OriginalMaximumDeviation = await Task.Run(() => TerrainCurveRecovery.MeasureMaximumDeviation(recovery, original,
                new()
                {
                    VerticalTolerance = settings.Tolerance,
                    PlanTolerance = settings.PlanTolerance,
                    FixedBreaklines = prepared.FixedBreaklines,
                    Region = prepared.Region
                }), token);
        token.ThrowIfCancellationRequested();
        if (!settings.Contours)
            recovery = new()
            {
                Borders = recovery.Borders,
                Contours = Array.Empty<TerrainCurveRecovery.RecoveredContour>(),
                LandformLines = recovery.LandformLines,
                SpotPoints = Array.Empty<double>(),
                MaximumDeviation = recovery.MaximumDeviation,
                Rounds = recovery.Rounds,
                Termination = recovery.Termination,
                Diagnostic = "Contours and spots disabled.",
                EvaluatedVertexCount = recovery.EvaluatedVertexCount
            };
        return prepared with { Recovery = recovery };
    }
}
