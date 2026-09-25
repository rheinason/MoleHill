namespace MoleHill.Core.Interop;

/// <summary>
/// Turns coded points into figures by walking the file and keeping one open run per code.
///
/// <b>File order is the ordering authority, not the point number.</b> A surveyor renumbers, and a file
/// merged from two days' work can carry numbers that decrease partway through. Sorting by number would
/// reorder a kerb line into a zigzag — so this walks the points exactly as they were written, and the
/// point number is treated as the label it is.
///
/// A run closes on an end marker, on a close marker, when a start marker reopens the same key, or at end
/// of file. Runs of different codes interleave freely, which is what happens when a crew shoots two kerb
/// lines and a toe in one pass.
///
/// A point carrying the table's continuation suffix (<c>EP-</c>) joins the open run of its key, or, when
/// that run has already been closed, reopens the most recently closed one and carries on from its last
/// point. That is the suffix's whole purpose: the crew ended a line, shot something else, and came back
/// to it. Figures are therefore only materialized once the file has been walked, so a run can be
/// reopened after it was ended.
/// </summary>
public static class SurveyFigureBuilder
{
    /// <summary>A run of fewer than this many points cannot be linework and is kept as a spot instead.</summary>
    private const int MinimumFigurePoints = 2;

    public static SurveyImportResult Build(IReadOnlyList<SurveyPoint> points, FieldCodeTable table)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(table);

        var figures = new List<SurveyFigure>();
        var spots = new List<int>();
        var spotLayers = new List<string>();
        var unmatched = new List<int>();
        var unmatchedCodes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var diagnostics = new List<SurveyReadDiagnostic>();
        int ignored = 0;

        // Open runs keyed by code+figure number. Insertion-ordered so end-of-file closure reports them in
        // the order they were opened rather than in hash order, which would differ between runs.
        var openRuns = new List<OpenRun>();

        // Runs that have been closed, in close order. A reopened run leaves this list and rejoins it at
        // the end when it closes again, so figure order stays the order the figures were finished.
        var finishedRuns = new List<OpenRun>();

        for (int index = 0; index < points.Count; index++)
        {
            SurveyPoint point = points[index];
            ParsedFieldCode parsed = FieldCodeParser.Parse(point.RawDescription, table);

            if (!parsed.HasCode)
            {
                unmatched.Add(index);
                Increment(unmatchedCodes, SurveyImportResult.BlankCodeLabel);
                continue;
            }

            FieldCodeRule? rule = table.Find(parsed.Code);
            if (rule == null)
            {
                unmatched.Add(index);
                Increment(unmatchedCodes, parsed.Code);
                continue;
            }

            if (rule.Role == FieldCodeRole.Ignore)
            {
                ignored++;
                continue;
            }

            if (rule.Role == FieldCodeRole.Spot)
            {
                spots.Add(index);
                spotLayers.Add(FieldCodeTable.ResolveLayer(rule));
                continue;
            }

            OpenRun? run = Find(openRuns, parsed.RunKey);

            // A start marker means "this is a new figure" even when one of the same key is already open —
            // that is the whole reason a crew types it on a code they are about to reuse.
            if (parsed.IsStart && run != null)
            {
                Finish(run, openRuns, finishedRuns, closed: false);
                run = null;
            }

            // A continuation with no open run reopens the last finished run of the same key. A start
            // marker on the same point contradicts it; the start marker is the more deliberate of the two.
            if (run == null && parsed.IsContinuation && !parsed.IsStart)
            {
                run = FindLast(finishedRuns, parsed.RunKey);
                if (run != null)
                {
                    finishedRuns.Remove(run);
                    run.IsClosed = false;
                    openRuns.Add(run);
                }
                else
                {
                    diagnostics.Add(new SurveyReadDiagnostic(
                        point.LineNumber,
                        $"'{point.RawDescription.Trim()}' continues a run, but no earlier '{parsed.Code}' run exists; a new run was started."));
                }
            }

            if (run == null)
            {
                run = new OpenRun(parsed.RunKey, parsed.Code, parsed.FigureNumber, rule);
                openRuns.Add(run);
            }

            run.PointIndices.Add(index);
            run.ArcFlags.Add(parsed.IsArc);

            if (parsed.IsEnd || parsed.IsClose)
                Finish(run, openRuns, finishedRuns, closed: parsed.IsClose || rule.ClosedByDefault);
        }

        foreach (OpenRun run in finishedRuns)
            Materialize(run, figures, spots, spotLayers, diagnostics, points);

        // Everything still open ran off the end of the file. That is ordinary — a crew rarely closes the
        // last figure — so it is closed and reported, not failed.
        foreach (OpenRun run in openRuns)
        {
            run.IsClosed = run.Rule.ClosedByDefault;
            Materialize(run, figures, spots, spotLayers, diagnostics, points);
            if (run.PointIndices.Count >= MinimumFigurePoints)
            {
                diagnostics.Add(new SurveyReadDiagnostic(
                    points[run.PointIndices[^1]].LineNumber,
                    $"Run '{run.Code}' was still open at the end of the file and was closed there."));
            }
        }

        return new SurveyImportResult(figures, spots, unmatched, unmatchedCodes, diagnostics, ignored, spotLayers);
    }

    private static void Finish(OpenRun run, List<OpenRun> openRuns, List<OpenRun> finishedRuns, bool closed)
    {
        run.IsClosed = closed;
        openRuns.Remove(run);
        finishedRuns.Add(run);
    }

    private static void Materialize(
        OpenRun run,
        List<SurveyFigure> figures,
        List<int> spots,
        List<string> spotLayers,
        List<SurveyReadDiagnostic> diagnostics,
        IReadOnlyList<SurveyPoint> points)
    {
        if (run.PointIndices.Count >= MinimumFigurePoints)
        {
            figures.Add(new SurveyFigure(
                run.Code,
                run.Rule.Role,
                FieldCodeTable.ResolveLayer(run.Rule),
                run.PointIndices,
                run.ArcFlags,
                run.IsClosed,
                run.FigureNumber));
            return;
        }

        // One point is not a line. Keeping it as a spot rather than discarding it is the difference
        // between a level the user can see and a level that vanished silently. It goes to the Spot role's
        // own layer: the run's rule describes linework, and no Spot rule claimed this point.
        foreach (int index in run.PointIndices)
        {
            spots.Add(index);
            spotLayers.Add(FieldCodeTable.DefaultLayerFor(FieldCodeRole.Spot));
            diagnostics.Add(new SurveyReadDiagnostic(
                points[index].LineNumber,
                $"Run '{run.Code}' held only one point and was kept as a spot level."));
        }
    }

    private static OpenRun? Find(List<OpenRun> runs, string key)
    {
        foreach (OpenRun run in runs)
        {
            if (string.Equals(run.Key, key, StringComparison.OrdinalIgnoreCase))
                return run;
        }

        return null;
    }

    private static OpenRun? FindLast(List<OpenRun> runs, string key)
    {
        for (int i = runs.Count - 1; i >= 0; i--)
        {
            if (string.Equals(runs[i].Key, key, StringComparison.OrdinalIgnoreCase))
                return runs[i];
        }

        return null;
    }

    private static void Increment(Dictionary<string, int> counts, string code) =>
        counts[code] = counts.TryGetValue(code, out int existing) ? existing + 1 : 1;

    private sealed class OpenRun
    {
        public OpenRun(string key, string code, int? figureNumber, FieldCodeRule rule)
        {
            Key = key;
            Code = code;
            FigureNumber = figureNumber;
            Rule = rule;
        }

        public string Key { get; }

        public string Code { get; }

        public int? FigureNumber { get; }

        public FieldCodeRule Rule { get; }

        public List<int> PointIndices { get; } = new();

        public List<bool> ArcFlags { get; } = new();

        /// <summary>Whether the run closes into a loop; decided when it is finished, reset if it is reopened.</summary>
        public bool IsClosed { get; set; }
    }
}
