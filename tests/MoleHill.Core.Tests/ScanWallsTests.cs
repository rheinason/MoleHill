using MoleHill.Core.Scan;
using MoleHill.Core.Processing;
using MoleHill.Core.Analysis;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class ScanWallsTests
{
    private static ScanGrid Grid(Func<double, double, double> height, double scale = 1)
    {
        const int size = 121;
        var values = new double[size * size];
        for (int row = 0; row < size; row++)
            for (int column = 0; column < size; column++)
                values[row * size + column] = height(column * 0.25, row * 0.25) * scale;
        return new ScanGrid(0, 0, 0.25 * scale, size, size, values);
    }

    private static ScanWalls.Options Options(double scale = 1) => new()
    {
        MinimumHeight = 0.6 * scale,
        MinimumLength = 3 * scale,
        PlanTolerance = 0.1 * scale,
        VerticalTolerance = 0.05 * scale,
        BorderMargin = 1 * scale,
        ReportRejected = Console.WriteLine,
    };

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    public void Trace_TerraceOnSlope_EmitsMeasuredTopAndToeInModelUnits(double scale)
    {
        ScanGrid grid = Grid((x, y) => 0.3 * x + 0.1 * y + (x >= 15 ? 1.5 : 0), scale);
        ScanWalls.Wall wall = Assert.Single(ScanWalls.Trace(grid, Options(scale)));
        Assert.InRange(wall.MeanHeight / scale, 1.499, 1.501);
        Assert.Equal(26.75, wall.Length / scale, 8);
        Assert.All(Enumerable.Range(0, wall.Top.Length / 3), i =>
        {
            double x = wall.Top[i * 3] / scale, y = wall.Top[i * 3 + 1] / scale;
            Assert.InRange(x, 14.99, 15.01);
            Assert.InRange(wall.Top[i * 3 + 2] / scale - (0.3 * x + 0.1 * y + 1.5), -0.051, 0.051);
        });
        Assert.All(Enumerable.Range(0, wall.Toe.Length / 3), i => Assert.InRange(wall.Toe[i * 3] / scale, 14.74, 14.76));
        Assert.True(wall.TopCurve.ControlPointCount < 12);
    }

    [Fact]
    public void Trace_SteepPlaneSmoothHillAndShortSpike_EmitsNothing()
    {
        Assert.Empty(ScanWalls.Trace(Grid((x, y) => 2 * x - y), Options()));
        Assert.Empty(ScanWalls.Trace(Grid((x, y) => 4 * Math.Exp(-((x - 15) * (x - 15) + (y - 15) * (y - 15)) / 30)), Options()));
        Assert.Empty(ScanWalls.Trace(Grid((x, y) => x == 15 && y == 15 ? 3 : 0), Options()));
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void Trace_WallSpreadAcrossRasterCells_EmitsOneFullHeightPair(double width)
    {
        ScanWalls.Wall wall = Assert.Single(ScanWalls.Trace(
            Grid((x, y) => 0.1 * y + 1.5 * Math.Clamp((x - 15) / width, 0, 1)), Options()));
        Assert.InRange(wall.MeanHeight, 1.49, 1.51);
        Assert.All(wall.Top.Where((_, i) => i % 3 == 0), x => Assert.InRange(x, 15 + width - 0.01, 15 + width + 0.01));
        Assert.All(wall.Toe.Where((_, i) => i % 3 == 0), x => Assert.InRange(x, 14.99, 15.01));
    }

    [Fact]
    public void Trace_DataGap_DoesNotJoinAcrossMissingGround()
    {
        ScanGrid grid = Grid((x, y) => y > 12 && y < 18 ? double.NaN : (x >= 15 ? 1.5 : 0));
        IReadOnlyList<ScanWalls.Wall> walls = ScanWalls.Trace(grid, Options());
        Assert.Equal(2, walls.Count);
        Assert.All(walls, wall => Assert.True(wall.Top.Where((_, i) => i % 3 == 1).All(y => y <= 12) ||
                                                wall.Top.Where((_, i) => i % 3 == 1).All(y => y >= 18)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Trace_HorizontalWallInEitherDirection_KeepsTheHighRailAboveTheToe(bool reverse)
    {
        IReadOnlyList<ScanWalls.Wall> walls = ScanWalls.Trace(
            Grid((x, y) => (reverse ? -1 : 1) * (y >= 15 ? 2 : 0)), Options());
        ScanWalls.Wall wall = Assert.Single(walls);
        Assert.True(wall.Top.Where((_, i) => i % 3 == 2).Average() > wall.Toe.Where((_, i) => i % 3 == 2).Average() + 1.9);
    }

    [Fact]
    public void Trace_DiagonalTerrace_KeepsOneLongPairedWall()
    {
        IReadOnlyList<ScanWalls.Wall> walls = ScanWalls.Trace(Grid((x, y) => x >= y ? 2 : 0), Options());
        ScanWalls.Wall wall = Assert.Single(walls);
        Assert.InRange(wall.MeanHeight, 1.99, 2.01);
        Assert.True(wall.TopCurve.ControlPointCount < 20, $"controls: {wall.TopCurve.ControlPointCount}");
    }

    [Fact]
    public void Trace_SmearedDiagonalStep_TracesOneWallNotFragments()
    {
        // Row and column scans each keep the first crossing of a smeared step, a band apart on a 45-degree wall, so
        // the dual-edge chain broke every cell or two: Vardin's quay and road-edge walls were all lost that way.
        // Joined, the row and column pieces each make the whole wall, and only one copy may be kept.
        IReadOnlyList<ScanWalls.Wall> walls = ScanWalls.Trace(
            Grid((x, y) => 1.5 * Math.Clamp(((x - y) / Math.Sqrt(2) + 0.3) / 0.6, 0, 1)), Options() with { MaximumWidth = 1 });
        ScanWalls.Wall wall = Assert.Single(walls);
        Assert.True(wall.Length > 30, $"length {wall.Length:F1}");
        Assert.InRange(wall.MeanHeight, 1.3, 1.6);
    }

    [Fact]
    public void Trace_RectangularTerrace_ClosesBothRailsWithoutAHeightSpike()
    {
        ScanWalls.Wall wall = Assert.Single(ScanWalls.Trace(Grid((x, y) => x >= 8 && x <= 22 && y >= 8 && y <= 22 ? 2 : 0), Options()));
        Assert.Equal(wall.Top.Take(3), wall.Top.TakeLast(3));
        Assert.Equal(wall.Toe.Take(3), wall.Toe.TakeLast(3));
        Assert.All(wall.Top.Where((_, i) => i % 3 == 2), z => Assert.InRange(z, 1.99, 2.01));
        Assert.All(wall.Toe.Where((_, i) => i % 3 == 2), z => Assert.InRange(z, -0.01, 0.01));
    }

    [Fact]
    public void Trace_ClosedTerraceWithRollingProfile_RetainsLongitudinalHeightVariation()
    {
        double Ground(double x, double y) => .12 * Math.Sin(y * .8) + .08 * Math.Sin(x * .6);
        var wall = Assert.Single(ScanWalls.Trace(Grid((x, y) => Ground(x, y) + (x >= 8 && x <= 22 && y >= 8 && y <= 22 ? 2 : 0)), Options()));
        foreach (var piece in wall.TopCurve.Pieces)
            for (int i = 0; i <= 30; i++) { var p = piece.PointAt(i / 30.0); Assert.InRange(Math.Abs(p.Z - Ground(p.X, p.Y) - 2), 0, .055); }
    }

    [Fact]
    public void ConstrainReference_InvalidRail_RejectsBeforeTriangulating()
    {
        IndexedTriMesh mesh = ScanGroundMesher.Mesh(Grid((x, y) => x * 0.1), 1);
        Assert.Throws<ArgumentException>(() => TerrainCurveRecovery.ConstrainReference(mesh,
            new[] { new[] { 1.0, 2, double.NaN, 3, 4, 5 } }, 0.1, out _));
    }

    [Fact]
    public void Recover_MeasuredWallRails_HoldsThemInReferenceAndEveryRebuild()
    {
        ScanGrid grid = Grid((x, y) => 0.1 * y + (x >= 15 ? 1.5 : 0));
        ScanWalls.Wall wall = Assert.Single(ScanWalls.Trace(grid, Options()));
        double[][] rails = { wall.Top, wall.Toe };
        IndexedTriMesh coarse = ScanGroundMesher.Mesh(grid, 1);
        IndexedTriMesh? constrained = TerrainCurveRecovery.ConstrainReference(coarse, rails, 0.1, out string? error);
        Assert.True(constrained.HasValue, error);
        Assert.True(MeshBoundaryLoopBuilder.TryBuildBoundaryLoopsIndexed(constrained.Value.Faces, constrained.Value.FaceCount, out List<int[]> loops));
        Assert.Single(loops);
        var projector = new MeshHeightProjector(constrained.Value.Vertices, constrained.Value.VertexCount,
            constrained.Value.Faces, constrained.Value.FaceCount);
        foreach (double[] rail in rails)
        {
            for (int i = 0; i < rail.Length; i += 3)
            {
                Assert.True(projector.TryGetHeightExtremes(rail[i], rail[i + 1], 1e-7, out _, out double z));
                Assert.InRange(z - rail[i + 2], -1e-6, 1e-6);
            }
        }
        for (int y = 2; y < 28; y++)
        {
            Assert.True(projector.TryGetHeightExtremes(15, y, 1e-7, out _, out double z));
            Assert.InRange(z - (0.1 * y + 1.5), -1e-6, 1e-6);
        }
        TerrainCurveRecovery.Result curves = TerrainCurveRecovery.Recover(constrained.Value, new()
        {
            VerticalTolerance = 0.1,
            PlanTolerance = 0.1,
            LandformLines = false,
            FixedBreaklines = rails,
            MaximumCurves = 50,
        });
        Assert.Equal(TerrainCurveRecovery.Termination.ToleranceSatisfied, curves.Termination);
        Assert.InRange(curves.MaximumDeviation, 0, 0.1);
    }
}
