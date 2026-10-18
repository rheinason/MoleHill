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
        options.ReportProgress?.Invoke("Finding and filling the ground");
        ScanGround.Result ground = ScanGround.FromSurface(rasters.Top, options.Ground);
        IndexedTriMesh reference = ScanGroundMesher.Mesh(ground.Ground, options.GroundSpacing);
        if (reference.IsEmpty)
            return new Result { Ground = ground, Reference = reference, Walls = Array.Empty<ScanWalls.Wall>(), Roads = Array.Empty<ScanRoadEdges.EdgeLine>(), FixedBreaklines = Array.Empty<double[]>() };
        ScanBuildings.Result? buildings = null;
        if (options.Buildings != null)
        {
            options.ReportProgress?.Invoke("Fitting building footprints and roof shapes");
            buildings = ScanBuildings.Extract(scan, ground, rasters.Colours, options.Buildings);
        }
        options.ReportProgress?.Invoke("Tracing wall top and toe lines");
        IReadOnlyList<ScanWalls.Wall> walls = options.Walls == null ? Array.Empty<ScanWalls.Wall>() : ScanWalls.Trace(ground.Ground, options.Walls, reference);
        var fixedLines = walls.SelectMany(wall => new[] { wall.Top, wall.Toe }).ToList();
        if (fixedLines.Count > 0)
        {
            options.ReportProgress?.Invoke("Preparing the reference with measured wall lines");
            reference = Prepare(reference, fixedLines);
        }
        options.ReportProgress?.Invoke("Classifying and tracing paved and soft areas");
        ScanSurfaceAreas.Result? areas = options.Areas == null || colours == null ? null : ScanSurfaceAreas.Classify(ground, rasters.Colours, options.Areas, reference);
        options.ReportProgress?.Invoke("Measuring kerbs and road edges");
        IReadOnlyList<ScanRoadEdges.EdgeLine> roads = options.Roads == null ? Array.Empty<ScanRoadEdges.EdgeLine>() : areas == null
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
        return new Result { Ground = ground, Reference = reference, Walls = walls, Roads = roads, Areas = areas, Buildings = buildings, FixedBreaklines = fixedLines, SurfaceColours = rasters.Colours };

        IndexedTriMesh Prepare(IndexedTriMesh mesh, IReadOnlyList<double[]> lines)
        {
            IndexedTriMesh? prepared = TerrainCurveRecovery.ConstrainReference(mesh, lines, options.PlanTolerance, out string? error);
            if (prepared == null)
                throw new InvalidOperationException(error ?? "The measured scan lines could not be triangulated.");
            return prepared.Value;
        }
    }
}
