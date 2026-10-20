namespace MoleHill.Core.Processing;

/// <summary>Opt-in scan recovery policy; ordinary terrain extraction retains its existing contract.</summary>
public sealed record TerrainRecoveryPolicy
{
    public int PreferredContours { get; init; } = 300;
    public int MaximumInputs { get; init; } = 2000;

    /// <summary>
    /// Above zero, interval recovery may add this many inputs per square model unit of measured ground, in place
    /// of <see cref="MaximumInputs"/>. The tolerance decides how many are spent; the cap only stops a runaway, and
    /// a fixed count stopped a site twice the size short: two references both need about 195 per hectare.
    /// </summary>
    public double InputsPerArea { get; init; }
    public int PruneInterval { get; init; } = 25;

    /// <summary>
    /// Above zero, contours are drawn at this height interval, redundant ones pruned and spot heights added
    /// only where the reconstruction still misses, instead of chasing individual errors. Zero keeps the
    /// error-driven selection.
    /// </summary>
    public double ContourInterval { get; init; }

    /// <summary>
    /// Above zero, interval recovery holds the terrain's folds of at least this many degrees (batter crests and toes,
    /// pad edges, wall tops and toes) as breaklines, returned in <see cref="TerrainCurveRecovery.Result.CreaseLines"/>.
    /// Folds shorter than twelve plan tolerances are left to contours and spots.
    /// </summary>
    public double CreaseAngleDegrees { get; init; }

    /// <summary>Above zero, vertices of faces steeper than this are a wall's and are not measured (interval only).</summary>
    public double WallSlopeDegrees { get; init; }

    /// <summary>Contour pieces shorter than this are dropped before pruning; zero uses eight plan tolerances.</summary>
    public double MinimumContourLength { get; init; }
    public Action<string>? ReportActivity { get; init; }
}
