namespace MoleHill.Rhino.Model;

/// <summary>
/// Field-guided quad retopology (finishing modifier — meant to run last). Stage 1 computes a cross-field
/// aligned to features and, when <see cref="ShowField"/> is on, colors the terrain by it so the flow can be
/// validated before the quad extraction stages are built. Later stages will replace the mesh with a
/// quad-dominant one.
/// </summary>
public sealed class RetopoModifierDefinition : ModifierDefinition
{
    /// <summary>Extra feature curves (road edges, ridges) the quad flow should follow.</summary>
    public SourceReferenceSet Constraints { get; set; } = new();

    /// <summary>Align the field to interior creases folding at least this many degrees. 0 = off.</summary>
    public double CreaseAngle { get; set; }

    /// <summary>Target quad edge length; 0 auto-derives from the terrain extent.</summary>
    public double TargetEdgeLength { get; set; }

    /// <summary>Draw the cross-field as a flow-cross overlay to preview the flow. Off = no overlay.</summary>
    public bool ShowField { get; set; } = true;

    /// <summary>
    /// Replace the terrain with the extracted quad-dominant mesh (Stage 2). Off keeps the input mesh and
    /// only previews the field. Quad output is terminal — put Retopo last in the stack.
    /// </summary>
    public bool Quads { get; set; }

    public RetopoModifierDefinition()
    {
        Label = "Retopo";
    }

    public override IEnumerable<SourceReferenceSet> EnumerateSourceSets()
    {
        yield return Constraints;
    }
}
