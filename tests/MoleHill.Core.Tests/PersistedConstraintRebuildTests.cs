using MoleHill.Core.Engine;
using MoleHill.Core.Processing;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// A line persisted as a constraint must be the line the TIN was built from. Triangulate stations long
/// straight runs; when it persisted the raw line instead, every later constrained rebuild (Retaining Wall,
/// grading) inserted each long segment within rounding of stations the mesh already carried, and the
/// triangulator left a zero-area cap at each one. On the Glyvra terrain that was ~11,000 caps; the stair
/// stage's tiny-face cleanup then deleted them into 65 holes and the wall rebuild gave up.
/// </summary>
public sealed class PersistedConstraintRebuildTests
{
    private const double Tolerance = 0.01;

    [Fact]
    public void ProcessSeparately_KeepsEachClassAlignedWithItsInputs()
    {
        double[] longLine = [0.3, 1.7, 5.0, 37.9, 13.1, 9.0];
        double[] tooShort = [1.0, 1.0, 0.0];
        double[] contour = [0, 30, 2, 10, 30, 2, 20, 30, 2, 30, 30, 2];

        TerrainConstraintPreprocessor.Result result = TerrainConstraintPreprocessor.ProcessSeparately(
            new[] { tooShort, longLine }, new[] { contour, tooShort }, Tolerance);

        Assert.Equal(2, result.Breaklines.Count);
        Assert.Null(result.Breaklines[0]);
        Assert.NotNull(result.Breaklines[1]);
        Assert.Equal(longLine[0], result.Breaklines[1]![0]);
        Assert.Equal(longLine[4], result.Breaklines[1]![^2]);
        Assert.Equal(2, result.Contours.Count);
        Assert.NotNull(result.Contours[0]);
        Assert.Null(result.Contours[1]);

        List<double[]> combined = TerrainConstraintPreprocessor.Process(
            new[] { tooShort, longLine }, new[] { contour, tooShort }, Tolerance);
        Assert.Equal(new[] { result.Breaklines[1]!, result.Contours[0]! }, combined);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rebuild_WithPersistedLine_RawAndStationedLinesProduceNoCaps(bool persistRaw)
    {
        double[] raw = [0.3, 1.7, 5.0, 37.9, 13.1, 9.0];
        List<double[]> stationed = TerrainConstraintPreprocessor.Process(new List<double[]> { raw }, Array.Empty<double[]>(), Tolerance);
        Assert.True(stationed[0].Length / 3 > 2, "the long straight line must be stationed for this case to mean anything");

        (double[] vertices, int[] faces) = BuildTin(stationed);
        Assert.Equal(0, CountCaps(vertices, faces));

        double[] persisted = persistRaw ? raw : stationed[0];
        SurfaceRemesher.Result result = SurfaceRemesher.Remesh(
            vertices,
            faces,
            new[] { new SurfaceRemesher.ConstraintPolyline(persisted, persisted.Length / 3, false, PreserveInputElevation: true) },
            new SurfaceRemesher.Options
            {
                Tolerance = Tolerance,
                ProtectSharpEdges = true,
                ConstraintInsertionOnly = true,
                AddReducedInteriorGuideSeeds = false,
                AddConstraintCorridorSeeds = false
            });

        Assert.True(result.Success, result.Warning);
        // Shared triangulation now splits raw segments at existing station vertices too. Both
        // representations must remain safe; requiring the old defect would reject that repair.
        Assert.Equal(0, CountCaps(result.Vertices, result.Faces));
    }

    private static (double[] vertices, int[] faces) BuildTin(List<double[]> lines)
    {
        var xy = new List<double>();
        var z = new List<double>();
        var segments = new List<int>();
        for (int gx = -2; gx <= 42; gx += 4)
        {
            for (int gy = -4; gy <= 20; gy += 4)
            {
                xy.Add(gx + 0.37);
                xy.Add(gy + 0.21);
                z.Add(4.0 + (0.1 * gx) + (0.05 * gy));
            }
        }

        foreach (double[] line in lines)
        {
            int start = z.Count;
            for (int i = 0; i < line.Length / 3; i++)
            {
                xy.Add(line[i * 3]);
                xy.Add(line[i * 3 + 1]);
                z.Add(line[i * 3 + 2]);
                if (i > 0)
                {
                    segments.Add(start + i - 1);
                    segments.Add(start + i);
                }
            }
        }

        TinResult? tin = new TinEngine().Build(xy.ToArray(), z.ToArray(), segments.ToArray(), QualitySettings.None, out string? message);
        Assert.True(tin != null, message);
        return (tin!.Vertices, tin.Faces);
    }

    /// <summary>Faces whose height over their longest edge is below the tolerance.</summary>
    private static int CountCaps(double[] v, int[] f)
    {
        int count = 0;
        for (int t = 0; t < f.Length / 3; t++)
        {
            int a = f[t * 3], b = f[t * 3 + 1], c = f[t * 3 + 2];
            double ux = v[b * 3] - v[a * 3], uy = v[b * 3 + 1] - v[a * 3 + 1], uz = v[b * 3 + 2] - v[a * 3 + 2];
            double wx = v[c * 3] - v[a * 3], wy = v[c * 3 + 1] - v[a * 3 + 1], wz = v[c * 3 + 2] - v[a * 3 + 2];
            double nx = (uy * wz) - (uz * wy), ny = (uz * wx) - (ux * wz), nz = (ux * wy) - (uy * wx);
            double doubleArea = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
            double longest = Math.Max(Length(v, a, b), Math.Max(Length(v, b, c), Length(v, c, a)));
            if (doubleArea < Tolerance * longest)
                count++;
        }

        return count;
    }

    private static double Length(double[] v, int a, int b)
    {
        double dx = v[a * 3] - v[b * 3], dy = v[a * 3 + 1] - v[b * 3 + 1], dz = v[a * 3 + 2] - v[b * 3 + 2];
        return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }
}
