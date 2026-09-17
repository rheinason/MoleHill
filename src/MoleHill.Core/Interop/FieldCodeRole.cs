namespace MoleHill.Core.Interop;

/// <summary>
/// What a coded point becomes once it reaches the document.
///
/// <see cref="Ignore"/> is a real decision, not an absence. "We code trees and I do not want them in the
/// terrain" must be expressible as a rule, because otherwise it is indistinguishable from a code nobody
/// has written a rule for yet — and those two cases get opposite treatment: one is silence, the other is
/// a prompt to add the rule.
/// </summary>
public enum FieldCodeRole
{
    /// <summary>Points in a coded run become a breakline: a hard constraint on the triangulation.</summary>
    Breakline,

    /// <summary>Points in a coded run become a contour line — drawing, not a surface constraint.</summary>
    Contour,

    /// <summary>A closed run defining an extent. Feeds a Triangulate boundary role.</summary>
    Boundary,

    /// <summary>A standalone levelled point. Never joined into a run.</summary>
    Spot,

    /// <summary>Recognised and deliberately not drawn.</summary>
    Ignore
}
