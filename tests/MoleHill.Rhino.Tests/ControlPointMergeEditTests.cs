using MoleHill.Rhino.Services;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class ControlPointMergeEditTests
{
    [RhinoNativeFact]
    public void TryReplaceLocations_Nurbs_PreservesWeightsKnotsAndUnselectedPoints()
    {
        using var source = NurbsCurve.Create(false, 3, new[]
        {
            Point3d.Origin, new Point3d(1, 2, 3), new Point3d(4, 5, 6), new Point3d(10, 0, 0)
        });
        source.Points.SetPoint(1, source.Points[1].Location, 0.75);
        var location = new Point3d(2.5, 3.5, 4.5);
        Assert.True(ControlPointSlopeEdit.TryReplaceLocations(source, new Dictionary<int, Point3d>
            { [1] = location, [2] = location }, out Curve? result, out string? error), error);
        using (result)
        {
            var edited = Assert.IsType<NurbsCurve>(result);
            Assert.Equal(source.Points.Count, edited.Points.Count);
            Assert.Equal(source.Knots.ToArray(), edited.Knots.ToArray());
            Assert.Equal(source.Points[0], edited.Points[0]);
            Assert.Equal(source.Points[3], edited.Points[3]);
            Assert.Equal(location, edited.Points[1].Location);
            Assert.Equal(location, edited.Points[2].Location);
            Assert.Equal(0.75, edited.Points[1].Weight);
            Assert.Equal(new Point3d(1, 2, 3), source.Points[1].Location);
        }
    }

    [RhinoNativeFact]
    public void TryReplaceLocations_PeriodicSeam_MovesCoupledPointsTogether()
    {
        using var source = NurbsCurve.Create(true, 3, new[]
        {
            new Point3d(5, 0, 1), new Point3d(0, 5, 2), new Point3d(-5, 0, 3), new Point3d(0, -5, 4)
        });
        var target = new Point3d(7, 2, 5);
        Assert.True(ControlPointSlopeEdit.TryReplaceLocations(source, new Dictionary<int, Point3d> { [0] = target },
            out Curve? result, out string? error), error);
        using (result)
        {
            var edited = Assert.IsType<NurbsCurve>(result);
            Assert.True(edited.IsPeriodic);
            Assert.Equal(target, edited.Points[0].Location);
            Assert.Equal(target, edited.Points[source.Points.Count - source.Degree].Location);
        }
    }

    [RhinoNativeFact]
    public void TryReplaceLocations_CollapsedLineOrConflictingSeam_RejectsBeforeEditing()
    {
        using var line = new LineCurve(Point3d.Origin, new Point3d(1, 2, 3));
        Assert.False(ControlPointSlopeEdit.TryReplaceLocations(line,
            new Dictionary<int, Point3d> { [1] = Point3d.Origin }, out Curve? result, out _));
        Assert.Null(result);
        Assert.Equal(new Point3d(1, 2, 3), line.PointAtEnd);
        using var closed = new PolylineCurve(new[] { Point3d.Origin, new Point3d(3, 0, 0), new Point3d(3, 3, 0), Point3d.Origin });
        Assert.False(ControlPointSlopeEdit.TryReplaceLocations(closed,
            new Dictionary<int, Point3d> { [0] = new Point3d(1, 0, 0), [3] = new Point3d(2, 0, 0) }, out result, out _));
        Assert.Null(result);
        Assert.True(closed.IsClosed);
    }
}
