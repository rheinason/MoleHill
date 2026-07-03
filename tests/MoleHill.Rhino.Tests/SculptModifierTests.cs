using MoleHill.Core.Sculpting;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// Sculpt modifier: persistence of the displacement field and the build-stage replay contract —
/// the field is a pure function of world XY, so it must re-apply on top of whatever arrives from
/// the stage below (stackability) and multiple sculpt modifiers must compose.
/// </summary>
public class SculptModifierTests
{
    private const double CellSize = 0.5;

    [Fact]
    public void Serialize_SculptTiles_RoundTripsFieldSamples()
    {
        var field = new SculptDisplacementField(CellSize);
        field.SetSample(-10, 3, 1.25f);
        field.SetSample(70, -70, -0.5f);

        var terrain = new TerrainDefinition();
        var sculpt = new SculptModifierDefinition { CellSize = CellSize, DetailSize = 1.0 };
        sculpt.Tiles = SculptFieldCodec.Encode(field);
        terrain.Modifiers.Add(sculpt);

        string json = TerrainSerializer.Serialize(new[] { terrain });
        var restored = TerrainSerializer.Deserialize(json).Single();
        var restoredSculpt = Assert.IsType<SculptModifierDefinition>(
            restored.Modifiers.Single(m => m is SculptModifierDefinition));

        SculptDisplacementField decoded = SculptFieldCodec.Decode(restoredSculpt);
        Assert.Equal(1.25f, decoded.GetSample(-10, 3));
        Assert.Equal(-0.5f, decoded.GetSample(70, -70));
        Assert.Equal(field.Tiles.Count, decoded.Tiles.Count);
    }

    [Fact]
    public void EffectiveCellSize_PinnedValue_IgnoresLaterDetailChanges()
    {
        // Tiles carry no spacing of their own: once written at a cell size, reinterpreting them at a
        // different one rescales the sculpt toward the world origin. A pinned CellSize must win over
        // any later DetailSize edit; only the unpinned default derives from DetailSize.
        var sculpt = new SculptModifierDefinition { DetailSize = 1.0 };
        Assert.Equal(0.5, sculpt.EffectiveCellSize, 9);

        sculpt.CellSize = 0.5; // pinned at first commit
        sculpt.DetailSize = 4.0;
        Assert.Equal(0.5, sculpt.EffectiveCellSize, 9);

        sculpt.DetailSize = 0.1; // even finer detail must not reinterpret existing tiles
        Assert.Equal(0.5, sculpt.EffectiveCellSize, 9);
    }

    [RhinoNativeFact]
    public void Build_SculptStage_DisplacesVerticesByFieldValue()
    {
        var fixture = CreateFixture(PlateauSculpt(2.0f), slopePerX: 0.0);

        TerrainBuildResult result = new TerrainBuildService().Build(fixture.Snapshot, new TerrainRuntimeCache(), TerrainBuildMode.Final);

        Assert.NotNull(result.PrimaryMesh);
        foreach (var vertex in result.PrimaryMesh!.Vertices)
            Assert.Equal(2.0, vertex.Z, 3);
    }

    [RhinoNativeFact]
    public void Build_UpstreamZChange_SculptReappliesOnTop()
    {
        // Same sculpt definition, different base surface: the displacement must ride on top.
        var flat = CreateFixture(PlateauSculpt(2.0f), slopePerX: 0.0);
        var sloped = CreateFixture(PlateauSculpt(2.0f), slopePerX: 0.1);

        var service = new TerrainBuildService();
        TerrainBuildResult flatResult = service.Build(flat.Snapshot, new TerrainRuntimeCache(), TerrainBuildMode.Final);
        TerrainBuildResult slopedResult = service.Build(sloped.Snapshot, new TerrainRuntimeCache(), TerrainBuildMode.Final);

        Assert.NotNull(slopedResult.PrimaryMesh);
        foreach (var vertex in slopedResult.PrimaryMesh!.Vertices)
            Assert.Equal(0.1 * vertex.X + 2.0, vertex.Z, 3);
        Assert.NotNull(flatResult.PrimaryMesh);
    }

    [RhinoNativeFact]
    public void Build_TwoSculptModifiers_Compose()
    {
        var fixture = CreateFixture(PlateauSculpt(2.0f), slopePerX: 0.0, secondSculpt: PlateauSculpt(1.0f));

        TerrainBuildResult result = new TerrainBuildService().Build(fixture.Snapshot, new TerrainRuntimeCache(), TerrainBuildMode.Final);

        Assert.NotNull(result.PrimaryMesh);
        foreach (var vertex in result.PrimaryMesh!.Vertices)
            Assert.Equal(3.0, vertex.Z, 3);
    }

    [RhinoNativeFact]
    public void Build_TileEdit_InvalidatesSculptStageButNotTriangulate()
    {
        var fixture = CreateFixture(PlateauSculpt(2.0f), slopePerX: 0.0);
        var cache = new TerrainRuntimeCache();
        var service = new TerrainBuildService();

        service.Build(fixture.Snapshot, cache, TerrainBuildMode.Final);
        fixture.Sculpt.Tiles = SculptFieldCodec.Encode(BuildPlateauField(3.5f));

        TerrainBuildResult rebuilt = service.Build(fixture.Snapshot, cache, TerrainBuildMode.Final);

        Assert.NotNull(rebuilt.PrimaryMesh);
        foreach (var vertex in rebuilt.PrimaryMesh!.Vertices)
            Assert.Equal(3.5, vertex.Z, 3);
        Assert.Contains(
            rebuilt.Timings,
            static timing => timing.Stage == "Triangulate" &&
                             timing.Detail?.Contains("cache hit", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static SculptModifierDefinition PlateauSculpt(float height)
    {
        return new SculptModifierDefinition
        {
            Id = Guid.NewGuid(),
            CellSize = CellSize,
            DetailSize = 1.0,
            DynTopo = false,
            Tiles = SculptFieldCodec.Encode(BuildPlateauField(height)),
        };
    }

    /// <summary>Constant-height plateau generously covering the test grid (world [-5, 5]²).</summary>
    private static SculptDisplacementField BuildPlateauField(float height)
    {
        var field = new SculptDisplacementField(CellSize);
        for (int gi = -12; gi <= 12; gi++)
        {
            for (int gj = -12; gj <= 12; gj++)
                field.SetSample(gi, gj, height);
        }

        return field;
    }

    private sealed record Fixture(TerrainDefinition Terrain, TerrainBuildSnapshot Snapshot, SculptModifierDefinition Sculpt);

    private static Fixture CreateFixture(SculptModifierDefinition sculpt, double slopePerX, SculptModifierDefinition? secondSculpt = null)
    {
        var triangulate = new TriangulateModifierDefinition
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            PeelBoundaryTriangles = false,
            Tolerance = 0.01,
        };

        var modifiers = new List<ModifierDefinition> { triangulate, sculpt };
        if (secondSculpt != null)
            modifiers.Add(secondSculpt);

        var terrain = new TerrainDefinition
        {
            TerrainId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            Name = "Sculpt Test",
            GlobalTolerance = 0.01,
            Modifiers = modifiers,
        };

        var snapshot = new TerrainBuildSnapshot
        {
            Terrain = terrain,
            ModelAbsoluteTolerance = 0.001,
            ModelUnitSystem = UnitSystem.Meters,
        };

        AddPointGrid(snapshot, triangulate.Points, slopePerX);
        return new Fixture(terrain, snapshot, sculpt);
    }

    private static void AddPointGrid(TerrainBuildSnapshot snapshot, SourceReferenceSet sourceSet, double slopePerX)
    {
        var objects = new List<ResolvedSourceObject>();
        ulong fingerprint = 17;
        for (int x = -4; x <= 4; x += 2)
        {
            for (int y = -4; y <= 4; y += 2)
            {
                Guid id = Guid.NewGuid();
                sourceSet.ObjectIds.Add(id);
                var geometry = new Point(new Point3d(x, y, slopePerX * x));
                BoundingBox bbox = geometry.GetBoundingBox(true);
                objects.Add(new ResolvedSourceObject
                {
                    ObjectId = id,
                    Geometry = geometry,
                    LocalBoundingBox = bbox,
                    WorldBoundingBox = bbox,
                    GeometryDataCrc = geometry.DataCRC(0),
                });
                fingerprint = unchecked((fingerprint * 397) ^ geometry.DataCRC(0));
            }
        }

        snapshot.SourceObjects[sourceSet] = objects;
        snapshot.SourceFingerprints[sourceSet] = fingerprint;
    }
}
