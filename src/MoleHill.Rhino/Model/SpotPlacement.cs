namespace MoleHill.Rhino.Model;

/// <summary>
/// Where a Spot Heights or Spot Slope card puts its labels. Stored as its number, so the values are fixed:
/// never renumber them.
/// </summary>
public enum SpotPlacement
{
    /// <summary>At picked points, and at the edit points of picked curves.</summary>
    Points = 0,

    /// <summary>At regular stations along picked curves.</summary>
    Curves = 1,

    /// <summary>On a regular plan grid, inside optional closed boundaries (the whole terrain without them).</summary>
    Grid = 2,
}
