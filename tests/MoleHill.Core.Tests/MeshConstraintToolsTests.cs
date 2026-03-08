using MoleHill.Core.Engine;
using Xunit;

namespace MoleHill.Core.Tests;

public class MeshConstraintToolsTests
{
    [Fact]
    public void AddBoundarySegments_TwoTriangles_AddsOnlyOuterEdges()
    {
        var faces = new[]
        {
            0, 1, 2,
            0, 2, 3
        };

        var segments = new List<(int a, int b)>();
        var segmentKeys = new HashSet<long>();

        MeshConstraintTools.AddBoundarySegments(segments, segmentKeys, faces, 2);

        Assert.Equal(4, segments.Count);
        Assert.Contains(segments, segment => MatchesEdge(segment, 0, 1));
        Assert.Contains(segments, segment => MatchesEdge(segment, 1, 2));
        Assert.Contains(segments, segment => MatchesEdge(segment, 2, 3));
        Assert.Contains(segments, segment => MatchesEdge(segment, 0, 3));
        Assert.DoesNotContain(segments, segment => MatchesEdge(segment, 0, 2));
    }

    [Theory]
    [InlineData("Constraints could not be enforced. Using plain Delaunay.", true)]
    [InlineData("Using non-conforming CDT for tightly spaced constraints.", false)]
    [InlineData("Quality constraints could not be applied.", false)]
    [InlineData(null, false)]
    public void ConstraintsWereDropped_MatchesOnlyConstraintLossWarnings(string? warning, bool expected)
    {
        Assert.Equal(expected, MeshConstraintTools.ConstraintsWereDropped(warning));
    }

    private static bool MatchesEdge((int a, int b) segment, int a, int b)
    {
        return (segment.a == a && segment.b == b) || (segment.a == b && segment.b == a);
    }
}
