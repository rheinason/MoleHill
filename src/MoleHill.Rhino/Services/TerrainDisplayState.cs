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
        return clone;
    }
}
