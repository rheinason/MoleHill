using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MoleHill.Rhino.Model;

namespace MoleHill.Rhino.Services;

/// <summary>
/// A fingerprint of every build result the panel's cards show, so a finished build that changed none of
/// them refreshes only the status line instead of relaying the visible tab out.
/// </summary>
/// <remarks>
/// Measured live on 2026-10-01: a wall edit on a 100,911-face terrain finished its geometry in 10 ms, and
/// then the completion's full panel refresh spent 47–55 ms relaying the Modifiers tab, on the UI thread,
/// in front of the next edit's result. Nothing on any card had changed; only the status line had.
///
/// The parts are what the cards read from a build: the terrain's analysis summaries, the computed fields
/// a modifier stores on itself (In-situ Stair), the zone summaries, the diagnostic counts each card badges,
/// and the mesh quality warnings. Whether a final mesh exists is left out: it flips off for every mid-edit
/// frame and back on with the next final build, so the caller instead refreshes in full on the first final
/// build, which is when cards comparing against this terrain become available. Anything new a card reads
/// from a build must be added here, or the card goes stale until the next full refresh.
/// </remarks>
internal static class TerrainCardResultSignature
{
    private static readonly JsonSerializerOptions Options = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static string Compute(
        TerrainDefinition terrain,
        IReadOnlyList<ZoneAnalysisSummary> zoneResults,
        IEnumerable<(RuntimeOverlayOwner Owner, RuntimeOverlaySeverity Severity)> diagnostics,
        IEnumerable<(Guid ModifierId, string? Warning)> meshQualityWarnings)
    {
        var text = new StringBuilder();
        text.Append(JsonSerializer.Serialize(terrain.LastAnalysisResults, Options)).Append(';');
        foreach (InSituStairModifierDefinition stair in terrain.Modifiers.OfType<InSituStairModifierDefinition>())
        {
            text.Append(stair.Id).Append(':')
                .Append(stair.ComputedSurfaceCount).Append('|')
                .Append(stair.ComputedTreadDepthSummary).Append('|')
                .Append(stair.ComputedStepCountSummary).Append(';');
        }

        text.Append(JsonSerializer.Serialize(zoneResults, Options)).Append(';');

        // Counts per owner and severity, in a stable order: the cards show counts, not the items.
        foreach (var group in diagnostics
                     .GroupBy(item => (item.Owner, item.Severity))
                     .Select(group => $"{group.Key.Owner.Kind}:{group.Key.Owner.Id}:{group.Key.Severity}={group.Count()}")
                     .OrderBy(item => item, StringComparer.Ordinal))
        {
            text.Append(group).Append(';');
        }

        foreach ((Guid modifierId, string? warning) in meshQualityWarnings)
            text.Append(modifierId).Append('=').Append(warning).Append(';');

        return text.ToString();
    }
}
