namespace MoleHill.Core.Analysis;

/// <summary>
/// The landform a face belongs to, read from the surface's mean curvature at the analysis's landform size.
/// In legend order: convex, planar, concave.
/// </summary>
/// <remarks>
/// Three classes, deliberately. A finer set (ridge, shoulder, saddle, toe, valley and the rest) was tried
/// and read as too many to scan on a plan; the direction of the bend stays available as the profile and
/// plan measures in Curvature value mode.
/// </remarks>
public enum LandformClass
{
    /// <summary>Bulges up more than the flatness tolerance: a crest, mound, ridge or rounded edge.</summary>
    Convex = 0,

    /// <summary>Within the flatness tolerance of a plane, level or sloping.</summary>
    Planar = 1,

    /// <summary>Dips down more than the flatness tolerance: a hollow, swale, valley or the foot of a slope.</summary>
    Concave = 2
}
