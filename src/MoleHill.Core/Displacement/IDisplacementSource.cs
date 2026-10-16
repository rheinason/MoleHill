namespace MoleHill.Core.Displacement;

/// <summary>A vertical offset as a function of world plan position.</summary>
public interface IDisplacementSource
{
    /// <summary>The height offset at world (x, y), before any region or hold weighting.</summary>
    double Offset(double x, double y);

    /// <summary>
    /// The finest plan distance the source means to show — a noise bump, an image pixel. A terrain whose
    /// edges are much longer than half of it cannot carry the pattern.
    /// </summary>
    double DetailSize { get; }
}
