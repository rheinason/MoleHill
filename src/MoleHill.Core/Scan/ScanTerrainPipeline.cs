using MoleHill.Core.Engine;
using MoleHill.Core.Processing;

namespace MoleHill.Core.Scan;

/// <summary>Host-free scan stages: ground, measured walls, surface classes and road edges, then one reference TIN.</summary>
public static class ScanTerrainPipeline
{
    public sealed class Options
    {
        public required ScanGround.Options Ground { get; init; }
        public required double GroundSpacing { get; init; }
        public required double PlanTolerance { get; init; }
        public ScanWalls.Options? Walls { get; init; }
        public ScanSurfaceAreas.Options? Areas { get; init; }
        public ScanRoadEdges.Options? Roads { get; init; }
        public ScanBuildings.Options? Buildings { get; init; }
        public Action<string>? ReportProgress { get; init; }
        public Action<Result>? ReportPreview { get; init; }
    }

    public sealed class Result
    {
        public required ScanGround.Result Ground { get; init; }
        public required IndexedTriMesh Reference { get; init; }
        public required IReadOnlyList<ScanWalls.Wall> Walls { get; init; }
        public required IReadOnlyList<ScanRoadEdges.EdgeLine> Roads { get; init; }
        public ScanSurfaceAreas.Result? Areas { get; init; }
        public ScanBuildings.Result? Buildings { get; init; }
        public int[]? SurfaceColours { get; init; }
        public required IReadOnlyList<double[]> FixedBreaklines { get; init; }
        public ITerrainRecoveryRegion? Region => Areas == null ? null : new ScanSoftRegion(Areas);
    }

    public static Result Build(IndexedTriMesh scan, ScanColourSource? colours, Options options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ReportProgress?.Invoke("Rasterising scan triangles and colours");
        double cell = options.Ground.CellSize > 0 ? options.Ground.CellSize : ScanRasterizer.AutomaticCellSize(scan);
        ScanRasterizer.Rasters rasters = ScanRasterizer.Rasterize(scan, cell, options.Ground.CancellationToken, colours);
        return BuildRasters(scan, rasters.Top, rasters.Colours, options);
    }

    public static Result Build(ScanPointCloud cloud, Options options)
    {
        ArgumentNullException.ThrowIfNull(cloud); ArgumentNullException.ThrowIfNull(options);
        options.ReportProgress?.Invoke("Binning cloud observations and colours");
        double cell = options.Ground.CellSize > 0 ? options.Ground.CellSize : cloud.AutomaticCellSize();
        ScanPointCloud.Raster raster = cloud.Rasterize(cell, options.Ground.CancellationToken);
        IndexedTriMesh roofs = options.Buildings == null ? IndexedTriMesh.Empty : raster.RoofMesh(options.Ground.CancellationToken);
        return BuildRasters(roofs, raster.Top, raster.Colours, options, raster.ClassifiedGround, raster.Classes);
    }

    private static Result BuildRasters(IndexedTriMesh scan, ScanGrid top, int[]? colours, Options options,
        ScanGrid? classifiedGround = null, byte[]? classes = null)
    {
        options.ReportProgress?.Invoke("Finding and filling the ground");
        ScanGround.Result ground = ScanGround.FromSurface(top, options.Ground);
        if (classifiedGround != null)
            ground = ScanGround.WithClassifiedGround(ground, classifiedGround, options.Ground);
        IndexedTriMesh reference = ScanGroundMesher.Mesh(ground.Ground, options.GroundSpacing);
        if (reference.IsEmpty)
            return new Result { Ground = ground, Reference = reference, Walls = Array.Empty<ScanWalls.Wall>(), Roads = Array.Empty<ScanRoadEdges.EdgeLine>(), FixedBreaklines = Array.Empty<double[]>() };
        ScanBuildings.Result? buildings = null;
        IReadOnlyList<ScanWalls.Wall> walls = Array.Empty<ScanWalls.Wall>();
        ScanSurfaceAreas.Result? areas = null;
        IReadOnlyList<ScanRoadEdges.EdgeLine> roads = Array.Empty<ScanRoadEdges.EdgeLine>();
        var fixedLines = new List<double[]>();
        Result Snapshot() => new() { Ground = ground, Reference = reference, Walls = walls, Roads = roads, Areas = areas,
            Buildings = buildings, FixedBreaklines = fixedLines.ToArray(), SurfaceColours = colours };
        options.ReportPreview?.Invoke(Snapshot());
        if (options.Buildings != null)
        {
            options.ReportProgress?.Invoke("Fitting building footprints and roof shapes");
            buildings = ScanBuildings.Extract(scan, ground, colours, options.Buildings, classes);
            options.ReportPreview?.Invoke(Snapshot());
        }
        options.ReportProgress?.Invoke("Tracing wall top and toe lines");
        walls = options.Walls == null ? Array.Empty<ScanWalls.Wall>() : ScanWalls.Trace(ground.Ground, options.Walls, reference);
        fixedLines.AddRange(walls.SelectMany(wall => new[] { wall.Top, wall.Toe }));
        if (fixedLines.Count > 0)
        {
            options.ReportProgress?.Invoke("Preparing the reference with measured wall lines");
            reference = Prepare(reference, fixedLines);
        }
        options.ReportProgress?.Invoke("Classifying and tracing paved and soft areas");
        options.ReportPreview?.Invoke(Snapshot());
        areas = options.Areas == null || colours == null ? null : ScanSurfaceAreas.Classify(ground, colours, options.Areas, reference);
        options.ReportPreview?.Invoke(Snapshot());
        options.ReportProgress?.Invoke("Measuring kerbs and road edges");
        roads = options.Roads == null ? Array.Empty<ScanRoadEdges.EdgeLine>() : areas == null
            ? ScanRoadEdges.TraceGeometry(ground, options.Roads, reference)
            : ScanRoadEdges.Trace(ground, areas, options.Roads, reference);
        if (areas != null)
            fixedLines.AddRange(areas.Boundaries.Where(boundary => boundary.Kind == ScanSurfaceKind.Paved).Select(boundary => boundary.Points));
        foreach (ScanRoadEdges.EdgeLine road in roads)
        {
            // A colour-only road edge is already represented by its closed paved boundary.
            if (areas != null && !road.IsKerb)
                continue;
            fixedLines.Add(road.Bottom);
            if (road.Top != null)
                fixedLines.Add(road.Top);
        }
        options.Ground.CancellationToken.ThrowIfCancellationRequested();
        if (fixedLines.Count > walls.Count * 2)
        {
            options.ReportProgress?.Invoke("Preparing the reference with measured breaklines");
            reference = Prepare(reference, fixedLines);
        }
        Result result = Snapshot(); options.ReportPreview?.Invoke(result); return result;

        IndexedTriMesh Prepare(IndexedTriMesh mesh, IReadOnlyList<double[]> lines)
        {
            IndexedTriMesh? prepared = TerrainCurveRecovery.ConstrainReference(mesh, lines, options.PlanTolerance, out string? error);
            if (prepared == null)
                throw new InvalidOperationException(error ?? "The measured scan lines could not be triangulated.");
            return prepared.Value;
        }
    }
}
