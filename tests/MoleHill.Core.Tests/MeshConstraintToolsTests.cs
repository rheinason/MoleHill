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
    [InlineData(TriangulationWarningFlags.DroppedSegments, true)]
    [InlineData(TriangulationWarningFlags.UsedPlainDelaunayFallback | TriangulationWarningFlags.DroppedSegments, true)]
    [InlineData(TriangulationWarningFlags.UsedNonConformingCdt, false)]
    [InlineData(TriangulationWarningFlags.DroppedQualityConstraints, false)]
    [InlineData(TriangulationWarningFlags.None, false)]
    public void ConstraintsWereDropped_MatchesOnlyConstraintLossFlags(TriangulationWarningFlags flags, bool expected)
    {
        Assert.Equal(expected, MeshConstraintTools.ConstraintsWereDropped(flags));
    }

    [Theory]
    [InlineData(TriangulationWarningFlags.DroppedQualityConstraints, true)]
    [InlineData(TriangulationWarningFlags.UsedNonConformingCdt | TriangulationWarningFlags.DroppedQualityConstraints, true)]
    [InlineData(TriangulationWarningFlags.UsedNonConformingCdt, false)]
    [InlineData(TriangulationWarningFlags.DroppedSegments, false)]
    [InlineData(TriangulationWarningFlags.None, false)]
    public void QualityWasDropped_MatchesOnlyQualityLossFlags(TriangulationWarningFlags flags, bool expected)
    {
        Assert.Equal(expected, MeshConstraintTools.QualityWasDropped(flags));
    }

    private static bool MatchesEdge((int a, int b) segment, int a, int b)
    {
        return (segment.a == a && segment.b == b) || (segment.a == b && segment.b == a);
    }
}
