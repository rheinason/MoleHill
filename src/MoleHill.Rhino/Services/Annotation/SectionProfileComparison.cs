// Adapts a sliced Rhino section to the pure profile comparison in MoleHill.Core.
using MoleHill.Core.Analysis;

namespace MoleHill.Rhino.Services;

internal static class SectionProfileComparison
{
    public static IReadOnlyList<SectionComparisonRegion> Compare(
        TerrainSectionResult proposed,
        TerrainSectionResult reference,
        double tolerance) =>
        SectionProfileComparer.Compare(ToProfile(proposed), ToProfile(reference), tolerance);

    private static IReadOnlyList<SectionProfilePoint>[] ToProfile(TerrainSectionResult result) =>
        result.Segments
            .Select(segment => (IReadOnlyList<SectionProfilePoint>)segment.Vertices
                .Select(vertex => new SectionProfilePoint(vertex.Station, vertex.World.Z))
                .ToArray())
            .ToArray();
}
