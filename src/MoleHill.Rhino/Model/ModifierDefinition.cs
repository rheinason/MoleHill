using System.Text.Json.Serialization;

namespace MoleHill.Rhino.Model;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(TriangulateModifierDefinition), "triangulate")]
[JsonDerivedType(typeof(RemeshModifierDefinition), "remesh")]
[JsonDerivedType(typeof(SmoothModifierDefinition), "smooth")]
[JsonDerivedType(typeof(MeshAreasModifierDefinition), "mesh-areas")]
[JsonDerivedType(typeof(MeshCollageModifierDefinition), "mesh-collage")]
[JsonDerivedType(typeof(RetainingWallModifierDefinition), "retaining-wall")]
[JsonDerivedType(typeof(GradePadModifierDefinition), "grade-pad")]
[JsonDerivedType(typeof(GradePathModifierDefinition), "grade-path")]
public abstract class ModifierDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Label { get; set; } = "Modifier";

    public bool IsEnabled { get; set; } = true;

    public int SchemaVersion { get; set; } = 1;

    public abstract IEnumerable<SourceReferenceSet> EnumerateSourceSets();
}
