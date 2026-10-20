namespace MoleHill.Core.Processing;

/// <summary>Opt-in scan recovery policy; ordinary terrain extraction retains its existing contract.</summary>
public sealed record TerrainRecoveryPolicy
{
    public int PreferredContours { get; init; } = 300;
    public int MaximumInputs { get; init; } = 2000;
    public int PruneInterval { get; init; } = 25;

    /// <summary>
    /// Above zero, contours are drawn at this height interval, redundant ones pruned and spot heights added
    /// only where the reconstruction still misses, instead of chasing individual errors. Zero keeps the
    /// error-driven selection.
    /// </summary>
    public double ContourInterval { get; init; }

    /// <summary>Contour pieces shorter than this are dropped before pruning; zero uses eight plan tolerances.</summary>
    public double MinimumContourLength { get; init; }
    public Action<string>? ReportActivity { get; init; }
}
