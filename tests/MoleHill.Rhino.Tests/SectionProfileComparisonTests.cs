using MoleHill.Rhino.Services;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class SectionProfileComparisonTests
{
    [Fact]
    public void Compare_ProfilesCross_SplitsCutAndFillAtIntersection()
    {
        TerrainSectionResult proposed = CreateResult((0, 0), (10, 10));
        TerrainSectionResult reference = CreateResult((0, 8), (10, 2));

        IReadOnlyList<SectionComparisonRegion> regions = SectionProfileComparison.Compare(
            proposed, reference, tolerance: 1e-6);

        Assert.Equal(2, regions.Count);
        Assert.True(regions[0].IsCut);
        Assert.False(regions[1].IsCut);
        Assert.Equal(5.0, regions[0].Vertices[^1].Station, 6);
        Assert.Equal(5.0, regions[1].Vertices[0].Station, 6);
    }

    [Fact]
    public void Compare_DisjointSegmentGap_DoesNotBridgeFillRegion()
    {
        TerrainSectionResult proposed = CreateResult(
            new[] { (0.0, 2.0), (4.0, 2.0) },
            new[] { (6.0, 2.0), (10.0, 2.0) });
        TerrainSectionResult reference = CreateResult((0, 0), (10, 0));

        IReadOnlyList<SectionComparisonRegion> regions = SectionProfileComparison.Compare(
            proposed, reference, tolerance: 1e-6);

        Assert.Equal(2, regions.Count);
        Assert.All(regions, region => Assert.False(region.IsCut));
        Assert.Equal(4.0, regions[0].Vertices[^1].Station, 6);
        Assert.Equal(6.0, regions[1].Vertices[0].Station, 6);
    }

    /// <summary>
    /// The slicer walks mesh adjacency, so a profile's vertices can come back in descending station order.
    /// That used to drop every edge of the run, and a fully descending profile produced no edges at all —
    /// so cut/fill shading came back silently empty on section lines that were perfectly valid.
    /// </summary>
    [Fact]
    public void Compare_DescendingStationOrder_StillFindsRegions()
    {
        TerrainSectionResult ascending = CreateResult((0, 5), (10, 5));
        TerrainSectionResult descending = CreateResult((10, 0), (0, 0));

        IReadOnlyList<SectionComparisonRegion> regions = SectionProfileComparison.Compare(
            ascending, descending, tolerance: 1e-6);

        Assert.Single(regions);
        Assert.False(regions[0].IsCut);
    }

    [Fact]
    public void Compare_BothProfilesDescending_MatchesTheAscendingResult()
    {
        IReadOnlyList<SectionComparisonRegion> ascending = SectionProfileComparison.Compare(
            CreateResult((0, 0), (10, 10)), CreateResult((0, 8), (10, 2)), tolerance: 1e-6);
        IReadOnlyList<SectionComparisonRegion> descending = SectionProfileComparison.Compare(
            CreateResult((10, 10), (0, 0)), CreateResult((10, 2), (0, 8)), tolerance: 1e-6);

        Assert.Equal(ascending.Count, descending.Count);
        Assert.Equal(2, descending.Count);
        Assert.True(descending[0].IsCut);
        Assert.False(descending[1].IsCut);
    }

    [Fact]
    public void Compare_EqualProfiles_ProducesNoRegions()
    {
        TerrainSectionResult proposed = CreateResult((0, 3), (10, 3));
        TerrainSectionResult reference = CreateResult((0, 3), (10, 3));

        IReadOnlyList<SectionComparisonRegion> regions = SectionProfileComparison.Compare(
            proposed, reference, tolerance: 1e-6);

        Assert.Empty(regions);
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
