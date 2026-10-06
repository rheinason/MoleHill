using MoleHill.Rhino.Model;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Whether output owned by an analysis or an annotation is shown. Preview
/// (<see cref="TerrainAnalysisPreviewBuilder"/>) and baked objects (<c>TerrainController</c>) both ask here,
/// so the two cannot disagree about what is visible.
/// </summary>
internal static class TerrainContentVisibility
{
    /// <summary>
    /// An analysis answers to the terrain's <see cref="TerrainDefinition.ShowAnalysisOutputs"/> gate and
    /// its own card; an annotation is the drawing, has no terrain-level gate, and answers only to its card.
    /// Before schema 31 both went through <c>ShowAnalysisOutputs</c>, so hiding slope colours also hid every
    /// label and section. An owner that no longer exists shows nothing.
    /// </summary>
    public static bool IsOwnerVisible(TerrainDefinition terrain, Guid ownerId)
    {
        AnalysisDefinition? analysis = terrain.Analyses.FirstOrDefault(item => item.Id == ownerId);
        if (analysis != null)
            return terrain.ShowAnalysisOutputs && analysis.IsEnabled;

        AnnotationDefinition? annotation = terrain.Annotations.FirstOrDefault(item => item.Id == ownerId);
        if (annotation != null)
            return annotation.IsEnabled;

        return false;
    }
}
