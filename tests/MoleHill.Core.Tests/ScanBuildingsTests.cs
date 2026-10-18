using MoleHill.Core.Analysis;
using MoleHill.Core.Engine;
using MoleHill.Core.Scan;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class ScanBuildingsTests
{
    private static ScanBuildings.Options Options(double scale = 1) => new()
    {
        MinimumArea = 20 * scale * scale, MinimumHeight = 2 * scale,
        FootprintTolerance = .5 * scale, RoofTolerance = .2 * scale,
    };

    private static (IndexedTriMesh Scan, ScanGround.Result Ground, int[] Colours) Scene(
        Func<double, double, double> roof, Func<double, double, bool>? footprint = null,
        double angle = 0, double scale = 1, double originX = 0, double originY = 0, bool green = false, double step = .25)
    {
        footprint ??= (u, v) => Math.Abs(u) <= 6 && Math.Abs(v) <= 4;
        int size = (int)Math.Round(20 / step) + 1;
        double cell = step * scale, cosine = Math.Cos(angle), sine = Math.Sin(angle);
        var surface = new double[size * size]; var ground = new double[surface.Length];
        var known = new bool[surface.Length]; var colours = new int[surface.Length];
        var xyz = new double[surface.Length * 3]; var faces = new List<int>();
        for (int j = 0; j < size; j++)
            for (int i = 0; i < size; i++)
            {
                int node = j * size + i;
                double x = i * step - 10, y = j * step - 10;
                double u = x * cosine + y * sine, v = -x * sine + y * cosine;
                ground[node] = (.02 * y + 1) * scale;
                known[node] = !footprint(u, v);
                surface[node] = known[node] ? ground[node] : roof(u, v) * scale;
                colours[node] = green ? 0x328432 : 0xA06040;
                xyz[node * 3] = originX + (x + 10) * scale;
                xyz[node * 3 + 1] = originY + (y + 10) * scale;
                xyz[node * 3 + 2] = surface[node];
                if (i + 1 < size && j + 1 < size)
                    faces.AddRange(new[] { node, node + 1, node + size + 1, node, node + size + 1, node + size });
            }
        var top = new ScanGrid(originX, originY, cell, size, size, surface);
        return (IndexedTriMesh.FromArrays(xyz, faces.ToArray()), new ScanGround.Result
        {
            Surface = top, Ground = top.WithValues(ground), Domain = Enumerable.Repeat(true, known.Length).ToArray(),
            IsGround = known, ObjectCount = 1, StepCount = 0, DomainArea = 400 * scale * scale,
            FilledArea = known.Count(value => !value) * cell * cell,
        }, colours);
    }

    [Theory]
    [InlineData(ScanRoofKind.Flat)]
    [InlineData(ScanRoofKind.Shed)]
    [InlineData(ScanRoofKind.Gable)]
    [InlineData(ScanRoofKind.Hip)]
    [InlineData(ScanRoofKind.HalfHip)]
    public void Extract_CanonicalRoof_RecoversTheRoofAsAWatertightVolume(ScanRoofKind kind)
    {
        double Roof(double u, double v) => kind switch
        {
            ScanRoofKind.Flat => 6,
            ScanRoofKind.Shed => 6 + .2 * v,
            ScanRoofKind.Gable => 6 + .5 * (4 - Math.Abs(v)),
            ScanRoofKind.Hip => 6 + .5 * Math.Min(4 - Math.Abs(v), 6 - Math.Abs(u)),
            _ => 6 + .5 * Math.Min(4 - Math.Abs(v), 2 + 6 - Math.Abs(u)),
        };
        var scene = Scene(Roof);
        ScanBuildings.Result result = ScanBuildings.Extract(scene.Scan, scene.Ground, scene.Colours, Options());
        ScanBuildings.Building building = Assert.Single(result.Buildings);
        Assert.Equal(kind, building.RoofKind);
        Assert.False(building.IsFallback);
        Assert.InRange(building.RoofRms, 0, .05);
        Assert.InRange(building.FootprintArea, 90, 105);
        Assert.InRange(building.GroundHeight, .90, .95);
        AssertWatertight(building.Solid);
        Assert.Equal(kind switch { ScanRoofKind.Flat or ScanRoofKind.Shed => 6, ScanRoofKind.Gable => 7, _ => 9 }, building.PlanarFaces.Count);
        var projector = new MeshHeightProjector(building.Solid.Vertices, building.Solid.VertexCount, building.Solid.Faces, building.Solid.FaceCount);
        for (double v = -3.5; v <= 3.5; v += .5)
            for (double u = -5.5; u <= 5.5; u += .5)
            {
                Assert.True(projector.TryGetHeightExtremes(u + 10, v + 10, .001, out double low, out double high));
                Assert.Equal(building.GroundHeight, low, 6);
                Assert.InRange(Math.Abs(high - Roof(u, v)), 0, .05);
            }
    }

    [Fact]
    public void Extract_CrossGableOnLFootprint_RetainsTheConcavityAndBothRidges()
    {
        bool Footprint(double u, double v) => Math.Abs(u) <= 6 && Math.Abs(v) <= 4 && (u <= -2 || v <= 0);
        double Roof(double u, double v) => 6 + .5 * Math.Max(2 - Math.Abs(u + 4), 2 - Math.Abs(v + 2));
        var scene = Scene(Roof, Footprint);
        var result = ScanBuildings.Extract(scene.Scan, scene.Ground, scene.Colours, Options());
        var building = Assert.Single(result.Buildings);
        Assert.Equal(ScanRoofKind.CrossGable, building.RoofKind);
        Assert.True(building.Footprints[0].Length >= 18);
        Assert.InRange(building.FootprintArea, 60, 72);
        AssertWatertight(building.Solid);
        var projector = new MeshHeightProjector(building.Solid.Vertices, building.Solid.VertexCount, building.Solid.Faces, building.Solid.FaceCount);
        Assert.False(projector.TryGetHeightExtremes(12, 12, .001, out _, out _));
        Assert.True(projector.TryGetHeightExtremes(6, 12, .001, out _, out double ridge));
        Assert.InRange(ridge, 6.99, 7.01);
    }

    [Theory]
    [InlineData(1.0, 0.0, 0.0)]
    [InlineData(1000.0, 0.0, 0.0)]
    [InlineData(1.0, 542000.0, 6789000.0)]
    public void Extract_RotatedGableAtDifferentUnitsAndOrigins_PreservesShape(double scale, double x, double y)
    {
        double angle = 27 * Math.PI / 180;
        var scene = Scene((u, v) => 6 + .5 * (4 - Math.Abs(v)), angle: angle, scale: scale, originX: x, originY: y);
        var building = Assert.Single(ScanBuildings.Extract(scene.Scan, scene.Ground, null, Options(scale)).Buildings);
        Assert.Equal(ScanRoofKind.Gable, building.RoofKind);
        Assert.InRange(building.RidgeHeight / scale, 7.9, 8.1);
        Assert.InRange(building.RoofRms / scale, 0, .1);
        Assert.InRange(building.FootprintArea / (scale * scale), 90, 105);
        AssertWatertight(building.Solid);
    }

    [Fact]
    public void Extract_GreenRaisedCanopy_RejectsVegetation()
    {
        var scene = Scene((u, v) => 6 + Math.Sin(u * 4) * Math.Cos(v * 3), green: true);
        ScanBuildings.Result result = ScanBuildings.Extract(scene.Scan, scene.Ground, scene.Colours, Options());
        Assert.Empty(result.Buildings);
        Assert.Equal(1, result.RejectedVegetation);
    }

    [Fact]
    public void Extract_PlanarGreenTurfRoof_DoesNotRejectABuildingForItsColour()
    {
        var scene = Scene((u, v) => 6 + .5 * (4 - Math.Abs(v)), green: true);
        var building = Assert.Single(ScanBuildings.Extract(scene.Scan, scene.Ground, scene.Colours, Options()).Buildings);
        Assert.Equal(ScanRoofKind.Gable, building.RoofKind);
        AssertWatertight(building.Solid);
    }

    [Fact]
    public void Extract_SmoothDomeWithoutColour_RejectsACollectionOfSmallTangentPatches()
    {
        var scene = Scene((u, v) => 6 + 3 * Math.Sqrt(Math.Max(0, 1 - (u * u + v * v) / 25)),
            (u, v) => u * u + v * v < 25);
        ScanBuildings.Result result = ScanBuildings.Extract(scene.Scan, scene.Ground, null, Options());
        Assert.Empty(result.Buildings);
    }

    [Fact]
    public void Extract_ConcaveRoofEnvelope_UsesMeasuredPlaneArrangement()
    {
        double Roof(double u, double v) => 6 + Math.Max(.5 * (4 - Math.Abs(v)), 1.5);
        var scene = Scene(Roof);
        var building = Assert.Single(ScanBuildings.Extract(scene.Scan, scene.Ground, null, Options()).Buildings);
        Assert.Equal(ScanRoofKind.PlaneArrangement, building.RoofKind);
        Assert.False(building.IsFallback);
        Assert.InRange(building.RoofRms, 0, .05);
        AssertWatertight(building.Solid);
        var projector = new MeshHeightProjector(building.Solid.Vertices, building.Solid.VertexCount, building.Solid.Faces, building.Solid.FaceCount);
        for (double v = -3.5; v <= 3.5; v += .5)
        {
            Assert.True(projector.TryGetHeightExtremes(10, v + 10, .001, out _, out double high));
            Assert.InRange(Math.Abs(high - Roof(0, v)), 0, .05);
        }
    }

    [Fact]
    public void Extract_InsufficientRoofModelBudget_FlagsMedianExtrusionFallback()
    {
        var scene = Scene((u, v) => u < 4 ? 6 : 9 + .4 * v);
        var options = new ScanBuildings.Options
        {
            MinimumArea = 20, MinimumHeight = 2, FootprintTolerance = .5, RoofTolerance = .2, MaximumPlanes = 1,
        };
        var building = Assert.Single(ScanBuildings.Extract(scene.Scan, scene.Ground, null, options).Buildings);
        Assert.True(building.IsFallback);
        Assert.Contains("fallback", building.Diagnostic);
        Assert.InRange(building.RidgeHeight, 5.99, 6.01);
        Assert.True(building.RoofRms > .2);
        AssertWatertight(building.Solid);
    }

    [Fact]
    public void Extract_TwoRoofLevels_ReconstructsTheirStepAsAClosedArrangement()
    {
        var scene = Scene((u, v) => u < 4 ? 6 : 9 + .4 * v);
        var building = Assert.Single(ScanBuildings.Extract(scene.Scan, scene.Ground, null, Options()).Buildings);
        Assert.Equal(ScanRoofKind.PlaneArrangement, building.RoofKind);
        Assert.InRange(building.RoofRms, 0, .05);
        AssertWatertight(building.Solid);
        var projector = new MeshHeightProjector(building.Solid.Vertices, building.Solid.VertexCount, building.Solid.Faces, building.Solid.FaceCount);
        Assert.True(projector.TryGetHeightExtremes(10, 10, .001, out _, out double lowRoof));
        Assert.Equal(6, lowRoof, 6);
        Assert.True(projector.TryGetHeightExtremes(15, 10, .001, out _, out double highRoof));
        Assert.Equal(9, highRoof, 6);
    }

    [Fact]
    public void Extract_CourtyardBuilding_RetainsTheHoleInTheClosedSolid()
    {
        var scene = Scene((u, v) => 6,
            (u, v) => Math.Abs(u) <= 6 && Math.Abs(v) <= 4 && (Math.Abs(u) >= 1.5 || Math.Abs(v) >= 1.5));
        var building = Assert.Single(ScanBuildings.Extract(scene.Scan, scene.Ground, null, Options()).Buildings);
        Assert.Equal(2, building.Footprints.Count);
        AssertWatertight(building.Solid);
        var projector = new MeshHeightProjector(building.Solid.Vertices, building.Solid.VertexCount, building.Solid.Faces, building.Solid.FaceCount);
        Assert.False(projector.TryGetHeightExtremes(10, 10, .001, out _, out _));
        Assert.True(projector.TryGetHeightExtremes(6, 10, .001, out _, out double high));
        Assert.Equal(6, high, 6);
    }

    [Fact]
    public void Extract_DiagonallyTouchingCourtyards_ClosesTheNonManifoldContact()
    {
        bool Footprint(double u, double v) => Math.Abs(u) <= 6 && Math.Abs(v) <= 4 &&
            !(u >= -2 && u <= 0 && v >= -2 && v <= 0) && !(u >= .25 && u <= 2.25 && v >= .25 && v <= 2.25);
        var scene = Scene((u, v) => 6, Footprint);
        var building = Assert.Single(ScanBuildings.Extract(scene.Scan, scene.Ground, null, Options()).Buildings);
        Assert.Equal(ScanRoofKind.Flat, building.RoofKind);
        AssertWatertight(building.Solid);
    }

    [Fact]
    public void Extract_NoisyGable_RecoversTheMeasuredPlanes()
    {
        var scene = Scene((u, v) => 6 + .5 * (4 - Math.Abs(v)) + .025 * Math.Sin(u * 2.3) * Math.Cos(v * 3.1));
        var building = Assert.Single(ScanBuildings.Extract(scene.Scan, scene.Ground, null, Options()).Buildings);
        Assert.Equal(ScanRoofKind.Gable, building.RoofKind);
        Assert.InRange(building.PlanarShare, .9, 1.000001);
        Assert.InRange(building.RoofRms, 0, .05);
        AssertWatertight(building.Solid);
    }

    [Fact]
    public void Extract_FlatRoofWithDrainageFallAndNoise_RetainsTheFlatRoofKind()
    {
        var scene = Scene((u, v) => 6 + .01 * u + .025 * Math.Sin(u * 2.3) * Math.Cos(v * 3.1));
        var building = Assert.Single(ScanBuildings.Extract(scene.Scan, scene.Ground, null, Options()).Buildings);
        Assert.Equal(ScanRoofKind.Flat, building.RoofKind);
        Assert.InRange(building.RoofRms, 0, .05);
        Assert.Equal(6, building.PlanarFaces.Count);
        AssertWatertight(building.Solid);
    }

    [Fact]
    public void Extract_RoughCanopyWithoutColour_RejectsNonPlanarGeometry()
    {
        var scene = Scene((u, v) => 6 + Math.Sin(u * 4) * Math.Cos(v * 3));
        ScanBuildings.Result result = ScanBuildings.Extract(scene.Scan, scene.Ground, null, Options());
        Assert.Empty(result.Buildings);
        Assert.Equal(1, result.RejectedGeometry);
    }

    [Fact]
    public void Build_BuildingsEnabled_ProducesContextWithoutChangingTerrainInputs()
    {
        var scene = Scene((u, v) => 6 + .5 * (4 - Math.Abs(v)));
        ScanTerrainPipeline.Options Pipeline(bool buildings) => new()
        {
            Ground = new() { CellSize = .25, SmallestObject = 2, LargestObject = 48, InitialThreshold = .3, MaximumThreshold = 4, Margin = .5, MinimumObjectArea = 20 },
            GroundSpacing = 1, PlanTolerance = .5, Buildings = buildings ? Options() : null,
        };
        ScanTerrainPipeline.Result with = ScanTerrainPipeline.Build(scene.Scan, null, Pipeline(true));
        ScanTerrainPipeline.Result without = ScanTerrainPipeline.Build(scene.Scan, null, Pipeline(false));
        var building = Assert.Single(with.Buildings!.Buildings);
        Assert.Equal(ScanRoofKind.Gable, building.RoofKind);
        Assert.Equal(without.Reference.Vertices, with.Reference.Vertices);
        Assert.Equal(without.Reference.Faces, with.Reference.Faces);
        Assert.Empty(with.FixedBreaklines);
        AssertWatertight(building.Solid);
    }

    [Theory]
    [InlineData(ScanRoofKind.Hip)]
    [InlineData(ScanRoofKind.HalfHip)]
    public void Build_CoarseRoofAndInferredGround_RecoversBothEndFacets(ScanRoofKind kind)
    {
        double Roof(double u, double v) => 6 + .5 * Math.Min(4 - Math.Abs(v), (kind == ScanRoofKind.HalfHip ? 2 : 0) + 6 - Math.Abs(u));
        var scene = Scene(Roof, step: .5);
        var result = ScanTerrainPipeline.Build(scene.Scan, null, new()
        {
            Ground = new() { CellSize = .5, SmallestObject = 2, LargestObject = 48, InitialThreshold = .3, MaximumThreshold = 4, Margin = .5, MinimumObjectArea = 20 },
            GroundSpacing = 1, PlanTolerance = .5,
            Buildings = new() { MinimumArea = 20, MinimumHeight = 2, FootprintTolerance = .5, RoofTolerance = .3 },
        });
        var building = Assert.Single(result.Buildings!.Buildings);
        Assert.Equal(kind, building.RoofKind);
        var projector = new MeshHeightProjector(building.Solid.Vertices, building.Solid.VertexCount, building.Solid.Faces, building.Solid.FaceCount);
        foreach (double u in new[] { -5.0, 5.0 })
        {
            Assert.True(projector.TryGetHeightExtremes(u + 10, 10, .001, out _, out double high));
            Assert.InRange(Math.Abs(high - Roof(u, 0)), 0, .05);
        }
        AssertWatertight(building.Solid);
    }

    [Fact]
    public void Extract_CancelledBeforeWork_ThrowsWithoutReturningPartialBuildings()
    {
        var scene = Scene((u, v) => 6);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ScanBuildings.Extract(scene.Scan, scene.Ground, null, new()
        {
            MinimumArea = 20, MinimumHeight = 2, FootprintTolerance = .5, RoofTolerance = .2,
            CancellationToken = cancellation.Token,
        }));
    }

    private static void AssertWatertight(IndexedTriMesh mesh)
    {
        var edges = IndexedMeshTools.CreateEdgeKeyMap<(int Uses, int Direction)>();
        for (int f = 0; f < mesh.FaceCount; f++)
            for (int k = 0; k < 3; k++)
            {
                int a = mesh.Faces[f * 3 + k], b = mesh.Faces[f * 3 + (k + 1) % 3];
                Assert.NotEqual(a, b);
                long key = ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
                edges.TryGetValue(key, out var entry);
                edges[key] = (entry.Uses + 1, entry.Direction + (a < b ? 1 : -1));
            }
        Assert.NotEmpty(edges);
        Assert.All(edges.Values, edge => { Assert.Equal(2, edge.Uses); Assert.Equal(0, edge.Direction); });
    }
}
