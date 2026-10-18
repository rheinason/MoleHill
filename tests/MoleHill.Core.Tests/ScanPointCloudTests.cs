using MoleHill.Core.Scan;
using Xunit;

namespace MoleHill.Core.Tests;

public class ScanPointCloudTests
{
    private static ScanTerrainPipeline.Options Options(double scale = 1, bool buildings = false) => new()
    {
        Ground = new() { CellSize = .5 * scale, SmallestObject = 2 * scale, LargestObject = 24 * scale,
            InitialThreshold = .3 * scale, MaximumThreshold = 4 * scale, Margin = .5 * scale, MinimumObjectArea = 20 * scale * scale },
        GroundSpacing = scale, PlanTolerance = .25 * scale,
        Buildings = buildings ? new() { MinimumArea = 20 * scale * scale, MinimumHeight = 2 * scale,
            FootprintTolerance = .5 * scale, RoofTolerance = .3 * scale } : null,
    };

    [Fact]
    public void Rasterize_MultipleReturns_PreservesTopColourClassLowestAndGround()
    {
        var cloud = new ScanPointCloud(new double[] { 0, 0, 1, 0, 0, 7, 1, 1, 2 },
            new[] { 0x00ff00, 0xff0000, 0x777777 }, new byte[] { 2, 6, 2 });
        var result = cloud.Rasterize(1);
        Assert.Equal(7, result.Top[0, 0]); Assert.Equal(1, result.Lowest[0, 0]); Assert.Equal(1, result.ClassifiedGround[0, 0]);
        Assert.Equal(0xff0000, result.Colours![0]); Assert.Equal(6, result.Classes![0]);
        Assert.True(double.IsNaN(result.Top[1, 0]));
        Assert.True(result.RoofMesh(default).IsEmpty);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    public void Build_DenseGableCloud_RecoversGroundAndRoofInPhysicalUnits(double scale)
    {
        var xyz = new List<double>(); var rgb = new List<int>();
        for (int y = 0; y <= 80; y++) for (int x = 0; x <= 120; x++)
        {
            double px = x * .25, py = y * .25, z = 1 + .02 * py;
            bool roof = px >= 9 && px <= 21 && py >= 6 && py <= 14;
            if (roof) z = 6 + .5 * (4 - Math.Abs(py - 10));
            xyz.AddRange(new[] { px * scale, py * scale, z * scale }); rgb.Add(roof ? 0xaa5544 : 0x448833);
        }
        var cloud = new ScanPointCloud(xyz.ToArray(), rgb.ToArray());
        var result = ScanTerrainPipeline.Build(cloud, Options(scale, buildings: true));
        Assert.InRange(Math.Abs(result.Ground.Ground[30, 20] / scale - 1.2), 0, .08);
        var building = Assert.Single(result.Buildings!.Buildings);
        Assert.Equal(ScanRoofKind.Gable, building.RoofKind);
        Assert.InRange(Math.Abs(building.RidgeHeight / scale - 8), 0, .15);
        Assert.InRange(building.RoofWithinToleranceShare, .9, 1.00001);
    }

    [Fact]
    public void Build_ClassifiedGroundBelowRoof_UsesAnchorsWithoutTreatingRoofAsMeasuredGround()
    {
        var xyz = new List<double>(); var classes = new List<byte>();
        for (int y = 0; y <= 40; y++) for (int x = 0; x <= 60; x++)
        {
            bool roof = x >= 18 && x <= 42 && y >= 12 && y <= 28;
            double ground = 1 + .02 * y * .5;
            xyz.AddRange(new[] { x * .5, y * .5, ground }); classes.Add(2);
            if (roof) { xyz.AddRange(new[] { x * .5, y * .5, 7.0 }); classes.Add(6); }
        }
        var result = ScanTerrainPipeline.Build(new ScanPointCloud(xyz.ToArray(), classes: classes.ToArray()), Options(buildings: true));
        Assert.Equal(1.2, result.Ground.Ground[30, 20], 8);
        Assert.False(result.Ground.IsGround[20 * 61 + 30]);
        Assert.Equal(2501, result.Ground.ClassifiedGroundNodes);
        Assert.Single(result.Buildings!.Buildings);
    }

    [Fact]
    public void Build_SuppliedGroundDisagreesWithFilter_ReportsItAndKeepsSuppliedHeight()
    {
        var xyz = new List<double>(); var classes = new List<byte>();
        for (int y = 0; y <= 20; y++) for (int x = 0; x <= 20; x++)
        {
            xyz.AddRange(new[] { x * .5, y * .5, 3.0 }); classes.Add(0);
            xyz.AddRange(new[] { x * .5, y * .5, 1.0 }); classes.Add(2);
        }
        var result = ScanTerrainPipeline.Build(new ScanPointCloud(xyz.ToArray(), classes: classes.ToArray()), Options());
        Assert.Equal(441, result.Ground.ClassifiedGroundDisagreements);
        Assert.All(result.Ground.Ground.Values, height => Assert.Equal(1, height));
        Assert.All(result.Ground.IsGround, value => Assert.False(value));
    }

    [Fact]
    public void Build_ExplicitVegetationClass_RejectsEvenAPlanarCanopy()
    {
        var xyz = new List<double>(); var classes = new List<byte>();
        for (int y = 0; y <= 40; y++) for (int x = 0; x <= 60; x++)
        {
            bool canopy = x >= 18 && x <= 42 && y >= 12 && y <= 28;
            xyz.AddRange(new[] { x * .5, y * .5, canopy ? 7 : 1.0 }); classes.Add(canopy ? (byte)5 : (byte)2);
        }
        var result = ScanTerrainPipeline.Build(new ScanPointCloud(xyz.ToArray(), classes: classes.ToArray()), Options(buildings: true));
        Assert.Empty(result.Buildings!.Buildings); Assert.Equal(1, result.Buildings.RejectedVegetation);
    }

    [Fact]
    public void Build_StageCallbacks_AreOrderedAndDoNotMutateEarlierSnapshots()
    {
        var xyz = new List<double>(); var rgb = new List<int>();
        for (int y = 0; y <= 20; y++) for (int x = 0; x <= 20; x++)
        {
            xyz.AddRange(new[] { x * .5, y * .5, 1.0 });
            rgb.Add(x >= 8 && x <= 12 ? 0x777777 : 0x448833);
        }
        var snapshots = new List<ScanTerrainPipeline.Result>();
        var baseOptions = Options();
        var result = ScanTerrainPipeline.Build(new ScanPointCloud(xyz.ToArray(), rgb.ToArray()), new()
        {
            Ground = baseOptions.Ground, GroundSpacing = 1, PlanTolerance = .25, ReportPreview = snapshots.Add,
            Areas = new() { MaximumPavedRoughness = .1, MinimumArea = .1, PlanTolerance = .1, VerticalTolerance = .02 },
        });
        Assert.True(snapshots.Count >= 3);
        Assert.Same(result, snapshots[^1]);
        Assert.NotEmpty(result.FixedBreaklines);
        Assert.All(snapshots.Where(snapshot => snapshot.Areas == null), snapshot => Assert.Empty(snapshot.FixedBreaklines));
    }

    [Fact]
    public void Rasterize_TinyCellOnLongExtent_RejectsBeforeAllocation()
    {
        var cloud = new ScanPointCloud(new double[] { 0, 0, 0, 1e12, 1, 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => cloud.Rasterize(.001));
        var raster = cloud.Rasterize(cloud.AutomaticCellSize());
        Assert.InRange(raster.Top.NodeCount, 1, (int)ScanRasterizer.MaximumNodeCount);
    }

    [Fact]
    public void Rasterize_Cancelled_ThrowsWithoutPartialResults()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => new ScanPointCloud(new double[] { 0, 0, 0 }).Rasterize(1, cancel.Token));
    }

    [Fact]
    public void Constructor_InvalidArrays_RejectsBadCoordinatesAndAlignment()
    {
        Assert.Throws<ArgumentException>(() => new ScanPointCloud(new[] { double.NaN, 0.0, 0.0 }));
        Assert.Throws<ArgumentException>(() => new ScanPointCloud(new double[2]));
        Assert.Throws<ArgumentException>(() => new ScanPointCloud(new double[3], Array.Empty<int>()));
        Assert.Throws<ArgumentException>(() => new ScanPointCloud(new double[3], classes: Array.Empty<byte>()));
    }
}
