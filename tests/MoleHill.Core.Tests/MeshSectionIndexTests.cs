using System.Diagnostics;
using MoleHill.Core.Analysis;
using MoleHill.Core.Engine;
using Xunit;
using Xunit.Abstractions;

namespace MoleHill.Core.Tests;

public class MeshSectionIndexTests
{
    private readonly ITestOutputHelper _output;

    public MeshSectionIndexTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static double Height(double x, double y) => (0.3 * x) - (0.2 * y) + 5.0;

    /// <summary>A regular n×n grid of unit squares, two triangles each, on the plane <see cref="Height"/>.</summary>
    private static IndexedTriMesh Grid(int n, bool splitSeamAtMiddle = false)
    {
        var vertices = new List<double>();
        for (int y = 0; y <= n; y++)
        {
            for (int x = 0; x <= n; x++)
            {
                vertices.Add(x);
                vertices.Add(y);
                vertices.Add(Height(x, y));
            }
        }

        int VertexAt(int x, int y) => (y * (n + 1)) + x;

        // A copy of the middle column, used by faces right of it: an unwelded seam, as a shading split leaves.
        int seamColumn = n / 2;
        var seamCopy = new int[n + 1];
        if (splitSeamAtMiddle)
        {
            for (int y = 0; y <= n; y++)
            {
                seamCopy[y] = vertices.Count / 3;
                vertices.Add(seamColumn);
                vertices.Add(y);
                vertices.Add(Height(seamColumn, y));
            }
        }

        int Corner(int x, int y, bool rightOfSeam) =>
            splitSeamAtMiddle && rightOfSeam && x == seamColumn ? seamCopy[y] : VertexAt(x, y);

        var faces = new List<int>();
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                bool right = x >= seamColumn;
                int a = Corner(x, y, right), b = Corner(x + 1, y, right);
                int c = Corner(x + 1, y + 1, right), d = Corner(x, y + 1, right);
                faces.AddRange(new[] { a, b, c, a, c, d });
            }
        }

        return IndexedTriMesh.FromArrays(vertices.ToArray(), faces.ToArray());
    }

    private static void AssertOnSurfaceAndAscending(double[] chain, double ax, double ay, double ux, double uy)
    {
        double previous = double.NegativeInfinity;
        for (int i = 0; i < chain.Length; i += 3)
        {
            Assert.Equal(Height(chain[i], chain[i + 1]), chain[i + 2], 9);
            double station = ((chain[i] - ax) * ux) + ((chain[i + 1] - ay) * uy);
            Assert.True(station >= previous - 1e-12, "stations must ascend along a chain");
            previous = station;
        }
    }

    [Fact]
    public void SliceSegment_CutExactlyThroughVertexRow_ReturnsOneContinuousChain()
    {
        var index = new MeshSectionIndex(Grid(10));

        // y = 4 is a row of vertices and a row of edges: the case Rhino's MeshPlane broke into pieces.
        List<double[]> chains = index.SliceSegment(0.0, 4.0, 10.0, 4.0, 1e-6);

        double[] chain = Assert.Single(chains);
        Assert.Equal(0.0, chain[0], 9);
        Assert.Equal(10.0, chain[^3], 9);
        AssertOnSurfaceAndAscending(chain, 0.0, 4.0, 1.0, 0.0);
    }

    [Fact]
    public void SliceSegment_CutAlongDiagonalThroughEveryVertex_ReturnsOneContinuousChain()
    {
        var index = new MeshSectionIndex(Grid(8));

        List<double[]> chains = index.SliceSegment(0.0, 0.0, 8.0, 8.0, 1e-6);

        double[] chain = Assert.Single(chains);
        double s = Math.Sqrt(0.5);
        AssertOnSurfaceAndAscending(chain, 0.0, 0.0, s, s);
        Assert.Equal(0.0, chain[0], 9);
        Assert.Equal(8.0, chain[^3], 9);
    }

    [Fact]
    public void SliceSegment_ObliqueCut_FollowsSurfaceInSegmentDirection()
    {
        var index = new MeshSectionIndex(Grid(12));

        // Drawn right to left, so the chain must come back in that direction.
        List<double[]> chains = index.SliceSegment(11.3, 2.7, 0.9, 9.1, 1e-6);

        double[] chain = Assert.Single(chains);
        double dx = 0.9 - 11.3, dy = 9.1 - 2.7, length = Math.Sqrt((dx * dx) + (dy * dy));
        AssertOnSurfaceAndAscending(chain, 11.3, 2.7, dx / length, dy / length);
        Assert.True(chain[0] > chain[^3], "the chain runs from the segment's start towards its end");
    }

    [Fact]
    public void SliceSegment_UnweldedSeam_StillChainsAcross()
    {
        var index = new MeshSectionIndex(Grid(10, splitSeamAtMiddle: true));

        List<double[]> chains = index.SliceSegment(0.0, 3.5, 10.0, 3.5, 1e-6);

        double[] chain = Assert.Single(chains);
        Assert.Equal(0.0, chain[0], 9);
        Assert.Equal(10.0, chain[^3], 9);
    }

    [Fact]
    public void SliceSegment_ShortSegmentInsideLargeMesh_CoversTheSegment()
    {
        var index = new MeshSectionIndex(Grid(100));

        List<double[]> chains = index.SliceSegment(40.25, 50.5, 43.75, 50.5, 1e-6);

        double[] chain = Assert.Single(chains);
        Assert.True(chain[0] <= 40.25 && chain[^3] >= 43.75, "the chain must span the whole segment");

        // Only faces near the segment are visited, so the chain stops within a cell or so of each end.
        Assert.True(chain[0] > 30.0 && chain[^3] < 54.0);
    }

    [Fact]
    public void SliceSegment_OutsideMesh_ReturnsNothing()
    {
        var index = new MeshSectionIndex(Grid(10));

        Assert.Empty(index.SliceSegment(20.0, 20.0, 30.0, 25.0, 1e-6));
    }

    [Fact]
    public void SliceSegment_MeshWithHole_ReturnsOneChainPerSide()
    {
        IndexedTriMesh grid = Grid(10);
        var faces = new List<int>();
        for (int f = 0; f < grid.FaceCount; f++)
        {
            // Drop the faces of squares x in [4, 6), y in [4, 6).
            int square = f / 2;
            int x = square % 10, y = square / 10;
            if (x is >= 4 and < 6 && y is >= 4 and < 6)
                continue;
            faces.AddRange(new[] { grid.Faces[f * 3], grid.Faces[(f * 3) + 1], grid.Faces[(f * 3) + 2] });
        }

        var index = new MeshSectionIndex(IndexedTriMesh.FromArrays(grid.Vertices, faces.ToArray()));

        List<double[]> chains = index.SliceSegment(0.0, 5.5, 10.0, 5.5, 1e-6);

        Assert.Equal(2, chains.Count);
        Assert.Contains(chains, chain => Math.Abs(chain[^3] - 4.0) < 1e-9);
        Assert.Contains(chains, chain => Math.Abs(chain[0] - 6.0) < 1e-9);
    }

    [Fact]
    public void TryGetTopZ_OnSurface_InterpolatesExactly()
    {
        var index = new MeshSectionIndex(Grid(10));

        Assert.True(index.TryGetTopZ(3.3, 7.1, out double z));
        Assert.Equal(Height(3.3, 7.1), z, 9);

        // On a vertex and on an edge, where several faces claim the point.
        Assert.True(index.TryGetTopZ(5.0, 5.0, out z));
        Assert.Equal(Height(5.0, 5.0), z, 9);
        Assert.True(index.TryGetTopZ(10.0, 2.5, out z));
        Assert.Equal(Height(10.0, 2.5), z, 9);
    }

    [Fact]
    public void TryGetTopZ_OutsideMesh_ReturnsFalse()
    {
        var index = new MeshSectionIndex(Grid(10));

        Assert.False(index.TryGetTopZ(-1.0, 5.0, out _));
        Assert.False(index.TryGetTopZ(5.0, 10.5, out _));
    }

    [Fact]
    public void TryGetTopZ_StackedFaces_ReturnsTheUpperOne()
    {
        double[] vertices =
        {
            0, 0, 0, 1, 0, 0, 0, 1, 0,
            0, 0, 10, 1, 0, 10, 0, 1, 10
        };
        int[] faces = { 0, 1, 2, 3, 4, 5 };
        var index = new MeshSectionIndex(IndexedTriMesh.FromArrays(vertices, faces));

        Assert.True(index.TryGetTopZ(0.25, 0.25, out double z));
        Assert.Equal(10.0, z, 12);
    }

    [Fact]
    public void SliceSegment_ManyCrossSectionsOnLargeTerrain_StaysFast()
    {
        // 500 x 500 squares = 500k faces, cut by 400 cross-sections 30 units wide: the shape of a long
        // alignment with dense stations on a park-scale terrain.
        var build = Stopwatch.StartNew();
        var index = new MeshSectionIndex(Grid(500));
        build.Stop();

        var query = Stopwatch.StartNew();
        int chains = 0;
        for (int i = 0; i < 400; i++)
        {
            double y = 50.0 + i + 0.37;
            chains += index.SliceSegment(235.1, y, 265.1, y + 3.0, 1e-6).Count;
        }

        query.Stop();
        _output.WriteLine($"index build {build.ElapsedMilliseconds} ms; 400 cuts {query.ElapsedMilliseconds} ms");
        Assert.Equal(400, chains);
        Assert.True(query.ElapsedMilliseconds < 2_000, $"400 cuts took {query.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void TryGetTopZ_BeyondTheColumnCap_StillFindsTheSurface()
    {
        // 1.28M tiny faces near the origin plus one long sloped triangle reaching 1,000 km east: enough faces
        // for small cells across a long plan, so the grid hits its 16,384-column cap. Points in the capped
        // far end must still be found.
        const int n = 800;
        var vertices = new List<double>();
        var faces = new List<int>();
        for (int y = 0; y <= n; y++)
        {
            for (int x = 0; x <= n; x++)
            {
                vertices.Add(x / (double)n);
                vertices.Add(y / (double)n);
                vertices.Add(0.0);
            }
        }

        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                int a = (y * (n + 1)) + x;
                faces.AddRange(new[] { a, a + 1, a + n + 2, a, a + n + 2, a + n + 1 });
            }
        }

        int far = vertices.Count / 3;
        vertices.AddRange(new[] { 0.0, 0.0, 0.0, 1_000_000.0, 0.0, 100.0, 1_000_000.0, 1_000.0, 100.0 });
        faces.AddRange(new[] { far, far + 1, far + 2 });
        var index = new MeshSectionIndex(IndexedTriMesh.FromArrays(vertices.ToArray(), faces.ToArray()), weldCoincidentVertices: false);

        Assert.True(index.TryGetTopZ(999_000.0, 10.0, out double z));
        Assert.Equal(99.9, z, 6);
        Assert.False(index.TryGetTopZ(1_000_001.0, 10.0, out _));
    }
}
