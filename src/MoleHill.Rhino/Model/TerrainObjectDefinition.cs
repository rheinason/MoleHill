namespace MoleHill.Rhino.Model;

// JSON polymorphism is registry-driven (Services/TerrainJsonTypeResolver reads ObjectTypeRegistry),
// not [JsonDerivedType] — registering an ObjectTypeDescriptor is enough. Discriminators are unchanged.
public abstract class TerrainObjectDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "Objects";

    public bool IsEnabled { get; set; } = true;

    public int SchemaVersion { get; set; } = 1;

    public SourceReferenceSet Sources { get; set; } = new();

    public int RandomSeed { get; set; }

    public double RandomRotationMinDegrees { get; set; }

    public double RandomRotationMaxDegrees { get; set; }

    public double RandomScaleMin { get; set; } = 1.0;

    public double RandomScaleMax { get; set; } = 1.0;

    public double ZOffset { get; set; }

    public List<TerrainObjectPlacementState> PlacementStates { get; set; } = new();

    public virtual IEnumerable<SourceReferenceSet> EnumerateSourceSets()
    {
        yield return Sources;
    }
}
