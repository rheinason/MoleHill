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
        public double NoiseFeatureSize { get; init; }
        public double MaximumBridgeLength { get; init; }
        public bool IncludeAreaBoundaries { get; init; } = true;
        public double NoiseHeightAllowance { get; init; }
        /// <summary>
        /// Soft ground departing this far from its local mean (bushes, hedges, bed edges) is measured with the
        /// paving instead of contoured; zero contours all soft ground.
        /// </summary>
        public double RoughGroundHeight { get; init; }
        /// <summary>
        /// Ground standing more than <see cref="StandingObjectHeight"/> above an opening this wide (cars, bushes,
        /// hedges the ground filter kept for being small) is lowered onto the opened floor; zero keeps it.
        /// </summary>
        public double StandingObjectWidth { get; init; }
        public double StandingObjectHeight { get; init; }
        /// <summary>Standing objects taller than this are not lowered (they are walls or the filter's building bands).</summary>
        public double StandingObjectMaximumHeight { get; init; } = double.PositiveInfinity;
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
        public bool IncludeAreaBoundaries { get; init; } = true;
        public IndexedTriMesh? OriginalReference { get; init; }
        public int FilteredNodeCount { get; init; }
        public double FilterMaximumDisplacement { get; init; }
        /// <summary>Ground area lowered as standing objects, and the tallest object lowered.</summary>
        public double LoweredObjectArea { get; init; }
        public double LoweredObjectHeight { get; init; }
        public int RejectedFeatureCount { get; init; }
        public double InferredFeatureLength { get; init; }
        public required IReadOnlyList<double[]> FixedBreaklines { get; init; }
        /// <summary>Rough soft ground on the ground grid; null when not separated.</summary>
        public bool[]? RoughGround { get; init; }

        /// <summary>Smooth soft ground: where contours describe the terrain at its accuracy.</summary>
        public ITerrainRecoveryRegion? Region => Areas == null || !IncludeAreaBoundaries ? null : ScanRecoveryRegion.Contoured(Areas, RoughGround);

        /// <summary>
        /// Paving (carried by its outline breaklines) and rough soft ground, measured at a looser tolerance with
        /// spot heights only. Without it, paving would be neither contoured nor measured.
        /// </summary>
        public ITerrainRecoveryRegion? SecondaryRegion => Areas == null || !IncludeAreaBoundaries ? null : ScanRecoveryRegion.Loose(Areas, RoughGround);
    }

    public static Result Build(IndexedTriMesh scan, ScanColourSource? colours, Options options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);
        options.ReportProgress?.Invoke("Rasterising scan triangles and colours");
        double cell = options.Ground.CellSize > 0 ? options.Ground.CellSize : ScanRasterizer.AutomaticCellSize(scan);
        ScanRasterizer.Rasters rasters = ScanRasterizer.Rasterize(scan, cell, options.Ground.CancellationToken, colours);
        ScanColourLattice? lattice = null;
        if (colours != null && rasters.Colours != null && options.Areas != null)
        {
            int factor = ScanColourLattice.ChooseFactor(rasters.Top, colours);
            lattice = factor > 1
                ? ScanRasterizer.RasterizeColours(scan, rasters.Top, factor, colours, options.Ground.CancellationToken)
                : ScanColourLattice.FromGround(rasters.Top, rasters.Colours);
        }
        return BuildRasters(scan, rasters.Top, rasters.Colours, options, source: new ScanSourceSampler(scan), lattice: lattice);
    }

    public static Result Build(ScanPointCloud cloud, Options options)
    {
        ArgumentNullException.ThrowIfNull(cloud); ArgumentNullException.ThrowIfNull(options);
        Validate(options);
        options.ReportProgress?.Invoke("Binning cloud observations and colours");
        double cell = options.Ground.CellSize > 0 ? options.Ground.CellSize : cloud.AutomaticCellSize();
        ScanPointCloud.Raster raster = cloud.Rasterize(cell, options.Ground.CancellationToken);
        IndexedTriMesh roofs = options.Buildings == null ? IndexedTriMesh.Empty : raster.RoofMesh(options.Ground.CancellationToken);
        return BuildRasters(roofs, raster.Top, raster.Colours, options, raster.ClassifiedGround, raster.Classes, new ScanSourceSampler(cloud, cell));
    }

    private static void Validate(Options options)
    {
        ArgumentNullException.ThrowIfNull(options.Ground);
        if (!double.IsFinite(options.GroundSpacing) || options.GroundSpacing <= 0 ||
            !double.IsFinite(options.PlanTolerance) || options.PlanTolerance <= 0 ||
            !double.IsFinite(options.NoiseFeatureSize) || options.NoiseFeatureSize < 0 ||
            !double.IsFinite(options.NoiseHeightAllowance) || options.NoiseHeightAllowance < 0 ||
            !double.IsFinite(options.RoughGroundHeight) || options.RoughGroundHeight < 0 ||
            !double.IsFinite(options.StandingObjectWidth) || options.StandingObjectWidth < 0 ||
            !double.IsFinite(options.StandingObjectHeight) || options.StandingObjectHeight < 0 ||
            double.IsNaN(options.StandingObjectMaximumHeight) || options.StandingObjectMaximumHeight < 0 ||
            !double.IsFinite(options.MaximumBridgeLength) || options.MaximumBridgeLength < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Scan distances must be finite; spacing and tolerance must be positive.");
    }

    private static Result BuildRasters(IndexedTriMesh scan, ScanGrid top, int[]? colours, Options options,
        ScanGrid? classifiedGround = null, byte[]? classes = null, ScanSourceSampler? source = null, ScanColourLattice? lattice = null)
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
        IndexedTriMesh? originalReference = null;
        int filteredNodes = 0, rejectedFeatures = 0, loweredNodes = 0; double filterDisplacement = 0, loweredHeight = 0;
        bool[]? rough = null;
        Result Snapshot() => new()
        {
            Ground = ground,
            Reference = reference,
            Walls = walls,
            Roads = roads,
            Areas = areas,
            RoughGround = rough,
            Buildings = buildings,
            FixedBreaklines = fixedLines.ToArray(),
            SurfaceColours = colours,
            IncludeAreaBoundaries = options.IncludeAreaBoundaries,
            OriginalReference = originalReference,
            FilteredNodeCount = filteredNodes,
            FilterMaximumDisplacement = filterDisplacement,
            LoweredObjectArea = loweredNodes * ground.Ground.NodeArea,
            LoweredObjectHeight = loweredHeight,
            RejectedFeatureCount = rejectedFeatures,
            InferredFeatureLength = walls.Sum(w => w.TopCurve.InferredIntervals.Sum(i => i.End - i.Start)) +
                roads.Where(r => r.IsKerb).Sum(r => r.BottomCurve.InferredIntervals.Sum(i => i.End - i.Start))
        };
        options.ReportPreview?.Invoke(Snapshot());
        if (options.Buildings != null)
        {
            options.ReportProgress?.Invoke("Fitting building footprints and roof shapes");
            buildings = ScanBuildings.Extract(scan, ground, colours, options.Buildings, classes);
            options.ReportPreview?.Invoke(Snapshot());
        }
        options.ReportProgress?.Invoke("Tracing wall top and toe lines");
        walls = options.Walls == null ? Array.Empty<ScanWalls.Wall>() : ScanWalls.Trace(ground, options.Walls with
        { ReportRejected = reason => { rejectedFeatures++; options.Walls.ReportRejected?.Invoke(reason); } }, reference);
        if (options.Walls != null && options.MaximumBridgeLength > 0)
            walls = ScanFeatureBridge.Join(walls.Select(w => new ScanFeatureBridge.Pair(w.TopCurve, w.ToeCurve, w.Length, w.MeanHeight, w.MinimumHeight, w.MaximumHeight)).ToArray(),
                ground, options.MaximumBridgeLength, options.Walls.PlanTolerance, options.Walls.VerticalTolerance, options.Ground.CancellationToken)
                .Select(p => new ScanWalls.Wall
                {
                    TopCurve = p.High,
                    ToeCurve = p.Low,
                    Top = p.High.PointsXyz,
                    Toe = p.Low.PointsXyz,
                    Length = p.ObservedLength,
                    MeanHeight = p.Height,
                    MinimumHeight = p.MinimumHeight,
                    MaximumHeight = p.MaximumHeight
                }).ToArray();
        fixedLines.AddRange(walls.SelectMany(wall => new[] { wall.Top, wall.Toe }));
        if (fixedLines.Count > 0)
        {
            options.ReportProgress?.Invoke("Preparing the reference with measured wall lines");
            reference = Prepare(reference, fixedLines);
        }
        options.ReportProgress?.Invoke("Classifying and tracing paved and soft areas");
        options.ReportPreview?.Invoke(Snapshot());
        areas = options.Areas == null || colours == null ? null
            : ScanSurfaceAreas.Classify(ground, lattice ?? ScanColourLattice.FromGround(ground.Ground, colours), options.Areas, reference);
        if (areas != null && options.RoughGroundHeight > 0)
            rough = ScanRecoveryRegion.RoughGround(ground.Ground, options.GroundSpacing * .55, options.RoughGroundHeight, options.Ground.CancellationToken);
        options.ReportPreview?.Invoke(Snapshot());
        options.ReportProgress?.Invoke("Measuring kerbs and road edges");
        // With area outlines drawn, a colour-only road edge duplicates a paved outline: trace kerbs alone.
        var roadOptions = options.Roads == null ? null : options.Roads with
        {
            KerbsOnly = options.Roads.KerbsOnly || areas != null && options.IncludeAreaBoundaries,
            ReportRejected = reason => { rejectedFeatures++; options.Roads.ReportRejected?.Invoke(reason); }
        };
        roads = roadOptions == null ? Array.Empty<ScanRoadEdges.EdgeLine>() : areas == null
            ? ScanRoadEdges.TraceGeometry(ground, roadOptions, reference)
            : ScanRoadEdges.Trace(ground, areas, roadOptions, reference, source == null ? null : source.Height);
        if (options.Roads != null && options.MaximumBridgeLength > 0)
        {
            var kerbs = roads.Where(r => r.IsKerb && !r.IsClosed).ToArray();
            var joined = ScanFeatureBridge.Join(kerbs.Select(r => new ScanFeatureBridge.Pair(r.TopCurve!, r.BottomCurve,
                ScanFeatureBridge.Length(r.BottomCurve) * r.MeasuredShare, r.MeanStep)).ToArray(), ground,
                options.MaximumBridgeLength, options.Roads.PlanTolerance, options.Roads.VerticalTolerance, options.Ground.CancellationToken);
            roads = roads.Where(r => !r.IsKerb || r.IsClosed).Concat(joined.Select(p => new ScanRoadEdges.EdgeLine
            {
                IsKerb = true,
                IsClosed = false,
                BottomCurve = p.Low,
                TopCurve = p.High,
                Bottom = p.Low.PointsXyz,
                Top = p.High.PointsXyz,
                MeanStep = p.Height,
                MeasuredShare = p.Low.ObservedShare
            })).ToArray();
        }
        // Paving is close to planar between its edges: its simplified outlines, draped on the ground, are its
        // breaklines. Soft outlines stay drawings; contours describe that ground.
        if (areas != null && options.IncludeAreaBoundaries)
            fixedLines.AddRange(areas.Boundaries.Where(b => b.Kind == ScanSurfaceKind.Paved && !b.IsSharedDuplicate).Select(b => b.Points));
        foreach (ScanRoadEdges.EdgeLine road in roads)
        {
            // A colour-only road edge is already represented by its closed paved boundary.
            if (areas != null && options.IncludeAreaBoundaries && !road.IsKerb)
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
        ScanGrid surface = ground.Ground;
        if (options.StandingObjectWidth > 0 && options.StandingObjectHeight > 0)
        {
            options.ReportProgress?.Invoke("Lowering cars, bushes and other small standing objects");
            var lowered = ScanGroundCleaner.Lower(ground, fixedLines, options.StandingObjectWidth, options.StandingObjectHeight,
                options.StandingObjectMaximumHeight, options.Ground.CancellationToken);
            loweredNodes = lowered.ChangedNodes; loweredHeight = lowered.MaximumDisplacement; surface = lowered.Grid;
            // Lowering objects interprets the ground, as the ground filter does; the original reference is after it.
            if (loweredNodes > 0) reference = Prepare(ScanGroundMesher.Mesh(surface, options.GroundSpacing), fixedLines);
        }
        originalReference = reference;
        if (options.NoiseFeatureSize > 0 && options.NoiseHeightAllowance > 0)
        {
            options.ReportProgress?.Invoke("Removing isolated scan noise away from accepted features");
            var cleaned = ScanGroundCleaner.Clean(ground, fixedLines, options.NoiseFeatureSize,
                options.NoiseHeightAllowance, options.Ground.CancellationToken, surface);
            filteredNodes = cleaned.ChangedNodes; filterDisplacement = cleaned.MaximumDisplacement; surface = cleaned.Grid;
        }
        if (filteredNodes > 0) reference = Prepare(ScanGroundMesher.Mesh(surface, options.GroundSpacing), fixedLines);
        // A lowered bush no longer makes the lawn round it rough.
        if (rough != null && loweredNodes > 0)
            rough = ScanRecoveryRegion.RoughGround(surface, options.GroundSpacing * .55, options.RoughGroundHeight, options.Ground.CancellationToken);
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
