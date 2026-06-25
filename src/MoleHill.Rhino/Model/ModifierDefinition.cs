namespace MoleHill.Rhino.Model;

// JSON polymorphism for ModifierDefinition is supplied by the registry-driven TerrainJsonTypeResolver
// (Services), not a hand-maintained [JsonDerivedType] list — registering a modifier descriptor is enough.
// Discriminator strings are unchanged so saved .3dm terrains still load.
public abstract class ModifierDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Label { get; set; } = "Modifier";

    public bool IsEnabled { get; set; } = true;

    public int SchemaVersion { get; set; } = 1;

    public abstract IEnumerable<SourceReferenceSet> EnumerateSourceSets();
}
