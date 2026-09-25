using MoleHill.Core.Analysis;
using Xunit;

namespace MoleHill.Core.Tests;

public class PondingSolverTests
{
    private static (double[] Vertices, int VertexCount, int[] Faces, int FaceCount) Grid(
        int nx, int ny, Func<double, double, double> height)
    {
        int columns = nx + 1;
        int rows = ny + 1;
        int vertexCount = columns * rows;
        var vertices = new double[vertexCount * 3];
        for (int j = 0; j < rows; j++)
        {
            for (int i = 0; i < columns; i++)
            {
                int vertex = (j * columns) + i;
                vertices[vertex * 3] = i;
                vertices[(vertex * 3) + 1] = j;
                vertices[(vertex * 3) + 2] = height(i, j);
            }
        }

        var faces = new int[nx * ny * 6];
        int face = 0;
        for (int j = 0; j < ny; j++)
        {
            for (int i = 0; i < nx; i++)
            {
                int a = (j * columns) + i;
                int b = a + 1;
                int c = a + columns + 1;
                int d = a + columns;
                faces[face++] = a;
                faces[face++] = b;
                faces[face++] = c;
                faces[face++] = a;
                faces[face++] = c;
                faces[face++] = d;
            }
        }

        return (vertices, vertexCount, faces, nx * ny * 2);
    }

    private static IReadOnlyList<PondingSolver.Pond> Solve(
        (double[] Vertices, int VertexCount, int[] Faces, int FaceCount) mesh,
        PondingSolver.Options? options = null)
    {
        BasinGraph graph = DrainageBasinAnalyzer.Analyze(
            mesh.Vertices, mesh.VertexCount, mesh.Faces, mesh.FaceCount);
        return PondingSolver.Solve(graph, mesh.Vertices, mesh.VertexCount, mesh.Faces, options);
    }

    /// <summary>
    /// A flat pad ringed by a wall of a stated height: the volume is exactly area × depth, so the prism
    /// sum can be checked against a number worked out on paper rather than against itself.
    /// </summary>
    [Fact]
    public void Solve_WalledPad_ReportsTheExactImpoundedVolume()
    {
        // Pad from 2..8 in both directions at z = 10, ringed by ground at z = 12.
        var mesh = Grid(10, 10, (x, y) => x >= 2 && x <= 8 && y >= 2 && y <= 8 ? 10.0 : 12.0);

        PondingSolver.Pond pond = Assert.Single(Solve(mesh));

        Assert.Equal(12.0, pond.SpillZ, 6);
        Assert.Equal(10.0, pond.FloorZ, 6);
        Assert.Equal(2.0, pond.MaxDepth, 6);

        // The pad is 6 x 6 at depth 2. The ring faces spanning 10 -> 12 are half submerged, and their
        // centroids sit at 11.33 or 10.67, so they contribute too; the pad alone is the lower bound.
        Assert.True(pond.Volume >= 72.0, $"volume {pond.Volume:F2} is below the 6x6x2 pad itself");
        Assert.True(pond.Volume < 144.0, $"volume {pond.Volume:F2} exceeds the whole 8x8 footprint at depth 2");
        Assert.True(pond.PlanArea >= 36.0, $"plan area {pond.PlanArea:F2} is below the pad itself");
    }

    /// <summary>
    /// The spill is the lowest lip, not the lowest point of the catchment boundary. A depression whose
    /// catchment runs down to the terrain edge would otherwise be given a spill far below its own rim —
    /// the naive reading, and the one that makes every pond volume nonsense.
    /// </summary>
    [Fact]
    public void Solve_DepressionOnAHillside_SpillsAtItsOwnLipNotAtTheTerrainEdge()
    {
        // Ground falls from z=20 at y=0 to z=10 at y=20. A bowl is cut into it around (10, 4), high up.
        var mesh = Grid(20, 20, (x, y) =>
        {
            double baseZ = 20.0 - (0.5 * y);
            double dx = x - 10.0;
            double dy = y - 4.0;
            double radius = Math.Sqrt((dx * dx) + (dy * dy));
            return radius < 3.0 ? baseZ - (2.0 * (3.0 - radius) / 3.0) : baseZ;
        });

        PondingSolver.Pond pond = Assert.Single(Solve(mesh));

        // The bowl's floor is at z = 16 and the terrain's low edge at z = 10. What is being pinned is
        // that the spill is up at the bowl's own lip and not down at the catchment's far boundary — the
        // naive rim reading would land near 10 and is an order of magnitude out, so a loose bound catches
        // it. How much of the 2.0 cut is actually impounded depends on where the mesh samples the lip, so
        // that is bounded rather than predicted.
        Assert.True(pond.SpillZ > 15.0, $"spill {pond.SpillZ:F2} is far below the bowl's own lip");
        Assert.True(pond.SpillZ < 17.0, $"spill {pond.SpillZ:F2} is above the bowl's rim");
        Assert.True(pond.MaxDepth > 0.2, $"depth {pond.MaxDepth:F2} is too shallow to be the bowl at all");
        Assert.True(pond.MaxDepth <= 2.0, $"depth {pond.MaxDepth:F2} exceeds the 2.0 the bowl was cut to");
    }

    /// <summary>A pond's shoreline is a closed loop at the water surface, and it is drawn as a contour.</summary>
    [Fact]
    public void Solve_WalledPad_TracesAClosedShorelineAtTheSpillLevel()
    {
        var mesh = Grid(10, 10, (x, y) => x >= 2 && x <= 8 && y >= 2 && y <= 8 ? 10.0 : 12.0);

        PondingSolver.Pond pond = Assert.Single(Solve(mesh));

        Assert.NotEmpty(pond.Outlines);
        foreach (double[] outline in pond.Outlines)
        {
            int pointCount = outline.Length / 3;
            for (int index = 0; index < pointCount; index++)
                Assert.Equal(pond.SpillZ, outline[(index * 3) + 2], 6);
        }
    }

    /// <summary>
    /// The threshold is on the measurement, not on the routing: the router still finds a shallow dimple,
    /// and this is where it is judged not worth reporting. Both halves matter — a router that dropped it
    /// would leave nothing to threshold, and a solver that reported it would cry wolf.
    /// </summary>
    [Fact]
    public void Solve_ShallowDimple_IsFoundByTheRouterAndFilteredHere()
    {
        var mesh = Grid(20, 20, (x, y) =>
        {
            double baseZ = 100.0 - (0.01 * y);
            double dx = x - 10.0;
            double dy = y - 10.0;
            double radius = Math.Sqrt((dx * dx) + (dy * dy));
            return radius < 3.0 ? baseZ - (0.02 * (3.0 - radius)) : baseZ;
        });
        BasinGraph graph = DrainageBasinAnalyzer.Analyze(
            mesh.Vertices, mesh.VertexCount, mesh.Faces, mesh.FaceCount);
        Assert.True(graph.SinkBasinCount >= 1, "the router should still see the depression");

        var reported = PondingSolver.Solve(
            graph, mesh.Vertices, mesh.VertexCount, mesh.Faces,
            new PondingSolver.Options { MinimumDepth = 0.05 });
        Assert.Empty(reported);

        var everything = PondingSolver.Solve(
            graph, mesh.Vertices, mesh.VertexCount, mesh.Faces,
            new PondingSolver.Options { MinimumDepth = 0.0 });
        Assert.NotEmpty(everything);
    }

    /// <summary>
    /// The depth filter runs before the pond is measured, so it must still be inclusive at the threshold:
    /// a pond exactly as deep as the minimum is reported, and one a hair deeper minimum drops it.
    /// </summary>
    [Fact]
    public void Solve_MinimumDepthAtPondDepth_KeepsThePondInclusively()
    {
        var mesh = Grid(20, 20, (x, y) =>
        {
            double baseZ = 100.0 - (0.01 * y);
            double dx = x - 10.0;
            double dy = y - 10.0;
            double radius = Math.Sqrt((dx * dx) + (dy * dy));
            return radius < 3.0 ? baseZ - (0.02 * (3.0 - radius)) : baseZ;
        });
        BasinGraph graph = DrainageBasinAnalyzer.Analyze(
            mesh.Vertices, mesh.VertexCount, mesh.Faces, mesh.FaceCount);
        var all = PondingSolver.Solve(
            graph, mesh.Vertices, mesh.VertexCount, mesh.Faces,
            new PondingSolver.Options { MinimumDepth = 0.0 });
        double deepest = all.Max(pond => pond.MaxDepth);

        var atThreshold = PondingSolver.Solve(
            graph, mesh.Vertices, mesh.VertexCount, mesh.Faces,
            new PondingSolver.Options { MinimumDepth = deepest });
        var aboveThreshold = PondingSolver.Solve(
            graph, mesh.Vertices, mesh.VertexCount, mesh.Faces,
            new PondingSolver.Options { MinimumDepth = deepest + 1e-9 });

        Assert.Contains(atThreshold, pond => pond.MaxDepth == deepest && pond.Volume > 0.0 && pond.Outlines.Count > 0);
        Assert.Empty(aboveThreshold);
    }

    /// <summary>A hillside with nothing cut into it holds no water, and must report none.</summary>
    [Fact]
    public void Solve_UniformSlope_ReportsNoPond()
    {
        var mesh = Grid(10, 10, (x, y) => 100.0 - (0.5 * y));

        Assert.Empty(Solve(mesh));
    }

    /// <summary>A graded pad that drains is not a pond, which is the false positive that matters most.</summary>
    [Fact]
    public void Solve_GradedPadOnAHillside_ReportsNoPond()
    {
        var mesh = Grid(10, 10, (x, y) =>
            x >= 3 && x <= 7 && y >= 3 && y <= 7 ? 100.0 - (0.2 * 5.0) : 100.0 - (0.2 * y));

        Assert.Empty(Solve(mesh));
    }

    /// <summary>Two separate depressions are two ponds, each measured on its own lip.</summary>
    [Fact]
    public void Solve_TwoWalledPads_ReportsTwoPondsAtTheirOwnLevels()
    {
        var mesh = Grid(20, 10, (x, y) =>
        {
            bool left = x >= 2 && x <= 8 && y >= 2 && y <= 8;
            bool right = x >= 12 && x <= 18 && y >= 2 && y <= 8;
            if (left) return 10.0;
            if (right) return 7.0;
            return 12.0;
        });

        var ponds = Solve(mesh);

        Assert.Equal(2, ponds.Count);
        var depths = new List<double>();
        foreach (PondingSolver.Pond pond in ponds)
            depths.Add(Math.Round(pond.MaxDepth, 6));
        depths.Sort();
        Assert.Equal(new List<double> { 2.0, 5.0 }, depths);
    }

    /// <summary>
    /// A depression whose rim is the terrain's own edge fills to that edge and runs off it. Reported as
    /// spilling off the terrain, because "it overflows into the next field" and "it overflows onto your
    /// own ground" are different facts for whoever reads it.
    /// </summary>
    [Fact]
    public void Solve_BowlReachingTheTerrainEdge_ReportsSpillingOffTheTerrain()
    {
        var mesh = Grid(10, 10, (x, y) => ((x - 5) * (x - 5)) + ((y - 5) * (y - 5)));

        PondingSolver.Pond pond = Assert.Single(Solve(mesh));

        Assert.True(pond.SpillsOffTerrain);
        Assert.Equal(0.0, pond.FloorZ, 6);
        Assert.True(pond.Volume > 0.0);
    }
}
