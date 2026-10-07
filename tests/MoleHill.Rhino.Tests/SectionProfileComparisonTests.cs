using MoleHill.Core.Analysis;
using MoleHill.Rhino.Services;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class SectionProfileComparisonTests
{
    [Fact]
    public void Compare_RhinoSection_UsesWorldElevationAndSplitsAtTheCrossing()
    {
        TerrainSectionResult proposed = CreateResult((0, 0), (10, 10));
        TerrainSectionResult reference = CreateResult((0, 8), (10, 2));

        IReadOnlyList<SectionComparisonRegion> regions = SectionProfileComparison.Compare(
            proposed, reference, tolerance: 1e-6);

        Assert.Equal(2, regions.Count);
        Assert.True(regions[0].IsCut);
        Assert.False(regions[1].IsCut);
        Assert.Equal(5.0, regions[0].Vertices[^1].Station, 6);
    }

    private static TerrainSectionResult CreateResult(params (double Station, double Elevation)[] vertices) =>
        CreateResult(vertices.AsEnumerable());

    private static TerrainSectionResult CreateResult(params IEnumerable<(double Station, double Elevation)>[] segments)
    {
        TerrainSectionSegment[] resultSegments = segments
            .Select(segment => new TerrainSectionSegment(segment
                .Select(vertex => new TerrainSectionVertex(
                    vertex.Station,
                    new Point3d(vertex.Station, 0, vertex.Elevation)))
                .ToArray()))
            .ToArray();
        double[] elevations = resultSegments.SelectMany(segment => segment.Vertices)
            .Select(vertex => vertex.World.Z)
            .ToArray();
        double totalStation = resultSegments.SelectMany(segment => segment.Vertices)
            .Max(vertex => vertex.Station);
        return new TerrainSectionResult(resultSegments, totalStation, elevations.Min(), elevations.Max());
    }
}
