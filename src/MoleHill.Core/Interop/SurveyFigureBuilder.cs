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
        var unmatched = new List<int>();
        var unmatchedCodes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var diagnostics = new List<SurveyReadDiagnostic>();
        int ignored = 0;

        // Open runs keyed by code+figure number. Insertion-ordered so end-of-file closure reports them in
        // the order they were opened rather than in hash order, which would differ between runs.
        var openRuns = new List<OpenRun>();

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
                continue;
            }

            OpenRun? run = Find(openRuns, parsed.RunKey);

            // A start marker means "this is a new figure" even when one of the same key is already open —
            // that is the whole reason a crew types it on a code they are about to reuse.
            if (parsed.IsStart && run != null)
            {
                Close(run, figures, spots, diagnostics, points, closed: false);
                openRuns.Remove(run);
                run = null;
            }

            if (run == null)
            {
                run = new OpenRun(parsed.RunKey, parsed.Code, parsed.FigureNumber, rule);
                openRuns.Add(run);
            }

            run.PointIndices.Add(index);
            run.ArcFlags.Add(parsed.IsArc);

            if (parsed.IsEnd || parsed.IsClose)
            {
                Close(run, figures, spots, diagnostics, points, closed: parsed.IsClose || rule.ClosedByDefault);
                openRuns.Remove(run);
            }
        }

        // Everything still open ran off the end of the file. That is ordinary — a crew rarely closes the
        // last figure — so it is closed and reported, not failed.
        foreach (OpenRun run in openRuns)
        {
            Close(run, figures, spots, diagnostics, points, closed: run.Rule.ClosedByDefault);
            if (run.PointIndices.Count >= MinimumFigurePoints)
            {
                diagnostics.Add(new SurveyReadDiagnostic(
                    points[run.PointIndices[^1]].LineNumber,
                    $"Run '{run.Code}' was still open at the end of the file and was closed there."));
            }
        }

        return new SurveyImportResult(figures, spots, unmatched, unmatchedCodes, diagnostics, ignored);
    }

    private static void Close(
        OpenRun run,
        List<SurveyFigure> figures,
        List<int> spots,
        List<SurveyReadDiagnostic> diagnostics,
        IReadOnlyList<SurveyPoint> points,
        bool closed)
    {
        if (run.PointIndices.Count >= MinimumFigurePoints)
        {
            figures.Add(new SurveyFigure(
                run.Code,
                run.Rule.Role,
                FieldCodeTable.ResolveLayer(run.Rule),
                run.PointIndices,
                run.ArcFlags,
                closed,
                run.FigureNumber));
            return;
        }

        // One point is not a line. Keeping it as a spot rather than discarding it is the difference
        // between a level the user can see and a level that vanished silently.
        foreach (int index in run.PointIndices)
        {
            spots.Add(index);
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
    }
}
