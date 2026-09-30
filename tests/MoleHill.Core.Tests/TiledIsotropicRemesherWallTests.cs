using MoleHill.Core.Engine;
using Xunit;
using Xunit.Abstractions;

namespace MoleHill.Core.Tests;

/// <summary>
/// A retaining wall and a crease under the tiled remesh. On the 1 m park the second and third passes placed
/// feature vertices on chords of already-remeshed edges and left hundreds of vertices up to 0.8 m off the
/// terrain; a wall classified again by slope on remeshed output froze remeshing artifacts. Both are pinned
/// here on a terrain small enough to run in every build.
/// </summary>
public class TiledIsotropicRemesherWallTests(ITestOutputHelper output)
{
    private const double Tile = 24.0;
    private const double WallX = 36.0; // exactly on a line of the second pass's grid
    private const double WallRun = 0.25;
    private const double WallHeight = 1.6;

    [Fact]
    public void Remesh_WallAlongATileLine_KeepsTheWallAndStaysOnTheTerrain()
    {
        SteppedTerrain(out double[] vertices, out int[] faces);
        var options = new IsotropicRemesher.Options
        {
            TargetEdgeLength = IsotropicRemesher.EstimateFaceCountPreservingTarget(vertices, faces),
            CreaseAngleDeg = 30,
            Tolerance = 0.001,
            WallFaceMinSlopeDeg = 70
        };

        IsotropicRemesher.Result result = TiledIsotropicRemesher.Remesh(vertices, faces, Array.Empty<SurfaceRemesher.ConstraintPolyline>(), options, Tile);

        Assert.True(result.Success, result.Warning);
        double inputWall = SteepArea(vertices, faces);
        double outputWall = SteepArea(result.Vertices, result.Faces);
        double worst = WorstDistance(vertices, faces, result.Vertices);
        (int loops, int nonManifold) = Topology(result.Faces);
        output.WriteLine($"{faces.Length / 3:N0} -> {result.Faces.Length / 3:N0} faces; wall {inputWall:0.00} -> {outputWall:0.00} m²; worst {worst:0.0000} m; {loops} boundary loop(s), {nonManifold} non-manifold");

        Assert.InRange(outputWall, inputWall * 0.99, inputWall * 1.01);
        Assert.True(worst < 0.001, $"a vertex is {worst:0.000} m off the terrain");
        Assert.Equal(1, loops);
        Assert.Equal(0, nonManifold);
    }

    /// <summary>A 96 m square with a 1.6 m wall along x = 36 and a 62° crease along a sine curve, whose chords cut inside the bends.</summary>
    private static void SteppedTerrain(out double[] vertices, out int[] faces)
    {
        var xs = new List<double>();
        for (double x = 0; x <= 96.0 + 1e-9; x += 1.0)
        {
            if (Math.Abs(x - WallX) < 1e-9)
            {
                xs.Add(WallX);
                xs.Add(WallX + WallRun);
            }
            else
            {
                xs.Add(x);
            }
        }

        int nx = xs.Count, ny = 97;
        var v = new double[nx * ny * 3];
        for (int j = 0; j < ny; j++)
        {
            for (int i = 0; i < nx; i++)
            {
                double x = xs[i], y = j;
                double z = (Math.Sin(x * 0.05) * 1.5) + (Math.Abs(y - 48.0 - (20.0 * Math.Sin(x / 12.0))) * 0.6);
                if (x > WallX + 1e-9)
                    z += WallHeight;
                int k = (j * nx) + i;
                v[k * 3] = x;
                v[k * 3 + 1] = y;
                v[k * 3 + 2] = z;
            }
        }

        var f = new List<int>((nx - 1) * (ny - 1) * 6);
        for (int j = 0; j < ny - 1; j++)
        {
            for (int i = 0; i < nx - 1; i++)
            {
                int a = (j * nx) + i;
                f.AddRange(new[] { a, a + 1, a + nx + 1, a, a + nx + 1, a + nx });
            }
        }

        vertices = v;
        faces = f.ToArray();
    }

    private static double SteepArea(double[] v, int[] f)
    {
        bool[] steep = FeaturePolylineGraph.BuildFrozenFaceMask(v, f, f.Length / 3, 70, 0.001);
        double area = 0;
        for (int t = 0; t < f.Length / 3; t++)
        {
            if (!steep[t])
                continue;
            int a = f[t * 3], b = f[t * 3 + 1], c = f[t * 3 + 2];
            double ux = v[b * 3] - v[a * 3], uy = v[b * 3 + 1] - v[a * 3 + 1], uz = v[b * 3 + 2] - v[a * 3 + 2];
            double wx = v[c * 3] - v[a * 3], wy = v[c * 3 + 1] - v[a * 3 + 1], wz = v[c * 3 + 2] - v[a * 3 + 2];
            double cx = (uy * wz) - (uz * wy), cy = (uz * wx) - (ux * wz), cz = (ux * wy) - (uy * wx);
            area += 0.5 * Math.Sqrt((cx * cx) + (cy * cy) + (cz * cz));
        }

        return area;
    }

    /// <summary>Largest distance from an output vertex to the nearest input face above or below it, walls included.</summary>
    private static double WorstDistance(double[] inputV, int[] inputF, double[] outputV)
    {
        double worst = 0;
        for (int i = 0; i < outputV.Length / 3; i++)
        {
            double x = outputV[i * 3], y = outputV[i * 3 + 1], z = outputV[i * 3 + 2];
            double best = double.MaxValue;
            for (int t = 0; t < inputF.Length / 3; t++)
            {
                int a = inputF[t * 3], b = inputF[t * 3 + 1], c = inputF[t * 3 + 2];
                double x0 = inputV[a * 3], y0 = inputV[a * 3 + 1], x1 = inputV[b * 3], y1 = inputV[b * 3 + 1], x2 = inputV[c * 3], y2 = inputV[c * 3 + 1];
                if (x < Math.Min(x0, Math.Min(x1, x2)) - 1e-6 || x > Math.Max(x0, Math.Max(x1, x2)) + 1e-6 ||
                    y < Math.Min(y0, Math.Min(y1, y2)) - 1e-6 || y > Math.Max(y0, Math.Max(y1, y2)) + 1e-6)
                    continue;
                double d = ((y1 - y2) * (x0 - x2)) + ((x2 - x1) * (y0 - y2));
                if (Math.Abs(d) < 1e-14)
                    continue;
                double w0 = (((y1 - y2) * (x - x2)) + ((x2 - x1) * (y - y2))) / d;
                double w1 = (((y2 - y0) * (x - x2)) + ((x0 - x2) * (y - y2))) / d;
                double w2 = 1 - w0 - w1;
                if (w0 < -1e-6 || w1 < -1e-6 || w2 < -1e-6)
                    continue;
                best = Math.Min(best, Math.Abs((w0 * inputV[a * 3 + 2]) + (w1 * inputV[b * 3 + 2]) + (w2 * inputV[c * 3 + 2]) - z));
            }

            if (best != double.MaxValue)
                worst = Math.Max(worst, best);
        }

        return worst;
    }

    private static (int Loops, int NonManifold) Topology(int[] faces)
    {
        var uses = new Dictionary<(int, int), int>();
        for (int t = 0; t < faces.Length / 3; t++)
        {
            for (int k = 0; k < 3; k++)
            {
                int a = faces[t * 3 + k], b = faces[t * 3 + ((k + 1) % 3)];
                var key = a < b ? (a, b) : (b, a);
                uses[key] = uses.GetValueOrDefault(key) + 1;
            }
        }

        int loops = TiledIsotropicRemesherQualityTests.CountLoops(uses.Where(static e => e.Value == 1).Select(static e => e.Key));
        return (loops, uses.Values.Count(static u => u > 2));
    }
}
