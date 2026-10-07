using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Registry;

/// <summary>
/// State handed to <see cref="AnalysisTypeDescriptor.Build"/> and <see cref="AnnotationTypeDescriptor.Build"/>:
/// the analysis pass's locals, so a type's build lives in its descriptor instead of in a central switch.
/// The mesh arrays and measurements are filled once, lazily, before the first stage that needs them runs
/// (<c>TerrainBuildService.BuildAnalyses</c>), and are shared by every analysis and annotation in the pass.
/// </summary>
internal sealed class AnalysisBuildContext
{
    public required TerrainBuildSnapshot Snapshot { get; init; }
    public required TerrainDefinition Terrain { get; init; }
    public required RhinoMesh FallbackBaseMesh { get; init; }
    public required RhinoMesh CurrentMesh { get; init; }
    public required TerrainBuildResult Build { get; init; }
    public required Dictionary<TerrainBuildService.ReferenceComparisonCacheKey, TerrainBuildService.ReferenceComparisonStats> ReferenceComparisonCache { get; init; }
    public required Dictionary<TerrainBuildService.ReferenceProjectionCacheKey, TerrainBuildService.ReferenceProjectionContext> ReferenceProjectionCache { get; init; }
    public required Dictionary<BasinGraphCacheKey, BasinGraph> BasinGraphCache { get; init; }
    public Func<bool>? ShouldCancel { get; init; }

    // Extracted from CurrentMesh by one TryExtractMeshData call, so the counts always describe the arrays.
    public double[] Vertices { get; set; } = Array.Empty<double>();
    public int VertexCount { get; set; }
    public int[] Faces { get; set; } = Array.Empty<int>();
    public int FaceCount { get; set; }
    public double ElevationMinZ { get; set; }
    public double ElevationMaxZ { get; set; }
    public double SurfaceArea { get; set; }
}
