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

    [UnitFree("An angle in degrees; angles do not change with model units.")]
    public double RandomRotationMinDegrees { get; set; }

    [UnitFree("An angle in degrees; angles do not change with model units.")]
    public double RandomRotationMaxDegrees { get; set; }

    [UnitFree("A multiple or ratio; unitless.")]
    public double RandomScaleMin { get; set; } = 1.0;

    [UnitFree("A multiple or ratio; unitless.")]
    public double RandomScaleMax { get; set; } = 1.0;

    [ModelLength]
    public double ZOffset { get; set; }

    public List<TerrainObjectPlacementState> PlacementStates { get; set; } = new();

    public virtual IEnumerable<SourceReferenceSet> EnumerateSourceSets()
    {
        yield return Sources;
    }
}
