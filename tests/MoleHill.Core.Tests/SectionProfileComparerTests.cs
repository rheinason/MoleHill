using MoleHill.Core.Analysis;
using Xunit;

namespace MoleHill.Core.Tests;

public class SectionProfileComparerTests
{
    [Fact]
    public void Compare_ProfilesCross_SplitsCutAndFillAtIntersection()
    {
        IReadOnlyList<IReadOnlyList<SectionProfilePoint>> proposed = CreateResult((0, 0), (10, 10));
        IReadOnlyList<IReadOnlyList<SectionProfilePoint>> reference = CreateResult((0, 8), (10, 2));

        IReadOnlyList<SectionComparisonRegion> regions = SectionProfileComparer.Compare(
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
        IReadOnlyList<IReadOnlyList<SectionProfilePoint>> proposed = CreateResult(
            new[] { (0.0, 2.0), (4.0, 2.0) },
            new[] { (6.0, 2.0), (10.0, 2.0) });
        IReadOnlyList<IReadOnlyList<SectionProfilePoint>> reference = CreateResult((0, 0), (10, 0));

        IReadOnlyList<SectionComparisonRegion> regions = SectionProfileComparer.Compare(
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
        IReadOnlyList<IReadOnlyList<SectionProfilePoint>> ascending = CreateResult((0, 5), (10, 5));
        IReadOnlyList<IReadOnlyList<SectionProfilePoint>> descending = CreateResult((10, 0), (0, 0));

        IReadOnlyList<SectionComparisonRegion> regions = SectionProfileComparer.Compare(
            ascending, descending, tolerance: 1e-6);

        Assert.Single(regions);
        Assert.False(regions[0].IsCut);
    }

    [Fact]
    public void Compare_BothProfilesDescending_MatchesTheAscendingResult()
    {
        IReadOnlyList<SectionComparisonRegion> ascending = SectionProfileComparer.Compare(
            CreateResult((0, 0), (10, 10)), CreateResult((0, 8), (10, 2)), tolerance: 1e-6);
        IReadOnlyList<SectionComparisonRegion> descending = SectionProfileComparer.Compare(
            CreateResult((10, 10), (0, 0)), CreateResult((10, 2), (0, 8)), tolerance: 1e-6);

        Assert.Equal(ascending.Count, descending.Count);
        Assert.Equal(2, descending.Count);
        Assert.True(descending[0].IsCut);
        Assert.False(descending[1].IsCut);
    }

    [Fact]
    public void Compare_EqualProfiles_ProducesNoRegions()
    {
        IReadOnlyList<IReadOnlyList<SectionProfilePoint>> proposed = CreateResult((0, 3), (10, 3));
        IReadOnlyList<IReadOnlyList<SectionProfilePoint>> reference = CreateResult((0, 3), (10, 3));

        IReadOnlyList<SectionComparisonRegion> regions = SectionProfileComparer.Compare(
            proposed, reference, tolerance: 1e-6);

        Assert.Empty(regions);
    }

    private static IReadOnlyList<IReadOnlyList<SectionProfilePoint>> CreateResult(params (double Station, double Elevation)[] vertices) =>
        CreateResult(vertices.AsEnumerable());

    private static IReadOnlyList<IReadOnlyList<SectionProfilePoint>> CreateResult(params IEnumerable<(double Station, double Elevation)>[] segments) =>
        segments
            .Select(segment => (IReadOnlyList<SectionProfilePoint>)segment
                .Select(vertex => new SectionProfilePoint(vertex.Station, vertex.Elevation))
                .ToArray())
            .ToArray();
}
