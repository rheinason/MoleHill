using System.Runtime.InteropServices;
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

    public Dictionary<string, PadTopologyCacheEntry> PadTopologyEntries { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, SmoothStageCacheEntry> SmoothEntries { get; } = new(StringComparer.Ordinal);

    public TerrainDisplayState? DisplayState { get; set; }

    public TimeSpan? LastPreviewDuration { get; set; }

    public TimeSpan? LastFinalDuration { get; set; }

    public TerrainRuntimeCache CreateWorkerCopy()
    {
        var copy = new TerrainRuntimeCache
        {
            TinEngine = TinEngine,
            LastPreviewDuration = LastPreviewDuration,
            LastFinalDuration = LastFinalDuration
        };

        foreach (var entry in StageEntries)
            copy.StageEntries[entry.Key] = TerrainRuntimeCacheCloner.CloneStageCacheEntry(entry.Value);

        foreach (var entry in PadTopologyEntries)
            copy.PadTopologyEntries[entry.Key] = entry.Value;

        foreach (var entry in SmoothEntries)
            copy.SmoothEntries[entry.Key] = entry.Value;

        return copy;
    }

    public void ReplaceBuildCachesFrom(TerrainRuntimeCache source)
    {
        StageEntries.Clear();
        foreach (var entry in source.StageEntries)
            StageEntries[entry.Key] = entry.Value;
        source.StageEntries.Clear();

        PadTopologyEntries.Clear();
        foreach (var entry in source.PadTopologyEntries)
            PadTopologyEntries[entry.Key] = entry.Value;
        source.PadTopologyEntries.Clear();

        SmoothEntries.Clear();
        foreach (var entry in source.SmoothEntries)
            SmoothEntries[entry.Key] = entry.Value;
        source.SmoothEntries.Clear();
    }

    public void Clear()
    {
        StageEntries.Clear();
        PadTopologyEntries.Clear();
        SmoothEntries.Clear();
        DisplayState = null;
        LastPreviewDuration = null;
        LastFinalDuration = null;
        TinEngine.InvalidateCache();
    }

    public void PruneUnused(IReadOnlySet<string> usedStageKeys, TerrainBuildMode mode)
    {
        string stagePrefix = GetStagePrefix(mode);
        if (usedStageKeys.Count == 0)
        {
            foreach (string stageKey in StageEntries.Keys.Where(key => key.StartsWith(stagePrefix, StringComparison.Ordinal)).ToList())
                StageEntries.Remove(stageKey);

            foreach (string stageKey in PadTopologyEntries.Keys.Where(key => key.StartsWith(stagePrefix, StringComparison.Ordinal)).ToList())
                PadTopologyEntries.Remove(stageKey);

            foreach (string stageKey in SmoothEntries.Keys.Where(key => key.StartsWith(stagePrefix, StringComparison.Ordinal)).ToList())
                SmoothEntries.Remove(stageKey);
            return;
        }

        foreach (string stageKey in StageEntries.Keys.Where(key => key.StartsWith(stagePrefix, StringComparison.Ordinal) && !usedStageKeys.Contains(key)).ToList())
            StageEntries.Remove(stageKey);

        foreach (string stageKey in PadTopologyEntries.Keys.Where(key => key.StartsWith(stagePrefix, StringComparison.Ordinal) && !usedStageKeys.Contains(key)).ToList())
            PadTopologyEntries.Remove(stageKey);

        foreach (string stageKey in SmoothEntries.Keys.Where(key => key.StartsWith(stagePrefix, StringComparison.Ordinal) && !usedStageKeys.Contains(key)).ToList())
            SmoothEntries.Remove(stageKey);
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

    public List<GeneratedRhinoObject> AuxiliaryObjects { get; init; } = new();

    public List<GeneratedRhinoObject> MarkerObjects { get; init; } = new();

    public List<SurfaceRemesher.ConstraintPolyline> PersistentHardConstraints { get; init; } = new();

    public List<string> Diagnostics { get; init; } = new();

    public int? StairSurfaceCount { get; set; }

    public string? StairTreadDepthSummary { get; set; }

    public string? StairStepCountSummary { get; set; }
}

internal sealed class PadTopologyCacheEntry
{
    public ulong Fingerprint { get; init; }

    public required ulong OutputFingerprint { get; init; }

    public double[] Vertices { get; init; } = Array.Empty<double>();

    public int VertexCount { get; init; }

    public int[] Faces { get; init; } = Array.Empty<int>();

    public int FaceCount { get; init; }

    public List<string> Diagnostics { get; init; } = new();
}

internal sealed class SmoothStageCacheEntry
{
    public ulong Fingerprint { get; init; }

    public required MeshSmoother.PreparedSmoothingData Prepared { get; init; }
}

internal struct FingerprintBuilder
{
    private const ulong OffsetBasis = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    private ulong _value = OffsetBasis;

    public FingerprintBuilder()
    {
        _value = OffsetBasis;
    }

    public void Add(bool value) => Add(value ? 1 : 0);

    public void Add(byte value) => _value = (_value ^ value) * Prime;

    public void Add(int value)
    {
        Span<int> buffer = stackalloc int[1];
        buffer[0] = value;
        AddBytes(MemoryMarshal.AsBytes(buffer));
    }

    public void Add(uint value)
    {
        Span<uint> buffer = stackalloc uint[1];
        buffer[0] = value;
        AddBytes(MemoryMarshal.AsBytes(buffer));
    }

    public void Add(long value)
    {
        Span<long> buffer = stackalloc long[1];
        buffer[0] = value;
        AddBytes(MemoryMarshal.AsBytes(buffer));
    }

    public void Add(ulong value)
    {
        Span<ulong> buffer = stackalloc ulong[1];
        buffer[0] = value;
        AddBytes(MemoryMarshal.AsBytes(buffer));
    }

    public void Add(double value) => Add(BitConverter.DoubleToInt64Bits(value));

    public void Add(Guid value) => AddBytes(value.ToByteArray());

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
        foreach (byte b in bytes)
            Add(b);
    }

    public ulong ToUInt64() => _value;
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
            InstanceTransform = generated.InstanceTransform
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

    public static PadTopologyCacheEntry ClonePadTopologyEntry(PadTopologyCacheEntry entry)
    {
        return new PadTopologyCacheEntry
        {
            Fingerprint = entry.Fingerprint,
            OutputFingerprint = entry.OutputFingerprint,
            Vertices = (double[])entry.Vertices.Clone(),
            VertexCount = entry.VertexCount,
            Faces = (int[])entry.Faces.Clone(),
            FaceCount = entry.FaceCount,
            Diagnostics = entry.Diagnostics.ToList()
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
            AuxiliaryObjects = entry.AuxiliaryObjects,
            MarkerObjects = entry.MarkerObjects,
            PersistentHardConstraints = entry.PersistentHardConstraints,
            Diagnostics = entry.Diagnostics,
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
