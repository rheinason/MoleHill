using System.Globalization;
using System.Text;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Turns raw <see cref="TerrainLatencyEvent"/>s into per-request timelines and an aggregate summary.
///
/// The reconstruction differences consecutive events, so the intervals partition a request's lifetime
/// by construction and nothing can hide in a gap. An interval whose two endpoints have no agreed name
/// is reported as <c>unclassified</c> rather than folded into a neighbour - the whole point of this
/// trace is that <c>Rebuild total</c> starts at the snapshot and therefore cannot see debounce,
/// dispatch blocking, thread-pool queueing, or the idle tick that accepts the result.
/// </summary>
internal static class TerrainLatencyReport
{
    private sealed record Request(uint DocSerial, Guid TerrainId, long Version, TerrainBuildMode Mode)
    {
        public List<TerrainLatencyEvent> Events { get; } = new();
    }

    /// <summary>Named intervals, keyed by the phase pair that bounds them.</summary>
    private static readonly Dictionary<(string From, string To), string> IntervalNames = new()
    {
        [(TerrainLatencyPhase.Edit, TerrainLatencyPhase.Due)] = "debounce",
        [(TerrainLatencyPhase.Edit, TerrainLatencyPhase.Dispatch)] = "debounce",
        [(TerrainLatencyPhase.Edit, TerrainLatencyPhase.DispatchBlocked)] = "debounce",
        [(TerrainLatencyPhase.Due, TerrainLatencyPhase.Dispatch)] = "dispatch wait",
        [(TerrainLatencyPhase.Due, TerrainLatencyPhase.DispatchBlocked)] = "dispatch wait",
        [(TerrainLatencyPhase.DispatchBlocked, TerrainLatencyPhase.DispatchBlocked)] = "blocked (busy/deferred)",
        [(TerrainLatencyPhase.DispatchBlocked, TerrainLatencyPhase.Dispatch)] = "blocked (busy/deferred)",
        [(TerrainLatencyPhase.Dispatch, TerrainLatencyPhase.SnapshotStart)] = "start overhead",
        [(TerrainLatencyPhase.SnapshotStart, TerrainLatencyPhase.SnapshotEnd)] = "snapshot",
        [(TerrainLatencyPhase.SnapshotEnd, TerrainLatencyPhase.CloneEnd)] = "worker cache clone",
        [(TerrainLatencyPhase.CloneEnd, TerrainLatencyPhase.WorkerQueued)] = "start overhead",
        [(TerrainLatencyPhase.WorkerQueued, TerrainLatencyPhase.WorkerStart)] = "thread-pool queue",
        [(TerrainLatencyPhase.WorkerStart, TerrainLatencyPhase.GeometryReady)] = "geometry (modifiers)",
        [(TerrainLatencyPhase.GeometryReady, TerrainLatencyPhase.InterimPublished)] = "interim copy + post",
        [(TerrainLatencyPhase.InterimPublished, TerrainLatencyPhase.InterimVisible)] = "interim marshal + redraw",
        [(TerrainLatencyPhase.InterimVisible, TerrainLatencyPhase.OutputsEnd)] = "dependent outputs",
        [(TerrainLatencyPhase.InterimPublished, TerrainLatencyPhase.OutputsEnd)] = "dependent outputs",
        [(TerrainLatencyPhase.GeometryReady, TerrainLatencyPhase.OutputsEnd)] = "dependent outputs",
        [(TerrainLatencyPhase.GeometryReady, TerrainLatencyPhase.WorkerEnd)] = "worker tail",
        [(TerrainLatencyPhase.OutputsEnd, TerrainLatencyPhase.WorkerEnd)] = "worker tail",
        [(TerrainLatencyPhase.WorkerEnd, TerrainLatencyPhase.CompletionDispatch)] = "idle completion wait",
        [(TerrainLatencyPhase.WorkerEnd, TerrainLatencyPhase.WakePosted)] = "wake post latency",
        [(TerrainLatencyPhase.WakePosted, TerrainLatencyPhase.WakeRan)] = "wake marshal wait",
        [(TerrainLatencyPhase.WakePosted, TerrainLatencyPhase.CompletionDispatch)] = "wake marshal wait",
        [(TerrainLatencyPhase.WakeRan, TerrainLatencyPhase.CompletionDispatch)] = "pump to pickup",
        [(TerrainLatencyPhase.CompletionDispatch, TerrainLatencyPhase.MergeEnd)] = "cache merge",
        [(TerrainLatencyPhase.MergeEnd, TerrainLatencyPhase.DisplayEnd)] = "display publish",
        [(TerrainLatencyPhase.DisplayEnd, TerrainLatencyPhase.SyncEnd)] = "object sync",
        [(TerrainLatencyPhase.DisplayEnd, TerrainLatencyPhase.RedrawEnd)] = "redraw",
        [(TerrainLatencyPhase.SyncEnd, TerrainLatencyPhase.SaveEnd)] = "document save",
        [(TerrainLatencyPhase.SaveEnd, TerrainLatencyPhase.RedrawEnd)] = "redraw",
        [(TerrainLatencyPhase.RedrawEnd, TerrainLatencyPhase.Closed)] = "close overhead",
    };

    public static string Format(IReadOnlyList<TerrainLatencyEvent> events, Guid? terrainFilter = null)
    {
        if (events.Count == 0)
            return "No latency events recorded. Enable the trace with 'mhLatencyTrace' and edit a terrain.";

        List<Request> requests = Group(events, terrainFilter);
        if (requests.Count == 0)
            return "No latency events matched the requested terrain.";

        var report = new StringBuilder();
        report.AppendLine($"MoleHill edit-to-visible latency trace: {events.Count:N0} events, {requests.Count:N0} requests.");
        report.AppendLine();

        var visibleSamples = new List<double>();
        var geometrySamples = new List<double>();
        var geometryVisibleSamples = new List<double>();
        var outputSamples = new List<double>();
        double abandonedMs = 0.0;
        int abandonedCount = 0;
        var intervalTotals = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (Request request in requests)
        {
            List<TerrainLatencyEvent> ordered = request.Events.OrderBy(item => item.Timestamp).ToList();
            string outcome = OutcomeOf(ordered);
            bool applied = outcome == "applied";

            report.AppendLine($"--- {request.Mode} #{request.Version:N0} on {Short(request.TerrainId)}  [{outcome}]");
            for (int index = 1; index < ordered.Count; index++)
            {
                TerrainLatencyEvent from = ordered[index - 1];
                TerrainLatencyEvent to = ordered[index];
                double ms = TerrainLatencyTrace.MillisecondsBetween(from.Timestamp, to.Timestamp);
                string name = NameFor(from.Phase, to.Phase);
                string detail = to.Detail is null ? string.Empty : $"  ({to.Detail})";
                report.AppendLine($"    {ms,9:0.0} ms  {name,-24} {from.Phase} -> {to.Phase}{detail}");

                if (applied)
                    intervalTotals[name] = intervalTotals.GetValueOrDefault(name) + ms;
            }

            double? editToVisible = Span(ordered, TerrainLatencyPhase.Edit, TerrainLatencyPhase.RedrawEnd);
            double? editToGeometryVisible = Span(ordered, TerrainLatencyPhase.Edit, TerrainLatencyPhase.InterimVisible);
            double? geometry = Span(ordered, TerrainLatencyPhase.WorkerStart, TerrainLatencyPhase.GeometryReady);
            double? outputs = Span(ordered, TerrainLatencyPhase.GeometryReady, TerrainLatencyPhase.OutputsEnd);
            double? worker = Span(ordered, TerrainLatencyPhase.WorkerStart, TerrainLatencyPhase.WorkerEnd);

            if (applied)
            {
                if (editToVisible.HasValue)
                {
                    visibleSamples.Add(editToVisible.Value);
                    report.AppendLine($"    = edit to visible: {editToVisible.Value:0.0} ms");
                }

                if (editToGeometryVisible.HasValue)
                {
                    geometryVisibleSamples.Add(editToGeometryVisible.Value);
                    report.AppendLine($"    = edit to terrain visible (outputs still stale): {editToGeometryVisible.Value:0.0} ms");
                }

                if (geometry.HasValue)
                    geometrySamples.Add(geometry.Value);
                if (outputs.HasValue)
                    outputSamples.Add(outputs.Value);
            }
            else if (worker.HasValue)
            {
                abandonedMs += worker.Value;
                abandonedCount++;
            }

            report.AppendLine();
        }

        report.AppendLine("=== Summary");
        AppendStat(report, "edit to visible (everything current)", visibleSamples);
        AppendStat(report, "edit to terrain visible (interim)", geometryVisibleSamples);
        AppendStat(report, "geometry (modifiers)", geometrySamples);
        AppendStat(report, "dependent outputs", outputSamples);
        report.AppendLine($"  abandoned worker time: {abandonedMs:0.0} ms across {abandonedCount:N0} superseded/cancelled requests");
        report.AppendLine();

        if (intervalTotals.Count > 0)
        {
            double total = intervalTotals.Values.Sum();
            report.AppendLine($"=== Where the time goes (applied requests only; {total:0.0} ms accounted, no unattributed remainder)");
            foreach (var entry in intervalTotals.OrderByDescending(item => item.Value))
            {
                double share = total > 0.0 ? entry.Value / total * 100.0 : 0.0;
                report.AppendLine($"  {entry.Value,9:0.0} ms  {share,5:0.0}%  {entry.Key}");
            }
        }

        return report.ToString();
    }

    public static string FormatCsv(IReadOnlyList<TerrainLatencyEvent> events)
    {
        var csv = new StringBuilder("elapsed_ms,doc,terrain,version,generation,mode,phase,detail\n");
        if (events.Count == 0)
            return csv.ToString();

        long origin = events.Min(item => item.Timestamp);
        foreach (TerrainLatencyEvent item in events.OrderBy(item => item.Timestamp))
        {
            string detail = (item.Detail ?? string.Empty).Replace('"', '\'').Replace(',', ';');
            // Invariant: this machine formats decimals with a comma, which would split the column.
            csv.Append(CultureInfo.InvariantCulture, $"{TerrainLatencyTrace.MillisecondsBetween(origin, item.Timestamp):0.000},")
               .Append($"{item.DocSerial},{Short(item.TerrainId)},{item.Version},{item.Generation},")
               .Append($"{item.Mode},{item.Phase},\"{detail}\"\n");
        }

        return csv.ToString();
    }

    private static List<Request> Group(IReadOnlyList<TerrainLatencyEvent> events, Guid? terrainFilter)
    {
        var map = new Dictionary<(uint, Guid, long, TerrainBuildMode), Request>();
        foreach (TerrainLatencyEvent item in events)
        {
            if (terrainFilter.HasValue && item.TerrainId != terrainFilter.Value)
                continue;

            var key = (item.DocSerial, item.TerrainId, item.Version, item.Mode);
            if (!map.TryGetValue(key, out Request? request))
            {
                request = new Request(item.DocSerial, item.TerrainId, item.Version, item.Mode);
                map[key] = request;
            }

            request.Events.Add(item);
        }

        return map.Values
            .OrderBy(request => request.Events.Min(item => item.Timestamp))
            .ToList();
    }

    private static string NameFor(string from, string to)
    {
        if (to.StartsWith(TerrainLatencyPhase.OutputFamilyPrefix, StringComparison.Ordinal))
            return to[TerrainLatencyPhase.OutputFamilyPrefix.Length..];
        if (from.StartsWith(TerrainLatencyPhase.OutputFamilyPrefix, StringComparison.Ordinal) &&
            to == TerrainLatencyPhase.OutputsEnd)
        {
            return "outputs tail";
        }

        return IntervalNames.TryGetValue((from, to), out string? name) ? name : "unclassified";
    }

    private static string OutcomeOf(List<TerrainLatencyEvent> ordered)
    {
        TerrainLatencyEvent? closed = ordered.LastOrDefault(item => item.Phase == TerrainLatencyPhase.Closed);
        if (closed?.Detail is { Length: > 0 } detail)
            return detail;

        return ordered.Any(item => item.Phase == TerrainLatencyPhase.RedrawEnd) ? "applied" : "incomplete";
    }

    private static double? Span(List<TerrainLatencyEvent> ordered, string from, string to)
    {
        TerrainLatencyEvent? start = ordered.FirstOrDefault(item => item.Phase == from);
        TerrainLatencyEvent? end = ordered.LastOrDefault(item => item.Phase == to);
        return start != null && end != null && end.Timestamp >= start.Timestamp
            ? TerrainLatencyTrace.MillisecondsBetween(start.Timestamp, end.Timestamp)
            : null;
    }

    private static void AppendStat(StringBuilder report, string label, List<double> samples)
    {
        if (samples.Count == 0)
        {
            report.AppendLine($"  {label}: no samples");
            return;
        }

        List<double> sorted = samples.OrderBy(static value => value).ToList();
        double median = sorted[sorted.Count / 2];
        double p95 = sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(sorted.Count * 0.95) - 1)];
        report.AppendLine(
            $"  {label}: n={sorted.Count}, median {median:0.0} ms, p95 {p95:0.0} ms, min {sorted[0]:0.0} ms, max {sorted[^1]:0.0} ms");
    }

    private static string Short(Guid id) => id.ToString("N")[..8];
}
