using MoleHill.Rhino.Services;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class GeometryCommandAlgorithmsTests
{
    [RhinoNativeFact]
    public void CalculatePlanLength_IgnoresCurveElevation()
    {
        var curve = new LineCurve(new Point3d(0, 0, 10), new Point3d(3, 4, 110));

        double length = GeometryCommandAlgorithms.CalculatePlanLength(curve, curve.Domain.T0, curve.Domain.T1);

        Assert.Equal(5.0, length, 6);
    }

    [RhinoNativeFact]
    public void CalculatePlanLength_KinkedPolyline_ReturnsExactPlanLength()
    {
        using var curve = new PolylineCurve(new[]
        {
            new Point3d(0, 0, 0),
            new Point3d(30, 40, 10),
            new Point3d(80, 40, -5),
            new Point3d(100, 55, 2)
        });

        double length = GeometryCommandAlgorithms.CalculatePlanLength(curve, curve.Domain.T0, curve.Domain.T1);

        Assert.Equal(125.0, length, 9);
    }

    [RhinoNativeFact]
    public void CurveSectionEdit_ZeroGrade_FlattensSelectedLine()
    {
        var curve = new LineCurve(new Point3d(0, 0, 10), new Point3d(10, 0, 20));

        bool succeeded = GeometryCommandAlgorithms.TryCreateCurveSectionEdit(
            curve, curve.Domain.T0, curve.Domain.T1, CurveSectionEditMode.GradePercent,
            0.0, 0.0, null, 1e-6, out Curve? result, out string? error);

        Assert.True(succeeded, error);
        Assert.NotNull(result);
        Assert.Equal(10.0, result!.PointAtStart.Z, 6);
        Assert.Equal(10.0, result.PointAtEnd.Z, 6);
        Assert.Equal(10.0, result.PointAtEnd.X, 6);
        result.Dispose();
    }

    [RhinoNativeFact]
    public void CurveSectionEdit_InteriorZeroGrade_FlattensOnlySelectedInterval()
    {
        using var curve = new LineCurve(new Point3d(0, 0, 10), new Point3d(10, 0, 20));

        bool succeeded = GeometryCommandAlgorithms.TryCreateCurveSectionEdit(
            curve, curve.Domain.ParameterAt(0.25), curve.Domain.ParameterAt(0.75),
            CurveSectionEditMode.GradePercent, 0.0, 0.0, null, 1e-6,
            out Curve? result, out string? error);

        Assert.True(succeeded, error);
        Assert.NotNull(result);
        using (result)
        {
            AssertCurveContainsPoint(result!, new Point3d(1, 0, 11));
            AssertCurveContainsPoint(result, new Point3d(5, 0, 12.5));
            AssertCurveContainsPoint(result, new Point3d(9, 0, 19));
            AssertCurveContainsPoint(result, new Point3d(7.5, 0, 12.5));
            AssertCurveContainsPoint(result, new Point3d(7.5, 0, 17.5));
        }
    }
    [Fact]
    public void InterpolateTwoPointElevation_Midpoint_ReturnsAverageElevation()
    {
        Point3d low = new(0, 0, 10);
        Point3d high = new(10, 0, 20);
        Point3d sample = new(5, 0, 999);

        double elevation = GeometryCommandAlgorithms.InterpolateTwoPointElevation(low, high, sample);

        Assert.Equal(15.0, elevation, 6);
    }

    [RhinoNativeFact]
    public void CurveSectionEdit_AnchorEnd_HoldsSecondPickAndMovesFirst()
    {
        using var curve = new LineCurve(new Point3d(0, 0, 0), new Point3d(100, 0, 0));

        bool succeeded = GeometryCommandAlgorithms.TryCreateCurveSectionEdit(
            curve, curve.Domain.ParameterAt(0.25), curve.Domain.ParameterAt(0.75),
            CurveSectionEditMode.GradePercent, 10.0, 0.0, null,
            CurveSectionAnchor.End, 0.0, SoftEditFalloff.Smooth, measureDeviation: true, 1e-6,
            out Curve? result, out CurveSectionEditResult report, out string? error);

        Assert.True(succeeded, error);
        Assert.NotNull(result);
        using (result)
        {
            Assert.Equal(0.0, report.SecondElevation, 6);
            Assert.Equal(-5.0, report.FirstElevation, 6);
            Assert.Equal(0.10, report.SlopeRatio, 6);
            Assert.Equal(50.0, report.PlanLength, 6);
        }
    }

    [RhinoNativeFact]
    public void CurveSectionEdit_AnchorMiddle_SplitsTheDeltaBetweenBothEnds()
    {
        using var curve = new LineCurve(new Point3d(0, 0, 0), new Point3d(100, 0, 0));

        bool succeeded = GeometryCommandAlgorithms.TryCreateCurveSectionEdit(
            curve, curve.Domain.ParameterAt(0.25), curve.Domain.ParameterAt(0.75),
            CurveSectionEditMode.GradePercent, 10.0, 0.0, null,
            CurveSectionAnchor.Middle, 0.0, SoftEditFalloff.Smooth, measureDeviation: true, 1e-6,
            out Curve? result, out CurveSectionEditResult report, out string? error);

        Assert.True(succeeded, error);
        Assert.NotNull(result);
        using (result)
        {
            Assert.Equal(-2.5, report.FirstElevation, 6);
            Assert.Equal(2.5, report.SecondElevation, 6);
        }
    }

    /// <summary>
    /// Picking down-curve and up-curve must not produce the same result: the grade runs from the first
    /// point picked towards the second, whichever way the curve happens to be parameterised.
    /// </summary>
    [RhinoNativeFact]
    public void CurveSectionEdit_ReversedPickOrder_RunsGradeFromTheFirstPick()
    {
        using var curve = new LineCurve(new Point3d(0, 0, 0), new Point3d(100, 0, 0));
        double atX25 = curve.Domain.ParameterAt(0.25);
        double atX75 = curve.Domain.ParameterAt(0.75);

        bool succeeded = GeometryCommandAlgorithms.TryCreateCurveSectionEdit(
            curve, atX75, atX25,
            CurveSectionEditMode.GradePercent, 10.0, 0.0, null,
            CurveSectionAnchor.Start, 0.0, SoftEditFalloff.Smooth, measureDeviation: true, 1e-6,
            out Curve? result, out CurveSectionEditResult report, out string? error);

        Assert.True(succeeded, error);
        Assert.NotNull(result);
        using (result)
        {
            // The first pick sits at x=75 and is the anchor; the far end of the section is x=25.
            Assert.Equal(0.0, report.FirstElevation, 6);
            Assert.Equal(5.0, report.SecondElevation, 6);

            // 25 along from the first pick at 10% is 2.5 up — the opposite sense to what the same two
            // points picked the other way round would produce.
            Assert.Equal(2.5, ElevationAtPlanX(result!, 50.0), 6);
        }
    }

    /// <summary>
    /// The abrupt change the command was known for: a graded section's far end no longer matches the
    /// curve it joins, and the two were bridged by a vertical line. A transition has to absorb that
    /// delta into the neighbouring stretch instead, without dragging the curve's own endpoint along.
    /// </summary>
    [RhinoNativeFact]
    public void CurveSectionEdit_Transition_AbsorbsStepWithoutMovingCurveEnd()
    {
        using var curve = new LineCurve(new Point3d(0, 0, 0), new Point3d(100, 0, 0));

        bool succeeded = GeometryCommandAlgorithms.TryCreateCurveSectionEdit(
            curve, curve.Domain.ParameterAt(0.25), curve.Domain.ParameterAt(0.5),
            CurveSectionEditMode.GradePercent, 10.0, 0.0, null,
            CurveSectionAnchor.Start, 20.0, SoftEditFalloff.Smooth, measureDeviation: true, 1e-6,
            out Curve? result, out CurveSectionEditResult report, out string? error);

        Assert.True(succeeded, error);
        Assert.NotNull(result);
        using (result)
        {
            // Anchored at x=25, so the section climbs 10% over 25 to reach 2.5 at x=50.
            Assert.Equal(2.5, report.SecondElevation, 6);
            Assert.Equal(2.5, ElevationAtPlanX(result!, 50.0), 3);

            // Absorbed over 20 units of the stretch beyond the section, and no further.
            Assert.InRange(ElevationAtPlanX(result!, 60.0), 0.05, 2.45);
            Assert.Equal(0.0, ElevationAtPlanX(result!, 75.0), 3);
            Assert.Equal(0.0, result!.PointAtEnd.Z, 6);
            Assert.Equal(0.0, result.PointAtStart.Z, 6);

            AssertNoVerticalStep(result);
        }
    }

    [RhinoNativeFact]
    public void CurveSectionEdit_WithoutTransition_StillBridgesWithAVerticalStep()
    {
        using var curve = new LineCurve(new Point3d(0, 0, 0), new Point3d(100, 0, 0));

        bool succeeded = GeometryCommandAlgorithms.TryCreateCurveSectionEdit(
            curve, curve.Domain.ParameterAt(0.25), curve.Domain.ParameterAt(0.5),
            CurveSectionEditMode.GradePercent, 10.0, 0.0, null,
            CurveSectionAnchor.Start, 0.0, SoftEditFalloff.Smooth, measureDeviation: true, 1e-6,
            out Curve? result, out CurveSectionEditResult report, out string? error);

        Assert.True(succeeded, error);
        Assert.NotNull(result);
        using (result)
        {
            Assert.Equal(2.5, report.SecondElevation, 6);

            // Both elevations exist at x=50: the graded end and the untouched stretch it joins.
            AssertCurveContainsPoint(result!, new Point3d(50, 0, 2.5));
            AssertCurveContainsPoint(result!, new Point3d(50, 0, 0));
        }
    }

    [RhinoNativeFact]
    public void CurveSectionEdit_LinearFalloff_AbsorbsTheStepWithoutSineEasing()
    {
        using var curve = new LineCurve(new Point3d(0, 0, 0), new Point3d(100, 0, 0));

        bool succeeded = GeometryCommandAlgorithms.TryCreateCurveSectionEdit(
            curve, curve.Domain.ParameterAt(0.25), curve.Domain.ParameterAt(0.5),
            CurveSectionEditMode.GradePercent, 10.0, 0.0, null,
            CurveSectionAnchor.Start, 20.0, SoftEditFalloff.Linear, measureDeviation: true, 1e-6,
            out Curve? result, out _, out string? error);

        Assert.True(succeeded, error);
        Assert.NotNull(result);
        using (result)
        {
            // A quarter into a 20-unit transition: linear keeps three quarters of the 2.5 step, where
            // the sine easing of Smooth would still be holding about 2.13. The midpoint is no use as a
            // discriminator — both falloffs pass through half there.
            Assert.Equal(1.875, ElevationAtPlanX(result!, 55.0), 2);
            Assert.Equal(1.25, ElevationAtPlanX(result!, 60.0), 2);
            Assert.Equal(0.0, ElevationAtPlanX(result!, 70.0), 3);
        }
    }

    [RhinoNativeFact]
    public void CurveSectionEdit_BetweenCurrentElevations_HoldsBothPickedEnds()
    {
        using var curve = new PolylineCurve(new[]
        {
            new Point3d(0, 0, 0),
            new Point3d(50, 0, 30),
            new Point3d(100, 0, 10)
        });

        bool succeeded = GeometryCommandAlgorithms.TryCreateCurveSectionEdit(
            curve, curve.Domain.T0, curve.Domain.T1,
            CurveSectionEditMode.BetweenCurrentElevations, 0.0, 0.0, null,
            CurveSectionAnchor.Start, 10.0, SoftEditFalloff.Smooth, measureDeviation: true, 1e-6,
            out Curve? result, out CurveSectionEditResult report, out string? error);

        Assert.True(succeeded, error);
        Assert.NotNull(result);
        using (result)
        {
            Assert.Equal(0.0, report.FirstElevation, 6);
            Assert.Equal(10.0, report.SecondElevation, 6);
            Assert.Equal(0.1, report.SlopeRatio, 6);
            Assert.Equal(15.0, ElevationAtPlanX(result!, 50.0), 6);
        }
    }

    /// <summary>
    /// Blending pulled the whole picked stretch towards the terrain at full strength and then stopped
    /// dead at the picks, stepping at both ends. The transition length is spent inside the section
    /// instead, ramping the pull up from each picked end.
    /// </summary>
    [RhinoNativeFact]
    public void CurveSectionEdit_BlendToTerrain_FeathersInFromBothPickedEnds()
    {
        using var curve = new LineCurve(new Point3d(0, 0, 0), new Point3d(100, 0, 0));
        using Mesh terrain = Mesh.CreateFromPlane(
            new Plane(new Point3d(50, 0, 100), Vector3d.ZAxis),
            new Interval(-60, 60), new Interval(-40, 40), 24, 16);

        bool succeeded = GeometryCommandAlgorithms.TryCreateCurveSectionEdit(
            curve, curve.Domain.ParameterAt(0.25), curve.Domain.ParameterAt(0.75),
            CurveSectionEditMode.BlendToTerrain, 0.0, 100.0, terrain,
            CurveSectionAnchor.Start, 10.0, SoftEditFalloff.Smooth, measureDeviation: true, 1e-6,
            out Curve? result, out CurveSectionEditResult report, out string? error);

        Assert.True(succeeded, error);
        Assert.NotNull(result);
        using (result)
        {
            Assert.Equal(0.0, report.FirstElevation, 6);
            Assert.Equal(0.0, report.SecondElevation, 6);
            Assert.Equal(100.0, ElevationAtPlanX(result!, 50.0), 3);

            // Half way up a 10-unit ramp from the pick at x=25.
            Assert.Equal(50.0, ElevationAtPlanX(result!, 30.0), 1);
            Assert.Equal(0.0, result!.PointAtStart.Z, 6);
            Assert.Equal(0.0, result.PointAtEnd.Z, 6);
            AssertNoVerticalStep(result);
        }
    }

    [RhinoNativeFact]
    public void CurveSectionEdit_BlendToTerrainWithoutTransition_StepsAtBothPicks()
    {
        using var curve = new LineCurve(new Point3d(0, 0, 0), new Point3d(100, 0, 0));
        using Mesh terrain = Mesh.CreateFromPlane(
            new Plane(new Point3d(50, 0, 100), Vector3d.ZAxis),
            new Interval(-60, 60), new Interval(-40, 40), 24, 16);

        bool succeeded = GeometryCommandAlgorithms.TryCreateCurveSectionEdit(
            curve, curve.Domain.ParameterAt(0.25), curve.Domain.ParameterAt(0.75),
            CurveSectionEditMode.BlendToTerrain, 0.0, 100.0, terrain,
            CurveSectionAnchor.Start, 0.0, SoftEditFalloff.Smooth, measureDeviation: true, 1e-6,
            out Curve? result, out CurveSectionEditResult report, out string? error);

        Assert.True(succeeded, error);
        Assert.NotNull(result);
        using (result)
        {
            Assert.Equal(100.0, report.FirstElevation, 3);
            Assert.Equal(100.0, report.SecondElevation, 3);
        }
    }

    /// <summary>
    /// A polyline's control points are its vertices, and both elevation and station run linearly
    /// between them, so moving Greville points reproduces the asked-for grade exactly. This is the
    /// case that must report nothing, or the warning becomes noise users learn to ignore.
    /// </summary>
    [RhinoNativeFact]
    public void MeasureElevationDeviation_PolylineGrade_ReportsNoDeviation()
    {
        using var curve = new PolylineCurve(new[]
        {
            new Point3d(0, 0, 0),
            new Point3d(40, 0, 0),
            new Point3d(40, 30, 0),
            new Point3d(90, 30, 0)
        });

        bool succeeded = GeometryCommandAlgorithms.TryCreateCurveSectionEdit(
            curve, curve.Domain.T0, curve.Domain.T1, CurveSectionEditMode.GradePercent,
            5.0, 0.0, null, CurveSectionAnchor.Start, 0.0, SoftEditFalloff.Smooth,
            measureDeviation: true, 1e-6,
            out Curve? result, out CurveSectionEditResult report, out string? error);

        Assert.True(succeeded, error);
        using (result)
        {
            Assert.False(report.Deviation.ExceedsTolerance(0.001));
            Assert.Equal(0.0, report.Deviation.MaxDeviation, 6);
        }
    }

    /// <summary>
    /// The case the measurement exists for: a degree-3 curve with four control points, whose plan
    /// stationing is nowhere linear in parameter. The grade is interpolated at the Greville abscissae
    /// and sags in between, and nothing about the curve says so on screen.
    /// </summary>
    [RhinoNativeFact]
    public void MeasureElevationDeviation_CurvedNurbsGrade_ReportsWhereItStrays()
    {
        using NurbsCurve curve = NurbsCurve.Create(
            periodic: false,
            degree: 3,
            new[]
            {
                new Point3d(0, 0, 0),
                new Point3d(30, 40, 0),
                new Point3d(70, -40, 0),
                new Point3d(100, 0, 0)
            });

        bool succeeded = GeometryCommandAlgorithms.TryCreateCurveSectionEdit(
            curve, curve.Domain.T0, curve.Domain.T1, CurveSectionEditMode.GradePercent,
            20.0, 0.0, null, CurveSectionAnchor.Start, 0.0, SoftEditFalloff.Smooth,
            measureDeviation: true, 1e-6,
            out Curve? result, out CurveSectionEditResult report, out string? error);

        Assert.True(succeeded, error);
        using (result)
        {
            Assert.True(report.Deviation.ExceedsTolerance(0.001),
                $"Expected a measurable stray, got {report.Deviation.MaxDeviation}.");
            Assert.InRange(report.Deviation.Station, 0.0, report.PlanLength);
            Assert.True(report.Deviation.SampleCount > 1);
        }
    }

    [RhinoNativeFact]
    public void MeasureElevationDeviation_NotRequested_ReportsNothing()
    {
        using NurbsCurve curve = NurbsCurve.Create(
            periodic: false,
            degree: 3,
            new[]
            {
                new Point3d(0, 0, 0),
                new Point3d(30, 40, 0),
                new Point3d(70, -40, 0),
                new Point3d(100, 0, 0)
            });

        bool succeeded = GeometryCommandAlgorithms.TryCreateCurveSectionEdit(
            curve, curve.Domain.T0, curve.Domain.T1, CurveSectionEditMode.GradePercent,
            20.0, 0.0, null, CurveSectionAnchor.Start, 0.0, SoftEditFalloff.Smooth,
            measureDeviation: false, 1e-6,
            out Curve? result, out CurveSectionEditResult report, out string? error);

        Assert.True(succeeded, error);
        using (result)
        {
            Assert.Equal(0, report.Deviation.SampleCount);
            Assert.False(report.Deviation.ExceedsTolerance(0.001));
        }
    }

    /// <summary>
    /// The measurement is of interpolation error, not of a difference of intent: handed the rule the
    /// curve already satisfies, it must read zero.
    /// </summary>
    [RhinoNativeFact]
    public void MeasureElevationDeviation_CurveAlreadyMatchesTheRule_ReadsZero()
    {
        using var curve = new PolylineCurve(new[]
        {
            new Point3d(0, 0, 5),
            new Point3d(50, 0, 10),
            new Point3d(100, 0, 15)
        });

        GeometryCommandAlgorithms.CurveSlopeDeviation deviation =
            GeometryCommandAlgorithms.MeasureElevationDeviation(curve, station => 5.0 + (station * 0.1));

        Assert.Equal(0.0, deviation.MaxDeviation, 6);
    }

    /// <summary>
    /// Elevation where the curve crosses a given plan position, read by intersecting a vertical plane.
    /// Deliberately not a closest-point query on the plan projection: where the result still carries a
    /// vertical step, that projection has a degenerate segment and the parameter comes back off by
    /// enough to fail a comparison the geometry actually satisfies.
    /// </summary>
    private static double ElevationAtPlanX(Curve curve, double x)
    {
        CurveIntersections crossings = Intersection.CurvePlane(
            curve, new Plane(new Point3d(x, 0, 0), Vector3d.XAxis), 1e-9);

        Assert.NotNull(crossings);
        Assert.Equal(1, crossings.Count);
        return crossings[0].PointA.Z;
    }

    /// <summary>
    /// A vertical step shows up as two elevations at one plan position, so sampling by arc length must
    /// advance in plan at every step.
    /// </summary>
    private static void AssertNoVerticalStep(Curve curve)
    {
        double[] parameters = curve.DivideByCount(200, includeEnds: true);
        Assert.NotNull(parameters);

        double previousX = double.NegativeInfinity;
        foreach (double parameter in parameters)
        {
            Point3d point = curve.PointAt(parameter);
            Assert.True(point.X > previousX,
                $"Plan position stalled at x={point.X}, which means a vertical step.");
            previousX = point.X;
        }
    }

    private static void AssertCurveContainsPoint(Curve curve, Point3d expected)
    {
        Assert.True(curve.ClosestPoint(expected, out double parameter));
        Assert.True(curve.PointAt(parameter).EpsilonEquals(expected, 1e-6),
            $"Expected curve to contain {expected}, closest point was {curve.PointAt(parameter)}.");
    }

    [Fact]
    public void InterpolateGradientElevation_UsesPromilleAndPlanDistance()
    {
        Point3d basePoint = new(0, 0, 100);
        Point3d sample = new(30, 40, 0);

        double elevation = GeometryCommandAlgorithms.InterpolateGradientElevation(basePoint, sample, 100.0);

        Assert.Equal(105.0, elevation, 6);
    }

    [Fact]
    public void CalculateSoftEditPoints_UsesRadiusBasedFalloff()
    {
        var points = new[]
        {
            new Point3d(-10, 0, 0),
            new Point3d(0, 0, 0),
            new Point3d(5, 0, 0),
            new Point3d(10, 0, 0)
        };

        List<Point3d> adjusted = GeometryCommandAlgorithms.CalculateSoftEditPoints(
            points,
            Point3d.Origin,
            10.0,
            new Vector3d(0, 0, 10));

        Assert.Equal(0.0, adjusted[0].Z, 6);
        Assert.Equal(10.0, adjusted[1].Z, 6);
        Assert.InRange(adjusted[2].Z, 4.9, 5.1);
        Assert.Equal(0.0, adjusted[3].Z, 6);
    }

    [Fact]
    public void CalculateSoftEditPoints_LinearFalloff_DoesNotApplySineEasing()
    {
        var points = new[] { new Point3d(2.5, 0, 0) };

        List<Point3d> adjusted = GeometryCommandAlgorithms.CalculateSoftEditPoints(
            points,
            Point3d.Origin,
            10.0,
            new Vector3d(0, 0, 10),
            SoftEditFalloff.Linear);

        Assert.Equal(7.5, adjusted[0].Z, 6);
    }

    [Fact]
    public void CalculatePlanDistance_IgnoresElevationDifference()
    {
        double distance = GeometryCommandAlgorithms.CalculatePlanDistance(
            new Point3d(1, 2, -100),
            new Point3d(4, 6, 500));

        Assert.Equal(5.0, distance, 6);
    }

    [Fact]
    public void BoundingBoxIntersectsPlanRadius_UsesNearestPlanDistance()
    {
        var bounds = new BoundingBox(
            new Point3d(0, 0, 100),
            new Point3d(10, 10, 200));

        Assert.True(GeometryCommandAlgorithms.BoundingBoxIntersectsPlanRadius(
            bounds,
            new Point3d(15, 5, -999),
            6.0));
        Assert.False(GeometryCommandAlgorithms.BoundingBoxIntersectsPlanRadius(
            bounds,
            new Point3d(20, 5, 150),
            6.0));
    }

    [RhinoNativeFact]
    public void TryCreateSoftEditedCurve_NurbsInput_RemainsNonPolylineAndLeavesSourceUntouched()
    {
        NurbsCurve source = NurbsCurve.Create(
            periodic: false,
            degree: 3,
            new[]
            {
                new Point3d(-10, 0, 0),
                new Point3d(-3, 0, 0),
                new Point3d(3, 0, 0),
                new Point3d(10, 0, 0)
            });

        bool succeeded = GeometryCommandAlgorithms.TryCreateSoftEditedCurve(
            source,
            Point3d.Origin,
            10.0,
            new Vector3d(0, 0, 10),
            SoftEditFalloff.Smooth,
            fixEnds: false,
            tolerance: 0.001,
            quickPreview: false,
            out Curve? result,
            out string? error);

        Assert.True(succeeded, error);
        Assert.NotNull(result);
        Assert.IsNotType<PolylineCurve>(result);
        Assert.True(result!.GetBoundingBox(accurate: true).Max.Z > 5.0);
        Assert.Equal(0.0, source.GetBoundingBox(accurate: true).Max.Z, 6);
        result.Dispose();
    }

    [RhinoNativeFact]
    public void TryCreateSoftEditedCurve_FixEnds_PreservesOpenCurveEndpoints()
    {
        NurbsCurve source = NurbsCurve.Create(
            periodic: false,
            degree: 3,
            new[]
            {
                new Point3d(-10, 0, 0),
                new Point3d(-3, 0, 0),
                new Point3d(3, 0, 0),
                new Point3d(10, 0, 0)
            });

        bool succeeded = GeometryCommandAlgorithms.TryCreateSoftEditedCurve(
            source,
            source.PointAtStart,
            15.0,
            new Vector3d(0, 0, 10),
            SoftEditFalloff.Smooth,
            fixEnds: true,
            tolerance: 0.001,
            quickPreview: false,
            out Curve? result,
            out string? error);

        Assert.True(succeeded, error);
        Assert.NotNull(result);
        Assert.True(result!.PointAtStart.EpsilonEquals(source.PointAtStart, 1e-9));
        Assert.True(result.PointAtEnd.EpsilonEquals(source.PointAtEnd, 1e-9));
        Assert.True(result.GetBoundingBox(accurate: true).Max.Z > 0.0);
        result.Dispose();
    }

    [Fact]
    public void CalculateSlopePercentMagnitude_UsesHorizontalRun()
    {
        double percent = GeometryCommandAlgorithms.CalculateSlopePercentMagnitude(new Vector3d(4, 3, 5));

        Assert.Equal(100.0, percent, 6);
    }

    [Fact]
    public void CalculateDefaultLiftFactor_RespectsKnownUnits()
    {
        Assert.Equal(2000.0, GeometryCommandAlgorithms.CalculateDefaultLiftFactor(global::Rhino.UnitSystem.Millimeters));
        Assert.Equal(2.0, GeometryCommandAlgorithms.CalculateDefaultLiftFactor(global::Rhino.UnitSystem.Meters));
        Assert.Equal(6.56167979002625, GeometryCommandAlgorithms.CalculateDefaultLiftFactor(global::Rhino.UnitSystem.Feet), 12);
    }

    [Fact]
    public void ShouldUseSlopePercentageInput_FlatCurve_ReturnsTrue()
    {
        bool usePercentage = GeometryCommandAlgorithms.ShouldUseSlopePercentageInput(100.0, 100.0004, 0.001);

        Assert.True(usePercentage);
    }

    [Fact]
    public void ShouldUseSlopePercentageInput_SlopedCurve_ReturnsFalse()
    {
        bool usePercentage = GeometryCommandAlgorithms.ShouldUseSlopePercentageInput(100.0, 100.01, 0.001);

        Assert.False(usePercentage);
    }

    [Fact]
    public void GetSignedOffsetDistanceForSide_LeftSide_ReturnsNegativeDistance()
    {
        double signedDistance = GeometryCommandAlgorithms.GetSignedOffsetDistanceForSide(true, 2.0);

        Assert.Equal(-2.0, signedDistance, 6);
    }

    [Fact]
    public void GetSignedOffsetDistanceForSide_RightSide_ReturnsPositiveDistance()
    {
        double signedDistance = GeometryCommandAlgorithms.GetSignedOffsetDistanceForSide(false, 2.0);

        Assert.Equal(2.0, signedDistance, 6);
    }

    [Fact]
    public void EaseInOutSine_MapsUnitIntervalSmoothly()
    {
        Assert.Equal(0.0, GeometryCommandAlgorithms.EaseInOutSine(0.0), 6);
        Assert.Equal(0.5, GeometryCommandAlgorithms.EaseInOutSine(0.5), 6);
        Assert.Equal(1.0, GeometryCommandAlgorithms.EaseInOutSine(1.0), 6);
    }

    [Fact]
    public void TryResolveVerticalDelta_Elevation_IgnoresHorizontalDistance()
    {
        bool resolved = GeometryCommandAlgorithms.TryResolveVerticalDelta(
            OffsetVerticalMode.Elevation,
            -0.04,
            2.0,
            out double verticalDelta,
            out string? error);

        Assert.True(resolved);
        Assert.Null(error);
        Assert.Equal(-0.04, verticalDelta, 9);
    }

    [Fact]
    public void TryResolveVerticalDelta_Degrees45_ReturnsHorizontalDistance()
    {
        bool resolved = GeometryCommandAlgorithms.TryResolveVerticalDelta(
            OffsetVerticalMode.Degrees,
            -45.0,
            5.0,
            out double verticalDelta,
            out _);

        Assert.True(resolved);
        Assert.Equal(-5.0, verticalDelta, 9);
    }

    [Fact]
    public void TryResolveVerticalDelta_DegreesAtVertical_ReturnsError()
    {
        bool resolved = GeometryCommandAlgorithms.TryResolveVerticalDelta(
            OffsetVerticalMode.Degrees,
            90.0,
            5.0,
            out _,
            out string? error);

        Assert.False(resolved);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryResolveVerticalDelta_Ratio_OneInTen_ReturnsTenthOfDistance()
    {
        Assert.True(GeometryCommandAlgorithms.TryResolveVerticalDelta(
            OffsetVerticalMode.Ratio, 10.0, 5.0, out double rising, out _));
        Assert.Equal(0.5, rising, 9);

        Assert.True(GeometryCommandAlgorithms.TryResolveVerticalDelta(
            OffsetVerticalMode.Ratio, -10.0, 5.0, out double falling, out _));
        Assert.Equal(-0.5, falling, 9);
    }

    [Fact]
    public void TryResolveVerticalDelta_ZeroRatio_ReturnsError()
    {
        bool resolved = GeometryCommandAlgorithms.TryResolveVerticalDelta(
            OffsetVerticalMode.Ratio,
            0.0,
            5.0,
            out _,
            out string? error);

        Assert.False(resolved);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryResolveVerticalDelta_Percent_MatchesRatio()
    {
        bool resolved = GeometryCommandAlgorithms.TryResolveVerticalDelta(
            OffsetVerticalMode.Percent,
            -4.0,
            2.0,
            out double verticalDelta,
            out _);

        Assert.True(resolved);
        Assert.Equal(-0.08, verticalDelta, 9);
    }

    [Fact]
    public void TryResolveVerticalDelta_UsesOffsetDistanceNotSignedDistance()
    {
        // The offset side must not flip the vertical: a left offset drops just like a right one.
        Assert.True(GeometryCommandAlgorithms.TryResolveVerticalDelta(
            OffsetVerticalMode.Ratio, -10.0, -5.0, out double leftSide, out _));

        Assert.Equal(-0.5, leftSide, 9);
    }

    [RhinoNativeFact]
    public void TryGetOffsetFeaturePolyline_AppliesVerticalDeltaToEveryVertex()
    {
        // A sloped, kinked feature line so the source's own longitudinal grade is exercised.
        var source = new PolylineCurve(new[]
        {
            new Point3d(0, 0, 10),
            new Point3d(10, 0, 11),
            new Point3d(20, 10, 12)
        });

        bool offsetSucceeded = GeometryCommandAlgorithms.TryGetOffsetFeaturePolyline(
            source,
            2.0,
            -0.04,
            0.001,
            out Polyline offset,
            out string? error);

        Assert.True(offsetSucceeded, error);
        Assert.True(offset.Count >= 2);

        Curve flat = Curve.ProjectToPlane(source, Plane.WorldXY);
        foreach (Point3d point in offset)
        {
            Assert.True(flat.ClosestPoint(point, out double parameter));
            Assert.Equal(source.PointAt(parameter).Z - 0.04, point.Z, 6);
        }

        source.Dispose();
    }

    [RhinoNativeFact]
    public void TryGetOffsetFeaturePolyline_ZeroVerticalDelta_PreservesSourceElevations()
    {
        var source = new PolylineCurve(new[]
        {
            new Point3d(0, 0, 5),
            new Point3d(10, 0, 7)
        });

        Assert.True(GeometryCommandAlgorithms.TryGetOffsetFeaturePolyline(
            source, 3.0, 0.0, 0.001, out Polyline offset, out string? error), error);

        Assert.Equal(5.0, offset[0].Z, 6);
        Assert.Equal(7.0, offset[^1].Z, 6);

        source.Dispose();
    }
}
