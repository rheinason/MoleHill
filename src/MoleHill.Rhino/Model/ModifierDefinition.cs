using System.Text.Json.Serialization;

namespace MoleHill.Rhino.Model;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(TriangulateModifierDefinition), "triangulate")]
[JsonDerivedType(typeof(AddGeometryModifierDefinition), "add-geometry")]
[JsonDerivedType(typeof(RemeshModifierDefinition), "remesh")]
[JsonDerivedType(typeof(SmoothModifierDefinition), "smooth")]
[JsonDerivedType(typeof(MeshAreasModifierDefinition), "mesh-areas")]
[JsonDerivedType(typeof(MeshCollageModifierDefinition), "mesh-collage")]
[JsonDerivedType(typeof(RetainingWallModifierDefinition), "retaining-wall")]
[JsonDerivedType(typeof(GradePadModifierDefinition), "grade-pad")]
[JsonDerivedType(typeof(GradePathModifierDefinition), "grade-path")]
[JsonDerivedType(typeof(InSituStairModifierDefinition), "in-situ-stair")]
public abstract class ModifierDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Label { get; set; } = "Modifier";

    public bool IsEnabled { get; set; } = true;

    public int SchemaVersion { get; set; } = 1;

    public abstract IEnumerable<SourceReferenceSet> EnumerateSourceSets();
}
