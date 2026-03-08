namespace MoleHill.Rhino.Model;

public sealed class ElevationMarkerDefinition : MarkerDefinition
{
    public string Format { get; set; } = "F2";

    public ElevationMarkerDefinition()
    {
        Name = "Elevation Markers";
    }

    public override IEnumerable<SourceReferenceSet> EnumerateSourceSets()
    {
        yield return Sources;
    }
}
