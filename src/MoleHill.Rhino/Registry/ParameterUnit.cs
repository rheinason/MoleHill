namespace MoleHill.Rhino.Registry;

/// <summary>
/// What a numeric parameter's number actually means. Every numeric row declares one, because a bare
/// number in a card is unreadable — "Fill Slope 3" is degrees, percent or 1:3 depending on nothing the
/// user can see. The schema builder turns this into the trailing unit label on the row, and for
/// <see cref="Slope"/> into the display conversion and unit-aware parsing as well.
/// </summary>
internal enum ParameterUnit
{
    /// <summary>A pure count, factor or fraction — nothing to label.</summary>
    None,

    /// <summary>A distance in model units; the row labels it with the document's unit abbreviation.</summary>
    ModelLength,

    /// <summary>A true angle (dihedral, rotation), always degrees. Never converted — an angle between two
    /// faces is not a slope, and offering it as 1:3 would be nonsense.</summary>
    Degrees,

    /// <summary>A slope, stored as an angle in degrees but shown and typed in the user's chosen slope
    /// unit. See <c>SlopeUnitPreference</c> and <c>MoleHill.Core.Analysis.SlopeInput</c>.</summary>
    Slope,

    /// <summary>A percentage that is not a slope (opacity, a share, a probability).</summary>
    Percent,
}
