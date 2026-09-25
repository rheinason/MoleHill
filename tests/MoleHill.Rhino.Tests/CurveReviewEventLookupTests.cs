using MoleHill.Rhino.Services;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class CurveReviewEventLookupTests
{
    [Fact]
    public void FirstEventPoint_NoEventOfKind_ReturnsNullRatherThanOrigin()
    {
        var events = new[]
        {
            new CurveReviewEvent(CurveReviewEventKind.Crest, 10.0, new Point3d(10, 0, 5), "Crest")
        };

        Assert.Null(CurveReviewAnalysis.FirstEventPoint(events, CurveReviewEventKind.TerrainGap));
    }

    [Fact]
    public void FirstEventPoint_SeveralOfKind_ReturnsTheFirst()
    {
        var events = new[]
        {
            new CurveReviewEvent(CurveReviewEventKind.Crest, 5.0, new Point3d(5, 0, 1), "Crest"),
            new CurveReviewEvent(CurveReviewEventKind.VerticalBreak, 10.0, new Point3d(10, 0, 2), "PI"),
            new CurveReviewEvent(CurveReviewEventKind.VerticalBreak, 20.0, new Point3d(20, 0, 3), "PI")
        };

        Assert.Equal(new Point3d(10, 0, 2), CurveReviewAnalysis.FirstEventPoint(events, CurveReviewEventKind.VerticalBreak));
    }

    [Theory]
    [InlineData(4.9, 2.0)]
    [InlineData(5.1, -3.0)]
    [InlineData(-1.0, 2.0)]
    [InlineData(99.0, -3.0)]
    public void SpanGradeAtStation_ExactStation_PicksTheContainingStretch(double station, double expected)
    {
        // Matched by the picked point's own station, so a pick just past a kink reads the next stretch
        // even when the nearest sample still sits on the previous one.
        var spans = new[]
        {
            new CurveReviewSpan(0.0, 5.0, Point3d.Origin, Point3d.Origin, Point3d.Origin, 2.0, 5.0),
            new CurveReviewSpan(5.0, 10.0, Point3d.Origin, Point3d.Origin, Point3d.Origin, -3.0, 5.0)
        };

        Assert.Equal(expected, CurveReviewAnalysis.SpanGradeAtStation(spans, station));
    }

    [Fact]
    public void SpanGradeAtStation_NoStretches_ReturnsNull()
    {
        Assert.Null(CurveReviewAnalysis.SpanGradeAtStation(Array.Empty<CurveReviewSpan>(), 1.0));
    }
}
