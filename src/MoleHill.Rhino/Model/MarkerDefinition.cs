namespace MoleHill.Rhino.Model;

// JSON polymorphism is registry-driven (Services/TerrainJsonTypeResolver reads MarkerTypeRegistry),
// not [JsonDerivedType] — registering a MarkerTypeDescriptor is enough. Discriminators are unchanged.
public abstract class MarkerDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "Marker";

    public bool IsEnabled { get; set; } = true;

    public int SchemaVersion { get; set; } = 1;

    public SourceReferenceSet Sources { get; set; } = new();

    public int ColorArgb { get; set; } = unchecked((int)0xFF1E1E1E);

    public bool UseBlockInstance { get; set; } = true;

    public string? BlockDefinitionName { get; set; }

    public double BlockScale { get; set; } = 1.0;

    /// <summary>When true (the default) <see cref="BlockScale"/> is a multiplier on the size derived from
    /// the terrain's annotation style, so symbols track label text. False keeps it as an absolute scale.
    /// Documents saved before schema 27 are migrated to false.</summary>
    public bool FollowsAnnotationStyle { get; set; } = true;

    public bool ShowValueLabel { get; set; } = true;

    public abstract IEnumerable<SourceReferenceSet> EnumerateSourceSets();
}
