using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

internal readonly record struct TerrainSectionVertex(double Station, Point3d World);

internal sealed class TerrainSectionSegment
{
    public TerrainSectionSegment(IReadOnlyList<TerrainSectionVertex> vertices)
    {
        Vertices = vertices;
    }

    public IReadOnlyList<TerrainSectionVertex> Vertices { get; }

    public double StartStation => Vertices.Count > 0 ? Vertices[0].Station : 0.0;

    public double EndStation => Vertices.Count > 0 ? Vertices[Vertices.Count - 1].Station : 0.0;
}

internal sealed class TerrainSectionResult
{
    public TerrainSectionResult(
        IReadOnlyList<TerrainSectionSegment> segments,
        double totalStationLength,
        double minimumElevation,
        double maximumElevation)
    {
        Segments = segments;
        TotalStationLength = totalStationLength;
        MinimumElevation = minimumElevation;
        MaximumElevation = maximumElevation;
    }

    public IReadOnlyList<TerrainSectionSegment> Segments { get; }

    public double TotalStationLength { get; }

    public double MinimumElevation { get; }

    public double MaximumElevation { get; }

    public bool IsEmpty => Segments.Count == 0;

    public static TerrainSectionResult Empty { get; } =
        new(Array.Empty<TerrainSectionSegment>(), 0.0, 0.0, 0.0);
}

internal static class TerrainSectionSlicer
{
    public static TerrainSectionResult SliceAlongPolyline(
        RhinoMesh mesh,
        IReadOnlyList<Point3d> cutPolylineVertices,
        double tolerance)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(cutPolylineVertices);
        if (cutPolylineVertices.Count < 2)
            return TerrainSectionResult.Empty;

        var perSegmentIntersections = new List<Polyline[]?>(cutPolylineVertices.Count - 1);
        for (int i = 1; i < cutPolylineVertices.Count; i++)
        {
            Point3d a = cutPolylineVertices[i - 1];
            Point3d b = cutPolylineVertices[i];
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double segmentLength = Math.Sqrt((dx * dx) + (dy * dy));
            if (segmentLength <= Math.Max(tolerance, 1e-9))
            {
                perSegmentIntersections.Add(null);
                continue;
            }

            var direction = new Vector3d(dx / segmentLength, dy / segmentLength, 0.0);
            var origin = new Point3d(a.X, a.Y, 0.0);
            var plane = new Plane(origin, direction, Vector3d.ZAxis);
            perSegmentIntersections.Add(Intersection.MeshPlane(mesh, plane));
        }

        return SliceFromPolylineIntersections(cutPolylineVertices, perSegmentIntersections, tolerance);
    }

    internal static TerrainSectionResult SliceFromPolylineIntersections(
        IReadOnlyList<Point3d> cutPolylineVertices,
        IReadOnlyList<Polyline[]?> perSegmentIntersections,
        double tolerance)
    {
        ArgumentNullException.ThrowIfNull(cutPolylineVertices);
        ArgumentNullException.ThrowIfNull(perSegmentIntersections);
        if (cutPolylineVertices.Count < 2)
            return TerrainSectionResult.Empty;

        double clampTol = Math.Max(tolerance, 1e-9);
        var rawSegments = new List<TerrainSectionSegment>();
        double cumulativeLength = 0.0;
        double minZ = double.PositiveInfinity;
        double maxZ = double.NegativeInfinity;

        int segmentIndex = 0;
        for (int i = 1; i < cutPolylineVertices.Count; i++, segmentIndex++)
        {
            Point3d a = cutPolylineVertices[i - 1];
            Point3d b = cutPolylineVertices[i];
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double segmentLength = Math.Sqrt((dx * dx) + (dy * dy));
            if (segmentLength <= clampTol)
                continue;

            var direction = new Vector3d(dx / segmentLength, dy / segmentLength, 0.0);
            var origin = new Point3d(a.X, a.Y, 0.0);

            Polyline[]? polylines = segmentIndex < perSegmentIntersections.Count
                ? perSegmentIntersections[segmentIndex]
                : null;

            if (polylines is { Length: > 0 })
            {
                AppendClippedSegments(
                    polylines,
                    origin,
                    direction,
                    segmentLength,
                    cumulativeLength,
                    clampTol,
                    rawSegments,
                    ref minZ,
                    ref maxZ);
            }

            cumulativeLength += segmentLength;
        }

        var stitched = StitchAdjacentSegments(rawSegments, clampTol);
        if (double.IsPositiveInfinity(minZ))
        {
            minZ = 0.0;
            maxZ = 0.0;
        }

        return new TerrainSectionResult(stitched, cumulativeLength, minZ, maxZ);
    }

    public static TerrainSectionResult SampleAlongCurve(
        RhinoMesh mesh,
        Curve curve,
        double sampleSpacing,
        double tolerance)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(curve);

        double clampTol = Math.Max(tolerance, 1e-9);
        double spacing = Math.Max(sampleSpacing, clampTol * 10.0);

        BoundingBox bounds = mesh.GetBoundingBox(true);
        if (!bounds.IsValid)
            return TerrainSectionResult.Empty;

        double zSpan = Math.Max(bounds.Max.Z - bounds.Min.Z, 1.0);
        double rayStartZ = bounds.Max.Z + zSpan;

        var parameters = new List<double> { curve.Domain.T0 };
        if (curve.DivideByLength(spacing, true) is { Length: > 0 } divisions)
            parameters.AddRange(divisions);
        if (Math.Abs(parameters[parameters.Count - 1] - curve.Domain.T1) > clampTol)
            parameters.Add(curve.Domain.T1);

        var resultSegments = new List<TerrainSectionSegment>();
        var current = new List<TerrainSectionVertex>();
        double totalLength = 0.0;
        double minZ = double.PositiveInfinity;
        double maxZ = double.NegativeInfinity;
        Point3d? previousXY = null;

        for (int i = 0; i < parameters.Count; i++)
        {
            Point3d sample = curve.PointAt(parameters[i]);
            if (previousXY.HasValue)
            {
                double dx = sample.X - previousXY.Value.X;
                double dy = sample.Y - previousXY.Value.Y;
                totalLength += Math.Sqrt((dx * dx) + (dy * dy));
            }
            previousXY = sample;

            var ray = new Ray3d(new Point3d(sample.X, sample.Y, rayStartZ), -Vector3d.ZAxis);
            double rayDistance = Intersection.MeshRay(mesh, ray);
            if (rayDistance < 0.0)
            {
                if (current.Count > 1)
                    resultSegments.Add(new TerrainSectionSegment(current.ToArray()));
                current = new List<TerrainSectionVertex>();
                continue;
            }

            Point3d hit = ray.PointAt(rayDistance);
            if (hit.Z < minZ) minZ = hit.Z;
            if (hit.Z > maxZ) maxZ = hit.Z;
            current.Add(new TerrainSectionVertex(totalLength, hit));
        }

        if (current.Count > 1)
            resultSegments.Add(new TerrainSectionSegment(current.ToArray()));

        if (double.IsPositiveInfinity(minZ))
        {
            minZ = 0.0;
            maxZ = 0.0;
        }

        return new TerrainSectionResult(resultSegments, totalLength, minZ, maxZ);
    }

    private static void AppendClippedSegments(
        Polyline[] polylines,
        Point3d segmentOrigin,
        Vector3d direction,
        double segmentLength,
        double stationOffset,
        double tolerance,
        List<TerrainSectionSegment> output,
        ref double minZ,
        ref double maxZ)
    {
        foreach (Polyline poly in polylines)
        {
            if (poly == null || poly.Count < 2)
                continue;

            var current = new List<TerrainSectionVertex>();

            for (int i = 0; i < poly.Count - 1; i++)
            {
                Point3d a = poly[i];
                Point3d b = poly[i + 1];
                double ua = ((a.X - segmentOrigin.X) * direction.X) + ((a.Y - segmentOrigin.Y) * direction.Y);
                double ub = ((b.X - segmentOrigin.X) * direction.X) + ((b.Y - segmentOrigin.Y) * direction.Y);

                bool aIn = ua >= -tolerance && ua <= segmentLength + tolerance;
                bool bIn = ub >= -tolerance && ub <= segmentLength + tolerance;
                bool straddles = !aIn && !bIn && ((ua < -tolerance && ub > segmentLength + tolerance) ||
                                                  (ub < -tolerance && ua > segmentLength + tolerance));

                if (!aIn && !bIn && !straddles)
                {
                    FlushIfReady(output, ref current);
                    continue;
                }

                Point3d clippedA = a;
                Point3d clippedB = b;
                double uClippedA = ua;
                double uClippedB = ub;

                if (!aIn)
                {
                    double targetU = ua < 0 ? 0.0 : segmentLength;
                    double t = (targetU - ua) / (ub - ua);
                    clippedA = new Point3d(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t), a.Z + ((b.Z - a.Z) * t));
                    uClippedA = targetU;
                }

                if (!bIn)
                {
                    double targetU = ub < 0 ? 0.0 : segmentLength;
                    double t = (targetU - ua) / (ub - ua);
                    clippedB = new Point3d(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t), a.Z + ((b.Z - a.Z) * t));
                    uClippedB = targetU;
                }

                double aStation = stationOffset + Math.Clamp(uClippedA, 0.0, segmentLength);
                double bStation = stationOffset + Math.Clamp(uClippedB, 0.0, segmentLength);

                if (current.Count == 0)
                {
                    current.Add(new TerrainSectionVertex(aStation, clippedA));
                }
                else
                {
                    var last = current[current.Count - 1];
                    double gap = last.World.DistanceTo(clippedA);
                    if (gap > tolerance)
                    {
                        FlushIfReady(output, ref current);
                        current.Add(new TerrainSectionVertex(aStation, clippedA));
                    }
                }

                current.Add(new TerrainSectionVertex(bStation, clippedB));

                if (clippedA.Z < minZ) minZ = clippedA.Z;
                if (clippedA.Z > maxZ) maxZ = clippedA.Z;
                if (clippedB.Z < minZ) minZ = clippedB.Z;
                if (clippedB.Z > maxZ) maxZ = clippedB.Z;

                if (!bIn)
                    FlushIfReady(output, ref current);
            }

            FlushIfReady(output, ref current);
        }
    }

    private static void FlushIfReady(List<TerrainSectionSegment> output, ref List<TerrainSectionVertex> buffer)
    {
        if (buffer.Count > 1)
            output.Add(new TerrainSectionSegment(buffer.ToArray()));
        buffer = new List<TerrainSectionVertex>();
    }

    private static IReadOnlyList<TerrainSectionSegment> StitchAdjacentSegments(
        List<TerrainSectionSegment> input,
        double tolerance)
    {
        if (input.Count <= 1)
            return input;

        input.Sort((x, y) => x.StartStation.CompareTo(y.StartStation));

        var result = new List<TerrainSectionSegment>();
        var current = new List<TerrainSectionVertex>(input[0].Vertices);

        for (int i = 1; i < input.Count; i++)
        {
            var next = input[i].Vertices;
            var lastCurrent = current[current.Count - 1];
            var firstNext = next[0];
            if (lastCurrent.World.DistanceTo(firstNext.World) <= tolerance)
            {
                for (int j = 1; j < next.Count; j++)
                    current.Add(next[j]);
            }
            else
            {
                if (current.Count > 1)
                    result.Add(new TerrainSectionSegment(current.ToArray()));
                current = new List<TerrainSectionVertex>(next);
            }
        }

        if (current.Count > 1)
            result.Add(new TerrainSectionSegment(current.ToArray()));

        return result;
    }
}
