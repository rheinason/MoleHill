using MoleHill.Core.Engine;
using MoleHill.Core.Processing;
using MoleHill.Core.Scan;

namespace MoleHill.Rhino.Services;

/// <summary>Source-independent preview/result. Scan preparation is optional; ordinary terrains use
/// their final mesh directly and never acquire inferred-ground semantics.</summary>
internal sealed record TerrainCurveExtractionPrepared(IndexedTriMesh Reference,
    TerrainCurveRecovery.Result? Recovery = null, ScanTerrainPipeline.Result? Scan = null)
{
    public IReadOnlyList<double[]> FixedBreaklines { get; init; } = Array.Empty<double[]>();
    public ITerrainRecoveryRegion? Region { get; init; }

    public static TerrainCurveExtractionPrepared FromScan(ScanTerrainPipeline.Result scan) =>
        new(scan.Reference, Scan: scan) { FixedBreaklines = scan.FixedBreaklines, Region = scan.Region };
}
