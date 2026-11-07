namespace MoleHill.Rhino.Services;

/// <summary>Immutable counts from one completed Scatter stage, shared by the panel and reports.</summary>
internal sealed record ScatterBuildSummary
{
    public required Guid DefinitionId { get; init; }
    public int Sampled { get; init; }
    public double? RegionArea { get; init; }
    public int OutsideRegion { get; init; }
    public int Excluded { get; init; }
    public int Setback { get; init; }
    public int Feathered { get; init; }
    public int TerrainMisses { get; init; }
    public int SlopeRejected { get; init; }
    public int ElevationRejected { get; init; }
    public int FootprintRejected { get; init; }
    public int Placed => BlockCounts.Values.Sum();
    public IReadOnlyDictionary<string, int> BlockCounts { get; init; } = new Dictionary<string, int>();
    public string? Message { get; init; }
    public string Breakdown => $"{OutsideRegion} outside region; {Excluded} exclusions; {Setback} setback; {Feathered} feathered; " +
        $"{TerrainMisses} terrain misses; {SlopeRejected} slope; {ElevationRejected} elevation; {FootprintRejected} whole-block fit";
}
