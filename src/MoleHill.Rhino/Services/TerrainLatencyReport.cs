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

    /// <summary>
    /// Who is responsible for an interval. The question the trace has to answer is not only "which
    /// stage is slow" but "is anyone working during this". A <see cref="LatencyKind.HostWait"/>
    /// interval is time when MoleHill has finished a piece of work and is waiting for Rhino's message
    /// loop to run the callback that consumes it - no CPU of ours is busy, and no optimization of any
    /// stage shortens it.
    /// </summary>
    private enum LatencyKind
    {
        /// <summary>Deliberate delay before dispatching, to coalesce a gesture.</summary>
        Debounce,

        /// <summary>MoleHill is computing.</summary>
        Work,

        /// <summary>Waiting for the host to run our code: message loop, idle event, thread pool.</summary>
        HostWait,

        /// <summary>Rhino redrawing its viewports.</summary>
        Redraw,

        /// <summary>A phase pair with no agreed meaning. Reported, never folded into a neighbour.</summary>
        Unclassified
    }

    private readonly record struct Interval(string Name, LatencyKind Kind);

    /// <summary>Named intervals, keyed by the phase pair that bounds them.</summary>
    private static readonly Dictionary<(string From, string To), Interval> IntervalNames = new()
    {
        [(TerrainLatencyPhase.Edit, TerrainLatencyPhase.Due)] = new Interval("debounce", LatencyKind.Debounce),
        [(TerrainLatencyPhase.Edit, TerrainLatencyPhase.Dispatch)] = new Interval("debounce", LatencyKind.Debounce),
        [(TerrainLatencyPhase.Edit, TerrainLatencyPhase.DispatchBlocked)] = new Interval("debounce", LatencyKind.Debounce),
        [(TerrainLatencyPhase.Due, TerrainLatencyPhase.Dispatch)] = new Interval("dispatch wait", LatencyKind.HostWait),
        [(TerrainLatencyPhase.Due, TerrainLatencyPhase.DispatchBlocked)] = new Interval("dispatch wait", LatencyKind.HostWait),
        [(TerrainLatencyPhase.DispatchBlocked, TerrainLatencyPhase.DispatchBlocked)] = new Interval("blocked (busy/deferred)", LatencyKind.HostWait),
        [(TerrainLatencyPhase.DispatchBlocked, TerrainLatencyPhase.Dispatch)] = new Interval("blocked (busy/deferred)", LatencyKind.HostWait),
        [(TerrainLatencyPhase.Dispatch, TerrainLatencyPhase.SnapshotStart)] = new Interval("start overhead", LatencyKind.Work),
        [(TerrainLatencyPhase.SnapshotStart, TerrainLatencyPhase.SnapshotEnd)] = new Interval("snapshot", LatencyKind.Work),
        [(TerrainLatencyPhase.SnapshotEnd, TerrainLatencyPhase.CloneEnd)] = new Interval("worker cache clone", LatencyKind.Work),
        [(TerrainLatencyPhase.CloneEnd, TerrainLatencyPhase.WorkerQueued)] = new Interval("start overhead", LatencyKind.Work),
        [(TerrainLatencyPhase.WorkerQueued, TerrainLatencyPhase.WorkerStart)] = new Interval("thread-pool queue", LatencyKind.HostWait),
        [(TerrainLatencyPhase.WorkerStart, TerrainLatencyPhase.GeometryReady)] = new Interval("geometry (modifiers)", LatencyKind.Work),
        [(TerrainLatencyPhase.GeometryReady, TerrainLatencyPhase.InterimPublished)] = new Interval("interim copy + post", LatencyKind.Work),
        [(TerrainLatencyPhase.InterimPublished, TerrainLatencyPhase.InterimRan)] = new Interval("interim marshal wait", LatencyKind.HostWait),
        [(TerrainLatencyPhase.InterimRan, TerrainLatencyPhase.InterimVisible)] = new Interval("interim publish + redraw", LatencyKind.Work),
        [(TerrainLatencyPhase.InterimPublished, TerrainLatencyPhase.InterimVisible)] = new Interval("interim marshal + redraw", LatencyKind.HostWait),
        [(TerrainLatencyPhase.InterimVisible, TerrainLatencyPhase.OutputsEnd)] = new Interval("dependent outputs", LatencyKind.Work),
        [(TerrainLatencyPhase.InterimPublished, TerrainLatencyPhase.OutputsEnd)] = new Interval("dependent outputs", LatencyKind.Work),
        [(TerrainLatencyPhase.GeometryReady, TerrainLatencyPhase.OutputsEnd)] = new Interval("dependent outputs", LatencyKind.Work),
        [(TerrainLatencyPhase.GeometryReady, TerrainLatencyPhase.WorkerEnd)] = new Interval("worker tail", LatencyKind.Work),
        [(TerrainLatencyPhase.OutputsEnd, TerrainLatencyPhase.WorkerEnd)] = new Interval("worker tail", LatencyKind.Work),
        [(TerrainLatencyPhase.WorkerEnd, TerrainLatencyPhase.CompletionDispatch)] = new Interval("idle completion wait", LatencyKind.HostWait),
        [(TerrainLatencyPhase.WorkerEnd, TerrainLatencyPhase.WakePosted)] = new Interval("wake post latency", LatencyKind.Work),
        [(TerrainLatencyPhase.WakePosted, TerrainLatencyPhase.WakeRan)] = new Interval("wake marshal wait", LatencyKind.HostWait),
        [(TerrainLatencyPhase.WakePosted, TerrainLatencyPhase.CompletionDispatch)] = new Interval("wake marshal wait", LatencyKind.HostWait),
        [(TerrainLatencyPhase.WakeRan, TerrainLatencyPhase.CompletionDispatch)] = new Interval("pump to pickup", LatencyKind.Work),
        // When the dependent outputs turn out to be cheap, the interim publication is still queued
        // behind the host loop and lands after the build has already finished. Name those orderings
        // too, or the report shows its largest interval as "unclassified".
        [(TerrainLatencyPhase.WakePosted, TerrainLatencyPhase.InterimRan)] = new Interval("interim marshal wait", LatencyKind.HostWait),
        [(TerrainLatencyPhase.WakePosted, TerrainLatencyPhase.InterimVisible)] = new Interval("interim marshal + redraw", LatencyKind.HostWait),
        [(TerrainLatencyPhase.InterimVisible, TerrainLatencyPhase.WakeRan)] = new Interval("wake marshal wait", LatencyKind.HostWait),
        [(TerrainLatencyPhase.InterimVisible, TerrainLatencyPhase.CompletionDispatch)] = new Interval("wake marshal wait", LatencyKind.HostWait),
        [(TerrainLatencyPhase.InterimPublished, TerrainLatencyPhase.WorkerEnd)] = new Interval("worker tail", LatencyKind.Work),
        [(TerrainLatencyPhase.OutputsEnd, TerrainLatencyPhase.InterimVisible)] = new Interval("interim marshal + redraw", LatencyKind.HostWait),
        [(TerrainLatencyPhase.OutputsEnd, TerrainLatencyPhase.InterimRan)] = new Interval("interim marshal wait", LatencyKind.HostWait),
        [(TerrainLatencyPhase.InterimRan, TerrainLatencyPhase.WakeRan)] = new Interval("interim publish + redraw", LatencyKind.Work),
        [(TerrainLatencyPhase.InterimRan, TerrainLatencyPhase.CompletionDispatch)] = new Interval("interim publish + redraw", LatencyKind.Work),
        [(TerrainLatencyPhase.CompletionDispatch, TerrainLatencyPhase.MergeEnd)] = new Interval("cache merge", LatencyKind.Work),
        [(TerrainLatencyPhase.MergeEnd, TerrainLatencyPhase.DisplayEnd)] = new Interval("display publish", LatencyKind.Work),
        [(TerrainLatencyPhase.DisplayEnd, TerrainLatencyPhase.SyncEnd)] = new Interval("object sync", LatencyKind.Work),
        [(TerrainLatencyPhase.DisplayEnd, TerrainLatencyPhase.RedrawEnd)] = new Interval("redraw", LatencyKind.Redraw),
        [(TerrainLatencyPhase.SyncEnd, TerrainLatencyPhase.SaveEnd)] = new Interval("document save", LatencyKind.Work),
        [(TerrainLatencyPhase.SaveEnd, TerrainLatencyPhase.RedrawEnd)] = new Interval("redraw", LatencyKind.Redraw),
        [(TerrainLatencyPhase.RedrawEnd, TerrainLatencyPhase.Closed)] = new Interval("close overhead", LatencyKind.Work),
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
        var kindTotals = new Dictionary<LatencyKind, double>();

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
                Interval interval = NameFor(from.Phase, to.Phase);
                string detail = to.Detail is null ? string.Empty : $"  ({to.Detail})";
                report.AppendLine(
                    $"    {ms,9:0.0} ms  {Tag(interval.Kind)}  {interval.Name,-24} {from.Phase} -> {to.Phase}{detail}");

                if (applied)
                {
                    intervalTotals[interval.Name] = intervalTotals.GetValueOrDefault(interval.Name) + ms;
                    kindTotals[interval.Kind] = kindTotals.GetValueOrDefault(interval.Kind) + ms;
                }
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

        if (kindTotals.Count > 0)
        {
            double kindTotal = kindTotals.Values.Sum();
            report.AppendLine("=== Who the wait belongs to (applied requests only)");
            foreach (LatencyKind kind in new[]
                     { LatencyKind.Debounce, LatencyKind.Work, LatencyKind.HostWait, LatencyKind.Redraw, LatencyKind.Unclassified })
            {
                if (!kindTotals.TryGetValue(kind, out double value) || value <= 0.0)
                    continue;

                double kindShare = kindTotal > 0.0 ? value / kindTotal * 100.0 : 0.0;
                report.AppendLine($"  {value,9:0.0} ms  {kindShare,5:0.0}%  {Describe(kind)}");
            }

            report.AppendLine();
        }

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

    private static Interval NameFor(string from, string to)
    {
        if (to.StartsWith(TerrainLatencyPhase.OutputFamilyPrefix, StringComparison.Ordinal))
            return new Interval(to[TerrainLatencyPhase.OutputFamilyPrefix.Length..], LatencyKind.Work);
        if (from.StartsWith(TerrainLatencyPhase.OutputFamilyPrefix, StringComparison.Ordinal) &&
            to == TerrainLatencyPhase.OutputsEnd)
        {
            return new Interval("outputs tail", LatencyKind.Work);
        }

        return IntervalNames.TryGetValue((from, to), out Interval interval)
            ? interval
            : new Interval("unclassified", LatencyKind.Unclassified);
    }

    /// <summary>Four-character column so a timeline can be scanned for host waits at a glance.</summary>
    private static string Tag(LatencyKind kind) => kind switch
    {
        LatencyKind.Debounce => "WAIT",
        LatencyKind.Work => "work",
        LatencyKind.HostWait => "HOST",
        LatencyKind.Redraw => "draw",
        _ => "????"
    };

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

    private static string Describe(LatencyKind kind) => kind switch
    {
        LatencyKind.Debounce => "debounce (deliberate delay before dispatching)",
        LatencyKind.Work => "MoleHill working",
        LatencyKind.HostWait => "waiting for Rhino to run our code (nobody working)",
        LatencyKind.Redraw => "Rhino redrawing",
        _ => "unclassified"
    };

    private static string Short(Guid id) => id.ToString("N")[..8];
}
