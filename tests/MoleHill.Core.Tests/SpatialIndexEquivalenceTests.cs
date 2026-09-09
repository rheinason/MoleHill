using MoleHill.Core.Analysis;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// The three flat-cell spatial indexes (<see cref="SpatialHashGrid2D"/>, <see cref="TerrainFaceGrid"/>,
/// <see cref="MeshHeightProjector"/>) must answer exactly what a brute-force scan answers, including on
/// the geometry that stresses cell membership: a long diagonal among tiny faces, a coarse/fine
/// transition, and a narrow corridor.
/// </summary>
public class SpatialIndexEquivalenceTests
{
    [Fact]
    public void SpatialHashGrid2D_Candidates_ContainEveryOverlappingItemExactlyOnce()
    {
        var random = new Random(90210);
        var bounds = new Bounds2D[400];
        for (int i = 0; i < bounds.Length; i++)
        {
            double x = random.NextDouble() * 100.0;
            double y = random.NextDouble() * 100.0;
            double w = random.NextDouble() * 6.0;
            double h = random.NextDouble() * 6.0;
            bounds[i] = new Bounds2D(x, x + w, y, y + h);
        }

        // One item spanning the whole domain — the "long diagonal" case for cell membership.
        bounds[7] = new Bounds2D(0.0, 100.0, 0.0, 100.0);

        SpatialHashGrid2D grid = SpatialHashGrid2D.Build(bounds);
        var scratch = new SpatialHashGrid2D.QueryScratch(bounds.Length);
        var candidates = new List<int>();

        for (int trial = 0; trial < 60; trial++)
        {
            double x = random.NextDouble() * 100.0;
            double y = random.NextDouble() * 100.0;
            var query = new Bounds2D(x, x + 3.0, y, y + 3.0);

            grid.GatherCandidates(query, candidates, scratch);

            var returned = new HashSet<int>(candidates);
            for (int i = 0; i < bounds.Length; i++)
            {
                if (bounds[i].Intersects(query))
                    Assert.Contains(i, returned);
            }

            // Candidates are gathered cell by cell, so they are not globally ordered - but no item may
            // be reported twice, whichever cells it spans.
            Assert.Equal(candidates.Count, returned.Count);
        }
    }

    [Fact]
    public void SpatialHashGrid2D_InvalidAndFilteredBounds_AreNeverReturned()
    {
        var bounds = new[]
        {
            new Bounds2D(0, 1, 0, 1),
            new Bounds2D(double.NaN, 1, 0, 1),
            new Bounds2D(2, 1, 0, 1),
            new Bounds2D(0, 1, 0, 1)
        };
        var valid = new[] { true, true, true, false };

        SpatialHashGrid2D grid = SpatialHashGrid2D.Build(bounds, valid);
        var candidates = new List<int>();
        grid.GatherCandidates(new Bounds2D(-10, 10, -10, 10), candidates);

        Assert.Equal(new[] { 0 }, candidates);
    }

    [Fact]
    public void SpatialHashGrid2D_EmptyInput_ReturnsNoCandidates()
    {
        SpatialHashGrid2D grid = SpatialHashGrid2D.Build(Array.Empty<Bounds2D>());
        var candidates = new List<int>();

        grid.GatherCandidates(new Bounds2D(0, 1, 0, 1), candidates);

        Assert.Empty(candidates);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TerrainFaceGrid_InterpolateZ_MatchesTheContainingTriangle(bool coarseFineTransition)
    {
        (double[] vertices, int[] faces) = coarseFineTransition ? CoarseFineSheet() : UniformSheet();
        var grid = new TerrainFaceGrid(vertices, vertices.Length / 3, faces, faces.Length / 3);

        var random = new Random(555);
        for (int trial = 0; trial < 300; trial++)
        {
            double x = random.NextDouble() * 40.0;
            double y = random.NextDouble() * 30.0;

            Assert.True(grid.TryInterpolateZ(x, y, out double z));
            Assert.Equal(Height(x, y), z, 6);
        }
    }

    [Fact]
    public void TerrainFaceGrid_LongDiagonalFaceAmongTinyFaces_IsStillFound()
    {
        // One huge triangle spans the whole domain; the rest are tiny. Its bounding box covers every
        // cell, so it must still be reachable from a query in any of them.
        double[] vertices =
        {
            0, 0, 0,
            100, 0, 0,
            0, 100, 0,
            0.1, 0.1, 5,
            0.2, 0.1, 5,
            0.1, 0.2, 5
        };
        int[] faces = { 0, 1, 2, 3, 4, 5 };
        var grid = new TerrainFaceGrid(vertices, 6, faces, 2);

        Assert.True(grid.TryInterpolateZ(30.0, 30.0, out double z));
        Assert.Equal(0.0, z, 6);
        Assert.True(grid.TryFindFace(60.0, 10.0, out int face, out _, out _, out _));
        Assert.Equal(0, face);
    }

    [Fact]
    public void TerrainFaceGrid_PointOutsideTheMesh_IsNotInterpolated()
    {
        (double[] vertices, int[] faces) = UniformSheet();
        var grid = new TerrainFaceGrid(vertices, vertices.Length / 3, faces, faces.Length / 3);

        Assert.False(grid.TryInterpolateZ(-25.0, -25.0, out _));
    }

    [Fact]
    public void TerrainFaceGrid_RayCandidates_StillMatchTheLinearTraversal()
    {
        // The ray candidate buffer no longer carries a face-sized mark array; duplicates are removed by
        // the sort that already restored source face order. The indexed answer must be unchanged.
        (double[] vertices, int[] faces) = CoarseFineSheet();
        var grid = new TerrainFaceGrid(vertices, vertices.Length / 3, faces, faces.Length / 3);

        var random = new Random(31337);
        for (int trial = 0; trial < 80; trial++)
        {
            double x = random.NextDouble() * 40.0;
            double y = random.NextDouble() * 30.0;
            double angle = random.NextDouble() * Math.PI * 2.0;
            double dirX = Math.Cos(angle);
            double dirY = Math.Sin(angle);

            bool expected = grid.TryFindRayDaylightReachLinearForDiagnostics(
                x, y, Height(x, y) + 5.0, dirX, dirY, 3.0, 1.0, 50.0,
                out double expectedReach, out _);
            bool actual = grid.TryFindRayDaylightReach(
                x, y, Height(x, y) + 5.0, dirX, dirY, 3.0, 1.0, 50.0,
                out double actualReach, out _);

            Assert.Equal(expected, actual);
            if (expected)
                Assert.Equal(expectedReach, actualReach, 9);
        }
    }

    [Fact]
    public void MeshHeightProjector_MatchesTheContainingTriangleAcrossACoarseFineTransition()
    {
        (double[] vertices, int[] faces) = CoarseFineSheet();
        var projector = new MeshHeightProjector(vertices, vertices.Length / 3, faces, faces.Length / 3);

        var random = new Random(24680);
        for (int trial = 0; trial < 300; trial++)
        {
            double x = random.NextDouble() * 40.0;
            double y = random.NextDouble() * 30.0;

            Assert.True(projector.TryProjectZ(x, y, 0.0, 1e-6, out double z, out MeshHeightProjector.ProjectionStatus status));
            Assert.Equal(MeshHeightProjector.ProjectionStatus.Projected, status);
            Assert.Equal(Height(x, y), z, 6);
        }
    }

    [Fact]
    public void MeshHeightProjector_OutsideTheMesh_ReportsOutside()
    {
        (double[] vertices, int[] faces) = UniformSheet();
        var projector = new MeshHeightProjector(vertices, vertices.Length / 3, faces, faces.Length / 3);

        Assert.False(projector.TryProjectZ(-50.0, -50.0, 0.0, 1e-6, out _, out MeshHeightProjector.ProjectionStatus status));
        Assert.Equal(MeshHeightProjector.ProjectionStatus.OutsideMesh, status);
    }

    [Fact]
    public void MeshHeightProjector_EmptyMesh_ProjectsNothing()
    {
        var projector = new MeshHeightProjector(Array.Empty<double>(), 0, Array.Empty<int>(), 0);

        Assert.False(projector.TryProjectZ(0.0, 0.0, 0.0, 1e-6, out _, out _));
    }

    [Fact]
    public void MeshHeightProjector_NarrowCorridor_ProjectsAlongItsWholeLength()
    {
        // A long thin strip: every face's bounding box is tall and skinny, the case where bounding-box
        // cell membership is at its least selective.
        const int steps = 200;
        var vertices = new double[(steps + 1) * 2 * 3];
        for (int i = 0; i <= steps; i++)
        {
            int lower = i * 2;
            int upper = lower + 1;
            vertices[lower * 3] = i * 0.5;
            vertices[(lower * 3) + 1] = 0.0;
            vertices[(lower * 3) + 2] = i * 0.25;
            vertices[upper * 3] = i * 0.5;
            vertices[(upper * 3) + 1] = 0.2;
            vertices[(upper * 3) + 2] = i * 0.25;
        }

        var faces = new List<int>();
        for (int i = 0; i < steps; i++)
        {
            int a = i * 2;
            faces.Add(a); faces.Add(a + 2); faces.Add(a + 3);
            faces.Add(a); faces.Add(a + 3); faces.Add(a + 1);
        }

        var projector = new MeshHeightProjector(vertices, (steps + 1) * 2, faces.ToArray(), faces.Count / 3);

        for (int i = 0; i < steps; i++)
        {
            double x = (i + 0.5) * 0.5;
            Assert.True(projector.TryProjectZ(x, 0.1, 0.0, 1e-6, out double z, out _), $"No projection at x={x}.");
            Assert.Equal(x * 0.5, z, 6);
        }
    }

    private static double Height(double x, double y) => (x * 0.3) - (y * 0.2);

    private static (double[] Vertices, int[] Faces) UniformSheet()
    {
        return Sheet(Steps(0.0, 40.0, 2.0), Steps(0.0, 30.0, 2.0));
    }

    private static (double[] Vertices, int[] Faces) CoarseFineSheet()
    {
        var xs = new List<double>();
        for (double x = 0.0; x < 12.0; x += 4.0) xs.Add(x);
        for (double x = 12.0; x < 20.0; x += 0.25) xs.Add(x);
        for (double x = 20.0; x < 40.0; x += 4.0) xs.Add(x);
        xs.Add(40.0);
        return Sheet(xs.ToArray(), Steps(0.0, 30.0, 2.5));
    }

    private static double[] Steps(double from, double to, double step)
    {
        var values = new List<double>();
        for (double value = from; value < to - 1e-9; value += step)
            values.Add(value);
        values.Add(to);
        return values.ToArray();
    }

    private static (double[] Vertices, int[] Faces) Sheet(double[] xs, double[] ys)
    {
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
                vertices[(v * 3) + 2] = Height(xs[i], ys[j]);
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
}
