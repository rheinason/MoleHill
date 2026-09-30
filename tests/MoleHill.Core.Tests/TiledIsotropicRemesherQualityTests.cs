using System.Diagnostics;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;
using Xunit.Abstractions;

namespace MoleHill.Core.Tests;

/// <summary>
/// The tiled remesh against the global one on a graded terrain: triangle shape, edge length, valence,
/// deviation from the input surface and topology, overall and next to each pass's tile lines, where a seam
/// would show. Remesh feeds sculpting and smoothing, so the tiled result has to be as good as the global
/// one there too (docs/incremental-rebuild-design-2026-09-29.md, D2). Opt in with <c>MOLEHILL_PERF=1</c>.
/// </summary>
public class TiledIsotropicRemesherQualityTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Performance")]
    public void Remesh_GradedTerrain_TiledMatchesGlobalQuality()
    {
        if (!PerformanceLane.ShouldRun(output, "tiled vs global isotropic remesh quality on a graded terrain"))
            return;

        int side = int.TryParse(Environment.GetEnvironmentVariable("MOLEHILL_TILE_SIDE"), out int s) ? s : 256;
        GradedTerrain(side, out double[] vertices, out int[] faces);
        double target = IsotropicRemesher.EstimateFaceCountPreservingTarget(vertices, faces);
        var options = new IsotropicRemesher.Options { TargetEdgeLength = target, CreaseAngleDeg = 30, Tolerance = 0.001, WallFaceMinSlopeDeg = 70 };
        double tile = target * TiledIsotropicRemesher.DefaultTileEdgeMultiple;
        output.WriteLine($"input {faces.Length / 3:N0} faces, target {target:0.###}, tile {tile:0.#}");

        var timer = Stopwatch.StartNew();
        IsotropicRemesher.Result global = IsotropicRemesher.Remesh(vertices, faces, Array.Empty<SurfaceRemesher.ConstraintPolyline>(), options);
        double globalMs = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        IsotropicRemesher.Result tiled = TiledIsotropicRemesher.Remesh(vertices, faces, Array.Empty<SurfaceRemesher.ConstraintPolyline>(), options, tile);
        double tiledMs = timer.Elapsed.TotalMilliseconds;

        var surface = new TerrainFaceGrid(vertices, vertices.Length / 3, faces, faces.Length / 3, target);
        output.WriteLine($"global {globalMs:N0} ms: {Describe(global, target, surface, tile)}");
        output.WriteLine($"tiled  {tiledMs:N0} ms: {Describe(tiled, target, surface, tile)}  [{tiled.Timing}] {tiled.Warning}");
        Assert.True(global.Success, global.Warning);
        Assert.True(tiled.Success, tiled.Warning);
        foreach (string line in DescribeBadFaces(tiled, tile, 30.0, 25))
            output.WriteLine(line);
    }

    /// <summary>Where the bad triangles are: distance of each to the nearest line of either pass's grid.</summary>
    internal static IEnumerable<string> DescribeBadFaces(IsotropicRemesher.Result result, double tile, double belowDeg, int limit)
    {
        double[] v = result.Vertices;
        int[] f = result.Faces;
        int shown = 0;
        for (int t = 0; t < f.Length / 3 && shown < limit; t++)
        {
            double angle = MinAngleDeg(v, f[t * 3], f[t * 3 + 1], f[t * 3 + 2]);
            if (angle >= belowDeg)
                continue;
            double cx = (v[f[t * 3] * 3] + v[f[t * 3 + 1] * 3] + v[f[t * 3 + 2] * 3]) / 3.0;
            double cy = (v[f[t * 3] * 3 + 1] + v[f[t * 3 + 1] * 3 + 1] + v[f[t * 3 + 2] * 3 + 1]) / 3.0;
            shown++;
            yield return $"  bad face {t}: min angle {angle:0.0} at ({cx:0.0}, {cy:0.0}); to A line {LineDistance(cx, cy, tile, 0.0):0.00}, to B line {LineDistance(cx, cy, tile, tile * 0.5):0.00}";
        }
    }

    private static double LineDistance(double x, double y, double tile, double offset)
    {
        double dx = Mod(x - offset, tile), dy = Mod(y - offset, tile);
        return Math.Min(Math.Min(dx, tile - dx), Math.Min(dy, tile - dy));
    }

    internal static string Describe(IsotropicRemesher.Result result, double target, TerrainFaceGrid surface, double tile)
    {
        double[] v = result.Vertices;
        int[] f = result.Faces;
        int faceCount = f.Length / 3;
        var minAngles = new List<double>(faceCount);
        var nearA = new List<double>();
        var nearB = new List<double>();
        for (int t = 0; t < faceCount; t++)
        {
            double angle = MinAngleDeg(v, f[t * 3], f[t * 3 + 1], f[t * 3 + 2]);
            minAngles.Add(angle);
            double cx = (v[f[t * 3] * 3] + v[f[t * 3 + 1] * 3] + v[f[t * 3 + 2] * 3]) / 3.0;
            double cy = (v[f[t * 3] * 3 + 1] + v[f[t * 3 + 1] * 3 + 1] + v[f[t * 3 + 2] * 3 + 1]) / 3.0;
            if (NearLine(cx, cy, tile, 0.0, 2.0 * target))
                nearA.Add(angle);
            if (NearLine(cx, cy, tile, tile * 0.5, 2.0 * target))
                nearB.Add(angle);
        }

        var edgeUse = new Dictionary<(int, int), int>();
        var valence = new Dictionary<int, int>();
        var lengths = new List<double>();
        for (int t = 0; t < faceCount; t++)
        {
            for (int k = 0; k < 3; k++)
            {
                int a = f[t * 3 + k], b = f[t * 3 + ((k + 1) % 3)];
                var key = a < b ? (a, b) : (b, a);
                edgeUse[key] = edgeUse.GetValueOrDefault(key) + 1;
            }
        }

        var boundaryVertex = new HashSet<int>();
        foreach (((int a, int b), int uses) in edgeUse)
        {
            valence[a] = valence.GetValueOrDefault(a) + 1;
            valence[b] = valence.GetValueOrDefault(b) + 1;
            lengths.Add(Distance(v, a, b) / target);
            if (uses == 1)
            {
                boundaryVertex.Add(a);
                boundaryVertex.Add(b);
            }
        }

        int interior = 0, six = 0, near6 = 0, extreme = 0;
        foreach ((int vertex, int n) in valence)
        {
            if (boundaryVertex.Contains(vertex))
                continue;
            interior++;
            if (n == 6) six++;
            if (n is >= 5 and <= 7) near6++;
            if (n <= 3 || n >= 9) extreme++;
        }

        double maxDeviation = 0, sumDeviation = 0, worstX = 0, worstY = 0;
        int sampled = 0;
        for (int i = 0; i < v.Length / 3; i++)
        {
            if (!surface.TryInterpolateZ(v[i * 3], v[i * 3 + 1], out double z))
                continue;
            double d = Math.Abs(v[i * 3 + 2] - z);
            if (d > maxDeviation)
            {
                maxDeviation = d;
                worstX = v[i * 3];
                worstY = v[i * 3 + 1];
            }
            sumDeviation += d;
            sampled++;
        }

        int boundaryEdges = edgeUse.Values.Count(static u => u == 1);
        int boundaryLoops = CountLoops(edgeUse.Where(static e => e.Value == 1).Select(static e => e.Key));
        int nonManifold = edgeUse.Values.Count(static u => u > 2);
        minAngles.Sort();
        nearA.Sort();
        nearB.Sort();
        lengths.Sort();
        double inBand = lengths.Count(static l => l >= 0.8 && l <= 1.6) / (double)lengths.Count;
        return $"{faceCount:N0} faces | min angle p1 {P(minAngles, 0.01):0.0} p5 {P(minAngles, 0.05):0.0} median {P(minAngles, 0.5):0.0}, " +
               $"<20° {Fraction(minAngles, 20):P2}, <30° {Fraction(minAngles, 30):P1} | near A lines p5 {P(nearA, 0.05):0.0} <30° {Fraction(nearA, 30):P1}, " +
               $"near B lines p5 {P(nearB, 0.05):0.0} <30° {Fraction(nearB, 30):P1} | edge/L p5 {P(lengths, 0.05):0.00} median {P(lengths, 0.5):0.00} p95 {P(lengths, 0.95):0.00}, in band {inBand:P1} | " +
               $"valence 6 {six / (double)interior:P1}, 5-7 {near6 / (double)interior:P1}, extreme {extreme} | " +
               $"deviation max {maxDeviation:0.0000} at ({worstX:0.00}, {worstY:0.00}) mean {sumDeviation / Math.Max(1, sampled):0.00000} | boundary edges {boundaryEdges} in {boundaryLoops} loop(s), non-manifold {nonManifold}";
    }

    /// <summary>Connected components of the boundary edges: a seam that failed to weld opens a new loop.</summary>
    internal static int CountLoops(IEnumerable<(int A, int B)> boundaryEdges)
    {
        var parent = new Dictionary<int, int>();
        int Find(int x)
        {
            while (parent[x] != x)
                x = parent[x] = parent[parent[x]];
            return x;
        }

        foreach ((int a, int b) in boundaryEdges)
        {
            parent.TryAdd(a, a);
            parent.TryAdd(b, b);
            int ra = Find(a), rb = Find(b);
            if (ra != rb)
                parent[ra] = rb;
        }

        return parent.Keys.Count(k => Find(k) == k);
    }

    private static bool NearLine(double x, double y, double tile, double offset, double band)
    {
        double dx = Math.Abs(Mod(x - offset, tile));
        double dy = Math.Abs(Mod(y - offset, tile));
        return Math.Min(dx, tile - dx) < band || Math.Min(dy, tile - dy) < band;
    }

    private static double Mod(double a, double m) => a - (m * Math.Floor(a / m));

    private static double P(List<double> sorted, double q) => sorted.Count == 0 ? double.NaN : sorted[Math.Min(sorted.Count - 1, (int)(q * sorted.Count))];

    private static double Fraction(List<double> values, double below) => values.Count == 0 ? 0 : values.Count(x => x < below) / (double)values.Count;

    private static double Distance(double[] v, int a, int b)
    {
        double dx = v[a * 3] - v[b * 3], dy = v[a * 3 + 1] - v[b * 3 + 1], dz = v[a * 3 + 2] - v[b * 3 + 2];
        return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    private static double MinAngleDeg(double[] v, int a, int b, int c)
    {
        double ab = Distance(v, a, b), bc = Distance(v, b, c), ca = Distance(v, c, a);
        double A = Angle(bc, ab, ca), B = Angle(ca, ab, bc), C = 180.0 - A - B;
        return Math.Min(A, Math.Min(B, C));

        static double Angle(double opposite, double s1, double s2)
        {
            double cos = ((s1 * s1) + (s2 * s2) - (opposite * opposite)) / Math.Max(2.0 * s1 * s2, 1e-300);
            return Math.Acos(Math.Clamp(cos, -1.0, 1.0)) * 180.0 / Math.PI;
        }
    }

    /// <summary>A 1 m jittered survey grid with a curving 6 m road graded through it (real batter fans).</summary>
    internal static void GradedTerrain(int side, out double[] vertices, out int[] faces)
    {
        int n = side;
        var v = new double[(n + 1) * (n + 1) * 3];
        for (int j = 0; j <= n; j++)
        {
            for (int i = 0; i <= n; i++)
            {
                int k = (j * (n + 1)) + i;
                uint h = (uint)k * 2654435761u;
                double jx = i > 0 && i < n ? (((h & 0xFFFF) / 65535.0) - 0.5) * 0.4 : 0;
                double jy = j > 0 && j < n ? ((((h >> 16) & 0xFFFF) / 65535.0) - 0.5) * 0.4 : 0;
                double x = i + jx, y = j + jy;
                v[k * 3] = x;
                v[k * 3 + 1] = y;
                v[k * 3 + 2] = (Math.Sin(x * 0.03) * 4.0) + (Math.Cos(y * 0.041) * 3.0) + (Math.Sin((x + y) * 0.11) * 0.4);
            }
        }

        var f = new int[n * n * 6];
        int w = 0;
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                int a = (j * (n + 1)) + i;
                f[w++] = a; f[w++] = a + 1; f[w++] = a + n + 2;
                f[w++] = a; f[w++] = a + n + 2; f[w++] = a + n + 1;
            }
        }

        var xy = new List<double>();
        var z = new List<double>();
        for (int i = 0; i <= 24; i++)
        {
            double t = i / 24.0;
            double x = 0.1 * n + (0.8 * n * t);
            double y = 0.5 * n + (0.25 * n * Math.Sin(t * Math.PI * 1.5));
            xy.Add(x);
            xy.Add(y);
            z.Add(1.0 + (2.0 * t));
        }

        var path = new PathGrader.PathDefinition(xy.ToArray(), z.ToArray(), z.Count, width: 6.0, slopeAngleDeg: 26.5, maxDistance: 15.0);
        GradingResult? graded = PathGrader.Grade(v, v.Length / 3, f, f.Length / 3, new[] { path }, out string? error, 0.001);
        Assert.True(graded != null, error);
        vertices = graded!.Vertices;
        faces = graded.Faces;
    }
}
