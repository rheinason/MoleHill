using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using MoleHill.Core.Processing;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class TerrainDetailInserterTests
{
    private const double Tolerance = 1e-4;
    private const double MergeTolerance = 0.05;
    private const double WallSlope = 70.0;

    [Fact]
    public void TryInsert_PointInsideFace_TakesItsElevationAndDelaunayNeighbours()
    {
        // One square split along its diagonal. A point just off the diagonal lies inside the far
        // triangle's circumcircle, so Delaunay insertion connects it to all four corners.
        double[] vertices = [0, 0, 0, 10, 0, 0, 10, 10, 0, 0, 10, 0];
        int[] faces = [0, 1, 2, 0, 2, 3];

        TerrainDetailInserter.Result result = Insert(vertices, faces, point: [5.5, 4.5, 3.0]);

        int inserted = FindVertex(result, 5.5, 4.5);
        Assert.Equal(3.0, result.Vertices[(inserted * 3) + 2], 12);
        Assert.Equal(4, Neighbours(result, inserted).Count);
        Assert.True(result.Flips > 0);
        AssertWatertight(vertices, faces, result);
    }

    [Fact]
    public void TryInsert_TerrainWithNarrowCliff_KeepsEveryCliffVertexAndFace()
    {
        // M15: a cliff whose top and toe are 0.01 apart in plan — narrower than the merge tolerance — and
        // on no constraint. A rebuild from the mesh's own vertices merges them by XY alone.
        Cliff(out double[] vertices, out int[] faces);
        var merged = PointCloudProcessor.Merge(vertices, vertices.Length / 3, BreaklineDiscretizer.Process([]), MergeTolerance);
        Assert.True(merged.VertexCount < vertices.Length / 3, "The rebuild path is expected to collapse the cliff.");

        TerrainDetailInserter.Result result = Insert(vertices, faces, point: [25.0, 5.0, 1.0]);

        for (int v = 0; v < vertices.Length / 3; v++)
        {
            Assert.Equal(vertices[v * 3], result.Vertices[v * 3]);
            Assert.Equal(vertices[(v * 3) + 1], result.Vertices[(v * 3) + 1]);
            Assert.Equal(vertices[(v * 3) + 2], result.Vertices[(v * 3) + 2]);
        }

        var steepBefore = SteepFaceSet(vertices, faces);
        var steepAfter = SteepFaceSet(result.Vertices, result.Faces);
        Assert.NotEmpty(steepBefore);
        Assert.Superset(steepBefore, steepAfter);
        AssertWatertight(vertices, faces, result);
    }

    [Fact]
    public void TryInsert_BreaklineAcrossTerrain_LiftsItsVerticesToTheBreakline()
    {
        Grid(4, 10.0, out double[] vertices, out int[] faces);
        var breakline = new SurfaceRemesher.ConstraintPolyline([3, 5, 7, 37, 25, 9], 2, IsClosed: false, PreserveInputElevation: true);

        TerrainDetailInserter.Result result = Insert(vertices, faces, constraints: [breakline]);

        int checkedVertices = 0;
        for (int v = 0; v < result.VertexCount; v++)
        {
            double x = result.Vertices[v * 3], y = result.Vertices[(v * 3) + 1];
            double t = (x - 3.0) / 34.0;
            if (t < 0 || t > 1 || Math.Abs(y - (5.0 + (t * 20.0))) > 1e-6)
                continue;
            Assert.Equal(7.0 + (t * 2.0), result.Vertices[(v * 3) + 2], 6);
            checkedVertices++;
        }

        Assert.True(checkedVertices >= 4);
        AssertWatertight(vertices, faces, result);
    }

    [Fact]
    public void TryInsert_BreaklineOverWallFace_LeavesTheWallElevations()
    {
        Cliff(out double[] vertices, out int[] faces);
        // Crosses the cliff band (x = 20 .. 20.01) at an elevation matching neither top nor toe.
        var breakline = new SurfaceRemesher.ConstraintPolyline([15, 5, 2.5, 25, 5, 2.5], 2, IsClosed: false, PreserveInputElevation: true);

        TerrainDetailInserter.Result result = Insert(vertices, faces, constraints: [breakline]);

        Assert.True(result.VerticesHeldByWalls > 0);
        for (int v = 0; v < result.VertexCount; v++)
        {
            double x = result.Vertices[v * 3];
            if (x > 20.0 - 1e-9 && x < 20.01 + 1e-9)
                Assert.NotEqual(2.5, result.Vertices[(v * 3) + 2]);
        }

        AssertWatertight(vertices, faces, result);
    }

    [Fact]
    public void TryInsert_PointOnExistingVertexOrOutside_IsNotInserted()
    {
        Grid(2, 10.0, out double[] vertices, out int[] faces);

        TerrainDetailInserter.Result result = Insert(vertices, faces, point: [10.01, 10.0, 99.0, 500.0, 500.0, 1.0]);

        Assert.Equal(0, result.PointsInserted);
        Assert.Equal(1, result.PointsOnExistingVertices);
        Assert.Equal(1, result.PointsOutsideTerrain);
        Assert.Equal(vertices, result.Vertices);
        Assert.Equal(faces, result.Faces);
    }

    private static TerrainDetailInserter.Result Insert(
        double[] vertices,
        int[] faces,
        double[]? point = null,
        SurfaceRemesher.ConstraintPolyline[]? constraints = null)
    {
        bool ok = TerrainDetailInserter.TryInsert(
            vertices, vertices.Length / 3, faces, faces.Length / 3,
            point ?? [], constraints ?? [], [], [],
            Tolerance, MergeTolerance, WallSlope,
            out TerrainDetailInserter.Result? result, out string? error);
        Assert.True(ok, error);
        return result!;
    }

    /// <summary>A 40 x 10 strip, flat at z = 0 left of x = 20 and z = 5 right of x = 20.01.</summary>
    private static void Cliff(out double[] vertices, out int[] faces)
    {
        double[] xs = [0, 10, 20, 20.01, 30, 40];
        double[] zs = [0, 0, 0, 5, 5, 5];
        var v = new List<double>();
        foreach (double y in new[] { 0.0, 10.0 })
            for (int i = 0; i < xs.Length; i++)
                v.AddRange([xs[i], y, zs[i]]);
        vertices = v.ToArray();

        var f = new List<int>();
        int n = xs.Length;
        for (int i = 0; i < n - 1; i++)
            f.AddRange([i, i + 1, n + i + 1, i, n + i + 1, n + i]);
        faces = f.ToArray();
    }

    private static void Grid(int cells, double size, out double[] vertices, out int[] faces)
    {
        var v = new List<double>();
        for (int j = 0; j <= cells; j++)
            for (int i = 0; i <= cells; i++)
                v.AddRange([i * size, j * size, (i + j) * 0.25]);
        vertices = v.ToArray();

        var f = new List<int>();
        int row = cells + 1;
        for (int j = 0; j < cells; j++)
            for (int i = 0; i < cells; i++)
            {
                int a = (j * row) + i;
                f.AddRange([a, a + 1, a + row + 1, a, a + row + 1, a + row]);
            }
        faces = f.ToArray();
    }

    private static int FindVertex(TerrainDetailInserter.Result result, double x, double y)
    {
        for (int v = 0; v < result.VertexCount; v++)
            if (Math.Abs(result.Vertices[v * 3] - x) < 1e-9 && Math.Abs(result.Vertices[(v * 3) + 1] - y) < 1e-9)
                return v;
        throw new Xunit.Sdk.XunitException($"No vertex at {x}, {y}.");
    }

    private static HashSet<int> Neighbours(TerrainDetailInserter.Result result, int vertex)
    {
        var set = new HashSet<int>();
        for (int f = 0; f < result.FaceCount; f++)
        {
            int a = result.Faces[f * 3], b = result.Faces[(f * 3) + 1], c = result.Faces[(f * 3) + 2];
            if (a != vertex && b != vertex && c != vertex)
                continue;
            set.UnionWith([a, b, c]);
        }

        set.Remove(vertex);
        return set;
    }

    private static HashSet<(double, double, double, double, double, double)> SteepFaceSet(double[] vertices, int[] faces)
    {
        var set = new HashSet<(double, double, double, double, double, double)>();
        bool[] steep = FeaturePolylineGraph.BuildFrozenFaceMask(vertices, faces, faces.Length / 3, WallSlope);
        for (int f = 0; f < steep.Length; f++)
        {
            if (!steep[f])
                continue;
            var corners = new[] { faces[f * 3], faces[(f * 3) + 1], faces[(f * 3) + 2] }
                .Select(i => (vertices[i * 3], vertices[(i * 3) + 1]))
                .OrderBy(p => p.Item1).ThenBy(p => p.Item2).ToArray();
            set.Add((corners[0].Item1, corners[0].Item2, corners[1].Item1, corners[1].Item2, corners[2].Item1, corners[2].Item2));
        }

        return set;
    }

    /// <summary>
    /// No edge used more than twice, no single-use edge inside the terrain (the perimeter length is
    /// unchanged), plan area unchanged, and every face keeps a consistent winding.
    /// </summary>
    private static void AssertWatertight(double[] inputVertices, int[] inputFaces, TerrainDetailInserter.Result result)
    {
        var uses = new Dictionary<long, int>(IndexedMeshTools.EdgeKeyComparer.Instance);
        double perimeter = 0.0, area = 0.0;
        for (int f = 0; f < result.FaceCount; f++)
        {
            int a = result.Faces[f * 3], b = result.Faces[(f * 3) + 1], c = result.Faces[(f * 3) + 2];
            double cross = ((result.Vertices[b * 3] - result.Vertices[a * 3]) * (result.Vertices[(c * 3) + 1] - result.Vertices[(a * 3) + 1])) -
                           ((result.Vertices[(b * 3) + 1] - result.Vertices[(a * 3) + 1]) * (result.Vertices[c * 3] - result.Vertices[a * 3]));
            Assert.True(cross > 0, $"Face {f} is degenerate or inverted.");
            area += cross * 0.5;
            foreach ((int p, int q) in new[] { (a, b), (b, c), (c, a) })
            {
                long key = IndexedMeshTools.GetEdgeKey(p, q);
                uses[key] = uses.GetValueOrDefault(key) + 1;
            }
        }

        foreach (var (key, count) in uses)
        {
            Assert.True(count <= 2, "An edge is used more than twice.");
            if (count == 1)
            {
                int p = (int)(key >> 32), q = (int)key;
                perimeter += Math.Sqrt(Math.Pow(result.Vertices[p * 3] - result.Vertices[q * 3], 2) +
                                       Math.Pow(result.Vertices[(p * 3) + 1] - result.Vertices[(q * 3) + 1], 2));
            }
        }

        double inputArea = 0.0, inputPerimeter = 0.0;
        var inputUses = new Dictionary<long, int>(IndexedMeshTools.EdgeKeyComparer.Instance);
        for (int f = 0; f < inputFaces.Length / 3; f++)
        {
            int a = inputFaces[f * 3], b = inputFaces[(f * 3) + 1], c = inputFaces[(f * 3) + 2];
            inputArea += (((inputVertices[b * 3] - inputVertices[a * 3]) * (inputVertices[(c * 3) + 1] - inputVertices[(a * 3) + 1])) -
                          ((inputVertices[(b * 3) + 1] - inputVertices[(a * 3) + 1]) * (inputVertices[c * 3] - inputVertices[a * 3]))) * 0.5;
            foreach ((int p, int q) in new[] { (a, b), (b, c), (c, a) })
            {
                long key = IndexedMeshTools.GetEdgeKey(p, q);
                inputUses[key] = inputUses.GetValueOrDefault(key) + 1;
            }
        }

        foreach (var (key, count) in inputUses)
        {
            if (count != 1)
                continue;
            int p = (int)(key >> 32), q = (int)key;
            inputPerimeter += Math.Sqrt(Math.Pow(inputVertices[p * 3] - inputVertices[q * 3], 2) +
                                        Math.Pow(inputVertices[(p * 3) + 1] - inputVertices[(q * 3) + 1], 2));
        }

        Assert.Equal(inputArea, area, 6);
        Assert.Equal(inputPerimeter, perimeter, 6);
    }
}
