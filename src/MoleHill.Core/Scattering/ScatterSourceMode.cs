namespace MoleHill.Core.Scattering;

/// <summary>Whether scatter fills a 2D region or distributes along 1D curves.</summary>
public enum ScatterSourceMode
{
    /// <summary>Fill the interior of one or more closed boundary loops.</summary>
    Region = 0,

    /// <summary>Distribute points along one or more open curves (by count or spacing over arc length).</summary>
    Curve = 1
}
