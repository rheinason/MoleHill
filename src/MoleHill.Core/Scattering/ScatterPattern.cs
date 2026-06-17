namespace MoleHill.Core.Scattering;

/// <summary>Spatial arrangement used to lay scatter sample points inside a boundary region.</summary>
public enum ScatterPattern
{
    /// <summary>Uniform random placement (rejection-sampled inside the boundary).</summary>
    Random = 0,

    /// <summary>Regular axis-aligned grid of cell centres.</summary>
    Grid = 1,

    /// <summary>Regular grid with each point randomly offset within its cell.</summary>
    JitteredGrid = 2,

    /// <summary>Bridson blue-noise: evenly spaced points no closer than the spacing radius.</summary>
    PoissonDisk = 3
}
