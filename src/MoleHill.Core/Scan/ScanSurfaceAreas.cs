using MoleHill.Core.Analysis;
using MoleHill.Core.Engine;
using MoleHill.Core.Processing;
using MoleHill.Core.Routing;

namespace MoleHill.Core.Scan;

/// <summary>Colour and local geometry split observed ground into paved and soft areas, retaining holes.</summary>
public static class ScanSurfaceAreas
{
    public sealed class Options
    {
        /// <summary>Normalised excess green (2G-R-B)/(R+G+B), independent of brightness.</summary>
        public double ExcessGreen { get; init; } = 0.08;
        public double MaximumPavedSaturation { get; init; } = 0.25;
        public double MaximumPavedSlope { get; init; } = 0.65;
        public required double MaximumPavedRoughness { get; init; }
        public required double MinimumArea { get; init; }
        public required double PlanTolerance { get; init; }
        public required double VerticalTolerance { get; init; }
        public CancellationToken CancellationToken { get; init; }
    }

    public sealed record Boundary(ScanSurfaceKind Kind, double[] Points, FittedRouteCurve Curve);
    public sealed class Result
    {
        public required ScanGrid Ground { get; init; }
        public required ScanSurfaceKind[] Classes { get; init; }
        public required bool[] PavedMask { get; init; }
        public required bool[] SoftMask { get; init; }
        public required IReadOnlyList<Boundary> Boundaries { get; init; }
        public double PavedArea => PavedMask.Count(value => value) * Ground.NodeArea;
        public double SoftArea => SoftMask.Count(value => value) * Ground.NodeArea;
    }

    public static Result Classify(ScanGround.Result ground, int[]? colours, Options options, IndexedTriMesh? footprint = null)
    {
        ArgumentNullException.ThrowIfNull(ground);
        ArgumentNullException.ThrowIfNull(options);
        ScanGrid grid = ground.Ground;
        if (colours != null && colours.Length != grid.NodeCount)
            throw new ArgumentException("Colours must align with the ground lattice.", nameof(colours));
        if (!double.IsFinite(options.ExcessGreen) || options.ExcessGreen < 0 || options.ExcessGreen > 2 ||
            !double.IsFinite(options.MaximumPavedSaturation) || options.MaximumPavedSaturation < 0 || options.MaximumPavedSaturation > 1 ||
            !Positive(options.MaximumPavedRoughness) || !Positive(options.MaximumPavedSlope) ||
            !Positive(options.MinimumArea) || !Positive(options.PlanTolerance) || !Positive(options.VerticalTolerance))
            throw new ArgumentOutOfRangeException(nameof(options));
        var classes = new ScanSurfaceKind[grid.NodeCount];
        if (colours == null)
            return new Result { Ground = grid, Classes = classes, PavedMask = new bool[classes.Length], SoftMask = new bool[classes.Length], Boundaries = Array.Empty<Boundary>() };
        var projector = footprint is IndexedTriMesh mesh ? new MeshHeightProjector(mesh.Vertices, mesh.VertexCount, mesh.Faces, mesh.FaceCount) : null;
        for (int row = 0; row < grid.Rows; row++)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            for (int column = 0; column < grid.Columns; column++)
            {
                int node = row * grid.Columns + column;
                if (!ground.IsGround[node] || !ground.Domain[node] || colours[node] < 0 || !double.IsFinite(grid.Values[node]))
                    continue;
                if (projector != null && !FitsFootprint(column, row))
                    continue;
                int rgb = colours[node], r = rgb >> 16 & 255, g = rgb >> 8 & 255, b = rgb & 255;
                if (r + g + b < 6)
                    continue;
                double exg = (2.0 * g - r - b) / (r + g + b);
                double maximum = Math.Max(r, Math.Max(g, b)), minimum = Math.Min(r, Math.Min(g, b));
                double saturation = maximum > 0 ? (maximum - minimum) / maximum : 0;
                (double slope, double roughness) = Geometry(column, row);
                if (exg >= options.ExcessGreen && g > r && g > b)
                    classes[node] = ScanSurfaceKind.Soft;
                else if (saturation <= options.MaximumPavedSaturation && slope <= options.MaximumPavedSlope && roughness <= options.MaximumPavedRoughness)
                    classes[node] = ScanSurfaceKind.Paved;
                else if (roughness > options.MaximumPavedRoughness)
                    classes[node] = ScanSurfaceKind.Soft;
            }
        }
        CullSmall(ScanSurfaceKind.Paved);
        CullSmall(ScanSurfaceKind.Soft);
        bool[] pavedMask = AreaMask(ScanSurfaceKind.Paved), softMask = AreaMask(ScanSurfaceKind.Soft);
        var boundaries = new List<Boundary>();
        foreach (ScanSurfaceKind kind in new[] { ScanSurfaceKind.Paved, ScanSurfaceKind.Soft })
        {
            bool[] mask = kind == ScanSurfaceKind.Paved ? pavedMask : softMask;
            foreach (ScanGridEdges.Chain chain in ScanGridEdges.Trace(mask, grid, cancellationToken: options.CancellationToken))
            {
                options.CancellationToken.ThrowIfCancellationRequested();
                if (!chain.Closed || chain.Edges.Count < 4)
                    continue;
                var raw = new List<double>();
                foreach (ScanGridEdges.Edge edge in chain.Edges)
                {
                    (double x, double y, double z) = ScanGridEdges.Corner(grid, edge.Start);
                    if (projector != null && projector.TryGetHeightExtremes(x, y, grid.CellSize * 1e-7, out _, out double clean))
                        z = clean;
                    raw.Add(x); raw.Add(y); raw.Add(z);
                }
                FittedRouteCurve curve = ScanBoundaryFitter.Fit(raw.ToArray(), true, grid, projector,
                    options.PlanTolerance, options.VerticalTolerance, options.CancellationToken);
                if (curve.Pieces.Count > 0)
                    boundaries.Add(new Boundary(kind, TerrainCurveRecovery.SimplifyOpen(curve.PointsXyz,
                        options.PlanTolerance * .05, options.VerticalTolerance * .1), curve));
            }
        }
        return new Result { Ground = grid, Classes = classes, PavedMask = pavedMask, SoftMask = softMask, Boundaries = boundaries };

        bool[] AreaMask(ScanSurfaceKind kind)
        {
            bool[] mask = classes.Select(value => value == kind).ToArray();
            int[] holes = GridMorphology.Label(mask.Select(value => !value).ToArray(), grid.Columns, grid.Rows,
                out List<int> sizes, out List<bool> touchesEdge);
            double minimumNodes = options.MinimumArea / grid.NodeArea;
            for (int node = 0; node < mask.Length; node++)
                if (holes[node] >= 0 && !touchesEdge[holes[node]] && sizes[holes[node]] < minimumNodes)
                    mask[node] = true;
            return mask;
        }

        bool FitsFootprint(int column, int row)
        {
            for (int sx = -1; sx <= 1; sx += 2)
            {
                for (int sy = -1; sy <= 1; sy += 2)
                {
                    double x = grid.OriginX + Math.Clamp(column + sx * 0.5, 0, grid.Columns - 1) * grid.CellSize;
                    double y = grid.OriginY + Math.Clamp(row + sy * 0.5, 0, grid.Rows - 1) * grid.CellSize;
                    if (!projector!.TryGetHeightExtremes(x, y, grid.CellSize * 1e-7, out _, out _))
                        return false;
                }
            }
            return true;
        }

        (double Slope, double Roughness) Geometry(int column, int row)
        {
            double z = grid[column, row], slope = 0, roughness = 0;
            Check(column - 1, row, column + 1, row);
            Check(column, row - 1, column, row + 1);
            return (slope, roughness);
            void Check(int x0, int y0, int x1, int y1)
            {
                if (x0 < 0 || y0 < 0 || x1 >= grid.Columns || y1 >= grid.Rows)
                    return;
                double before = grid[x0, y0], after = grid[x1, y1];
                if (!double.IsFinite(before + after))
                    return;
                // A kerb beside a paved node is a height jump, not the pavement's crossfall.
                slope = Math.Max(slope, Math.Min(Math.Abs(z - before), Math.Abs(after - z)) / grid.CellSize);
                roughness = Math.Max(roughness, Math.Abs(z - (before + after) * 0.5));
            }
        }

        void CullSmall(ScanSurfaceKind kind)
        {
            bool[] mask = classes.Select(value => value == kind).ToArray();
            int[] labels = GridMorphology.Label(mask, grid.Columns, grid.Rows, out List<int> sizes, out _);
            double minimumNodes = options.MinimumArea / grid.NodeArea;
            for (int node = 0; node < classes.Length; node++)
                if (labels[node] >= 0 && sizes[labels[node]] < minimumNodes)
                    classes[node] = ScanSurfaceKind.Unknown;
        }
    }

    private static bool Positive(double value) => double.IsFinite(value) && value > 0;
}
