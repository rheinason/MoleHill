using MoleHill.Core.Engine;
using MoleHill.Core.Retopo;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// Covers <see cref="TriQuadPairer"/>: geometry-preserving tri-to-quad merging. Every input triangle
/// must appear exactly once (as a triangle or half a quad), features are never straddled, and the
/// triangulated shadow of the output is watertight.
/// </summary>
public class TriQuadPairerTests
{
    private static (double[] vertices, int[] faces) BuildGrid(int nx, int ny, double spacing, Func<double, double, double> z)
    {
        var vertices = new double[(nx + 1) * (ny + 1) * 3];
        for (int j = 0; j <= ny; j++)
        {
            for (int i = 0; i <= nx; i++)
            {
                int v = (j * (nx + 1)) + i;
                vertices[v * 3] = i * spacing;
                vertices[v * 3 + 1] = j * spacing;
                vertices[v * 3 + 2] = z(i * spacing, j * spacing);
            }
        }

        var faces = new int[nx * ny * 6];
        int f = 0;
        for (int j = 0; j < ny; j++)
        {
            for (int i = 0; i < nx; i++)
            {
                int v00 = (j * (nx + 1)) + i;
                int v10 = v00 + 1;
                int v01 = v00 + nx + 1;
                int v11 = v01 + 1;
                faces[f++] = v00; faces[f++] = v10; faces[f++] = v11;
                faces[f++] = v00; faces[f++] = v11; faces[f++] = v01;
            }
        }

        return (vertices, faces);
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

    [Fact]
    public void Pair_RightTriangulatedFlatGrid_AllTrianglesBecomeQuads()
    {
        var (vertices, faces) = BuildGrid(8, 8, 1.0, (_, _) => 0.0);

        var result = TriQuadPairer.Pair(vertices, faces, new HashSet<long>(), null, new TriQuadPairer.Options());

        Assert.Equal(64, result.QuadCount);
        Assert.Empty(result.Tris);

        var topology = MeshTopologyValidator.AnalyzeBoundaryGraph(
            TriangulatedShadow(result.Quads, result.Tris), (result.Quads.Length / 4 * 2) + (result.Tris.Length / 3));
        Assert.Equal(0, topology.NonManifoldEdgeCount);
        Assert.False(topology.HasOpenBoundaryChains);
    }

    [Fact]
    public void Pair_RidgeFeatureEdges_NoQuadStraddlesTheRidge()
    {
        // Gable roof over y ∈ [-4, 4]; ridge along y = 0 marked as feature edges.
        var (vertices, faces) = BuildGrid(8, 8, 1.0, (_, y) => 2.0 - (Math.Abs(y - 4.0) * 0.5));
        var featureEdges = new HashSet<long>();
        for (int t = 0; t < faces.Length / 3; t++)
        {
            for (int c = 0; c < 3; c++)
            {
                int a = faces[t * 3 + c];
                int b = faces[t * 3 + ((c + 1) % 3)];
                if (Math.Abs(vertices[a * 3 + 1] - 4.0) < 1e-9 && Math.Abs(vertices[b * 3 + 1] - 4.0) < 1e-9)
                    featureEdges.Add(IndexedMeshTools.GetEdgeKey(a, b));
            }
        }

        var result = TriQuadPairer.Pair(vertices, faces, featureEdges, null, new TriQuadPairer.Options());

        Assert.True(result.QuadCount > 0);
        for (int q = 0; q < result.QuadCount; q++)
        {
            bool above = false, below = false;
            for (int c = 0; c < 4; c++)
            {
                double y = vertices[result.Quads[q * 4 + c] * 3 + 1];
                if (y > 4.0 + 1e-9) above = true;
                else if (y < 4.0 - 1e-9) below = true;
            }

            Assert.False(above && below, $"quad {q} straddles the ridge");
        }
    }

    [Fact]
    public void Pair_EveryInputTriangleAppearsExactlyOnce()
    {
        var (vertices, faces) = BuildGrid(6, 5, 1.0, (x, y) => 0.3 * Math.Sin(x) * Math.Cos(y));

        var result = TriQuadPairer.Pair(vertices, faces, new HashSet<long>(), null, new TriQuadPairer.Options());

        int inputTriangles = faces.Length / 3;
        int accounted = (result.QuadCount * 2) + (result.Tris.Length / 3);
        Assert.Equal(inputTriangles, accounted);
    }

    [Fact]
    public void Pair_VerticalWallBand_WallTrianglesPairInTheirOwnPlane()
    {
        // A single vertical quad wall split into two triangles: XY-degenerate, but pairable in-plane.
        double[] vertices =
        {
            0, 0, 0,
            4, 0, 0,
            4, 0, 3,
            0, 0, 3
        };
        int[] faces = { 0, 1, 2, 0, 2, 3 };
        var frozen = new[] { true, true };

        var result = TriQuadPairer.Pair(vertices, faces, new HashSet<long>(), frozen, new TriQuadPairer.Options());

        Assert.Equal(1, result.QuadCount);
        Assert.Empty(result.Tris);
    }
}
