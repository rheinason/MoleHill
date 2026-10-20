using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using MoleHill.Core.Processing;
using MoleHill.Core.Scan;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class ScanGroundTests
{
    /// <summary>A scan-like mesh: a height field sampled every <paramref name="spacing"/>, two triangles a cell.</summary>
    private static IndexedTriMesh HeightField(double width, double depth, double spacing, Func<double, double, double> height)
    {
        int columns = (int)Math.Round(width / spacing) + 1, rows = (int)Math.Round(depth / spacing) + 1;
        var vertices = new double[columns * rows * 3];
        for (int j = 0; j < rows; j++)
        {
            for (int i = 0; i < columns; i++)
            {
                int v = (j * columns) + i;
                vertices[v * 3] = i * spacing;
                vertices[(v * 3) + 1] = j * spacing;
                vertices[(v * 3) + 2] = height(i * spacing, j * spacing);
            }
        }

        var faces = new int[(columns - 1) * (rows - 1) * 6];
        int f = 0;
        for (int j = 0; j < rows - 1; j++)
        {
            for (int i = 0; i < columns - 1; i++)
            {
                int a = (j * columns) + i, b = a + 1, c = a + columns + 1, d = a + columns;
                faces[f++] = a; faces[f++] = b; faces[f++] = c;
                faces[f++] = a; faces[f++] = c; faces[f++] = d;
            }
        }

        return IndexedTriMesh.FromArrays(vertices, faces);
    }

    private static readonly ScanGround.Options Metric = new()
    {
        CellSize = 0.25,
        SmallestObject = 2.0,
        LargestObject = 48.0,
        InitialThreshold = 0.3,
        MaximumThreshold = 4.0,
        Margin = 0.5,
        MinimumObjectArea = 4.0,
    };

    [Fact]
    public void Rasterize_OverlappingSparseTriangles_InterpolatesTopAndLowestInEitherWinding()
    {
        var scan = IndexedTriMesh.FromArrays(
            new double[] { 0, 0, 1, 10, 0, 3, 0, 10, 5, 0, 0, 11, 10, 0, 13, 0, 10, 15 },
            new[] { 0, 1, 2, 5, 4, 3 });

        ScanRasterizer.Rasters rasters = ScanRasterizer.Rasterize(scan, 1.0);

        Assert.Equal(12.8, rasters.Top[3, 3], 10);
        Assert.Equal(2.8, rasters.Lowest[3, 3], 10);
        Assert.True(double.IsNaN(rasters.Top[9, 9]));
    }

    [Fact]
    public void Rasterize_CellWouldOverflowDimension_RejectsBeforeAllocating()
    {
        IndexedTriMesh scan = HeightField(1, 1, 1, (_, _) => 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => ScanRasterizer.Rasterize(scan, 1e-20));
    }

    [Fact]
    public void Mesh_CoarsePlane_PreservesBorderHeights()
    {
        var values = new double[21 * 21];
        for (int j = 0; j < 21; j++)
            for (int i = 0; i < 21; i++)
                values[j * 21 + i] = 0.3 * i + 0.2 * j;
        var grid = new ScanGrid(0, 0, 1, 21, 21, values);

        IndexedTriMesh mesh = ScanGroundMesher.Mesh(grid, 4.0);

        Assert.False(mesh.IsEmpty);
        for (int v = 0; v < mesh.VertexCount; v++)
            Assert.Equal(0.3 * mesh.Vertices[v * 3] + 0.2 * mesh.Vertices[v * 3 + 1], mesh.Vertices[v * 3 + 2], 10);
        Assert.Throws<ArgumentOutOfRangeException>(() => ScanGroundMesher.Mesh(grid, double.NaN));
        Assert.True(ScanGroundMesher.Mesh(grid, double.MaxValue).IsEmpty);
    }

    [Fact]
    public void Build_EmptyScan_ReturnsEmptyGround()
    {
        ScanGround.Result result = ScanGround.Build(IndexedTriMesh.Empty, Metric);
        Assert.Equal(0, result.Ground.NodeCount);
        Assert.Equal(0.0, result.FilledShare);
        Assert.True(ScanGroundMesher.Mesh(result.Ground, 1.0).IsEmpty);
    }

    [Fact]
    public void Fill_Cancelled_ThrowsBeforeSolving()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => HarmonicFill.Fill(
            new[] { 1.0, double.NaN }, new[] { true, false }, new[] { true, true }, 2, 1, cancellation.Token));
    }

    [Fact]
    public void Build_NonfiniteWindow_RejectsInsteadOfLooping()
    {
        var options = new ScanGround.Options
        {
            SmallestObject = 2,
            LargestObject = double.PositiveInfinity,
            InitialThreshold = 0.3,
            MaximumThreshold = 4,
            Margin = 0.5,
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => ScanGround.Build(IndexedTriMesh.Empty, options));
    }

    /// <summary>Hillside rising 15% in x, with a 12 m terrace between 2 m walls at x = 30 and x = 42.</summary>
    private static double Hillside(double x, double y) =>
        (0.15 * x) + 0.02 * y + (x > 30.0 ? 2.0 : 0.0) + (x > 42.0 ? 2.0 : 0.0);

    private static readonly (double X0, double Y0, double X1, double Y1)[] Houses =
    {
        (8, 10, 18, 24),
        (48, 40, 62, 50),
        (12, 50, 26, 60),
    };

    private static readonly (double X, double Y)[] Trees = { (60, 12), (52, 68) };

    /// <summary>The hillside with gable-roofed houses and two conical trees on it.</summary>
    private static double Site(double x, double y)
    {
        double ground = Hillside(x, y);
        foreach ((double x0, double y0, double x1, double y1) in Houses)
        {
            if (x < x0 || x > x1 || y < y0 || y > y1)
                continue;
            double baseZ = Hillside((x0 + x1) / 2, (y0 + y1) / 2);
            double halfSpan = (y1 - y0) / 2;
            double eave = baseZ + 5.0;
            return eave + (2.5 * (1.0 - (Math.Abs(y - ((y0 + y1) / 2)) / halfSpan)));
        }

        foreach ((double tx, double ty) in Trees)
        {
            double r = Math.Sqrt(((x - tx) * (x - tx)) + ((y - ty) * (y - ty)));
            if (r < 3.0)
                ground = Math.Max(ground, Hillside(tx, ty) + 6.0 * (1.0 - (r / 3.0)) + 1.0);
        }

        return ground;
    }

    [Fact]
    public void Build_HillsideWithHousesAndTrees_RemovesThemAndKeepsTheTerraces()
    {
        IndexedTriMesh scan = HeightField(80, 80, 0.25, Site);
        ScanGround.Result result = ScanGround.Build(scan, Metric);

        Assert.Equal(Houses.Length + Trees.Length, result.ObjectCount);
        ScanGrid ground = result.Ground;
        double worstUnderHouse = 0.0;
        foreach ((double x0, double y0, double x1, double y1) in Houses)
        {
            for (double x = x0 + 1; x <= x1 - 1; x += 1.0)
            {
                for (double y = y0 + 1; y <= y1 - 1; y += 1.0)
                {
                    int i = (int)Math.Round(x / ground.CellSize), j = (int)Math.Round(y / ground.CellSize);
                    Assert.False(result.IsGround[(j * ground.Columns) + i]);
                    // Away from the walls the hillside under a house is a plane, which harmonic filling rebuilds.
                    if (Math.Abs(x - 30) > 2 && Math.Abs(x - 42) > 2)
                        worstUnderHouse = Math.Max(worstUnderHouse, Math.Abs(ground[i, j] - Hillside(x, y)));
                }
            }
        }

        Assert.True(worstUnderHouse < 0.35, $"ground under the houses misses by {worstUnderHouse:F3}");

        // The terrace top, raised only along its wall, stays ground.
        for (double y = 2; y < 78; y += 4)
        {
            int i = (int)Math.Round(36 / ground.CellSize), j = (int)Math.Round(y / ground.CellSize);
            if (y is > 38 and < 52)
                continue;
            Assert.True(result.IsGround[(j * ground.Columns) + i], $"terrace at y = {y} was taken away");
            Assert.InRange(ground[i, j] - Hillside(36, y), -0.05, 0.05);
        }
    }

    [Fact]
    public void Build_TreeOnAKnoll_RemovesTheTreeAndKeepsTheKnoll()
    {
        // The filter shaves a knoll's top off as raised, and that region runs out into the slope: a step, kept as
        // ground. A tree standing on it was part of the same region, took its verdict and stayed as ground too.
        static double Knoll(double x, double y) => 10 * Math.Max(0, 1 - Math.Sqrt((x - 40) * (x - 40) + (y - 40) * (y - 40)) / 15);
        static double Site(double x, double y) =>
            Knoll(x, y) + ((x - 41) * (x - 41) + (y - 40) * (y - 40) < 4 ? 6.0 : 0.0);
        ScanGround.Result result = ScanGround.Build(HeightField(80, 80, 0.25, Site), Metric);
        ScanGrid ground = result.Ground;
        int Node(double x, double y) => (int)Math.Round(y / ground.CellSize) * ground.Columns + (int)Math.Round(x / ground.CellSize);

        Assert.False(result.IsGround[Node(41, 40)]);
        // The fill cannot rebuild the knoll's peak under the tree, but the 6 m tree is gone.
        Assert.InRange(ground.Values[Node(41, 40)] - Knoll(41, 40), -1.5, 0.5);
        foreach ((double x, double y) in new[] { (40.0, 46.0), (34.0, 40.0), (40.0, 34.0) })
            Assert.True(result.IsGround[Node(x, y)], $"the knoll at {x}, {y} was taken away");
    }

    [Fact]
    public void Build_SteepPlainSlope_IsAllGround()
    {
        ScanGround.Result result = ScanGround.Build(HeightField(60, 40, 0.25, (x, y) => 0.5 * x), Metric);

        Assert.Equal(0, result.ObjectCount);
        Assert.Equal(0.0, result.FilledShare);
    }

    [Fact]
    public void Fill_LinearGroundAroundAHole_RebuildsThePlane()
    {
        const int size = 120;
        var values = new double[size * size];
        var known = new bool[values.Length];
        var domain = new bool[values.Length];
        for (int j = 0; j < size; j++)
        {
            for (int i = 0; i < size; i++)
            {
                int node = (j * size) + i;
                domain[node] = true;
                values[node] = (0.3 * i) - (0.1 * j);
                known[node] = Math.Abs(i - 60) > 30 || Math.Abs(j - 50) > 25;
            }
        }

        double[] filled = HarmonicFill.Fill(values, known, domain, size, size);

        double worst = 0.0;
        for (int node = 0; node < values.Length; node++)
            worst = Math.Max(worst, Math.Abs(filled[node] - values[node]));
        Assert.True(worst < 1e-3, $"worst {worst}");
    }

    [Fact]
    public void Open_RemovesANarrowSpikeAndKeepsAWidePlateau()
    {
        const int size = 40;
        var values = new double[size * size];
        for (int j = 0; j < size; j++)
        {
            for (int i = 0; i < size; i++)
                values[(j * size) + i] = i >= 20 ? 5.0 : (i == 5 && j == 5 ? 9.0 : 0.0);
        }

        values[(10 * size) + 10] = double.NaN;
        double[] opened = GridMorphology.Open(values, size, size, 2);

        Assert.Equal(0.0, opened[(5 * size) + 5]);
        Assert.Equal(5.0, opened[(20 * size) + 30]);
        Assert.Equal(5.0, opened[(20 * size) + 20]);
        Assert.Equal(0.0, opened[(10 * size) + 11]);
    }

    [Fact]
    public void SimplyBordered_CornerOnlyContact_DropsOneCell()
    {
        // 3 x 3 cells: a block of four at the bottom left and one cell touching it only at a corner.
        bool[] cells =
        {
            true, true, false,
            true, true, false,
            false, false, true,
        };

        bool[] result = ScanGroundMesher.SimplyBordered(cells, 3, 3);

        Assert.False(result[8]);
        Assert.Equal(4, result.Count(c => c));
    }

    [Fact]
    public void Mesh_GroundWithAHouseCutOut_HasSimpleBorderLoopsAndRecoversWithinTolerance()
    {
        IndexedTriMesh scan = HeightField(80, 80, 0.25, Site);
        ScanGround.Result ground = ScanGround.Build(scan, Metric);
        IndexedTriMesh mesh = ScanGroundMesher.Mesh(ground.Ground, 1.0);

        Assert.Equal(81 * 81, mesh.VertexCount);
        Assert.True(MeshBoundaryLoopBuilder.TryBuildBoundaryLoopsIndexed(mesh.Faces, mesh.FaceCount, out List<int[]> loops));
        Assert.Single(loops);

        TerrainCurveRecovery.Result curves = TerrainCurveRecovery.Recover(mesh, new TerrainCurveRecovery.Options
        {
            VerticalTolerance = 0.25,
            MaximumCurves = 60,
            LandformLines = false,
        });
        // Terraces extruded across the site: the border alone rebuilds them, the filled houses included.
        Assert.Equal(TerrainCurveRecovery.Termination.ToleranceSatisfied, curves.Termination);
    }
}
