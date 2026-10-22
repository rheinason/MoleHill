using System.Diagnostics;
using System.Text.Json;
using MoleHill.Core.Engine;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// Grade Path beside hard constraints, through the real build: a surveyed Triangulate with breaklines, a
/// Retaining Wall or Grade Pad where the case has one, then the Grade Path. The Core sweep
/// (<c>GradePathNearConstraintsStressTests</c>) covers the grader; this covers the stage, its windowing and
/// its barrier stops, as a Rhino user's terrain would run them.
///
/// <para>Each case is built twice, with and without the path, and both surfaces are sampled along every
/// constraint. A path may stop at a breakline, a wall or a pad edge; it may not move one. The probe also
/// reports whether the path graded at all, its tier, the stops, the terrain's border loops, and how much of
/// the drawn centreline ended up at the design height.</para>
///
/// Run it inside Rhino like the other hosted probes:
/// <c>tools/rhino-hosted-perf.py --entry GradePathProbe</c>.
/// </summary>
public static class GradePathProbe
{
    public sealed class Request
    {
        public string ResultPath { get; set; } = string.Empty;
        public List<string>? Contexts { get; set; }
        public List<double>? Rises { get; set; }
        public List<double>? Widths { get; set; }
        public double Spacing { get; set; } = 1.0;
        public double CaseBudgetSeconds { get; set; } = 90;
    }

    public sealed class CaseResult
    {
        public string Context { get; set; } = string.Empty;
        public double Rise { get; set; }
        public double Width { get; set; }
        public string? Error { get; set; }
        public double BuildMs { get; set; }
        public bool Graded { get; set; }
        public string? Refusal { get; set; }
        public string? Tier { get; set; }
        public int Stops { get; set; }
        public int BoundaryLoops { get; set; }
        public int ReferenceBoundaryLoops { get; set; }
        public int NonManifoldEdges { get; set; }

        /// <summary>Largest height change along any constraint between the builds with and without the path.</summary>
        public double ConstraintMoved { get; set; }
        public string? ConstraintMovedAt { get; set; }

        /// <summary>Share of centreline stations within 2 cm of the design height.</summary>
        public double OnDesign { get; set; }
        public List<string> Notes { get; set; } = new();
    }

    private const double SiteX = 40.0;
    private const double SiteY = 30.0;
    private const double PathY = 15.0;

    private static readonly string[] AllContexts =
    {
        "none", "breakline-inside", "breakline-outside", "breakline-cross-vertex", "breakline-cross-oblique",
        "breakline-stub", "breakline-bend", "breakline-past-end", "pad-corner", "path-to-pad", "wall-cross",
        "wall-beside"
    };

    public static void Start(string requestPath)
    {
        Request request = JsonSerializer.Deserialize<Request>(File.ReadAllText(requestPath), PerfRunResult.JsonOptions)
            ?? throw new InvalidOperationException($"Could not read the request at {requestPath}.");
        HostedPowerThrottling.OptOut();
        var thread = new Thread(() => RunToFile(request)) { IsBackground = true, Name = "MoleHill grade path probe" };
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
            foreach (string context in request.Contexts is { Count: > 0 } ? request.Contexts : AllContexts.ToList())
            foreach (double width in request.Widths is { Count: > 0 } ? request.Widths : new List<double> { 3.0 })
            foreach (double rise in request.Rises is { Count: > 0 } ? request.Rises : new List<double> { -1.0, 0.0, 1.0 })
            {
                CaseResult result = RunCaseWithBudget(context, width, rise, request.Spacing, request.CaseBudgetSeconds);
                results.Add(result);
                Progress(Describe(result));
                if (result.Error?.StartsWith("hung", StringComparison.Ordinal) == true)
                    throw new InvalidOperationException("A case ignored cancellation; stopping, since its thread cannot be stopped.");
            }
        }
        catch (Exception ex)
        {
            error = ex.ToString();
        }

        string temp = request.ResultPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new { Error = error, Cases = results }, PerfRunResult.JsonOptions));
        File.Move(temp, request.ResultPath, overwrite: true);
    }

    private static string Describe(CaseResult c) =>
        $"{c.Context,-24} w {c.Width:0.#} rise {c.Rise,4:+0.#;-0.#;0} " +
        $"{(c.Graded ? "graded" : "REFUSED"),-7} tier {c.Tier ?? "-",-14} stops {c.Stops} " +
        $"loops {c.BoundaryLoops}/{c.ReferenceBoundaryLoops} nm {c.NonManifoldEdges} " +
        $"constraint moved {c.ConstraintMoved:0.000}{(c.ConstraintMoved > 0.005 ? " at " + c.ConstraintMovedAt : string.Empty)} " +
        $"on design {c.OnDesign:P0} {c.BuildMs,6:N0} ms" +
        (c.Refusal == null ? string.Empty : "  REFUSAL " + c.Refusal) +
        (c.Error == null ? string.Empty : "  ERROR " + c.Error.Split('\n')[0]) +
        (c.Notes.Count == 0 ? string.Empty : "  | " + string.Join(" | ", c.Notes));

    private static CaseResult RunCaseWithBudget(string context, double width, double rise, double spacing, double budgetSeconds)
    {
        CaseResult? result = null;
        var budget = TimeSpan.FromSeconds(budgetSeconds);
        var clock = Stopwatch.StartNew();
        var worker = new Thread(() => result = RunCase(context, width, rise, spacing, () => clock.Elapsed > budget))
        {
            IsBackground = true,
            Name = "grade path probe case"
        };
        worker.Start();
        if (worker.Join(budget + TimeSpan.FromSeconds(60)))
            return result!;

        return new CaseResult
        {
            Context = context, Width = width, Rise = rise, BuildMs = clock.Elapsed.TotalMilliseconds,
            Error = $"hung: still building {clock.Elapsed.TotalSeconds:N0} s in, past a {budgetSeconds:N0} s budget"
        };
    }

    internal static CaseResult RunCase(string context, double width, double rise, double spacing, Func<bool>? shouldCancel = null)
    {
        var result = new CaseResult { Context = context, Width = width, Rise = rise };
        try
        {
            Point3d[] centre = { new(5, PathY, 0), new(20, PathY, 0), new(35, PathY, 0) };
            var pathCurve = new PolylineCurve(centre.Select(p => new Point3d(p.X, p.Y, Ground(p.X, p.Y) + rise)));
            List<Curve> constraints = new();

            TerrainBuildSnapshot Snapshot(bool withPath)
            {
                var triangulate = new TriangulateModifierDefinition
                {
                    Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    PeelBoundaryTriangles = false,
                    Tolerance = 0.01
                };
                var terrain = new TerrainDefinition
                {
                    TerrainId = Guid.Parse("99999999-9999-9999-9999-999999999999"),
                    Name = "Grade path probe",
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
                constraints = AddContext(context, width, snapshot, terrain, triangulate);
                if (withPath)
                {
                    var path = new GradePathModifierDefinition
                    {
                        Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                        Width = width,
                        SlopeAngle = 30.0,
                        MaxDistance = 12.0
                    };
                    SetGeometry(snapshot, path.Paths, 307, pathCurve);
                    terrain.Modifiers.Add(path);
                }

                return snapshot;
            }

            var timer = Stopwatch.StartNew();
            TerrainBuildResult build = new TerrainBuildService().Build(Snapshot(true), new TerrainRuntimeCache(), TerrainBuildMode.Final, shouldCancel);
            result.BuildMs = timer.Elapsed.TotalMilliseconds;
            TerrainBuildResult reference = new TerrainBuildService().Build(Snapshot(false), new TerrainRuntimeCache(), TerrainBuildMode.Final, shouldCancel);
            Mesh? mesh = build.PrimaryMesh;
            Mesh? referenceMesh = reference.PrimaryMesh;
            if (mesh == null || referenceMesh == null)
            {
                result.Error = mesh == null ? "no mesh" : "no reference mesh";
                return result;
            }

            ReadStages(build, result);
            result.BoundaryLoops = Loops(mesh, out int nonManifold);
            result.NonManifoldEdges = nonManifold;
            result.ReferenceBoundaryLoops = Loops(referenceMesh, out _);

            foreach (Curve constraint in constraints)
                MeasureMoved(constraint, referenceMesh, mesh, result);

            int stations = 0, onDesign = 0;
            for (double x = 5.25; x < 35.0; x += 0.5)
            {
                Point3d[] hits = Intersection.MeshLine(mesh, new Line(x, PathY, -1000, x, PathY, 1000));
                stations++;
                int span = x < centre[1].X ? 0 : 1;
                double u = (x - centre[span].X) / (centre[span + 1].X - centre[span].X);
                double design = pathCurve.Point(span).Z + ((pathCurve.Point(span + 1).Z - pathCurve.Point(span).Z) * u);
                if (hits is { Length: > 0 } && hits.Any(h => Math.Abs(h.Z - design) <= 0.02))
                    onDesign++;
            }

            result.OnDesign = stations == 0 ? 0 : onDesign / (double)stations;
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

    /// <summary>Adds the case's constraint to the stack and returns every curve the grade must not move.</summary>
    private static List<Curve> AddContext(
        string context, double width, TerrainBuildSnapshot snapshot, TerrainDefinition terrain, TriangulateModifierDefinition triangulate)
    {
        double half = width * 0.5;
        Curve Breakline(params (double X, double Y)[] plan) =>
            new PolylineCurve(plan.Select(p => new Point3d(p.X, p.Y, Ground(p.X, p.Y))));
        List<Curve> Breaklines(params Curve[] curves)
        {
            SetGeometry(snapshot, triangulate.Breaklines, 201, curves.Cast<GeometryBase>().ToArray());
            return curves.ToList();
        }

        switch (context)
        {
            case "breakline-inside":
                return Breaklines(Breakline((10, PathY + (half * 0.4)), (30, PathY + (half * 0.4))));
            case "breakline-outside":
                return Breaklines(Breakline((10, PathY + half + 1.0), (30, PathY + half + 1.0)));
            case "breakline-cross-vertex":
                return Breaklines(Breakline((20, PathY - 8), (20, PathY + 8)));
            case "breakline-cross-oblique":
                return Breaklines(Breakline((13 - 6.9, PathY - 4), (13 + 6.9, PathY + 4)));
            case "breakline-stub":
                return Breaklines(Breakline((13, PathY + half + 6), (13, PathY + (half * 0.4))));
            case "breakline-bend":
                return Breaklines(Breakline((9, PathY + half + 5), (13, PathY + (half * 0.5)), (17, PathY + half + 5)));
            case "breakline-past-end":
                return Breaklines(Breakline((36.5, PathY - 8), (36.5, PathY + 8)));
            case "pad-corner":
            case "path-to-pad":
            {
                // A level pad. Its corner pokes into the road, or its west edge is where the path ends.
                (double X, double Y)[] corners = context == "pad-corner"
                    ? new[] { (14.0, PathY + (half * 0.5)), (22.0, PathY + (half * 0.5) + 2.0), (19.0, PathY + 9.0), (11.0, PathY + 7.0) }
                    : new[] { (35.0, PathY - 4.0), (39.0, PathY - 4.0), (39.0, PathY + 4.0), (35.0, PathY + 4.0) };
                double z = Ground(corners[0].X, corners[0].Y);
                var boundary = new PolylineCurve(corners.Append(corners[0]).Select(p => new Point3d(p.X, p.Y, z)));
                var pad = new GradePadModifierDefinition { Id = Guid.Parse("22222222-2222-2222-2222-222222222222"), SlopeAngle = 26.5, MaxDistance = 15 };
                SetGeometry(snapshot, pad.Boundaries, 211, boundary);
                terrain.Modifiers.Add(pad);
                return new List<Curve> { boundary };
            }
            case "wall-cross":
            case "wall-beside":
            {
                // A breakline-mode wall: a top rail 1.2 m up and a toe rail at grade, 0.3 m apart.
                (double X, double Y)[] top = context == "wall-cross"
                    ? new[] { (24.0, 2.0), (24.0, 28.0) }
                    : new[] { (4.0, PathY + half + 1.5), (36.0, PathY + half + 1.5) };
                (double X, double Y)[] toe = context == "wall-cross"
                    ? new[] { (24.3, 2.0), (24.3, 28.0) }
                    : new[] { (4.0, PathY + half + 1.8), (36.0, PathY + half + 1.8) };
                Curve topRail = new PolylineCurve(top.Select(p => new Point3d(p.X, p.Y, Ground(p.X, p.Y) + 1.2)));
                Curve toeRail = new PolylineCurve(toe.Select(p => new Point3d(p.X, p.Y, Ground(p.X, p.Y))));
                var wall = new RetainingWallModifierDefinition
                {
                    Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                    Mode = RetainingWallModifierDefinition.BreaklineOnlyMode,
                    MaxWallWidth = 1.0
                };
                SetGeometry(snapshot, wall.WallCurves, 401, topRail, toeRail);
                terrain.Modifiers.Add(wall);
                return new List<Curve> { topRail, toeRail };
            }
            default:
                return new List<Curve>();
        }
    }

    private static void ReadStages(TerrainBuildResult build, CaseResult result)
    {
        result.Graded = true;
        foreach (TerrainBuildTiming timing in build.Timings)
        {
            if (timing.Stage == "Grade Path Core" && timing.Detail == "failed")
                result.Graded = false;
        }

        foreach (string diagnostic in build.Diagnostics)
        {
            if (diagnostic.StartsWith("Grade Path stopped at ", StringComparison.Ordinal) &&
                int.TryParse(diagnostic["Grade Path stopped at ".Length..].Split(' ')[0].Replace(",", string.Empty), out int stops))
            {
                result.Stops = stops;
            }

            if (diagnostic.StartsWith("Grade Path topology mode:", StringComparison.Ordinal))
            {
                string tier = diagnostic["Grade Path topology mode:".Length..].Trim();
                int cut = tier.IndexOfAny(['(', '.']);
                result.Tier = (cut > 0 ? tier[..cut] : tier).Trim();
            }

            if (!result.Graded && result.Refusal == null &&
                (diagnostic.StartsWith("Grade Path", StringComparison.Ordinal) || diagnostic.Contains("hard constraint", StringComparison.Ordinal)) &&
                !diagnostic.StartsWith("Grade Path stopped", StringComparison.Ordinal))
            {
                result.Refusal = diagnostic.Length > 180 ? diagnostic[..180] : diagnostic;
            }
        }

        if (!result.Graded && result.Refusal == null)
            result.Refusal = string.Join(" / ", build.Diagnostics.Take(3));
    }

    private static int Loops(Mesh mesh, out int nonManifold)
    {
        int[] faces = mesh.Faces.ToIntArray(true);
        var topology = MeshTopologyValidator.AnalyzeBoundaryGraph(faces, faces.Length / 3);
        nonManifold = topology.NonManifoldEdgeCount;
        return topology.BoundaryComponentCount;
    }

    /// <summary>
    /// Samples a constraint every 0.25 m on both surfaces. A wall face gives several heights at one point;
    /// each reference height must have one within reach on the graded surface.
    /// </summary>
    private static void MeasureMoved(Curve constraint, Mesh reference, Mesh graded, CaseResult result)
    {
        double length = constraint.GetLength();
        int steps = Math.Max(1, (int)Math.Ceiling(length / 0.25));
        for (int k = 0; k <= steps; k++)
        {
            constraint.LengthParameter(length * k / steps, out double t);
            Point3d p = constraint.PointAt(t);
            var line = new Line(p.X, p.Y, -1000, p.X, p.Y, 1000);
            Point3d[] before = Intersection.MeshLine(reference, line);
            Point3d[] after = Intersection.MeshLine(graded, line);
            if (before is not { Length: > 0 } || after is not { Length: > 0 })
                continue;

            foreach (Point3d b in before)
            {
                double moved = after.Min(a => Math.Abs(a.Z - b.Z));
                if (moved > result.ConstraintMoved)
                {
                    result.ConstraintMoved = moved;
                    result.ConstraintMovedAt = $"({p.X:0.##}, {p.Y - PathY:+0.##;-0.##} off the path)";
                }
            }
        }
    }

    /// <summary>Gently sloping ground with a little noise.</summary>
    private static double Ground(double x, double y) =>
        10.0 + (0.05 * x) + (0.03 * y) + (0.2 * Math.Sin(x * 0.6) * Math.Cos(y * 0.8));

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
