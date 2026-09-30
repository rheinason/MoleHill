using System.Diagnostics;
using System.Runtime;
using System.Text.Json;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Geometry;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// A stress probe, not a gated benchmark: how far does one terrain scale before something breaks?
///
/// The fixture is a synthetic urban park the size of Central Park (4.0 x 0.8 km, ~40 m of relief) with
/// what makes that site hard: rock outcrops with near-vertical sides, a reservoir and a lake, three
/// graded lawns, two park drives, four transverse roads and a web of footpaths, and a few retaining
/// walls. The survey is a jittered LiDAR-style point cloud, laid down at a ladder of spacings from 8 m
/// (50k points) to 1 m (3.2M points).
///
/// At each rung the stack is built <b>one stage at a time</b> on the same cache (Triangulate, then + Grade
/// Pad, + Grade Path, + Retaining Wall, + Remesh, + analyses, + contours), so each step's wall time is the
/// new stage's own cost and a hang names the stage that hung, which a single whole-stack build would not.
/// Two edits follow on the full stack. A step that throws is recorded and its modifier dropped from the
/// later steps, so one failure does not hide what the rest of the stack does at that scale.
///
/// Every step runs under a budget through the build's own <c>shouldCancel</c>. A stage that does not
/// honour cancellation within the grace period is recorded as <b>hung</b> and the run ends there, because
/// its thread cannot be stopped and would contaminate every later measurement.
///
/// Run it inside a Rhino like the hosted lane: <c>tools/rhino-hosted-perf.py --entry ParkScaleStress</c>
/// (docs/validation-lanes.md, "park-stress").
/// </summary>
public static class ParkScaleStress
{
    public sealed class Request
    {
        public string ResultPath { get; set; } = string.Empty;

        /// <summary>Survey spacings in metres, coarse to fine. The ladder stops at the first rung that fails.</summary>
        public List<double>? Spacings { get; set; }

        public double StepBudgetSeconds { get; set; } = 600;

        public double CancelGraceSeconds { get; set; } = 120;

        public string? Commit { get; set; }

        /// <summary>
        /// Lay paths across the lawns and past the walls, as a real park's are. Grade Path refuses a road edge
        /// that crosses a hard constraint and then grades nothing, so this measures that refusal, not path
        /// grading. Off by default, which keeps every path clear of the lawns and walls.
        /// </summary>
        public bool PathsCrossConstraints { get; set; }

        /// <summary>
        /// "points" (default): a jittered LiDAR-style point survey at the rung's spacing. "contours": the same
        /// ground as contour lines every <see cref="ContourInterval"/>, traced over a grid at the rung's spacing,
        /// plus breaklines along the real breaks in slope (each outcrop's toe and rim, each basin's shore and
        /// bank foot), the way a surveyed or digitised topo arrives.
        /// </summary>
        public string? SurveyMode { get; set; }

        /// <summary>Contour interval in metres for <see cref="SurveyMode"/> "contours".</summary>
        public double ContourInterval { get; set; } = 0.5;

        /// <summary>Triangulate's contour mode ("auto", "constrained", "vertices"); null keeps the card's default.</summary>
        public string? ContourMode { get; set; }

        /// <summary>The Remesh card's mode: "isotropic" (default, tiled) or "global" (the whole-mesh remesher).</summary>
        public string? RemeshMode { get; set; }

        /// <summary>
        /// When set, the mesh and hard constraints the Remesh stage receives are written here as
        /// <c>remesh-input-{spacing}.bin</c>, for a Core-level replay (<c>RemeshInputFile</c>).
        /// </summary>
        public string? ExportRemeshInputFolder { get; set; }
    }

    public sealed class StressResult
    {
        public PerfEnvironment? Environment { get; set; }
        public double StepBudgetSeconds { get; set; }
        public List<RungResult> Rungs { get; set; } = new();
        public string? StoppedBecause { get; set; }
        public string? Error { get; set; }
    }

    public sealed class RungResult
    {
        public double Spacing { get; set; }
        public int SurveyPoints { get; set; }
        public double FixtureSeconds { get; set; }
        public List<StepResult> Steps { get; set; } = new();
    }

    public sealed class StepResult
    {
        public string Name { get; set; } = string.Empty;

        /// <summary><c>ok</c>, <c>failed</c>, <c>no-mesh</c>, <c>over-budget</c> (cancelled cleanly) or <c>hung</c>.</summary>
        public string Status { get; set; } = string.Empty;
        public double WallMs { get; set; }
        public int Vertices { get; set; }
        public int Faces { get; set; }
        public double ManagedMb { get; set; }
        public double WorkingSetMb { get; set; }
        public double PeakWorkingSetMb { get; set; }
        public string? Error { get; set; }
        public List<string> Stages { get; set; } = new();
        public List<string> Diagnostics { get; set; } = new();
        public int DiagnosticsTotal { get; set; }
    }

    private const double ParkLength = 4000.0;
    private const double ParkWidth = 800.0;
    private const int MaxDiagnosticsPerStep = 40;
    private static readonly double[] DefaultSpacings = [8.0, 4.0, 2.0, 1.4, 1.0];

    /// <summary>Hosted entry point: returns at once and writes the result file when done (see <see cref="HostedPerformanceLane.Start"/>).</summary>
    public static void Start(string requestPath)
    {
        Request request = JsonSerializer.Deserialize<Request>(File.ReadAllText(requestPath), PerfRunResult.JsonOptions)
            ?? throw new InvalidOperationException($"Could not read the request at {requestPath}.");

        var thread = new Thread(() => RunToFile(request))
        {
            IsBackground = true,
            Name = "MoleHill park-scale stress"
        };
        thread.Start();
    }

    public static void RunToFile(Request request)
    {
        string progressPath = request.ResultPath + ".progress";
        void Progress(string line) =>
            File.AppendAllText(progressPath, $"{DateTime.Now:HH:mm:ss} {line}{System.Environment.NewLine}");

        var result = new StressResult { StepBudgetSeconds = request.StepBudgetSeconds };
        try
        {
            result.Environment = HostedPerformanceLane.CaptureEnvironmentFor(request.Commit);
            Run(request, result, Progress);
        }
        catch (Exception ex)
        {
            result.Error = ex.ToString();
            Progress("FAILED: " + ex.Message);
        }

        string temp = request.ResultPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(result, PerfRunResult.JsonOptions));
        File.Move(temp, request.ResultPath, overwrite: true);
    }

    private static void Run(Request request, StressResult result, Action<string> progress)
    {
        foreach (double spacing in request.Spacings is { Count: > 0 } ? request.Spacings : DefaultSpacings.ToList())
        {
            Settle();
            var fixtureTimer = Stopwatch.StartNew();
            bool contourSurvey = string.Equals(request.SurveyMode, "contours", StringComparison.OrdinalIgnoreCase);
            ParkFixture fixture = ParkFixture.Create(spacing, request.PathsCrossConstraints, contourSurvey ? request.ContourInterval : 0.0);
            if (!string.IsNullOrWhiteSpace(request.ContourMode))
                fixture.Triangulate.ContourMode = request.ContourMode;
            if (!string.IsNullOrWhiteSpace(request.RemeshMode))
                fixture.Remesh.Mode = request.RemeshMode;
            fixtureTimer.Stop();
            var rung = new RungResult
            {
                Spacing = spacing,
                SurveyPoints = fixture.SurveyPointCount,
                FixtureSeconds = fixtureTimer.Elapsed.TotalSeconds
            };
            result.Rungs.Add(rung);
            progress(fixture.ContourCurves.Count > 0
                ? $"=== rung {spacing:0.##} m: {fixture.ContourCurves.Count:N0} contours ({fixture.SurveyPointCount:N0} vertices) every {request.ContourInterval:0.##} m, {fixture.BreaklineCount:N0} breaklines, contour mode {fixture.Triangulate.ContourMode} (fixture {rung.FixtureSeconds:N1} s)"
                : $"=== rung {spacing:0.##} m: {fixture.SurveyPointCount:N0} survey points (fixture {rung.FixtureSeconds:N1} s)");

            string? stop = RunRung(request, fixture, rung, progress);
            if (stop != null)
            {
                result.StoppedBecause = $"{spacing:0.##} m: {stop}";
                progress("STOP: " + result.StoppedBecause);
                return;
            }
        }

        progress("done");
    }

    /// <summary>Runs one rung's steps. Returns why the ladder must stop, or null to climb on.</summary>
    private static string? RunRung(Request request, ParkFixture fixture, RungResult rung, Action<string> progress)
    {
        var cache = new TerrainRuntimeCache();
        var service = new TerrainBuildService();
        TerrainDefinition terrain = fixture.Terrain;
        var dropped = new HashSet<object>(ReferenceEqualityComparer.Instance);

        // Progressive stack: each step adds one stage to what the previous step built.
        var steps = new (string Name, object? Item)[]
        {
            ("triangulate", fixture.Triangulate),
            ("+ grade pad", fixture.Pad),
            ("+ grade path", fixture.Path),
            ("+ retaining wall", fixture.Wall),
            ("+ remesh", fixture.Remesh),
            ("+ analyses", null),
            ("+ contours", fixture.Contours)
        };

        terrain.Modifiers.Clear();
        terrain.Analyses.Clear();
        terrain.Annotations.Clear();
        bool anyOverBudget = false;
        foreach ((string name, object? item) in steps)
        {
            switch (item)
            {
                case ModifierDefinition modifier:
                    terrain.Modifiers.Add(modifier);
                    break;
                case AnnotationDefinition annotation:
                    terrain.Annotations.Add(annotation);
                    break;
                default:
                    terrain.Analyses.AddRange(fixture.Analyses);
                    break;
            }

            StepResult step = RunStep(request, name, service, fixture.Snapshot, cache, progress);
            rung.Steps.Add(step);
            if (item == fixture.Wall && !string.IsNullOrWhiteSpace(request.ExportRemeshInputFolder))
                ExportRemeshInput(request.ExportRemeshInputFolder!, rung.Spacing, fixture, cache, progress);
            if (step.Status == "hung")
                return $"'{name}' did not honour cancellation within {request.CancelGraceSeconds:N0} s";
            if (step.Status == "over-budget")
                anyOverBudget = true;
            if (step.Status is "failed" or "over-budget" or "no-mesh")
            {
                // Take the failing stage out, so the later steps still say what the rest of the stack does.
                if (item is ModifierDefinition failedModifier)
                    terrain.Modifiers.Remove(failedModifier);
                else if (item is AnnotationDefinition failedAnnotation)
                    terrain.Annotations.Remove(failedAnnotation);
                else
                    terrain.Analyses.Clear();
                dropped.Add(item ?? fixture.Analyses);
                if (step.Status == "no-mesh" && item == fixture.Triangulate)
                    return "Triangulate produced no mesh";
            }
        }

        // Edits on the full (surviving) stack, as a designer would make them.
        if (!dropped.Contains(fixture.Pad))
        {
            fixture.Pad.SlopeAngle = 26.5;
            StepResult padEdit = RunStep(request, "edit: pad slope", service, fixture.Snapshot, cache, progress);
            rung.Steps.Add(padEdit);
            if (padEdit.Status == "hung")
                return "the pad edit did not honour cancellation";
            anyOverBudget |= padEdit.Status == "over-budget";
        }

        fixture.NudgeSurveyPoint();
        StepResult pointEdit = RunStep(request, fixture.ContourCurves.Count > 0 ? "edit: contour vertex" : "edit: survey point", service, fixture.Snapshot, cache, progress);
        rung.Steps.Add(pointEdit);
        if (pointEdit.Status == "hung")
            return "the survey-point edit did not honour cancellation";
        anyOverBudget |= pointEdit.Status == "over-budget";

        // Edits that should cost nothing, because they cannot change the terrain: the price of the cache
        // itself, of a card that has no inputs yet, and of a card's name.
        var structural = new List<(string Name, Action Change)>
        {
            ("rebuild: no change", static () => { }),
            ("add: empty grade pad", () => terrain.Modifiers.Insert(1, new GradePadModifierDefinition
            {
                Id = Guid.Parse("66666666-6666-6666-6666-666666666666"),
                Label = "Empty Grade Pad"
            })),
        };
        if (terrain.Modifiers.Contains(fixture.Remesh))
            structural.Add(("rename: remesh card", () => fixture.Remesh.Label = "Remesh (renamed)"));

        foreach ((string name, Action change) in structural)
        {
            change();
            StepResult step = RunStep(request, name, service, fixture.Snapshot, cache, progress);
            rung.Steps.Add(step);
            if (step.Status == "hung")
                return $"'{name}' did not honour cancellation";
            anyOverBudget |= step.Status == "over-budget";
        }

        return anyOverBudget ? $"a step exceeded the {request.StepBudgetSeconds:N0} s budget" : null;
    }

    /// <summary>Writes the Retaining Wall stage's output — the Remesh stage's input — for a Core replay.</summary>
    private static void ExportRemeshInput(string folder, double spacing, ParkFixture fixture, TerrainRuntimeCache cache, Action<string> progress)
    {
        string key = TerrainStageKey.ForMode(TerrainBuildMode.Final, TerrainStageKey.CreateModifier(fixture.Wall));
        if (!cache.StageEntries.TryGetValue(key, out StageCacheEntry? entry) || entry.MeshOutput == null ||
            !RhinoGeometryConversions.TryExtractMeshData(entry.MeshOutput, out double[] vertices, out int vertexCount, out int[] faces, out int faceCount, out _))
        {
            progress("export: no Retaining Wall stage output to write");
            return;
        }

        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"remesh-input-{spacing:0.###}.bin");
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write(vertexCount);
            for (int i = 0; i < vertexCount * 3; i++)
                writer.Write(vertices[i]);
            writer.Write(faceCount);
            for (int i = 0; i < faceCount * 3; i++)
                writer.Write(faces[i]);
            writer.Write(entry.PersistentHardConstraints.Count);
            foreach (var constraint in entry.PersistentHardConstraints)
            {
                writer.Write(constraint.PointCount);
                writer.Write(constraint.IsClosed);
                writer.Write(constraint.PreserveInputElevation);
                for (int i = 0; i < constraint.PointCount * 3; i++)
                    writer.Write(constraint.Points[i]);
            }
        }

        progress($"export: {vertexCount:N0} vertices, {faceCount:N0} faces, {entry.PersistentHardConstraints.Count:N0} constraints -> {path}");
    }

    private static StepResult RunStep(
        Request request,
        string name,
        TerrainBuildService service,
        TerrainBuildSnapshot snapshot,
        TerrainRuntimeCache cache,
        Action<string> progress)
    {
        var step = new StepResult { Name = name };
        var budget = TimeSpan.FromSeconds(request.StepBudgetSeconds);
        var timer = new Stopwatch();
        TerrainBuildResult? built = null;
        Exception? error = null;
        string lastProgress = string.Empty;

        var worker = new Thread(() =>
        {
            try
            {
                timer.Start();
                built = service.Build(
                    snapshot,
                    cache,
                    TerrainBuildMode.Final,
                    shouldCancel: () => timer.Elapsed > budget,
                    reportProgress: p => lastProgress = p.Format());
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                timer.Stop();
            }
        })
        {
            IsBackground = true,
            Name = "park-stress " + name
        };

        worker.Start();
        bool finished = worker.Join(budget + TimeSpan.FromSeconds(request.CancelGraceSeconds));
        step.WallMs = timer.Elapsed.TotalMilliseconds;
        CaptureMemory(step);

        if (!finished)
        {
            step.Status = "hung";
            step.Error = $"Still running {step.WallMs / 1000:N0} s in; last progress: {lastProgress}";
        }
        else if (error is OperationCanceledException)
        {
            step.Status = "over-budget";
            step.Error = $"Cancelled at the {budget.TotalSeconds:N0} s budget; last progress: {lastProgress}";
        }
        else if (error != null)
        {
            step.Status = "failed";
            step.Error = error.ToString();
        }
        else
        {
            TerrainBuildResult result = built!;
            step.Status = result.PrimaryMesh == null ? "no-mesh" : "ok";
            step.Vertices = result.PrimaryMesh?.Vertices.Count ?? 0;
            step.Faces = result.PrimaryMesh?.Faces.Count ?? 0;
            foreach (TerrainBuildTiming timing in result.Timings)
            {
                string cached = timing.IsCacheHit ? " (cached)" : string.Empty;
                string detail = string.IsNullOrEmpty(timing.Detail) ? string.Empty : $" [{timing.Detail}]";
                step.Stages.Add($"{timing.Elapsed.TotalMilliseconds,10:N0} ms  {timing.Stage}{cached}{detail}");

                // A modifier that fails hands its input mesh on and the build still "succeeds". That is the
                // product's contract, but a probe that called it ok would misreport the stage's cost as its
                // no-op cost - the first run's Grade Path did exactly that at every scale.
                if (!timing.IsCacheHit && timing.Detail == "failed" && step.Status == "ok")
                    step.Status = "stage-failed";
            }

            step.DiagnosticsTotal = result.Diagnostics.Count;
            step.Diagnostics.AddRange(result.Diagnostics.Take(MaxDiagnosticsPerStep));
            // The earthwork figures, to the last digit, so a speed change can be shown not to move them.
            foreach (TerrainAnalysisSummary summary in result.AnalysisResults.Where(s => s.CutVolume != 0 || s.FillVolume != 0))
                step.Diagnostics.Add(FormattableString.Invariant($"earthwork: cut {summary.CutVolume:R} fill {summary.FillVolume:R} abs max {summary.CutFillDisplayAbsMax:R}"));
        }

        progress(
            $"  {name,-20} {step.Status,-11} {step.WallMs / 1000,8:N1} s  " +
            $"{step.Vertices,10:N0} v {step.Faces,10:N0} f  managed {step.ManagedMb,7:N0} MB  " +
            $"ws {step.WorkingSetMb,7:N0} MB (peak {step.PeakWorkingSetMb:N0})" +
            (step.Error == null ? string.Empty : $"  :: {FirstLine(step.Error)}"));
        return step;
    }

    private static void CaptureMemory(StepResult step)
    {
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        step.ManagedMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
        step.WorkingSetMb = process.WorkingSet64 / (1024.0 * 1024.0);
        step.PeakWorkingSetMb = process.PeakWorkingSet64 / (1024.0 * 1024.0);
    }

    private static string FirstLine(string text)
    {
        int end = text.IndexOfAny(['\r', '\n']);
        return end < 0 ? text : text[..end];
    }

    private static void Settle()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    /// <summary>The synthetic park. Everything is deterministic: no <see cref="Random"/>, only integer hashes.</summary>
    private sealed class ParkFixture
    {
        public required TerrainDefinition Terrain { get; init; }
        public required TerrainBuildSnapshot Snapshot { get; init; }
        public required TriangulateModifierDefinition Triangulate { get; init; }
        public required GradePadModifierDefinition Pad { get; init; }
        public required GradePathModifierDefinition Path { get; init; }
        public required RetainingWallModifierDefinition Wall { get; init; }
        public required RemeshModifierDefinition Remesh { get; init; }
        public required List<AnalysisDefinition> Analyses { get; init; }
        public required ContourAnnotationDefinition Contours { get; init; }
        public required Point3d[] Survey { get; init; }

        /// <summary>The contour survey, when the fixture was built from contours (empty for a point survey).</summary>
        public List<Curve> ContourCurves { get; init; } = new();

        public int BreaklineCount { get; init; }

        public int SurveyPointCount => ContourCurves.Count > 0
            ? ContourCurves.Sum(static c => c is PolylineCurve p ? p.PointCount : 0)
            : Survey.Length;

        /// <summary>A sheep meadow, a great lawn and a terrace plaza, as plan rectangles.</summary>
        private static readonly (double X0, double Y0, double X1, double Y1)[] Lawns =
        [
            (820, 380, 1060, 600),
            (2180, 300, 2440, 520),
            (1480, 380, 1560, 430)
        ];

        /// <summary>Retaining walls as plan segments, clear of the lawns and the drives.</summary>
        private static readonly (double X0, double Y0, double X1, double Y1)[] Walls =
        [
            (300, 250, 420, 245),
            (1700, 560, 1840, 580),
            (3500, 200, 3620, 230)
        ];

        public static ParkFixture Create(double spacing, bool pathsCrossConstraints, double contourInterval = 0.0)
        {
            var triangulate = new TriangulateModifierDefinition
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                PeelBoundaryTriangles = false,
                Tolerance = 0.01
            };
            var pad = new GradePadModifierDefinition
            {
                Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                SlopeAngle = 18.0,
                MaxDistance = 25.0
            };
            var path = new GradePathModifierDefinition
            {
                Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                Width = 4.0,
                SlopeAngle = 26.5,
                MaxDistance = 12.0
            };
            var wall = new RetainingWallModifierDefinition
            {
                Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                MaxWallWidth = 2.0
            };
            var remesh = new RemeshModifierDefinition
            {
                Id = Guid.Parse("55555555-5555-5555-5555-555555555555"),
                Mode = "isotropic",
                EdgeLength = 0.0
            };
            var waterflow = new WaterflowAnalysisDefinition { Id = Guid.Parse("a0000000-0000-0000-0000-000000000004") };
            var analyses = new List<AnalysisDefinition>
            {
                new SlopeAnalysisDefinition { Id = Guid.Parse("a0000000-0000-0000-0000-000000000001") },
                new CutFillAnalysisDefinition { Id = Guid.Parse("a0000000-0000-0000-0000-000000000002") },
                waterflow,
                new CatchmentAnalysisDefinition { Id = Guid.Parse("a0000000-0000-0000-0000-000000000005") },
                new PondingAnalysisDefinition { Id = Guid.Parse("a0000000-0000-0000-0000-000000000006") }
            };
            var contours = new ContourAnnotationDefinition
            {
                Id = Guid.Parse("c0000000-0000-0000-0000-000000000001"),
                Interval = 1.0,
                MajorEveryNth = 5
            };

            var terrain = new TerrainDefinition
            {
                TerrainId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
                Name = $"Park {spacing:0.##} m",
                GlobalTolerance = 0.01
            };
            var snapshot = new TerrainBuildSnapshot
            {
                Terrain = terrain,
                ModelAbsoluteTolerance = 0.001,
                ModelUnitSystem = UnitSystem.Meters
            };

            Point3d[] survey = Array.Empty<Point3d>();
            var contourCurves = new List<Curve>();
            int breaklineCount = 0;
            if (contourInterval > 0.0)
            {
                contourCurves = CreateContours(spacing, contourInterval);
                SetCurves(snapshot, triangulate.Contours, 111, contourCurves.ToArray());
                Curve[] breaklines = CreateBreaklines(spacing);
                breaklineCount = breaklines.Length;
                SetCurves(snapshot, triangulate.Breaklines, 121, breaklines);
            }
            else
            {
                survey = CreateSurvey(spacing);
                SetSurvey(snapshot, triangulate.Points, survey, 101);
            }

            SetCurves(snapshot, pad.Boundaries, 211, Lawns.Select(l => Lawn(l.X0, l.Y0, l.X1, l.Y1)).ToArray());
            SetCurves(snapshot, path.Paths, 307, CreatePaths(pathsCrossConstraints));

            // Walls as toe/top rail pairs 1.2 m apart, stepping 2.5 m.
            SetCurves(snapshot, wall.WallCurves, 401,
                Walls.SelectMany(w => new[] { WallRail(w.X0, w.Y0, w.X1, w.Y1, 0.0), WallRail(w.X0, w.Y0, w.X1, w.Y1, 1.2) }).ToArray());

            var sources = new List<Point3d>();
            for (int i = 1; i <= 5; i++)
            {
                for (int j = 1; j <= 2; j++)
                {
                    double x = ParkLength * i / 6.0;
                    double y = ParkWidth * j / 3.0;
                    sources.Add(new Point3d(x, y, Elevation(x, y) + 1.0));
                }
            }

            SetGeometry(snapshot, waterflow.Sources, 503, sources.Select(p => (GeometryBase)new Point(p)).ToArray());

            return new ParkFixture
            {
                Terrain = terrain,
                Snapshot = snapshot,
                Triangulate = triangulate,
                Pad = pad,
                Path = path,
                Wall = wall,
                Remesh = remesh,
                Analyses = analyses,
                Contours = contours,
                Survey = survey,
                ContourCurves = contourCurves,
                BreaklineCount = breaklineCount
            };
        }

        /// <summary>
        /// Raise one survey point near the park centre by 5 cm: the smallest edit a survey can take. For a contour
        /// survey, move the contour vertex nearest the centre 30 cm east, as redrawing one kink of a contour would.
        /// </summary>
        public void NudgeSurveyPoint()
        {
            if (ContourCurves.Count > 0)
            {
                int bestCurve = -1, bestVertex = -1;
                double best = double.MaxValue;
                for (int c = 0; c < ContourCurves.Count; c++)
                {
                    if (ContourCurves[c] is not PolylineCurve polyline)
                        continue;
                    for (int k = 1; k < polyline.PointCount - 1; k++)
                    {
                        Point3d q = polyline.Point(k);
                        double d = ((q.X - ParkLength / 2) * (q.X - ParkLength / 2)) + ((q.Y - ParkWidth / 2) * (q.Y - ParkWidth / 2));
                        if (d < best)
                        {
                            best = d;
                            bestCurve = c;
                            bestVertex = k;
                        }
                    }
                }

                if (bestCurve >= 0)
                {
                    var edited = (PolylineCurve)ContourCurves[bestCurve].Duplicate();
                    edited.SetPoint(bestVertex, edited.Point(bestVertex) + new Vector3d(0.3, 0, 0));
                    ContourCurves[bestCurve] = edited;
                    SetCurves(Snapshot, Triangulate.Contours, 112, ContourCurves.ToArray());
                }

                return;
            }

            int index = Survey.Length / 2;
            Survey[index] = Survey[index] + new Vector3d(0, 0, 0.05);
            SetSurvey(Snapshot, Triangulate.Points, Survey, 102);
        }

        /// <summary>
        /// Contours of the park's ground every <paramref name="interval"/> metres, traced over a lattice at
        /// <paramref name="spacing"/> by the same marching pass the Contours annotation uses, so a contour carries
        /// one vertex per lattice cell it crosses.
        /// </summary>
        private static List<Curve> CreateContours(double spacing, double interval)
        {
            int nx = (int)(ParkLength / spacing) + 1;
            int ny = (int)(ParkWidth / spacing) + 1;
            var vertices = new double[nx * ny * 3];
            double minZ = double.MaxValue, maxZ = double.MinValue;
            for (int i = 0; i < nx; i++)
            {
                for (int j = 0; j < ny; j++)
                {
                    double x = Math.Min(i * spacing, ParkLength), y = Math.Min(j * spacing, ParkWidth);
                    double z = Elevation(x, y);
                    int k = (i * ny) + j;
                    vertices[k * 3] = x;
                    vertices[k * 3 + 1] = y;
                    vertices[k * 3 + 2] = z;
                    minZ = Math.Min(minZ, z);
                    maxZ = Math.Max(maxZ, z);
                }
            }

            var faces = new int[(nx - 1) * (ny - 1) * 6];
            int n = 0;
            for (int i = 0; i < nx - 1; i++)
            {
                for (int j = 0; j < ny - 1; j++)
                {
                    int a = (i * ny) + j, b = a + ny;
                    faces[n++] = a; faces[n++] = b; faces[n++] = b + 1;
                    faces[n++] = a; faces[n++] = b + 1; faces[n++] = a + 1;
                }
            }

            var levels = new List<double>();
            for (long step = (long)Math.Ceiling(minZ / interval); step * interval <= maxZ; step++)
                levels.Add(step * interval);

            var curves = new List<Curve>();
            foreach (MoleHill.Core.Analysis.ContourLevel contourLevel in MoleHill.Core.Analysis.ContourGenerator.Generate(
                         vertices, nx * ny, faces, faces.Length / 3, levels, 1e-6))
            {
                foreach (MoleHill.Core.Analysis.ContourPolyline polyline in contourLevel.Polylines)
                {
                    if (polyline.PointCount < 3)
                        continue;
                    var points = new List<Point3d>(polyline.PointCount + 1);
                    for (int k = 0; k < polyline.PointCount; k++)
                        points.Add(new Point3d(polyline.PointsXyz[k * 3], polyline.PointsXyz[(k * 3) + 1], contourLevel.Z));
                    if (polyline.IsClosed && points[0].DistanceTo(points[^1]) > 1e-9)
                        points.Add(points[0]);
                    curves.Add(new PolylineCurve(points));
                }
            }

            return curves;
        }

        /// <summary>
        /// The ground's real breaks in slope: each outcrop's toe and rim (the 2 m rock wall of
        /// <see cref="Elevation"/>), and each basin's shore and bank foot, stationed at the rung's spacing.
        /// </summary>
        private static Curve[] CreateBreaklines(double spacing)
        {
            var curves = new List<Curve>();
            Curve Ring(double cx, double cy, double rx, double ry)
            {
                int segments = Math.Max(12, (int)Math.Ceiling(2 * Math.PI * Math.Max(rx, ry) / spacing));
                var points = new List<Point3d>(segments + 1);
                for (int s = 0; s < segments; s++)
                {
                    double t = 2 * Math.PI * s / segments;
                    double x = Math.Clamp(cx + (rx * Math.Cos(t)), 0, ParkLength);
                    double y = Math.Clamp(cy + (ry * Math.Sin(t)), 0, ParkWidth);
                    points.Add(new Point3d(x, y, Elevation(x, y)));
                }

                points.Add(points[0]);
                return new PolylineCurve(points);
            }

            for (int k = 0; k < 40; k++)
            {
                double cx = 100 + (Lattice(k, 0, 5) + 1) * 0.5 * (ParkLength - 200);
                double cy = 60 + (Lattice(k, 1, 5) + 1) * 0.5 * (ParkWidth - 120);
                double radius = 12 + (Lattice(k, 2, 5) + 1) * 0.5 * 30;
                curves.Add(Ring(cx, cy, radius + 1.0, radius + 1.0));
                curves.Add(Ring(cx, cy, radius - 1.0, radius - 1.0));
            }

            foreach ((double cx, double cy, double rx, double ry, double bank) in new[] { (2900.0, 420.0, 330.0, 230.0, 12.0), (1250.0, 250.0, 180.0, 90.0, 6.0) })
            {
                double foot = 1.0 - (bank / Math.Min(rx, ry));
                curves.Add(Ring(cx, cy, rx, ry));
                curves.Add(Ring(cx, cy, rx * foot, ry * foot));
            }

            return curves.ToArray();
        }

        private static Point3d[] CreateSurvey(double spacing)
        {
            int nx = (int)(ParkLength / spacing) + 1;
            int ny = (int)(ParkWidth / spacing) + 1;
            var points = new Point3d[nx * ny];
            for (int i = 0; i < nx; i++)
            {
                for (int j = 0; j < ny; j++)
                {
                    // LiDAR ground returns are not a lattice: jitter each by up to a third of the spacing,
                    // clamped inside the park so the boundary stays straight.
                    double x = Math.Clamp(i * spacing + Lattice(i, j, 11) * spacing / 3, 0, ParkLength);
                    double y = Math.Clamp(j * spacing + Lattice(i, j, 29) * spacing / 3, 0, ParkWidth);
                    points[i * ny + j] = new Point3d(x, y, Elevation(x, y));
                }
            }

            return points;
        }

        private static Curve Lawn(double x0, double y0, double x1, double y1)
        {
            // Level at the mean of the ground it replaces, so it cuts at one end and fills at the other.
            double z = 0;
            const int n = 8;
            for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                z += Elevation(x0 + (x1 - x0) * (i + 0.5) / n, y0 + (y1 - y0) * (j + 0.5) / n);
            z /= n * n;
            return new PolylineCurve(new[]
            {
                new Point3d(x0, y0, z), new Point3d(x1, y0, z), new Point3d(x1, y1, z), new Point3d(x0, y1, z), new Point3d(x0, y0, z)
            });
        }

        private static Curve WallRail(double x0, double y0, double x1, double y1, double offset)
        {
            double dx = x1 - x0, dy = y1 - y0, len = Math.Sqrt(dx * dx + dy * dy);
            double ox = -dy / len * offset, oy = dx / len * offset;
            double z0 = Elevation(x0, y0) + (offset > 0 ? 2.5 : 0.0);
            double z1 = Elevation(x1, y1) + (offset > 0 ? 2.5 : 0.0);
            return new PolylineCurve(new[] { new Point3d(x0 + ox, y0 + oy, z0), new Point3d(x1 + ox, y1 + oy, z1) });
        }

        /// <summary>
        /// Two drives down the long sides, four transverse roads across, and eighteen wandering footpaths.
        /// Unless <paramref name="crossConstraints"/>, a footpath is only kept when it stays 40 m clear of every
        /// lawn and wall, and the transverse road that would cross the great lawn runs west of it instead.
        /// Paths still cross each other.
        /// </summary>
        private static Curve[] CreatePaths(bool crossConstraints)
        {
            var curves = new List<Curve>
            {
                Draped(Meander(80, 690, 3920, 700, 0, 14.0)),
                Draped(Meander(80, 110, 3920, 100, 1, 14.0))
            };
            foreach (double x in new[] { 620.0, 1420.0, crossConstraints ? 2320.0 : 2080.0, 3320.0 })
                curves.Add(Draped(Meander(x, 10, x + 40, 790, 2 + (int)x, 6.0)));

            int kept = 0;
            for (int k = 0; kept < 18 && k < 500; k++)
            {
                double ax = 150 + (Lattice(k, 0, 71) + 1) * 0.5 * 3700;
                double ay = 150 + (Lattice(k, 1, 71) + 1) * 0.5 * 500;
                double bx = Math.Clamp(ax + Lattice(k, 2, 71) * 500, 60, ParkLength - 60);
                double by = Math.Clamp(ay + Lattice(k, 3, 71) * 300, 60, ParkWidth - 60);
                List<Point2d> plan = Meander(ax, ay, bx, by, 100 + k, 18.0);
                if (!crossConstraints && !plan.All(p => IsClear(p, 40.0)))
                    continue;
                curves.Add(Draped(plan));
                kept++;
            }

            return curves.ToArray();
        }

        private static bool IsClear(Point2d p, double clearance)
        {
            foreach ((double x0, double y0, double x1, double y1) in Lawns.Concat(Walls))
            {
                if (p.X > Math.Min(x0, x1) - clearance && p.X < Math.Max(x0, x1) + clearance &&
                    p.Y > Math.Min(y0, y1) - clearance && p.Y < Math.Max(y0, y1) + clearance)
                    return false;
            }

            return true;
        }

        private static List<Point2d> Meander(double ax, double ay, double bx, double by, int seed, double amplitude)
        {
            double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
            if (len < 100)
            {
                bx = ax < ParkLength / 2 ? ax + 300 : ax - 300;
                len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
            }

            int segments = Math.Max(4, (int)(len / 40));
            double nx = -(by - ay) / len, ny = (bx - ax) / len;
            var points = new List<Point2d>(segments + 1);
            for (int s = 0; s <= segments; s++)
            {
                double t = s / (double)segments;
                double wobble = s == 0 || s == segments ? 0 : amplitude * Math.Sin(t * Math.PI * 3 + seed);
                points.Add(new Point2d(ax + (bx - ax) * t + nx * wobble, ay + (by - ay) * t + ny * wobble));
            }

            return points;
        }

        private static Curve Draped(List<Point2d> plan) =>
            new PolylineCurve(plan.Select(p => new Point3d(p.X, p.Y, Elevation(p.X, p.Y))));

        private static void SetSurvey(TerrainBuildSnapshot snapshot, SourceReferenceSet set, Point3d[] survey, ulong fingerprint)
        {
            SetGeometry(snapshot, set, fingerprint, new PointCloud(survey));
        }

        private static void SetCurves(TerrainBuildSnapshot snapshot, SourceReferenceSet set, ulong seed, params Curve[] curves)
        {
            ulong fingerprint = seed;
            foreach (Curve curve in curves)
                fingerprint = unchecked((fingerprint * 397) ^ curve.DataCRC(0));
            SetGeometry(snapshot, set, fingerprint, curves);
        }

        private static void SetGeometry(TerrainBuildSnapshot snapshot, SourceReferenceSet set, ulong fingerprint, params GeometryBase[] geometry)
        {
            var objects = new List<ResolvedSourceObject>(geometry.Length);
            set.ObjectIds.Clear();
            for (int i = 0; i < geometry.Length; i++)
            {
                // Stable ids, so a replaced survey is the same source object with new geometry.
                var id = new Guid(set.GetHashCode(), (short)i, 0, new byte[8]);
                set.ObjectIds.Add(id);
                BoundingBox bbox = geometry[i].GetBoundingBox(true);
                objects.Add(new ResolvedSourceObject
                {
                    ObjectId = id,
                    Geometry = geometry[i],
                    LocalBoundingBox = bbox,
                    WorldBoundingBox = bbox,
                    GeometryDataCrc = geometry[i].DataCRC(0)
                });
            }

            snapshot.SourceObjects[set] = objects;
            snapshot.SourceFingerprints[set] = fingerprint;
        }
    }

    /// <summary>
    /// The park's ground, in metres. A tilt from 12 m at the south end to 32 m at the north, rolling
    /// swales, value noise, a reservoir and a lake with steep banks, and forty schist outcrops whose
    /// sides approach vertical over two metres.
    /// </summary>
    internal static double Elevation(double x, double y)
    {
        double z = 12.0 + 20.0 * (x / ParkLength)
                   + 3.0 * Math.Sin(x / 190.0) * Math.Cos(y / 140.0)
                   + 1.5 * ValueNoise(x / 25.0, y / 25.0);

        z -= 7.0 * Basin(x, y, 2900, 420, 330, 230, 12.0);   // reservoir
        z -= 3.0 * Basin(x, y, 1250, 250, 180, 90, 6.0);     // lake

        for (int k = 0; k < 40; k++)
        {
            double cx = 100 + (Lattice(k, 0, 5) + 1) * 0.5 * (ParkLength - 200);
            double cy = 60 + (Lattice(k, 1, 5) + 1) * 0.5 * (ParkWidth - 120);
            double radius = 12 + (Lattice(k, 2, 5) + 1) * 0.5 * 30;
            double height = 3 + (Lattice(k, 3, 5) + 1) * 0.5 * 6;
            double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
            z += height * SmoothStep(radius + 1.0, radius - 1.0, d);   // a 2 m wall of rock
        }

        return z;
    }

    private static double Basin(double x, double y, double cx, double cy, double rx, double ry, double bank)
    {
        double u = (x - cx) / rx, v = (y - cy) / ry;
        double r = Math.Sqrt(u * u + v * v);
        double scale = Math.Min(rx, ry);
        return SmoothStep(1.0, 1.0 - bank / scale, r);
    }

    private static double SmoothStep(double edge0, double edge1, double value)
    {
        double t = Math.Clamp((value - edge0) / (edge1 - edge0), 0.0, 1.0);
        return t * t * (3 - 2 * t);
    }

    private static double ValueNoise(double gx, double gy)
    {
        int ix = (int)Math.Floor(gx), iy = (int)Math.Floor(gy);
        double fx = gx - ix, fy = gy - iy;
        double sx = fx * fx * (3 - 2 * fx), sy = fy * fy * (3 - 2 * fy);
        double a = Lattice(ix, iy, 0), b = Lattice(ix + 1, iy, 0), c = Lattice(ix, iy + 1, 0), d = Lattice(ix + 1, iy + 1, 0);
        return a + (b - a) * sx + (c - a) * sy + (a - b - c + d) * sx * sy;
    }

    /// <summary>An integer hash mapped to [-1, 1]; stable across runtimes.</summary>
    private static double Lattice(int x, int y, int salt)
    {
        unchecked
        {
            uint h = (uint)x * 374761393u + (uint)y * 668265263u + (uint)salt * 2246822519u;
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return h / (double)uint.MaxValue * 2.0 - 1.0;
        }
    }
}
