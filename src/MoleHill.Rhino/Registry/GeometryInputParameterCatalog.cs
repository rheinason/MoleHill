using MoleHill.Rhino.Model;
using ModifierParam = MoleHill.Rhino.Registry.ParameterDescriptor<MoleHill.Rhino.Model.ModifierDefinition>;

namespace MoleHill.Rhino.Registry;

/// <summary>
/// The boundary-peel rows shared by every <see cref="GeometryInputModifierDefinition"/> — Triangulate
/// and Add Geometry — declared once, the way <see cref="ObjectParameterCatalog"/> declares the rows
/// every terrain-object type shares.
///
/// <para>These four were hand-written card rows until the slope-unit work: being outside the schema,
/// they were invisible to <c>ParameterSchemaGuardTests</c>, and "Slope Limit" duly shipped as an
/// unlabelled degrees field — the very defect the unit vocabulary exists to prevent. Declaring them
/// here puts them back under the guard.</para>
///
/// <para>They open their own closed "Peel Border" section, so a card that concatenates the catalog gets
/// the group with no panel code.</para>
/// </summary>
internal static class GeometryInputParameterCatalog
{
    /// <summary>Keys of the "Peel Border" rows.</summary>
    public static IReadOnlyList<string> BoundaryPeelKeys { get; } = new[]
    {
        "PeelBoundaryTriangles",
        "MaxBoundaryEdgeLength",
        "MaxBoundaryAngleDegrees",
        "MaxBoundarySlopeDegrees",
    };

    /// <summary>The closed "Peel Border" section and its rows; the summary says whether peeling is on.</summary>
    public static IReadOnlyList<ModifierParam> BoundaryPeel { get; } = new[]
    {
        ModifierParam.Section(
            "peelBorder", "Peel Border",
            summary: m => ((GeometryInputModifierDefinition)m).PeelBoundaryTriangles ? "On" : "Off"),
        ModifierParam.Bool(
            "PeelBoundaryTriangles", "Enabled",
            m => ((GeometryInputModifierDefinition)m).PeelBoundaryTriangles,
            (m, v) => ((GeometryInputModifierDefinition)m).PeelBoundaryTriangles = v,
            "Remove unwanted triangles from the mesh border only. Interior faces are never candidates."),
        ModifierParam.Number(
            "MaxBoundaryEdgeLength", "Max Edge",
            m => ((GeometryInputModifierDefinition)m).MaxBoundaryEdgeLength,
            (m, v) => ((GeometryInputModifierDefinition)m).MaxBoundaryEdgeLength = v,
            "Edge length above which a border triangle may be peeled. 0 chooses an automatic threshold from the mesh edge lengths.",
            unit: ParameterUnit.ModelLength),
        ModifierParam.Number(
            "MaxBoundaryAngleDegrees", "Max Angle",
            m => ((GeometryInputModifierDefinition)m).MaxBoundaryAngleDegrees,
            (m, v) => ((GeometryInputModifierDefinition)m).MaxBoundaryAngleDegrees = v,
            // A true interior angle of a triangle, not a slope: it runs to 180 and has no rise over run.
            "Border triangles with an edge longer than Max Edge and an interior angle at or above this value are peeled.",
            max: 180.0,
            unit: ParameterUnit.Degrees),
        ModifierParam.Slope(
            "MaxBoundarySlopeDegrees", "Slope Limit",
            m => ((GeometryInputModifierDefinition)m).MaxBoundarySlopeDegrees,
            (m, v) => ((GeometryInputModifierDefinition)m).MaxBoundarySlopeDegrees = v,
            "Border triangles at or above this slope are peeled. 0 disables slope-based peeling."),
    };
}
