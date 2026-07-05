namespace MoleHill.Rhino.Model;

public sealed class RemeshModifierDefinition : ModifierDefinition
{
    public SourceReferenceSet Constraints { get; set; } = new();

    /// <summary>
    /// Remesh algorithm: "isotropic" (default, best quality), "rebuild" (classic SurfaceRemesher full
    /// re-triangulation, wall-safe), or "local" (LocalMeshRefiner, topology-preserving). Schema 23; old
    /// documents without this field deserialize to "isotropic" via this property's initializer.
    /// </summary>
    public string Mode { get; set; } = "isotropic";

    /// <summary>
    /// Target edge length for the remesh. 0 = auto-derive from the input mesh's median edge
    /// length (the remesh then regularizes at the mesh's own scale instead of changing density).
    /// </summary>
    public double EdgeLength { get; set; }

    /// <summary>
    /// Legacy (pre-schema-22) quality target, kept only so old documents deserialize; the serializer
    /// migrates it into <see cref="EdgeLength"/> (equilateral-triangle mapping) and zeroes it. Not
    /// exposed in the UI and ignored by the build.
    /// </summary>
    public double MaxArea { get; set; }

    /// <summary>
    /// Crease-preservation dihedral angle (degrees). Feature edges (batter toes, slope breaks) at or above
    /// this fold angle are pinned for this remesh so they stay crisp — detected from geometry, never
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
