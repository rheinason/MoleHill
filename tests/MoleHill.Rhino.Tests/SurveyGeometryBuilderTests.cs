using MoleHill.Core.Interop;
using MoleHill.Rhino.Services;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Rhino.Tests;

public sealed class SurveyGeometryBuilderTests
{
    /// <summary>Points on a gentle arc, so a three-point arc through them is well conditioned.</summary>
    private static List<Point3d> ArcPoints() => new()
    {
        new Point3d(0, 0, 0),
        new Point3d(10, 4, 0),
        new Point3d(20, 0, 0)
    };

    private static List<Point3d> Line(int count)
    {
        var points = new List<Point3d>(count);
        for (int i = 0; i < count; i++)
            points.Add(new Point3d(i * 10.0, 0, i));
        return points;
    }

    private static SurveyFigure Figure(
        IReadOnlyList<int> indices,
        IReadOnlyList<bool>? arcs = null,
        bool closed = false) =>
        new(
            "EP",
            FieldCodeRole.Breakline,
            "MoleHill::Inputs::Breaklines",
            indices,
            arcs ?? indices.Select(static _ => false).ToList(),
            closed,
            null);

    [RhinoNativeFact]
    public void BuildFigure_TwoPoints_MakesAPolyline()
    {
        Curve? curve = SurveyGeometryBuilder.BuildFigure(Figure(new[] { 0, 1 }), Line(2));

        Assert.NotNull(curve);
        Assert.Equal(2, curve!.PointAtStart.DistanceTo(curve.PointAtEnd) > 0 ? 2 : 0);
        Assert.False(curve.IsClosed);
    }

    [RhinoNativeFact]
    public void BuildFigure_SinglePoint_ReturnsNull()
    {
        Assert.Null(SurveyGeometryBuilder.BuildFigure(Figure(new[] { 0 }), Line(2)));
    }

    [RhinoNativeFact]
    public void BuildFigure_ClosedFlag_ClosesTheCurve()
    {
        var points = new List<Point3d>
        {
            new(0, 0, 0), new(10, 0, 0), new(10, 10, 0), new(0, 10, 0)
        };

        Curve? curve = SurveyGeometryBuilder.BuildFigure(Figure(new[] { 0, 1, 2, 3 }, closed: true), points);

        Assert.NotNull(curve);
        Assert.True(curve!.IsClosed);
    }

    [RhinoNativeFact]
    public void BuildFigure_ClosedFigureAlreadyBackAtStart_IsNotDoubled()
    {
        var points = new List<Point3d>
        {
            new(0, 0, 0), new(10, 0, 0), new(10, 10, 0), new(0, 0, 0)
        };

        Curve? curve = SurveyGeometryBuilder.BuildFigure(Figure(new[] { 0, 1, 2, 3 }, closed: true), points);

        Assert.NotNull(curve);
        Assert.True(curve!.IsClosed);
    }

    [RhinoNativeFact]
    public void BuildFigure_ArcFlagOnTheMiddlePoint_BendsThroughIt()
    {
        Curve? curve = SurveyGeometryBuilder.BuildFigure(
            Figure(new[] { 0, 1, 2 }, new[] { false, true, false }),
            ArcPoints());

        Assert.NotNull(curve);

        // A straight polyline through those points would be shorter than the arc that bulges past them.
        double straight = ArcPoints()[0].DistanceTo(ArcPoints()[1]) + ArcPoints()[1].DistanceTo(ArcPoints()[2]);
        Assert.True(curve!.GetLength() > straight);
    }

    [RhinoNativeFact]
    public void BuildFigure_NoArcFlags_StaysAPolyline()
    {
        Curve? curve = SurveyGeometryBuilder.BuildFigure(Figure(new[] { 0, 1, 2 }), Line(3));

        Assert.NotNull(curve);
        Assert.True(curve!.TryGetPolyline(out _));
    }

    [RhinoNativeFact]
    public void BuildFigure_CollinearArcFlag_FallsBackToAStraightSegment()
    {
        // A crew can flag a point that does not actually curve. Drawing it straight beats failing.
        Curve? curve = SurveyGeometryBuilder.BuildFigure(
            Figure(new[] { 0, 1, 2 }, new[] { false, true, false }),
            new List<Point3d> { new(0, 0, 0), new(10, 0, 0), new(20, 0, 0) });

        Assert.NotNull(curve);
        Assert.Equal(20.0, curve!.GetLength(), 6);
    }

    [RhinoNativeFact]
    public void BuildFigure_ArcFlagOnTheLastPoint_DoesNotThrow()
    {
        Curve? curve = SurveyGeometryBuilder.BuildFigure(
            Figure(new[] { 0, 1, 2 }, new[] { false, false, true }),
            ArcPoints());

        Assert.NotNull(curve);
    }

    [RhinoNativeFact]
    public void BuildFigure_RepeatedShotAtTheSamePlace_IsDroppedRatherThanFailing()
    {
        var points = new List<Point3d> { new(0, 0, 0), new(0, 0, 0), new(10, 0, 0) };

        Curve? curve = SurveyGeometryBuilder.BuildFigure(Figure(new[] { 0, 1, 2 }), points);

        Assert.NotNull(curve);
        Assert.Equal(10.0, curve!.GetLength(), 6);
    }

    [RhinoNativeFact]
    public void BuildFigure_AllPointsCoincident_ReturnsNull()
    {
        var points = new List<Point3d> { new(5, 5, 0), new(5, 5, 0) };

        Assert.Null(SurveyGeometryBuilder.BuildFigure(Figure(new[] { 0, 1 }), points));
    }

    [RhinoNativeFact]
    public void BuildFigure_IndexOutsideThePointList_IsSkipped()
    {
        Curve? curve = SurveyGeometryBuilder.BuildFigure(Figure(new[] { 0, 99, 1 }), Line(2));

        Assert.NotNull(curve);
        Assert.Equal(Line(2)[0].DistanceTo(Line(2)[1]), curve!.GetLength(), 6);
    }

    [RhinoNativeFact]
    public void BuildFigure_KeepsTheSurveyedZ()
    {
        Curve? curve = SurveyGeometryBuilder.BuildFigure(Figure(new[] { 0, 1, 2 }), Line(3));

        Assert.NotNull(curve);
        Assert.Equal(0.0, curve!.PointAtStart.Z, 6);
        Assert.Equal(2.0, curve.PointAtEnd.Z, 6);
    }
}
