using MoleHill.Rhino.Model;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

internal sealed class TerrainDisplayState
{
    public bool IsPreview { get; set; }

    public bool HasDeferredOutputs { get; set; }

    public Mesh? TerrainMesh { get; set; }

    public Mesh? BaseTerrainMesh { get; set; }

    public Mesh? PreviewTerrainMesh { get; set; }

    public Guid? ActiveAnalysisId { get; set; }

    public string? ActiveAnalysisLabel { get; set; }

    public List<TerrainAnalysisSummary> AnalysisResults { get; } = new();

    public List<GeneratedRhinoObject> ZoneObjects { get; } = new();

    public List<GeneratedRhinoObject> AuxiliaryObjects { get; } = new();

    public List<GeneratedRhinoObject> MarkerObjects { get; } = new();

    public List<GeneratedRhinoObject> ScatterObjects { get; } = new();

    public Dictionary<Guid, ScatterObjectRange> ScatterObjectRanges { get; } = new();

    public void RebuildScatterObjectRanges()
    {
        ScatterObjectRanges.Clear();
        for (int i = 0; i < ScatterObjects.Count; i++)
        {
            Guid key = ScatterObjects[i].ScatterDefinitionId ?? Guid.Empty;
            if (ScatterObjectRanges.TryGetValue(key, out var range))
            {
                ScatterObjectRanges[key] = range.ExtendToInclude(i);
                continue;
            }

            ScatterObjectRanges[key] = new ScatterObjectRange(i, 1);
        }
    }

    public TerrainDisplayState Clone()
    {
        var clone = new TerrainDisplayState
        {
            IsPreview = IsPreview,
            HasDeferredOutputs = HasDeferredOutputs,
            TerrainMesh = TerrainRuntimeCacheCloner.CloneMesh(TerrainMesh),
            BaseTerrainMesh = TerrainRuntimeCacheCloner.CloneMesh(BaseTerrainMesh),
            PreviewTerrainMesh = TerrainRuntimeCacheCloner.CloneMesh(PreviewTerrainMesh),
            ActiveAnalysisId = ActiveAnalysisId,
            ActiveAnalysisLabel = ActiveAnalysisLabel
        };
        clone.AnalysisResults.AddRange(TerrainRuntimeCacheCloner.CloneAnalyses(AnalysisResults));
        clone.ZoneObjects.AddRange(TerrainRuntimeCacheCloner.CloneGeneratedObjects(ZoneObjects));
        clone.AuxiliaryObjects.AddRange(TerrainRuntimeCacheCloner.CloneGeneratedObjects(AuxiliaryObjects));
        clone.MarkerObjects.AddRange(TerrainRuntimeCacheCloner.CloneGeneratedObjects(MarkerObjects));
        clone.ScatterObjects.AddRange(TerrainRuntimeCacheCloner.CloneGeneratedObjects(ScatterObjects));
        clone.RebuildScatterObjectRanges();
        return clone;
    }
}

internal readonly record struct ScatterObjectRange(int StartIndex, int Count)
{
    public int EndExclusive => StartIndex + Count;

    public ScatterObjectRange ExtendToInclude(int index)
    {
        int start = Math.Min(StartIndex, index);
        int end = Math.Max(EndExclusive, index + 1);
        return new ScatterObjectRange(start, end - start);
    }
}
