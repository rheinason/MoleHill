using System.Text.Json;
using System.Text.Json.Serialization;

namespace MoleHill.Rhino.Tests;

/// <summary>Where and on what a hosted-performance run executed. A baseline is only comparable on one machine.</summary>
public sealed class PerfEnvironment
{
    public string? Commit { get; set; }
    public string Machine { get; set; } = string.Empty;
    public int Processors { get; set; }
    public string Runtime { get; set; } = string.Empty;
    public string? RhinoVersion { get; set; }
    public string CoreAssembly { get; set; } = string.Empty;
    public bool CoreOptimized { get; set; }
    public bool TestsOptimized { get; set; }
    public string Timestamp { get; set; } = string.Empty;
}

public sealed class PerfMetricStats
{
    public double MedianMs { get; set; }
    public double P95Ms { get; set; }
    public double MinMs { get; set; }
    public double MaxMs { get; set; }
    public int Samples { get; set; }

    /// <summary>Median and nearest-rank p95. With five samples p95 is the maximum, which is the honest reading.</summary>
    public static PerfMetricStats From(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
            throw new ArgumentException("A metric needs at least one sample.", nameof(values));

        double[] sorted = values.OrderBy(static v => v).ToArray();
        int mid = sorted.Length / 2;
        double median = sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) * 0.5;
        int p95Rank = (int)Math.Ceiling(0.95 * sorted.Length) - 1;
        return new PerfMetricStats
        {
            MedianMs = median,
            P95Ms = sorted[Math.Clamp(p95Rank, 0, sorted.Length - 1)],
            MinMs = sorted[0],
            MaxMs = sorted[^1],
            Samples = sorted.Length
        };
    }
}

/// <summary>
/// The result of one hosted-performance run, and — the same shape — a committed baseline. Keeping them one
/// type means re-baselining is copying a result, so a baseline can never carry numbers a run did not produce.
/// </summary>
public sealed class PerfRunResult
{
    public int Schema { get; set; } = 1;
    public PerfEnvironment Environment { get; set; } = new();
    public List<string> Scenarios { get; set; } = new();
    public int SamplesPerScenario { get; set; }
    public int WarmupsDiscarded { get; set; }
    public SortedDictionary<string, PerfMetricStats> Metrics { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Stage detail lines from each scenario's last sample. Informational, never compared.</summary>
    public SortedDictionary<string, string> Details { get; set; } = new(StringComparer.Ordinal);
    public PerfComparison? Comparison { get; set; }
    public string? Error { get; set; }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };
}

public enum PerfVerdict
{
    Within,
    Regressed,
    Improved,
    New,
    Missing
}

public sealed class PerfMetricComparison
{
    public string Metric { get; set; } = string.Empty;
    public PerfVerdict Verdict { get; set; }
    public double? BaselineMedianMs { get; set; }
    public double? CurrentMedianMs { get; set; }
    public double? Ratio { get; set; }
}

public sealed class PerfComparison
{
    public string BaselinePath { get; set; } = string.Empty;
    public string? BaselineCommit { get; set; }
    public double Margin { get; set; }
    public double FloorMs { get; set; }

    /// <summary>Set when the baseline cannot be compared at all — another machine, say. The lane fails.</summary>
    public string? NotComparableReason { get; set; }

    public List<PerfMetricComparison> Metrics { get; set; } = new();

    [JsonIgnore]
    public bool Passed => NotComparableReason == null && Metrics.All(static m => m.Verdict != PerfVerdict.Regressed);

    /// <summary>
    /// A metric regresses only when its median is both <paramref name="margin"/> slower relatively and
    /// <paramref name="floorMs"/> slower absolutely. The relative test alone would fail on a 2 ms stage that
    /// became 3 ms; the absolute test alone would ignore a 20% regression in a 100 ms stage.
    ///
    /// Only scenarios the current run executed are compared, so running one scenario does not report the
    /// others as missing.
    /// </summary>
    public static PerfComparison Compare(
        PerfRunResult baseline,
        PerfRunResult current,
        string baselinePath,
        double margin,
        double floorMs)
    {
        var comparison = new PerfComparison
        {
            BaselinePath = baselinePath,
            BaselineCommit = baseline.Environment.Commit,
            Margin = margin,
            FloorMs = floorMs
        };

        if (!string.Equals(baseline.Environment.Machine, current.Environment.Machine, StringComparison.OrdinalIgnoreCase))
        {
            comparison.NotComparableReason =
                $"The baseline was recorded on '{baseline.Environment.Machine}', this run is on " +
                $"'{current.Environment.Machine}'. Timings do not transfer between machines; record a baseline here.";
            return comparison;
        }

        var ran = new HashSet<string>(current.Scenarios, StringComparer.Ordinal);
        foreach ((string metric, PerfMetricStats now) in current.Metrics)
        {
            if (!baseline.Metrics.TryGetValue(metric, out PerfMetricStats? then))
            {
                comparison.Metrics.Add(new PerfMetricComparison { Metric = metric, Verdict = PerfVerdict.New, CurrentMedianMs = now.MedianMs });
                continue;
            }

            double delta = now.MedianMs - then.MedianMs;
            PerfVerdict verdict = PerfVerdict.Within;
            if (delta > floorMs && now.MedianMs > then.MedianMs * (1.0 + margin))
                verdict = PerfVerdict.Regressed;
            else if (-delta > floorMs && now.MedianMs < then.MedianMs * (1.0 - margin))
                verdict = PerfVerdict.Improved;

            comparison.Metrics.Add(new PerfMetricComparison
            {
                Metric = metric,
                Verdict = verdict,
                BaselineMedianMs = then.MedianMs,
                CurrentMedianMs = now.MedianMs,
                Ratio = then.MedianMs > 0 ? now.MedianMs / then.MedianMs : null
            });
        }

        foreach ((string metric, PerfMetricStats then) in baseline.Metrics)
        {
            if (current.Metrics.ContainsKey(metric) || !ran.Contains(ScenarioOf(metric)))
                continue;

            comparison.Metrics.Add(new PerfMetricComparison { Metric = metric, Verdict = PerfVerdict.Missing, BaselineMedianMs = then.MedianMs });
        }

        comparison.Metrics.Sort(static (a, b) => string.CompareOrdinal(a.Metric, b.Metric));
        return comparison;
    }

    public static string ScenarioOf(string metric)
    {
        int slash = metric.IndexOf('/');
        return slash < 0 ? metric : metric[..slash];
    }
}
