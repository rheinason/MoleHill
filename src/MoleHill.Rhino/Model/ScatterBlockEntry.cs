namespace MoleHill.Rhino.Model;

/// <summary>
/// One entry in a scatter card's weighted block mix: a source (block instances and/or layers) plus a
/// relative weight. Per instance the build picks an entry with probability proportional to its weight,
/// so a single scatter can mix several blocks (e.g. 70% pine / 30% shrub).
/// </summary>
public sealed class ScatterBlockEntry
{
    public SourceReferenceSet Source { get; set; } = new();

    public double Weight { get; set; } = 1.0;
}
