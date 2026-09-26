using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Text.Json;
using MoleHill.Core.Engine;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// The hosted-performance lane: the full-stack benchmarks, run inside a real Rhino, sampled, and compared
/// against a committed baseline.
///
/// It exists because the benchmarks that measure what a user waits for — a terrain rebuild through
/// <c>TerrainBuildService</c> — need Rhino's native runtime, and Rhino 8.35 will not initialise that runtime
/// outside its own process (<c>docs/validation-lanes.md</c>, "Known broken"). So <c>validate.ps1 hosted-perf</c>
/// builds this assembly in Release, spawns a disposable <c>rhino-mcp</c> slot, loads the assembly into an
/// isolated <see cref="System.Runtime.Loader.AssemblyLoadContext"/> together with its own
/// <c>MoleHill.Core</c>, and calls <see cref="Start"/>. The isolation is the point: the slot has already
/// loaded the plug-in's <b>Debug</b> Core, and a plain <c>Assembly.LoadFrom</c> binds to it silently — a 25%
/// error measured on the geometry-heavy fixture. <see cref="Run"/> refuses to measure unoptimized code.
/// </summary>
public static class HostedPerformanceLane
{
    public sealed class Request
    {
        public string ResultPath { get; set; } = string.Empty;
        public string? BaselinePath { get; set; }
        public int Samples { get; set; } = 5;
        public int Warmups { get; set; } = 1;
        public double Margin { get; set; } = 0.20;
        public double FloorMs { get; set; } = 25.0;
        public string? Commit { get; set; }
        public List<string>? Scenarios { get; set; }
    }

    private sealed record Scenario(string Name, Action<PerfSampleRecorder> Sample);

    /// <summary>Every scenario the lane knows, in run order. Metric names are prefixed with the scenario name.</summary>
    private static readonly Scenario[] AllScenarios =
    [
        new("geometry-heavy", GeometryHeavyStackBenchmark.Sample),
        new("analysis-heavy", AnalysisHeavyBenchmark.Sample),
        new("interactive", InteractiveScaleBenchmark.Sample)
    ];

    public static IReadOnlyList<string> ScenarioNames => AllScenarios.Select(static s => s.Name).ToArray();

    /// <summary>
    /// Hosted entry point. Reads the request, starts the run on a background thread and returns at once.
    /// The run must not hold the script call: a <c>run_csharp</c> script executes on Rhino's UI thread, and
    /// blocking it both times out the router call and stalls the process being measured. The caller polls
    /// for <see cref="Request.ResultPath"/>, which is written last and atomically; progress lines go to
    /// <c>{ResultPath}.progress</c>.
    /// </summary>
    public static void Start(string requestPath)
    {
        Request request = JsonSerializer.Deserialize<Request>(File.ReadAllText(requestPath), PerfRunResult.JsonOptions)
            ?? throw new InvalidOperationException($"Could not read the request at {requestPath}.");

        var thread = new Thread(() => RunToFile(request))
        {
            IsBackground = true,
            Name = "MoleHill hosted performance lane"
        };
        thread.Start();
    }

    public static void RunToFile(Request request)
    {
        string progressPath = request.ResultPath + ".progress";
        void Progress(string line)
        {
            File.AppendAllText(progressPath, $"{DateTime.Now:HH:mm:ss} {line}{System.Environment.NewLine}");
        }

        PerfRunResult result;
        try
        {
            result = Run(request, Progress);
        }
        catch (Exception ex)
        {
            result = new PerfRunResult { Environment = CaptureEnvironment(request.Commit), Error = ex.ToString() };
            Progress("FAILED: " + ex.Message);
        }

        string temp = request.ResultPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(result, PerfRunResult.JsonOptions));
        File.Move(temp, request.ResultPath, overwrite: true);
    }

    public static PerfRunResult Run(Request request, Action<string> progress)
    {
        PerfEnvironment environment = CaptureEnvironment(request.Commit);
        if (!environment.CoreOptimized || !environment.TestsOptimized)
        {
            throw new InvalidOperationException(
                $"Refusing to measure unoptimized code: MoleHill.Core at {environment.CoreAssembly} " +
                $"optimized={environment.CoreOptimized}, tests optimized={environment.TestsOptimized}. " +
                "Build the test project in Release and load it into its own AssemblyLoadContext.");
        }

        Scenario[] scenarios = SelectScenarios(request.Scenarios);
        var result = new PerfRunResult
        {
            Environment = environment,
            Scenarios = scenarios.Select(static s => s.Name).ToList(),
            SamplesPerScenario = request.Samples,
            WarmupsDiscarded = request.Warmups
        };

        progress($"Core: {environment.CoreAssembly} (optimized)");
        var collected = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        foreach (Scenario scenario in scenarios)
        {
            for (int i = 0; i < request.Warmups + request.Samples; i++)
            {
                bool warmup = i < request.Warmups;
                Settle();
                var recorder = new PerfSampleRecorder();
                var timer = Stopwatch.StartNew();
                scenario.Sample(recorder);
                timer.Stop();
                progress($"{scenario.Name} {(warmup ? "warm-up" : $"sample {i - request.Warmups + 1}/{request.Samples}")} " +
                         $"{timer.Elapsed.TotalSeconds:N1} s");
                if (warmup)
                    continue;

                foreach ((string metric, double ms) in recorder.Values)
                {
                    string name = $"{scenario.Name}/{metric}";
                    if (!collected.TryGetValue(name, out List<double>? values))
                        collected[name] = values = new List<double>();
                    values.Add(ms);
                }

                foreach ((string metric, string detail) in recorder.Details)
                    result.Details[$"{scenario.Name}/{metric}"] = detail;
            }
        }

        foreach ((string metric, List<double> values) in collected)
            result.Metrics[metric] = PerfMetricStats.From(values);

        if (!string.IsNullOrEmpty(request.BaselinePath) && File.Exists(request.BaselinePath))
        {
            PerfRunResult baseline = JsonSerializer.Deserialize<PerfRunResult>(File.ReadAllText(request.BaselinePath), PerfRunResult.JsonOptions)
                ?? throw new InvalidOperationException($"Could not read the baseline at {request.BaselinePath}.");
            result.Comparison = PerfComparison.Compare(baseline, result, request.BaselinePath, request.Margin, request.FloorMs);
        }

        progress("done");
        return result;
    }

    private static Scenario[] SelectScenarios(List<string>? names)
    {
        if (names == null || names.Count == 0)
            return AllScenarios;

        var selected = new List<Scenario>();
        foreach (string name in names)
        {
            Scenario? match = AllScenarios.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            selected.Add(match ?? throw new ArgumentException(
                $"Unknown scenario '{name}'. Known: {string.Join(", ", ScenarioNames)}."));
        }

        return selected.ToArray();
    }

    /// <summary>Collect between samples so one sample's garbage is not charged to the next.</summary>
    private static void Settle()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static PerfEnvironment CaptureEnvironment(string? commit)
    {
        Assembly core = typeof(TinEngine).Assembly;
        Assembly tests = typeof(HostedPerformanceLane).Assembly;
        return new PerfEnvironment
        {
            Commit = commit,
            Machine = System.Environment.MachineName,
            Processors = System.Environment.ProcessorCount,
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            RhinoVersion = TryGetRhinoVersion(),
            CoreAssembly = RepositoryRelative(core.Location),
            CoreOptimized = IsOptimized(core),
            TestsOptimized = IsOptimized(tests),
            Timestamp = DateTimeOffset.Now.ToString("o")
        };
    }

    /// <summary>
    /// A path relative to the checkout (the directory holding <c>MoleHill.sln</c>), so a committed baseline
    /// names what was measured without carrying the recording machine's user or folder layout.
    /// </summary>
    private static string RepositoryRelative(string path)
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(path) ?? "."); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MoleHill.sln")))
                return Path.GetRelativePath(directory.FullName, path).Replace('\\', '/');
        }

        return Path.GetFileName(path);
    }

    private static bool IsOptimized(Assembly assembly)
    {
        var debuggable = assembly.GetCustomAttribute<DebuggableAttribute>();
        return debuggable == null || !debuggable.IsJITOptimizerDisabled;
    }

    private static string? TryGetRhinoVersion()
    {
        try
        {
            return global::Rhino.RhinoApp.Version.ToString();
        }
        catch (Exception)
        {
            // Outside a Rhino process (the managed lane's unit tests) there is no version to report.
            return null;
        }
    }
}
