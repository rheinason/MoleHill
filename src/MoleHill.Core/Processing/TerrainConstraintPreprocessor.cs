namespace MoleHill.Core.Processing;

/// <summary>
/// Light preprocessing for terrain contour and breakline polylines before TIN build.
/// Preserves long meaningful straight runs by resampling them, while also splitting
/// very long uneven segments to reduce fan triangulation between sparse inputs.
/// </summary>
public static class TerrainConstraintPreprocessor
{
    private readonly record struct Vertex(double X, double Y, double Z);

    /// <param name="samplePoints">
    /// Optional flat XYZ spot samples (points, DEM samples, vertices-only contours). They take no part in
    /// the output, but a breakline next to them is stationed to their density.
    /// </param>
    public static List<double[]> Process(
        IReadOnlyList<double[]> breaklinePolylines,
        IReadOnlyList<double[]> contourPolylines,
        double tolerance,
        double[]? samplePoints = null)
    {
        Result processed = ProcessSeparately(breaklinePolylines, contourPolylines, tolerance, samplePoints);
        var result = new List<double[]>(processed.Breaklines.Count + processed.Contours.Count);
        result.AddRange(processed.Breaklines.Where(static polyline => polyline != null)!);
        result.AddRange(processed.Contours.Where(static polyline => polyline != null)!);
        return result;
    }

    /// <summary>
    /// Processed polylines by source class, each list aligned with its input: entry <c>i</c> is input
    /// <c>i</c> as stationed, or null when it was dropped (fewer than two usable points).
    /// </summary>
    public sealed record Result(IReadOnlyList<double[]?> Breaklines, IReadOnlyList<double[]?> Contours);

    /// <summary>
    /// <see cref="Process"/> keeping the source classes apart and aligned with the inputs. Whatever persists
    /// a line as a constraint for a later stage must persist THIS form: it is what the TIN was built from.
    /// A later constrained rebuild inserting the raw line instead passes each long straight segment within
    /// rounding of the stations the mesh already carries, and the triangulator fills every gap with a
    /// zero-area cap — ~11,000 of them on one terrain.
    /// </summary>
    public static Result ProcessSeparately(
        IReadOnlyList<double[]> breaklinePolylines,
        IReadOnlyList<double[]> contourPolylines,
        double tolerance,
        double[]? samplePoints = null)
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
        double contourSpacing = measuredContourSpacing ?? combinedSpacing;

        int sampleCount = samplePoints == null ? 0 : samplePoints.Length / 3;
        bool hasNeighbourData = contourLengths.Count > 0 || sampleCount > 0;

        // With contours or spots present, a breakline's stations come from the data around it (below).
        // Borrowing the contour median here instead made a two-point breakline 0.25 m-dense across a 3 m
        // gap — slivers — while a four-point one kept its own median and stayed sparse.
        double breaklineSpacing = measuredBreaklineSpacing ??
            (hasNeighbourData ? double.PositiveInfinity : combinedSpacing);

        double[]?[] alignedBreaklines = ProcessPolylines(breaklinePolylines, breaklineSpacing, tolerance);
        double[]?[] alignedContours = ProcessPolylines(contourPolylines, contourSpacing, tolerance);
        var processedBreaklines = alignedBreaklines.Where(static polyline => polyline != null).Select(static polyline => polyline!).ToList();
        var processedContours = alignedContours.Where(static polyline => polyline != null).Select(static polyline => polyline!).ToList();
        if (processedBreaklines.Count > 0)
        {
            double floor = Math.Max(tolerance * 8.0, (measuredContourSpacing ?? 0.0) * 0.5);
            StationBreaklinesToNeighbours(processedBreaklines, processedContours, samplePoints, sampleCount, floor, tolerance);

            // Stationing replaces list entries; write them back into their aligned slots.
            int next = 0;
            for (int i = 0; i < alignedBreaklines.Length; i++)
            {
                if (alignedBreaklines[i] != null)
                    alignedBreaklines[i] = processedBreaklines[next++];
            }
        }

        return new Result(alignedBreaklines, alignedContours);
    }

    /// <summary>
    /// Adds stations along each breakline segment at roughly its clearance — the plan distance to the
    /// nearest input vertex that is not its own. A sparse breakline between dense contours otherwise
    /// becomes a few hub vertices, each fanning to 100+ contour vertices in slivers under 1°, and the line
    /// cannot hold its shape in the TIN. Stationing to the clearance gives near-equilateral triangles
    /// across the gap. In a void the clearance is large, so nothing is added — densifying there is
    /// measurably worse, because the added stations only give isolated vertices more distant partners.
    /// </summary>
    private static void StationBreaklinesToNeighbours(
        List<double[]> breaklines,
        List<double[]> contours,
        double[]? samplePoints,
        int sampleCount,
        double floor,
        double tolerance)
    {
        var index = NeighbourIndex.Build(breaklines, contours, samplePoints, sampleCount);
        if (index == null)
            return;

        // Never more than double the input: a breakline cannot out-number the data it is stationed to.
        long budget = index.Count;
        var output = new List<double>();
        for (int b = 0; b < breaklines.Count && budget > 0; b++)
        {
            double[] polyline = breaklines[b];
            int pointCount = polyline.Length / 3;
            output.Clear();
            output.Add(polyline[0]);
            output.Add(polyline[1]);
            output.Add(polyline[2]);
            bool changed = false;
            for (int i = 0; i + 1 < pointCount; i++)
            {
                var start = new Vertex(polyline[i * 3], polyline[i * 3 + 1], polyline[i * 3 + 2]);
                var end = new Vertex(polyline[i * 3 + 3], polyline[i * 3 + 4], polyline[i * 3 + 5]);
                double length = Distance(start, end);
                double position = 0.0;
                while (budget > 0)
                {
                    double remaining = length - position;
                    double t = position / length;
                    double spacing = Math.Max(
                        index.Clearance(Lerp(start.X, end.X, t), Lerp(start.Y, end.Y, t), b, remaining),
                        floor);

                    // Only split when the remainder holds at least one and a half stations, so the last
                    // gap is never a sliver next to the segment end.
                    if (remaining < spacing * 1.5)
                        break;

                    position += spacing;
                    t = position / length;
                    AddVertex(output, new Vertex(Lerp(start.X, end.X, t), Lerp(start.Y, end.Y, t), Lerp(start.Z, end.Z, t)), tolerance);
                    budget--;
                    changed = true;
                }

                AddVertex(output, end, tolerance);
            }

            if (changed)
                breaklines[b] = output.ToArray();
        }
    }

    /// <summary>XY hash of every input vertex, tagged with its breakline (or -1), for clearance queries.</summary>
    private sealed class NeighbourIndex
    {
        private readonly Dictionary<(long X, long Y), List<int>> _cells;
        private readonly double[] _xy;
        private readonly int[] _owner;
        private readonly double _cellSize;
        private readonly double _diagonal;

        public int Count => _owner.Length;

        private NeighbourIndex(double[] xy, int[] owner, double cellSize, double diagonal)
        {
            _xy = xy;
            _owner = owner;
            _cellSize = cellSize;
            _diagonal = diagonal;
            _cells = new Dictionary<(long, long), List<int>>();
            for (int i = 0; i < owner.Length; i++)
            {
                var key = Key(xy[i * 2], xy[i * 2 + 1]);
                if (!_cells.TryGetValue(key, out List<int>? bucket))
                    _cells.Add(key, bucket = new List<int>(4));
                bucket.Add(i);
            }
        }

        public static NeighbourIndex? Build(List<double[]> breaklines, List<double[]> contours, double[]? samples, int sampleCount)
        {
            int count = sampleCount;
            foreach (double[] p in breaklines) count += p.Length / 3;
            foreach (double[] p in contours) count += p.Length / 3;
            if (count == 0)
                return null;

            var xy = new double[count * 2];
            var owner = new int[count];
            int n = 0;
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            void Add(double x, double y, int tag)
            {
                if (!double.IsFinite(x) || !double.IsFinite(y))
                    return;
                xy[n * 2] = x;
                xy[n * 2 + 1] = y;
                owner[n++] = tag;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }

            for (int b = 0; b < breaklines.Count; b++)
                for (int i = 0; i < breaklines[b].Length / 3; i++)
                    Add(breaklines[b][i * 3], breaklines[b][i * 3 + 1], b);
            foreach (double[] p in contours)
                for (int i = 0; i < p.Length / 3; i++)
                    Add(p[i * 3], p[i * 3 + 1], -1);
            for (int i = 0; i < sampleCount; i++)
                Add(samples![i * 3], samples[i * 3 + 1], -1);
            if (n == 0)
                return null;

            if (n < count)
            {
                Array.Resize(ref xy, n * 2);
                Array.Resize(ref owner, n);
            }

            double width = maxX - minX, height = maxY - minY;
            double diagonal = Math.Sqrt((width * width) + (height * height));
            double cellSize = Math.Sqrt(Math.Max(width * height, diagonal * diagonal * 1e-6) / n);
            if (!(cellSize > 0.0) || !double.IsFinite(cellSize))
                cellSize = Math.Max(diagonal, 1.0);
            return new NeighbourIndex(xy, owner, cellSize, diagonal);
        }

        /// <summary>
        /// Plan distance from (x, y) to the nearest vertex not owned by <paramref name="self"/>, searched no
        /// farther than <paramref name="maxRadius"/> (returned when nothing is nearer).
        /// </summary>
        public double Clearance(double x, double y, int self, double maxRadius)
        {
            double limit = Math.Min(maxRadius, _diagonal);
            if (!(limit > 0.0))
                return maxRadius;

            double bestSq = limit * limit;
            var (cx, cy) = Key(x, y);
            long rings = (long)Math.Ceiling(limit / _cellSize) + 1;
            if ((2 * rings + 1) * (2 * rings + 1) > _owner.Length)
            {
                for (int i = 0; i < _owner.Length; i++)
                    Consider(i);
                return Math.Sqrt(bestSq);
            }

            for (long r = 0; r <= rings; r++)
            {
                for (long gy = cy - r; gy <= cy + r; gy++)
                {
                    bool edgeRow = gy == cy - r || gy == cy + r;
                    for (long gx = cx - r; gx <= cx + r; gx += edgeRow || r == 0 ? 1 : 2 * r)
                    {
                        if (_cells.TryGetValue((gx, gy), out List<int>? bucket))
                            foreach (int i in bucket)
                                Consider(i);
                    }
                }

                // Every unvisited cell is at least r cells away.
                double reached = r * _cellSize;
                if (reached * reached >= bestSq)
                    break;
            }

            return Math.Sqrt(bestSq);

            void Consider(int i)
            {
                if (_owner[i] == self && self >= 0)
                    return;
                double dx = _xy[i * 2] - x, dy = _xy[i * 2 + 1] - y;
                double dSq = (dx * dx) + (dy * dy);
                if (dSq < bestSq)
                    bestSq = dSq;
            }
        }

        private (long X, long Y) Key(double x, double y) =>
            ((long)Math.Floor(x / _cellSize), (long)Math.Floor(y / _cellSize));
    }

    private static double[]?[] ProcessPolylines(
        IReadOnlyList<double[]> polylines,
        double targetSpacing,
        double tolerance)
    {
        var result = new double[]?[polylines.Count];
        for (int i = 0; i < polylines.Count; i++)
        {
            double[] polyline = polylines[i];
            if (!TryExtractVertices(polyline, tolerance, out List<Vertex> vertices))
                continue;

            double[] processed = ResamplePolyline(vertices, targetSpacing, tolerance);
            if (processed.Length >= 6)
                result[i] = processed;
        }

        return result;
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
