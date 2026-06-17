using System.Text.Json.Serialization;

namespace MoleHill.Rhino.Model;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(LowestPointObjectDefinition), "lowest-point")]
[JsonDerivedType(typeof(SurfaceOrientedObjectDefinition), "surface-oriented")]
[JsonDerivedType(typeof(ScatterObjectDefinition), "scatter")]
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
