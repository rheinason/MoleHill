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
