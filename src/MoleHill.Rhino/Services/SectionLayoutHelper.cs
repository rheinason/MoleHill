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
        double startElevation = Math.Floor(minimumElevation / gridSpacing) * gridSpacing;
        double endElevation = Math.Ceiling(maximumElevation / gridSpacing) * gridSpacing;

        for (double e = startElevation; e <= endElevation + (gridSpacing * 0.5); e += gridSpacing)
        {
            Point3d a = ProjectToInsertionPlane(insertionPlane, 0.0, e, horizontalScale, verticalScale, baseElevation);
            Point3d b = ProjectToInsertionPlane(insertionPlane, totalStation, e, horizontalScale, verticalScale, baseElevation);
            lines.Add(new Line(a, b));
        }

        return lines;
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
