using System.Text.Json.Serialization;

namespace MoleHill.Rhino.Model;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(ElevationMarkerDefinition), "elevation")]
[JsonDerivedType(typeof(SlopeMarkerDefinition), "slope")]
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

    public bool ShowValueLabel { get; set; } = true;

    public abstract IEnumerable<SourceReferenceSet> EnumerateSourceSets();
}
