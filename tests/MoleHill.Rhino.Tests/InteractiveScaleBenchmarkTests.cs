using System.Diagnostics;
using MoleHill.Core.Tests;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Geometry;
using Xunit;
using Xunit.Abstractions;

namespace MoleHill.Rhino.Tests;

public class InteractiveScaleBenchmarkTests(ITestOutputHelper output)
{
    [RhinoNativeFact]
    [Trait("Category", "Performance")]
    public void Build_TriangulateAndWall_AcrossInteractiveScales()
    {
        if (!PerformanceLane.ShouldRun(output, "Triangulate + Retaining Wall across interactive scales"))
            return;

        InteractiveScaleBenchmark.Run(output.WriteLine);
    }
}

/// <summary>
/// How much worker time a *warm wall-height edit* costs as the terrain grows, on the interactive plan's
/// first supported workflow (Triangulate -> Retaining Wall).
///
/// This is the headroom measurement the realtime targets depend on. The plan asks for 30 surface
/// updates per second and an input-to-visible p95 of 66 ms during a sustained gesture; whether that is
/// reachable by scheduling work alone, or needs an approximate interactive surface, is decided by how
/// much of that 66 ms the *evaluation itself* consumes at each scale. Nothing had measured it.
///
/// The edit is a rail raise, which is what dragging a wall height actually does: it changes the wall
/// curve source, so it misses the wall stage and re-plans nothing upstream. See
/// `docs/interactive-terrain-plan-2026-09-16.md`, Step 2.
/// </summary>
/// <remarks>
/// Opt in with <c>MOLEHILL_PERF=1</c>; needs the native Rhino runtime, so on Rhino 8.35 it runs through
/// the hosted route in `docs/validation-lanes.md` rather than the native lane. Worker time only: this
/// says nothing about scheduling, the marshal back to the UI thread, or redraw, all of which sit
/// between the worker finishing and anything appearing on screen.
/// </remarks>
public static class InteractiveScaleBenchmark
{
    /// <summary>
    /// Grid side lengths and the labels they stand in for. 36 -> ~2.5k faces matches the trailer-ramp
    /// fixture the small-terrain latency numbers came from; 160 -> ~50k and 224 -> ~100k bracket the
    /// plan's "warmed 100k-face fixture" target.
    /// </summary>
    private static readonly (int Side, string Label)[] Scales =
    [
        (36, "small"),
        (112, "medium"),
        (160, "large"),
        (224, "plan target")
    ];

    public static string RunToFile(string path)
    {
        var lines = new List<string>();
        try
        {
            Run(lines.Add);
        }
        catch (Exception ex)
        {
            lines.Add("FAILED: " + ex);
        }

        File.WriteAllLines(path, lines);
        return path;
    }

    public static void Run(Action<string> write)
    {
        write($"{"scale",-12} {"faces",9} {"cold",10} {"warm edit",10}   stage split on the warm edit");
        foreach ((int side, string label) in Scales)
        {
            Fixture fixture = CreateFixture(side);
            var cache = new TerrainRuntimeCache();
            var service = new TerrainBuildService();

            var coldTimer = Stopwatch.StartNew();
            TerrainBuildResult cold = service.Build(fixture.Snapshot, cache, TerrainBuildMode.Final);
            coldTimer.Stop();
            if (cold.PrimaryMesh == null)
                throw new InvalidOperationException($"The {label} cold build produced no mesh.");

            // Three consecutive rail raises, as a drag would produce. The last one is reported: the
            // first warms the stage caches that a real gesture would already have warm.
            double warmMs = 0;
            TerrainBuildResult edited = cold;
            for (int i = 0; i < 3; i++)
            {
                RaiseWall(fixture, 0.15);
                var editTimer = Stopwatch.StartNew();
                edited = service.Build(fixture.Snapshot, cache, TerrainBuildMode.Final);
                editTimer.Stop();
                warmMs = editTimer.Elapsed.TotalMilliseconds;
            }

            if (edited.PrimaryMesh == null)
                throw new InvalidOperationException($"The {label} warm build produced no mesh.");

            string split = string.Join(
                ", ",
                edited.Timings
                    .Where(t => t.Elapsed.TotalMilliseconds >= 1.0 && !t.Stage.StartsWith("Build pipeline", StringComparison.Ordinal))
                    .OrderByDescending(t => t.Elapsed)
                    .Take(4)
                    .Select(t => $"{t.Stage} {t.Elapsed.TotalMilliseconds:N0}"));

            write(
                $"{label,-12} {edited.PrimaryMesh.Faces.Count,9:N0} " +
                $"{coldTimer.Elapsed.TotalMilliseconds,9:N0}ms {warmMs,9:N0}ms   {split}");
        }

        write(string.Empty);
        write("Targets for reference: interactive terrain wants input-to-visible p95 <= 66 ms and >= 30");
        write("updates/s during a gesture; exact settlement after release wants <= 200 ms. Worker time");
        write("is only part of that budget - scheduling, the UI marshal and redraw are all still to come.");
    }

    private static void RaiseWall(Fixture fixture, double dz)
    {
        SourceReferenceSet set = fixture.Wall.WallCurves;
        List<ResolvedSourceObject> existing = fixture.Snapshot.SourceObjects[set];
        var raised = new List<ResolvedSourceObject>(existing.Count);
        ulong fingerprint = 977;
        foreach (ResolvedSourceObject source in existing)
        {
            Curve moved = (Curve)source.Geometry.Duplicate();
            moved.Translate(new Vector3d(0, 0, dz));
            raised.Add(CreateSourceObject(source.ObjectId, moved));
            fingerprint = unchecked((fingerprint * 397) ^ moved.DataCRC(0));
        }

        fixture.Snapshot.SourceObjects[set] = raised;
        fixture.Snapshot.SourceFingerprints[set] = fingerprint;
    }

    private static Fixture CreateFixture(int side)
    {
        var triangulate = new TriangulateModifierDefinition
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            PeelBoundaryTriangles = false,
            Tolerance = 0.01
        };
        var wall = new RetainingWallModifierDefinition
        {
            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            MaxWallWidth = 2.0
        };

        var terrain = new TerrainDefinition
        {
            TerrainId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            Name = $"Interactive {side}",
            GlobalTolerance = 0.01,
            Modifiers = new List<ModifierDefinition> { triangulate, wall }
        };

        var snapshot = new TerrainBuildSnapshot
        {
            Terrain = terrain,
            ModelAbsoluteTolerance = 0.001,
            ModelUnitSystem = UnitSystem.Meters
        };

        var points = new List<ResolvedSourceObject>(side * side);
        for (int x = 0; x < side; x++)
        {
            for (int y = 0; y < side; y++)
            {
                Guid id = Guid.NewGuid();
                triangulate.Points.ObjectIds.Add(id);
                double z = 3.0 * Math.Sin(x * 0.05) + 2.0 * Math.Cos(y * 0.07);
                points.Add(CreateSourceObject(id, new Point(new Point3d(x, y, z))));
            }
        }

        snapshot.SourceObjects[triangulate.Points] = points;
        snapshot.SourceFingerprints[triangulate.Points] = 101;

        // A toe/top rail pair running across the middle of the survey, clear of the boundary.
        double mid = side * 0.5;
        double from = side * 0.2;
        double to = side * 0.8;
        var toe = new PolylineCurve(new[] { new Point3d(from, mid - 0.6, 2.0), new Point3d(to, mid - 0.6, 2.0) });
        var top = new PolylineCurve(new[] { new Point3d(from, mid + 0.6, 5.0), new Point3d(to, mid + 0.6, 5.0) });

        var curves = new List<ResolvedSourceObject>();
        ulong fingerprint = 211;
        foreach (Curve curve in new Curve[] { toe, top })
        {
            Guid id = Guid.NewGuid();
            wall.WallCurves.ObjectIds.Add(id);
            curves.Add(CreateSourceObject(id, curve));
            fingerprint = unchecked((fingerprint * 397) ^ curve.DataCRC(0));
        }

        snapshot.SourceObjects[wall.WallCurves] = curves;
        snapshot.SourceFingerprints[wall.WallCurves] = fingerprint;

        return new Fixture(terrain, snapshot, wall);
    }

    private static ResolvedSourceObject CreateSourceObject(Guid id, GeometryBase geometry)
    {
        BoundingBox bbox = geometry.GetBoundingBox(true);
        return new ResolvedSourceObject
        {
            ObjectId = id,
            Geometry = geometry,
            LocalBoundingBox = bbox,
            WorldBoundingBox = bbox,
            GeometryDataCrc = geometry.DataCRC(0)
        };
    }

    private sealed record Fixture(
        TerrainDefinition Terrain,
        TerrainBuildSnapshot Snapshot,
        RetainingWallModifierDefinition Wall);
}
