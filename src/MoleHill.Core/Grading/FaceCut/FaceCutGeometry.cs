using MoleHill.Core.Engine;
using MoleHill.Core.Geometry;

namespace MoleHill.Core.Grading;

/// <summary>
/// The face-cutting kernel shared by <see cref="MeshAreaTopologySplitter"/> (conform area loops) and
/// <see cref="MeshConstraintTopologyInserter"/> (insert constraint lines): splitting a segment network at
/// its own crossings, clipping a segment to one face, recording the edge points and interior pieces a face
/// must honour, and building the per-edge split chains of its local triangulation.
/// <para>
/// The two callers were once copies of each other. What is genuinely different between them is policy and
/// stays with them, named: edge-point merging (<see cref="EdgePointMerge"/>), and the area splitter's
/// 8x-tolerance conform snaps (<see cref="MeshAreaTopologySplitter.ConformSnapToleranceFactor"/>), which line
/// insertion must not use - a wall rail moved 8 mm leaves needles beside the conformed geometry.
/// </para>
/// </summary>
internal static class FaceCutGeometry
{
    /// <summary>
    /// Splits every segment at its crossings with the others, then drops sub-segments shorter than the
    /// tolerance and duplicates (either direction, keyed on a tolerance grid). Pairs are discovered through a
    /// spatial index and visited in ascending order, so split parameters accumulate exactly as an all-pairs
    /// sweep would.
    /// </summary>
    public static List<CutSegment> SplitAtMutualIntersections(
        List<CutSegment> sourceSegments,
        double tolerance,
        CancellationProbe probe)
    {
        if (sourceSegments.Count == 0)
            return sourceSegments;

        var splitParameters = new List<double>[sourceSegments.Count];
        var segmentBounds = new Bounds2D[sourceSegments.Count];
        for (int i = 0; i < sourceSegments.Count; i++)
        {
            splitParameters[i] = new List<double>(4) { 0.0, 1.0 };
            CutSegment segment = sourceSegments[i];
            segmentBounds[i] = new Bounds2D(
                Math.Min(segment.Start.X, segment.End.X),
                Math.Max(segment.Start.X, segment.End.X),
                Math.Min(segment.Start.Y, segment.End.Y),
                Math.Max(segment.Start.Y, segment.End.Y));
        }

        // Zone boundaries come straight from GIS/CAD polygons and routinely carry tens of thousands of
        // vertices; the all-pairs sweep this replaces did ~1.6e9 bbox tests on a 56k-vertex cadastral layer.
        SpatialHashGrid2D pairGrid = SpatialHashGrid2D.Build(segmentBounds, valid: null, probe);
        var pairScratch = new SpatialHashGrid2D.QueryScratch(sourceSegments.Count);
        var pairCandidates = new List<int>(16);

        for (int i = 0; i < sourceSegments.Count; i++)
        {
            probe.ThrowIfCancelledOften();
            pairGrid.GatherCandidates(segmentBounds[i], pairCandidates, pairScratch);
            pairCandidates.Sort();

            foreach (int j in pairCandidates)
            {
                if (j <= i)
                    continue;

                if (!segmentBounds[i].Intersects(segmentBounds[j]))
                    continue;

                SegmentIntersection intersection = IntersectSegments(
                    sourceSegments[i].Start, sourceSegments[i].End, sourceSegments[j].Start, sourceSegments[j].End, tolerance);
                if (intersection.Kind == SegmentIntersectionKind.None)
                    continue;

                AddSplitParameter(splitParameters[i], intersection.T0);
                AddSplitParameter(splitParameters[i], intersection.T1);

                SegmentIntersection reverse = IntersectSegments(
                    sourceSegments[j].Start, sourceSegments[j].End, sourceSegments[i].Start, sourceSegments[i].End, tolerance);
                AddSplitParameter(splitParameters[j], reverse.T0);
                AddSplitParameter(splitParameters[j], reverse.T1);
            }
        }

        var dedupedSegments = new List<CutSegment>();
        double inverseTolerance = 1.0 / tolerance;
        var seen = new HashSet<(long, long, long, long)>();

        for (int segmentIndex = 0; segmentIndex < sourceSegments.Count; segmentIndex++)
        {
            List<double> parameters = splitParameters[segmentIndex];
            parameters.Sort();

            int uniqueCount = 0;
            for (int i = 0; i < parameters.Count; i++)
            {
                double value = Math.Clamp(parameters[i], 0.0, 1.0);
                if (uniqueCount > 0 && Math.Abs(value - parameters[uniqueCount - 1]) <= 1e-9)
                    continue;

                parameters[uniqueCount++] = value;
            }

            CutSegment source = sourceSegments[segmentIndex];
            for (int i = 0; i < uniqueCount - 1; i++)
            {
                double t0 = parameters[i];
                double t1 = parameters[i + 1];
                if (t1 - t0 <= 1e-9)
                    continue;

                Point2D start = Lerp(source.Start, source.End, t0);
                Point2D end = Lerp(source.Start, source.End, t1);
                if (DistanceSquared(start, end) <= tolerance * tolerance)
                    continue;

                if (!seen.Add(CreateSegmentKey(start, end, inverseTolerance)))
                    continue;

                dedupedSegments.Add(new CutSegment(start, end));
            }
        }

        return dedupedSegments;
    }

    /// <summary>
    /// Clips <paramref name="segment"/> to <paramref name="face"/> in one pass over its three edges: the
    /// points where it touches an edge (snapped onto the edge, deduplicated by <paramref name="merge"/>) and
    /// the pieces of it lying inside the face (ends snapped to a corner or edge within tolerance). Results go
    /// to caller-owned buffers - at most 8 edge points, 8 parameters and 7 pieces - so a parallel caller
    /// allocates nothing per face.
    /// </summary>
    public static void AnalyzeSegmentAgainstFace(
        in FaceData face,
        CutSegment segment,
        double tolerance,
        EdgePointMerge merge,
        EdgePoint[] edgePoints,
        out int edgePointCount,
        double[] parameters,
        SegmentPiece[] clippedPieces,
        out int clippedPieceCount)
    {
        edgePointCount = 0;
        clippedPieceCount = 0;
        int parameterCount = 0;
        bool startInside = face.ContainsPoint(segment.Start, tolerance);
        bool endInside = face.ContainsPoint(segment.End, tolerance);

        if (startInside)
            parameters[parameterCount++] = 0.0;
        if (endInside)
            parameters[parameterCount++] = 1.0;

        for (int edgeIndex = 0; edgeIndex < 3; edgeIndex++)
        {
            SegmentIntersection intersection = IntersectSegments(
                segment.Start, segment.End, face.GetEdgeStart(edgeIndex), face.GetEdgeEnd(edgeIndex), tolerance);
            if (intersection.Kind == SegmentIntersectionKind.None)
                continue;

            AddEdgeTouchPoint(edgePoints, ref edgePointCount, face, edgeIndex, intersection.P0, tolerance, merge);
            if (intersection.Kind == SegmentIntersectionKind.Overlap)
                AddEdgeTouchPoint(edgePoints, ref edgePointCount, face, edgeIndex, intersection.P1, tolerance, merge);

            parameters[parameterCount++] = intersection.T0;
            parameters[parameterCount++] = intersection.T1;
        }

        if (startInside)
        {
            int edgeIndex = face.GetEdgeIndex(segment.Start, tolerance);
            if (edgeIndex >= 0)
                AddEdgeTouchPoint(edgePoints, ref edgePointCount, face, edgeIndex, segment.Start, tolerance, merge);
        }

        if (endInside)
        {
            int edgeIndex = face.GetEdgeIndex(segment.End, tolerance);
            if (edgeIndex >= 0)
                AddEdgeTouchPoint(edgePoints, ref edgePointCount, face, edgeIndex, segment.End, tolerance, merge);
        }

        if (parameterCount == 0)
            return;

        // Insertion sort: at most eight values.
        for (int index = 1; index < parameterCount; index++)
        {
            double value = parameters[index];
            int writeIndex = index;
            while (writeIndex > 0 && parameters[writeIndex - 1] > value)
            {
                parameters[writeIndex] = parameters[writeIndex - 1];
                writeIndex--;
            }

            parameters[writeIndex] = value;
        }

        int uniqueCount = 0;
        for (int index = 0; index < parameterCount; index++)
        {
            double value = Math.Clamp(parameters[index], 0.0, 1.0);
            if (uniqueCount > 0 && Math.Abs(value - parameters[uniqueCount - 1]) <= 1e-9)
                continue;

            parameters[uniqueCount++] = value;
        }

        for (int index = 0; index < uniqueCount - 1; index++)
        {
            double t0 = parameters[index];
            double t1 = parameters[index + 1];
            if (t1 - t0 <= 1e-9)
                continue;

            Point2D midpoint = Lerp(segment.Start, segment.End, (t0 + t1) * 0.5);
            if (!face.ContainsPoint(midpoint, tolerance))
                continue;

            Point2D start = SnapPointToTriangle(face, Lerp(segment.Start, segment.End, t0), tolerance);
            Point2D end = SnapPointToTriangle(face, Lerp(segment.Start, segment.End, t1), tolerance);
            if (DistanceSquared(start, end) <= tolerance * tolerance)
                continue;

            clippedPieces[clippedPieceCount++] = new SegmentPiece(start, end);
        }
    }

    /// <summary>The pieces of <paramref name="segment"/> lying inside <paramref name="face"/> (see <see cref="AnalyzeSegmentAgainstFace"/>).</summary>
    public static List<SegmentPiece> ClipSegmentToTriangle(in FaceData face, CutSegment segment, double tolerance)
    {
        var pieces = new SegmentPiece[7];
        AnalyzeSegmentAgainstFace(
            face, segment, tolerance, EdgePointMerge.SameEdgeOnly,
            new EdgePoint[8], out _, new double[8], pieces, out int pieceCount);
        var result = new List<SegmentPiece>(pieceCount);
        for (int i = 0; i < pieceCount; i++)
            result.Add(pieces[i]);
        return result;
    }

    private static void AddEdgeTouchPoint(
        EdgePoint[] destination,
        ref int count,
        in FaceData face,
        int edgeIndex,
        Point2D point,
        double tolerance,
        EdgePointMerge merge)
    {
        Point2D snapped = SnapPointToEdge(face.GetEdgeStart(edgeIndex), face.GetEdgeEnd(edgeIndex), point);
        if (face.IsNearVertex(snapped, tolerance))
            return;

        var candidate = new EdgePoint(edgeIndex, snapped);
        for (int index = 0; index < count; index++)
        {
            if (IsDuplicateEdgePoint(destination[index], candidate, face, tolerance, merge))
                return;
        }

        destination[count++] = candidate;
    }

    /// <summary>Records a split point on one edge of a face unless <paramref name="merge"/> calls it a duplicate.</summary>
    public static void AddUniqueEdgePoint(List<EdgePoint> destination, EdgePoint candidate, in FaceData face, double tolerance, EdgePointMerge merge)
    {
        for (int i = 0; i < destination.Count; i++)
        {
            if (IsDuplicateEdgePoint(destination[i], candidate, face, tolerance, merge))
                return;
        }

        destination.Add(candidate);
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> repeats <paramref name="existing"/>: always when both lie on the
    /// same edge within tolerance; under <see cref="EdgePointMerge.SameEdgeOrSharedCorner"/> also when they
    /// lie on different edges within tolerance of each other and both within twice the tolerance of one corner.
    /// </summary>
    public static bool IsDuplicateEdgePoint(EdgePoint existing, EdgePoint candidate, in FaceData face, double tolerance, EdgePointMerge merge)
    {
        double toleranceSquared = tolerance * tolerance;
        if (DistanceSquared(existing.Point, candidate.Point) > toleranceSquared)
            return false;

        if (existing.EdgeIndex == candidate.EdgeIndex)
            return true;

        if (merge == EdgePointMerge.SameEdgeOnly)
            return false;

        double vertexToleranceSquared = 4.0 * toleranceSquared;
        for (int vertexIndex = 0; vertexIndex < 3; vertexIndex++)
        {
            Point2D vertex = face.GetVertex(vertexIndex);
            if (DistanceSquared(existing.Point, vertex) <= vertexToleranceSquared &&
                DistanceSquared(candidate.Point, vertex) <= vertexToleranceSquared)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Records the ends of an interior piece that land on a face edge as split points of that edge.</summary>
    public static void AddPieceEndpointEdgePoints(List<EdgePoint> edgePoints, in FaceData face, SegmentPiece piece, double tolerance, EdgePointMerge merge)
    {
        int startEdge = face.GetEdgeIndex(piece.Start, tolerance);
        if (startEdge >= 0)
            AddUniqueEdgePoint(edgePoints, new EdgePoint(startEdge, piece.Start), face, tolerance, merge);

        int endEdge = face.GetEdgeIndex(piece.End, tolerance);
        if (endEdge >= 0)
            AddUniqueEdgePoint(edgePoints, new EdgePoint(endEdge, piece.End), face, tolerance, merge);
    }

    /// <summary>The edge a piece runs along (both ends and its midpoint on it), or -1 for an interior piece.</summary>
    public static int GetPieceEdgeIndex(in FaceData face, SegmentPiece piece, double tolerance)
    {
        int startEdge = face.GetEdgeIndex(piece.Start, tolerance);
        int endEdge = face.GetEdgeIndex(piece.End, tolerance);
        if (startEdge < 0 || startEdge != endEdge)
            return -1;

        var midpoint = new Point2D((piece.Start.X + piece.End.X) * 0.5, (piece.Start.Y + piece.End.Y) * 0.5);
        return face.GetEdgeIndex(midpoint, tolerance) == startEdge
            ? startEdge
            : -1;
    }

    public static void AddUniqueSegment(List<SegmentPiece> destination, SegmentPiece candidate, double tolerance)
    {
        double toleranceSquared = tolerance * tolerance;
        for (int i = 0; i < destination.Count; i++)
        {
            SegmentPiece existing = destination[i];
            bool sameDirection = DistanceSquared(existing.Start, candidate.Start) <= toleranceSquared &&
                                 DistanceSquared(existing.End, candidate.End) <= toleranceSquared;
            bool reverseDirection = DistanceSquared(existing.Start, candidate.End) <= toleranceSquared &&
                                    DistanceSquared(existing.End, candidate.Start) <= toleranceSquared;
            if (sameDirection || reverseDirection)
                return;
        }

        destination.Add(candidate);
    }

    /// <summary>
    /// Seeds the three per-edge split chains of a face's local triangulation with the edge's own ends
    /// (edge 0 runs A-B, edge 1 B-C, edge 2 C-A).
    /// </summary>
    public static List<(double Parameter, int LocalIndex)>[] CreateEdgeChains(int a, int b, int c)
    {
        var chains = new List<(double Parameter, int LocalIndex)>[3];
        for (int edgeIndex = 0; edgeIndex < 3; edgeIndex++)
            chains[edgeIndex] = new List<(double Parameter, int LocalIndex)>(4);

        chains[0].Add((0.0, a));
        chains[0].Add((1.0, b));
        chains[1].Add((0.0, b));
        chains[1].Add((1.0, c));
        chains[2].Add((0.0, c));
        chains[2].Add((1.0, a));
        return chains;
    }

    /// <summary>
    /// Sorts each edge chain by parameter, drops repeats (same local point, or parameters within 1e-9) in
    /// place, and adds a constraint segment between consecutive points. Returns each chain's kept length.
    /// </summary>
    public static int[] AddEdgeChainSegments(
        List<(double Parameter, int LocalIndex)>[] chains,
        List<(int a, int b)> segments,
        HashSet<long> segmentKeys)
    {
        var counts = new int[3];
        for (int edgeIndex = 0; edgeIndex < 3; edgeIndex++)
        {
            List<(double Parameter, int LocalIndex)> points = chains[edgeIndex];
            points.Sort((left, right) => left.Parameter.CompareTo(right.Parameter));

            int writeIndex = 0;
            for (int i = 0; i < points.Count; i++)
            {
                (double Parameter, int LocalIndex) current = points[i];
                if (writeIndex > 0)
                {
                    (double Parameter, int LocalIndex) previous = points[writeIndex - 1];
                    if (current.LocalIndex == previous.LocalIndex || Math.Abs(current.Parameter - previous.Parameter) <= 1e-9)
                        continue;
                }

                points[writeIndex++] = current;
            }

            counts[edgeIndex] = writeIndex;
            for (int i = 0; i < writeIndex - 1; i++)
            {
                int start = points[i].LocalIndex;
                int end = points[i + 1].LocalIndex;
                if (start == end)
                    continue;

                MeshConstraintTools.TryAddSegment(segments, segmentKeys, start, end);
            }
        }

        return counts;
    }

    /// <summary>
    /// True when the local point set spans a real area, i.e. some three points form a triangle above the
    /// degeneracy epsilon. Runs in O(n): the point farthest from the first one fixes the dominant direction,
    /// so if any point lies off that line the set is not collinear.
    /// </summary>
    public static bool HasTriangulableArea(LocalPointBuilder points, double tolerance)
    {
        if (points.Count < 3)
            return false;

        Point2D origin = points.GetPoint(0);
        int farthest = -1;
        double farthestDistance = 0.0;
        for (int i = 1; i < points.Count; i++)
        {
            double distance = DistanceSquared(origin, points.GetPoint(i));
            if (distance > farthestDistance)
            {
                farthestDistance = distance;
                farthest = i;
            }
        }

        if (farthest < 0 || farthestDistance <= 0.0)
            return false;

        Point2D axis = points.GetPoint(farthest);
        double areaEpsilon = tolerance * tolerance * 1e-3;
        for (int i = 1; i < points.Count; i++)
        {
            if (i == farthest)
                continue;

            Point2D candidate = points.GetPoint(i);
            double cross = Math.Abs(
                ((axis.X - origin.X) * (candidate.Y - origin.Y)) -
                ((axis.Y - origin.Y) * (candidate.X - origin.X)));
            if (cross > areaEpsilon)
                return true;
        }

        return false;
    }

    public static SegmentIntersection IntersectSegments(Point2D a0, Point2D a1, Point2D b0, Point2D b1, double tolerance)
    {
        double rx = a1.X - a0.X;
        double ry = a1.Y - a0.Y;
        double sx = b1.X - b0.X;
        double sy = b1.Y - b0.Y;
        double rxs = Cross(rx, ry, sx, sy);
        double qpx = b0.X - a0.X;
        double qpy = b0.Y - a0.Y;
        double qpxr = Cross(qpx, qpy, rx, ry);

        if (Math.Abs(rxs) <= tolerance && Math.Abs(qpxr) <= tolerance)
        {
            double t0 = ParameterOnSegment(a0, a1, b0);
            double t1 = ParameterOnSegment(a0, a1, b1);
            double minT = Math.Max(0.0, Math.Min(t0, t1));
            double maxT = Math.Min(1.0, Math.Max(t0, t1));
            if (maxT < 0.0 || minT > 1.0)
                return NoIntersection(a0);

            Point2D start = Lerp(a0, a1, minT);
            Point2D end = Lerp(a0, a1, maxT);
            if (DistanceSquared(start, end) <= tolerance * tolerance)
            {
                return new SegmentIntersection
                {
                    Kind = SegmentIntersectionKind.Point,
                    T0 = minT,
                    T1 = minT,
                    P0 = start,
                    P1 = start
                };
            }

            return new SegmentIntersection
            {
                Kind = SegmentIntersectionKind.Overlap,
                T0 = minT,
                T1 = maxT,
                P0 = start,
                P1 = end
            };
        }

        if (Math.Abs(rxs) <= tolerance)
            return NoIntersection(a0);

        double t = Cross(qpx, qpy, sx, sy) / rxs;
        double u = Cross(qpx, qpy, rx, ry) / rxs;
        if (t < -1e-9 || t > 1.0 + 1e-9 || u < -1e-9 || u > 1.0 + 1e-9)
            return NoIntersection(a0);

        double clamped = Math.Clamp(t, 0.0, 1.0);
        Point2D intersectionPoint = Lerp(a0, a1, clamped);
        return new SegmentIntersection
        {
            Kind = SegmentIntersectionKind.Point,
            T0 = clamped,
            T1 = clamped,
            P0 = intersectionPoint,
            P1 = intersectionPoint
        };
    }

    private static SegmentIntersection NoIntersection(Point2D a0) => new()
    {
        Kind = SegmentIntersectionKind.None,
        T0 = 0.0,
        T1 = 0.0,
        P0 = a0,
        P1 = a0
    };

    public static void AddSplitParameter(List<double> parameters, double value)
    {
        value = Math.Clamp(value, 0.0, 1.0);
        for (int i = 0; i < parameters.Count; i++)
        {
            if (Math.Abs(parameters[i] - value) <= 1e-9)
                return;
        }

        parameters.Add(value);
    }

    public static (long, long, long, long) CreateSegmentKey(Point2D start, Point2D end, double inverseTolerance)
    {
        long x0 = (long)Math.Round(start.X * inverseTolerance);
        long y0 = (long)Math.Round(start.Y * inverseTolerance);
        long x1 = (long)Math.Round(end.X * inverseTolerance);
        long y1 = (long)Math.Round(end.Y * inverseTolerance);
        bool keepOrder = x0 < x1 || (x0 == x1 && y0 <= y1);
        return keepOrder
            ? (x0, y0, x1, y1)
            : (x1, y1, x0, y0);
    }

    /// <summary>Snaps a point within tolerance of a corner onto it, else one within tolerance of an edge onto the edge.</summary>
    public static Point2D SnapPointToTriangle(in FaceData face, Point2D point, double tolerance)
    {
        if (DistanceSquared(point, face.A) <= tolerance * tolerance)
            return face.A;
        if (DistanceSquared(point, face.B) <= tolerance * tolerance)
            return face.B;
        if (DistanceSquared(point, face.C) <= tolerance * tolerance)
            return face.C;

        int edgeIndex = face.GetEdgeIndex(point, tolerance);
        return edgeIndex < 0
            ? point
            : SnapPointToEdge(face.GetEdgeStart(edgeIndex), face.GetEdgeEnd(edgeIndex), point);
    }

    /// <summary>Projects a point onto the segment (clamped to its ends).</summary>
    public static Point2D SnapPointToEdge(Point2D edgeStart, Point2D edgeEnd, Point2D point) =>
        Lerp(edgeStart, edgeEnd, ParameterOnEdge(edgeStart, edgeEnd, point));

    /// <summary>Parameter of the projection of <paramref name="point"/> on the line through the segment (unclamped).</summary>
    public static double ParameterOnSegment(Point2D start, Point2D end, Point2D point)
    {
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared <= 1e-20)
            return 0.0;

        return (((point.X - start.X) * dx) + ((point.Y - start.Y) * dy)) / lengthSquared;
    }

    /// <summary>Parameter of the projection of <paramref name="point"/> on the segment, clamped to [0, 1].</summary>
    public static double ParameterOnEdge(Point2D start, Point2D end, Point2D point)
    {
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared <= 1e-20)
            return 0.0;

        return Math.Clamp((((point.X - start.X) * dx) + ((point.Y - start.Y) * dy)) / lengthSquared, 0.0, 1.0);
    }

    /// <summary>
    /// True when the point lies within <paramref name="tolerance"/> of the segment. Note the parameter window
    /// is also widened by <paramref name="tolerance"/> (a parameter, not a distance); the distance test after
    /// clamping is what decides.
    /// </summary>
    public static bool PointOnSegment(Point2D point, Point2D start, Point2D end, double tolerance)
    {
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared <= 1e-20)
            return DistanceSquared(point, start) <= tolerance * tolerance;

        double t = (((point.X - start.X) * dx) + ((point.Y - start.Y) * dy)) / lengthSquared;
        if (t < -tolerance || t > 1.0 + tolerance)
            return false;

        var projected = new Point2D(start.X + (dx * Math.Clamp(t, 0.0, 1.0)), start.Y + (dy * Math.Clamp(t, 0.0, 1.0)));
        return DistanceSquared(projected, point) <= tolerance * tolerance;
    }

    public static Point2D Lerp(Point2D start, Point2D end, double t) =>
        new(start.X + ((end.X - start.X) * t), start.Y + ((end.Y - start.Y) * t));

    public static double Cross(double ax, double ay, double bx, double by) => Geometry2D.Cross(ax, ay, bx, by);

    public static double DistanceSquared(Point2D a, Point2D b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return (dx * dx) + (dy * dy);
    }

    /// <summary>Spatial cell key. Collections keyed by it use <see cref="IndexedMeshTools.CellKeyComparer"/>.</summary>
    public static long PackKey(long cellX, long cellY) => (cellX * 0x100000001L) ^ (cellY * 0x27d4eb2dL);
}
