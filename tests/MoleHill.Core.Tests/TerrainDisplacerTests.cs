using MoleHill.Core.Displacement;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// Displace on the mesh: only Z moves, the boundary is where the edit stops and the feather is how it
/// fades in from it, held lines, walls and the border keep their height exactly, and the reported sampling
/// is the edge length the pattern is actually read at.
/// </summary>
public class TerrainDisplacerTests
{
    private const double Tolerance = 1e-6;

    /// <summary>Lifts everything by the same amount, so a vertex's offset is its weight times that.</summary>
    private sealed class ConstantSource : IDisplacementSource
    {
        public ConstantSource(double offset) => Value = offset;

        public double Value { get; }

        public double DetailSize => 1.0;

        public double Offset(double x, double y) => Value;
    }

    /// <summary>A (2n+1)² grid of spacing h over [-n h, n h]², heights from <paramref name="z"/>.</summary>
    private static IndexedTriMesh Grid(int n, double h, Func<double, double, double>? z = null)
    {
        int side = (2 * n) + 1;
        var vertices = new double[side * side * 3];
        for (int j = 0; j < side; j++)
        {
            for (int i = 0; i < side; i++)
            {
                int v = (j * side) + i;
                double x = (i - n) * h, y = (j - n) * h;
                vertices[v * 3] = x;
                vertices[v * 3 + 1] = y;
                vertices[v * 3 + 2] = z?.Invoke(x, y) ?? 0.0;
            }
        }

        var faces = new List<int>();
        for (int j = 0; j < side - 1; j++)
        {
            for (int i = 0; i < side - 1; i++)
            {
                int a = (j * side) + i, b = a + 1, c = a + side, d = c + 1;
                faces.AddRange(new[] { a, b, d, a, d, c });
            }
        }

        return IndexedTriMesh.FromArrays(vertices, faces.ToArray());
    }

    private static IEnumerable<(double X, double Y, double Before, double After)> Vertices(IndexedTriMesh mesh, double[] after)
    {
        for (int v = 0; v < mesh.VertexCount; v++)
            yield return (mesh.Vertices[v * 3], mesh.Vertices[v * 3 + 1], mesh.Vertices[v * 3 + 2], after[v * 3 + 2]);
    }

    private static double Smoothstep(double u)
    {
        u = Math.Clamp(u, 0.0, 1.0);
        return u * u * (3.0 - (2.0 * u));
    }

    [Fact]
    public void Apply_MovesOnlyHeights_AndKeepsTheInputUntouched()
    {
        IndexedTriMesh mesh = Grid(6, 1.0, (x, y) => 0.1 * x);
        double[] before = (double[])mesh.Vertices.Clone();
        var source = new NoiseDisplacementSource(3.0, 1.0, 3, 0.5, 4, new DisplacementPlacement(0, 0, 0));

        TerrainDisplacer.Result result = TerrainDisplacer.Apply(new TerrainDisplacer.Request
        {
            Mesh = mesh,
            Source = source,
            Tolerance = Tolerance
        });

        Assert.Equal(before, mesh.Vertices);
        Assert.Equal(mesh.VertexCount * 3, result.Vertices.Length);
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            Assert.Equal(before[v * 3], result.Vertices[v * 3]);
            Assert.Equal(before[v * 3 + 1], result.Vertices[v * 3 + 1]);
        }

        Assert.True(result.MovedVertexCount > 0);
        Assert.InRange(result.MinOffset, -0.5, 0.0);
        Assert.InRange(result.MaxOffset, 0.0, 0.5);
    }

    [Fact]
    public void Apply_HoldsTheBorder_UnlessAskedNotTo()
    {
        IndexedTriMesh mesh = Grid(4, 1.0);
        var source = new ConstantSource(2.0);

        TerrainDisplacer.Result held = TerrainDisplacer.Apply(new TerrainDisplacer.Request { Mesh = mesh, Source = source, Tolerance = Tolerance });
        TerrainDisplacer.Result free = TerrainDisplacer.Apply(new TerrainDisplacer.Request { Mesh = mesh, Source = source, HoldBorder = false, Tolerance = Tolerance });

        foreach ((double x, double y, double _, double after) in Vertices(mesh, held.Vertices))
        {
            bool onBorder = Math.Abs(x) == 4.0 || Math.Abs(y) == 4.0;
            Assert.Equal(onBorder ? 0.0 : 2.0, after, 12);
        }

        Assert.All(Vertices(mesh, free.Vertices), item => Assert.Equal(2.0, item.After, 12));
    }

    [Fact]
    public void Apply_Boundary_StopsAtTheDrawnLine_AndFeathersInsideIt()
    {
        IndexedTriMesh mesh = Grid(10, 0.5);
        double feather = 2.0;
        var square = (new[] { -3.0, -3.0, 3.0, -3.0, 3.0, 3.0, -3.0, 3.0 }, 4);

        TerrainDisplacer.Result result = TerrainDisplacer.Apply(new TerrainDisplacer.Request
        {
            Mesh = mesh,
            Source = new ConstantSource(1.0),
            Boundaries = new[] { square },
            HoldBorder = false,
            Feather = feather,
            Tolerance = Tolerance
        });

        foreach ((double x, double y, double _, double after) in Vertices(mesh, result.Vertices))
        {
            bool inside = Math.Abs(x) < 3.0 && Math.Abs(y) < 3.0;
            double inward = 3.0 - Math.Max(Math.Abs(x), Math.Abs(y));
            double expected = inside ? Smoothstep(inward / feather) : 0.0;
            Assert.Equal(expected, after, 9);
        }

        // The profile across the edge: zero at the line, half at half the feather, full beyond it.
        Assert.Equal(0.5, Height(mesh, result, -2.0, 0.0), 9);
        Assert.Equal(1.0, Height(mesh, result, 0.0, 0.0), 9);
        Assert.Equal(0.0, Height(mesh, result, -3.0, 0.0), 9);
        Assert.Equal(0.0, Height(mesh, result, -4.0, 0.0), 9);
    }

    [Fact]
    public void Apply_HoldLine_KeepsItsVerticesExactly_AndFadesAroundThem()
    {
        IndexedTriMesh mesh = Grid(8, 0.5, (x, y) => 3.0 + (0.2 * y));
        double feather = 2.0;
        var line = new MeshSmoother.BreaklinePolyline(new[] { -4.0, 0.0, 4.0, 0.0 }, 2, IsClosed: false);

        TerrainDisplacer.Result result = TerrainDisplacer.Apply(new TerrainDisplacer.Request
        {
            Mesh = mesh,
            Source = new ConstantSource(1.0),
            HoldLines = new[] { line },
            HoldBorder = false,
            Feather = feather,
            Tolerance = Tolerance
        });

        foreach ((double x, double y, double before, double after) in Vertices(mesh, result.Vertices))
        {
            if (y == 0.0)
                Assert.Equal(before, after);
            else
                Assert.Equal(before + Smoothstep(Math.Abs(y) / feather), after, 9);
        }
    }

    [Fact]
    public void Apply_WallFaces_NeverMove()
    {
        // A step of 3 over one 0.5 cell is an 80 degree face: a wall. Its top and foot keep their heights.
        IndexedTriMesh mesh = Grid(6, 0.5, (x, y) => x > 0.25 ? 3.0 : 0.0);
        TerrainDisplacer.Result result = TerrainDisplacer.Apply(new TerrainDisplacer.Request
        {
            Mesh = mesh,
            Source = new ConstantSource(1.0),
            HoldBorder = false,
            Tolerance = Tolerance
        });

        foreach ((double x, double _, double before, double after) in Vertices(mesh, result.Vertices))
        {
            bool onWall = x == 0.0 || x == 0.5;
            Assert.Equal(onWall ? before : before + 1.0, after, 12);
        }
    }

    [Fact]
    public void Apply_ReportsTheMedianEdgeLengthItSampledAt()
    {
        IndexedTriMesh mesh = Grid(10, 0.5);
        TerrainDisplacer.Result result = TerrainDisplacer.Apply(new TerrainDisplacer.Request
        {
            Mesh = mesh,
            Source = new ConstantSource(1.0),
            Tolerance = Tolerance
        });

        // Two of every three grid edges are axis-aligned (0.5); the third is a diagonal.
        Assert.Equal(0.5, result.MedianEdgeLength, 9);
        Assert.Equal(-4.5, result.MinX, 9);
        Assert.Equal(4.5, result.MaxY, 9);
    }

    [Fact]
    public void Apply_NothingInsideTheBoundary_MovesNothing()
    {
        IndexedTriMesh mesh = Grid(4, 1.0);
        var farAway = (new[] { 100.0, 100.0, 101.0, 100.0, 101.0, 101.0 }, 3);
        TerrainDisplacer.Result result = TerrainDisplacer.Apply(new TerrainDisplacer.Request
        {
            Mesh = mesh,
            Source = new ConstantSource(1.0),
            Boundaries = new[] { farAway },
            Tolerance = Tolerance
        });

        Assert.Equal(0, result.MovedVertexCount);
        Assert.Equal(0.0, result.MedianEdgeLength);
        Assert.Equal(mesh.Vertices, result.Vertices);
    }

    private static double Height(IndexedTriMesh mesh, TerrainDisplacer.Result result, double x, double y)
    {
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            if (Math.Abs(mesh.Vertices[v * 3] - x) < 1e-9 && Math.Abs(mesh.Vertices[v * 3 + 1] - y) < 1e-9)
                return result.Vertices[v * 3 + 2];
        }

        throw new InvalidOperationException($"No vertex at ({x}, {y}).");
    }
}
