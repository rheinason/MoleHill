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
}
