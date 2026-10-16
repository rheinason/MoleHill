using MoleHill.Core.Displacement;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Shared;
using Rhino.Geometry;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Displace modifier build stage: adds a world-XY noise or image offset to the incoming terrain's heights
/// through <see cref="TerrainDisplacer"/>. Every breakline, wall rail and graded edge the cards below left
/// (<see cref="TerrainBuildResult.PersistentHardConstraints"/>), the walls and the Protect curves keep their
/// height. Only Z changes, so the incoming topology is reused. An image is an external file: its stamp is part
/// of the stage fingerprint, so saving over it rebuilds.
/// </summary>
internal static class DisplaceStage
{
    /// <summary>The most image tiles the mapping guide draws; beyond it only the tile under the terrain's centre.</summary>
    private const int MaxGuideTiles = 400;

    internal static void Run(ModifierBuildContext c)
    {
        var displace = (DisplaceModifierDefinition)c.Modifier;
        RhinoMesh? input = c.CurrentMesh;

        HeightmapImage? image = null;
        HeightmapFileStamp stamp = default;
        if (DisplaceSources.IsImage(displace) &&
            !HeightmapImageLoader.TryLoad(displace.ImagePath, out image, out stamp, out string? imageError))
        {
            // InertReason has already vetted the file, so it changed under the build; pass the terrain through.
            c.Build.Diagnostics.Add($"{displace.Label}: {imageError} Terrain unchanged.");
            return;
        }

        ModifierStageReport? report = null;
        c.CurrentMesh = TerrainBuildService.ExecuteCachedMeshStage(
            c.Build,
            c.RuntimeCache,
            c.StageKey,
            "Displace",
            ComputeFingerprint(c.Snapshot, c.Terrain, displace, c.CurrentMeshFingerprint, image != null ? stamp : (HeightmapFileStamp?)null),
            () => input == null
                ? TerrainBuildService.WarnMissingMesh(c.Build, displace.Label)
                : ApplyDisplace(c.Snapshot, c.Terrain, input, displace, image, c.Build, out report),
            result => TerrainBuildService.DescribeModifierMeshResult(displace.Label, result),
            out ulong fingerprint,
            c.ShouldCancel);
        c.CurrentMeshFingerprint = fingerprint;

        // A cache hit keeps the report the cached run stored; a fresh run replaces it.
        if (report != null && c.RuntimeCache.StageEntries.TryGetValue(c.StageKey, out StageCacheEntry? entry))
            entry.StageReport = report;
    }

    /// <summary>
    /// The stage's inputs: the definition and its curves (as every modifier), plus the image file's identity,
    /// so an image saved over in place rebuilds although nothing in the document changed.
    /// </summary>
    internal static ulong ComputeFingerprint(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        DisplaceModifierDefinition displace,
        ulong upstreamFingerprint,
        HeightmapFileStamp? imageStamp)
    {
        var builder = new FingerprintBuilder();
        builder.Add(TerrainBuildService.ComputeModifierStageFingerprint(snapshot, terrain, displace, upstreamFingerprint));
        if (imageStamp is { } stamp)
        {
            builder.Add("DisplaceImageV1");
            builder.Add(stamp.Length);
            builder.Add(stamp.LastWriteUtcTicks);
        }

        return builder.ToUInt64();
    }

    private static RhinoMesh ApplyDisplace(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        DisplaceModifierDefinition displace,
        HeightmapImage? image,
        TerrainBuildResult build,
        out ModifierStageReport? report)
    {
        report = null;
        if (!RhinoGeometryConversions.TryExtractMesh(mesh, out IndexedTriMesh indexed, out string? errorMessage))
        {
            build.Diagnostics.Add(errorMessage ?? "Could not extract mesh data for Displace.");
            return mesh;
        }

        TerrainTolerancePolicy.Profile profile = TerrainBuildService.GetToleranceProfile(snapshot, terrain);
        double curveTolerance = profile.CurveChordTolerance;
        IDisplacementSource source = CreateSource(displace, image);

        var boundaries = new List<(double[] xyVerts, int vertCount)>();
        foreach (ConstraintPolyline boundary in TerrainBuildService.CreateConstraintPolylines(
                     TerrainBuildSnapshotResolver.ResolveCurves(snapshot, displace.Boundaries), curveTolerance, preserveInputElevation: false))
        {
            if (boundary.IsClosed && ToXy(boundary, dropClosingPoint: true) is { } xy && xy.Count >= 3)
                boundaries.Add((xy.Xy, xy.Count));
        }

        // Every hard line the cards below left keeps its height, and so does every Protect curve.
        var holdLines = new List<MeshSmoother.BreaklinePolyline>();
        foreach (ConstraintPolyline line in TerrainBuildService.CombineConstraints(
                     build.PersistentHardConstraints,
                     TerrainBuildService.CreateConstraintPolylines(
                         TerrainBuildSnapshotResolver.ResolveCurves(snapshot, displace.Protect), curveTolerance, preserveInputElevation: false)))
        {
            if (ToXy(line, dropClosingPoint: line.IsClosed) is { } xy && xy.Count >= 2)
                holdLines.Add(new MeshSmoother.BreaklinePolyline(xy.Xy, xy.Count, line.IsClosed));
        }

        TerrainDisplacer.Result result = TerrainDisplacer.Apply(new TerrainDisplacer.Request
        {
            Mesh = indexed,
            Source = source,
            Boundaries = boundaries,
            HoldLines = holdLines,
            HoldBorder = displace.HoldBorder,
            Feather = displace.Feather,
            // Hard lines are mesh edges, but their vertices come back through single precision: hold
            // within the tolerance the remesher pins those same lines at.
            Tolerance = Math.Max(curveTolerance, profile.RemeshConstraintTolerance)
        });

        string Length(double value) => ModelUnits.FormatLength(value, snapshot.ModelUnitSystem, "G3");
        report = DisplaceModifierDescriptor.DescribeResult(result, source, Length);
        if (report.Warning != null)
            build.Diagnostics.Add($"{displace.Label}: {report.Warning}");

        if (image != null && displace.ShowMapping)
            AddMappingGuide(build, displace, (ImageDisplacementSource)source, indexed, result);

        if (result.MovedVertexCount == 0)
        {
            build.Diagnostics.Add($"{displace.Label}: no vertex moved; every one inside the boundaries is held, or none is inside.");
            return mesh;
        }

        if (result.Vertices.Any(value => !double.IsFinite(value)))
        {
            build.Diagnostics.Add("Displace produced invalid mesh data. Incoming mesh kept.");
            return mesh;
        }

        // Heights only: the incoming topology is reused, and the helper orients normals for the new heights.
        RhinoMesh displaced = RhinoGeometryConversions.BuildMeshWithNewHeights(mesh, result.Vertices, indexed.VertexCount);
        if (displaced.Faces.Count == 0 || displaced.Vertices.Count == 0 || !displaced.IsValid)
        {
            build.Diagnostics.Add("Displace produced an invalid mesh. Incoming mesh kept.");
            return mesh;
        }

        build.Diagnostics.Add(
            $"{displace.Label}: moved {result.MovedVertexCount:N0} vertices by {Length(result.MinOffset)} to {Length(result.MaxOffset)}; " +
            $"median edge {Length(result.MedianEdgeLength)}.");
        return displaced;
    }

    internal static IDisplacementSource CreateSource(DisplaceModifierDefinition displace, HeightmapImage? image)
    {
        var placement = new DisplacementPlacement(displace.OriginX, displace.OriginY, displace.Rotation);
        return image != null
            ? new ImageDisplacementSource(
                image,
                displace.TileSizeX,
                displace.TileSizeY,
                displace.Height,
                displace.Neutral,
                DisplaceSources.ParseWrap(displace.Wrap),
                placement)
            : new NoiseDisplacementSource(
                displace.NoiseSize,
                displace.Height,
                displace.Octaves,
                displace.Roughness,
                displace.Seed,
                placement);
    }

    private static (double[] Xy, int Count)? ToXy(ConstraintPolyline line, bool dropClosingPoint)
    {
        int count = Math.Min(line.PointCount, line.Points.Length / 3);
        if (dropClosingPoint && count > 1 &&
            line.Points[0] == line.Points[(count - 1) * 3] &&
            line.Points[1] == line.Points[(count - 1) * 3 + 1])
        {
            count--;
        }

        if (count < 2)
            return null;

        var xy = new double[count * 2];
        for (int i = 0; i < count; i++)
        {
            xy[i * 2] = line.Points[i * 3];
            xy[i * 2 + 1] = line.Points[i * 3 + 1];
        }

        return (xy, count);
    }

    /// <summary>
    /// Draws where the image lies: the frame of every tile over the edited ground (or, past
    /// <see cref="MaxGuideTiles"/>, the one under its centre), the image's top edge, and its origin. A viewport
    /// guide owned by the card, never a document object, so it is neither baked nor routed to a layer.
    /// </summary>
    private static void AddMappingGuide(
        TerrainBuildResult build,
        DisplaceModifierDefinition displace,
        ImageDisplacementSource source,
        IndexedTriMesh mesh,
        TerrainDisplacer.Result result)
    {
        double minX, minY, maxX, maxY;
        double topZ = double.NegativeInfinity;
        if (result.MovedVertexCount > 0)
            (minX, minY, maxX, maxY) = (result.MinX, result.MinY, result.MaxX, result.MaxY);
        else
            (minX, minY, maxX, maxY) = (double.PositiveInfinity, double.PositiveInfinity, double.NegativeInfinity, double.NegativeInfinity);

        for (int v = 0; v < mesh.VertexCount; v++)
        {
            double x = mesh.Vertices[v * 3], y = mesh.Vertices[v * 3 + 1];
            topZ = Math.Max(topZ, result.Vertices[v * 3 + 2]);
            if (result.MovedVertexCount > 0)
                continue;
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
        }

        if (!double.IsFinite(minX) || !double.IsFinite(topZ))
            return;

        // The extent in tile units, through all four corners since the image may be turned.
        DisplacementPlacement placement = source.Placement;
        double minI = double.PositiveInfinity, maxI = double.NegativeInfinity;
        double minJ = double.PositiveInfinity, maxJ = double.NegativeInfinity;
        foreach ((double x, double y) in new[] { (minX, minY), (maxX, minY), (maxX, maxY), (minX, maxY) })
        {
            (double u, double v) = placement.ToLocal(x, y);
            minI = Math.Min(minI, u / source.TileSizeX);
            maxI = Math.Max(maxI, u / source.TileSizeX);
            minJ = Math.Min(minJ, v / source.TileSizeY);
            maxJ = Math.Max(maxJ, v / source.TileSizeY);
        }

        var tiles = new List<(long I, long J)>();
        if (source.Wrap == DisplacementWrap.Repeat)
        {
            long i0 = (long)Math.Floor(minI), i1 = (long)Math.Floor(maxI);
            long j0 = (long)Math.Floor(minJ), j1 = (long)Math.Floor(maxJ);
            if ((i1 - i0 + 1) * (j1 - j0 + 1) <= MaxGuideTiles)
            {
                for (long j = j0; j <= j1; j++)
                {
                    for (long i = i0; i <= i1; i++)
                        tiles.Add((i, j));
                }
            }
            else
            {
                tiles.Add(((long)Math.Floor(0.5 * (minI + maxI)), (long)Math.Floor(0.5 * (minJ + maxJ))));
            }
        }
        else
        {
            // One image. It is drawn even when it misses the terrain; that it lies elsewhere is the news.
            tiles.Add((0, 0));
            if (maxI < 0.0 || minI > 1.0 || maxJ < 0.0 || minJ > 1.0)
            {
                build.Diagnostics.Add(
                    $"{displace.Label}: the image does not reach the terrain. Move its Origin onto the site " +
                    $"(the terrain spans X {minX:0.##} to {maxX:0.##}, Y {minY:0.##} to {maxY:0.##}).");
            }
        }

        Point3d World(double i, double j)
        {
            (double x, double y) = placement.ToWorld(i * source.TileSizeX, j * source.TileSizeY);
            return new Point3d(x, y, topZ);
        }

        var primitives = new List<RuntimeOverlayPrimitive>();
        foreach ((long i, long j) in tiles)
        {
            primitives.Add(RuntimeOverlayPrimitive.Polyline(
                new[] { World(i, j), World(i + 1, j), World(i + 1, j + 1), World(i, j + 1) },
                isClosed: true,
                thickness: 1));
        }

        // The image's top edge, heavier, on the tile under the terrain's centre: which way up it lies.
        (long ti, long tj) = tiles[tiles.Count / 2];
        primitives.Add(RuntimeOverlayPrimitive.Polyline(new[] { World(ti, tj + 1), World(ti + 1, tj + 1) }, thickness: 3));
        primitives.Add(RuntimeOverlayPrimitive.Label(World(ti + 0.5, tj + 1), "image top", size: 11));

        (double ox, double oy) = (placement.OriginX, placement.OriginY);
        double padX = source.TileSizeX, padY = source.TileSizeY;
        if (ox >= minX - padX && ox <= maxX + padX && oy >= minY - padY && oy <= maxY + padY)
        {
            primitives.Add(RuntimeOverlayPrimitive.Marker(new Point3d(ox, oy, topZ), size: 6));
            primitives.Add(RuntimeOverlayPrimitive.Label(new Point3d(ox, oy, topZ), "origin", size: 11));
        }

        build.RuntimeOverlays.Add(new RuntimeOverlayItem
        {
            StableId = $"displace-mapping:{displace.Id:N}",
            Owner = new RuntimeOverlayOwner(RuntimeOverlayOwnerKind.Modifier, displace.Id),
            Channel = RuntimeOverlayChannel.Guide,
            Code = "displace.mapping",
            Message = $"{displace.Label} image mapping.",
            ShortLabel = "Mapping",
            Primitives = primitives
        });
    }
}
