using MoleHill.Shared;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class AdaptivePolylineBuilderTests
{
    [RhinoNativeFact]
    public void TryGetPolyline_RefinesSmoothCurve_WhenRequestedEdgeLengthProvided()
    {
        Curve curve = CreateSmoothPathCurve();

        bool coarseSuccess = AdaptivePolylineBuilder.TryGetPolyline(
            curve,
            modelTolerance: 5.0,
            requireClosed: false,
            requestedEdgeLength: 0.0,
            maxArea: 0.0,
            out Polyline coarse);
        bool refinedSuccess = AdaptivePolylineBuilder.TryGetPolyline(
            curve,
            modelTolerance: 5.0,
            requireClosed: false,
            requestedEdgeLength: 2.0,
            maxArea: 0.0,
            out Polyline refined);

        Assert.True(coarseSuccess);
        Assert.True(refinedSuccess);
        Assert.True(refined.Count > coarse.Count);
    }

    [RhinoNativeFact]
    public void TryGetPolyline_UsesRequestedEdgeLength_AsMaximumSegmentLength()
    {
        Curve curve = CreateSmoothPathCurve();

        bool success = AdaptivePolylineBuilder.TryGetPolyline(
            curve,
            modelTolerance: 5.0,
            requireClosed: false,
            requestedEdgeLength: 2.0,
            maxArea: 0.0,
            out Polyline polyline);

        Assert.True(success);
        Assert.InRange(GetMaxSegmentLength(polyline), 0.0, 2.01);
    }

    [RhinoNativeFact]
    public void TryGetPolyline_StraightInPlanWithAProfile_KeepsTheHeights()
    {
        // Straight in plan, with a 1 m sag in its profile: a graded centreline or a draped straight line.
        // Simplification used to test collinearity in plan only, collapse the run to one chord and lose the sag.
        Curve curve = Curve.CreateInterpolatedCurve(
            Enumerable.Range(0, 11).Select(i => new Point3d(i * 10.0, 0.0, -Math.Sin(Math.PI * i / 10.0))),
            3);

        bool success = AdaptivePolylineBuilder.TryGetPolyline(
            curve,
            modelTolerance: 0.01,
            requireClosed: false,
            requestedEdgeLength: 0.0,
            maxArea: 0.0,
            out Polyline polyline);

        Assert.True(success);
        for (int i = 0; i <= 200; i++)
        {
            double x = i * 0.5;
            curve.ClosestPoint(new Point3d(x, 0.0, 0.0), out double t);
            double expected = curve.PointAt(t).Z;
            double actual = HeightOnPolylineAt(polyline, x);
            Assert.True(Math.Abs(actual - expected) <= 0.05, $"x={x}: polyline Z {actual:F3}, curve Z {expected:F3}");
        }
    }

    private static double HeightOnPolylineAt(Polyline polyline, double x)
    {
        for (int i = 1; i < polyline.Count; i++)
        {
            Point3d a = polyline[i - 1];
            Point3d b = polyline[i];
            if (x >= Math.Min(a.X, b.X) - 1e-9 && x <= Math.Max(a.X, b.X) + 1e-9)
            {
                double t = Math.Abs(b.X - a.X) < 1e-12 ? 0.0 : (x - a.X) / (b.X - a.X);
                return a.Z + (t * (b.Z - a.Z));
            }
        }

        return double.NaN;
    }

    private static Curve CreateSmoothPathCurve()
    {
        return Curve.CreateInterpolatedCurve(
            new[]
            {
                new Point3d(0.0, 0.0, 0.0),
                new Point3d(6.0, 8.0, 0.0),
                new Point3d(12.0, 12.0, 0.0),
                new Point3d(18.0, 8.0, 0.0),
                new Point3d(24.0, 0.0, 0.0)
            },
            degree: 3)!;
    }

    private static double GetMaxSegmentLength(Polyline polyline)
    {
        double maxLength = 0.0;
        for (int i = 1; i < polyline.Count; i++)
        {
            maxLength = Math.Max(maxLength, polyline[i - 1].DistanceTo(polyline[i]));
        }

        return maxLength;
    }
}
