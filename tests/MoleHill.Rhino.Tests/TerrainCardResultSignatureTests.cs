using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// A finished build refreshes only the status line when this signature is unchanged, so every build
/// result a card shows must move it — and nothing a card does not show may.
/// </summary>
public sealed class TerrainCardResultSignatureTests
{
    private static readonly RuntimeOverlayOwner Wall = new(RuntimeOverlayOwnerKind.Modifier, Guid.NewGuid());

    private static string Compute(
        TerrainDefinition terrain,
        IReadOnlyList<ZoneAnalysisSummary>? zones = null,
        IEnumerable<(RuntimeOverlayOwner, RuntimeOverlaySeverity)>? diagnostics = null,
        IEnumerable<(Guid, string?)>? warnings = null) =>
        TerrainCardResultSignature.Compute(
            terrain,
            zones ?? Array.Empty<ZoneAnalysisSummary>(),
            diagnostics ?? Array.Empty<(RuntimeOverlayOwner, RuntimeOverlaySeverity)>(),
            warnings ?? Array.Empty<(Guid, string?)>());

    [Fact]
    public void Compute_OnlyTheStatusMessageChanged_IsUnchanged()
    {
        var terrain = new TerrainDefinition { LastBuildMessage = "Build #1 succeeded in 0.07 s" };
        string before = Compute(terrain);

        terrain.LastBuildMessage = "Build #2 succeeded in 0.02 s";
        terrain.LastBuildUtc = DateTimeOffset.UtcNow;

        Assert.Equal(before, Compute(terrain));
    }

    [Fact]
    public void Compute_AnalysisMeasuredSomethingNew_Changes()
    {
        var terrain = new TerrainDefinition();
        terrain.LastAnalysisResults.Add(new TerrainAnalysisSummary { AnalysisId = Guid.NewGuid(), SlopeMaxPercent = 12 });
        string before = Compute(terrain);

        terrain.LastAnalysisResults[0].SlopeMaxPercent = 14;

        Assert.NotEqual(before, Compute(terrain));
    }

    [Fact]
    public void Compute_StairRecomputedItsSteps_Changes()
    {
        var terrain = new TerrainDefinition();
        var stair = new InSituStairModifierDefinition { ComputedStepCountSummary = "6 steps" };
        terrain.Modifiers.Add(stair);
        string before = Compute(terrain);

        stair.ComputedStepCountSummary = "7 steps";

        Assert.NotEqual(before, Compute(terrain));
    }

    [Fact]
    public void Compute_ZoneSummaryWithNaNChanged_ChangesWithoutThrowing()
    {
        var terrain = new TerrainDefinition();
        var zone = new ZoneAnalysisSummary { ZoneId = Guid.NewGuid(), SlopeMaxPercent = double.NaN };
        string before = Compute(terrain, zones: new[] { zone });

        zone.CutVolume = 3.5;

        Assert.NotEqual(before, Compute(terrain, zones: new[] { zone }));
    }

    [Fact]
    public void Compute_DiagnosticCountsChangedButNotTheirOrder_ComparesByCount()
    {
        var terrain = new TerrainDefinition();
        var one = new[] { (Wall, RuntimeOverlaySeverity.Warning), (Wall, RuntimeOverlaySeverity.Error) };
        var reordered = new[] { (Wall, RuntimeOverlaySeverity.Error), (Wall, RuntimeOverlaySeverity.Warning) };
        var more = new[] { (Wall, RuntimeOverlaySeverity.Error), (Wall, RuntimeOverlaySeverity.Warning), (Wall, RuntimeOverlaySeverity.Warning) };

        Assert.Equal(Compute(terrain, diagnostics: one), Compute(terrain, diagnostics: reordered));
        Assert.NotEqual(Compute(terrain, diagnostics: one), Compute(terrain, diagnostics: more));
    }

    [Fact]
    public void Compute_NewMeshQualityWarning_Changes()
    {
        var terrain = new TerrainDefinition();
        Guid smooth = Guid.NewGuid();

        Assert.NotEqual(
            Compute(terrain, warnings: new[] { (smooth, (string?)null) }),
            Compute(terrain, warnings: new[] { (smooth, (string?)"Incoming mesh is very sparse") }));
    }
}
