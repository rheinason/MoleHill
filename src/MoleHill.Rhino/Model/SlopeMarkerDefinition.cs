namespace MoleHill.Rhino.Model;

public sealed class SlopeMarkerDefinition : MarkerDefinition
{
    public string Format { get; set; } = "F1";

    public bool AsPercent { get; set; } = true;

    public SlopeMarkerDefinition()
    {
        Name = "Slope Markers";
    }

    public override IEnumerable<SourceReferenceSet> EnumerateSourceSets()
    {
        yield return Sources;
    }
}
