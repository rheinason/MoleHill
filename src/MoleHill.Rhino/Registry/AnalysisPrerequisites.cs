using System.Linq;
using MoleHill.Rhino.Model;

namespace MoleHill.Rhino.Registry;

/// <summary>
/// Shared readiness checks for the comparative analyses (cut/fill, earthworks, section cut/fill shading).
///
/// These types answer "what did my grading move, and by how much". They do that by comparing the terrain's
/// own <em>initial triangulation</em> — the ground as first triangulated, before any modifier ran — against
/// the finished modifier stack. No second terrain, and no reference geometry, is needed for the ordinary
/// case; an explicit reference is for comparing against surveyed ground that is not this terrain's own
/// starting point.
///
/// So a missing reference is never the problem. The only real gap is a terrain nothing has graded yet:
/// then the two surfaces are the same and the analysis correctly reports zero everywhere, which reads as a
/// broken map unless the card says why.
/// </summary>
internal static class AnalysisPrerequisites
{
    /// <summary>What the comparison is currently measuring against, stated plainly on the card.</summary>
    public const string BaseTriangulationBasis =
        "Comparing this terrain's initial triangulation against the finished modifier stack.";

    public const string ReferenceBasis =
        "Comparing against the reference surface set below.";

    public const string NothingToCompareMessage =
        "Nothing has moved the ground yet, so cut and fill are zero everywhere. Add a grading modifier, " +
        "or set an explicit reference to compare against a different surface.";

    /// <summary>
    /// True when some modifier in the stack can change elevations, and there is therefore a difference to
    /// measure against the initial triangulation.
    ///
    /// Judged by modifier type rather than by inspecting results: this runs while building a card, long
    /// before any comparison has been made, and a card must not have to trigger a build to describe itself.
    /// Disabled modifiers do not count — the terrain really is unchanged while they are off.
    /// </summary>
    public static bool HasElevationChangingModifier(TerrainDefinition terrain) =>
        terrain.Modifiers.Any(modifier => modifier.IsEnabled && ChangesElevations(modifier));

    /// <summary>The basis line for a comparative analysis: which two surfaces are being differenced.</summary>
    public static string DescribeBasis(bool hasExplicitReference) =>
        hasExplicitReference ? ReferenceBasis : BaseTriangulationBasis;

    /// <summary>
    /// Whether this modifier type moves the surface vertically.
    ///
    /// Triangulate and the geometry inputs establish the ground rather than alter it — they are what the
    /// initial triangulation *is* — so they are deliberately excluded. Remesh only redistributes vertices
    /// across the same surface.
    /// </summary>
    private static bool ChangesElevations(ModifierDefinition modifier) => modifier switch
    {
        GradePadModifierDefinition => true,
        GradePathModifierDefinition => true,
        SculptModifierDefinition => true,
        SmoothModifierDefinition => true,
        _ => false
    };
}
