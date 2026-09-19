using System.Diagnostics;
using MoleHill.Core.Tests;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Geometry;
using Xunit;
using Xunit.Abstractions;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// The geometry-heavy stack from the 2026-09-19 edit-to-visible trace, run through the real
/// <see cref="TerrainBuildService"/> so the measurement includes the Rhino stage around the Core
/// graders — input resolution, mesh marshalling, the grading tiers and the stage cache — which
/// <c>PadGraderCreateConstraintsBenchmarkTests</c> deliberately excludes.
///
/// It exists to close one named acceptance gap: the snapper region clip (commit <c>2aad7d0</c>) was
/// accepted on a micro-benchmark, and architecture.md recorded that "no trace has yet measured what
/// the 7.0 s build becomes". This measures that build.
/// </summary>
/// <remarks>
/// Opt in with <c>MOLEHILL_PERF=1</c>, and it needs the native Rhino runtime. Single observations on
/// one machine, not warmed medians. It measures the worker only: debounce, the marshal back to the UI
/// thread, display publication and redraw are host costs and are not exercised here. The heavy fixture
/// measured those at 1.4% combined, which is why a worker-only number is meaningful for this shape.
/// </remarks>
public class GeometryHeavyStackBenchmarkTests(ITestOutputHelper output)
{
    [RhinoNativeFact]
    [Trait("Category", "Performance")]
    public void Build_GeometryHeavyStack_ColdThenGradePadEdit()
    {
        if (!PerformanceLane.ShouldRun(output, "geometry-heavy modifier stack, cold build and Grade Pad edit"))
            return;

        GeometryHeavyStackBenchmark.Run(output.WriteLine);
    }
}

/// <summary>
/// The benchmark body, separated from its xunit wrapper so it can also be driven from inside a real
/// Rhino process.
///
/// That is not a convenience. On Rhino 8.35 the native lane cannot start at all — `rhcommon_c` refuses
/// to initialise outside a Rhino process, so every `[RhinoNativeFact]` fails with
/// `DllNotFoundException` and the xunit wrapper above cannot execute on this machine (see
/// `docs/validation-lanes.md`, "Known broken"). <see cref="RunToFile"/> is the route that does work:
/// load this assembly inside a `rhino-mcp` slot and call it, per `docs/rhino-live-testing.md`.
/// </summary>
public static class GeometryHeavyStackBenchmark
{
    /// <summary>The traced fixture: a 250x250 synthetic survey, 62,500 points.</summary>
    private const int GridSide = 250;

    /// <summary>
    /// Entry point for a Rhino-hosted run. Writes the same report to <paramref name="path"/>, because a
    /// script driven through <c>run_command</c> gets back only "Done." and has to leave its results on
    /// disk. Returns the path so the caller can echo it.
    /// </summary>
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
        StackFixture fixture = CreateFixture();
        var cache = new TerrainRuntimeCache();
        var service = new TerrainBuildService();

        var coldTimer = Stopwatch.StartNew();
        TerrainBuildResult cold = service.Build(fixture.Snapshot, cache, TerrainBuildMode.Final);
        coldTimer.Stop();

        if (cold.PrimaryMesh == null)
            throw new InvalidOperationException("The cold build produced no mesh.");
        Report(write, "COLD", cold, coldTimer.Elapsed);

        // The trace's edit: a slope nudge on Grade Pad, which is near the top of the stack and so
        // re-runs everything below it. Only Triangulate is reused.
        fixture.Pad.SlopeAngle = 26.5;

        var editTimer = Stopwatch.StartNew();
        TerrainBuildResult edited = service.Build(fixture.Snapshot, cache, TerrainBuildMode.Final);
        editTimer.Stop();

        if (edited.PrimaryMesh == null)
            throw new InvalidOperationException("The edited build produced no mesh.");
        Report(write, "GRADE PAD EDIT", edited, editTimer.Elapsed);

        write(
            "Trace 2026-09-19, before the snapper region clip: cold 6.28 s, Grade Pad edit 5.12 s, " +
            "of which Grade Pad 2.97 s (CreateConstraints 1.28 s) and Grade Path 1.72 s " +
            "(CreateConstraints 0.72 s).");
    }

    private static void Report(Action<string> write, string label, TerrainBuildResult result, TimeSpan wall)
    {
        write(
            $"--- {label}: {wall.TotalMilliseconds:N0} ms wall, " +
            $"{result.PrimaryMesh!.Vertices.Count:N0} verts / {result.PrimaryMesh.Faces.Count:N0} faces");
        foreach (TerrainBuildTiming timing in result.Timings)
        {
            string detail = string.IsNullOrEmpty(timing.Detail) ? string.Empty : $"  [{timing.Detail}]";
            write($"    {timing.Elapsed.TotalMilliseconds,9:N1} ms  {timing.Stage}{detail}");
        }
    }

    private static StackFixture CreateFixture()
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
            SlopeAngle = 33.0,
            MaxDistance = 12.0
        };
        var path = new GradePathModifierDefinition
        {
            Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Width = 6.0,
            SlopeAngle = 33.0,
            MaxDistance = 12.0
        };
        var smooth = new SmoothModifierDefinition
        {
            Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
            Iterations = 2,
            Strength = 0.3
        };
        var remesh = new RemeshModifierDefinition
        {
            Id = Guid.Parse("55555555-5555-5555-5555-555555555555"),
            Mode = "isotropic",
            EdgeLength = 0.0
        };

        var terrain = new TerrainDefinition
        {
            TerrainId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            Name = "Geometry Heavy",
            GlobalTolerance = 0.01,
            Modifiers = new List<ModifierDefinition> { triangulate, pad, path, smooth, remesh }
        };

        var snapshot = new TerrainBuildSnapshot
        {
            Terrain = terrain,
            ModelAbsoluteTolerance = 0.001,
            ModelUnitSystem = UnitSystem.Meters
        };

        AddPointGrid(snapshot, triangulate.Points);

        // A pad over roughly 8% of the survey, as the traced pad was.
        const double extent = GridSide;
        const double padSize = extent * 0.283;
        const double padOrigin = (extent - padSize) * 0.5;
        AddCurves(
            snapshot,
            pad.Boundaries,
            CreateClosedPolylineCurve(
                new Point3d(padOrigin, padOrigin, 5.0),
                new Point3d(padOrigin + padSize, padOrigin, 5.0),
                new Point3d(padOrigin + padSize, padOrigin + padSize, 5.0),
                new Point3d(padOrigin, padOrigin + padSize, 5.0)));

        // A path crossing the survey clear of the pad.
        AddCurves(
            snapshot,
            path.Paths,
            new PolylineCurve(new[]
            {
                new Point3d(10.0, 30.0, 3.0),
                new Point3d(90.0, 40.0, 4.5),
                new Point3d(170.0, 30.0, 3.0),
                new Point3d(240.0, 45.0, 5.5)
            }));

        return new StackFixture(terrain, snapshot, pad);
    }

    private static void AddPointGrid(TerrainBuildSnapshot snapshot, SourceReferenceSet sourceSet)
    {
        var objects = new List<ResolvedSourceObject>(GridSide * GridSide);
        for (int x = 0; x < GridSide; x++)
        {
            for (int y = 0; y < GridSide; y++)
            {
                Guid id = Guid.NewGuid();
                sourceSet.ObjectIds.Add(id);
                // A gentle rolling surface, so grading has real height differences to daylight into.
                double z = 3.0 * Math.Sin(x * 0.05) + 2.0 * Math.Cos(y * 0.07);
                objects.Add(CreateSourceObject(id, new Point(new Point3d(x, y, z))));
            }
        }

        snapshot.SourceObjects[sourceSet] = objects;
        snapshot.SourceFingerprints[sourceSet] = 101;
    }

    private static void AddCurves(TerrainBuildSnapshot snapshot, SourceReferenceSet sourceSet, params Curve[] curves)
    {
        var objects = new List<ResolvedSourceObject>();
        ulong fingerprint = 211;
        foreach (Curve curve in curves)
        {
            Guid id = Guid.NewGuid();
            sourceSet.ObjectIds.Add(id);
            objects.Add(CreateSourceObject(id, curve));
            fingerprint = unchecked((fingerprint * 397) ^ curve.DataCRC(0));
        }

        snapshot.SourceObjects[sourceSet] = objects;
        snapshot.SourceFingerprints[sourceSet] = fingerprint;
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

    private static PolylineCurve CreateClosedPolylineCurve(params Point3d[] points)
    {
        var closed = new Point3d[points.Length + 1];
        Array.Copy(points, closed, points.Length);
        closed[^1] = points[0];
        return new PolylineCurve(closed);
    }

    private sealed record StackFixture(
        TerrainDefinition Terrain,
        TerrainBuildSnapshot Snapshot,
        GradePadModifierDefinition Pad);
}
