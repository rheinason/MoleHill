using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

public class CreaseRounderTests
{
    private const double CreaseAngleDeg = 10.0;
    private const double Tolerance = 1e-6;

    /// <summary>Square grid over [-n*h, n*h]^2, every cell split along the same diagonal.</summary>
    private sealed class Grid
    {
        public required double[] Vertices { get; init; }
        public required int[] Faces { get; init; }
        public required int N { get; init; }
        public required double H { get; init; }

        public int VertexCount => Vertices.Length / 3;
        public int FaceCount => Faces.Length / 3;

        public int Index(int i, int j) => ((j + N) * ((2 * N) + 1)) + (i + N);

        public IndexedTriMesh Mesh => new(Vertices, VertexCount, Faces, FaceCount);
    }

    /// <param name="symmetric">Split each quad along the diagonal pointing away from the origin, so the
    /// triangulation itself has the square's symmetry (a uniform diagonal makes the four quadrants different TINs).</param>
    private static Grid MakeGrid(int n, double h, Func<double, double, double> z, bool symmetric = false)
    {
        int side = (2 * n) + 1;
        var vertices = new double[side * side * 3];
        for (int j = -n; j <= n; j++)
        {
            for (int i = -n; i <= n; i++)
            {
                int v = ((j + n) * side) + (i + n);
                double x = i * h;
                double y = j * h;
                vertices[v * 3] = x;
                vertices[(v * 3) + 1] = y;
                vertices[(v * 3) + 2] = z(x, y);
            }
        }

        var faces = new List<int>();
        for (int j = 0; j < side - 1; j++)
        {
            for (int i = 0; i < side - 1; i++)
            {
                int a = (j * side) + i;
                int b = a + 1;
                int c = a + side;
                int d = c + 1;
                bool alongMainDiagonal = !symmetric || ((i - n + 0.5) * (j - n + 0.5) > 0.0);
                faces.AddRange(alongMainDiagonal ? new[] { a, b, d, a, d, c } : new[] { a, b, c, b, d, c });
            }
        }

        return new Grid { Vertices = vertices, Faces = faces.ToArray(), N = n, H = h };
    }

    private static CreaseRounder.PreparedCreaseRounding Prepare(
        Grid grid,
        (double[] xyVerts, int vertCount)[]? boundaries = null,
        MeshSmoother.BreaklinePolyline[]? protect = null) =>
        CreaseRounder.Prepare(
            grid.Mesh,
            boundaries ?? Array.Empty<(double[] xyVerts, int vertCount)>(),
            protect ?? Array.Empty<MeshSmoother.BreaklinePolyline>(),
            CreaseAngleDeg,
            Tolerance);

    private static CreaseRounder.CreaseRoundingResult Round(Grid grid, double length) =>
        CreaseRounder.Round(grid.Vertices, Prepare(grid), length);

    /// <summary>A kink at x = 0: flat for x &lt; 0, then a slope of <paramref name="slope"/> (crest if negative).</summary>
    private static double Kink(double x, double slope) => x > 0.0 ? slope * x : 0.0;

    private static double Parabola(double kappa, double r, double d) =>
        d >= r ? 0.0 : kappa * (r - d) * (r - d) / (4.0 * r);

    private static double ZAt(Grid grid, double[] vertices, int i, int j) => vertices[(grid.Index(i, j) * 3) + 2];

    [Fact]
    public void Round_PlanarSlope_MovesNothing()
    {
        Grid grid = MakeGrid(100, 0.1, (x, y) => (0.3 * x) + (0.1 * y));
        var prepared = Prepare(grid);

        var result = CreaseRounder.Round(grid.Vertices, prepared, 2.0);

        Assert.Equal(0, prepared.CreaseEdgeCount);
        Assert.Equal(0, result.MovedVertexCount);
        Assert.Equal(0.0, result.MaxOffset);
        Assert.Equal(grid.Vertices, result.Vertices);
    }

    [Fact]
    public void Round_CrestProfile_MatchesParabolaAndLeavesXyUntouched()
    {
        Grid grid = MakeGrid(100, 0.1, (x, _) => Kink(x, -0.5));
        const double length = 2.0;
        double r = length / 2.0;

        var result = Round(grid, length);

        for (int v = 0; v < grid.VertexCount; v++)
        {
            Assert.Equal(grid.Vertices[v * 3], result.Vertices[v * 3]);
            Assert.Equal(grid.Vertices[(v * 3) + 1], result.Vertices[(v * 3) + 1]);
        }

        foreach (int j in new[] { 0, 30, -45 })
        {
            foreach (int i in new[] { -8, -3, 0, 4, 9 })
            {
                double x = i * grid.H;
                double expected = grid.Vertices[(grid.Index(i, j) * 3) + 2] + Parabola(-0.5, r, Math.Abs(x));
                Assert.Equal(expected, ZAt(grid, result.Vertices, i, j), 5);
            }
        }

        Assert.Equal(-0.125, ZAt(grid, result.Vertices, 0, 0) - ZAt(grid, grid.Vertices, 0, 0), 5);
        Assert.True(result.MovedVertexCount > 0);
        Assert.Equal(0.125, result.MaxOffset, 5);
    }

    [Fact]
    public void Round_ToeProfile_RaisesByTheSameMagnitude()
    {
        Grid grid = MakeGrid(100, 0.1, (x, _) => Kink(x, 0.5));
        const double length = 2.0;
        double r = length / 2.0;

        var result = Round(grid, length);

        Assert.Equal(0.125, ZAt(grid, result.Vertices, 0, 0) - ZAt(grid, grid.Vertices, 0, 0), 5);
        foreach (int i in new[] { -7, -2, 3, 8 })
        {
            double x = i * grid.H;
            double expected = grid.Vertices[(grid.Index(i, 10) * 3) + 2] + Parabola(0.5, r, Math.Abs(x));
            Assert.Equal(expected, ZAt(grid, result.Vertices, i, 10), 5);
        }
    }

    [Fact]
    public void Round_PointsFartherThanHalfLengthFromCrease_DoNotMove()
    {
        Grid grid = MakeGrid(100, 0.1, (x, _) => Kink(x, -0.5));

        var result = Round(grid, 2.0);

        for (int j = -100; j <= 100; j++)
        {
            for (int i = -100; i <= 100; i++)
            {
                if (Math.Abs(i * grid.H) < 1.0)
                    continue;
                Assert.Equal(ZAt(grid, grid.Vertices, i, j), ZAt(grid, result.Vertices, i, j));
            }
        }
    }

    [Fact]
    public void Round_CrestAndToeCloserThanLength_OffsetsSuperpose()
    {
        // Flat, a 1:2 batter from x = 0 to x = 1, then flat again: crest at 0 (kappa -0.5), toe at 1 (kappa +0.5).
        Grid grid = MakeGrid(100, 0.1, (x, _) => x <= 0.0 ? 0.0 : (x >= 1.0 ? -0.5 : -0.5 * x));
        const double length = 2.0;
        double r = length / 2.0;

        var result = Round(grid, length);

        foreach (int i in new[] { -5, 2, 5, 8, 14 })
        {
            double x = i * grid.H;
            double expected = grid.Vertices[(grid.Index(i, 5) * 3) + 2]
                + Parabola(-0.5, r, Math.Abs(x))
                + Parabola(0.5, r, Math.Abs(x - 1.0));
            Assert.Equal(expected, ZAt(grid, result.Vertices, i, 5), 5);
        }

        // Pinned down by hand at x = 0.2: crest -0.08, toe +0.005.
        Assert.Equal(-0.075, ZAt(grid, result.Vertices, 2, 5) - ZAt(grid, grid.Vertices, 2, 5), 5);
    }

    [Fact]
    public void Round_ProtectCurveAlongCrease_KeepsCreaseSharp()
    {
        Grid grid = MakeGrid(100, 0.1, (x, _) => Kink(x, -0.5));
        var protect = new[]
        {
            new MeshSmoother.BreaklinePolyline(new[] { 0.0, -10.0, 0.0, 10.0 }, 2, false)
        };
        var prepared = Prepare(grid, protect: protect);

        var result = CreaseRounder.Round(grid.Vertices, prepared, 2.0);

        Assert.Equal(0, prepared.CreaseEdgeCount);
        Assert.Equal(0, result.MovedVertexCount);
        Assert.Equal(grid.Vertices, result.Vertices);
        Assert.True(prepared.Held[grid.Index(0, 0)]);
    }

    [Fact]
    public void Round_WallStep_HoldsWallVerticesAndIgnoresWallEdgesAsCreases()
    {
        // A 1 m drop across one 0.1 m cell is about 84 degrees: a wall.
        Grid grid = MakeGrid(100, 0.1, (x, _) => x <= 0.0 ? 0.0 : -1.0);
        var prepared = Prepare(grid);

        var result = CreaseRounder.Round(grid.Vertices, prepared, 2.0);

        Assert.Equal(0, prepared.CreaseEdgeCount);
        Assert.Equal(0, result.MovedVertexCount);
        for (int j = -100; j <= 100; j++)
        {
            Assert.True(prepared.Held[grid.Index(0, j)]);
            Assert.True(prepared.Held[grid.Index(1, j)]);
            Assert.Equal(ZAt(grid, grid.Vertices, 0, j), ZAt(grid, result.Vertices, 0, j));
            Assert.Equal(ZAt(grid, grid.Vertices, 1, j), ZAt(grid, result.Vertices, 1, j));
        }
    }

    [Fact]
    public void Round_BoundaryExcludingCrease_MovesNothing()
    {
        Grid grid = MakeGrid(100, 0.1, (x, _) => Kink(x, -0.5));
        var boundary = new[] { (new[] { 3.0, -5.0, 8.0, -5.0, 8.0, 5.0, 3.0, 5.0 }, 4) };
        var prepared = Prepare(grid, boundary);

        var result = CreaseRounder.Round(grid.Vertices, prepared, 2.0);

        Assert.Equal(0, prepared.CreaseEdgeCount);
        Assert.Equal(0, result.MovedVertexCount);
        Assert.Equal(grid.Vertices, result.Vertices);
    }

    [Fact]
    public void Round_BoundaryCoveringPartOfCrease_NeverMovesVerticesOutside()
    {
        Grid grid = MakeGrid(100, 0.1, (x, _) => Kink(x, -0.5));
        var boundary = new[] { (new[] { -5.0, -5.0, 0.55, -5.0, 0.55, 5.0, -5.0, 5.0 }, 4) };
        var prepared = Prepare(grid, boundary);

        var result = CreaseRounder.Round(grid.Vertices, prepared, 2.0);

        Assert.True(result.MovedVertexCount > 0);
        for (int j = -100; j <= 100; j++)
        {
            for (int i = -100; i <= 100; i++)
            {
                double x = i * grid.H;
                double y = j * grid.H;
                bool outside = x > 0.55 || x < -5.0 || y < -5.0 || y > 5.0;
                if (outside)
                    Assert.Equal(ZAt(grid, grid.Vertices, i, j), ZAt(grid, result.Vertices, i, j));
            }
        }

        // Inside, the crease still rounds, only less than the full parabola where the boundary's fade reaches.
        double offset = ZAt(grid, result.Vertices, 0, 0) - ZAt(grid, grid.Vertices, 0, 0);
        Assert.True(offset < 0.0);
        Assert.True(offset > -0.125);
    }

    [Fact]
    public void Round_CreaseNetworkShorterThanLength_IsSkipped()
    {
        Grid grid = MakeGrid(100, 0.1, (x, _) => Kink(x, -0.5));
        var prepared = Prepare(grid);
        Assert.True(prepared.CreaseLength < 30.0);

        var skipped = CreaseRounder.Round(grid.Vertices, prepared, 30.0);
        var rounded = CreaseRounder.Round(grid.Vertices, prepared, 2.0);

        Assert.Equal(0, skipped.MovedVertexCount);
        Assert.Equal(0.0, skipped.RoundedCreaseLength);
        Assert.Equal(grid.Vertices, skipped.Vertices);
        Assert.True(rounded.MovedVertexCount > 0);
        Assert.True(rounded.RoundedCreaseLength > 0.0);
    }

    [Fact]
    public void Round_CoarseMesh_ReportsCoarseCreaseLength()
    {
        Grid coarse = MakeGrid(5, 2.0, (x, _) => Kink(x, -0.5));
        Grid fine = MakeGrid(100, 0.1, (x, _) => Kink(x, -0.5));

        var coarseResult = Round(coarse, 2.0);
        var fineResult = Round(fine, 2.0);

        Assert.True(coarseResult.RoundedCreaseLength > 0.0);
        Assert.True(coarseResult.CoarseCreaseLength > 0.0);
        Assert.True(fineResult.RoundedCreaseLength > 0.0);
        Assert.Equal(0.0, fineResult.CoarseCreaseLength);
    }

    [Fact]
    public void DensifyBand_CoarseMesh_RoundsIntoTheBandAndLeavesTheFarSideLevel()
    {
        // Cells 2 wide: a crest with nothing within L/2 = 1 of it but its own vertices. Rounded as is, the
        // crest vertices drop and every face touching them tilts across its whole 2 m width.
        Grid coarse = MakeGrid(5, 2.0, (x, _) => Kink(x, -0.5));
        var prepared = Prepare(coarse);

        var dense = CreaseRounder.DensifyBand(coarse.Mesh, prepared, 2.0, Tolerance, 100_000, out bool overBudget);

        Assert.False(overBudget);
        Assert.NotNull(dense);
        Assert.True(dense!.Value.AddedVertices > 0);
        (double[] vertices, int vertexCount, int[] faces, int faceCount) = dense.Value.Mesh;

        // Splitting does not change the surface: every added vertex lies on the kinked profile.
        for (int v = 0; v < vertexCount; v++)
            Assert.Equal(Kink(vertices[v * 3], -0.5), vertices[(v * 3) + 2], 9);

        var densePrepared = CreaseRounder.Prepare(
            dense.Value.Mesh,
            Array.Empty<(double[] xyVerts, int vertCount)>(),
            Array.Empty<MeshSmoother.BreaklinePolyline>(),
            10.0,
            Tolerance);
        var result = CreaseRounder.Round(vertices, densePrepared, 2.0);

        Assert.Equal(0.0, result.CoarseCreaseLength);
        for (int v = 0; v < vertexCount; v++)
        {
            double x = vertices[v * 3];
            double y = vertices[(v * 3) + 1];
            if (Math.Abs(y) > 6.0)
                continue;
            double offset = result.Vertices[(v * 3) + 2] - vertices[(v * 3) + 2];
            if (Math.Abs(x) >= 1.0)
                Assert.Equal(0.0, offset, 9);
            else
                Assert.Equal(Parabola(-0.5, 1.0, Math.Abs(x)), offset, 5);
        }
    }

    [Fact]
    public void DensifyBand_ShortLengthOverLongCrease_IsRefusedNotRun()
    {
        // 20 m of crease at L = 0.05 asks for ~7,400 vertices; a budget of 1,000 must refuse up front.
        Grid coarse = MakeGrid(5, 2.0, (x, _) => Kink(x, -0.5));

        var dense = CreaseRounder.DensifyBand(coarse.Mesh, Prepare(coarse), 0.05, Tolerance, 1_000, out bool overBudget);

        Assert.Null(dense);
        Assert.True(overBudget);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void Round_NonPositiveLength_ReturnsVerticesUnchanged(double length)
    {
        Grid grid = MakeGrid(100, 0.1, (x, _) => Kink(x, -0.5));

        var result = Round(grid, length);

        Assert.Equal(0, result.MovedVertexCount);
        Assert.Equal(0.0, result.MaxOffset);
        Assert.Equal(grid.Vertices, result.Vertices);
        Assert.NotSame(grid.Vertices, result.Vertices);
    }

    private static Grid MakePad() => MakeGrid(60, 0.1, (x, y) =>
    {
        double dx = Math.Max(Math.Abs(x) - 2.0, 0.0);
        double dy = Math.Max(Math.Abs(y) - 2.0, 0.0);
        return -0.5 * Math.Sqrt((dx * dx) + (dy * dy));
    }, symmetric: true);

    // 20 degrees: the 26.6 degree pad edge is a crease, the facet-to-facet folds of the cone's discretization are not.
    private static CreaseRounder.PreparedCreaseRounding PreparePad(Grid grid, double creaseAngle = 20.0) =>
        CreaseRounder.Prepare(
            grid.Mesh,
            Array.Empty<(double[] xyVerts, int vertCount)>(),
            Array.Empty<MeshSmoother.BreaklinePolyline>(),
            creaseAngle,
            Tolerance);

    [Fact]
    public void Round_SquarePadWithBatters_OnlyLowersCrestAndIsSymmetric()
    {
        // Flat pad |x|,|y| <= 2 with a 1:2 batter falling away on every side (cone at the corners).
        Grid grid = MakePad();
        var prepared = PreparePad(grid);

        var result = CreaseRounder.Round(grid.Vertices, prepared, 2.0);

        Assert.True(prepared.CreaseEdgeCount > 0);
        Assert.True(result.MovedVertexCount > 0);
        for (int v = 0; v < grid.VertexCount; v++)
            Assert.True(result.Vertices[(v * 3) + 2] <= grid.Vertices[(v * 3) + 2] + 1e-12);

        // Mid-edge of the pad: the straight-crease value.
        Assert.Equal(-0.125, ZAt(grid, result.Vertices, 20, 0) - ZAt(grid, grid.Vertices, 20, 0), 5);

        // The pad is symmetric under 90 degree rotation and mirroring, so the offset field must be too.
        const double symmetryTolerance = 5e-3;
        foreach ((int i, int j) in new[] { (20, 20), (20, 14), (23, 17), (17, 20), (12, 19), (26, 26) })
        {
            double o = ZAt(grid, result.Vertices, i, j) - ZAt(grid, grid.Vertices, i, j);
            foreach ((int a, int b) in new[] { (-i, j), (i, -j), (-i, -j), (j, i), (-j, i), (j, -i), (-j, -i) })
            {
                double other = ZAt(grid, result.Vertices, a, b) - ZAt(grid, grid.Vertices, a, b);
                Assert.True(Math.Abs(o - other) <= symmetryTolerance, $"offset at ({i},{j}) = {o} but at ({a},{b}) = {other}");
            }
        }

        // The corner region does round.
        Assert.True(ZAt(grid, result.Vertices, 20, 20) < ZAt(grid, grid.Vertices, 20, 20) - 1e-3);
    }

    // At 10 degrees the cone's own facet folds are creases too; they must add to the blur, not to a count.
    [Theory]
    [InlineData(20.0)]
    [InlineData(10.0)]
    public void Round_SquarePadWithBatters_CornerStaysBelowTwoOverlappingCreases(double creaseAngle)
    {
        Grid grid = MakePad();
        var prepared = PreparePad(grid, creaseAngle);

        var result = CreaseRounder.Round(grid.Vertices, prepared, 2.0);

        // Every crease here has kappa -0.5, so a straight crest drops kappa * L / 8 = 0.125. At a corner two
        // crests overlap and the coned batter end adds its own folds, so a blur drops it further - but never
        // as far as two full crests (0.25), however many mesh edges meet there. Splitting one crease into
        // several chains used to count it up to four times (0.5).
        Assert.True(result.MaxOffset > 0.125, $"MaxOffset was {result.MaxOffset}");
        Assert.True(result.MaxOffset < 0.25, $"MaxOffset was {result.MaxOffset}");
    }
}
