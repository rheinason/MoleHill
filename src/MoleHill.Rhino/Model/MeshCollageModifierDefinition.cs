namespace MoleHill.Rhino.Model;

public sealed class MeshCollageModifierDefinition : ModifierDefinition
{
    public List<CollageZoneDefinition> Zones { get; set; } = new();

    public int BaseColorArgb { get; set; } = unchecked((int)0xFFC8C8C8);

    public MeshCollageModifierDefinition()
    {
        Label = "Mesh Collage";
    }

    public override IEnumerable<SourceReferenceSet> EnumerateSourceSets()
    {
        foreach (var zone in Zones)
            yield return zone.Boundaries;
    }
}
