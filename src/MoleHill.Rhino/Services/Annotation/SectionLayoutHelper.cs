using Rhino.DocObjects;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

internal static class SectionLayoutHelper
{
    public static Point3d ProjectToInsertionPlane(
        Plane insertionPlane,
        double station,
        double elevation,
        double horizontalScale,
        double verticalScale,
        double baseElevation)
    {
        return insertionPlane.Origin
            + (insertionPlane.XAxis * (station * horizontalScale))
            + (insertionPlane.YAxis * ((elevation - baseElevation) * verticalScale));
    }

    public static Polyline LayoutFlat(
        TerrainSectionSegment segment,
        Plane insertionPlane,
        double horizontalScale,
        double verticalScale,
        double baseElevation)
    {
        ArgumentNullException.ThrowIfNull(segment);
        var polyline = new Polyline(segment.Vertices.Count);
        for (int i = 0; i < segment.Vertices.Count; i++)
        {
            var vertex = segment.Vertices[i];
            polyline.Add(ProjectToInsertionPlane(
                insertionPlane,
                vertex.Station,
                vertex.World.Z,
                horizontalScale,
                verticalScale,
                baseElevation));
        }
        return polyline;
    }

    public static IReadOnlyList<Polyline> LayoutFlatAll(
        TerrainSectionResult result,
        Plane insertionPlane,
        double horizontalScale,
        double verticalScale,
        double baseElevation)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.IsEmpty)
            return Array.Empty<Polyline>();

        var polylines = new List<Polyline>(result.Segments.Count);
        for (int i = 0; i < result.Segments.Count; i++)
        {
            polylines.Add(LayoutFlat(
                result.Segments[i],
                insertionPlane,
                horizontalScale,
                verticalScale,
                baseElevation));
        }
        return polylines;
    }

    public static Line BuildBaselineAxis(
        Plane insertionPlane,
        double totalStation,
        double horizontalScale,
        double verticalScale,
        double axisElevation,
        double baseElevation)
    {
        Point3d a = ProjectToInsertionPlane(insertionPlane, 0.0, axisElevation, horizontalScale, verticalScale, baseElevation);
        Point3d b = ProjectToInsertionPlane(insertionPlane, totalStation, axisElevation, horizontalScale, verticalScale, baseElevation);
        return new Line(a, b);
    }

    public static Line BuildElevationAxis(
        Plane insertionPlane,
        double minimumElevation,
        double maximumElevation,
        double horizontalScale,
        double verticalScale,
        double baseElevation)
    {
        Point3d a = ProjectToInsertionPlane(insertionPlane, 0.0, minimumElevation, horizontalScale, verticalScale, baseElevation);
        Point3d b = ProjectToInsertionPlane(insertionPlane, 0.0, maximumElevation, horizontalScale, verticalScale, baseElevation);
        return new Line(a, b);
    }

    public static IReadOnlyList<Line> BuildElevationGridLines(
        Plane insertionPlane,
        double totalStation,
        double minimumElevation,
        double maximumElevation,
        double baseElevation,
        double gridSpacing,
        double horizontalScale,
        double verticalScale)
    {
        if (gridSpacing <= 0.0 || totalStation <= 0.0 || maximumElevation < minimumElevation)
            return Array.Empty<Line>();

        var lines = new List<Line>();
        foreach (double e in ElevationSteps(minimumElevation, maximumElevation, gridSpacing))
        {
            Point3d a = ProjectToInsertionPlane(insertionPlane, 0.0, e, horizontalScale, verticalScale, baseElevation);
            Point3d b = ProjectToInsertionPlane(insertionPlane, totalStation, e, horizontalScale, verticalScale, baseElevation);
            lines.Add(new Line(a, b));
        }

        return lines;
    }

    /// <summary>
    /// Whole multiples of <paramref name="spacing"/> from the one at or below the minimum to the one at or
    /// above the maximum. Each is computed from its index rather than accumulated, so a 0.1 step lands on
    /// 12.3, not 12.299999999999.
    /// </summary>
    public static IReadOnlyList<double> ElevationSteps(double minimumElevation, double maximumElevation, double spacing)
    {
        if (!(spacing > 0.0) || maximumElevation < minimumElevation ||
            !double.IsFinite(minimumElevation) || !double.IsFinite(maximumElevation))
            return Array.Empty<double>();

        double first = Math.Floor(minimumElevation / spacing);
        double last = Math.Ceiling(maximumElevation / spacing);

        // A spacing far too fine for the range would draw thousands of lines; such a grid is unreadable.
        if (last - first > 1000)
            return Array.Empty<double>();

        // 123 * 0.1 is 12.300000000000001; 123 / 10 is 12.3. Divide when the step is a whole fraction.
        double inverse = Math.Round(1.0 / spacing);
        bool divide = inverse >= 1.0 && Math.Abs((1.0 / spacing) - inverse) <= inverse * 1e-9;

        var steps = new List<double>((int)(last - first) + 1);
        for (double k = first; k <= last; k++)
            steps.Add(divide ? k / inverse : k * spacing);
        return steps;
    }

    /// <summary>
    /// Which grid steps carry an elevation figure: every one when a step is at least 1.8 text heights tall
    /// on the drawing, otherwise every 2nd, 5th, 10th, 20th... — the same 1/2/5 rhythm as the grid, so the
    /// labelled values stay round.
    /// </summary>
    public static int ElevationLabelStride(double drawnStepHeight, double textHeight)
    {
        if (!(drawnStepHeight > 0.0) || !(textHeight > 0.0))
            return 1;

        double needed = 1.8 * textHeight / drawnStepHeight;
        for (int decade = 1; decade <= 1_000_000; decade *= 10)
        {
            foreach (int factor in new[] { 1, 2, 5 })
            {
                if (factor * decade >= needed)
                    return factor * decade;
            }
        }

        return 1_000_000;
    }

    /// <summary>
    /// A fixed-point format with just enough decimals to show every step of the grid exactly: 1 or 5 gives
    /// whole numbers, 0.5 one decimal, 0.25 two. At most three.
    /// </summary>
    public static string ElevationLabelFormat(double spacing)
    {
        int decimals = 0;
        double scaled = Math.Abs(spacing);
        while (decimals < 3 && Math.Abs(scaled - Math.Round(scaled)) > 1e-6 * Math.Max(scaled, 1.0))
        {
            scaled *= 10.0;
            decimals++;
        }

        return "F" + decimals;
    }

    public static double ResolveElevationGridSpacing(
        double minimumElevation,
        double maximumElevation,
        double requestedSpacing)
    {
        if (requestedSpacing > 0.0)
            return requestedSpacing;

        double range = maximumElevation - minimumElevation;
        if (!double.IsFinite(range) || range < 0.0)
            return 0.0;

        // A level section (a pad, a platform) still needs its one elevation labelled: take a step a
        // hundredth of the elevation's own size, so 20 m reads 20, 20.2 and so on. Zero labels as whole metres.
        if (range == 0.0)
        {
            double size = Math.Abs(minimumElevation);
            return size > 0.0 && double.IsFinite(size)
                ? Math.Pow(10.0, Math.Floor(Math.Log10(size)) - 1.0)
                : 1.0;
        }

        // Aim for about five grid intervals, then round to a familiar 1/2/5 step so labels remain
        // readable instead of producing a dense forest of arbitrary decimal lines.
        double raw = range / 5.0;
        double magnitude = Math.Pow(10.0, Math.Floor(Math.Log10(raw)));
        double normalized = raw / magnitude;
        double step = normalized <= 1.0 ? 1.0 : normalized <= 2.0 ? 2.0 : normalized <= 5.0 ? 5.0 : 10.0;
        return step * magnitude;
    }

    public static IReadOnlyList<Line> BuildStationTicks(
        Plane insertionPlane,
        IReadOnlyList<double> stations,
        double tickHalfHeight,
        double horizontalScale,
        double verticalScale,
        double baseElevation,
        double tickElevation)
    {
        ArgumentNullException.ThrowIfNull(stations);
        if (stations.Count == 0 || tickHalfHeight <= 0.0)
            return Array.Empty<Line>();

        var lines = new List<Line>(stations.Count);
        foreach (double station in stations)
        {
            Point3d top = ProjectToInsertionPlane(insertionPlane, station, tickElevation + tickHalfHeight, horizontalScale, verticalScale, baseElevation);
            Point3d bottom = ProjectToInsertionPlane(insertionPlane, station, tickElevation - tickHalfHeight, horizontalScale, verticalScale, baseElevation);
            lines.Add(new Line(bottom, top));
        }
        return lines;
    }

    public static TextEntity BuildLabel(
        Plane insertionPlane,
        double station,
        double elevation,
        double horizontalScale,
        double verticalScale,
        double baseElevation,
        string text,
        double textHeight,
        TextJustification justification = TextJustification.MiddleCenter)
    {
        Point3d origin = ProjectToInsertionPlane(insertionPlane, station, elevation, horizontalScale, verticalScale, baseElevation);
        return new TextEntity
        {
            Plane = new Plane(origin, insertionPlane.XAxis, insertionPlane.YAxis),
            PlainText = text ?? string.Empty,
            TextHeight = textHeight,
            Justification = justification,
            MaskFrame = DimensionStyle.MaskFrame.NoFrame
        };
    }

    public static Plane FrameFromCurveTangent(Point3d origin, Vector3d tangent)
    {
        Vector3d xAxis = tangent;
        xAxis.Z = 0.0;
        if (!xAxis.Unitize())
            xAxis = Vector3d.XAxis;

        Vector3d yAxis = Vector3d.CrossProduct(Vector3d.ZAxis, xAxis);
        if (!yAxis.Unitize())
            yAxis = Vector3d.YAxis;

        return new Plane(origin, xAxis, yAxis);
    }
}
