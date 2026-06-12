using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

internal static class MeshAreaTopologySplitter
{
    private readonly record struct Point2D(double X, double Y);
    private readonly record struct BoundarySegment(Point2D Start, Point2D End);
    private readonly record struct SegmentPiece(Point2D Start, Point2D End);
    private readonly record struct EdgePoint(int EdgeIndex, Point2D Point);

    private sealed class FaceData
    {
        public required int I0 { get; init; }
        public required int I1 { get; init; }
        public required int I2 { get; init; }
        public required Point2D A { get; init; }
        public required Point2D B { get; init; }
        public required Point2D C { get; init; }
        public required double Az { get; init; }
        public required double Bz { get; init; }
        public required double Cz { get; init; }
        public required Bounds2D Bounds { get; init; }

        public Point2D GetVertex(int index) => index switch
        {
            0 => A,
            1 => B,
            _ => C
        };

        public Point2D GetEdgeStart(int edgeIndex) => edgeIndex switch
        {
            0 => A,
            1 => B,
            _ => C
        };

        public Point2D GetEdgeEnd(int edgeIndex) => edgeIndex switch
        {
            0 => B,
            1 => C,
            _ => A
        };

        public bool IsNearVertex(Point2D point, double tolerance)
        {
            double tolSq = tolerance * tolerance;
            return DistanceSquared(point, A) <= tolSq ||
                   DistanceSquared(point, B) <= tolSq ||
                   DistanceSquared(point, C) <= tolSq;
        }

        public bool ContainsPoint(Point2D point, double tolerance)
        {
            double x0 = A.X;
            double y0 = A.Y;
            double x1 = B.X;
            double y1 = B.Y;
            double x2 = C.X;
            double y2 = C.Y;
            double denom = ((y1 - y2) * (x0 - x2)) + ((x2 - x1) * (y0 - y2));
            if (Math.Abs(denom) <= 1e-16)
                return false;

            double w0 = (((y1 - y2) * (point.X - x2)) + ((x2 - x1) * (point.Y - y2))) / denom;
            double w1 = (((y2 - y0) * (point.X - x2)) + ((x0 - x2) * (point.Y - y2))) / denom;
            double w2 = 1.0 - w0 - w1;
            const double barycentricTolerance = 1e-8;
            return w0 >= -barycentricTolerance &&
                   w1 >= -barycentricTolerance &&
                   w2 >= -barycentricTolerance;
        }

        public double InterpolateZ(Point2D point)
        {
            double x0 = A.X;
            double y0 = A.Y;
            double x1 = B.X;
            double y1 = B.Y;
            double x2 = C.X;
            double y2 = C.Y;
            double denom = ((y1 - y2) * (x0 - x2)) + ((x2 - x1) * (y0 - y2));
            if (Math.Abs(denom) <= 1e-16)
                return Az;

            double w0 = (((y1 - y2) * (point.X - x2)) + ((x2 - x1) * (point.Y - y2))) / denom;
            double w1 = (((y2 - y0) * (point.X - x2)) + ((x0 - x2) * (point.Y - y2))) / denom;
            double w2 = 1.0 - w0 - w1;
            return (w0 * Az) + (w1 * Bz) + (w2 * Cz);
        }

        public int GetEdgeIndex(Point2D point, double tolerance)
        {
            for (int edgeIndex = 0; edgeIndex < 3; edgeIndex++)
            {
                if (PointOnSegment(point, GetEdgeStart(edgeIndex), GetEdgeEnd(edgeIndex), tolerance))
                    return edgeIndex;
            }

            return -1;
        }
    }

    private sealed class FaceCutData
    {
        public List<SegmentPiece> InternalSegments { get; } = new();
        public List<EdgePoint> EdgePoints { get; } = new();

        public bool HasData => InternalSegments.Count > 0 || EdgePoints.Count > 0;
    }

    private sealed class LocalPointBuilder
    {
        private readonly double _toleranceSquared;

        public LocalPointBuilder(double tolerance)
        {
            _toleranceSquared = tolerance * tolerance;
        }

        public List<double> Xy { get; } = new();
        public List<double> Z { get; } = new();

        public int Count => Z.Count;

        public int Add(Point2D point, double z)
        {
            for (int i = 0; i < Z.Count; i++)
            {
                double dx = Xy[i * 2] - point.X;
                double dy = Xy[i * 2 + 1] - point.Y;
                if ((dx * dx) + (dy * dy) <= _toleranceSquared)
                    return i;
            }

            int index = Z.Count;
            Xy.Add(point.X);
            Xy.Add(point.Y);
            Z.Add(z);
            return index;
        }

        public Point2D GetPoint(int index) => new(Xy[index * 2], Xy[index * 2 + 1]);

        public double GetZ(int index) => Z[index];

        public int Find(Point2D point)
        {
            for (int i = 0; i < Z.Count; i++)
            {
                double dx = Xy[i * 2] - point.X;
                double dy = Xy[i * 2 + 1] - point.Y;
                if ((dx * dx) + (dy * dy) <= _toleranceSquared)
                    return i;
            }

            return -1;
        }
    }

    private sealed class GlobalPointLookup
    {
        private readonly List<double> _vertices;
        private readonly double _toleranceSquared;
        private readonly double _inverseCellSize;
        private readonly Dictionary<long, List<int>> _cells = new();

        public GlobalPointLookup(List<double> vertices, double tolerance)
        {
            _vertices = vertices;
            double resolvedTolerance = Math.Max(tolerance, 1e-9);
            _toleranceSquared = resolvedTolerance * resolvedTolerance;
            _inverseCellSize = 1.0 / resolvedTolerance;

            int vertexCount = vertices.Count / 3;
            for (int i = 0; i < vertexCount; i++)
                Register(i, vertices[i * 3], vertices[i * 3 + 1]);
        }

        public int Resolve(Point2D point, double z)
        {
            if (TryFind(point, out int existing))
                return existing;

            int index = _vertices.Count / 3;
            _vertices.Add(point.X);
            _vertices.Add(point.Y);
            _vertices.Add(z);
            Register(index, point.X, point.Y);
            return index;
        }

        private bool TryFind(Point2D point, out int index)
        {
            long cellX = ToCell(point.X);
            long cellY = ToCell(point.Y);
            double bestDistanceSquared = double.MaxValue;
            int bestIndex = -1;

            for (long dx = -1; dx <= 1; dx++)
            {
                for (long dy = -1; dy <= 1; dy++)
                {
                    if (!_cells.TryGetValue(PackKey(cellX + dx, cellY + dy), out var list))
                        continue;

                    foreach (int candidate in list)
                    {
                        double vx = _vertices[candidate * 3];
                        double vy = _vertices[candidate * 3 + 1];
                        double deltaX = vx - point.X;
                        double deltaY = vy - point.Y;
                        double distanceSquared = (deltaX * deltaX) + (deltaY * deltaY);
                        if (distanceSquared > _toleranceSquared || distanceSquared >= bestDistanceSquared)
                            continue;

                        bestDistanceSquared = distanceSquared;
                        bestIndex = candidate;
                    }
                }
            }

            index = bestIndex;
            return bestIndex >= 0;
        }

        private void Register(int index, double x, double y)
        {
            long key = PackKey(ToCell(x), ToCell(y));
            if (!_cells.TryGetValue(key, out var list))
            {
                list = new List<int>(4);
                _cells[key] = list;
            }

            list.Add(index);
        }

        private long ToCell(double value) => (long)Math.Floor(value * _inverseCellSize);
    }

    private enum SegmentIntersectionKind
    {
        None,
        Point,
        Overlap
    }

    private sealed class SegmentIntersection
    {
        public required SegmentIntersectionKind Kind { get; init; }
        public required double T0 { get; init; }
        public required double T1 { get; init; }
        public required Point2D P0 { get; init; }
        public required Point2D P1 { get; init; }
    }

    public static MeshAreaSplitter.SplitResult? Split(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        MeshAreaSplitter.AreaBoundary[] areas,
        double boundaryTolerance,
        out string? errorMessage)
    {
        errorMessage = null;

        if (areas.Length == 0)
        {
            errorMessage = "No area boundaries provided.";
            return null;
        }

        if (vertexCount == 0 || faceCount == 0)
        {
            errorMessage = "Input mesh has no usable triangles.";
            return null;
        }

        double tolerance = Math.Max(boundaryTolerance, 1e-9);
        var faceData = BuildFaceData(vertices, faces, faceCount);
        var boundarySegments = BuildBoundarySegments(areas, tolerance);
        if (boundarySegments.Count == 0)
        {
            // Exact topology split should classify strictly by area ownership rather than
            // inflating the inside region by boundary tolerance, which can steal outside
            // seam-adjacent faces and leave the extracted outside mesh open.
            return MeshAreaSplitter.Classify(vertices, vertexCount, faces, faceCount, areas, 0.0, out errorMessage);
        }

        var faceCuts = MapBoundarySegmentsToFaces(faceData, boundarySegments, tolerance);
        bool hasTopologyEdits = false;
        for (int i = 0; i < faceCuts.Length; i++)
        {
            if (faceCuts[i]?.HasData == true)
            {
                hasTopologyEdits = true;
                break;
            }
        }

        if (!hasTopologyEdits)
            return MeshAreaSplitter.Classify(vertices, vertexCount, faces, faceCount, areas, 0.0, out errorMessage);

        // Conforming guarantee: every cut point that lands on a terrain edge is registered against
        // that edge (keyed by its two global vertex ids, shared by the two adjacent faces). Both
        // faces then subdivide the shared edge at the SAME points, so independent per-face
        // re-triangulation cannot leave a T-junction / crack. This is what keeps dense and nested
        // boundary loops manifold instead of producing naked edges along shared terrain edges.
        var sharedEdgePoints = BuildSharedEdgeRegistry(faceData, faceCuts, tolerance);

        var globalVertices = new List<double>(vertices);
        var globalFaces = new List<int>(faces.Length * 2);
        var pointLookup = new GlobalPointLookup(globalVertices, tolerance);

        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            var cuts = faceCuts[faceIndex];
            FaceData face = faceData[faceIndex];

            // A face must be re-triangulated when it carries its own cut data OR when a neighbour
            // subdivided one of its edges (registry hit): emitting it unchanged would leave the
            // neighbour's edge point dangling as a T-junction.
            bool hasOwnCuts = cuts != null && cuts.HasData;
            if (!hasOwnCuts && !HasRegistryEdgePoints(face, sharedEdgePoints))
            {
                globalFaces.Add(faces[faceIndex * 3]);
                globalFaces.Add(faces[faceIndex * 3 + 1]);
                globalFaces.Add(faces[faceIndex * 3 + 2]);
                continue;
            }

            if (!TriangulateTouchedFace(face, cuts, sharedEdgePoints, pointLookup, globalFaces, tolerance, out errorMessage))
                return null;
        }

        return MeshAreaSplitter.Classify(
            globalVertices.ToArray(),
            globalVertices.Count / 3,
            globalFaces.ToArray(),
            globalFaces.Count / 3,
            areas,
            0.0,
            out errorMessage);
    }

    /// <summary>
    /// Canonical key for the terrain edge at <paramref name="edgeIndex"/> of a face: the unordered
    /// pair of its two global vertex ids. Two faces sharing that edge produce the same key, so cut
    /// points registered against it are visible to both.
    /// </summary>
    private static (int, int) EdgeKey(FaceData face, int edgeIndex)
    {
        int start = edgeIndex switch { 0 => face.I0, 1 => face.I1, _ => face.I2 };
        int end = edgeIndex switch { 0 => face.I1, 1 => face.I2, _ => face.I0 };
        return start < end ? (start, end) : (end, start);
    }

    /// <summary>
    /// Collects every cut point that lies on a terrain edge into a per-edge registry keyed by the
    /// edge's two global vertex ids. The result is the union of subdivision points contributed by
    /// either adjacent face, so both faces can conform to it identically.
    /// </summary>
    private static Dictionary<(int, int), List<Point2D>> BuildSharedEdgeRegistry(
        FaceData[] faceData,
        FaceCutData?[] faceCuts,
        double tolerance)
    {
        var registry = new Dictionary<(int, int), List<Point2D>>();
        double toleranceSquared = tolerance * tolerance;

        for (int faceIndex = 0; faceIndex < faceData.Length; faceIndex++)
        {
            var cuts = faceCuts[faceIndex];
            if (cuts == null || cuts.EdgePoints.Count == 0)
                continue;

            FaceData face = faceData[faceIndex];
            foreach (var edgePoint in cuts.EdgePoints)
            {
                // Endpoints that coincide with a triangle vertex never subdivide the edge.
                if (face.IsNearVertex(edgePoint.Point, tolerance))
                    continue;

                (int, int) key = EdgeKey(face, edgePoint.EdgeIndex);
                if (!registry.TryGetValue(key, out var list))
                {
                    list = new List<Point2D>(4);
                    registry[key] = list;
                }

                bool duplicate = false;
                for (int i = 0; i < list.Count; i++)
                {
                    if (DistanceSquared(list[i], edgePoint.Point) <= toleranceSquared)
                    {
                        duplicate = true;
                        break;
                    }
                }

                if (!duplicate)
                    list.Add(edgePoint.Point);
            }
        }

        return registry;
    }

    private static bool HasRegistryEdgePoints(FaceData face, Dictionary<(int, int), List<Point2D>> registry)
    {
        for (int edgeIndex = 0; edgeIndex < 3; edgeIndex++)
        {
            if (registry.TryGetValue(EdgeKey(face, edgeIndex), out var list) && list.Count > 0)
                return true;
        }

        return false;
    }

    private static FaceData[] BuildFaceData(double[] vertices, int[] faces, int faceCount)
    {
        var result = new FaceData[faceCount];
        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            int i0 = faces[faceIndex * 3];
            int i1 = faces[faceIndex * 3 + 1];
            int i2 = faces[faceIndex * 3 + 2];
            var a = new Point2D(vertices[i0 * 3], vertices[i0 * 3 + 1]);
            var b = new Point2D(vertices[i1 * 3], vertices[i1 * 3 + 1]);
            var c = new Point2D(vertices[i2 * 3], vertices[i2 * 3 + 1]);

            result[faceIndex] = new FaceData
            {
                I0 = i0,
                I1 = i1,
                I2 = i2,
                A = a,
                B = b,
                C = c,
                Az = vertices[i0 * 3 + 2],
                Bz = vertices[i1 * 3 + 2],
                Cz = vertices[i2 * 3 + 2],
                Bounds = new Bounds2D(
                    Math.Min(a.X, Math.Min(b.X, c.X)),
                    Math.Max(a.X, Math.Max(b.X, c.X)),
                    Math.Min(a.Y, Math.Min(b.Y, c.Y)),
                    Math.Max(a.Y, Math.Max(b.Y, c.Y)))
            };
        }

        return result;
    }

    private static List<BoundarySegment> BuildBoundarySegments(MeshAreaSplitter.AreaBoundary[] areas, double tolerance)
    {
        var sourceSegments = new List<BoundarySegment>();
        for (int areaIndex = 0; areaIndex < areas.Length; areaIndex++)
        {
            var area = areas[areaIndex];
            for (int vertexIndex = 0; vertexIndex < area.VertexCount; vertexIndex++)
            {
                int next = (vertexIndex + 1) % area.VertexCount;
                var start = new Point2D(area.XyVertices[vertexIndex * 2], area.XyVertices[vertexIndex * 2 + 1]);
                var end = new Point2D(area.XyVertices[next * 2], area.XyVertices[next * 2 + 1]);
                if (DistanceSquared(start, end) <= tolerance * tolerance)
                    continue;

                sourceSegments.Add(new BoundarySegment(start, end));
            }
        }

        if (sourceSegments.Count == 0)
            return sourceSegments;

        var splitParameters = new List<double>[sourceSegments.Count];
        var segmentBounds = new Bounds2D[sourceSegments.Count];
        for (int i = 0; i < sourceSegments.Count; i++)
        {
            splitParameters[i] = new List<double>(4) { 0.0, 1.0 };
            var segment = sourceSegments[i];
            segmentBounds[i] = new Bounds2D(
                Math.Min(segment.Start.X, segment.End.X),
                Math.Max(segment.Start.X, segment.End.X),
                Math.Min(segment.Start.Y, segment.End.Y),
                Math.Max(segment.Start.Y, segment.End.Y));
        }

        for (int i = 0; i < sourceSegments.Count; i++)
        {
            for (int j = i + 1; j < sourceSegments.Count; j++)
            {
                if (!segmentBounds[i].Intersects(segmentBounds[j]))
                    continue;

                var intersection = IntersectSegments(sourceSegments[i].Start, sourceSegments[i].End, sourceSegments[j].Start, sourceSegments[j].End, tolerance);
                if (intersection.Kind == SegmentIntersectionKind.None)
                    continue;

                AddSplitParameter(splitParameters[i], intersection.T0);
                AddSplitParameter(splitParameters[i], intersection.T1);

                var reverse = IntersectSegments(sourceSegments[j].Start, sourceSegments[j].End, sourceSegments[i].Start, sourceSegments[i].End, tolerance);
                AddSplitParameter(splitParameters[j], reverse.T0);
                AddSplitParameter(splitParameters[j], reverse.T1);
            }
        }

        var dedupedSegments = new List<BoundarySegment>();
        double inverseTolerance = 1.0 / tolerance;
        var seen = new HashSet<(long, long, long, long)>();

        for (int segmentIndex = 0; segmentIndex < sourceSegments.Count; segmentIndex++)
        {
            var parameters = splitParameters[segmentIndex];
            parameters.Sort();

            int uniqueCount = 0;
            for (int i = 0; i < parameters.Count; i++)
            {
                double value = Math.Clamp(parameters[i], 0.0, 1.0);
                if (uniqueCount > 0 && Math.Abs(value - parameters[uniqueCount - 1]) <= 1e-9)
                    continue;

                parameters[uniqueCount++] = value;
            }

            var source = sourceSegments[segmentIndex];
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

                var key = CreateSegmentKey(start, end, inverseTolerance);
                if (!seen.Add(key))
                    continue;

                dedupedSegments.Add(new BoundarySegment(start, end));
            }
        }

        return dedupedSegments;
    }

    private static FaceCutData[] MapBoundarySegmentsToFaces(
        FaceData[] faceData,
        List<BoundarySegment> boundarySegments,
        double tolerance)
    {
        var faceBounds = new Bounds2D[faceData.Length];
        for (int i = 0; i < faceData.Length; i++)
            faceBounds[i] = faceData[i].Bounds;

        var grid = SpatialHashGrid2D.Build(faceBounds);
        var scratch = new SpatialHashGrid2D.QueryScratch(faceData.Length);
        var candidates = new List<int>(16);
        var result = new FaceCutData[faceData.Length];

        // Cut endpoints within this distance of a terrain edge are projected onto it so they conform
        // (see the snap rationale in the clipped-piece loop). Several times the model tolerance — large
        // enough to absorb near-edge cut points, far below terrain detail.
        double edgeSnapToleranceSquared = (tolerance * 8.0) * (tolerance * 8.0);

        foreach (var segment in boundarySegments)
        {
            var queryBounds = new Bounds2D(
                Math.Min(segment.Start.X, segment.End.X) - tolerance,
                Math.Max(segment.Start.X, segment.End.X) + tolerance,
                Math.Min(segment.Start.Y, segment.End.Y) - tolerance,
                Math.Max(segment.Start.Y, segment.End.Y) + tolerance);

            grid.GatherCandidates(queryBounds, candidates, scratch);
            foreach (int faceIndex in candidates)
            {
                var face = faceData[faceIndex];
                if (!face.Bounds.Intersects(queryBounds))
                    continue;

                var edgePoints = CollectSegmentEdgeTouchPoints(face, segment, tolerance);
                var clippedPieces = ClipSegmentToTriangle(face, segment, tolerance);
                if (edgePoints.Count == 0 && clippedPieces.Count == 0)
                    continue;

                result[faceIndex] ??= new FaceCutData();
                foreach (var edgePoint in edgePoints)
                    AddUniqueEdgePoint(result[faceIndex].EdgePoints, edgePoint, face, tolerance);

                foreach (var rawPiece in clippedPieces)
                {
                    // Conform a cut endpoint that lands NEAR (but not within the model tolerance of) a
                    // terrain edge onto that edge. Such a point is otherwise kept interior, and because
                    // each adjacent face detects it at a slightly different spot, they emit overlapping
                    // sliver triangles along the shared edge — one non-manifold edge in the conformed
                    // terrain. Projecting onto the edge makes the point register in the shared-edge
                    // registry, so BOTH faces subdivide the edge identically (no sliver). Projection onto
                    // a shared edge is position-identical from either side, so it is consistent by
                    // construction. The snap radius is several times the model tolerance but far below
                    // terrain detail, so the carve boundary moves negligibly.
                    var clippedPiece = new SegmentPiece(
                        SnapPointToNearEdge(face, rawPiece.Start, edgeSnapToleranceSquared),
                        SnapPointToNearEdge(face, rawPiece.End, edgeSnapToleranceSquared));

                    int edgeIndex = GetPieceEdgeIndex(face, clippedPiece, tolerance);
                    if (edgeIndex >= 0)
                    {
                        AddUniqueEdgePoint(result[faceIndex].EdgePoints, new EdgePoint(edgeIndex, clippedPiece.Start), face, tolerance);
                        AddUniqueEdgePoint(result[faceIndex].EdgePoints, new EdgePoint(edgeIndex, clippedPiece.End), face, tolerance);
                        continue;
                    }

                    AddUniqueSegment(result[faceIndex].InternalSegments, clippedPiece, tolerance);
                    AddPieceEndpointEdgePoints(result[faceIndex].EdgePoints, face, clippedPiece, tolerance);
                }
            }
        }

        return result;
    }

    private static bool TriangulateTouchedFace(
        FaceData face,
        FaceCutData? cutData,
        Dictionary<(int, int), List<Point2D>> sharedEdgePoints,
        GlobalPointLookup pointLookup,
        List<int> globalFaces,
        double tolerance,
        out string? errorMessage)
    {
        errorMessage = null;

        var localPoints = new LocalPointBuilder(tolerance);
        int a = localPoints.Add(face.A, face.Az);
        int b = localPoints.Add(face.B, face.Bz);
        int c = localPoints.Add(face.C, face.Cz);

        // Snap a cut point that lands very close to a triangle CORNER onto that corner. A daylight-loop
        // segment that grazes near an existing terrain vertex otherwise leaves a cut point a hair off
        // the corner (just beyond the model tolerance), and each adjacent face places its own slightly
        // different near-corner point — producing two overlapping sliver triangles, i.e. a non-manifold
        // edge in the conformed terrain. Snapping to the shared corner is consistent across both faces
        // by construction (same global vertex), so the slivers collapse to degenerate and drop out. The
        // snap radius is several times the model tolerance but far below terrain detail, so the carve
        // boundary moves negligibly.
        double cornerSnapTolSq = (tolerance * 8.0) * (tolerance * 8.0);
        Point2D SnapToCorner(Point2D p)
        {
            double da = DistanceSquared(p, face.A);
            double db = DistanceSquared(p, face.B);
            double dc = DistanceSquared(p, face.C);
            double best = Math.Min(da, Math.Min(db, dc));
            if (best > cornerSnapTolSq)
                return p;

            return best == da ? face.A : (best == db ? face.B : face.C);
        }

        var edgePointLists = new List<(double Parameter, int LocalIndex)>[3];
        for (int edgeIndex = 0; edgeIndex < 3; edgeIndex++)
            edgePointLists[edgeIndex] = new List<(double Parameter, int LocalIndex)>(4);

        edgePointLists[0].Add((0.0, a));
        edgePointLists[0].Add((1.0, b));
        edgePointLists[1].Add((0.0, b));
        edgePointLists[1].Add((1.0, c));
        edgePointLists[2].Add((0.0, c));
        edgePointLists[2].Add((1.0, a));

        // Seed edge subdivision points from the shared-edge registry rather than this face's own
        // detected points. The registry is the union of both adjacent faces' cut points on each
        // edge, so both faces subdivide their shared edge identically — no T-junction.
        for (int edgeIndex = 0; edgeIndex < 3; edgeIndex++)
        {
            if (!sharedEdgePoints.TryGetValue(EdgeKey(face, edgeIndex), out var points))
                continue;

            foreach (Point2D rawPoint in points)
            {
                Point2D point = SnapToCorner(rawPoint);

                // A point that snapped to (or already coincides with) a corner does not subdivide the
                // edge — the corner is already a triangle vertex.
                if (face.IsNearVertex(point, tolerance))
                    continue;

                int localIndex = localPoints.Add(point, face.InterpolateZ(point));
                double parameter = ParameterOnEdge(face.GetEdgeStart(edgeIndex), face.GetEdgeEnd(edgeIndex), point);
                edgePointLists[edgeIndex].Add((parameter, localIndex));
            }
        }

        var segments = new List<(int a, int b)>();
        var segmentKeys = new HashSet<long>();

        if (cutData != null)
        {
            foreach (var piece in cutData.InternalSegments)
            {
                Point2D pieceStart = SnapToCorner(piece.Start);
                Point2D pieceEnd = SnapToCorner(piece.End);
                int start = localPoints.Add(pieceStart, face.InterpolateZ(pieceStart));
                int end = localPoints.Add(pieceEnd, face.InterpolateZ(pieceEnd));
                if (start == end)
                    continue;

                MeshConstraintTools.TryAddSegment(segments, segmentKeys, start, end);
            }
        }

        for (int edgeIndex = 0; edgeIndex < 3; edgeIndex++)
        {
            var points = edgePointLists[edgeIndex];
            points.Sort((left, right) => left.Parameter.CompareTo(right.Parameter));

            int writeIndex = 0;
            for (int i = 0; i < points.Count; i++)
            {
                var current = points[i];
                if (writeIndex > 0)
                {
                    var previous = points[writeIndex - 1];
                    if (current.LocalIndex == previous.LocalIndex || Math.Abs(current.Parameter - previous.Parameter) <= 1e-9)
                        continue;
                }

                points[writeIndex++] = current;
            }

            for (int i = 0; i < writeIndex - 1; i++)
            {
                int start = points[i].LocalIndex;
                int end = points[i + 1].LocalIndex;
                if (start == end)
                    continue;

                MeshConstraintTools.TryAddSegment(segments, segmentKeys, start, end);
            }
        }

        var outcome = TriangulationHelper.Triangulate(localPoints.Xy, localPoints.Count, segments, 0, 0, convex: true, segmentSplitting: 0);
        if (outcome.Mesh == null || MeshConstraintTools.ConstraintsWereDropped(outcome.Flags))
        {
            errorMessage = outcome.WarningMessage ?? "Topology-preserving zone split failed.";
            return false;
        }

        var extracted = TriangleNetExtractor.Extract(outcome.Mesh);
        var extractedToGlobal = new int[extracted.VertexCount];
        for (int i = 0; i < extracted.VertexCount; i++)
        {
            var point = new Point2D(extracted.Xy[i * 2], extracted.Xy[i * 2 + 1]);
            extractedToGlobal[i] = pointLookup.Resolve(point, face.InterpolateZ(point));
        }

        for (int faceIndex = 0; faceIndex < extracted.FaceCount; faceIndex++)
        {
            int vertex0 = extracted.Faces[faceIndex * 3];
            int vertex1 = extracted.Faces[faceIndex * 3 + 1];
            int vertex2 = extracted.Faces[faceIndex * 3 + 2];
            var p0 = new Point2D(extracted.Xy[vertex0 * 2], extracted.Xy[vertex0 * 2 + 1]);
            var p1 = new Point2D(extracted.Xy[vertex1 * 2], extracted.Xy[vertex1 * 2 + 1]);
            var p2 = new Point2D(extracted.Xy[vertex2 * 2], extracted.Xy[vertex2 * 2 + 1]);
            var centroid = new Point2D((p0.X + p1.X + p2.X) / 3.0, (p0.Y + p1.Y + p2.Y) / 3.0);
            if (!face.ContainsPoint(centroid, tolerance * 8.0))
                continue;

            int g0 = extractedToGlobal[vertex0];
            int g1 = extractedToGlobal[vertex1];
            int g2 = extractedToGlobal[vertex2];
            if (g0 == g1 || g1 == g2 || g2 == g0)
                continue;

            double cross = Math.Abs((p1.X - p0.X) * (p2.Y - p0.Y) - (p1.Y - p0.Y) * (p2.X - p0.X));
            if (cross < tolerance * tolerance * 1e-3)
                continue;

            globalFaces.Add(g0);
            globalFaces.Add(g1);
            globalFaces.Add(g2);
        }

        return true;
    }

    private static List<EdgePoint> CollectSegmentEdgeTouchPoints(FaceData face, BoundarySegment segment, double tolerance)
    {
        var result = new List<EdgePoint>(4);
        for (int edgeIndex = 0; edgeIndex < 3; edgeIndex++)
        {
            var edgeStart = face.GetEdgeStart(edgeIndex);
            var edgeEnd = face.GetEdgeEnd(edgeIndex);
            var intersection = IntersectSegments(segment.Start, segment.End, edgeStart, edgeEnd, tolerance);
            if (intersection.Kind == SegmentIntersectionKind.None)
                continue;

            AddEdgeTouchPoint(result, face, edgeIndex, intersection.P0, tolerance);
            if (intersection.Kind == SegmentIntersectionKind.Overlap)
                AddEdgeTouchPoint(result, face, edgeIndex, intersection.P1, tolerance);
        }

        if (face.ContainsPoint(segment.Start, tolerance))
        {
            int edgeIndex = face.GetEdgeIndex(segment.Start, tolerance);
            if (edgeIndex >= 0)
                AddEdgeTouchPoint(result, face, edgeIndex, segment.Start, tolerance);
        }

        if (face.ContainsPoint(segment.End, tolerance))
        {
            int edgeIndex = face.GetEdgeIndex(segment.End, tolerance);
            if (edgeIndex >= 0)
                AddEdgeTouchPoint(result, face, edgeIndex, segment.End, tolerance);
        }

        return result;
    }

    private static List<SegmentPiece> ClipSegmentToTriangle(FaceData face, BoundarySegment segment, double tolerance)
    {
        var parameters = new List<double>(8);
        if (face.ContainsPoint(segment.Start, tolerance))
            parameters.Add(0.0);
        if (face.ContainsPoint(segment.End, tolerance))
            parameters.Add(1.0);

        for (int edgeIndex = 0; edgeIndex < 3; edgeIndex++)
        {
            var intersection = IntersectSegments(segment.Start, segment.End, face.GetEdgeStart(edgeIndex), face.GetEdgeEnd(edgeIndex), tolerance);
            if (intersection.Kind == SegmentIntersectionKind.None)
                continue;

            parameters.Add(intersection.T0);
            parameters.Add(intersection.T1);
        }

        if (parameters.Count == 0)
            return new List<SegmentPiece>();

        parameters.Sort();
        int uniqueCount = 0;
        for (int i = 0; i < parameters.Count; i++)
        {
            double value = Math.Clamp(parameters[i], 0.0, 1.0);
            if (uniqueCount > 0 && Math.Abs(value - parameters[uniqueCount - 1]) <= 1e-9)
                continue;

            parameters[uniqueCount++] = value;
        }

        var result = new List<SegmentPiece>(2);
        for (int i = 0; i < uniqueCount - 1; i++)
        {
            double t0 = parameters[i];
            double t1 = parameters[i + 1];
            if (t1 - t0 <= 1e-9)
                continue;

            double midpointT = (t0 + t1) * 0.5;
            var midpoint = Lerp(segment.Start, segment.End, midpointT);
            if (!face.ContainsPoint(midpoint, tolerance))
                continue;

            var start = SnapPointToTriangle(face, Lerp(segment.Start, segment.End, t0), tolerance);
            var end = SnapPointToTriangle(face, Lerp(segment.Start, segment.End, t1), tolerance);
            if (DistanceSquared(start, end) <= tolerance * tolerance)
                continue;

            result.Add(new SegmentPiece(start, end));
        }

        return result;
    }

    private static int GetPieceEdgeIndex(FaceData face, SegmentPiece piece, double tolerance)
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

    private static void AddPieceEndpointEdgePoints(List<EdgePoint> edgePoints, FaceData face, SegmentPiece piece, double tolerance)
    {
        int startEdge = face.GetEdgeIndex(piece.Start, tolerance);
        if (startEdge >= 0)
            AddUniqueEdgePoint(edgePoints, new EdgePoint(startEdge, piece.Start), face, tolerance);

        int endEdge = face.GetEdgeIndex(piece.End, tolerance);
        if (endEdge >= 0)
            AddUniqueEdgePoint(edgePoints, new EdgePoint(endEdge, piece.End), face, tolerance);
    }

    private static void AddEdgeTouchPoint(List<EdgePoint> destination, FaceData face, int edgeIndex, Point2D point, double tolerance)
    {
        var snapped = SnapPointToEdge(face.GetEdgeStart(edgeIndex), face.GetEdgeEnd(edgeIndex), point);
        if (face.IsNearVertex(snapped, tolerance))
            return;

        AddUniqueEdgePoint(destination, new EdgePoint(edgeIndex, snapped), face, tolerance);
    }

    private static void AddUniqueEdgePoint(List<EdgePoint> destination, EdgePoint candidate, FaceData face, double tolerance)
    {
        double toleranceSquared = tolerance * tolerance;
        double vertexToleranceSquared = 4.0 * toleranceSquared;
        for (int i = 0; i < destination.Count; i++)
        {
            var existing = destination[i];
            if (DistanceSquared(existing.Point, candidate.Point) > toleranceSquared)
                continue;

            if (existing.EdgeIndex == candidate.EdgeIndex)
                return;

            for (int vertexIndex = 0; vertexIndex < 3; vertexIndex++)
            {
                var vertex = face.GetVertex(vertexIndex);
                if (DistanceSquared(existing.Point, vertex) <= vertexToleranceSquared &&
                    DistanceSquared(candidate.Point, vertex) <= vertexToleranceSquared)
                {
                    return;
                }
            }
        }

        destination.Add(candidate);
    }

    private static void AddUniqueSegment(List<SegmentPiece> destination, SegmentPiece candidate, double tolerance)
    {
        double toleranceSquared = tolerance * tolerance;
        for (int i = 0; i < destination.Count; i++)
        {
            var existing = destination[i];
            bool sameDirection = DistanceSquared(existing.Start, candidate.Start) <= toleranceSquared &&
                                 DistanceSquared(existing.End, candidate.End) <= toleranceSquared;
            bool reverseDirection = DistanceSquared(existing.Start, candidate.End) <= toleranceSquared &&
                                    DistanceSquared(existing.End, candidate.Start) <= toleranceSquared;
            if (sameDirection || reverseDirection)
                return;
        }

        destination.Add(candidate);
    }

    private static SegmentIntersection IntersectSegments(Point2D a0, Point2D a1, Point2D b0, Point2D b1, double tolerance)
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
            {
                return new SegmentIntersection
                {
                    Kind = SegmentIntersectionKind.None,
                    T0 = 0.0,
                    T1 = 0.0,
                    P0 = a0,
                    P1 = a0
                };
            }

            var start = Lerp(a0, a1, minT);
            var end = Lerp(a0, a1, maxT);
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
        {
            return new SegmentIntersection
            {
                Kind = SegmentIntersectionKind.None,
                T0 = 0.0,
                T1 = 0.0,
                P0 = a0,
                P1 = a0
            };
        }

        double t = Cross(qpx, qpy, sx, sy) / rxs;
        double u = Cross(qpx, qpy, rx, ry) / rxs;
        if (t < -1e-9 || t > 1.0 + 1e-9 || u < -1e-9 || u > 1.0 + 1e-9)
        {
            return new SegmentIntersection
            {
                Kind = SegmentIntersectionKind.None,
                T0 = 0.0,
                T1 = 0.0,
                P0 = a0,
                P1 = a0
            };
        }

        var intersectionPoint = Lerp(a0, a1, Math.Clamp(t, 0.0, 1.0));
        return new SegmentIntersection
        {
            Kind = SegmentIntersectionKind.Point,
            T0 = Math.Clamp(t, 0.0, 1.0),
            T1 = Math.Clamp(t, 0.0, 1.0),
            P0 = intersectionPoint,
            P1 = intersectionPoint
        };
    }

    private static void AddSplitParameter(List<double> parameters, double value)
    {
        value = Math.Clamp(value, 0.0, 1.0);
        for (int i = 0; i < parameters.Count; i++)
        {
            if (Math.Abs(parameters[i] - value) <= 1e-9)
                return;
        }

        parameters.Add(value);
    }

    private static (long, long, long, long) CreateSegmentKey(Point2D start, Point2D end, double inverseTolerance)
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

    private static Point2D SnapPointToTriangle(FaceData face, Point2D point, double tolerance)
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

    private static Point2D SnapPointToEdge(Point2D edgeStart, Point2D edgeEnd, Point2D point)
    {
        double t = ParameterOnEdge(edgeStart, edgeEnd, point);
        return Lerp(edgeStart, edgeEnd, t);
    }

    /// <summary>
    /// Projects <paramref name="point"/> onto the nearest of the face's three edges if it lies within
    /// the (squared) snap radius; otherwise returns it unchanged. Used to conform near-edge cut points
    /// onto the terrain edge so adjacent faces subdivide it identically.
    /// </summary>
    private static Point2D SnapPointToNearEdge(FaceData face, Point2D point, double snapToleranceSquared)
    {
        double bestDistanceSquared = snapToleranceSquared;
        Point2D best = point;
        for (int edgeIndex = 0; edgeIndex < 3; edgeIndex++)
        {
            Point2D projected = SnapPointToEdge(face.GetEdgeStart(edgeIndex), face.GetEdgeEnd(edgeIndex), point);
            double distanceSquared = DistanceSquared(point, projected);
            if (distanceSquared < bestDistanceSquared)
            {
                bestDistanceSquared = distanceSquared;
                best = projected;
            }
        }

        return best;
    }

    private static double ParameterOnSegment(Point2D start, Point2D end, Point2D point)
    {
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared <= 1e-20)
            return 0.0;

        return (((point.X - start.X) * dx) + ((point.Y - start.Y) * dy)) / lengthSquared;
    }

    private static double ParameterOnEdge(Point2D start, Point2D end, Point2D point)
    {
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared <= 1e-20)
            return 0.0;

        return Math.Clamp((((point.X - start.X) * dx) + ((point.Y - start.Y) * dy)) / lengthSquared, 0.0, 1.0);
    }

    private static bool PointOnSegment(Point2D point, Point2D start, Point2D end, double tolerance)
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

    private static Point2D Lerp(Point2D start, Point2D end, double t)
    {
        return new Point2D(
            start.X + ((end.X - start.X) * t),
            start.Y + ((end.Y - start.Y) * t));
    }

    private static double Cross(double ax, double ay, double bx, double by) => (ax * by) - (ay * bx);

    private static double DistanceSquared(Point2D a, Point2D b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return (dx * dx) + (dy * dy);
    }

    private static long PackKey(long cellX, long cellY) => (cellX * 0x100000001L) ^ (cellY * 0x27d4eb2dL);
}
