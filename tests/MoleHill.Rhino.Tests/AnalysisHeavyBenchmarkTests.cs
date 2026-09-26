using System.Diagnostics;
using MoleHill.Core.Tests;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Geometry;
using Xunit;
using Xunit.Abstractions;

namespace MoleHill.Rhino.Tests;

public class AnalysisHeavyBenchmarkTests(ITestOutputHelper output)
{
    [RhinoNativeFact]
    [Trait("Category", "Performance")]
    public void Build_AnalysisHeavyTerrain_ColdThenPointEdit()
    {
        if (!PerformanceLane.ShouldRun(output, "analysis-heavy terrain, cold build and point edit"))
            return;

        var recorder = new PerfSampleRecorder();
        AnalysisHeavyBenchmark.Sample(recorder);
        foreach ((string metric, double ms) in recorder.Values.OrderBy(static p => p.Key, StringComparer.Ordinal))
            output.WriteLine($"{ms,10:N1} ms  {metric}");
    }
}

/// <summary>
/// The analysis-heavy shape from the 2026-09-19 edit-to-visible trace: a 122,500-point survey (about 244k
/// faces) with the six analyses the trace had enabled — Slope, Aspect, Elevation, Waterflow, Catchments
/// and Ponding. On that trace Ponding (3.72 s) and Catchments (1.40 s) were 69% of a 7.5 s wait.
/// </summary>
/// <remarks>
/// A <b>reconstruction</b>: the traced fixture was not saved. The survey is the other benchmarks' rolling
/// surface plus value noise on a 2.5 m lattice, chosen by a sweep on 2026-09-26 as the setting that loads
/// the drainage analyses hardest: about 1,870 ponds and 1,620 catchments, where a pure sinusoid gives 11
/// and 22. Two measured facts shaped it. Waterflow is ~1.1 s of fixed cost as soon as it has one source
/// and 0 ms with none (the trace's "under 10 ms" had none), so four sources keep that cost gated. And
/// Catchments' traced 1.40 s did not reproduce on any surface swept (at most ~220 ms, highest on a few
/// large basins), so this fixture gates Catchments as it is now rather than reproducing that figure.
/// Absolute numbers are this fixture's own; the baseline is what they are compared against. Worker time
/// only, like the other hosted benchmarks.
/// </remarks>
public static class AnalysisHeavyBenchmark
{
    private const int GridSide = 350;
    private const double NoiseCell = 2.5;
    private const double NoiseAmplitude = 0.4;
    private const int WaterflowSourceSide = 2;

    /// <summary>
    /// One hosted-lane sample: a cold build, then a single survey point nudged 50 mm — a real source edit,
    /// so the triangulation and every analysis run again, which is what the user waited 7.5 s for.
    /// </summary>
    public static void Sample(PerfSampleRecorder recorder)
    {
        Fixture fixture = CreateFixture();
        var cache = new TerrainRuntimeCache();
        var service = new TerrainBuildService();

        var coldTimer = Stopwatch.StartNew();
        TerrainBuildResult cold = service.Build(fixture.Snapshot, cache, TerrainBuildMode.Final);
        coldTimer.Stop();
        if (cold.PrimaryMesh == null)
            throw new InvalidOperationException("The cold build produced no mesh.");
        recorder.RecordBuild("cold", cold, coldTimer.Elapsed);

        NudgePoint(fixture, GridSide / 2 * GridSide + GridSide / 2, 0.05);
        var editTimer = Stopwatch.StartNew();
        TerrainBuildResult edited = service.Build(fixture.Snapshot, cache, TerrainBuildMode.Final);
        editTimer.Stop();
        if (edited.PrimaryMesh == null)
            throw new InvalidOperationException("The edited build produced no mesh.");
        recorder.RecordBuild("point-edit", edited, editTimer.Elapsed);
    }

    private static void NudgePoint(Fixture fixture, int index, double dz)
    {
        SourceReferenceSet set = fixture.Triangulate.Points;
        List<ResolvedSourceObject> points = fixture.Snapshot.SourceObjects[set];
        ResolvedSourceObject source = points[index];
        Point3d location = ((Point)source.Geometry).Location;
        points[index] = CreateSourceObject(source.ObjectId, new Point(location + new Vector3d(0, 0, dz)));
        fixture.Snapshot.SourceFingerprints[set] = unchecked(fixture.Snapshot.SourceFingerprints[set] * 397 + 1);
    }

    private static Fixture CreateFixture()
    {
        var triangulate = new TriangulateModifierDefinition
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            PeelBoundaryTriangles = false,
            Tolerance = 0.01
        };
        var waterflow = new WaterflowAnalysisDefinition { Id = Guid.Parse("a0000000-0000-0000-0000-000000000004") };

        var terrain = new TerrainDefinition
        {
            TerrainId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            Name = "Analysis Heavy",
            GlobalTolerance = 0.01,
            Modifiers = new List<ModifierDefinition> { triangulate },
            Analyses = new List<AnalysisDefinition>
            {
                new SlopeAnalysisDefinition { Id = Guid.Parse("a0000000-0000-0000-0000-000000000001") },
                new AspectAnalysisDefinition { Id = Guid.Parse("a0000000-0000-0000-0000-000000000002") },
                new ElevationAnalysisDefinition { Id = Guid.Parse("a0000000-0000-0000-0000-000000000003") },
                waterflow,
                new CatchmentAnalysisDefinition { Id = Guid.Parse("a0000000-0000-0000-0000-000000000005") },
                new PondingAnalysisDefinition { Id = Guid.Parse("a0000000-0000-0000-0000-000000000006") }
            }
        };

        var snapshot = new TerrainBuildSnapshot
        {
            Terrain = terrain,
            ModelAbsoluteTolerance = 0.001,
            ModelUnitSystem = UnitSystem.Meters
        };

        var points = new List<ResolvedSourceObject>(GridSide * GridSide);
        for (int x = 0; x < GridSide; x++)
        {
            for (int y = 0; y < GridSide; y++)
            {
                Guid id = Guid.NewGuid();
                triangulate.Points.ObjectIds.Add(id);
                points.Add(CreateSourceObject(id, new Point(new Point3d(x, y, Elevation(x, y)))));
            }
        }

        snapshot.SourceObjects[triangulate.Points] = points;
        snapshot.SourceFingerprints[triangulate.Points] = 101;

        // A small lattice of waterflow sources across the survey's interior. Waterflow's cost is almost all
        // fixed setup, so the count barely matters as long as it is not zero.
        var sources = new List<ResolvedSourceObject>();
        for (int i = 1; i <= WaterflowSourceSide; i++)
        {
            for (int j = 1; j <= WaterflowSourceSide; j++)
            {
                double x = GridSide * i / (WaterflowSourceSide + 1.0);
                double y = GridSide * j / (WaterflowSourceSide + 1.0);
                Guid id = Guid.NewGuid();
                waterflow.Sources.ObjectIds.Add(id);
                sources.Add(CreateSourceObject(id, new Point(new Point3d(x, y, Elevation(x, y) + 1.0))));
            }
        }

        snapshot.SourceObjects[waterflow.Sources] = sources;
        snapshot.SourceFingerprints[waterflow.Sources] = 307;

        return new Fixture(snapshot, triangulate);
    }

    /// <summary>The shared rolling surface plus smoothed value noise. Deterministic.</summary>
    private static double Elevation(double x, double y)
    {
        double gx = x / NoiseCell;
        double gy = y / NoiseCell;
        int ix = (int)Math.Floor(gx);
        int iy = (int)Math.Floor(gy);
        double fx = gx - ix;
        double fy = gy - iy;
        double sx = fx * fx * (3 - 2 * fx);
        double sy = fy * fy * (3 - 2 * fy);
        double a = Lattice(ix, iy), b = Lattice(ix + 1, iy), c = Lattice(ix, iy + 1), d = Lattice(ix + 1, iy + 1);
        double noise = a + (b - a) * sx + (c - a) * sy + (a - b - c + d) * sx * sy;
        return 3.0 * Math.Sin(x * 0.05) + 2.0 * Math.Cos(y * 0.07) + NoiseAmplitude * noise;
    }

    /// <summary>An integer hash mapped to [-1, 1]; stable across runtimes, unlike a seeded <see cref="Random"/>.</summary>
    private static double Lattice(int x, int y)
    {
        unchecked
        {
            uint h = (uint)x * 374761393u + (uint)y * 668265263u;
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return h / (double)uint.MaxValue * 2.0 - 1.0;
        }
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

    private sealed record Fixture(TerrainBuildSnapshot Snapshot, TriangulateModifierDefinition Triangulate);
}
