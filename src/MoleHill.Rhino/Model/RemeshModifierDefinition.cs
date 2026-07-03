namespace MoleHill.Rhino.Model;

public sealed class RemeshModifierDefinition : ModifierDefinition
{
    public SourceReferenceSet Constraints { get; set; } = new();

    /// <summary>
    /// When true, use the connectivity-preserving local-refinement remesh (keep input topology / flow
    /// lines, split only coarse triangles in place on the surface, flip toward regularity, respect creases)
    /// instead of the global constrained-Delaunay rebuild. False keeps the classic Delaunay remesh.
    /// </summary>
    public bool LocalRefine { get; set; }

    public double EdgeLength { get; set; }

    public double MaxArea { get; set; }

    public double MinAngle { get; set; } = 20.0;

    /// <summary>
    /// Merge-by-distance threshold. Near-duplicate input/constraint vertices (e.g. batter-toe pinches)
    /// closer than this are collapsed instead of protected. 0 disables it.
    /// </summary>
    public double MergeDistance { get; set; }

    /// <summary>
    /// Crease-preservation dihedral angle (degrees). Feature edges (batter toes, slope breaks) at or above
    /// this fold angle are pinned for this remesh so they stay smooth — detected from geometry, never
    /// persisted as breaklines. 0 disables it.
    /// </summary>
    public double CreaseAngle { get; set; }

    public RemeshModifierDefinition()
    {
        Label = "Remesh";
    }

    public override IEnumerable<SourceReferenceSet> EnumerateSourceSets()
    {
        yield return Constraints;
    }
}
