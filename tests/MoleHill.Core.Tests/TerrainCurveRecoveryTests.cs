using MoleHill.Core.Engine;
using MoleHill.Core.Processing;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class TerrainCurveRecoveryTests
{
    private static IndexedTriMesh Grid(int size, Func<double, double, double> height)
    {
        int n = size + 1;
        var vertices = new double[n * n * 3];
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                int v = j * n + i;
                vertices[v * 3] = i;
                vertices[v * 3 + 1] = j;
                vertices[v * 3 + 2] = height(i, j);
            }
        }

        var faces = new List<int>();
        for (int j = 0; j < size; j++)
        {
            for (int i = 0; i < size; i++)
            {
                int a = j * n + i, b = a + 1, c = a + n + 1, d = a + n;
                faces.AddRange(new[] { a, b, c, a, c, d });
            }
        }

        return new IndexedTriMesh(vertices, n * n, faces.ToArray(), faces.Count / 3);
    }

    [Fact]
    public void ConstrainReference_OutlineHuggingTheBorder_KeepsTheBorderSimple()
    {
        // The reference scan's paving outline ran down its west edge 6 cm inside the 0.987 m ground mesh,
        // with 8.7 m between vertices, then turned into the terrain (coordinates from that scan, shifted).
        const double spacing = .98697;
        int columns = 12, rows = 24;
        var vertices = new List<double>();
        for (int j = 0; j < rows; j++)
            for (int i = 0; i < columns; i++)
                vertices.AddRange(new[] { i * spacing, j * spacing, .03 * j + .02 * i });
        var faces = new List<int>();
        for (int j = 0; j + 1 < rows; j++)
            for (int i = 0; i + 1 < columns; i++)
            {
                int a = j * columns + i, b = a + 1, c = a + columns + 1, d = a + columns;
                faces.AddRange(new[] { a, b, c, a, c, d });
            }
        var terrain = new IndexedTriMesh(vertices.ToArray(), columns * rows, faces.ToArray(), faces.Count / 3);
        double[] outline = { 1.474, 1.707, .3, 1.143, 1.578, .3, .786, 1.514, .3, .422, 1.521, .3, .062, 1.598, .3,
            .062, 10.872, .3, .062, 20.145, .3, .336, 20.037, .3, .600, 19.872, .3, .838, 19.654, .3 };
        IndexedTriMesh? constrained = TerrainCurveRecovery.ConstrainReference(terrain, new[] { outline }, .5, out string? error);
        Assert.True(constrained.HasValue, error);
        Assert.True(MoleHill.Core.Grading.MeshBoundaryLoopBuilder.TryBuildBoundaryLoopsIndexed(
            constrained.Value.Faces, constrained.Value.FaceCount, out List<int[]> loops), "The border touches itself.");
        Assert.Single(loops);
    }

    [Fact]
    public void Recover_TiltedPlane_NeedsOnlyItsBorder()
    {
        TerrainCurveRecovery.Result result = TerrainCurveRecovery.Recover(
            Grid(20, (x, y) => 0.1 * x + 0.05 * y),
            new TerrainCurveRecovery.Options { VerticalTolerance = 0.01 });

        Assert.Equal(TerrainCurveRecovery.Termination.ToleranceSatisfied, result.Termination);
        Assert.Empty(result.Contours);
        Assert.Equal(0, result.SpotPointCount);
        double[] border = Assert.Single(result.Borders);
        Assert.Equal(4, border.Length / 3);
    }

    [Fact]
    public void Recover_Mound_SeedsThePeakAndAddsContoursUntilWithinTolerance()
    {
        IndexedTriMesh mound = Grid(40, (x, y) =>
        {
            double r = Math.Sqrt((x - 20) * (x - 20) + (y - 20) * (y - 20));
            return 10.0 * Math.Exp(-r * r / 120.0);
        });

        TerrainCurveRecovery.Result result = TerrainCurveRecovery.Recover(
            mound, new TerrainCurveRecovery.Options { VerticalTolerance = 0.25, PlanTolerance = 0.2 });

        Assert.Equal(TerrainCurveRecovery.Termination.ToleranceSatisfied, result.Termination);
        Assert.True(result.MaximumDeviation <= 0.25, $"{result.MaximumDeviation}");
        Assert.True(result.SpotPointCount >= 1, "the peak is a spot point");
        Assert.NotEmpty(result.Contours);
        Assert.True(result.Contours.Count < 30, $"{result.Contours.Count} contours");
        foreach (TerrainCurveRecovery.RecoveredContour contour in result.Contours)
        {
            for (int i = 2; i < contour.PointsXyz.Length; i += 3)
                Assert.Equal(contour.Level, contour.PointsXyz[i]);
        }
    }

    [Fact]
    public void Recover_CurveBudget_StopsAndSaysSo()
    {
        IndexedTriMesh mound = Grid(30, (x, y) => Math.Sin(x * 0.4) * Math.Cos(y * 0.4) * 3.0);

        TerrainCurveRecovery.Result result = TerrainCurveRecovery.Recover(
            mound, new TerrainCurveRecovery.Options { VerticalTolerance = 0.001, MaximumCurves = 2 });

        Assert.True(result.Contours.Count <= 2);
        Assert.NotEqual(TerrainCurveRecovery.Termination.ToleranceSatisfied, result.Termination);
    }

    [Fact]
    public void SimplifyOpen_DropsCollinearPoints()
    {
        double[] points = { 0, 0, 0, 1, 0, 0, 2, 0, 0, 3, 1, 0 };

        double[] simplified = TerrainCurveRecovery.SimplifyOpen(points, 0.01, double.PositiveInfinity);

        Assert.Equal(new double[] { 0, 0, 0, 2, 0, 0, 3, 1, 0 }, simplified);
    }

    [Fact]
    public void SimplifyClosed_KeepsAVerticalBreak()
    {
        // A flat square in plan whose one corner-free side carries a 1 m bump at its midpoint.
        double[] loop = { 0, 0, 0, 5, 0, 1, 10, 0, 0, 10, 10, 0, 0, 10, 0 };

        double[] simplified = TerrainCurveRecovery.SimplifyClosed(loop, 0.01, 0.1);

        Assert.Equal(5, simplified.Length / 3);
    }

    private static double Crease(double y) => 20.0 + 6.0 * Math.Sin(y * 0.15);

    [Fact]
    public void Recover_CreaseOnMeshEdges_ValleyLineSavesContours()
    {
        // A valley floor along the x = 20 grid column whose height undulates, as a breakline-built TIN would
        // have it: the border cannot describe it, and every contour would kink across it.
        IndexedTriMesh valley = Grid(40, (x, y) => 2.0 * Math.Sin(y * 0.2) + 0.3 * Math.Abs(x - 20));

        TerrainCurveRecovery.Result withLines = TerrainCurveRecovery.Recover(
            valley, new TerrainCurveRecovery.Options { VerticalTolerance = 0.05, PlanTolerance = 0.1 });
        TerrainCurveRecovery.Result withoutLines = TerrainCurveRecovery.Recover(
            valley, new TerrainCurveRecovery.Options { VerticalTolerance = 0.05, PlanTolerance = 0.1, LandformLines = false });

        Assert.Equal(TerrainCurveRecovery.Termination.ToleranceSatisfied, withLines.Termination);
        Assert.Contains(withLines.LandformLines, line =>
            Enumerable.Range(0, line.Length / 3).All(i => Math.Abs(line[i * 3] - 20.0) < 1e-9));
        Assert.True(withLines.Contours.Count + withLines.LandformLines.Count < withoutLines.Contours.Count,
            $"{withLines.LandformLines.Count} lines + {withLines.Contours.Count} contours vs {withoutLines.Contours.Count} contours");
    }

    [Fact]
    public void ExtractLandformLines_MeanderingRidge_FollowsTheCrest()
    {
        IndexedTriMesh ridge = Grid(40, (x, y) => 10.0 - 0.2 * Math.Abs(x - Crease(y)) + 0.05 * y);
        (double[] vertices, int vertexCount, int[] faces, int faceCount) = ridge;
        var adjacency = MeshVertexAdjacency.Build(new List<int>(faces), faceCount, vertexCount);
        var onBorder = new bool[vertexCount];
        for (int v = 0; v < vertexCount; v++)
            onBorder[v] = vertices[v * 3] == 0 || vertices[v * 3] == 40 || vertices[v * 3 + 1] == 0 || vertices[v * 3 + 1] == 40;

        List<double[]> lines = TerrainCurveRecovery.ExtractLandformLines(vertices, vertexCount, adjacency, onBorder);

        double[] longest = lines.OrderByDescending(line => line.Length).First();
        Assert.True(longest.Length / 3 >= 20, $"{longest.Length / 3} points");
        for (int i = 0; i < longest.Length; i += 3)
            Assert.True(Math.Abs(longest[i] - Crease(longest[i + 1])) <= 1.5, $"({longest[i]}, {longest[i + 1]}) is off the crest");
    }

    [Fact]
    public void Recover_PlainSlope_OffersNoLandformLines()
    {
        TerrainCurveRecovery.Result result = TerrainCurveRecovery.Recover(
            Grid(40, (x, y) => 0.1 * x + 0.03 * y), new TerrainCurveRecovery.Options { VerticalTolerance = 0.01 });

        Assert.Empty(result.LandformLines);
    }

    [Fact]
    public void Recover_SeveralContoursPerRound_StillMeetsTolerance()
    {
        IndexedTriMesh hills = Grid(60, (x, y) => 4.0 * Math.Sin(x * 0.15) * Math.Cos(y * 0.11) + 0.05 * x);

        TerrainCurveRecovery.Result single = TerrainCurveRecovery.Recover(
            hills, new TerrainCurveRecovery.Options { VerticalTolerance = 0.1, PlanTolerance = 0.2, ContoursPerRound = 1 });
        TerrainCurveRecovery.Result batched = TerrainCurveRecovery.Recover(
            hills, new TerrainCurveRecovery.Options { VerticalTolerance = 0.1, PlanTolerance = 0.2, ContoursPerRound = 4 });

        Assert.Equal(TerrainCurveRecovery.Termination.ToleranceSatisfied, batched.Termination);
        Assert.True(batched.MaximumDeviation <= 0.1);
        Assert.True(batched.Rounds < single.Rounds, $"{batched.Rounds} batched rounds vs {single.Rounds}");
    }
}
