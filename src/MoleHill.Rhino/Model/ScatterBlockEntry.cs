namespace MoleHill.Rhino.Model;

/// <summary>
/// One entry in a scatter card's weighted block mix: a source (block instances and/or layers) plus a
/// relative weight. Per instance the build picks an entry with probability proportional to its weight,
/// so a single scatter can mix several blocks (e.g. 70% pine / 30% shrub).
/// </summary>
public sealed class ScatterBlockEntry
{
    /// <summary>Block definition selected directly by name (via the block selector). Preferred over
    /// <see cref="Source"/> when set.</summary>
    public string? BlockDefinitionName { get; set; }

    /// <summary>Alternative source: block instance(s)/layers picked in the document, resolved to their
    /// block definition name(s) at build time. Used when <see cref="BlockDefinitionName"/> is empty.</summary>
    public SourceReferenceSet Source { get; set; } = new();

    public double Weight { get; set; } = 1.0;
}
