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
/// Smooth's Round Creases mode on a real graded scene, built through the full pipeline inside Rhino: a
/// sloping survey, a Grade Pad and a Grade Path, with and without a Remesh before the rounding, and with the
/// pad edge protected. Each case is compared against the same stack without the Smooth card, so the
/// section it writes is the rounding's own offset. The meshes are written to a .3dm beside the result for
/// a look. Run it like the other hosted probes:
/// <c>tools/rhino-hosted-perf.py --entry CreaseRoundingProbe</c>.
/// </summary>
public static class CreaseRoundingProbe
{
    public sealed class Request
    {
        public string ResultPath { get; set; } = string.Empty;
        public double RoundingLength { get; set; } = 3.0;
        public double SurveySpacing { get; set; } = 1.0;
    }

    public sealed class CaseResult
    {
        public string Name { get; set; } = string.Empty;
        public string? Error { get; set; }
        public double BuildMs { get; set; }
        public double SmoothStageMs { get; set; }
        public int Vertices { get; set; }
        public int Faces { get; set; }
        public bool Valid { get; set; }
        public int BoundaryLoops { get; set; }
        public int NonManifoldEdges { get; set; }
        public double MaxDrop { get; set; }
        public double MaxRise { get; set; }
        public double PadEdgeOffset { get; set; }
        public List<string> Section { get; set; } = new();
        public List<string> Diagnostics { get; set; } = new();
    }

    private const double SiteX = 70.0;
    private const double SiteY = 50.0;
    private const double PadZ = 13.0;

    private static readonly Point3d[] PadPlan =
    {
        new(22, 15, PadZ), new(42, 15, PadZ), new(42, 30, PadZ), new(22, 30, PadZ), new(22, 15, PadZ)
    };

    public static void Start(string requestPath)
    {
        Request request = JsonSerializer.Deserialize<Request>(File.ReadAllText(requestPath), PerfRunResult.JsonOptions)
            ?? throw new InvalidOperationException($"Could not read the request at {requestPath}.");
        HostedPowerThrottling.OptOut();
        var thread = new Thread(() => RunToFile(request)) { IsBackground = true, Name = "MoleHill crease rounding probe" };
        thread.Start();
    }

    public static void RunToFile(Request request)
    {
        var results = new List<CaseResult>();
        string? error = null;
        var file = new global::Rhino.FileIO.File3dm();
        try
        {
            foreach ((string name, bool remesh, bool protectPad) in new[]
            {
                ("graded", false, false),
                ("remeshed", true, false),
                ("remeshed-protect-pad", true, true),
            })
            {
                Mesh? baseline = Build(request, remesh, round: false, protectPad, out _, out _);
                Mesh? rounded = Build(request, remesh, round: true, protectPad, out TerrainBuildResult? build, out double ms);
                results.Add(Measure(name, baseline, rounded, build, ms));
                if (baseline != null)
                    file.Objects.AddMesh(baseline, new global::Rhino.DocObjects.ObjectAttributes { Name = name + " before" });
                if (rounded != null)
                    file.Objects.AddMesh(rounded, new global::Rhino.DocObjects.ObjectAttributes { Name = name + " rounded" });
                File.AppendAllText(request.ResultPath + ".progress", $"{DateTime.Now:HH:mm:ss} {name} done{System.Environment.NewLine}");
            }

            file.Write(Path.ChangeExtension(request.ResultPath, ".3dm"), 8);
        }
        catch (Exception ex)
        {
            error = ex.ToString();
        }

        File.WriteAllText(request.ResultPath, JsonSerializer.Serialize(new { error, results }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static Mesh? Build(Request request, bool remesh, bool round, bool protectPad, out TerrainBuildResult? build, out double ms)
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
            SlopeAngle = 26.6,
            MaxDistance = 15.0
        };
        var path = new GradePathModifierDefinition
        {
            Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Width = 4.0,
            SlopeAngle = 26.6,
            MaxDistance = 12.0
        };
        var modifiers = new List<ModifierDefinition> { triangulate, pad, path };
        if (remesh)
        {
            modifiers.Add(new RemeshModifierDefinition
            {
                Id = Guid.Parse("55555555-5555-5555-5555-555555555555"),
                Mode = "isotropic",
                EdgeLength = request.RoundingLength / 5.0,
                CreaseAngle = 10.0
            });
        }

        var smooth = new SmoothModifierDefinition
        {
            Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
            Mode = SmoothModes.Round,
            RoundingLength = request.RoundingLength,
            CreaseAngle = 10.0
        };
        if (round)
            modifiers.Add(smooth);

        var terrain = new TerrainDefinition
        {
            TerrainId = Guid.NewGuid(),
            Name = "Crease rounding probe",
            GlobalTolerance = 0.01,
            Modifiers = modifiers
        };
        var snapshot = new TerrainBuildSnapshot
        {
            Terrain = terrain,
            ModelAbsoluteTolerance = 0.001,
            ModelUnitSystem = UnitSystem.Meters
        };

        SetGeometry(snapshot, triangulate.Points, 101, new PointCloud(Survey(request.SurveySpacing)));
        SetGeometry(snapshot, pad.Boundaries, 201, new PolylineCurve(PadPlan));
        SetGeometry(snapshot, path.Paths, 301, RoadCurve());
        if (protectPad)
            SetGeometry(snapshot, smooth.Breaklines, 401, new PolylineCurve(PadPlan));

        var timer = Stopwatch.StartNew();
        build = new TerrainBuildService().Build(snapshot, new TerrainRuntimeCache(), TerrainBuildMode.Final, () => false);
        ms = timer.Elapsed.TotalMilliseconds;
        return build.PrimaryMesh;
    }

    private static CaseResult Measure(string name, Mesh? baseline, Mesh? rounded, TerrainBuildResult? build, double ms)
    {
        var result = new CaseResult { Name = name, BuildMs = ms };
        if (build != null)
        {
            result.Diagnostics.AddRange(build.Diagnostics.Where(d => d.Contains("Smooth", StringComparison.Ordinal) || d.Contains("crease", StringComparison.OrdinalIgnoreCase)));
            result.SmoothStageMs = build.Timings.Where(t => t.Stage.StartsWith("Smooth", StringComparison.Ordinal)).Sum(t => t.Elapsed.TotalMilliseconds);
        }

        if (baseline == null || rounded == null)
        {
            result.Error = "no mesh";
            return result;
        }

        result.Vertices = rounded.Vertices.Count;
        result.Faces = rounded.Faces.Count;
        result.Valid = rounded.IsValid;
        int[] faces = rounded.Faces.ToIntArray(true);
        var topology = MeshTopologyValidator.AnalyzeBoundaryGraph(faces, faces.Length / 3);
        result.BoundaryLoops = topology.BoundaryComponentCount;
        result.NonManifoldEdges = topology.NonManifoldEdgeCount;

        // Same topology in and out: rounding moves heights only.
        if (baseline.Vertices.Count == rounded.Vertices.Count)
        {
            for (int i = 0; i < rounded.Vertices.Count; i++)
            {
                double dz = rounded.Vertices[i].Z - baseline.Vertices[i].Z;
                result.MaxDrop = Math.Min(result.MaxDrop, dz);
                result.MaxRise = Math.Max(result.MaxRise, dz);
            }
        }
        else
        {
            result.Diagnostics.Add($"vertex count changed {baseline.Vertices.Count} -> {rounded.Vertices.Count}");
        }

        result.PadEdgeOffset = Height(rounded, 32, 15) - Height(baseline, 32, 15);

        // A section south from the pad centre through its edge, the batter and the toe.
        for (double y = 22.0; y >= 2.0; y -= 0.5)
        {
            double before = Height(baseline, 32, y);
            double after = Height(rounded, 32, y);
            result.Section.Add($"y={y:0.0} before {before:0.000} offset {after - before:+0.000;-0.000;0.000}");
        }

        return result;
    }

    private static double Height(Mesh mesh, double x, double y)
    {
        Point3d[] hits = Intersection.MeshLine(mesh, new Line(x, y, -1000, x, y, 1000));
        return hits is { Length: > 0 } ? hits.Max(h => h.Z) : double.NaN;
    }

    private static double Ground(double x, double y) => 10.0 + (0.08 * y) + (0.5 * Math.Sin(x * 0.15));

    private static IEnumerable<Point3d> Survey(double spacing)
    {
        for (double y = 0; y <= SiteY + 1e-9; y += spacing)
        for (double x = 0; x <= SiteX + 1e-9; x += spacing)
            yield return new Point3d(x, y, Ground(x, y));
    }

    private static Curve RoadCurve()
    {
        var points = new List<Point3d>();
        for (double x = 4; x <= 66; x += 2)
        {
            double y = 40 + (2.0 * Math.Sin(x * 0.08));
            points.Add(new Point3d(x, y, Ground(x, y) + 0.4 + (0.02 * (x - 35))));
        }

        return new PolylineCurve(points);
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
