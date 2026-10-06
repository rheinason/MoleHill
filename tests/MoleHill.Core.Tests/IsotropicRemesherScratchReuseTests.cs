using MoleHill.Core.Engine;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// Guards the remesh phases that now reuse scratch across sweeps and rounds: the flip sweep's edge
/// incidence / touched / created-edge structures, and the collapse phase's candidate list and one-ring
/// locks. Stale state leaking from one sweep or round into the next would show up as a changed result,
/// a lost feature, or a broken mesh — so these run workloads that need many sweeps and rounds and pin
/// determinism plus the structural invariants.
/// </summary>
public class IsotropicRemesherScratchReuseTests
{
    private static readonly IReadOnlyList<ConstraintPolyline> NoConstraints =
        Array.Empty<ConstraintPolyline>();

    [Fact]
    public void Remesh_RepeatedRuns_ProduceIdenticalGeometry()
    {
        (double[] vertices, int[] faces) = BuildIrregularGrid();

        IsotropicRemesher.Result first = Run(vertices, faces, targetEdgeLength: 3.0, iterations: 4);
        IsotropicRemesher.Result second = Run(vertices, faces, targetEdgeLength: 3.0, iterations: 4);

        Assert.True(first.Success, first.Warning);
        Assert.True(second.Success, second.Warning);
        Assert.Equal(first.Vertices, second.Vertices);
        Assert.Equal(first.Faces, second.Faces);
        Assert.Equal(first.Splits, second.Splits);
        Assert.Equal(first.Collapses, second.Collapses);
        Assert.Equal(first.Flips, second.Flips);
    }

    [Fact]
    public void Remesh_ManyCollapseRounds_LeavesNoStaleLockAndStaysValid()
    {
        // A far coarser target than the input forces the collapse phase through several rounds; a lock
        // surviving from an earlier round would silently veto later collapses.
        (double[] vertices, int[] faces) = BuildIrregularGrid();
        int inputFaceCount = faces.Length / 3;

        IsotropicRemesher.Result result = Run(vertices, faces, targetEdgeLength: 12.0, iterations: 4);

        Assert.True(result.Success, result.Warning);
        Assert.True(result.Collapses > 0);
        Assert.True(result.Faces.Length / 3 < inputFaceCount);
        AssertValidMesh(result);
    }

    [Fact]
    public void Remesh_ManyFlipSweeps_KeepsEveryEdgeManifold()
    {
        // A dense target drives the split and flip phases hard. A duplicated diagonal from a stale
        // created-edge set, or a stale touched flag, would show as a non-manifold or repeated edge.
        (double[] vertices, int[] faces) = BuildIrregularGrid();

        IsotropicRemesher.Result result = Run(vertices, faces, targetEdgeLength: 2.0, iterations: 4);

        Assert.True(result.Success, result.Warning);
        Assert.True(result.Flips > 0);
        AssertValidMesh(result);
    }

    [Fact]
    public void Remesh_BoundaryPolygonSurvivesTheMergedFeatureSetup()
    {
        // Boundary edges are now read off the single shared edge-incidence pass. If that reading were
        // wrong the perimeter would no longer be pinned and the outline would shrink inward.
        (double[] vertices, int[] faces) = BuildIrregularGrid();

        IsotropicRemesher.Result result = Run(vertices, faces, targetEdgeLength: 6.0, iterations: 4);

        Assert.True(result.Success, result.Warning);
        (double minX, double maxX, double minY, double maxY) = Extents(result.Vertices);
        Assert.Equal(0.0, minX, 6);
        Assert.Equal(60.0, maxX, 6);
        Assert.Equal(0.0, minY, 6);
        Assert.Equal(40.0, maxY, 6);

        foreach (double corner in new[] { 0.0, 60.0 })
            Assert.True(HasVertexAt(result.Vertices, corner, 0.0));
    }

    private static IsotropicRemesher.Result Run(double[] vertices, int[] faces, double targetEdgeLength, int iterations)
    {
        return IsotropicRemesher.Remesh(
            (double[])vertices.Clone(),
            (int[])faces.Clone(),
            NoConstraints,
            new IsotropicRemesher.Options
            {
                TargetEdgeLength = targetEdgeLength,
                CreaseAngleDeg = 30,
                Tolerance = 0.001,
                Iterations = iterations
            });
    }

    private static void AssertValidMesh(IsotropicRemesher.Result result)
    {
        int vertexCount = result.Vertices.Length / 3;
        int faceCount = result.Faces.Length / 3;
        Assert.True(faceCount > 0);

        var incidence = new Dictionary<long, int>();
        for (int f = 0; f < faceCount; f++)
        {
            int a = result.Faces[f * 3];
            int b = result.Faces[(f * 3) + 1];
            int c = result.Faces[(f * 3) + 2];
            Assert.InRange(a, 0, vertexCount - 1);
            Assert.InRange(b, 0, vertexCount - 1);
            Assert.InRange(c, 0, vertexCount - 1);
            Assert.True(a != b && b != c && c != a, $"Face {f} is degenerate.");

            Count(incidence, a, b);
            Count(incidence, b, c);
            Count(incidence, c, a);
        }

        foreach ((long key, int count) in incidence)
            Assert.True(count <= 2, $"Edge {key} is used by {count} faces.");
    }

    private static void Count(Dictionary<long, int> incidence, int a, int b)
    {
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        incidence[key] = incidence.TryGetValue(key, out int count) ? count + 1 : 1;
    }

    private static (double MinX, double MaxX, double MinY, double MaxY) Extents(double[] vertices)
    {
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        for (int i = 0; i < vertices.Length / 3; i++)
        {
            minX = Math.Min(minX, vertices[i * 3]);
            maxX = Math.Max(maxX, vertices[i * 3]);
            minY = Math.Min(minY, vertices[(i * 3) + 1]);
            maxY = Math.Max(maxY, vertices[(i * 3) + 1]);
        }

        return (minX, maxX, minY, maxY);
    }

    private static bool HasVertexAt(double[] vertices, double x, double y)
    {
        for (int i = 0; i < vertices.Length / 3; i++)
        {
            if (Math.Abs(vertices[i * 3] - x) <= 1e-6 && Math.Abs(vertices[(i * 3) + 1] - y) <= 1e-6)
                return true;
        }

        return false;
    }

    /// <summary>A 60x40 sheet with deliberately uneven spacing, so both collapse and split have work.</summary>
    private static (double[] Vertices, int[] Faces) BuildIrregularGrid()
    {
        double[] xs = Coordinates(0.0, 60.0, coarse: 5.0, fineFrom: 20.0, fineTo: 34.0, fine: 1.5);
        double[] ys = Coordinates(0.0, 40.0, coarse: 5.0, fineFrom: 12.0, fineTo: 24.0, fine: 1.5);

        int nx = xs.Length;
        int ny = ys.Length;
        var vertices = new double[nx * ny * 3];
        for (int j = 0; j < ny; j++)
        {
            for (int i = 0; i < nx; i++)
            {
                int v = (j * nx) + i;
                vertices[v * 3] = xs[i];
                vertices[(v * 3) + 1] = ys[j];
                vertices[(v * 3) + 2] = (Math.Sin(xs[i] * 0.08) * 2.0) + (Math.Cos(ys[j] * 0.11) * 1.5);
            }
        }

        var faces = new int[(nx - 1) * (ny - 1) * 6];
        int f = 0;
        for (int j = 0; j < ny - 1; j++)
        {
            for (int i = 0; i < nx - 1; i++)
            {
                int v00 = (j * nx) + i;
                int v10 = v00 + 1;
                int v01 = v00 + nx;
                int v11 = v01 + 1;
                faces[f++] = v00; faces[f++] = v10; faces[f++] = v11;
                faces[f++] = v00; faces[f++] = v11; faces[f++] = v01;
            }
        }

        return (vertices, faces);
    }

    private static double[] Coordinates(double from, double to, double coarse, double fineFrom, double fineTo, double fine)
    {
        var values = new List<double>();
        for (double value = from; value < fineFrom - 1e-9; value += coarse)
            values.Add(value);
        for (double value = fineFrom; value < fineTo - 1e-9; value += fine)
            values.Add(value);
        for (double value = fineTo; value < to - 1e-9; value += coarse)
            values.Add(value);
        values.Add(to);
        return values.ToArray();
    }
}
