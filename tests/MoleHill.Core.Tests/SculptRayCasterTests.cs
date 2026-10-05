using MoleHill.Core.Sculpting;
using Xunit;

namespace MoleHill.Core.Tests;

public class SculptRayCasterTests
{
    private static (double[] Vertices, int VertexCount, int[] Faces, int FaceCount) BuildGridMesh(
        int n, double spacing, Func<double, double, double> height)
    {
        int vertexCount = n * n;
        var vertices = new double[vertexCount * 3];
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                int v = j * n + i;
                vertices[v * 3] = i * spacing;
                vertices[v * 3 + 1] = j * spacing;
                vertices[v * 3 + 2] = height(i * spacing, j * spacing);
            }
        }

        int faceCount = (n - 1) * (n - 1) * 2;
        var faces = new int[faceCount * 3];
        int f = 0;
        for (int j = 0; j < n - 1; j++)
        {
            for (int i = 0; i < n - 1; i++)
            {
                int v00 = j * n + i, v10 = v00 + 1, v01 = v00 + n, v11 = v01 + 1;
                faces[f++] = v00; faces[f++] = v10; faces[f++] = v11;
                faces[f++] = v00; faces[f++] = v11; faces[f++] = v01;
            }
        }

        return (vertices, vertexCount, faces, faceCount);
    }

    [Fact]
    public void TryIntersect_VerticalRayOnFlatGrid_HitsAtGroundHeight()
    {
        var (v, vc, f, fc) = BuildGridMesh(11, 1.0, (_, _) => 2.0);
        var caster = new SculptRayCaster(v, vc, f, fc);

        Assert.True(caster.TryIntersect(v, 3.3, 4.7, 10.0, 0, 0, -20.0, out double t));
        Assert.Equal(2.0, 10.0 - 20.0 * t, 9);
    }

    [Fact]
    public void TryIntersect_RayOutsideFootprint_Misses()
    {
        var (v, vc, f, fc) = BuildGridMesh(11, 1.0, (_, _) => 0.0);
        var caster = new SculptRayCaster(v, vc, f, fc);

        Assert.False(caster.TryIntersect(v, 20.0, 20.0, 10.0, 0, 0, -20.0, out _));
        Assert.False(caster.TryIntersect(v, -5.0, 5.0, 10.0, 0, 5.0, 0, out _));
    }

    [Fact]
    public void TryIntersect_SegmentEndingAboveGround_Misses()
    {
        var (v, vc, f, fc) = BuildGridMesh(11, 1.0, (_, _) => 0.0);
        var caster = new SculptRayCaster(v, vc, f, fc);

        Assert.False(caster.TryIntersect(v, 5.0, 5.0, 10.0, 0, 0, -5.0, out _));
    }

    [Fact]
    public void TryIntersect_AfterVertexZChange_SeesNewHeightWithoutRebuild()
    {
        var (v, vc, f, fc) = BuildGridMesh(11, 1.0, (_, _) => 0.0);
        var caster = new SculptRayCaster(v, vc, f, fc);
        for (int i = 0; i < vc; i++)
            v[i * 3 + 2] = 3.0;

        Assert.True(caster.TryIntersect(v, 5.2, 5.6, 10.0, 0, 0, -20.0, out double t));
        Assert.Equal(3.0, 10.0 - 20.0 * t, 9);
    }

    [Fact]
    public void TryIntersect_GrazingRayOverRidge_ReturnsNearestHit()
    {
        // A ridge at x = 10 is crossed twice by a low ray running along +X (at x = 7 and x = 13);
        // the walk must stop at the near flank.
        var (v, vc, f, fc) = BuildGridMesh(21, 1.0, (x, _) => 5.0 - Math.Abs(x - 10.0));
        var caster = new SculptRayCaster(v, vc, f, fc);

        Assert.True(caster.TryIntersect(v, -1.0, 10.3, 2.0, 30.0, 0.0, 0.0, out double t));
        double hitX = -1.0 + 30.0 * t;
        Assert.Equal(7.0, hitX, 6);
    }

    [Fact]
    public void TryIntersect_RandomObliqueRays_MatchBruteForce()
    {
        var (v, vc, f, fc) = BuildGridMesh(41, 0.5, (x, y) => Math.Sin(x * 0.7) * 2.0 + Math.Cos(y * 0.9) * 1.5);
        var caster = new SculptRayCaster(v, vc, f, fc);
        var random = new Random(1234);

        for (int trial = 0; trial < 500; trial++)
        {
            double ox = random.NextDouble() * 40.0 - 10.0;
            double oy = random.NextDouble() * 40.0 - 10.0;
            double oz = 8.0 + random.NextDouble() * 10.0;
            double tx = random.NextDouble() * 20.0;
            double ty = random.NextDouble() * 20.0;
            double tz = -10.0;
            double dx = tx - ox, dy = ty - oy, dz = tz - oz;

            bool expected = BruteForce(v, f, fc, ox, oy, oz, dx, dy, dz, out double expectedT);
            bool actual = caster.TryIntersect(v, ox, oy, oz, dx, dy, dz, out double actualT);

            Assert.Equal(expected, actual);
            if (expected)
                Assert.Equal(expectedT, actualT, 9);
        }
    }

    [Fact]
    public void TryLocate_PointOnMesh_WeightsFollowLaterZChange()
    {
        var (v, vc, f, fc) = BuildGridMesh(11, 1.0, (x, y) => x + 2.0 * y);
        var caster = new SculptRayCaster(v, vc, f, fc);

        Assert.True(caster.TryLocate(v, 3.25, 6.5, 3.25 + 13.0, out int face, out double w0, out double w1, out double w2));
        Assert.Equal(1.0, w0 + w1 + w2, 12);

        for (int i = 0; i < vc; i++)
            v[i * 3 + 2] *= 2.0;
        double z = w0 * v[f[face * 3] * 3 + 2] + w1 * v[f[face * 3 + 1] * 3 + 2] + w2 * v[f[face * 3 + 2] * 3 + 2];
        Assert.Equal(2.0 * (3.25 + 13.0), z, 9);
    }

    [Fact]
    public void TryLocate_StackedFaces_PicksFaceNearestGivenZ()
    {
        // Two identical footprints, one at Z = 0 and one at Z = 5.
        double[] v =
        {
            0, 0, 0, 10, 0, 0, 0, 10, 0,
            0, 0, 5, 10, 0, 5, 0, 10, 5,
        };
        int[] f = { 0, 1, 2, 3, 4, 5 };
        var caster = new SculptRayCaster(v, 6, f, 2);

        Assert.True(caster.TryLocate(v, 2, 2, 4.8, out int upper, out _, out _, out _));
        Assert.True(caster.TryLocate(v, 2, 2, 0.3, out int lower, out _, out _, out _));
        Assert.Equal(1, upper);
        Assert.Equal(0, lower);
    }

    [Fact]
    public void TryLocate_OutsideFootprint_ReturnsFalse()
    {
        var (v, vc, f, fc) = BuildGridMesh(11, 1.0, (_, _) => 0.0);
        var caster = new SculptRayCaster(v, vc, f, fc);

        Assert.False(caster.TryLocate(v, 12.0, 5.0, 0.0, out _, out _, out _, out _));
    }

    private static bool BruteForce(
        double[] v, int[] faces, int faceCount,
        double ox, double oy, double oz, double dx, double dy, double dz,
        out double best)
    {
        best = double.PositiveInfinity;
        for (int f = 0; f < faceCount; f++)
        {
            int a = faces[f * 3] * 3, b = faces[f * 3 + 1] * 3, c = faces[f * 3 + 2] * 3;
            double e1x = v[b] - v[a], e1y = v[b + 1] - v[a + 1], e1z = v[b + 2] - v[a + 2];
            double e2x = v[c] - v[a], e2y = v[c + 1] - v[a + 1], e2z = v[c + 2] - v[a + 2];
            double px = dy * e2z - dz * e2y, py = dz * e2x - dx * e2z, pz = dx * e2y - dy * e2x;
            double det = e1x * px + e1y * py + e1z * pz;
            if (Math.Abs(det) < 1e-12)
                continue;
            double inv = 1.0 / det;
            double sx = ox - v[a], sy = oy - v[a + 1], sz = oz - v[a + 2];
            double u = (sx * px + sy * py + sz * pz) * inv;
            if (u < 0 || u > 1)
                continue;
            double qx = sy * e1z - sz * e1y, qy = sz * e1x - sx * e1z, qz = sx * e1y - sy * e1x;
            double w = (dx * qx + dy * qy + dz * qz) * inv;
            if (w < 0 || u + w > 1)
                continue;
            double t = (e2x * qx + e2y * qy + e2z * qz) * inv;
            if (t >= 0 && t <= 1 && t < best)
                best = t;
        }

        return !double.IsPositiveInfinity(best);
    }
}
