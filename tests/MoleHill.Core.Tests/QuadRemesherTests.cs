using MoleHill.Core.Engine;
using MoleHill.Core.Retopo;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// End-to-end coverage of the rebuilt <see cref="QuadRemesher"/> pipeline (cross-field →
/// field-aligned isotropic remesh → tri-to-quad pairing). Replaces the parametrization/lattice tests
/// of the razed GuidedParametrizer/QuadExtractor path.
/// </summary>
public class QuadRemesherTests
{
    private static readonly IReadOnlyList<SurfaceRemesher.ConstraintPolyline> NoConstraints =
        Array.Empty<SurfaceRemesher.ConstraintPolyline>();

    private static (double[] vertices, int[] faces) BuildGrid(double[] xs, double[] ys, Func<double, double, double> z)
    {
        int nx = xs.Length, ny = ys.Length;
        var vertices = new double[nx * ny * 3];
        for (int j = 0; j < ny; j++)
        {
            for (int i = 0; i < nx; i++)
            {
                int v = (j * nx) + i;
                vertices[v * 3] = xs[i];
                vertices[v * 3 + 1] = ys[j];
                vertices[v * 3 + 2] = z(xs[i], ys[j]);
            }
        }

        var faces = new int[(nx - 1) * (ny - 1) * 6];
        int f = 0;
        for (int j = 0; j < ny - 1; j++)
        {
            for (int i = 0; i < nx - 1; i++)
            {
                int v00 = (j * nx) + i;
                faces[f++] = v00; faces[f++] = v00 + 1; faces[f++] = v00 + nx + 1;
                faces[f++] = v00; faces[f++] = v00 + nx + 1; faces[f++] = v00 + nx;
            }
        }

        return (vertices, faces);
    }

    private static double[] Steps(double from, double to, double step)
    {
        var values = new List<double>();
        for (double v = from; v <= to + 1e-9; v += step)
            values.Add(v);
        return values.ToArray();
    }

    private static int[] TriangulatedShadow(int[] quads, int[] tris)
    {
        var faces = new List<int>((quads.Length / 4 * 6) + tris.Length);
        for (int i = 0; i < quads.Length / 4; i++)
        {
            int a = quads[i * 4], b = quads[i * 4 + 1], c = quads[i * 4 + 2], d = quads[i * 4 + 3];
            faces.Add(a); faces.Add(b); faces.Add(c);
            faces.Add(a); faces.Add(c); faces.Add(d);
        }

        faces.AddRange(tris);
        return faces.ToArray();
    }

    private static void AssertWatertight(QuadRemesher.Result result)
    {
        int[] shadow = TriangulatedShadow(result.Quads, result.Tris);
        var topology = MeshTopologyValidator.AnalyzeBoundaryGraph(shadow, shadow.Length / 3);
        Assert.Equal(0, topology.NonManifoldEdgeCount);
        Assert.False(topology.HasOpenBoundaryChains);
    }

    [Fact]
    public void Remesh_AxisAlignedGrid_ProducesQuadDominantWatertightMesh()
    {
        var (vertices, faces) = BuildGrid(Steps(0, 12, 1.0), Steps(0, 12, 1.0), (_, _) => 0.0);

        var result = QuadRemesher.Remesh(vertices, faces, NoConstraints, new QuadRemesher.Options
        {
            EdgeLength = 1.5,
            CreaseAngleDeg = 30,
            Tolerance = 0.01
        });

        Assert.True(result.Success, result.Warning);
        Assert.True(result.QuadCount * 2 >= result.TriangleCount,
            $"expected quad-dominant output, got {result.QuadCount} quads vs {result.TriangleCount} tris");
        foreach (double v in result.Vertices)
            Assert.True(double.IsFinite(v));
        AssertWatertight(result);
    }

    /// <summary>Deterministic hash jitter on interior vertices (breaks the pre-aligned grid diagonals).</summary>
    private static void JitterInterior(double[] vertices, double amount, double minX, double maxX, double minY, double maxY)
    {
        for (int i = 0; i < vertices.Length / 3; i++)
        {
            double x = vertices[i * 3], y = vertices[i * 3 + 1];
            if (x <= minX + 1e-9 || x >= maxX - 1e-9 || y <= minY + 1e-9 || y >= maxY - 1e-9)
                continue;
            uint h = (uint)(i * 2654435761u);
            vertices[i * 3] = x + ((((h & 0xFFFF) / 65535.0) - 0.5) * 2.0 * amount);
            vertices[i * 3 + 1] = y + (((((h >> 16) & 0xFFFF) / 65535.0) - 0.5) * 2.0 * amount);
        }
    }

    [Fact]
    public void Remesh_JitteredPatch_FieldFlipYieldsQuadMajority()
    {
        // A jittered patch has no pre-aligned diagonals, so the pairing outcome is governed by the
        // remesh's flip objective. The field-alignment flip (θ steers diagonals to the ±45° hypotenuse)
        // must yield a clear quad majority — pure-Lawson equilateral triangles would leave mostly tris.
        var (vertices, faces) = BuildGrid(Steps(0, 14, 1.0), Steps(0, 14, 1.0), (_, _) => 0.0);
        JitterInterior(vertices, 0.3, 0, 14, 0, 14);

        var result = QuadRemesher.Remesh(vertices, faces, NoConstraints, new QuadRemesher.Options
        {
            EdgeLength = 1.5,
            CreaseAngleDeg = 30,
            Tolerance = 0.01
        });

        Assert.True(result.Success, result.Warning);
        AssertWatertight(result);
        Assert.True(result.QuadCount > result.TriangleCount,
            $"field flip did not yield a quad majority: {result.QuadCount} quads vs {result.TriangleCount} tris");
    }

    [Fact]
    public void Remesh_CreasedRoof_RidgeSurvivesAndNoQuadStraddlesIt()
    {
        var (vertices, faces) = BuildGrid(Steps(0, 12, 1.0), Steps(-6, 6, 1.0), (_, y) => 3.0 - (Math.Abs(y) * 0.5));

        var result = QuadRemesher.Remesh(vertices, faces, NoConstraints, new QuadRemesher.Options
        {
            EdgeLength = 1.4,
            CreaseAngleDeg = 20,
            Tolerance = 0.01
        });

        Assert.True(result.Success, result.Warning);
        AssertWatertight(result);

        int ridgeVertices = 0;
        for (int i = 0; i < result.Vertices.Length / 3; i++)
        {
            if (Math.Abs(result.Vertices[i * 3 + 1]) < 1e-9)
                ridgeVertices++;
        }

        Assert.True(ridgeVertices >= 2, "ridge chain vanished");

        for (int q = 0; q < result.QuadCount; q++)
        {
            bool above = false, below = false;
            for (int c = 0; c < 4; c++)
            {
                double y = result.Vertices[result.Quads[q * 4 + c] * 3 + 1];
                if (y > 1e-9) above = true;
                else if (y < -1e-9) below = true;
            }

            Assert.False(above && below, $"quad {q} straddles the ridge crease");
        }
    }

    [Fact]
    public void Remesh_SteepWallBand_WallVerticesUntouchedAndOutputHoleFree()
    {
        double[] xs = { 0, 1.5, 3, 4.5, 5, 5.2, 6.7, 8.2, 9.7, 11.2 };
        var (vertices, faces) = BuildGrid(xs, Steps(0, 6, 1.5), (x, _) => x <= 5.0 ? 0.0 : (x >= 5.2 ? 3.0 : (x - 5.0) * 15.0));

        var result = QuadRemesher.Remesh(vertices, faces, NoConstraints, new QuadRemesher.Options
        {
            EdgeLength = 1.5,
            CreaseAngleDeg = 30,
            Tolerance = 0.01,
            WallFaceMinSlopeDeg = 70
        });

        Assert.True(result.Success, result.Warning);
        AssertWatertight(result);

        // Every input wall-band vertex must exist bit-identical in the output (frozen through).
        for (int i = 0; i < vertices.Length / 3; i++)
        {
            double x = vertices[i * 3];
            if (x != 5.0 && x != 5.2)
                continue;

            bool found = false;
            for (int o = 0; o < result.Vertices.Length / 3 && !found; o++)
            {
                found = result.Vertices[o * 3] == vertices[i * 3]
                    && result.Vertices[o * 3 + 1] == vertices[i * 3 + 1]
                    && result.Vertices[o * 3 + 2] == vertices[i * 3 + 2];
            }

            Assert.True(found, $"wall vertex ({vertices[i * 3]}, {vertices[i * 3 + 1]}, {vertices[i * 3 + 2]}) was moved or removed");
        }
    }
}
