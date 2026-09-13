namespace MoleHill.Rhino.Model;

/// <summary>
/// What the drainage analyses share: the one setting that decides how the terrain is routed.
/// </summary>
/// <remarks>
/// Catchments and ponding are separate cards because they answer different questions — "where does water
/// go" draws forty polygons on a real survey, "did I build a bathtub" usually draws nothing and is worth
/// leaving on permanently. They rest on one computation, though, and this base is what lets the build
/// stage recognise that: two cards agreeing on <see cref="FlatSlopeThresholdDegrees"/> share a basin
/// graph rather than routing the terrain twice. That is the same arrangement Earthworks and Cut / Fill
/// have through <see cref="ReferenceComparisonAnalysisDefinition"/>.
/// </remarks>
public abstract class DrainageAnalysisDefinition : AnalysisDefinition
{
    /// <summary>
    /// Ground falling less steeply than this is treated as level, and routed as a region that drains
    /// where it actually spills rather than face by face.
    ///
    /// Stored in degrees like every other slope in the model, and shown and typed in the user's own slope
    /// unit. Never zero: a survey-derived surface is never exactly level, and routing its noise face by
    /// face turns a graded pad's catchment into confetti — which is the failure this whole setting exists
    /// to prevent.
    /// </summary>
    public double FlatSlopeThresholdDegrees { get; set; } = 0.3;
}
