namespace MoleHill.Core.Processing;

/// <summary>
/// Light preprocessing for terrain contour and breakline polylines before TIN build.
/// Preserves long meaningful straight runs by resampling them, while also splitting
/// very long uneven segments to reduce fan triangulation between sparse inputs.
/// </summary>
public static class TerrainConstraintPreprocessor
{
    private readonly record struct Vertex(double X, double Y, double Z);

    public static List<double[]> Process(
        IReadOnlyList<double[]> breaklinePolylines,
        IReadOnlyList<double[]> contourPolylines,
        double tolerance)
    {
        // Collect and sort each source class once. Multi-million-station surveys used to scan and sort
        // the populated class twice while deriving the combined and class-specific spacing values.
        List<double> breaklineLengths = CollectSegmentLengths(breaklinePolylines, tolerance);
        List<double> contourLengths = CollectSegmentLengths(contourPolylines, tolerance);
        double? measuredBreaklineSpacing = breaklineLengths.Count >= 3
            ? ComputeClampedMedian(breaklineLengths, tolerance)
            : null;
        double? measuredContourSpacing = contourLengths.Count >= 3
            ? ComputeClampedMedian(contourLengths, tolerance)
            : null;
        double combinedSpacing = measuredBreaklineSpacing ??
            measuredContourSpacing ??
            ComputeClampedMedian(contourLengths, tolerance);
        double breaklineSpacing = measuredBreaklineSpacing ?? combinedSpacing;
        double contourSpacing = measuredContourSpacing ?? combinedSpacing;

        var result = new List<double[]>(breaklinePolylines.Count + contourPolylines.Count);
        result.AddRange(ProcessPolylines(breaklinePolylines, breaklineSpacing, tolerance));
        result.AddRange(ProcessPolylines(contourPolylines, contourSpacing, tolerance));
        return result;
    }

    private static IEnumerable<double[]> ProcessPolylines(
        IReadOnlyList<double[]> polylines,
        double targetSpacing,
        double tolerance)
    {
        for (int i = 0; i < polylines.Count; i++)
        {
            double[] polyline = polylines[i];
            if (!TryExtractVertices(polyline, tolerance, out List<Vertex> vertices))
                continue;

            double[] processed = ResamplePolyline(vertices, targetSpacing, tolerance);
            if (processed.Length >= 6)
                yield return processed;
        }
    }

    private static bool TryExtractVertices(double[] polyline, double tolerance, out List<Vertex> vertices)
    {
        vertices = new List<Vertex>(polyline.Length / 3);
        if (polyline.Length < 6)
            return false;

        double tolSq = tolerance * tolerance;
        for (int i = 0; i < polyline.Length / 3; i++)
        {
            double x = polyline[i * 3];
            double y = polyline[i * 3 + 1];
            double z = polyline[i * 3 + 2];
            if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
                continue;

            if (vertices.Count > 0)
            {
                Vertex previous = vertices[^1];
                double dx = x - previous.X;
                double dy = y - previous.Y;
                if ((dx * dx) + (dy * dy) <= tolSq && Math.Abs(z - previous.Z) <= tolerance)
                    continue;
            }

            vertices.Add(new Vertex(x, y, z));
        }

        return vertices.Count >= 2;
    }

    private static double[] ResamplePolyline(List<Vertex> vertices, double targetSpacing, double tolerance)
    {
        double preserveThreshold = Math.Max(targetSpacing * 3.0, tolerance * 24.0);
        double densifyThreshold = Math.Max(targetSpacing * 2.5, tolerance * 16.0);
        double lineTolerance = Math.Max(tolerance * 0.25, 1e-9);
        var output = new List<double>(vertices.Count * 3);

        AddVertex(output, vertices[0]);

        int i = 0;
        while (i < vertices.Count - 1)
        {
            int runEnd = FindCollinearRunEnd(vertices, i, lineTolerance, tolerance);
            double runLength = Distance(vertices[i], vertices[runEnd]);

            if (runEnd > i + 1 && runLength >= preserveThreshold)
            {
                AppendResampledRun(output, vertices, i, runEnd, targetSpacing, tolerance);
                i = runEnd;
                continue;
            }

            double segmentLength = Distance(vertices[i], vertices[i + 1]);
            if (segmentLength >= densifyThreshold)
                AppendResampledSegment(output, vertices[i], vertices[i + 1], targetSpacing, tolerance);
            else
                AddVertex(output, vertices[i + 1], tolerance);

            i++;
        }

        return output.ToArray();
    }

    private static int FindCollinearRunEnd(List<Vertex> vertices, int startIndex, double lineTolerance, double zTolerance)
    {
        int best = startIndex + 1;
        int seedEndIndex = startIndex + 2;
        if (seedEndIndex >= vertices.Count ||
            !TryGetCollinearParameter(
                vertices[startIndex],
                vertices[startIndex + 1],
                vertices[seedEndIndex],
                lineTolerance,
                zTolerance,
                out _))
        {
            return best;
        }

        // Anchor the accepted run to its first three stations. The former implementation moved the
        // end point one station at a time and rechecked every earlier station, making a straight run
        // O(n^2). Once the first three stations define a line, every later station only needs one
        // projection/cross-track/Z check against that stable line, so the run scan is O(n).
        Vertex start = vertices[startIndex];
        Vertex seedEnd = vertices[seedEndIndex];
        double dx = seedEnd.X - start.X;
        double dy = seedEnd.Y - start.Y;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length <= lineTolerance)
            return best;

        double unitX = dx / length;
        double unitY = dy / length;
        double zPerUnit = (seedEnd.Z - start.Z) / length;
        double previousProjection = length;
        double lineToleranceSq = lineTolerance * lineTolerance;
        best = seedEndIndex;

        for (int end = seedEndIndex + 1; end < vertices.Count; end++)
        {
            Vertex candidate = vertices[end];
            double offsetX = candidate.X - start.X;
            double offsetY = candidate.Y - start.Y;
            double projection = (offsetX * unitX) + (offsetY * unitY);
            double orderEpsilon = Math.Max(1e-12, Math.Abs(projection) * 1e-6);

            // Preserve source station order; a backtracking station ends the straight run.
            if (projection <= previousProjection + orderEpsilon)
                break;

            double crossTrackX = offsetX - (projection * unitX);
            double crossTrackY = offsetY - (projection * unitY);
            if ((crossTrackX * crossTrackX) + (crossTrackY * crossTrackY) > lineToleranceSq)
                break;

            double expectedZ = start.Z + (projection * zPerUnit);
            if (Math.Abs(candidate.Z - expectedZ) > zTolerance)
                break;

            previousProjection = projection;
            best = end;
        }

        return best;
    }

    private static bool TryGetCollinearParameter(
        Vertex start,
        Vertex middle,
        Vertex end,
        double lineTolerance,
        double zTolerance,
        out double t)
    {
        t = 0.0;
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double lenSq = (dx * dx) + (dy * dy);
        if (lenSq <= lineTolerance * lineTolerance)
            return false;

        t = (((middle.X - start.X) * dx) + ((middle.Y - start.Y) * dy)) / lenSq;
        if (t <= 1e-6 || t >= 1.0 - 1e-6)
            return false;

        double projX = start.X + (t * dx);
        double projY = start.Y + (t * dy);
        double distSq = ((middle.X - projX) * (middle.X - projX)) + ((middle.Y - projY) * (middle.Y - projY));
        if (distSq > lineTolerance * lineTolerance)
            return false;

        double expectedZ = start.Z + (t * (end.Z - start.Z));
        return Math.Abs(middle.Z - expectedZ) <= zTolerance;
    }

    private static void AppendResampledSegment(List<double> output, Vertex start, Vertex end, double targetSpacing, double tolerance)
    {
        double length = Distance(start, end);
        if (length <= tolerance || targetSpacing <= tolerance)
        {
            AddVertex(output, end, tolerance);
            return;
        }

        int divisions = Math.Max(1, (int)Math.Ceiling(length / targetSpacing));
        for (int step = 1; step <= divisions; step++)
        {
            double t = step / (double)divisions;
            AddVertex(output, new Vertex(
                Lerp(start.X, end.X, t),
                Lerp(start.Y, end.Y, t),
                Lerp(start.Z, end.Z, t)), tolerance);
        }
    }

    private static void AppendResampledRun(
        List<double> output,
        List<Vertex> vertices,
        int startIndex,
        int endIndex,
        double targetSpacing,
        double tolerance)
    {
        if (endIndex <= startIndex)
            return;

        int runVertexCount = endIndex - startIndex + 1;
        var cumulativeLengths = new double[runVertexCount];
        double totalLength = 0.0;
        for (int i = 1; i < runVertexCount; i++)
        {
            totalLength += Distance(vertices[startIndex + i - 1], vertices[startIndex + i]);
            cumulativeLengths[i] = totalLength;
        }

        if (totalLength <= tolerance || targetSpacing <= tolerance)
        {
            AddVertex(output, vertices[endIndex], tolerance);
            return;
        }

        int divisions = Math.Max(1, (int)Math.Ceiling(totalLength / targetSpacing));
        int segmentIndex = 0;
        for (int step = 1; step <= divisions; step++)
        {
            double targetDistance = step / (double)divisions * totalLength;
            while (segmentIndex < runVertexCount - 2 && cumulativeLengths[segmentIndex + 1] < targetDistance)
                segmentIndex++;

            Vertex segmentStart = vertices[startIndex + segmentIndex];
            Vertex segmentEnd = vertices[startIndex + segmentIndex + 1];
            double segmentStartDistance = cumulativeLengths[segmentIndex];
            double segmentLength = cumulativeLengths[segmentIndex + 1] - segmentStartDistance;

            if (segmentLength <= tolerance)
            {
                AddVertex(output, segmentEnd, tolerance);
                continue;
            }

            double localT = (targetDistance - segmentStartDistance) / segmentLength;
            AddVertex(output, new Vertex(
                Lerp(segmentStart.X, segmentEnd.X, localT),
                Lerp(segmentStart.Y, segmentEnd.Y, localT),
                Lerp(segmentStart.Z, segmentEnd.Z, localT)), tolerance);
        }
    }

    private static void AddVertex(List<double> output, Vertex vertex, double tolerance = 0.0)
    {
        if (output.Count >= 3)
        {
            double dx = vertex.X - output[^3];
            double dy = vertex.Y - output[^2];
            if ((dx * dx) + (dy * dy) <= tolerance * tolerance && Math.Abs(vertex.Z - output[^1]) <= tolerance)
                return;
        }

        output.Add(vertex.X);
        output.Add(vertex.Y);
        output.Add(vertex.Z);
    }

    private static List<double> CollectSegmentLengths(IReadOnlyList<double[]> polylines, double tolerance)
    {
        var lengths = new List<double>();
        double minLength = Math.Max(tolerance * 2.0, 1e-9);
        for (int i = 0; i < polylines.Count; i++)
        {
            double[] polyline = polylines[i];
            for (int pointIndex = 1; pointIndex < polyline.Length / 3; pointIndex++)
            {
                double dx = polyline[pointIndex * 3] - polyline[(pointIndex - 1) * 3];
                double dy = polyline[pointIndex * 3 + 1] - polyline[(pointIndex - 1) * 3 + 1];
                double length = Math.Sqrt((dx * dx) + (dy * dy));
                if (length >= minLength)
                    lengths.Add(length);
            }
        }

        return lengths;
    }

    private static double ComputeClampedMedian(List<double> lengths, double tolerance)
    {
        if (lengths.Count == 0)
            return Math.Max(tolerance * 32.0, double.Epsilon);

        lengths.Sort();
        int middle = lengths.Count / 2;
        double median = lengths.Count % 2 == 0
            ? (lengths[middle - 1] + lengths[middle]) * 0.5
            : lengths[middle];
        // Once the source supplies enough samples, its median segment length is the meaningful terrain
        // scale. Capping it at tolerance * 200 made a 0.0125 model tolerance force 2.5-unit stations
        // across kilometre-scale contour datasets, multiplying otherwise usable inputs by 8x or more.
        // Keep only the lower guard against microscopic/noisy source segments; isolated sparse lines
        // still use the explicit fallback above.
        return Math.Max(median, tolerance * 8.0);
    }

    private static double Distance(Vertex a, Vertex b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static double Lerp(double a, double b, double t) => a + ((b - a) * t);
}
