using System.Text;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using MoleHill.Rhino.Model;
using MoleHill.Shared;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Everything one terrain keeps between builds: the persistent <see cref="TinEngine"/>, the per-stage
/// caches, the last published display state, and the session-only measurements the scheduling policies
/// read. The UI thread owns one of these per terrain; a build runs against a worker copy
/// (<see cref="CreateWorkerCopy"/>) that is merged back on completion
/// (<see cref="ReplaceBuildCachesFrom"/>). Stage entries are shared by reference across that boundary,
/// so an entry is replaced, never mutated in place.
/// </summary>
internal sealed class TerrainRuntimeCache
{
    public TinEngine TinEngine { get; init; } = new();

    public Dictionary<string, StageCacheEntry> StageEntries { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, GradingTopologyCacheEntry> GradingTopologyEntries { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, SmoothStageCacheEntry> SmoothEntries { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Retaining-wall rail plans, keyed by stage key. Planning depends only on the wall curves and the
    /// wall tolerances - never on the terrain mesh - so it is cached separately from the wall stage,
    /// whose own fingerprint includes the upstream mesh. An upstream Z edit therefore misses the stage
    /// and still reuses the plan. See <see cref="RetainingWallPlanCacheEntry"/> for the ownership rule
    /// that applies to a cached plan's Breps.
    /// </summary>
    public Dictionary<string, RetainingWallPlanCacheEntry> RetainingWallPlanEntries { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The tiled Remesh's tile outputs from its last run, by stage key. When the Remesh stage has to run again,
    /// every tile whose input is unchanged comes from here, exactly as a cold build would make it. A memo is
    /// replaced, never modified, so a cancelled build cannot leave one half written.
    /// </summary>
    public Dictionary<string, MoleHill.Core.Engine.TiledIsotropicRemesher.TiledRemeshMemo> RemeshMemos { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The windowed graders' window results from their last run, by stage key (<c>GradingWindows.Memo</c>).
    /// Like <see cref="RemeshMemos"/>: replaced, never modified, and reused only by an identical window key.
    /// </summary>
    public Dictionary<string, MoleHill.Core.Grading.GradingWindows.Memo> GradingWindowMemos { get; } = new(StringComparer.Ordinal);

    public TerrainDisplayState? DisplayState { get; set; }

    /// <summary>
    /// Session-only UI preference. Worker copies and cache merges intentionally leave this set on
    /// the UI-owned cache so a background build cannot reset what the user chose to inspect.
    /// </summary>
    public HashSet<RuntimeOverlayOwner> VisibleDiagnosticOwners { get; } = new();

    public TerrainCoreCaseRecorder? CoreCaseRecorder { get; init; }

    public TimeSpan? LastPreviewDuration { get; set; }

    public TimeSpan? LastFinalDuration { get; set; }

    /// <summary>
    /// What the cards showed of this terrain's results after the last applied build
    /// (<see cref="TerrainCardResultSignature"/>); null until a build is applied. Mid-edit frames do not
    /// touch the cards, so a finished build compares against this rather than against them.
    /// </summary>
    public string? LastCardResultSignature { get; set; }

    /// <summary>
    /// The longest any final build in this session has spent on dependent outputs after its mesh was
    /// complete. Decides whether a build publishes its geometry early - see
    /// <see cref="TerrainInterimPublishPolicy"/>, which explains why this is a peak and not the last
    /// value: a cached-output build measures near zero, and keying on it would switch early publication
    /// off again right before the next expensive edit.
    /// </summary>
    public TimeSpan? PeakDependentOutputsDuration { get; set; }

    /// <summary>Raises <see cref="PeakDependentOutputsDuration"/> to cover this build.</summary>
    public void ObserveDependentOutputsDuration(TimeSpan elapsed)
    {
        if (PeakDependentOutputsDuration is not { } peak || elapsed > peak)
            PeakDependentOutputsDuration = elapsed;
    }

    public ulong LastFinalMeshFingerprint { get; set; }

    /// <summary>
    /// What the last applied final build compared against: each referenced terrain's final-mesh
    /// fingerprint as that build saw it. Null until a final build lands. Compared with the referenced
    /// terrain's current <see cref="LastFinalMeshFingerprint"/> to say "compared against an older version".
    /// </summary>
    public IReadOnlyDictionary<Guid, ulong>? ReferencedTerrainFingerprints { get; set; }

    /// <summary>
    /// On a worker copy, the stage meshes it was handed by the main cache. They stay the main cache's,
    /// so <see cref="DiscardOwnedMeshOutputs"/> must never dispose them. Null on the main cache.
    /// </summary>
    private HashSet<RhinoMesh>? _borrowedMeshOutputs;

    public TerrainRuntimeCache CreateWorkerCopy()
    {
        var copy = new TerrainRuntimeCache
        {
            // Share the persistent TinEngine so incremental edits (single spot-point add/remove)
            // stay reachable from Rhino background builds instead of forcing a full CDT every time
            // inputs change. TinEngine.Build is internally serialized by its own gate, so concurrent
            // old/new workers queue rather than race. Safe because Vertex.ID is re-keyed to
            // the current input index after every incremental edit.
            TinEngine = TinEngine,
            LastPreviewDuration = LastPreviewDuration,
            LastFinalDuration = LastFinalDuration,
            PeakDependentOutputsDuration = PeakDependentOutputsDuration
        };

        copy._borrowedMeshOutputs = new HashSet<RhinoMesh>(ReferenceEqualityComparer.Instance);
        foreach (var entry in StageEntries)
        {
            copy.StageEntries[entry.Key] = TerrainRuntimeCacheCloner.CloneStageCacheEntry(entry.Value);
            if (entry.Value.MeshOutput != null)
                copy._borrowedMeshOutputs.Add(entry.Value.MeshOutput);
        }

        foreach (var entry in GradingTopologyEntries)
            copy.GradingTopologyEntries[entry.Key] = entry.Value;

        foreach (var entry in SmoothEntries)
            copy.SmoothEntries[entry.Key] = entry.Value;

        foreach (var entry in RetainingWallPlanEntries)
            copy.RetainingWallPlanEntries[entry.Key] = entry.Value;

        foreach (var entry in RemeshMemos)
            copy.RemeshMemos[entry.Key] = entry.Value;

        foreach (var entry in GradingWindowMemos)
            copy.GradingWindowMemos[entry.Key] = entry.Value;

        return copy;
    }

    public List<RhinoMesh> ReplaceBuildCachesFrom(TerrainRuntimeCache source)
    {
        // Collect displaced meshes here; the controller disposes them after retired workers drain.
        var incomingMeshes = new HashSet<RhinoMesh>(ReferenceEqualityComparer.Instance);
        foreach (var entry in source.StageEntries.Values)
            if (entry.MeshOutput != null)
                incomingMeshes.Add(entry.MeshOutput);

        var displacedMeshes = new List<RhinoMesh>();
        var displacedSet = new HashSet<RhinoMesh>(ReferenceEqualityComparer.Instance);
        foreach (var entry in StageEntries.Values)
            if (entry.MeshOutput != null && !incomingMeshes.Contains(entry.MeshOutput))
                AddMeshOutput(displacedMeshes, displacedSet, entry.MeshOutput);

        StageEntries.Clear();
        foreach (var entry in source.StageEntries)
            StageEntries[entry.Key] = entry.Value;
        source.StageEntries.Clear();

        GradingTopologyEntries.Clear();
        foreach (var entry in source.GradingTopologyEntries)
            GradingTopologyEntries[entry.Key] = entry.Value;
        source.GradingTopologyEntries.Clear();

        SmoothEntries.Clear();
        foreach (var entry in source.SmoothEntries)
            SmoothEntries[entry.Key] = entry.Value;
        source.SmoothEntries.Clear();

        RetainingWallPlanEntries.Clear();
        foreach (var entry in source.RetainingWallPlanEntries)
            RetainingWallPlanEntries[entry.Key] = entry.Value;
        source.RetainingWallPlanEntries.Clear();

        RemeshMemos.Clear();
        foreach (var entry in source.RemeshMemos)
            RemeshMemos[entry.Key] = entry.Value;
        source.RemeshMemos.Clear();

        GradingWindowMemos.Clear();
        foreach (var entry in source.GradingWindowMemos)
            GradingWindowMemos[entry.Key] = entry.Value;
        source.GradingWindowMemos.Clear();

        return displacedMeshes;
    }

    /// <summary>
    /// Disposes the stage meshes this worker copy produced itself, for a build whose cache will never be
    /// merged (cancelled, failed, superseded, stale, retired, or its terrain gone). Borrowed meshes are
    /// left alone, and so is the shared <see cref="TinEngine"/> — unlike <see cref="Clear"/>, this is safe
    /// on a worker. Idempotent, and a no-op after <see cref="ReplaceBuildCachesFrom"/> has taken the
    /// entries. The stage meshes are cache clones, never the build's display meshes, so a superseded
    /// result that was published as a preview can still be discarded here.
    /// </summary>
    public void DiscardOwnedMeshOutputs()
    {
        if (_borrowedMeshOutputs == null)
            throw new InvalidOperationException("Only a worker copy can discard its own mesh outputs.");

        var owned = new List<RhinoMesh>();
        var ownedSet = new HashSet<RhinoMesh>(ReferenceEqualityComparer.Instance);
        foreach (var entry in StageEntries.Values)
            if (entry.MeshOutput != null && !_borrowedMeshOutputs.Contains(entry.MeshOutput))
                AddMeshOutput(owned, ownedSet, entry.MeshOutput);

        StageEntries.Clear();
        DisposeMeshOutputs(owned);
    }

    public void Clear()
    {
        DisposeMeshOutputs(DetachMeshOutputs());
    }

    public List<RhinoMesh> DetachMeshOutputs()
    {
        var meshOutputs = new List<RhinoMesh>();
        var meshSet = new HashSet<RhinoMesh>(ReferenceEqualityComparer.Instance);
        foreach (var entry in StageEntries.Values)
            if (entry.MeshOutput != null)
                AddMeshOutput(meshOutputs, meshSet, entry.MeshOutput);

        StageEntries.Clear();
        GradingTopologyEntries.Clear();
        SmoothEntries.Clear();
        RetainingWallPlanEntries.Clear();
        RemeshMemos.Clear();
        GradingWindowMemos.Clear();
        DisplayState = null;
        LastPreviewDuration = null;
        LastFinalDuration = null;
        LastCardResultSignature = null;
        PeakDependentOutputsDuration = null;
        LastFinalMeshFingerprint = 0;
        ReferencedTerrainFingerprints = null;
        TinEngine.InvalidateCache();
        return meshOutputs;
    }

    public void PruneUnused(IReadOnlySet<string> usedStageKeys, TerrainBuildMode mode)
    {
        string stagePrefix = GetStagePrefix(mode);
        if (usedStageKeys.Count == 0)
        {
            foreach (string stageKey in StageEntries.Keys.Where(key => key.StartsWith(stagePrefix, StringComparison.Ordinal)).ToList())
            {
                // Do not Dispose — see comment in the non-empty branch below.
                StageEntries.Remove(stageKey);
            }

            foreach (string stageKey in GradingTopologyEntries.Keys.Where(key => key.StartsWith(stagePrefix, StringComparison.Ordinal)).ToList())
                GradingTopologyEntries.Remove(stageKey);

            foreach (string stageKey in SmoothEntries.Keys.Where(key => key.StartsWith(stagePrefix, StringComparison.Ordinal)).ToList())
                SmoothEntries.Remove(stageKey);

            foreach (string stageKey in RetainingWallPlanEntries.Keys.Where(key => key.StartsWith(stagePrefix, StringComparison.Ordinal)).ToList())
                RetainingWallPlanEntries.Remove(stageKey);

            foreach (string stageKey in RemeshMemos.Keys.Where(key => key.StartsWith(stagePrefix, StringComparison.Ordinal)).ToList())
                RemeshMemos.Remove(stageKey);

            foreach (string stageKey in GradingWindowMemos.Keys.Where(key => key.StartsWith(stagePrefix, StringComparison.Ordinal)).ToList())
                GradingWindowMemos.Remove(stageKey);
            return;
        }

        foreach (string stageKey in StageEntries.Keys.Where(key => key.StartsWith(stagePrefix, StringComparison.Ordinal) && !usedStageKeys.Contains(key)).ToList())
        {
            // Do not Dispose here — PruneUnused runs on the worker cache, which holds shallow
            // mesh refs copied from the main cache. Disposing would corrupt the main cache's
            // live meshes. ReplaceBuildCachesFrom disposes main cache entries on merge.
            StageEntries.Remove(stageKey);
        }

        foreach (string stageKey in GradingTopologyEntries.Keys.Where(key => key.StartsWith(stagePrefix, StringComparison.Ordinal) && !usedStageKeys.Contains(key)).ToList())
            GradingTopologyEntries.Remove(stageKey);

        foreach (string stageKey in SmoothEntries.Keys.Where(key => key.StartsWith(stagePrefix, StringComparison.Ordinal) && !usedStageKeys.Contains(key)).ToList())
            SmoothEntries.Remove(stageKey);

        foreach (string stageKey in RetainingWallPlanEntries.Keys.Where(key => key.StartsWith(stagePrefix, StringComparison.Ordinal) && !usedStageKeys.Contains(key)).ToList())
            RetainingWallPlanEntries.Remove(stageKey);

        foreach (string stageKey in RemeshMemos.Keys.Where(key => key.StartsWith(stagePrefix, StringComparison.Ordinal) && !usedStageKeys.Contains(key)).ToList())
            RemeshMemos.Remove(stageKey);

        foreach (string stageKey in GradingWindowMemos.Keys.Where(key => key.StartsWith(stagePrefix, StringComparison.Ordinal) && !usedStageKeys.Contains(key)).ToList())
            GradingWindowMemos.Remove(stageKey);
    }

    /// <summary>
    /// Stage keys of grading stages after <paramref name="currentModifierIndex"/> in
    /// <paramref name="modifiers"/> whose patches overlap the candidates, directly or through each other.
    /// Positions come from the current stack, not from the keys, which carry only the modifier's id.
    /// </summary>
    public List<string> FindIntersectingGradingStageKeys(
        string stagePrefix,
        IReadOnlyList<ModifierDefinition> modifiers,
        int currentModifierIndex,
        IReadOnlyList<GradingPatch> candidatePatches,
        string? excludeTopologyStageKey = null)
    {
        if (candidatePatches.Count == 0)
            return new List<string>();

        var topologyEntries = new List<(string StageKey, string BaseStageKey, int ModifierIndex, IReadOnlyList<GradingPatch> Patches)>();
        foreach (var entry in GradingTopologyEntries)
        {
            if (!entry.Key.StartsWith(stagePrefix, StringComparison.Ordinal))
                continue;

            if (excludeTopologyStageKey != null && string.Equals(entry.Key, excludeTopologyStageKey, StringComparison.Ordinal))
                continue;

            if (!TerrainStageKey.TryParseModifierId(entry.Key, out Guid modifierId))
                continue;

            int modifierIndex = IndexOfModifier(modifiers, modifierId);
            if (modifierIndex <= currentModifierIndex)
                continue;

            topologyEntries.Add((entry.Key, TerrainStageKey.GetBase(entry.Key), modifierIndex, entry.Value.PatchSummaries));
        }

        if (topologyEntries.Count == 0)
            return new List<string>();

        var seeds = new Queue<int>();
        var visited = new HashSet<int>();
        for (int i = 0; i < topologyEntries.Count; i++)
        {
            if (!HasPatchOverlap(candidatePatches, topologyEntries[i].Patches))
                continue;

            visited.Add(i);
            seeds.Enqueue(i);
        }

        if (seeds.Count == 0)
            return new List<string>();

        while (seeds.Count > 0)
        {
            int current = seeds.Dequeue();
            for (int next = 0; next < topologyEntries.Count; next++)
            {
                if (visited.Contains(next))
                    continue;

                if (!HasPatchOverlap(topologyEntries[current].Patches, topologyEntries[next].Patches))
                    continue;

                visited.Add(next);
                seeds.Enqueue(next);
            }
        }

        var results = new HashSet<string>(StringComparer.Ordinal);
        foreach (int index in visited)
            results.Add(topologyEntries[index].BaseStageKey);

        return results.ToList();
    }

    private static int IndexOfModifier(IReadOnlyList<ModifierDefinition> modifiers, Guid modifierId)
    {
        for (int i = 0; i < modifiers.Count; i++)
        {
            if (modifiers[i].Id == modifierId)
                return i;
        }

        return -1;
    }

    public void InvalidateStages(IEnumerable<string> stageKeys)
    {
        foreach (string stageKey in stageKeys.Distinct(StringComparer.Ordinal))
        {
            StageEntries.Remove(stageKey);

            foreach (string topologyStageKey in GradingTopologyEntries.Keys
                         .Where(key => string.Equals(TerrainStageKey.GetBase(key), stageKey, StringComparison.Ordinal))
                         .ToList())
            {
                GradingTopologyEntries.Remove(topologyStageKey);
            }

            foreach (string smoothStageKey in SmoothEntries.Keys
                         .Where(key => string.Equals(TerrainStageKey.GetBase(key), stageKey, StringComparison.Ordinal))
                         .ToList())
            {
                SmoothEntries.Remove(smoothStageKey);
            }
        }
    }

    private static void AddMeshOutput(List<RhinoMesh> meshes, HashSet<RhinoMesh> meshSet, RhinoMesh mesh)
    {
        if (meshSet.Add(mesh))
            meshes.Add(mesh);
    }

    private static void DisposeMeshOutputs(IEnumerable<RhinoMesh> meshes)
    {
        foreach (var mesh in meshes)
            mesh.Dispose();
    }

    private static bool HasPatchOverlap(IReadOnlyList<GradingPatch> left, IReadOnlyList<GradingPatch> right)
    {
        for (int i = 0; i < left.Count; i++)
        {
            double[] leftLoop = GetComparisonLoop(left[i]);
            Bounds2D leftBounds = GradingPatch.ComputeBounds(leftLoop);
            for (int j = 0; j < right.Count; j++)
            {
                double[] rightLoop = GetComparisonLoop(right[j]);
                Bounds2D rightBounds = GradingPatch.ComputeBounds(rightLoop);
                if (!leftBounds.Intersects(rightBounds))
                    continue;

                if (LoopsOverlap(leftLoop, rightLoop))
                    return true;
            }
        }

        return false;
    }

    private static double[] GetComparisonLoop(GradingPatch patch)
    {
        if (patch.StitchLoopXy is { Length: >= 6 })
            return patch.StitchLoopXy;
        if (patch.DaylightLoopXy is { Length: >= 6 })
            return patch.DaylightLoopXy;
        return patch.OwnedRegionLoopXy;
    }

    private static bool LoopsOverlap(double[] leftLoop, double[] rightLoop)
    {
        int leftVertexCount = leftLoop.Length / 2;
        int rightVertexCount = rightLoop.Length / 2;
        if (leftVertexCount < 3 || rightVertexCount < 3)
            return false;

        for (int i = 0; i < leftVertexCount; i++)
        {
            if (GradingGeometry2D.PointInPolygon(leftLoop[i * 2], leftLoop[i * 2 + 1], rightLoop, rightVertexCount))
                return true;
        }

        for (int i = 0; i < rightVertexCount; i++)
        {
            if (GradingGeometry2D.PointInPolygon(rightLoop[i * 2], rightLoop[i * 2 + 1], leftLoop, leftVertexCount))
                return true;
        }

        for (int i = 0; i < leftVertexCount; i++)
        {
            int leftNext = (i + 1) % leftVertexCount;
            double ax = leftLoop[i * 2];
            double ay = leftLoop[i * 2 + 1];
            double bx = leftLoop[leftNext * 2];
            double by = leftLoop[leftNext * 2 + 1];

            for (int j = 0; j < rightVertexCount; j++)
            {
                int rightNext = (j + 1) % rightVertexCount;
                double cx = rightLoop[j * 2];
                double cy = rightLoop[j * 2 + 1];
                double dx = rightLoop[rightNext * 2];
                double dy = rightLoop[rightNext * 2 + 1];

                if (SegmentsIntersect(ax, ay, bx, by, cx, cy, dx, dy))
                    return true;
            }
        }

        return false;
    }

    private static bool SegmentsIntersect(double ax, double ay, double bx, double by, double cx, double cy, double dx, double dy)
    {
        double o1 = Orient(ax, ay, bx, by, cx, cy);
        double o2 = Orient(ax, ay, bx, by, dx, dy);
        double o3 = Orient(cx, cy, dx, dy, ax, ay);
        double o4 = Orient(cx, cy, dx, dy, bx, by);

        if ((o1 > 0) != (o2 > 0) && (o3 > 0) != (o4 > 0))
            return true;

        const double epsilon = 1e-12;
        return Math.Abs(o1) <= epsilon && OnSegment(ax, ay, bx, by, cx, cy) ||
               Math.Abs(o2) <= epsilon && OnSegment(ax, ay, bx, by, dx, dy) ||
               Math.Abs(o3) <= epsilon && OnSegment(cx, cy, dx, dy, ax, ay) ||
               Math.Abs(o4) <= epsilon && OnSegment(cx, cy, dx, dy, bx, by);
    }

    private static double Orient(double ax, double ay, double bx, double by, double cx, double cy)
    {
        return (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
    }

    private static bool OnSegment(double ax, double ay, double bx, double by, double px, double py)
    {
        return px >= Math.Min(ax, bx) - 1e-12 &&
               px <= Math.Max(ax, bx) + 1e-12 &&
               py >= Math.Min(ay, by) - 1e-12 &&
               py <= Math.Max(ay, by) + 1e-12;
    }

    public static string GetStagePrefix(TerrainBuildMode mode)
    {
        return mode == TerrainBuildMode.Preview ? "preview:" : "final:";
    }
}

internal sealed class StageCacheEntry
{
    public string StageName { get; init; } = string.Empty;

    public ulong PreResolutionFingerprint { get; init; }

    public ulong ResolvedInputFingerprint { get; init; }

    public ulong OutputFingerprint { get; init; }

    public RhinoMesh? MeshOutput { get; init; }

    public List<TerrainAnalysisSummary> AnalysisOutput { get; init; } = new();

    public List<GeneratedRhinoObject> ZoneObjects { get; init; } = new();

    public List<TerrainRegionState> TerrainRegions { get; init; } = new();

    public List<ZoneAnalysisSummary> ZoneAnalysisOutput { get; init; } = new();

    public List<GeneratedRhinoObject> AuxiliaryObjects { get; init; } = new();

    public List<GeneratedRhinoObject> MarkerObjects { get; init; } = new();

    public List<GeneratedRhinoObject> ScatterObjects { get; init; } = new();

    public List<TerrainObjectPlacementGroup> ObjectPlacements { get; init; } = new();

    public List<SurfaceRemesher.ConstraintPolyline> PersistentHardConstraints { get; init; } = new();

    public List<SurfaceRemesher.ConstraintPolyline> PersistentElevationConstraints { get; init; } = new();

    public List<string> Diagnostics { get; init; } = new();

    public List<GradingDiagnostic> StructuredDiagnostics { get; init; } = new();

    public List<RuntimeOverlayItem> RuntimeOverlays { get; init; } = new();

    public int? StairSurfaceCount { get; set; }

    public string? StairTreadDepthSummary { get; set; }

    public string? StairStepCountSummary { get; set; }
}

/// <summary>
/// Retained grading topology for one stage.
/// </summary>
/// <remarks>
/// <b>Immutable after construction.</b> Every member is <c>init</c>-only and no consumer writes into
/// <see cref="Vertices"/>, <see cref="Faces"/> or <see cref="PatchSummaries"/> - `PadGrader.ApplyGradingZ`
/// writes into its own fresh array, and mesh building only reads. That is what lets
/// <c>CreateWorkerCopy</c> and <c>ReplaceBuildCachesFrom</c> pass entries between the UI-owned cache and
/// worker copies by reference, and it is why storing and restoring one does not copy the whole terrain
/// topology. If a consumer ever needs to mutate an entry, build a new one - do not reintroduce a
/// defensive deep copy on the cache path.
/// </remarks>
internal sealed class GradingTopologyCacheEntry
{
    public string GraderKind { get; init; } = string.Empty;

    public ulong Fingerprint { get; init; }

    public required ulong OutputFingerprint { get; init; }

    // Grade Pad reuses retained topology geometry on cache hits. Grade Path entries are summaries:
    // their output counts/fingerprint and patch bounds are sufficient for downstream invalidation.
    public double[] Vertices { get; init; } = Array.Empty<double>();

    public int VertexCount { get; init; }

    public int[] Faces { get; init; } = Array.Empty<int>();

    public int FaceCount { get; init; }

    public List<GradingPatch> PatchSummaries { get; init; } = new();

    // Grade Pad publishes these as persistent hard constraints. A topology cache hit must publish the
    // same set as a fresh grade, or downstream stages (Grade Path's mode choice) see a different
    // terrain depending on cache state.
    public IReadOnlyList<SurfaceRemesher.ConstraintPolyline> OutputConstraints { get; init; } =
        Array.Empty<SurfaceRemesher.ConstraintPolyline>();

    public List<string> Diagnostics { get; init; } = new();

    public List<GradingDiagnostic> StructuredDiagnostics { get; init; } = new();
}

internal sealed class SmoothStageCacheEntry
{
    public ulong Fingerprint { get; init; }

    public required MeshSmoother.PreparedSmoothingData Prepared { get; init; }
}

/// <summary>
/// One cached retaining-wall rail plan.
///
/// A cached plan is shared by reference across worker copies and across builds, so anything native it
/// carries belongs to the cache and not to the build being served. A plan built with solids holds
/// <c>Brep</c>s, and the wall stage publishes a duplicate of one rather than the cached instance —
/// the same rule <c>TerrainRuntimeCacheCloner.CloneGeneratedObject</c> keeps for the stage cache.
///
/// <see cref="BuiltSolids"/> is part of the cache match, not a note: a plan built without solids
/// cannot serve a build that needs them.
/// </summary>
internal sealed class RetainingWallPlanCacheEntry
{
    public ulong Fingerprint { get; init; }

    public bool BuiltSolids { get; init; }

    public required RetainingWallPlannerCore.PlanResult Plan { get; init; }
}

internal sealed class FingerprintBuilder
{
    private readonly XxHash64Builder _hasher = new();

    public void Add(bool value) => Add(value ? 1 : 0);

    public void Add(byte value) => _hasher.Add(value);

    public void Add(int value) => _hasher.Add(value);

    public void Add(uint value) => _hasher.Add(value);

    public void Add(long value) => _hasher.Add(value);

    public void Add(ulong value) => _hasher.Add(value);

    public void Add(double value) => _hasher.Add(value);

    public void Add(Guid value) => _hasher.Add(value);

    public void Add(string? value)
    {
        if (value == null)
        {
            Add(0);
            return;
        }

        AddBytes(Encoding.UTF8.GetBytes(value));
        Add(0xFF);
    }

    public void AddBytes(ReadOnlySpan<byte> bytes)
    {
        _hasher.AddBytes(bytes);
    }

    public ulong ToUInt64() => _hasher.ToUInt64();
}

internal static class TerrainRuntimeCacheCloner
{
    /// <summary>Carries the extracted arrays across, so a restored stage mesh is not re-normalized downstream.</summary>
    public static RhinoMesh? CloneMesh(RhinoMesh? mesh) =>
        mesh == null ? null : RhinoGeometryConversions.DuplicateWithCachedData(mesh);

    /// <summary>
    /// Deep-copies one summary, member by member.
    ///
    /// Every field has to be listed. A field left out of this list is not a cosmetic omission: it reads
    /// back as zero on any build the stage cache serves, which looks exactly like an analysis that measured
    /// nothing. <c>TerrainRuntimeCacheClonerTests.CloneAnalysis_CopiesEveryProperty</c> fails when a
    /// property is added to <see cref="TerrainAnalysisSummary"/> and not to this method, because that is
    /// otherwise only visible on a cache hit in a live session.
    /// </summary>
    public static TerrainAnalysisSummary? CloneAnalysis(TerrainAnalysisSummary? analysis)
    {
        if (analysis == null)
            return null;

        return new TerrainAnalysisSummary
        {
            AnalysisId = analysis.AnalysisId,
            SurfaceArea = analysis.SurfaceArea,
            SlopeMinPercent = analysis.SlopeMinPercent,
            SlopeMaxPercent = analysis.SlopeMaxPercent,
            SlopeAveragePercent = analysis.SlopeAveragePercent,
            SlopeDisplayLowPercent = analysis.SlopeDisplayLowPercent,
            SlopeDisplayHighPercent = analysis.SlopeDisplayHighPercent,
            AspectFaceCount = analysis.AspectFaceCount,
            AspectFlatFaceCount = analysis.AspectFlatFaceCount,
            AspectDominantBearing = analysis.AspectDominantBearing,
            DisplayRangeLow = analysis.DisplayRangeLow,
            DisplayRangeHigh = analysis.DisplayRangeHigh,
            DistributionBins = analysis.DistributionBins == null
                ? null
                : (double[])analysis.DistributionBins.Clone(),
            ElevationMinZ = analysis.ElevationMinZ,
            ElevationMaxZ = analysis.ElevationMaxZ,
            CutFillDisplayAbsMax = analysis.CutFillDisplayAbsMax,
            CutVolume = analysis.CutVolume,
            FillVolume = analysis.FillVolume,
            NetVolume = analysis.NetVolume,
            EarthworkIsEstimated = analysis.EarthworkIsEstimated,
            CatchmentBasinCount = analysis.CatchmentBasinCount,
            CatchmentSinkCount = analysis.CatchmentSinkCount,
            CatchmentLargestArea = analysis.CatchmentLargestArea,
            CatchmentFlatFaceCount = analysis.CatchmentFlatFaceCount,
            PondCount = analysis.PondCount,
            PondTotalVolume = analysis.PondTotalVolume,
            PondMaxDepth = analysis.PondMaxDepth,
            PondTotalArea = analysis.PondTotalArea,
            LevelAreaCheckedArea = analysis.LevelAreaCheckedArea,
            LevelAreaExceedingArea = analysis.LevelAreaExceedingArea,
            LevelAreaSteepestSlopeDegrees = analysis.LevelAreaSteepestSlopeDegrees,
            RouteCheckedArea = analysis.RouteCheckedArea,
            RouteRampArea = analysis.RouteRampArea,
            RouteRunningExceedingArea = analysis.RouteRunningExceedingArea,
            RouteCrossExceedingArea = analysis.RouteCrossExceedingArea,
            RouteSteepestRunningDegrees = analysis.RouteSteepestRunningDegrees,
            RouteSteepestCrossDegrees = analysis.RouteSteepestCrossDegrees,
            RouteRunCount = analysis.RouteRunCount,
            RouteFailedRunCount = analysis.RouteFailedRunCount,
            RouteLandingCount = analysis.RouteLandingCount,
            RouteLargestRunRise = analysis.RouteLargestRunRise,
            ContourCurveCount = analysis.ContourCurveCount,
            ContourLevelCount = analysis.ContourLevelCount,
            ContourFirstLevel = analysis.ContourFirstLevel,
            ContourLastLevel = analysis.ContourLastLevel,
            GeneratedOutputCount = analysis.GeneratedOutputCount,
            SampleSourceCount = analysis.SampleSourceCount,
            SectionTerrainCount = analysis.SectionTerrainCount,
            SectionCutRegionCount = analysis.SectionCutRegionCount,
            SectionFillRegionCount = analysis.SectionFillRegionCount,
            WaterflowBoundaryCount = analysis.WaterflowBoundaryCount,
            WaterflowSinkCount = analysis.WaterflowSinkCount,
            WaterflowRejectedCount = analysis.WaterflowRejectedCount,
            CutFillDeltaContourCount = analysis.CutFillDeltaContourCount,
            CutFillBalanceCurveCount = analysis.CutFillBalanceCurveCount,
            ReportRowCount = analysis.ReportRowCount,
            ReportTableCount = analysis.ReportTableCount,
            SampleMinValue = analysis.SampleMinValue,
            SampleMaxValue = analysis.SampleMaxValue,
            SampleAverageValue = analysis.SampleAverageValue
        };
    }

    public static List<TerrainAnalysisSummary> CloneAnalyses(IEnumerable<TerrainAnalysisSummary> analyses)
    {
        return analyses
            .Select(CloneAnalysis)
            .Where(analysis => analysis != null)
            .Cast<TerrainAnalysisSummary>()
            .ToList();
    }

    public static List<GeneratedRhinoObject> CloneGeneratedObjects(IEnumerable<GeneratedRhinoObject> objects)
    {
        return objects.Select(CloneGeneratedObject).ToList();
    }

    public static List<TerrainObjectPlacementGroup> CloneObjectPlacementGroups(IEnumerable<TerrainObjectPlacementGroup> groups)
    {
        return groups.Select(CloneObjectPlacementGroup).ToList();
    }

    private static TerrainObjectPlacementGroup CloneObjectPlacementGroup(TerrainObjectPlacementGroup group)
    {
        var clone = new TerrainObjectPlacementGroup
        {
            DefinitionId = group.DefinitionId
        };

        foreach (var placement in group.Placements)
        {
            clone.Placements.Add(new TerrainObjectPlacement
            {
                ObjectId = placement.ObjectId,
                AppliedTransform = placement.AppliedTransform
            });
        }

        return clone;
    }

    public static GeneratedRhinoObject CloneGeneratedObject(GeneratedRhinoObject generated)
    {
        return new GeneratedRhinoObject
        {
            Role = generated.Role,
            Geometry = generated.Geometry?.Duplicate(),
            Name = generated.Name,
            Kind = generated.Kind,
            AnalysisId = generated.AnalysisId,
            ColorArgb = generated.ColorArgb,
            LayerPath = generated.LayerPath,
            SourceLayerPath = generated.SourceLayerPath,
            MaterialName = generated.MaterialName,
            InstanceDefinitionName = generated.InstanceDefinitionName,
            MarkerBlockTemplate = generated.MarkerBlockTemplate,
            InstanceUserStrings = generated.InstanceUserStrings == null
                ? null
                : new Dictionary<string, string>(generated.InstanceUserStrings, StringComparer.Ordinal),
            InstanceTransform = generated.InstanceTransform,
            PlotWeight = generated.PlotWeight,
            // Every property the bake reads has to survive the clone, or the cached copy bakes differently
            // from the one the build produced. AppearanceSource and DisplayOrder were both being dropped
            // here, so layer-driven output silently reverted to object appearance and section stacking
            // order was lost between build and bake.
            AppearanceSource = generated.AppearanceSource,
            DisplayOrder = generated.DisplayOrder,
            ScatterDefinitionId = generated.ScatterDefinitionId
        };
    }

    public static List<SurfaceRemesher.ConstraintPolyline> CloneConstraints(IEnumerable<SurfaceRemesher.ConstraintPolyline> constraints)
    {
        return constraints
            .Select(constraint => new SurfaceRemesher.ConstraintPolyline(
                (double[])constraint.Points.Clone(),
                constraint.PointCount,
                constraint.IsClosed,
                constraint.PreserveInputElevation))
            .ToList();
    }

    /// <summary>
    /// Deep copy of a grading topology entry. <b>Not used on the build path</b> — entries are immutable
    /// and are shared by reference there (see <see cref="GradingTopologyCacheEntry"/>). This exists for
    /// callers that genuinely need an independent copy.
    /// </summary>
    public static GradingTopologyCacheEntry CloneGradingTopologyEntry(GradingTopologyCacheEntry entry)
    {
        return new GradingTopologyCacheEntry
        {
            GraderKind = entry.GraderKind,
            Fingerprint = entry.Fingerprint,
            OutputFingerprint = entry.OutputFingerprint,
            Vertices = (double[])entry.Vertices.Clone(),
            VertexCount = entry.VertexCount,
            Faces = (int[])entry.Faces.Clone(),
            FaceCount = entry.FaceCount,
            PatchSummaries = entry.PatchSummaries
                .Select(static patch => new GradingPatch
                {
                    OwnerKey = patch.OwnerKey,
                    Kind = patch.Kind,
                    Priority = patch.Priority,
                    OwnedRegionLoopXy = (double[])patch.OwnedRegionLoopXy.Clone(),
                    DaylightLoopXy = patch.DaylightLoopXy != null ? (double[])patch.DaylightLoopXy.Clone() : Array.Empty<double>(),
                    StitchLoopXy = patch.StitchLoopXy != null ? (double[])patch.StitchLoopXy.Clone() : Array.Empty<double>(),
                    DirtyBounds = patch.DirtyBounds,
                    UsesFallbackBand = patch.UsesFallbackBand
                })
                .ToList(),
            OutputConstraints = CloneConstraints(entry.OutputConstraints),
            Diagnostics = entry.Diagnostics.ToList(),
            StructuredDiagnostics = entry.StructuredDiagnostics.ToList()
        };
    }

    public static StageCacheEntry CloneStageCacheEntry(StageCacheEntry entry)
    {
        return new StageCacheEntry
        {
            StageName = entry.StageName,
            PreResolutionFingerprint = entry.PreResolutionFingerprint,
            ResolvedInputFingerprint = entry.ResolvedInputFingerprint,
            OutputFingerprint = entry.OutputFingerprint,
            MeshOutput = entry.MeshOutput,
            AnalysisOutput = entry.AnalysisOutput,
            ZoneObjects = entry.ZoneObjects,
            TerrainRegions = entry.TerrainRegions.Select(region => region.Duplicate()).ToList(),
            ZoneAnalysisOutput = CloneZoneAnalyses(entry.ZoneAnalysisOutput),
            AuxiliaryObjects = entry.AuxiliaryObjects,
            MarkerObjects = entry.MarkerObjects,
            ScatterObjects = entry.ScatterObjects,
            ObjectPlacements = entry.ObjectPlacements,
            PersistentHardConstraints = entry.PersistentHardConstraints,
            PersistentElevationConstraints = entry.PersistentElevationConstraints,
            Diagnostics = entry.Diagnostics,
            StructuredDiagnostics = entry.StructuredDiagnostics,
            RuntimeOverlays = CloneRuntimeOverlays(entry.RuntimeOverlays),
            StairSurfaceCount = entry.StairSurfaceCount,
            StairTreadDepthSummary = entry.StairTreadDepthSummary,
            StairStepCountSummary = entry.StairStepCountSummary
        };
    }

    public static ZoneAnalysisSummary CloneZoneAnalysis(ZoneAnalysisSummary source)
    {
        return new ZoneAnalysisSummary
        {
            ZoneId = source.ZoneId,
            PlanArea = source.PlanArea,
            SurfaceArea = source.SurfaceArea,
            ElevationMinZ = source.ElevationMinZ,
            ElevationAverageZ = source.ElevationAverageZ,
            ElevationMaxZ = source.ElevationMaxZ,
            SlopeMinPercent = source.SlopeMinPercent,
            SlopeAveragePercent = source.SlopeAveragePercent,
            SlopeMaxPercent = source.SlopeMaxPercent,
            TriangleCount = source.TriangleCount,
            OutputCount = source.OutputCount,
            HasEarthwork = source.HasEarthwork,
            EarthworkIsEstimated = source.EarthworkIsEstimated,
            CutVolume = source.CutVolume,
            FillVolume = source.FillVolume
        };
    }

    public static List<ZoneAnalysisSummary> CloneZoneAnalyses(IEnumerable<ZoneAnalysisSummary> analyses)
    {
        return analyses.Select(CloneZoneAnalysis).ToList();
    }

    public static List<RuntimeOverlayItem> CloneRuntimeOverlays(IEnumerable<RuntimeOverlayItem> items)
    {
        return items.Select(item => item.Clone()).ToList();
    }

}
