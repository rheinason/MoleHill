using System.Text;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using MoleHill.Rhino.Model;
using Rhino.Geometry;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

internal sealed class TerrainRuntimeCache
{
    public TinEngine TinEngine { get; init; } = new();

    public Dictionary<string, StageCacheEntry> StageEntries { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, GradingTopologyCacheEntry> GradingTopologyEntries { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, SmoothStageCacheEntry> SmoothEntries { get; } = new(StringComparer.Ordinal);

    public TerrainDisplayState? DisplayState { get; set; }

    /// <summary>
    /// Session-only UI preference. Worker copies and cache merges intentionally leave this set on
    /// the UI-owned cache so a background build cannot reset what the user chose to inspect.
    /// </summary>
    public HashSet<RuntimeOverlayOwner> VisibleDiagnosticOwners { get; } = new();

    public TerrainCoreCaseRecorder? CoreCaseRecorder { get; init; }

    public TimeSpan? LastPreviewDuration { get; set; }

    public TimeSpan? LastFinalDuration { get; set; }

    public TerrainRuntimeCache CreateWorkerCopy()
    {
        var copy = new TerrainRuntimeCache
        {
            // Share the persistent TinEngine so incremental edits (single spot-point add/remove)
            // stay reachable from Rhino background builds instead of forcing a full CDT every time
            // inputs change. TinEngine.Build is internally serialized by its own gate, so concurrent
            // old/new workers queue rather than race. Safe now that H1 (Vertex.ID re-keying after
            // incremental edits) is fixed — see docs/release-review-2026-07-04.md P1.
            TinEngine = TinEngine,
            LastPreviewDuration = LastPreviewDuration,
            LastFinalDuration = LastFinalDuration
        };

        foreach (var entry in StageEntries)
            copy.StageEntries[entry.Key] = TerrainRuntimeCacheCloner.CloneStageCacheEntry(entry.Value);

        foreach (var entry in GradingTopologyEntries)
            copy.GradingTopologyEntries[entry.Key] = entry.Value;

        foreach (var entry in SmoothEntries)
            copy.SmoothEntries[entry.Key] = entry.Value;

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

        return displacedMeshes;
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
        DisplayState = null;
        LastPreviewDuration = null;
        LastFinalDuration = null;
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
    }

    public List<string> FindIntersectingGradingStageKeys(
        string stagePrefix,
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

            if (!TerrainStageKey.TryParseModifierIndex(entry.Key, out int modifierIndex) || modifierIndex <= currentModifierIndex)
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

    public List<string> Diagnostics { get; init; } = new();

    public List<GradingDiagnostic> StructuredDiagnostics { get; init; } = new();
}

internal sealed class SmoothStageCacheEntry
{
    public ulong Fingerprint { get; init; }

    public required MeshSmoother.PreparedSmoothingData Prepared { get; init; }
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
    public static RhinoMesh? CloneMesh(RhinoMesh? mesh) => mesh?.DuplicateMesh();

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
            ElevationMinZ = analysis.ElevationMinZ,
            ElevationMaxZ = analysis.ElevationMaxZ,
            CutFillDisplayAbsMax = analysis.CutFillDisplayAbsMax,
            CutVolume = analysis.CutVolume,
            FillVolume = analysis.FillVolume,
            NetVolume = analysis.NetVolume,
            EarthworkIsEstimated = analysis.EarthworkIsEstimated,
            ContourCurveCount = analysis.ContourCurveCount,
            ContourLevelCount = analysis.ContourLevelCount,
            ContourFirstLevel = analysis.ContourFirstLevel,
            ContourLastLevel = analysis.ContourLastLevel,
            GeneratedOutputCount = analysis.GeneratedOutputCount,
            SampleSourceCount = analysis.SampleSourceCount,
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

    public static SmoothStageCacheEntry CloneSmoothStageCacheEntry(SmoothStageCacheEntry entry)
    {
        return new SmoothStageCacheEntry
        {
            Fingerprint = entry.Fingerprint,
            Prepared = ClonePreparedSmoothingData(entry.Prepared)
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

    private static MeshSmoother.PreparedSmoothingData ClonePreparedSmoothingData(MeshSmoother.PreparedSmoothingData prepared)
    {
        return new MeshSmoother.PreparedSmoothingData
        {
            VertexCount = prepared.VertexCount,
            NeighborOffsets = (int[])prepared.NeighborOffsets.Clone(),
            NeighborIndices = (int[])prepared.NeighborIndices.Clone(),
            IsMeshBoundary = (bool[])prepared.IsMeshBoundary.Clone(),
            InsideBoundaries = (bool[])prepared.InsideBoundaries.Clone(),
            IsOnBreakline = (bool[])prepared.IsOnBreakline.Clone()
        };
    }
}
