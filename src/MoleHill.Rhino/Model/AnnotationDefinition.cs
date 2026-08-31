namespace MoleHill.Rhino.Model;

// JSON polymorphism is registry-driven (Services/TerrainJsonTypeResolver reads AnnotationTypeRegistry),
// not [JsonDerivedType] — registering an AnnotationTypeDescriptor is enough. Discriminators are unchanged
// from when these types lived in the analysis family, so saved .3dm terrains still load.

/// <summary>
/// Root of the annotation family: content that <em>describes</em> the terrain — contours, spot labels,
/// callouts, sections. Its output is drawing, and it says what is already there.
///
/// This is deliberately a peer of <see cref="AnalysisDefinition"/> rather than a subclass of it, matching
/// the other content families (<see cref="ModifierDefinition"/>, <see cref="MarkerDefinition"/>,
/// <see cref="TerrainObjectDefinition"/>). An analysis <em>evaluates</em> the terrain and its result is a
/// measurement — a number, or a colour mapped onto the mesh. The two answer different questions, and
/// while they shared a base every annotation inherited a colour-ramp apparatus it never used and
/// persisted into the document, and a single visibility flag gated both — so hiding slope colours also
/// silently hid every label and section.
/// </summary>
public abstract class AnnotationDefinition : ITerrainContentItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Label { get; set; } = "Annotation";

    public bool IsEnabled { get; set; } = true;

    public int SchemaVersion { get; set; } = 2;

    /// <summary>When true (the default) annotation size comes from the terrain's Rhino dimension style
    /// rather than this definition's stored absolute height, so drawing standards live in Rhino's
    /// Annotation Styles editor. Documents saved before schema 27 are migrated to false so their existing
    /// explicit heights are preserved exactly.</summary>
    public bool FollowsAnnotationStyle { get; set; } = true;

    public virtual IEnumerable<SourceReferenceSet> EnumerateSourceSets()
    {
        yield break;
    }
}
