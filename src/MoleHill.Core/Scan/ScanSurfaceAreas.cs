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
        public double? BuiltEdgePlanTolerance { get; init; }
        /// <summary>Radius over which class evidence is averaged before deciding; null uses 0.6 x plan tolerance.</summary>
        public double? SmoothingRadius { get; init; }
        /// <summary>Raised objects up to this height (cars, shrubs) may be filled by the class around them; null fills by size alone.</summary>
        public double? MaximumFilledObjectHeight { get; init; }
        /// <summary>Soft ground joined only by strips narrower than this is outlined as separate areas; null keeps it whole.</summary>
        public double? SoftNeckWidth { get; init; }
        public CancellationToken CancellationToken { get; init; }
    }

    public sealed record Boundary(ScanSurfaceKind Kind, double[] Points, ScanCurveGeometry Curve)
    {
        /// <summary>Retained for region topology, but its complete interface is already drawn by another family.</summary>
        public bool IsSharedDuplicate { get; init; }
    }
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
        Validate(options);
        var classes = new ScanSurfaceKind[grid.NodeCount];
        if (colours == null)
            return new Result { Ground = grid, Classes = classes, PavedMask = new bool[classes.Length], SoftMask = new bool[classes.Length], Boundaries = Array.Empty<Boundary>() };
        return Classify(ground, ScanColourLattice.FromGround(grid, colours), options, footprint);
    }

    /// <summary>Classifies on a colour lattice that may be finer than the ground grid; classes are reported per ground node.</summary>
    internal static Result Classify(ScanGround.Result ground, ScanColourLattice lattice, Options options, IndexedTriMesh? footprint = null)
    {
        ArgumentNullException.ThrowIfNull(ground);
        Validate(options);
        ScanGrid grid = ground.Ground;
        var projector = footprint is IndexedTriMesh mesh ? new MeshHeightProjector(mesh.Vertices, mesh.VertexCount, mesh.Faces, mesh.FaceCount) : null;
        byte[] labels = ScanSurfaceClassifier.Classify(ground, lattice, options, projector, options.SmoothingRadius ?? options.PlanTolerance * .6);
        bool[] pavedFine = labels.Select(value => value == (byte)ScanSurfaceKind.Paved).ToArray();
        bool[] softFine = labels.Select(value => value == (byte)ScanSurfaceKind.Soft).ToArray();
        // Drawing masks pass over small enclosed objects; observed classes never claim ground that was removed.
        var classes = new ScanSurfaceKind[grid.NodeCount];
        bool[] pavedMask = new bool[grid.NodeCount], softMask = new bool[grid.NodeCount];
        for (int row = 0; row < grid.Rows; row++)
            for (int column = 0; column < grid.Columns; column++)
            {
                int node = row * grid.Columns + column;
                var kind = (ScanSurfaceKind)labels[row * lattice.Factor * lattice.Columns + column * lattice.Factor];
                pavedMask[node] = kind == ScanSurfaceKind.Paved;
                softMask[node] = kind == ScanSurfaceKind.Soft;
                classes[node] = ground.IsGround[node] && ground.Domain[node] ? kind : ScanSurfaceKind.Unknown;
            }
        int[]? softRegions = options.SoftNeckWidth is double neck
            ? ScanSoftNecks.Regions(softFine, lattice.Columns, lattice.Rows, neck * .5 / lattice.CellSize,
                Math.Max(1, (int)Math.Ceiling(options.MinimumArea / (lattice.CellSize * lattice.CellSize))), options.CancellationToken)
            : null;
        var boundaries = ScanSurfaceGraph.Build(grid, lattice, pavedFine, softFine, options, projector, softRegions);
        return new Result { Ground = grid, Classes = classes, PavedMask = pavedMask, SoftMask = softMask, Boundaries = boundaries };
    }

    private static void Validate(Options options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!double.IsFinite(options.ExcessGreen) || options.ExcessGreen < 0 || options.ExcessGreen > 2 ||
            !double.IsFinite(options.MaximumPavedSaturation) || options.MaximumPavedSaturation < 0 || options.MaximumPavedSaturation > 1 ||
            !Positive(options.MaximumPavedRoughness) || !Positive(options.MaximumPavedSlope) || !Positive(options.MinimumArea) ||
            options.BuiltEdgePlanTolerance.HasValue && !Positive(options.BuiltEdgePlanTolerance.Value) ||
            options.SmoothingRadius is double radius && (!double.IsFinite(radius) || radius < 0) ||
            options.SoftNeckWidth.HasValue && !Positive(options.SoftNeckWidth.Value) ||
            !Positive(options.PlanTolerance) || !Positive(options.VerticalTolerance))
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    private static bool Positive(double value) => double.IsFinite(value) && value > 0;
}
