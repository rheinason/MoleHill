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
    /// Target edge length for the remesh. 0 = preserve the input mesh's approximate plan face
    /// density (the remesh then regularizes at the mesh's own scale instead of changing density).
    /// </summary>
    public double EdgeLength { get; set; }

    /// <summary>
    /// Maximum triangle area for the <c>"rebuild"</c> (classic constrained-Delaunay) mode — a quality
    /// refinement target. 0 = no area constraint. Ignored by the "isotropic" and "local" modes, which
    /// regularize by <see cref="EdgeLength"/> instead. Pre-schema-22 documents drove refinement through
    /// this field globally; the serializer migrates those into <see cref="EdgeLength"/> and zeroes it,
    /// so a nonzero value here only ever comes from a schema-24+ rebuild-mode edit.
    /// </summary>
    public double MaxArea { get; set; }

    /// <summary>
    /// Minimum triangle angle in degrees for the <c>"rebuild"</c> mode — Triangle.NET's quality knob that
    /// forces skinny triangles to be refined. 0 = no angle constraint. Ignored by the "isotropic" and
    /// "local" modes. Schema 24.
    /// </summary>
    public double MinAngle { get; set; }

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
