namespace MoleHill.Rhino.Model;

public sealed class RetainingWallModifierDefinition : ModifierDefinition
{
    public SourceReferenceSet WallCurves { get; set; } = new();

    public double Tolerance { get; set; } = 1.0;

    public double Sharpness { get; set; } = 0.5;

    public double ShoulderWidth { get; set; }

    public string? OutputLayerPath { get; set; }

    public RetainingWallModifierDefinition()
    {
        Label = "Retaining Wall";
    }

    public override IEnumerable<SourceReferenceSet> EnumerateSourceSets()
    {
        yield return WallCurves;
    }
}
