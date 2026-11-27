// What a dragged profile bend snapped to.
namespace MoleHill.Core.Analysis;

public enum ProfileSnapKind
{
    None,

    /// <summary>The ground line at the bend's station.</summary>
    Ground,

    /// <summary>Level with the bend before or after it.</summary>
    Level,

    /// <summary>A round grade (in the user's slope unit) from the bend before or after it.</summary>
    Grade,

    /// <summary>A round elevation.</summary>
    Elevation,
}
