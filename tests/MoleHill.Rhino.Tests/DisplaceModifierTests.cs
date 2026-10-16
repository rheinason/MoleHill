using System.Drawing;
using System.Drawing.Imaging;
using MoleHill.Core.Displacement;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.Geometry;
using Xunit;
using RhinoPoint = Rhino.Geometry.Point;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// The Displace modifier's document and build contract: it round-trips and rescales with the model units,
/// a missing or unreadable image makes the card inert rather than silently flat, the image file's identity
/// is part of the stage fingerprint, and the card reports how finely the terrain samples the pattern. The
/// build tests need Rhino's native runtime.
/// </summary>
public class DisplaceModifierTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "MoleHillDisplaceTests", Guid.NewGuid().ToString("N"));

    public DisplaceModifierTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static ModifierTypeDescriptor Descriptor => TerrainTypeRegistry.ForModifierKind("displace")!;

    private static DisplaceModifierDefinition RoundTrip(DisplaceModifierDefinition modifier)
    {
        var terrain = new TerrainDefinition();
        terrain.Modifiers.Add(modifier);
        string json = TerrainSerializer.Serialize(new[] { terrain });
        return Assert.IsType<DisplaceModifierDefinition>(Assert.Single(TerrainSerializer.Deserialize(json)).Modifiers.Last());
    }

    /// <summary>Writes a greyscale PNG whose pixel (x, y) has grey level <paramref name="grey"/>(x, y).</summary>
    private string WriteImage(string name, int width, int height, Func<int, int, int> grey)
    {
        string path = Path.Combine(_folder, name);
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int g = grey(x, y);
                bitmap.SetPixel(x, y, Color.FromArgb(255, g, g, g));
            }
        }

        bitmap.Save(path, ImageFormat.Png);
        return path;
    }

    [Fact]
    public void Registry_DiscoversDisplace()
    {
        Assert.NotNull(Descriptor);
        Assert.Equal(typeof(DisplaceModifierDefinition), Descriptor.DefinitionType);
        Assert.True(Descriptor.CanCreateFromMenu);
    }

    [Fact]
    public void Serialize_RoundTripsEveryField()
    {
        var original = new DisplaceModifierDefinition
        {
            Source = DisplaceSources.Image,
            Feather = 1.5,
            HoldBorder = false,
            Height = -0.75,
            Rotation = 33.0,
            OriginX = 1234.5,
            OriginY = -678.25,
            NoiseSize = 7.0,
            Octaves = 6,
            Roughness = 0.35,
            Seed = -12,
            ImagePath = @"C:\maps\dunes.png",
            TileSizeX = 40.0,
            TileSizeY = 25.0,
            Neutral = 0.25,
            Wrap = DisplaceSources.Once,
            ShowMapping = false,
        };
        Guid boundaryId = Guid.NewGuid();
        Guid protectId = Guid.NewGuid();
        original.Boundaries.ObjectIds.Add(boundaryId);
        original.Protect.ObjectIds.Add(protectId);

        DisplaceModifierDefinition restored = RoundTrip(original);

        Assert.True(DisplaceSources.IsImage(restored));
        Assert.Equal(1.5, restored.Feather);
        Assert.False(restored.HoldBorder);
        Assert.Equal(-0.75, restored.Height);
        Assert.Equal(33.0, restored.Rotation);
        Assert.Equal(1234.5, restored.OriginX);
        Assert.Equal(-678.25, restored.OriginY);
        Assert.Equal(7.0, restored.NoiseSize);
        Assert.Equal(6, restored.Octaves);
        Assert.Equal(0.35, restored.Roughness);
        Assert.Equal(-12, restored.Seed);
        Assert.Equal(@"C:\maps\dunes.png", restored.ImagePath);
        Assert.Equal(40.0, restored.TileSizeX);
        Assert.Equal(25.0, restored.TileSizeY);
        Assert.Equal(0.25, restored.Neutral);
        Assert.Equal(DisplaceSources.Once, restored.Wrap);
        Assert.False(restored.ShowMapping);
        Assert.Contains(boundaryId, restored.Boundaries.ObjectIds);
        Assert.Contains(protectId, restored.Protect.ObjectIds);
    }

    [Fact]
    public void Create_SetsMetricDefaults_InTheDocumentUnit()
    {
        var metres = Assert.IsType<DisplaceModifierDefinition>(Descriptor.Create(UnitSystem.Meters));
        var millimetres = Assert.IsType<DisplaceModifierDefinition>(Descriptor.Create(UnitSystem.Millimeters));

        Assert.False(DisplaceSources.IsImage(metres));
        Assert.Equal(0.5, metres.Height, 9);
        Assert.Equal(10.0, metres.NoiseSize, 9);
        Assert.Equal(500.0, millimetres.Height, 6);
        Assert.Equal(10000.0, millimetres.NoiseSize, 6);
        Assert.Equal(20000.0, millimetres.TileSizeX, 6);
        Assert.Equal(2000.0, millimetres.Feather, 6);
        Assert.Null(Descriptor.InertReason(metres, null!));
    }

    [Fact]
    public void UnitScale_ScalesLengthsAndOrigin_ButNotAnglesOrImageValues()
    {
        var displace = new DisplaceModifierDefinition
        {
            Height = 0.5, Feather = 2.0, NoiseSize = 10.0, TileSizeX = 20.0, TileSizeY = 30.0,
            OriginX = 100.0, OriginY = -50.0, Rotation = 45.0, Neutral = 0.4, Roughness = 0.6, Octaves = 5, Seed = 3,
        };

        TerrainUnitScaler.Scale(displace, 1000.0);

        Assert.Equal(500.0, displace.Height, 9);
        Assert.Equal(2000.0, displace.Feather, 9);
        Assert.Equal(10000.0, displace.NoiseSize, 9);
        Assert.Equal(20000.0, displace.TileSizeX, 9);
        Assert.Equal(30000.0, displace.TileSizeY, 9);
        Assert.Equal(100000.0, displace.OriginX, 9);
        Assert.Equal(-50000.0, displace.OriginY, 9);
        Assert.Equal(45.0, displace.Rotation, 12);
        Assert.Equal(0.4, displace.Neutral, 12);
        Assert.Equal(0.6, displace.Roughness, 12);
        Assert.Equal(5, displace.Octaves);
        Assert.Equal(3, displace.Seed);
    }

    [Fact]
    public void Inert_WithoutHeightOrSize_SaysWhatToSet()
    {
        Assert.Contains("Height", Descriptor.InertReason(new DisplaceModifierDefinition { Height = 0.0, NoiseSize = 1.0 }, null!));
        Assert.Contains("Noise Size", Descriptor.InertReason(new DisplaceModifierDefinition { Height = 1.0, NoiseSize = 0.0 }, null!));
        Assert.Null(Descriptor.InertReason(new DisplaceModifierDefinition { Height = 1.0, NoiseSize = 1.0 }, null!));
    }

    [Fact]
    public void Inert_WithAMissingOrUnreadableImage_NamesTheFile()
    {
        var noPath = new DisplaceModifierDefinition { Source = DisplaceSources.Image, Height = 1.0, TileSizeX = 10.0, TileSizeY = 10.0 };
        Assert.Contains("Choose an image", Descriptor.InertReason(noPath, null!));

        var missing = new DisplaceModifierDefinition
        {
            Source = DisplaceSources.Image, Height = 1.0, TileSizeX = 10.0, TileSizeY = 10.0,
            ImagePath = Path.Combine(_folder, "gone.png"),
        };
        string? reason = Descriptor.InertReason(missing, null!);
        Assert.NotNull(reason);
        Assert.Contains("gone.png", reason);
        Assert.Contains("not found", reason);

        string garbage = Path.Combine(_folder, "garbage.png");
        File.WriteAllText(garbage, "this is not an image");
        var unreadable = new DisplaceModifierDefinition
        {
            Source = DisplaceSources.Image, Height = 1.0, TileSizeX = 10.0, TileSizeY = 10.0, ImagePath = garbage,
        };
        Assert.Contains("could not be read", Descriptor.InertReason(unreadable, null!));

        var good = new DisplaceModifierDefinition
        {
            Source = DisplaceSources.Image, Height = 1.0, TileSizeX = 10.0, TileSizeY = 10.0,
            ImagePath = WriteImage("ok.png", 4, 4, (_, _) => 128),
        };
        Assert.Null(Descriptor.InertReason(good, null!));
        Assert.Contains("Tile Size", Descriptor.InertReason(new DisplaceModifierDefinition
        {
            Source = DisplaceSources.Image, Height = 1.0, TileSizeX = 0.0, TileSizeY = 10.0, ImagePath = good.ImagePath,
        }, null!));
    }

    [Fact]
    public void ImageLoader_ReadsGreyLevels_TopRowFirst()
    {
        string path = WriteImage("ramp.png", 3, 2, (x, y) => y == 0 ? 255 : x * 100);

        Assert.True(HeightmapImageLoader.TryLoad(path, out HeightmapImage? image, out _, out string? error), error);
        Assert.Equal(3, image!.Width);
        Assert.Equal(2, image.Height);
        Assert.Equal(1.0, image[0, 0], 6);
        Assert.Equal(0.0, image[0, 1], 6);
        Assert.Equal(100.0 / 255.0, image[1, 1], 6);
        Assert.Equal(200.0 / 255.0, image[2, 1], 6);
    }

    [Fact]
    public void ImageEdit_ChangesTheStageFingerprint_AndTheDecodedImage()
    {
        string path = WriteImage("edit.png", 2, 2, (_, _) => 50);
        Assert.True(HeightmapImageLoader.TryLoad(path, out HeightmapImage? before, out HeightmapFileStamp first, out _));

        // Save over the image in place, as an image editor would; the document itself does not change.
        WriteImage("edit.png", 2, 2, (_, _) => 200);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(5));
        Assert.True(HeightmapImageLoader.TryLoad(path, out HeightmapImage? after, out HeightmapFileStamp second, out _));

        Assert.NotEqual(first, second);
        Assert.Equal(50.0 / 255.0, before![0, 0], 6);
        Assert.Equal(200.0 / 255.0, after![0, 0], 6);

        var displace = new DisplaceModifierDefinition { Source = DisplaceSources.Image, Height = 1.0, TileSizeX = 5, TileSizeY = 5, ImagePath = path };
        var terrain = new TerrainDefinition { Modifiers = new List<ModifierDefinition> { displace } };
        var snapshot = new TerrainBuildSnapshot { Terrain = terrain, ModelAbsoluteTolerance = 0.001, ModelUnitSystem = UnitSystem.Meters };
        ulong a = DisplaceStage.ComputeFingerprint(snapshot, terrain, displace, 77UL, first);
        ulong b = DisplaceStage.ComputeFingerprint(snapshot, terrain, displace, 77UL, second);
        Assert.NotEqual(a, b);
        Assert.Equal(a, DisplaceStage.ComputeFingerprint(snapshot, terrain, displace, 77UL, first));
    }

    [Fact]
    public void Fingerprint_IgnoresTheLabel_ButNotTheSeed()
    {
        var displace = new DisplaceModifierDefinition { Height = 1.0, NoiseSize = 4.0 };
        var terrain = new TerrainDefinition { Modifiers = new List<ModifierDefinition> { displace } };
        var snapshot = new TerrainBuildSnapshot { Terrain = terrain, ModelAbsoluteTolerance = 0.001, ModelUnitSystem = UnitSystem.Meters };
        ulong original = DisplaceStage.ComputeFingerprint(snapshot, terrain, displace, 5UL, null);

        displace.Label = "Dunes";
        Assert.Equal(original, DisplaceStage.ComputeFingerprint(snapshot, terrain, displace, 5UL, null));

        displace.Seed = 2;
        Assert.NotEqual(original, DisplaceStage.ComputeFingerprint(snapshot, terrain, displace, 5UL, null));
    }

    [Fact]
    public void DescribeResult_WarnsWhenTheEdgesAreTooLongForTheNoise()
    {
        var noise = new NoiseDisplacementSource(4.0, 1.0, 3, 0.5, 1, default);
        static string Length(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " m";

        ModifierStageReport coarse = DisplaceModifierDescriptor.DescribeResult(
            new TerrainDisplacer.Result(Array.Empty<double>(), 120, 8, -0.3, 0.4, 3.0, 0, 0, 10, 10), noise, Length);
        ModifierStageReport fine = DisplaceModifierDescriptor.DescribeResult(
            new TerrainDisplacer.Result(Array.Empty<double>(), 120, 0, -0.3, 0.4, 1.5, 0, 0, 10, 10), noise, Length);

        Assert.NotNull(coarse.Warning);
        Assert.Contains("Remesh", coarse.Warning);
        Assert.Contains("2 m", coarse.Warning);
        Assert.Null(fine.Warning);
        Assert.Equal("edges 1.5 m (median), noise 4 m, finest octave 1 m", fine.Rows.Single(row => row.Label == "Sampling").Value);
        Assert.Equal("-0.3 m to 0.4 m over 120 vertices", fine.Rows.Single(row => row.Label == "Offset").Value);
        Assert.Contains(coarse.Rows, row => row.Label == "Held");
        Assert.NotEqual(coarse.Signature, fine.Signature);
    }

    [Fact]
    public void DescribeResult_JudgesAnImageByItsPixelSize()
    {
        var image = new ImageDisplacementSource(new HeightmapImage(10, 10, new float[100]), 5.0, 5.0, 1.0, 0.5, DisplacementWrap.Repeat, default);
        static string Length(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " m";

        ModifierStageReport report = DisplaceModifierDescriptor.DescribeResult(
            new TerrainDisplacer.Result(Array.Empty<double>(), 10, 0, 0, 0.5, 1.0, 0, 0, 1, 1), image, Length);

        Assert.Contains("pixels 0.5 m", report.Rows[0].Value);
        Assert.NotNull(report.Warning);
        Assert.Contains("0.25 m or less", report.Warning);
    }

    [Fact]
    public void DescribeResult_NothingMoved_SaysSo()
    {
        var noise = new NoiseDisplacementSource(4.0, 1.0, 3, 0.5, 1, default);
        ModifierStageReport report = DisplaceModifierDescriptor.DescribeResult(
            new TerrainDisplacer.Result(Array.Empty<double>(), 0, 0, 0, 0, 0, 0, 0, 0, 0), noise, value => value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.Null(report.Warning);
        Assert.Equal("nothing moved", Assert.Single(report.Rows).Value);
    }

    [Fact]
    public void Parameters_ShowOnlyTheChosenSourcesRows()
    {
        var noise = new DisplaceModifierDefinition { Source = DisplaceSources.Noise };
        var image = new DisplaceModifierDefinition { Source = DisplaceSources.Image };
        IEnumerable<string> Visible(DisplaceModifierDefinition d) =>
            Descriptor.Parameters.Where(p => p.VisibleWhen?.Invoke(d) ?? true).Select(p => p.Key);

        Assert.Contains("NoiseSize", Visible(noise));
        Assert.Contains("Seed", Visible(noise));
        Assert.DoesNotContain("ImagePath", Visible(noise));
        Assert.Contains("ImagePath", Visible(image));
        Assert.Contains("Neutral", Visible(image));
        Assert.DoesNotContain("Octaves", Visible(image));
        Assert.Contains("Rotation", Visible(image));
        Assert.Contains("Rotation", Visible(noise));
        Assert.Equal(ParameterUnit.Degrees, Descriptor.Parameters.Single(p => p.Key == "Rotation").Unit);
        Assert.Equal(ParameterKind.FilePath, Descriptor.Parameters.Single(p => p.Key == "ImagePath").Kind);
    }

    // ---- Build (native) ----

    [RhinoNativeFact]
    public void Build_Noise_MovesHeightsOnly_KeepsTheTopology_AndReports()
    {
        var displace = new DisplaceModifierDefinition { Id = Guid.NewGuid(), Height = 1.0, NoiseSize = 3.0, Feather = 0.0, HoldBorder = true };
        Fixture plain = CreateFixture(null);
        Fixture displaced = CreateFixture(displace);
        var service = new TerrainBuildService();
        var cache = new TerrainRuntimeCache();

        Mesh before = service.Build(plain.Snapshot, new TerrainRuntimeCache(), TerrainBuildMode.Final).PrimaryMesh!;
        Mesh after = service.Build(displaced.Snapshot, cache, TerrainBuildMode.Final).PrimaryMesh!;

        Assert.Equal(before.Vertices.Count, after.Vertices.Count);
        Assert.Equal(before.Faces.Count, after.Faces.Count);
        Assert.Equal(CountHoles(before), CountHoles(after));
        int moved = 0;
        for (int i = 0; i < before.Vertices.Count; i++)
        {
            Assert.Equal(before.Vertices[i].X, after.Vertices[i].X, 5);
            Assert.Equal(before.Vertices[i].Y, after.Vertices[i].Y, 5);
            if (Math.Abs(before.Vertices[i].Z - after.Vertices[i].Z) > 1e-6)
                moved++;
            Assert.InRange(after.Vertices[i].Z - before.Vertices[i].Z, -0.5 - 1e-5, 0.5 + 1e-5);
        }

        Assert.True(moved > 0);
        string stageKey = TerrainStageKey.ForMode(TerrainBuildMode.Final, TerrainStageKey.CreateModifier(displace));
        Assert.NotNull(cache.StageEntries[stageKey].StageReport);

        // A cache hit keeps showing what the cached run measured.
        service.Build(displaced.Snapshot, cache, TerrainBuildMode.Final);
        Assert.NotNull(cache.StageEntries[stageKey].StageReport);
    }

    [RhinoNativeFact]
    public void Build_Breakline_KeepsItsHeight()
    {
        var displace = new DisplaceModifierDefinition { Id = Guid.NewGuid(), Height = 2.0, NoiseSize = 2.0, Feather = 1.0, HoldBorder = false };
        Fixture fixture = CreateFixture(displace, breakline: true);

        Mesh after = new TerrainBuildService().Build(fixture.Snapshot, new TerrainRuntimeCache(), TerrainBuildMode.Final).PrimaryMesh!;

        int onLine = 0;
        foreach (Point3f vertex in after.Vertices)
        {
            if (Math.Abs(vertex.Y) > 1e-4 || Math.Abs(vertex.X) > 3.0)
                continue;
            onLine++;
            Assert.Equal(1.0, vertex.Z, 4);
        }

        Assert.True(onLine >= 2);
    }

    [RhinoNativeFact]
    public void Build_ImageSavedOver_RebuildsTheStage()
    {
        string path = WriteImage("build.png", 2, 2, (_, _) => 255);
        var displace = new DisplaceModifierDefinition
        {
            Id = Guid.NewGuid(), Source = DisplaceSources.Image, Height = 1.0, Neutral = 0.0,
            TileSizeX = 4.0, TileSizeY = 4.0, ImagePath = path, HoldBorder = false, Feather = 0.0,
        };
        Fixture fixture = CreateFixture(displace);
        var service = new TerrainBuildService();
        var cache = new TerrainRuntimeCache();

        Mesh white = service.Build(fixture.Snapshot, cache, TerrainBuildMode.Final).PrimaryMesh!;
        WriteImage("build.png", 2, 2, (_, _) => 0);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(5));
        Mesh black = service.Build(fixture.Snapshot, cache, TerrainBuildMode.Final).PrimaryMesh!;

        Assert.All(white.Vertices, vertex => Assert.Equal(1.0, vertex.Z, 4));
        Assert.All(black.Vertices, vertex => Assert.Equal(0.0, vertex.Z, 4));
    }

    private static int CountHoles(Mesh mesh) => mesh.GetNakedEdges()?.Length ?? 0;

    private sealed record Fixture(TerrainDefinition Terrain, TerrainBuildSnapshot Snapshot);

    /// <summary>A flat 0.5-spaced point grid over [-4, 4]² at Z = 0 (Z = 1 with a breakline along y = 0).</summary>
    private static Fixture CreateFixture(DisplaceModifierDefinition? displace, bool breakline = false)
    {
        var triangulate = new TriangulateModifierDefinition
        {
            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            PeelBoundaryTriangles = false,
            Tolerance = 0.01,
        };

        var modifiers = new List<ModifierDefinition> { triangulate };
        if (displace != null)
            modifiers.Add(displace);

        var terrain = new TerrainDefinition
        {
            TerrainId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            Name = "Displace Test",
            GlobalTolerance = 0.01,
            Modifiers = modifiers,
        };
        var snapshot = new TerrainBuildSnapshot
        {
            Terrain = terrain,
            ModelAbsoluteTolerance = 0.001,
            ModelUnitSystem = UnitSystem.Meters,
        };

        var objects = new List<ResolvedSourceObject>();
        ulong fingerprint = 17;
        double z = breakline ? 1.0 : 0.0;
        for (int i = -8; i <= 8; i++)
        {
            for (int j = -8; j <= 8; j++)
            {
                if (breakline && j == 0)
                    continue;
                objects.Add(Resolve(triangulate.Points, new RhinoPoint(new Point3d(i * 0.5, j * 0.5, z)), ref fingerprint));
            }
        }

        snapshot.SourceObjects[triangulate.Points] = objects;
        snapshot.SourceFingerprints[triangulate.Points] = fingerprint;

        if (breakline)
        {
            ulong lineFingerprint = 31;
            var line = new LineCurve(new Point3d(-3.0, 0.0, 1.0), new Point3d(3.0, 0.0, 1.0));
            snapshot.SourceObjects[triangulate.Breaklines] = new List<ResolvedSourceObject> { Resolve(triangulate.Breaklines, line, ref lineFingerprint) };
            snapshot.SourceFingerprints[triangulate.Breaklines] = lineFingerprint;
        }

        return new Fixture(terrain, snapshot);
    }

    private static ResolvedSourceObject Resolve(SourceReferenceSet set, GeometryBase geometry, ref ulong fingerprint)
    {
        Guid id = Guid.NewGuid();
        set.ObjectIds.Add(id);
        BoundingBox bbox = geometry.GetBoundingBox(true);
        fingerprint = unchecked((fingerprint * 397) ^ geometry.DataCRC(0));
        return new ResolvedSourceObject
        {
            ObjectId = id,
            Geometry = geometry,
            LocalBoundingBox = bbox,
            WorldBoundingBox = bbox,
            GeometryDataCrc = geometry.DataCRC(0),
        };
    }
}
