using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Rhino.Geometry;
using MoleHill.Rhino.Model;

namespace MoleHill.Rhino.Services;

internal sealed record TerrainBuildTiming(string Stage, TimeSpan Elapsed, string? Detail, bool IsCacheHit);

internal sealed class TerrainObjectPlacement
{
    public Guid ObjectId { get; init; }

    public Transform AppliedTransform { get; init; } = Transform.Identity;
}

internal sealed class TerrainObjectPlacementGroup
{
    public Guid DefinitionId { get; init; }

    public List<TerrainObjectPlacement> Placements { get; } = new();
}

internal sealed class TerrainBuildResult
{
    public TerrainBuildMode Mode { get; set; } = TerrainBuildMode.Final;

    public bool HasDeferredOutputs { get; set; }

    /// <summary>
    /// How long the final-only output stages took after the mesh was complete. Fed back into
    /// <see cref="TerrainRuntimeCache.PeakDependentOutputsDuration"/> so the next build can decide
    /// whether publishing its geometry early is worth an extra copy and redraw.
    /// </summary>
    public TimeSpan DependentOutputsElapsed { get; set; }

    public Mesh? PrimaryMesh { get; set; }

    public Mesh? BaseMesh { get; set; }

    public List<GeneratedRhinoObject> ZoneObjects { get; } = new();

    public List<TerrainRegionState> TerrainRegions { get; } = new();

    public List<ZoneAnalysisSummary> ZoneAnalysisResults { get; } = new();

    public List<GeneratedRhinoObject> AuxiliaryObjects { get; } = new();

    public List<GeneratedRhinoObject> MarkerObjects { get; } = new();

    /// <summary>
    /// The final-mesh fingerprint of every other terrain this build compared against, as it was when the
    /// build was snapshotted (0 when that terrain had no finished mesh). Lets a card tell that the terrain
    /// it compares to has changed since these results were computed. See
    /// <see cref="TerrainBuildSnapshot.ReferencedTerrainFingerprints"/>.
    /// </summary>
    public IReadOnlyDictionary<Guid, ulong> ReferencedTerrainFingerprints { get; set; } = new Dictionary<Guid, ulong>();

    public List<GeneratedRhinoObject> ScatterObjects { get; } = new();

    public List<TerrainObjectPlacementGroup> ObjectPlacements { get; } = new();

    public List<string> Diagnostics { get; } = new();

    public List<GradingDiagnostic> StructuredDiagnostics { get; } = new();

    public List<TerrainAnalysisSummary> AnalysisResults { get; } = new();

    public List<TerrainBuildTiming> Timings { get; } = new();

    public List<ConstraintPolyline> PersistentHardConstraints { get; } = new();

    public List<ConstraintPolyline> PersistentElevationConstraints { get; } = new();

    /// <summary>Runtime-only guide and diagnostic geometry. It is displayed by the conduit and is never baked.</summary>
    public List<RuntimeOverlayItem> RuntimeOverlays { get; } = new();

    public void RecordTiming(
        string stage,
        TimeSpan elapsed,
        string? detail = null,
        int diagnosticThresholdMs = int.MaxValue,
        bool isCacheHit = false)
    {
        bool resolvedCacheHit = isCacheHit ||
            detail?.Contains("cache hit", StringComparison.OrdinalIgnoreCase) == true;
        Timings.Add(new TerrainBuildTiming(stage, elapsed, detail, resolvedCacheHit));
        if (elapsed.TotalMilliseconds < diagnosticThresholdMs)
            return;

        Diagnostics.Add(FormatTimingMessage(stage, elapsed, detail));
    }

    public void AddGradingDiagnostics(GradingResult result)
    {
        Diagnostics.AddRange(result.Diagnostics);
        StructuredDiagnostics.AddRange(result.StructuredDiagnostics);
    }

    private static string FormatTimingMessage(string stage, TimeSpan elapsed, string? detail)
    {
        string message = $"timing.{stage}: {elapsed.TotalSeconds:0.##} s";
        return string.IsNullOrWhiteSpace(detail)
            ? message
            : $"{message} | {detail}";
    }
}
