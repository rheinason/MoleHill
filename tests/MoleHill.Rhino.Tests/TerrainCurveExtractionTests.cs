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
    private static TerrainCurveExtractionSettings Settings() => new() { Tolerance = .25, PlanTolerance = .1, MaxContours = 50, RidgesValleys = 2 };
    private static ModelUnitContext Units => ModelUnitContext.FromUnitSystem(UnitSystem.Meters);

    [Fact]
    public async Task RecoverAsync_PreparedScanAndDirectTerrainWithSameReference_ProduceIdenticalCurves()
    {
        var scan = ScanTerrainPipeline.Build(Mound(), null, new() { Ground = new() { CellSize = 1, SmallestObject = 1,
            LargestObject = 4, InitialThreshold = .3, MaximumThreshold = 4, Margin = 0 }, GroundSpacing = 1, PlanTolerance = .1 });
        var fromScan = TerrainCurveExtractionPrepared.FromScan(scan);
        var direct = new TerrainCurveExtractionPrepared(scan.Reference) { FixedBreaklines = fromScan.FixedBreaklines, Region = fromScan.Region };
        var a = await TerrainCurveExtractionService.RecoverAsync(fromScan, Settings(), Units, default, _ => { });
        var b = await TerrainCurveExtractionService.RecoverAsync(direct, Settings(), Units, default, _ => { });
        Assert.NotEmpty(a.Recovery!.Contours);
        Assert.Equal(a.Recovery.MaximumDeviation, b.Recovery!.MaximumDeviation);
        Assert.Equal(a.Recovery.Contours.SelectMany(c => c.PointsXyz), b.Recovery.Contours.SelectMany(c => c.PointsXyz));
        Assert.Equal(a.Recovery.SpotPoints, b.Recovery.SpotPoints);
        Assert.Contains(b.Recovery.Contours, c => c.Curve != null);
    }

    [Fact]
    public async Task RecoverAsync_UnpreparedTerrain_KeepsItsPeakAndDoesNotRunScanFiltering()
    {
        var mesh = Mound(); var before = (double[])mesh.Vertices.Clone();
        var result = await TerrainCurveExtractionService.RecoverAsync(new(mesh), Settings(), Units, default, _ => { });
        Assert.Null(result.Scan); Assert.Equal(before, result.Reference.Vertices);
        Assert.InRange(result.Recovery!.MaximumDeviation, 0, .25);
        Assert.True(result.Recovery.SpotPoints.Where((_, i) => i % 3 == 2).Max() > 6.9);
    }

    [Fact]
    public async Task RecoverAsync_Cancelled_DoesNotReturnAcceptablePartialOutput()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TerrainCurveExtractionService.RecoverAsync(new(Mound()), Settings(), Units, cancel.Token, _ => { }));
    }

    [Fact]
    public void Settings_CacheNamespacesAndModelUnits_PreservePhysicalAccuracyWithoutSharingBudgets()
    {
        string key = "ExtractionTest." + Guid.NewGuid() + ".";
        var settings = TerrainCurveExtractionSettings.Load(Units, key);
        settings.Tolerance = .2; settings.PlanTolerance = .4; settings.MaxContours = 77; settings.Save(Units, key);
        var millimetres = TerrainCurveExtractionSettings.Load(ModelUnitContext.FromUnitSystem(UnitSystem.Millimeters), key);
        Assert.Equal(200, millimetres.Tolerance); Assert.Equal(400, millimetres.PlanTolerance); Assert.Equal(77, millimetres.MaxContours);
        var other = TerrainCurveExtractionSettings.Load(Units, key + "Other.");
        Assert.Equal(.1, other.Tolerance); Assert.Equal(200, other.MaxContours);
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
}
