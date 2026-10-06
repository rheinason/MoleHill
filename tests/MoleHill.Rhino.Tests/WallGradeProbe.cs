using System.Diagnostics;
using System.Text.Json;
using MoleHill.Core.Engine;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using MoleHill.Shared;
using Rhino;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// A sweep of the Retaining Wall's grade mode at small scale, measured on the finished surface.
///
/// Grade mode is three steps (TerrainBuildService.RetainingWalls): batter each rail away from its partner
/// with <c>PathGrader</c>, insert the rails into the graded mesh, and fall back to a constrained rebuild when
/// the insert is rejected. Each step can fail quietly and still leave a build that reports success, so this
/// probe does not trust diagnostics. For every case it cuts sections square to the wall and compares the
/// surface against the batter the card asked for: falling from the top rail at the slope until it meets
/// the ground behind, and rising from the toe rail until it meets the ground in front.
///
/// The sweep crosses survey spacing, slope, the plan gap between the rails, and wall shape (straight, a
/// mitred bend, an arc). Run it inside Rhino like the other hosted probes:
/// <c>tools/rhino-hosted-perf.py --entry WallGradeProbe</c>.
/// </summary>
public static class WallGradeProbe
{
    public sealed class Request
    {
        public string ResultPath { get; set; } = string.Empty;
        public List<double>? Spacings { get; set; }
        public List<double>? Slopes { get; set; }
        public List<double>? Gaps { get; set; }
        public List<string>? Shapes { get; set; }

        /// <summary>
        /// What else is in the stack: <c>alone</c>, <c>remesh-before</c>, <c>pad-before</c>, <c>wall-then-pad</c>,
        /// <c>path-near</c> (a path within the top batter's reach) or <c>terrace</c> (a second, stepped wall).
        /// </summary>
        public List<string>? Contexts { get; set; }

        /// <summary>Also run each case in breakline-only mode, as the control.</summary>
        public bool IncludeBreaklineControl { get; set; }

        public double CaseBudgetSeconds { get; set; } = 90;

        /// <summary>Keep every timing row and diagnostic of each case in its notes (for a narrowed rerun).</summary>
        public bool DumpDiagnostics { get; set; }

        /// <summary>When set, capture each case's rail insertion to this folder instead of running the sweep.</summary>
        public string? CaptureFolder { get; set; }

        /// <summary>When set, also write every grading call of each case as a Core regression test to this folder.</summary>
        public string? CoreCaseFolder { get; set; }
    }

    public sealed class CaseResult
    {
        public string Shape { get; set; } = string.Empty;
        public string Context { get; set; } = string.Empty;
        public double Spacing { get; set; }
        public double Slope { get; set; }
        public double Gap { get; set; }
        public string Mode { get; set; } = string.Empty;
        public string? Error { get; set; }
        public double BuildMs { get; set; }
        public int Vertices { get; set; }
        public int Faces { get; set; }

        /// <summary>Did <c>PathGrader</c> produce a graded mesh (not the "failed" timing row)?</summary>
        public bool Graded { get; set; }
        public string? GradingTier { get; set; }

        /// <summary><c>local</c>, <c>rebuild</c>, or <c>failed</c> (the upstream mesh was kept).</summary>
        public string Insertion { get; set; } = string.Empty;
        public int BoundaryLoops { get; set; }
        public int NonManifoldEdges { get; set; }

        /// <summary>Worst |measured − designed| over every section station, as a fraction of wall height.</summary>
        public double WorstBatterError { get; set; }
        public string? WorstBatterAt { get; set; }
        public double MeanBatterError { get; set; }
        public int Stations { get; set; }
        public int StationsMissed { get; set; }
        public List<string> Notes { get; set; } = new();
    }

    private const double GroundBase = 10.0;
    private const double TopAboveGround = 2.5;
    private const double ToeBelowGround = 1.0;
    private const double SiteX = 60.0;
    private const double SiteY = 44.0;

    public static void Start(string requestPath)
    {
        Request request = JsonSerializer.Deserialize<Request>(File.ReadAllText(requestPath), PerfRunResult.JsonOptions)
            ?? throw new InvalidOperationException($"Could not read the request at {requestPath}.");
        HostedPowerThrottling.OptOut();
        var thread = new Thread(() => RunToFile(request)) { IsBackground = true, Name = "MoleHill wall grade probe" };
        thread.Start();
    }

    public static void RunToFile(Request request)
    {
        string progressPath = request.ResultPath + ".progress";
        void Progress(string line) =>
            File.AppendAllText(progressPath, $"{DateTime.Now:HH:mm:ss} {line}{System.Environment.NewLine}");

        var results = new List<CaseResult>();
        string? error = null;
        try
        {
            var modes = new List<string> { RetainingWallModifierDefinition.GradeMode };
            if (request.IncludeBreaklineControl)
                modes.Add(RetainingWallModifierDefinition.BreaklineOnlyMode);

            foreach (string context in request.Contexts is { Count: > 0 } ? request.Contexts : new List<string> { "alone" })
            foreach (string shape in request.Shapes is { Count: > 0 } ? request.Shapes : new List<string> { "straight", "bend", "arc" })
            foreach (double gap in request.Gaps is { Count: > 0 } ? request.Gaps : new List<double> { 0.3, 1.2 })
            foreach (double spacing in request.Spacings is { Count: > 0 } ? request.Spacings : new List<double> { 2.0, 1.0, 0.5 })
            foreach (double slope in request.Slopes is { Count: > 0 } ? request.Slopes : new List<double> { 15, 20, 25, 30, 33.7, 45, 60 })
            foreach (string mode in modes)
            {
                if (request.CaptureFolder != null)
                {
                    string file = Path.Combine(request.CaptureFolder, $"{mode}-{context}-{shape}-g{gap}-s{spacing}-a{slope}.json");
                    Progress(mode + " " + CaptureInsertCase(context, shape, spacing, slope, gap, file, mode));
                    continue;
                }

                s_coreCaseFolder = request.CoreCaseFolder;
                CaseResult result = RunCaseWithBudget(context, shape, spacing, slope, gap, mode, request.CaseBudgetSeconds, request.DumpDiagnostics);
                results.Add(result);
                Progress(Describe(result));
                if (result.Error?.StartsWith("hung", StringComparison.Ordinal) == true)
                    throw new InvalidOperationException("A case ignored cancellation; stopping, since its thread cannot be stopped.");
            }
        }
        catch (Exception ex)
        {
            error = ex.ToString();
            Progress("FAILED: " + ex.Message);
        }

        Progress("done");
        string temp = request.ResultPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new { Error = error, Cases = results }, PerfRunResult.JsonOptions));
        File.Move(temp, request.ResultPath, overwrite: true);
    }

    private static string Describe(CaseResult c) =>
        $"{c.Context,-13} {c.Shape,-8} gap {c.Gap,4:0.0} sp {c.Spacing,4:0.0} slope {c.Slope,5:0.#} {c.Mode,-10} " +
        $"graded {(c.Graded ? "yes" : "NO "),-3} tier {c.GradingTier ?? "-",-12} insert {c.Insertion,-8} " +
        $"loops {c.BoundaryLoops} nm {c.NonManifoldEdges} batter worst {c.WorstBatterError:P0} mean {c.MeanBatterError:P0} " +
        $"({c.StationsMissed}/{c.Stations} missed) {c.BuildMs,6:N0} ms" +
        (c.WorstBatterError > 0.05 ? $"  worst: {c.WorstBatterAt}" : string.Empty) +
        (c.Error == null ? string.Empty : "  ERROR " + c.Error.Split('\n')[0]) +
        (c.Notes.Count == 0 ? string.Empty : "  | " + string.Join(" | ", c.Notes));

    /// <summary>
    /// Runs a case on its own thread under the build's cancellation. A case that still has not returned a
    /// minute past its budget is reported as hung: its thread cannot be stopped, so the sweep must end there.
    /// </summary>
    private static string? s_coreCaseFolder;

    private static CaseResult RunCaseWithBudget(
        string context, string shape, double spacing, double slope, double gap, string mode, double budgetSeconds, bool dump)
    {
        CaseResult? result = null;
        var budget = TimeSpan.FromSeconds(budgetSeconds);
        var clock = Stopwatch.StartNew();
        var worker = new Thread(() => result = RunCase(context, shape, spacing, slope, gap, mode, () => clock.Elapsed > budget, dump))
        {
            IsBackground = true,
            Name = "wall grade probe case"
        };
        worker.Start();
        if (worker.Join(budget + TimeSpan.FromSeconds(60)))
            return result!;

        return new CaseResult
        {
            Context = context, Shape = shape, Spacing = spacing, Slope = slope, Gap = gap, Mode = mode,
            BuildMs = clock.Elapsed.TotalMilliseconds,
            Error = $"hung: still building {clock.Elapsed.TotalSeconds:N0} s in, past a {budgetSeconds:N0} s budget"
        };
    }

    internal static CaseResult RunCase(
        string context, string shape, double spacing, double slope, double gap, string mode, Func<bool>? shouldCancel = null, bool dump = false)
    {
        var result = new CaseResult { Context = context, Shape = shape, Spacing = spacing, Slope = slope, Gap = gap, Mode = mode };
        try
        {
            Point3d[] toePlan = WallPlan(shape);
            Point3d[] topPlan = Offset(toePlan, gap);
            List<GeometryBase> curves = WallCurves(context, toePlan, topPlan, gap).Cast<GeometryBase>().ToList();

            var triangulate = new TriangulateModifierDefinition
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                PeelBoundaryTriangles = false,
                Tolerance = 0.01
            };
            var wall = new RetainingWallModifierDefinition
            {
                Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                Mode = mode,
                SlopeAngle = slope,
                MaxWallWidth = Math.Max(2.0, gap * 2.0)
            };
            var terrain = new TerrainDefinition
            {
                TerrainId = Guid.NewGuid(),
                Name = "Wall grade probe",
                GlobalTolerance = 0.01,
                Modifiers = new List<ModifierDefinition> { triangulate }
            };
            var snapshot = new TerrainBuildSnapshot
            {
                Terrain = terrain,
                ModelAbsoluteTolerance = 0.001,
                ModelUnitSystem = UnitSystem.Meters
            };
            SetGeometry(snapshot, triangulate.Points, 101, new PointCloud(Survey(spacing)));
            SetGeometry(snapshot, wall.WallCurves, 401, curves.ToArray());
            AddContext(context, snapshot, terrain, wall, toePlan, topPlan);

            var timer = Stopwatch.StartNew();
            TerrainBuildResult build = new TerrainBuildService().Build(
                snapshot, new TerrainRuntimeCache(), TerrainBuildMode.Final, shouldCancel);
            result.BuildMs = timer.Elapsed.TotalMilliseconds;
            if (s_coreCaseFolder != null)
            {
                string folder = Path.Combine(s_coreCaseFolder, $"{mode}-{context}-{shape}-g{gap}-s{spacing}-a{slope}");
                Directory.CreateDirectory(folder);
                foreach (TerrainCoreCaseTestExport export in TerrainCoreCaseTestExporter.CreateAll(snapshot))
                    File.WriteAllText(Path.Combine(folder, export.FileName), export.SourceCode);
            }

            Mesh? mesh = build.PrimaryMesh;
            if (mesh == null)
            {
                result.Error = "no mesh";
                return result;
            }

            result.Vertices = mesh.Vertices.Count;
            result.Faces = mesh.Faces.Count;
            ReadStages(build, result);
            if (dump)
            {
                result.Notes.AddRange(build.Timings.Select(t => $"T {t.Elapsed.TotalMilliseconds:N0} ms {t.Stage} [{t.Detail}]"));
                result.Notes.AddRange(build.Diagnostics.Select(d => "D " + d.Replace("\r\n", " / ")));
            }

            int[] faces = mesh.Faces.ToIntArray(true);
            var topology = MeshTopologyValidator.AnalyzeBoundaryGraph(faces, faces.Length / 3);
            result.BoundaryLoops = topology.BoundaryComponentCount;
            result.NonManifoldEdges = topology.NonManifoldEdgeCount;

            // The terrace's second wall owns the ground behind the first, so only the toe side has a
            // designed profile there.
            if (mode == RetainingWallModifierDefinition.GradeMode)
                MeasureBatter(mesh, toePlan, topPlan, slope, measureTop: context != "terrace", result);

            if (dump && mode == RetainingWallModifierDefinition.GradeMode)
            {
                // The full toe-side section at 35% along: designed, measured and ground, every 25 cm.
                var toeCurve = new Polyline(toePlan).ToPolylineCurve();
                double t = toeCurve.Domain.ParameterAt(0.35);
                Point3d start = toeCurve.PointAt(t);
                Vector3d tangent = toeCurve.TangentAt(t);
                var outward = new Vector3d(tangent.Y, -tangent.X, 0);
                outward.Unitize();
                double railZ = Rail(toePlan, -ToeBelowGround).PointAt(t).Z;
                double rise = Math.Tan(slope * Math.PI / 180.0);
                for (double d = 0; d <= 6.0; d += 0.25)
                {
                    Point3d p = start + (outward * d);
                    Point3d[] hits = Intersection.MeshLine(mesh, new Line(p.X, p.Y, -1000, p.X, p.Y, 1000));
                    double designed = Math.Min(Ground(p.X, p.Y), railZ + (d * rise));
                    string measured = hits is { Length: > 0 } ? string.Join("/", hits.Select(h => h.Z.ToString("0.000"))) : "-";
                    result.Notes.Add($"S d={d:0.00} designed {designed:0.000} ground {Ground(p.X, p.Y):0.000} measured {measured}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            result.Error = "over budget (cancelled)";
        }
        catch (Exception ex)
        {
            result.Error = ex.ToString();
        }

        return result;
    }

    /// <summary>
    /// Replays the grade-mode wall stage up to its local rail insertion, outside the stage, and writes the
    /// graded mesh and the prepared rail constraints to <paramref name="outPath"/> as a Core test case. The
    /// private stage helpers are called by reflection so the captured inputs are the ones the stage builds.
    /// Returns a one-line verdict of the insertion, which must match what the full build reported.
    /// </summary>
    internal static string CaptureInsertCase(
        string context, string shape, double spacing, double slope, double gap, string outPath,
        string mode = RetainingWallModifierDefinition.GradeMode)
    {
        const double tolerance = 0.001;
        Point3d[] toePlan = WallPlan(shape);
        Point3d[] topPlan = Offset(toePlan, gap);
        List<Curve> wallCurves = WallCurves(context, toePlan, topPlan, gap);

        // The upstream terrain: everything in the stack before the wall.
        var triangulate = new TriangulateModifierDefinition { Id = Guid.Parse("11111111-1111-1111-1111-111111111111"), PeelBoundaryTriangles = false, Tolerance = 0.01 };
        var wall = new RetainingWallModifierDefinition { Id = Guid.Parse("44444444-4444-4444-4444-444444444444"), Mode = RetainingWallModifierDefinition.GradeMode, SlopeAngle = slope, MaxWallWidth = Math.Max(2.0, gap * 2.0) };
        var terrain = new TerrainDefinition { TerrainId = Guid.NewGuid(), Name = "capture", GlobalTolerance = 0.01, Modifiers = new List<ModifierDefinition> { triangulate } };
        var snapshot = new TerrainBuildSnapshot { Terrain = terrain, ModelAbsoluteTolerance = 0.001, ModelUnitSystem = UnitSystem.Meters };
        SetGeometry(snapshot, triangulate.Points, 101, new PointCloud(Survey(spacing)));
        SetGeometry(snapshot, wall.WallCurves, 401, wallCurves.Cast<GeometryBase>().ToArray());
        AddContext(context, snapshot, terrain, wall, toePlan, topPlan);
        terrain.Modifiers.Remove(wall);
        Mesh upstream = new TerrainBuildService().Build(snapshot, new TerrainRuntimeCache(), TerrainBuildMode.Final).PrimaryMesh
            ?? throw new InvalidOperationException("no upstream mesh");

        var plan = RetainingWallPlannerCore.Plan(wallCurves, wall.MaxWallWidth, curveParsingTolerance: tolerance, curveCleanupTolerance: tolerance, buildSolids: false);
        if (!RhinoGeometryConversions.TryExtractMeshData(upstream, out double[] v0, out int vc0, out int[] f0, out int fc0, out string? extractError))
            throw new InvalidOperationException(extractError);
        List<Core.Grading.PathGrader.PathDefinition> grades = RetainingWallGradePlanner.Build(
            plan.Walls,
            new RetainingWallGradePlanner.Options
            {
                FillAngleDeg = slope,
                Toe = RetainingWallGradePlanner.SideSlopes.Inherit,
                Top = RetainingWallGradePlanner.SideSlopes.Inherit,
                Tolerance = tolerance
            });
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        Type service = typeof(TerrainBuildService);
        Mesh gradedMesh = upstream;
        if (mode == RetainingWallModifierDefinition.GradeMode)
        {
            var graded = Core.Grading.PathGrader.Grade(v0, vc0, f0, fc0, grades.ToArray(),
                Array.Empty<SurfaceRemesher.ConstraintPolyline>(), out string? gradeWarning, tolerance, preferSplitKeep: false)
                ?? throw new InvalidOperationException("grading failed: " + gradeWarning);
            gradedMesh = (Mesh)service.GetMethod("FinalizeGradingMesh", flags)!.Invoke(null, new object[]
            {
                RhinoGeometryConversions.BuildMesh(graded.Vertices, graded.VertexCount, graded.Faces, graded.FaceCount),
                "Retaining Wall",
                new TerrainBuildResult()
            })!;
        }
        var raw = new List<SurfaceRemesher.ConstraintPolyline>();
        foreach (var planned in plan.Walls)
            raw.AddRange((SurfaceRemesher.ConstraintPolyline[])service.GetMethod("BuildWallConstraintCurves", flags)!.Invoke(null, new object[] { planned.Rails, tolerance })!);
        var prepared = (List<SurfaceRemesher.ConstraintPolyline>)service.GetMethod("PrepareWallConstraintsForRemesh", flags)!
            .Invoke(null, new object[] { gradedMesh, raw, tolerance })!;

        if (!RhinoGeometryConversions.TryExtractMeshData(gradedMesh, out double[] v1, out int vc1, out int[] f1, out int fc1, out extractError))
            throw new InvalidOperationException(extractError);
        bool inserted = Core.Grading.MeshConstraintTopologyInserter.TryInsert(
            v1, vc1, f1, fc1, prepared, tolerance,
            out double[] v2, out int vc2, out int[] f2, out int fc2, out string? insertError);
        var before = MeshTopologyValidator.AnalyzeBoundaryGraph(f1, fc1);
        var after = inserted ? MeshTopologyValidator.AnalyzeBoundaryGraph(f2, fc2) : before;

        File.WriteAllText(outPath, JsonSerializer.Serialize(new
        {
            Case = $"{context} {shape} gap {gap} spacing {spacing} slope {slope}",
            Tolerance = tolerance,
            Vertices = v1[..(vc1 * 3)],
            Faces = f1[..(fc1 * 3)],
            Constraints = prepared.Select(c => new { Points = c.Points[..(c.PointCount * 3)], c.PointCount, c.IsClosed, c.PreserveInputElevation }),
            UpstreamVertices = v0[..(vc0 * 3)],
            UpstreamFaces = f0[..(fc0 * 3)],
            Grades = grades.Select(g => new
            {
                Xy = g.XyVertices, Z = g.ZValues, g.VertexCount, g.SlopeAngleDeg, g.FillSlopeAngleDeg, g.MaxDistance, g.IsClosed,
                Normals = g.OutwardNormals
            })
        }));

        return $"{context} {shape}: graded {vc1} v / {fc1} f; insert {(inserted ? "ok" : "FAILED: " + insertError)}; " +
               $"loops {before.BoundaryComponentCount} -> {after.BoundaryComponentCount}, nm {before.NonManifoldEdgeCount} -> {after.NonManifoldEdgeCount}, " +
               $"open chains {after.HasOpenBoundaryChains}; {prepared.Count} constraints";
    }

    /// <summary>The wall rails for a case: toe and top, plus the terrace's second, stepped wall.</summary>
    private static List<Curve> WallCurves(string context, Point3d[] toePlan, Point3d[] topPlan, double gap)
    {
        var curves = new List<Curve> { Rail(toePlan, -ToeBelowGround), Rail(topPlan, TopAboveGround) };
        if (context == "terrace")
        {
            // A second wall stepped 5 m behind the first, holding the ground another 2 m up. The first
            // wall's top batter now falls toward the second wall's toe batter, and they meet between.
            Point3d[] toe2 = Offset(topPlan, 5.0);
            curves.Add(Rail(toe2, TopAboveGround - 0.5));
            curves.Add(Rail(Offset(toe2, gap), TopAboveGround + 2.0));
        }

        return curves;
    }

    private static PolylineCurve Rail(Point3d[] plan, double aboveGround) =>
        new(plan.Select(p => new Point3d(p.X, p.Y, Ground(p.X, p.Y) + aboveGround)));

    /// <summary>Adds the stack around the wall. The wall always goes in; where depends on the context.</summary>
    private static void AddContext(
        string context,
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RetainingWallModifierDefinition wall,
        Point3d[] toePlan,
        Point3d[] topPlan)
    {
        var toeCurve = new Polyline(toePlan).ToPolylineCurve();
        double tMid = toeCurve.Domain.ParameterAt(0.5);
        Point3d mid = toeCurve.PointAt(tMid);
        Vector3d tangent = toeCurve.TangentAt(tMid);
        var outward = new Vector3d(tangent.Y, -tangent.X, 0);
        outward.Unitize();

        switch (context)
        {
            case "remesh-before":
                terrain.Modifiers.Add(new RemeshModifierDefinition { Id = Guid.Parse("55555555-5555-5555-5555-555555555555"), Mode = "isotropic" });
                terrain.Modifiers.Add(wall);
                return;
            case "pad-before":
            case "wall-then-pad":
            {
                // A level pad on the toe side, 7 m out: its boundary is a hard constraint the wall's
                // insert and rebuild must respect.
                Point3d c = mid + (outward * 10.0);
                var along = new Vector3d(-outward.Y, outward.X, 0);
                double z = Ground(c.X, c.Y);
                Point3d[] corners =
                {
                    c + (along * 5) + (outward * 3), c - (along * 5) + (outward * 3),
                    c - (along * 5) - (outward * 3), c + (along * 5) - (outward * 3)
                };
                var boundary = new PolylineCurve(corners.Append(corners[0]).Select(p => new Point3d(p.X, p.Y, z)));
                var pad = new GradePadModifierDefinition { Id = Guid.Parse("22222222-2222-2222-2222-222222222222"), SlopeAngle = 26.5, MaxDistance = 15 };
                SetGeometry(snapshot, pad.Boundaries, 211, boundary);
                if (context == "pad-before")
                {
                    terrain.Modifiers.Add(pad);
                    terrain.Modifiers.Add(wall);
                }
                else
                {
                    terrain.Modifiers.Add(wall);
                    terrain.Modifiers.Add(pad);
                }

                return;
            }
            case "path-near":
            {
                // A 3 m path 6 m behind the top rail, inside the top batter's reach at shallow slopes.
                var path = new GradePathModifierDefinition { Id = Guid.Parse("33333333-3333-3333-3333-333333333333"), Width = 3.0, SlopeAngle = 26.5, MaxDistance = 12 };
                SetGeometry(snapshot, path.Paths, 307, Rail(Offset(topPlan, 6.0), 0.0));
                terrain.Modifiers.Add(path);
                terrain.Modifiers.Add(wall);
                return;
            }
            default:
                terrain.Modifiers.Add(wall);
                return;
        }
    }

    private static void ReadStages(TerrainBuildResult build, CaseResult result)
    {
        result.Insertion = "local";
        foreach (TerrainBuildTiming timing in build.Timings)
        {
            if (timing.Stage == "Retaining Wall Grading")
                result.Graded = timing.Detail != "failed";
            if (timing.Stage == "Retaining Wall Topology Insert" && timing.Detail?.StartsWith("not inserted", StringComparison.Ordinal) == true)
                result.Insertion = "rebuild";
            if (timing.Stage == "Retaining Wall Remesh" && timing.Detail is "kept upstream mesh" or "returned upstream mesh")
                result.Insertion = "failed";
        }

        foreach (string diagnostic in build.Diagnostics)
        {
            if (diagnostic.Contains("topology mode:", StringComparison.Ordinal))
            {
                int colon = diagnostic.IndexOf("topology mode:", StringComparison.Ordinal) + "topology mode:".Length;
                string tier = diagnostic[colon..].Trim();
                int cut = tier.IndexOfAny(['(', '.', ' ']);
                string name = diagnostic[..diagnostic.IndexOf("topology mode:", StringComparison.Ordinal)].Trim() + ":" + (cut > 0 ? tier[..cut] : tier);
                result.GradingTier = result.GradingTier == null ? name : result.GradingTier + "," + name;
            }

            if (diagnostic.Contains("discarded terrain detail", StringComparison.Ordinal) ||
                diagnostic.Contains("rebuild damaged the terrain", StringComparison.Ordinal))
            {
                result.Insertion = "failed";
            }
            if (diagnostic.Contains("fail", StringComparison.OrdinalIgnoreCase) &&
                diagnostic.Contains("Retaining Wall", StringComparison.Ordinal) &&
                result.Notes.Count < 3)
            {
                result.Notes.Add(diagnostic.Length > 160 ? diagnostic[..160] : diagnostic);
            }
        }
    }

    /// <summary>
    /// Sections square to the wall at the middle three quarters of its length. Behind the top rail the
    /// designed surface falls from the rail at the slope until it meets the ground; in front of the toe rail
    /// it rises from the rail until it meets the ground. Past daylight it must be undisturbed ground.
    /// </summary>
    private static void MeasureBatter(Mesh mesh, Point3d[] toePlan, Point3d[] topPlan, double slope, bool measureTop, CaseResult result)
    {
        double rise = Math.Tan(slope * Math.PI / 180.0);
        double height = TopAboveGround + ToeBelowGround;
        var toeCurve = new Polyline(toePlan).ToPolylineCurve();
        var topCurve = new Polyline(topPlan).ToPolylineCurve();

        // The rails as built: elevations at their vertices, straight between. Reading ground − 1 at a station
        // instead mis-scored every straight run by the ground's own noise (up to 10% of wall height).
        PolylineCurve toeRail = Rail(toePlan, -ToeBelowGround);
        PolylineCurve topRail = Rail(topPlan, TopAboveGround);
        double worst = 0, sum = 0;
        int count = 0, missed = 0;
        string worstAt = string.Empty;

        // A section is only a design check away from a corner: inside a bend both legs' batters overlap,
        // so the surface there is governed by the nearer leg, not by the perpendicular through the corner.
        double[] fractions = toePlan.Length > 2 && toePlan[0].DistanceTo(toePlan[^1]) > 1e-9 && toePlan.Length <= 4
            ? new[] { 0.2, 0.3, 0.7, 0.8 }
            : new[] { 0.2, 0.35, 0.5, 0.65, 0.8 };
        foreach (double fraction in fractions)
        {
            double tToe = toeCurve.Domain.ParameterAt(fraction);
            Point3d toePoint = toeCurve.PointAt(tToe);
            Vector3d tangent = toeCurve.TangentAt(tToe);
            var outward = new Vector3d(tangent.Y, -tangent.X, 0); // away from the top rail (the offset side is +left)
            outward.Unitize();
            topCurve.ClosestPoint(toePoint + (-outward * 0.01), out double tTop);
            Point3d topPoint = topCurve.PointAt(tTop);

            double toeZ = toeRail.PointAt(tToe).Z;
            double topZ = topRail.PointAt(tTop).Z;
            foreach (double reach in new[] { 0.25, 0.5, 0.75, 1.5 })
            {
                // Toe side: the batter rises from the toe until it meets ground.
                double toeDaylight = ToeBelowGround / rise;
                Point3d a = toePoint + (outward * (toeDaylight * reach));
                Score(mesh, a, Math.Min(Ground(a.X, a.Y), toeZ + (toeDaylight * reach * rise)), height,
                    $"toe f{fraction} r{reach}", ref worst, ref worstAt, ref sum, ref count, ref missed);

                // Top side: the batter falls from the top rail until it meets ground.
                if (!measureTop)
                    continue;
                double topDaylight = TopAboveGround / rise;
                Point3d b = topPoint - (outward * (topDaylight * reach));
                Score(mesh, b, Math.Max(Ground(b.X, b.Y), topZ - (topDaylight * reach * rise)), height,
                    $"top f{fraction} r{reach}", ref worst, ref worstAt, ref sum, ref count, ref missed);
            }
        }

        result.WorstBatterError = worst;
        result.WorstBatterAt = worstAt;
        result.MeanBatterError = count > 0 ? sum / count : 0;
        result.Stations = count + missed;
        result.StationsMissed = missed;
    }

    private static void Score(Mesh mesh, Point3d plan, double designedZ, double height, string label,
        ref double worst, ref string worstAt, ref double sum, ref int count, ref int missed)
    {
        if (plan.X < 1 || plan.Y < 1 || plan.X > SiteX - 1 || plan.Y > SiteY - 1)
            return;

        Point3d[] hits = Intersection.MeshLine(mesh, new Line(plan.X, plan.Y, -1000, plan.X, plan.Y, 1000));
        if (hits == null || hits.Length == 0)
        {
            missed++;
            return;
        }

        // Away from the wall face a section line crosses the surface once; take the hit nearest the design.
        double measured = hits.Select(h => h.Z).OrderBy(z => Math.Abs(z - designedZ)).First();
        double error = Math.Abs(measured - designedZ) / height;
        if (error > worst)
        {
            worst = error;
            worstAt = $"{label} at ({plan.X:0.0},{plan.Y:0.0}) designed {designedZ:0.00} measured {measured:0.00} ground {Ground(plan.X, plan.Y):0.00}";
        }
        sum += error;
        count++;
    }

    /// <summary>Gently sloping ground with a little noise, so daylight distance varies along the wall.</summary>
    private static double Ground(double x, double y) =>
        GroundBase + (0.04 * x) + (0.03 * y) + (0.15 * Math.Sin(x * 0.7) * Math.Cos(y * 0.9));

    private static Point3d[] Survey(double spacing)
    {
        int nx = (int)(SiteX / spacing) + 1;
        int ny = (int)(SiteY / spacing) + 1;
        var points = new Point3d[nx * ny];
        for (int i = 0; i < nx; i++)
        for (int j = 0; j < ny; j++)
        {
            uint h = unchecked(((uint)i * 374761393u) + ((uint)j * 668265263u));
            h = unchecked((h ^ (h >> 13)) * 1274126177u);
            double jx = (((h & 0xFFFF) / 65535.0) - 0.5) * spacing * 0.6;
            double jy = ((((h >> 16) & 0xFFFF) / 65535.0) - 0.5) * spacing * 0.6;
            double x = Math.Clamp((i * spacing) + jx, 0, SiteX);
            double y = Math.Clamp((j * spacing) + jy, 0, SiteY);
            points[(i * ny) + j] = new Point3d(x, y, Ground(x, y));
        }

        return points;
    }

    /// <summary>The toe rail's plan. The top rail is offset to its left by the gap, so the high side is +left.</summary>
    private static Point3d[] WallPlan(string shape)
    {
        switch (shape)
        {
            case "bend":
                return new[] { new Point3d(12, 16, 0), new Point3d(30, 16, 0), new Point3d(46, 26, 0) };
            case "straight-dense":
                return Enumerable.Range(0, 37).Select(k => new Point3d(12 + k, 20, 0)).ToArray();
            case "ring":
            {
                // Closed, counter-clockwise: the top rail offsets inward, so it holds up a raised centre.
                var points = new List<Point3d>();
                for (int k = 0; k < 32; k++)
                {
                    double a = 2 * Math.PI * k / 32;
                    points.Add(new Point3d(30 + (13 * Math.Cos(a)), 22 + (13 * Math.Sin(a)), 0));
                }

                points.Add(points[0]);
                return points.ToArray();
            }
            case "arc":
            {
                var points = new List<Point3d>();
                for (int k = 0; k <= 24; k++)
                {
                    double a = Math.PI * (1.15 + (0.7 * k / 24.0));
                    points.Add(new Point3d(30 + (22 * Math.Cos(a)), 40 + (22 * Math.Sin(a)), 0));
                }

                return points.ToArray();
            }
            default:
                return new[] { new Point3d(12, 20, 0), new Point3d(48, 20, 0) };
        }
    }

    /// <summary>A mitred offset to the left of the polyline; a closed polyline (last point = first) wraps.</summary>
    private static Point3d[] Offset(Point3d[] plan, double distance)
    {
        bool closed = plan.Length > 2 && plan[0].DistanceTo(plan[^1]) < 1e-9;
        int n = closed ? plan.Length - 1 : plan.Length;
        var result = new Point3d[plan.Length];
        for (int i = 0; i < n; i++)
        {
            bool hasPrev = closed || i > 0;
            bool hasNext = closed || i < n - 1;
            Point3d prev = plan[(i - 1 + n) % n];
            Point3d next = plan[(i + 1) % n];
            Vector3d normal = Vector3d.Zero;
            if (hasPrev)
                normal += LeftNormal(prev, plan[i]);
            if (hasNext)
                normal += LeftNormal(plan[i], next);
            normal.Unitize();
            double miter = hasPrev && hasNext ? 1.0 / Math.Max(0.3, normal * LeftNormal(plan[i], next)) : 1.0;
            result[i] = plan[i] + (normal * distance * miter);
        }

        if (closed)
            result[^1] = result[0];
        return result;
    }

    private static Vector3d LeftNormal(Point3d a, Point3d b)
    {
        var d = b - a;
        var n = new Vector3d(-d.Y, d.X, 0);
        n.Unitize();
        return n;
    }

    private static void SetGeometry(TerrainBuildSnapshot snapshot, SourceReferenceSet set, ulong fingerprint, params GeometryBase[] geometry)
    {
        var objects = new List<ResolvedSourceObject>(geometry.Length);
        set.ObjectIds.Clear();
        foreach (GeometryBase item in geometry)
        {
            Guid id = Guid.NewGuid();
            set.ObjectIds.Add(id);
            BoundingBox bbox = item.GetBoundingBox(true);
            objects.Add(new ResolvedSourceObject
            {
                ObjectId = id,
                Geometry = item,
                LocalBoundingBox = bbox,
                WorldBoundingBox = bbox,
                GeometryDataCrc = item.DataCRC(0)
            });
            fingerprint = unchecked((fingerprint * 397) ^ item.DataCRC(0));
        }

        snapshot.SourceObjects[set] = objects;
        snapshot.SourceFingerprints[set] = fingerprint;
    }
}
