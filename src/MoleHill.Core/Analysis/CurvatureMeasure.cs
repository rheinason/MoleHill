namespace MoleHill.Core.Analysis;

/// <summary>
/// Which curvature a <see cref="CurvatureField"/> reports as a value. Every measure is a normal curvature
/// of the fitted surface in 1/model-unit, and every one is <b>convex positive</b>: a crest, a knoll or a
/// rounded edge reads above zero, a hollow, a channel or the foot of a slope below it.
/// </summary>
public enum CurvatureMeasure
{
    /// <summary>The mean of the two principal curvatures: how the ground bends overall, in no particular
    /// direction. Equal to half the sum of <see cref="Profile"/> and <see cref="Plan"/>.</summary>
    Mean = 0,

    /// <summary>Curvature along the fall line, the direction water runs. Convex is a slope that steepens
    /// downhill (a shoulder, where flow speeds up); concave is one that eases out (a toe, where it slows).</summary>
    Profile = 1,

    /// <summary>
    /// Curvature across the fall line, along the contour: the tangential normal curvature. Convex is ground
    /// that sheds water sideways (a ridge or spur, where flow spreads); concave is ground that gathers it
    /// (a valley or swale). Measured as a normal curvature rather than as the curvature of the contour line
    /// itself, because contour curvature divides by the slope and blows up on near-level ground.
    /// </summary>
    Plan = 2
}
