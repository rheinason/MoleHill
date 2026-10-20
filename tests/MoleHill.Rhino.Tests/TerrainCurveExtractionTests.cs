using MoleHill.Core.Engine;
using MoleHill.Core.Scan;
using MoleHill.Rhino.Services;
using MoleHill.Shared;
using Rhino;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class TerrainCurveExtractionTests
{
    private static IndexedTriMesh Mound()
    {
        const int n = 17;
        var xyz = new double[n * n * 3]; var faces = new List<int>();
        for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
        {
            int i = y * n + x; xyz[i * 3] = x; xyz[i * 3 + 1] = y;
            xyz[i * 3 + 2] = 7 * Math.Exp(-((x - 8) * (x - 8) + (y - 8) * (y - 8)) / 20.0);
            if (x + 1 < n && y + 1 < n) faces.AddRange(new[] { i, i + 1, i + n + 1, i, i + n + 1, i + n });
        }
        return IndexedTriMesh.FromArrays(xyz, faces.ToArray());
    }
    private static TerrainCurveExtractionSettings Settings() => new() { Tolerance = .25, PlanTolerance = .1, ContourInterval = .5 };
    private static ModelUnitContext Units => ModelUnitContext.FromUnitSystem(UnitSystem.Meters);

    [Fact]
    public async Task RecoverAsync_PreparedScanAndDirectTerrainWithSameReference_OnlyTheTerrainHoldsItsCreases()
    {
        var scan = ScanTerrainPipeline.Build(Mound(), null, new()
        {
            Ground = new()
            {
                CellSize = 1,
                SmallestObject = 1,
                LargestObject = 4,
                InitialThreshold = .3,
                MaximumThreshold = 4,
                Margin = 0
            },
            GroundSpacing = 1,
            PlanTolerance = .1
        });
        var fromScan = TerrainCurveExtractionPrepared.FromScan(scan);
        var direct = new TerrainCurveExtractionPrepared(scan.Reference) { FixedBreaklines = fromScan.FixedBreaklines, Region = fromScan.Region };
        var a = await TerrainCurveExtractionService.RecoverAsync(fromScan, Settings(), Units, default, _ => { });
        var b = await TerrainCurveExtractionService.RecoverAsync(direct, Settings(), Units, default, _ => { });
        // A scan's walls are its measured rails and its folds noise; a terrain's folds are design, held as creases.
        Assert.NotEmpty(a.Recovery!.Contours);
        Assert.Empty(a.Recovery.CreaseLines);
        Assert.NotEmpty(b.Recovery!.CreaseLines);
        Assert.InRange(a.Recovery.MaximumDeviation, 0, .25);
        Assert.InRange(b.Recovery.MaximumDeviation, 0, .25);
        Assert.Contains(b.Recovery.Contours, c => c.Curve != null);
        foreach (var contour in b.Recovery.Contours)
        {
            Assert.All(contour.PointsXyz.Where((_, i) => i % 3 == 2), z => Assert.Equal(contour.Level, z));
            if (contour.Curve != null)
                foreach (var piece in contour.Curve.Pieces)
                    Assert.All(piece.ControlPoints.Where((_, i) => i % 3 == 2), z => Assert.Equal(contour.Level, z));
        }
    }

    [Fact]
    public async Task RecoverAsync_UnpreparedTerrain_KeepsItsPeakAndDoesNotRunScanFiltering()
    {
        var mesh = Mound(); var before = (double[])mesh.Vertices.Clone();
        var result = await TerrainCurveExtractionService.RecoverAsync(new(mesh), Settings(), Units, default, _ => { });
        Assert.Null(result.Scan); Assert.Equal(before, result.Reference.Vertices);
        // Unfiltered, the peak is measured like the rest of the terrain.
        Assert.InRange(result.Recovery!.MaximumDeviation, 0, .25);
    }

    [Fact]
    public async Task RecoverAsync_Cancelled_DoesNotReturnAcceptablePartialOutput()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TerrainCurveExtractionService.RecoverAsync(new(Mound()), Settings(), Units, cancel.Token, _ => { }));
    }

    [Fact]
    public void Settings_CacheNamespacesAndModelUnits_PreservePhysicalAccuracyWithoutSharingCaps()
    {
        string key = "ExtractionTest." + Guid.NewGuid() + ".";
        var settings = TerrainCurveExtractionSettings.Load(Units, key);
        settings.Tolerance = .2; settings.PlanTolerance = .4; settings.ContourInterval = .3; settings.InputsPerHectare = 77; settings.Save(Units, key);
        var millimetres = TerrainCurveExtractionSettings.Load(ModelUnitContext.FromUnitSystem(UnitSystem.Millimeters), key);
        Assert.Equal(200, millimetres.Tolerance); Assert.Equal(400, millimetres.PlanTolerance);
        Assert.Equal(300, millimetres.ContourInterval, 9); Assert.Equal(77, millimetres.InputsPerHectare);
        var other = TerrainCurveExtractionSettings.Load(Units, key + "Other.");
        Assert.Equal(.1, other.Tolerance); Assert.Equal(.25, other.ContourInterval); Assert.Equal(1000, other.InputsPerHectare);
    }

    [Fact]
    public void Settings_SnapshotThroughSharedType_RetainsScanPreparationAndDoesNotMutateTheOpenForm()
    {
        TerrainCurveExtractionSettings original = ScanToTerrainSettings.Load(Units);
        var snapshot = original with { Tolerance = .7 };
        Assert.IsType<ScanToTerrainSettings>(snapshot);
        Assert.NotEqual(snapshot.Tolerance, original.Tolerance);
        Assert.Equal(((ScanToTerrainSettings)original).RoofTolerance, ((ScanToTerrainSettings)snapshot).RoofTolerance);
    }

    [Theory]
    [InlineData(UnitSystem.Meters, 1.0)]
    [InlineData(UnitSystem.Millimeters, 1000.0)]
    public void ScanSettings_CleanupControls_UsePhysicalScaleAndSeparateMeasuredKerbAccuracy(UnitSystem system, double scale)
    {
        var units = ModelUnitContext.FromUnitSystem(system);
        var settings = ScanToTerrainSettings.Load(units) with
        {
            SurfaceArea = 12 * scale * scale,
            PlanTolerance = 1.2 * scale,
            Tolerance = .2 * scale,
            BuiltEdgeTolerance = 2 * scale,
            RoadHeightTolerance = .08 * scale,
        };
        var pipeline = settings.Pipeline(units, default, _ => { }, _ => { });
        Assert.Equal(12 * scale * scale, pipeline.Areas!.MinimumArea);
        Assert.Equal(1.2 * scale, pipeline.Buildings!.FootprintTolerance);
        Assert.Equal(.015 * scale, pipeline.Roads!.VerticalTolerance);
        Assert.Equal(2 * scale, pipeline.Roads.PlanTolerance);
        Assert.Equal(.08 * scale, pipeline.Roads.RoadVerticalTolerance);
    }
}
