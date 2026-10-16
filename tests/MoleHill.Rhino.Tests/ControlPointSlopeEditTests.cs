using MoleHill.Rhino.Services;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class ControlPointSlopeEditTests
{
    [RhinoNativeFact]
    public void TryCreateSections_Polyline_ChangesSegmentAndRetainsOtherVertices()
    {
        using var source = new PolylineCurve(new[]
        {
            new Point3d(0, 0, 10), new Point3d(3, 4, 90),
            new Point3d(6, 8, 70), new Point3d(9, 12, 50)
        });
        Assert.True(ControlPointSlopeEdit.TryCreateSections(source, new[] { 1 }, Array.Empty<int>(),
            new Point3d(0, 0, 10), 0.1, 0.001, out Curve? result, out string? error), error);
        using (result)
        {
            var edited = Assert.IsType<PolylineCurve>(result);
            Assert.Equal(source.Point(0), edited.Point(0));
            Assert.Equal(new Point3d(3, 4, 10.5), edited.Point(1));
            Assert.Equal(new Point3d(6, 8, 11), edited.Point(2));
            Assert.Equal(source.Point(3), edited.Point(3));
        }
    }

    [RhinoNativeFact]
    public void TryCreateSections_Polycurve_KeepsAdjacentEndpointsConnected()
    {
        using var source = new PolyCurve();
        source.Append(new LineCurve(new Point3d(0, 0, 10), new Point3d(3, 4, 90)));
        source.Append(new LineCurve(new Point3d(3, 4, 90), new Point3d(6, 8, 70)));
        source.Append(new LineCurve(new Point3d(6, 8, 70), new Point3d(9, 12, 50)));
        Assert.True(ControlPointSlopeEdit.TryCreateSections(source, new[] { 1 }, Array.Empty<int>(),
            new Point3d(0, 0, 10), 0.1, 0.001, out Curve? result, out string? error), error);
        using (result)
        {
            var edited = Assert.IsType<PolyCurve>(result);
            Assert.Equal(source.PointAtStart, edited.PointAtStart);
            Assert.Equal(source.PointAtEnd, edited.PointAtEnd);
            Assert.Equal(new Point3d(3, 4, 10.5), edited.SegmentCurve(1).PointAtStart);
            Assert.Equal(new Point3d(6, 8, 11), edited.SegmentCurve(1).PointAtEnd);
            Assert.Equal(edited.SegmentCurve(0).PointAtEnd, edited.SegmentCurve(1).PointAtStart);
            Assert.Equal(edited.SegmentCurve(1).PointAtEnd, edited.SegmentCurve(2).PointAtStart);
        }
    }

    [RhinoNativeFact]
    public void TryCreateSections_ReferenceAtSectionStart_RetainsReferenceElevation()
    {
        using var source = new PolylineCurve(new[] { new Point3d(0, 0, 10), new Point3d(3, 4, 90) });
        Assert.True(ControlPointSlopeEdit.TryCreateSections(source, new[] { 0 }, Array.Empty<int>(),
            source.PointAtStart, 0.1, 0.001, out Curve? result, out string? error), error);
        using (result)
        {
            Assert.Equal(source.PointAtStart, result!.PointAtStart);
            Assert.Equal(10.5, result.PointAtEnd.Z);
        }
    }

    [RhinoNativeFact]
    public void TryCreate_RationalNurbs_PreservesWeightsAndOtherPoints()
    {
        using var source = NurbsCurve.Create(false, 3, new[]
        {
            new Point3d(0, 0, 10), new Point3d(12, 16, 30),
            new Point3d(25, 20, 15), new Point3d(30, 0, 12)
        });
        source.Points.SetPoint(1, source.Points[1].Location, 0.75);

        Assert.True(ControlPointSlopeEdit.TryCreate(source, new[] { 1 }, new Point3d(0, 0, 10),
            -0.02, 0.001, out Curve? result, out string? error), error);
        using (result)
        {
            var edited = Assert.IsType<NurbsCurve>(result);
            Assert.True(new Point3d(12, 16, 9.6).DistanceTo(edited.Points[1].Location) < 1e-10);
            Assert.Equal(0.75, edited.Points[1].Weight, 12);
            Assert.Equal(30, source.Points[1].Location.Z);
            foreach (int index in new[] { 0, 2, 3 })
                Assert.Equal(source.Points[index], edited.Points[index]);
            Assert.Equal(source.Domain, edited.Domain);
        }
    }

    [RhinoNativeFact]
    public void TryCreate_MultipleVertices_UsesEachPointsPlanDistance()
    {
        using var source = new PolylineCurve(new[]
        {
            new Point3d(3, 4, 90), new Point3d(6, 8, 70), new Point3d(9, 12, 50)
        });
        Assert.True(ControlPointSlopeEdit.TryCreate(source, new[] { 0, 2 }, new Point3d(0, 0, 10),
            0.1, 0.001, out Curve? result, out string? error), error);
        using (result)
        {
            var edited = Assert.IsType<PolylineCurve>(result);
            Assert.Equal(new Point3d(3, 4, 10.5), edited.Point(0));
            Assert.Equal(source.Point(1), edited.Point(1));
            Assert.Equal(new Point3d(9, 12, 11.5), edited.Point(2));
        }
    }

    [RhinoNativeFact]
    public void TryCreate_ClosedPolylineSeam_KeepsBothEndsTogether()
    {
        using var source = new PolylineCurve(new[]
        {
            new Point3d(3, 4, 90), new Point3d(6, 8, 70),
            new Point3d(9, 12, 50), new Point3d(3, 4, 90)
        });
        Assert.True(ControlPointSlopeEdit.TryCreate(source, new[] { 0 }, Point3d.Origin,
            0.1, 0.001, out Curve? result, out string? error), error);
        using (result)
        {
            Assert.True(result!.IsClosed);
            Assert.Equal(result.PointAtStart, result.PointAtEnd);
            Assert.Equal(0.5, result.PointAtStart.Z);
        }
    }

    [RhinoNativeFact]
    public void TryCreate_PeriodicNurbs_PreservesPeriodicSeam()
    {
        using var source = NurbsCurve.Create(true, 3, new[]
        {
            new Point3d(5, 0, 1), new Point3d(0, 5, 2),
            new Point3d(-5, 0, 3), new Point3d(0, -5, 4)
        });
        Assert.True(source.IsPeriodic);
        Assert.True(ControlPointSlopeEdit.TryCreate(source, new[] { 0 }, new Point3d(20, 20, 10),
            0.02, 0.001, out Curve? result, out string? error), error);
        using (result)
        {
            var edited = Assert.IsType<NurbsCurve>(result);
            Assert.True(edited.IsPeriodic);
            Assert.True(edited.IsClosed);
            int period = source.Points.Count - source.Degree;
            Assert.Equal(edited.Points[0], edited.Points[period]);
        }
    }

    [RhinoNativeFact]
    public void TryCreate_ZeroSlope_LevelsOnlySelectedLineEnd()
    {
        using var source = new LineCurve(new Point3d(0, 0, 99), new Point3d(3, 4, 80));
        Assert.True(ControlPointSlopeEdit.TryCreate(source, new[] { 1 }, new Point3d(0, 0, 10),
            0, 0.001, out Curve? result, out string? error), error);
        using (result)
        {
            Assert.Equal(source.PointAtStart, result!.PointAtStart);
            Assert.Equal(new Point3d(3, 4, 10), result.PointAtEnd);
        }
    }

    [RhinoNativeFact]
    public void TryCreate_ZeroRunOrInvalidInput_RejectsWithoutChangingSource()
    {
        using var source = new LineCurve(Point3d.Origin, new Point3d(3, 4, 80));
        foreach (var input in new[]
        {
            (Indices: new[] { 0 }, Reference: new Point3d(0, 0, 10), Ratio: 0.02),
            (Indices: new[] { 9 }, Reference: new Point3d(2, 0, 10), Ratio: 0.02),
            (Indices: new[] { 1 }, Reference: new Point3d(2, 0, 10), Ratio: double.NaN),
            (Indices: Array.Empty<int>(), Reference: new Point3d(2, 0, 10), Ratio: 0.02)
        })
        {
            Assert.False(ControlPointSlopeEdit.TryCreate(source, input.Indices, input.Reference,
                input.Ratio, 0.001, out Curve? result, out string? error));
            Assert.Null(result);
            Assert.NotNull(error);
            Assert.Equal(new Point3d(3, 4, 80), source.PointAtEnd);
        }
    }
}
