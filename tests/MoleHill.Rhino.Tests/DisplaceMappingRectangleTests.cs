using MoleHill.Core.Displacement;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// Displace's Mapping rectangle: it persists, it overrides the typed placement and hides those rows, a curve
/// that is not a rectangle makes the card inert, and moving it changes the stage fingerprint. The resolution
/// tests read real Rhino curves and need the native runtime.
/// </summary>
public class DisplaceMappingRectangleTests
{
    private static ModifierTypeDescriptor Descriptor => TerrainTypeRegistry.ForModifierKind("displace")!;

    private static DisplaceModifierDefinition Mapped()
    {
        var displace = new DisplaceModifierDefinition
        {
            Source = DisplaceSources.Image,
            Height = 1.0,
            OriginX = 7.0, OriginY = 8.0, Rotation = 45.0, TileSizeX = 3.0, TileSizeY = 3.0,
        };
        displace.Mapping.ObjectIds.Add(Guid.NewGuid());
        return displace;
    }

    [Fact]
    public void Serialize_RoundTripsTheMappingReference()
    {
        DisplaceModifierDefinition original = Mapped();
        var terrain = new TerrainDefinition();
        terrain.Modifiers.Add(original);

        var restored = Assert.IsType<DisplaceModifierDefinition>(
            Assert.Single(TerrainSerializer.Deserialize(TerrainSerializer.Serialize(new[] { terrain }))).Modifiers.Last());

        Assert.Equal(original.Mapping.ObjectIds, restored.Mapping.ObjectIds);
        Assert.Contains(restored.Mapping, restored.EnumerateSourceSets());
    }

    [Fact]
    public void Rectangle_OverridesTheTypedPlacement_OfAnImage()
    {
        var image = new HeightmapImage(2, 2, new[] { 0.0f, 0.0f, 1.0f, 0.0f });
        DisplaceModifierDefinition displace = Mapped();
        var rectangle = new MappingRectangle(100.0, 50.0, 20.0, 10.0, 90.0);

        var typed = (ImageDisplacementSource)DisplaceStage.CreateSource(displace, image);
        var mapped = (ImageDisplacementSource)DisplaceStage.CreateSource(displace, image, rectangle);

        Assert.Equal(3.0, typed.TileSizeX);
        Assert.Equal(45.0, typed.Placement.RotationDegrees);
        Assert.Equal(20.0, mapped.TileSizeX);
        Assert.Equal(10.0, mapped.TileSizeY);
        Assert.Equal(100.0, mapped.Placement.OriginX);
        Assert.Equal(50.0, mapped.Placement.OriginY);
        Assert.Equal(90.0, mapped.Placement.RotationDegrees);

        // The bottom-left pixel centre (white, neutral 0.5), local (5, 2.5), turned 90 degrees about (100, 50).
        Assert.Equal(0.5, mapped.Offset(97.5, 55.0), 6);
    }

    [Fact]
    public void Rectangle_NeverPlacesNoise()
    {
        DisplaceModifierDefinition displace = Mapped();
        displace.Source = DisplaceSources.Noise;
        displace.NoiseSize = 4.0;

        var noise = (NoiseDisplacementSource)DisplaceStage.CreateSource(displace, null, new MappingRectangle(100, 50, 20, 10, 90));

        Assert.Equal(7.0, noise.Placement.OriginX);
        Assert.Equal(45.0, noise.Placement.RotationDegrees);
    }

    [Fact]
    public void Rectangle_HidesTheRowsItDerives()
    {
        DisplaceModifierDefinition mapped = Mapped();
        var typed = new DisplaceModifierDefinition { Source = DisplaceSources.Image };
        IEnumerable<string> Visible(DisplaceModifierDefinition d) =>
            Descriptor.Parameters.Where(p => p.VisibleWhen?.Invoke(d) ?? true).Select(p => p.Key);

        foreach (string key in new[] { "OriginX", "OriginY", "Rotation", "TileSizeX", "TileSizeY" })
        {
            Assert.DoesNotContain(key, Visible(mapped));
            Assert.Contains(key, Visible(typed));
        }

        Assert.Contains("Mapping", Visible(mapped));
        Assert.Contains("Mapping", Visible(typed));
        Assert.DoesNotContain("Mapping", Visible(new DisplaceModifierDefinition { Source = DisplaceSources.Noise }));
    }

    [Fact]
    public void UnitScale_LeavesTheRectangleReference_AndScalesTheTypedFallback()
    {
        DisplaceModifierDefinition displace = Mapped();
        Guid id = displace.Mapping.ObjectIds.Single();

        TerrainUnitScaler.Scale(displace, 1000.0);

        Assert.Equal(id, displace.Mapping.ObjectIds.Single());
        Assert.Equal(3000.0, displace.TileSizeX, 9);
        Assert.Equal(7000.0, displace.OriginX, 9);
        Assert.Equal(45.0, displace.Rotation, 12);
    }

    [Fact]
    public void Report_ShowsTheDerivedPlacement()
    {
        var image = new ImageDisplacementSource(new HeightmapImage(1, 1, new[] { 0.5f }), 20, 10, 1, 0.5, DisplacementWrap.Repeat, default);
        ModifierStageReport report = DisplaceModifierDescriptor.DescribeResult(
            new TerrainDisplacer.Result(Array.Empty<double>(), 5, 0, 0, 0.1, 0.5, 0, 0, 1, 1),
            image,
            value => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " m",
            new MappingRectangle(100.0, 50.0, 20.0, 10.0, 30.0));

        Assert.Equal("rectangle 20 m x 10 m, turned 30 deg, bottom-left at 100, 50", report.Rows[0].Value);
    }

    // ---- Resolving real curves (native) ----

    private static (TerrainBuildSnapshot Snapshot, TerrainDefinition Terrain) SnapshotWith(DisplaceModifierDefinition displace, Curve curve)
    {
        var terrain = new TerrainDefinition { GlobalTolerance = 0.01, Modifiers = new List<ModifierDefinition> { displace } };
        var snapshot = new TerrainBuildSnapshot { Terrain = terrain, ModelAbsoluteTolerance = 0.001, ModelUnitSystem = UnitSystem.Meters };
        Guid id = displace.Mapping.ObjectIds.Single();
        BoundingBox bbox = curve.GetBoundingBox(true);
        snapshot.SourceObjects[displace.Mapping] = new List<ResolvedSourceObject>
        {
            new() { ObjectId = id, Geometry = curve, LocalBoundingBox = bbox, WorldBoundingBox = bbox, GeometryDataCrc = curve.DataCRC(0) },
        };
        snapshot.SourceFingerprints[displace.Mapping] = curve.DataCRC(0);
        return (snapshot, terrain);
    }

    private static PolylineCurve Rectangle(params (double X, double Y)[] corners) =>
        new(corners.Append(corners[0]).Select(c => new Point3d(c.X, c.Y, 2.0)));

    [RhinoNativeFact]
    public void Resolve_ReversedRectangle_ReadsTheSamePlacement()
    {
        DisplaceModifierDefinition displace = Mapped();
        (TerrainBuildSnapshot forward, _) = SnapshotWith(displace, Rectangle((0, 0), (8, 0), (8, 4), (0, 4)));
        Assert.True(DisplaceStage.TryResolveMappingRectangle(forward, displace, out MappingRectangle? a, out string? error), error);
        (TerrainBuildSnapshot reversed, _) = SnapshotWith(displace, Rectangle((0, 0), (0, 4), (8, 4), (8, 0)));
        Assert.True(DisplaceStage.TryResolveMappingRectangle(reversed, displace, out MappingRectangle? b, out error), error);

        Assert.Equal(new MappingRectangle(0, 0, 8, 4, 0), a);
        Assert.Equal(a, b);
    }

    [RhinoNativeFact]
    public void NonRectangle_MakesTheCardInert()
    {
        DisplaceModifierDefinition displace = Mapped();
        displace.ImagePath = string.Empty;
        (TerrainBuildSnapshot snapshot, _) = SnapshotWith(displace, Rectangle((0, 0), (8, 0), (9, 4), (1, 4)));
        Assert.False(DisplaceStage.TryResolveMappingRectangle(snapshot, displace, out _, out string? error));
        Assert.Contains("not a rectangle", error);

        (TerrainBuildSnapshot circle, _) = SnapshotWith(displace, new ArcCurve(new Circle(Point3d.Origin, 3.0)));
        Assert.False(DisplaceStage.TryResolveMappingRectangle(circle, displace, out _, out error));
        Assert.Contains("not a rectangle", error);
    }

    [RhinoNativeFact]
    public void MovingTheRectangle_ChangesTheStageFingerprint()
    {
        DisplaceModifierDefinition displace = Mapped();
        PolylineCurve curve = Rectangle((0, 0), (8, 0), (8, 4), (0, 4));
        (TerrainBuildSnapshot before, TerrainDefinition terrain) = SnapshotWith(displace, curve);
        ulong a = DisplaceStage.ComputeFingerprint(before, terrain, displace, 1UL, null);

        var moved = (PolylineCurve)curve.Duplicate();
        moved.Rotate(0.3, Vector3d.ZAxis, Point3d.Origin);
        (TerrainBuildSnapshot after, _) = SnapshotWith(displace, moved);

        Assert.NotEqual(a, DisplaceStage.ComputeFingerprint(after, terrain, displace, 1UL, null));
    }
}
