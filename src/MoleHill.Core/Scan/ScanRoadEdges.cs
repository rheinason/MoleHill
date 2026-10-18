using MoleHill.Core.Processing;
using MoleHill.Core.Routing;
using MoleHill.Core.Engine;
using MoleHill.Core.Analysis;
using MoleHill.Core.Geometry;

namespace MoleHill.Core.Scan;

/// <summary>Measures profiles across paved edges: persistent kerb steps become pairs, other edges one line.</summary>
public static class ScanRoadEdges
{
    public sealed class Options
    {
        public required double MinimumKerbHeight { get; init; }
        public required double MaximumKerbHeight { get; init; }
        public required double ProfileReach { get; init; }
        public required double MaximumKerbWidth { get; init; }
        public required double MinimumLength { get; init; }
        public required double PlanTolerance { get; init; }
        public required double VerticalTolerance { get; init; }
        /// <summary>Height tolerance for colour-only edges; null uses the measured kerb tolerance.</summary>
        public double? RoadVerticalTolerance { get; init; }
        public double EvidenceShare { get; init; } = 0.65;
        public CancellationToken CancellationToken { get; init; }
    }

    public sealed class EdgeLine
    {
        public required bool IsKerb { get; init; }
        public required bool IsClosed { get; init; }
        public required FittedRouteCurve BottomCurve { get; init; }
        public FittedRouteCurve? TopCurve { get; init; }
        public required double[] Bottom { get; init; }
        public double[]? Top { get; init; }
        public required double MeanStep { get; init; }
        public required double MeasuredShare { get; init; }
    }

    private readonly record struct Profile(bool Kerb, double Height,
        double BottomX, double BottomY, double BottomZ, double TopX, double TopY, double TopZ);

    /// <summary>Without colour, retain only long narrow measured steps in the kerb height range.</summary>
    public static IReadOnlyList<EdgeLine> TraceGeometry(ScanGround.Result ground, Options options, IndexedTriMesh reference)
    {
        ArgumentNullException.ThrowIfNull(ground);
        Validate(options);
        // Filled heights can describe the reference, but they cannot establish measured kerb evidence.
        double[] observed = new double[ground.Ground.NodeCount];
        for (int i = 0; i < observed.Length; i++)
        {
            if ((i & 4095) == 0)
                options.CancellationToken.ThrowIfCancellationRequested();
            observed[i] = ground.IsGround[i] && ground.Domain[i] ? ground.Ground.Values[i] : double.NaN;
        }
        IReadOnlyList<ScanWalls.Wall> steps = ScanWalls.Trace(ground.Ground.WithValues(observed), new()
        {
            MinimumHeight = options.MinimumKerbHeight,
            MinimumLength = options.MinimumLength,
            MaximumWidth = options.MaximumKerbWidth,
            BorderMargin = options.ProfileReach * 2,
            PlanTolerance = options.PlanTolerance,
            VerticalTolerance = options.VerticalTolerance,
            CancellationToken = options.CancellationToken,
        }, reference);
        return steps.Where(step => step.MinimumHeight >= options.MinimumKerbHeight && step.MaximumHeight <= options.MaximumKerbHeight).Select(step => new EdgeLine
        {
            IsKerb = true,
            IsClosed = step.TopCurve.Pieces[0].Start == step.TopCurve.Pieces[^1].End,
            BottomCurve = step.ToeCurve,
            TopCurve = step.TopCurve,
            Bottom = step.Toe,
            Top = step.Top,
            MeanStep = step.MeanHeight,
            MeasuredShare = 1,
        }).ToArray();
    }

    public static IReadOnlyList<EdgeLine> Trace(ScanGround.Result ground, ScanSurfaceAreas.Result areas, Options options,
        IndexedTriMesh? reference = null)
    {
        ArgumentNullException.ThrowIfNull(ground);
        ArgumentNullException.ThrowIfNull(areas);
        Validate(options);
        ScanGrid grid = ground.Ground;
        MeshHeightProjector? projector = reference is IndexedTriMesh mesh ? new(mesh.Vertices, mesh.VertexCount, mesh.Faces, mesh.FaceCount) : null;
        if (areas.Classes.Length != grid.NodeCount || areas.Ground.OriginX != grid.OriginX || areas.Ground.OriginY != grid.OriginY ||
            areas.Ground.CellSize != grid.CellSize || areas.Ground.Columns != grid.Columns || areas.Ground.Rows != grid.Rows)
            throw new ArgumentException("Surface classes must align with this ground lattice.", nameof(areas));
        bool[] paved = areas.PavedMask;
        List<ScanGridEdges.Chain> chains = ScanGridEdges.Trace(paved, grid,
            (_, outside) => outside >= 0 && ground.Domain[outside], options.CancellationToken);
        var result = new List<EdgeLine>();
        foreach (ScanGridEdges.Chain chain in chains)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            if (chain.Edges.Count * grid.CellSize < options.MinimumLength)
                continue;
            Profile[] profiles = chain.Edges.Select(Measure).ToArray();
            var kerb = new bool[profiles.Length];
            int halfWindow = Math.Max(1, (int)Math.Ceiling(options.ProfileReach / (2 * grid.CellSize)));
            for (int i = 0; i < profiles.Length; i++)
            {
                int hits = 0, count = 0;
                for (int j = Math.Max(0, i - halfWindow); j <= Math.Min(profiles.Length - 1, i + halfWindow); j++)
                {
                    count++;
                    if (profiles[j].Kerb)
                        hits++;
                }
                kerb[i] = profiles[i].Kerb && hits >= options.EvidenceShare * count;
            }
            int first = 0;
            while (first < profiles.Length)
            {
                int last = first + 1;
                while (last < profiles.Length && kerb[last] == kerb[first])
                    last++;
                bool paired = kerb[first] && (last - first - 1) * grid.CellSize >= options.MinimumLength;
                AddRun(first, last, paired);
                first = last;
            }

            void AddRun(int first, int last, bool paired)
            {
                options.CancellationToken.ThrowIfCancellationRequested();
                bool closed = chain.Closed && first == 0 && last == chain.Edges.Count;
                var bottom = new List<double>();
                var top = new List<double>();
                if (paired)
                {
                    for (int i = first; i < last; i++)
                    {
                        Profile p = profiles[i];
                        Append(bottom, p.BottomX, p.BottomY, p.BottomZ);
                        Append(top, p.TopX, p.TopY, p.TopZ);
                    }
                    if (closed)
                    {
                        bottom.AddRange(bottom.Take(3).ToArray());
                        top.AddRange(top.Take(3).ToArray());
                    }
                }
                else
                {
                    for (int i = first; i < last; i++)
                    {
                        (double x, double y, double z) = ScanGridEdges.Corner(grid, chain.Edges[i].Start);
                        if (projector != null && projector.TryGetHeightExtremes(x, y, grid.CellSize * 1e-7, out _, out double clean))
                            z = clean;
                        Append(bottom, x, y, z);
                    }
                    (double ex, double ey, double ez) = ScanGridEdges.Corner(grid, chain.Edges[last - 1].End);
                    if (projector != null && projector.TryGetHeightExtremes(ex, ey, grid.CellSize * 1e-7, out _, out double endClean))
                        ez = endClean;
                    Append(bottom, ex, ey, ez);
                }
                if (bottom.Count < 6 || paired && top.Count < 6)
                    return;
                double plan = Math.Min(options.PlanTolerance, grid.CellSize * 0.1);
                FittedRouteCurve Fit(List<double> points)
                {
                    double[] light = TerrainCurveRecovery.SimplifyOpen(points.ToArray(), grid.CellSize * 0.65, options.VerticalTolerance);
                    return RouteCurveFitter.Fit(new IReadOnlyList<double>[] { light }, plan, options.VerticalTolerance, 0, 0, 0);
                }
                FittedRouteCurve bottomCurve = paired ? Fit(bottom) : ScanBoundaryFitter.Fit(bottom.ToArray(), closed,
                    grid, projector, options.PlanTolerance, options.RoadVerticalTolerance ?? options.VerticalTolerance, options.CancellationToken, straightPlan: true);
                FittedRouteCurve? topCurve = paired ? Fit(top) : null;
                if (bottomCurve.Pieces.Count == 0 || paired && topCurve!.Pieces.Count == 0)
                    return;
                double runLength = 0;
                double[] runPoints = bottomCurve.PointsXyz;
                for (int i = 3; i < runPoints.Length; i += 3)
                    runLength += Math.Sqrt(Geometry2D.DistanceSquared(runPoints[i - 3], runPoints[i - 2], runPoints[i], runPoints[i + 1]));
                if (runLength < options.MinimumLength)
                    return;
                result.Add(new EdgeLine
                {
                    IsKerb = paired,
                    IsClosed = closed,
                    BottomCurve = bottomCurve,
                    TopCurve = topCurve,
                    Bottom = TerrainCurveRecovery.SimplifyOpen(bottomCurve.PointsXyz, plan * 0.1, options.VerticalTolerance * 0.1),
                    Top = topCurve == null ? null : TerrainCurveRecovery.SimplifyOpen(topCurve.PointsXyz, plan * 0.1, options.VerticalTolerance * 0.1),
                    MeanStep = profiles.Skip(first).Take(last - first).Where(p => double.IsFinite(p.Height)).Select(p => p.Height).DefaultIfEmpty(0).Average(),
                    MeasuredShare = profiles.Skip(first).Take(last - first).Count(p => p.Kerb) / (double)(last - first),
                });
            }
        }
        return result;

        Profile Measure(ScanGridEdges.Edge edge)
        {
            int a = edge.Inside, b = edge.Outside;
            double px = grid.NodeX(a % grid.Columns), py = grid.NodeY(a / grid.Columns);
            double qx = grid.NodeX(b % grid.Columns), qy = grid.NodeY(b / grid.Columns);
            double nx = (qx - px) / grid.CellSize, ny = (qy - py) / grid.CellSize;
            double cx = (px + qx) * 0.5, cy = (py + qy) * 0.5;
            double reach = Math.Max(grid.CellSize * 1.5, options.ProfileReach);
            if (!Known(-reach, out double low) || !Known(-reach - grid.CellSize, out double lowFar) ||
                !Known(reach, out double high) || !Known(reach + grid.CellSize, out double highFar))
                return new(false, double.NaN, px, py, grid.Values[a], qx, qy, grid.Values[b]);
            double slopeLow = (low - lowFar) / grid.CellSize, slopeHigh = (highFar - high) / grid.CellSize;
            double lowAtEdge = low + slopeLow * reach, highAtEdge = high - slopeHigh * reach;
            double step = highAtEdge - lowAtEdge;
            if (step < options.MinimumKerbHeight || step > options.MaximumKerbHeight)
                return new(false, step, px, py, grid.Values[a], qx, qy, grid.Values[b]);
            double bottomT = -reach, topT = reach;
            bool foundBottom = false, foundTop = false;
            double tolerance = Math.Max(options.VerticalTolerance, step * 0.15);
            for (double t = -reach; t <= reach + grid.CellSize * 0.01; t += grid.CellSize * 0.5)
            {
                if (!Known(t, out double z))
                    return new(false, step, px, py, grid.Values[a], qx, qy, grid.Values[b]);
                if (!foundBottom && Math.Abs(z - (lowAtEdge + slopeLow * t)) > tolerance)
                {
                    bottomT = Math.Max(-reach, t - grid.CellSize * 0.5);
                    foundBottom = true;
                }
                if (foundBottom && !foundTop && Math.Abs(z - (highAtEdge + slopeHigh * t)) <= tolerance)
                {
                    topT = t;
                    foundTop = true;
                }
            }
            if (!foundBottom || !foundTop || topT - bottomT > options.MaximumKerbWidth)
                return new(false, step, px, py, grid.Values[a], qx, qy, grid.Values[b]);
            Known(bottomT, out double bottomZ);
            Known(topT, out double topZ);
            return new(true, step, cx + nx * bottomT, cy + ny * bottomT, bottomZ,
                cx + nx * topT, cy + ny * topT, topZ);

            bool Known(double t, out double z)
            {
                double x = cx + nx * t, y = cy + ny * t;
                int column = (int)Math.Round((x - grid.OriginX) / grid.CellSize), row = (int)Math.Round((y - grid.OriginY) / grid.CellSize);
                z = double.NaN;
                return column >= 0 && row >= 0 && column < grid.Columns && row < grid.Rows &&
                    ground.IsGround[row * grid.Columns + column] && ScanGridSampling.TryHeight(grid, x, y, out z);
            }
        }
    }

    private static void Append(List<double> points, double x, double y, double z)
    {
        if (points.Count > 0 && points[^3] == x && points[^2] == y)
            return;
        points.Add(x); points.Add(y); points.Add(z);
    }

    private static bool Positive(double value) => double.IsFinite(value) && value > 0;

    private static void Validate(Options options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Positive(options.MinimumKerbHeight) || !Positive(options.MaximumKerbHeight) || options.MaximumKerbHeight < options.MinimumKerbHeight ||
            !Positive(options.ProfileReach) || !Positive(options.MaximumKerbWidth) || !Positive(options.MinimumLength) ||
            !Positive(options.PlanTolerance) || !Positive(options.VerticalTolerance) || !double.IsFinite(options.EvidenceShare) ||
            options.RoadVerticalTolerance is double roadTolerance && !Positive(roadTolerance) ||
            options.EvidenceShare <= 0 || options.EvidenceShare > 1)
            throw new ArgumentOutOfRangeException(nameof(options));
    }
}
