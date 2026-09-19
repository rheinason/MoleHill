using System.Diagnostics;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// The latency report exists because <c>Rebuild total</c> starts at the snapshot and so cannot see
/// debounce, dispatch blocking, thread-pool queueing, or the idle tick that accepts a finished build.
/// These tests hold it to that: every interval is attributed, gaps are named rather than swallowed,
/// and work thrown away by a superseding edit is reported instead of disappearing.
/// </summary>
public sealed class TerrainLatencyReportTests
{
    private static readonly Guid TerrainId = Guid.Parse("848a8059-a1f8-4307-9ef6-bb06a802174b");

    private static long Tick(double milliseconds) =>
        (long)(milliseconds / 1000.0 * Stopwatch.Frequency);

    private static TerrainLatencyEvent Event(double atMs, string phase, long version = 1, string? detail = null) =>
        new(Tick(atMs), DocSerial: 1, TerrainId, version, Generation: 1, TerrainBuildMode.Final, phase, detail);

    private static List<TerrainLatencyEvent> AppliedRequest(long version = 1, double origin = 0.0) => new()
    {
        Event(origin + 0, TerrainLatencyPhase.Edit, version),
        Event(origin + 500, TerrainLatencyPhase.Due, version),
        Event(origin + 520, TerrainLatencyPhase.Dispatch, version),
        Event(origin + 521, TerrainLatencyPhase.SnapshotStart, version),
        Event(origin + 561, TerrainLatencyPhase.SnapshotEnd, version),
        Event(origin + 563, TerrainLatencyPhase.CloneEnd, version),
        Event(origin + 564, TerrainLatencyPhase.WorkerQueued, version),
        Event(origin + 566, TerrainLatencyPhase.WorkerStart, version),
        Event(origin + 906, TerrainLatencyPhase.GeometryReady, version),
        Event(origin + 1006, TerrainLatencyPhase.OutputsEnd, version),
        Event(origin + 1008, TerrainLatencyPhase.WorkerEnd, version, "ok"),
        Event(origin + 1040, TerrainLatencyPhase.CompletionDispatch, version),
        Event(origin + 1041, TerrainLatencyPhase.MergeEnd, version),
        Event(origin + 1045, TerrainLatencyPhase.DisplayEnd, version),
        Event(origin + 1050, TerrainLatencyPhase.SyncEnd, version),
        Event(origin + 1052, TerrainLatencyPhase.SaveEnd, version),
        Event(origin + 1162, TerrainLatencyPhase.RedrawEnd, version),
        Event(origin + 1163, TerrainLatencyPhase.Closed, version, "applied"),
    };

    [Fact]
    public void Format_AppliedRequest_ReportsEditToVisibleAcrossTheWholeLifetime()
    {
        string report = TerrainLatencyReport.Format(AppliedRequest());

        // 1,162 ms from the edit to the frame - of which the build service's own total sees only the
        // 641 ms from snapshot start onward.
        Assert.Contains("edit to visible: 1162", report.Replace(",", "."));
        Assert.Contains("[applied]", report);
    }

    [Fact]
    public void Format_AppliedRequest_AttributesEveryIntervalWithNoUnclassifiedRemainder()
    {
        string report = TerrainLatencyReport.Format(AppliedRequest());

        Assert.DoesNotContain("unclassified", report);
        Assert.Contains("debounce", report);
        Assert.Contains("idle completion wait", report);
        Assert.Contains("thread-pool queue", report);
    }

    [Fact]
    public void Format_UnknownPhasePair_IsReportedAsUnclassifiedRatherThanFoldedIntoANeighbour()
    {
        var events = new List<TerrainLatencyEvent>
        {
            Event(0, TerrainLatencyPhase.Edit),
            Event(100, TerrainLatencyPhase.SaveEnd),
            Event(150, TerrainLatencyPhase.RedrawEnd),
        };

        string report = TerrainLatencyReport.Format(events);

        Assert.Contains("unclassified", report);
    }

    [Fact]
    public void Format_SupersededRequest_ReportsItsWorkerTimeAsAbandonedInsteadOfDroppingIt()
    {
        var events = new List<TerrainLatencyEvent>(AppliedRequest(version: 2, origin: 1_000.0));
        events.InsertRange(0, new[]
        {
            Event(0, TerrainLatencyPhase.Edit, version: 1),
            Event(500, TerrainLatencyPhase.Dispatch, version: 1),
            Event(510, TerrainLatencyPhase.WorkerStart, version: 1),
            Event(600, TerrainLatencyPhase.CancelRequested, version: 1, detail: "superseded by #2"),
            Event(810, TerrainLatencyPhase.WorkerEnd, version: 1, detail: "cancelled"),
            Event(830, TerrainLatencyPhase.Closed, version: 1, detail: "superseded"),
        });

        string report = TerrainLatencyReport.Format(events).Replace(",", ".");

        Assert.Contains("[superseded]", report);
        Assert.Contains("abandoned worker time: 300.0 ms across 1", report);
        // Abandoned work must not pollute the applied-request breakdown.
        Assert.Contains("edit to visible (everything current): n=1", report);
    }

    [Fact]
    public void Format_OutputFamilyMarks_AreNamedByFamilyNotLumpedIntoDependentOutputs()
    {
        var events = new List<TerrainLatencyEvent>
        {
            Event(0, TerrainLatencyPhase.Edit),
            Event(10, TerrainLatencyPhase.WorkerStart),
            Event(50, TerrainLatencyPhase.GeometryReady),
            Event(90, $"{TerrainLatencyPhase.OutputFamilyPrefix}analyses"),
            Event(95, $"{TerrainLatencyPhase.OutputFamilyPrefix}zones"),
            Event(96, TerrainLatencyPhase.OutputsEnd),
            Event(97, TerrainLatencyPhase.RedrawEnd),
        };

        string report = TerrainLatencyReport.Format(events);

        Assert.Contains("analyses", report);
        Assert.Contains("zones", report);
    }

    [Fact]
    public void Format_SeparatesTimeNobodyIsWorkingFromTimeSpentWorking()
    {
        // The question that matters when a build looks slow: is anything actually running? A wait for
        // Rhino to run a posted callback is not shortened by optimizing any stage.
        string report = TerrainLatencyReport.Format(AppliedRequest());

        Assert.Contains("Who the wait belongs to", report);
        Assert.Contains("waiting for Rhino to run our code (nobody working)", report);
        Assert.Contains("MoleHill working", report);
        Assert.Contains("HOST", report);
        Assert.Contains("work", report);
    }

    [Fact]
    public void Format_InterimPublicationLandingAfterTheBuild_IsStillAttributed()
    {
        // When the dependent outputs are cheap, the interim publication is queued behind the host loop
        // and arrives after worker-end. That ordering used to print as the report's largest
        // "unclassified" interval - the exact gap this trace exists to close.
        var events = new List<TerrainLatencyEvent>
        {
            Event(0, TerrainLatencyPhase.Edit),
            Event(60, TerrainLatencyPhase.Due),
            Event(61, TerrainLatencyPhase.Dispatch),
            Event(61, TerrainLatencyPhase.SnapshotStart),
            Event(61, TerrainLatencyPhase.SnapshotEnd),
            Event(61, TerrainLatencyPhase.CloneEnd),
            Event(61, TerrainLatencyPhase.WorkerQueued),
            Event(62, TerrainLatencyPhase.WorkerStart),
            Event(400, TerrainLatencyPhase.GeometryReady),
            Event(406, TerrainLatencyPhase.InterimPublished),
            Event(409, TerrainLatencyPhase.OutputsEnd),
            Event(409, TerrainLatencyPhase.WorkerEnd, detail: "ok"),
            Event(409, TerrainLatencyPhase.WakePosted),
            Event(2_126, TerrainLatencyPhase.InterimVisible),
            Event(2_126, TerrainLatencyPhase.WakeRan),
            Event(2_127, TerrainLatencyPhase.CompletionDispatch),
            Event(2_128, TerrainLatencyPhase.MergeEnd),
            Event(2_130, TerrainLatencyPhase.DisplayEnd),
            Event(2_200, TerrainLatencyPhase.RedrawEnd),
            Event(2_201, TerrainLatencyPhase.Closed, detail: "applied"),
        };

        string report = TerrainLatencyReport.Format(events);

        Assert.DoesNotContain("unclassified", report);
        Assert.Contains("interim marshal + redraw", report);
    }

    [Fact]
    public void FormatCsv_EmitsOneRowPerEventRelativeToTheFirstTimestamp()
    {
        string csv = TerrainLatencyReport.FormatCsv(AppliedRequest());
        string[] lines = csv.TrimEnd('\n').Split('\n');

        Assert.Equal(19, lines.Length); // header + 18 events
        Assert.StartsWith("elapsed_ms,", lines[0]);
        Assert.StartsWith("0.000,", lines[1]);
    }

    [Fact]
    public void Format_NoEvents_SaysHowToEnableTheTrace()
    {
        Assert.Contains("mhLatencyTrace", TerrainLatencyReport.Format(Array.Empty<TerrainLatencyEvent>()));
    }
}
