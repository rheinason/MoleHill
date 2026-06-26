namespace MoleHill.Core.Scattering;

/// <summary>How the number/spacing of scattered instances is specified.</summary>
public enum ScatterDensityMode
{
    /// <summary>A total instance count distributed across the whole boundary.</summary>
    Count = 0,

    /// <summary>Instances per unit area; the total scales with boundary area.</summary>
    PerArea = 1,

    /// <summary>A minimum centre-to-centre spacing; the count follows from the area.</summary>
    Spacing = 2,

    /// <summary>Curve mode only: space items edge-to-edge by their footprint plus a fixed gap.</summary>
    EdgeToEdge = 3
}
