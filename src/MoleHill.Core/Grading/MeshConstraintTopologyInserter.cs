using MoleHill.Core.Engine;
using static MoleHill.Core.Grading.FaceCutGeometry;

namespace MoleHill.Core.Grading;

/// <summary>
/// Inserts constraint segments into existing terrain faces with topology-preserving local triangulation.
/// The face-cutting geometry is shared with <see cref="MeshAreaTopologySplitter"/> through
/// <see cref="FaceCutGeometry"/>; this class owns the line-insertion policy: same-edge-only edge-point
/// merging, no conform snapping, the face-by-face consistency check and its local-triangulation fallback.
/// </summary>
internal static partial class MeshConstraintTopologyInserter
{
    public static bool TryInsert(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        IReadOnlyList<ConstraintPolyline> constraints,
        double tolerance,
        out double[] outputVertices,
        out int outputVertexCount,
        out int[] outputFaces,
        out int outputFaceCount,
        out string? errorMessage) =>
        TryInsert(
            vertices, vertexCount, faces, faceCount, constraints, Array.Empty<double>(), tolerance,
            out outputVertices, out outputVertexCount, out outputFaces, out outputFaceCount, out _, out errorMessage);

    /// <summary>What happened to the isolated points handed to the point-aware <c>TryInsert</c>.</summary>
    internal readonly record struct PointPlacement(int Inserted, int OnExistingVertex, int OutsideMesh);

    /// <summary>
    /// Inserts constraint segments and isolated points (<paramref name="pointXy"/>, flat XY) into the
    /// existing faces. A point within tolerance of an existing vertex is not inserted (the vertex already
    /// carries the surface there); one on an edge splits that edge in both neighbours; one inside a face
    /// becomes a free vertex of that face's local triangulation. New vertices take the elevation of the
    /// face they land in — the caller assigns data elevations afterwards.
    /// </summary>
    internal static bool TryInsert(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        IReadOnlyList<ConstraintPolyline> constraints,
        double[] pointXy,
        double tolerance,
        out double[] outputVertices,
        out int outputVertexCount,
        out int[] outputFaces,
        out int outputFaceCount,
        out PointPlacement pointPlacement,
        out string? errorMessage)
    {
        errorMessage = null;
        outputVertexCount = vertexCount;
        outputFaceCount = faceCount;
        pointPlacement = default;

        if (constraints.Count == 0 && pointXy.Length == 0)
        {
            CloneInput(vertices, faces, out outputVertices, out outputFaces);
            return true;
        }

        if (vertexCount == 0 || faceCount == 0)
        {
            CloneInput(vertices, faces, out outputVertices, out outputFaces);
            errorMessage = "Input mesh has no usable triangles.";
            return false;
        }

        double resolvedTolerance = Math.Max(tolerance, 1e-9);
        List<CutSegment> segments = BuildConstraintSegments(constraints, resolvedTolerance);
        if (segments.Count == 0 && pointXy.Length == 0)
        {
            CloneInput(vertices, faces, out outputVertices, out outputFaces);
            return true;
        }

        FaceCutData[] faceCuts = MapConstraintSegmentsToFaces(
            vertices, faces, faceCount, segments, resolvedTolerance, pointXy, out pointPlacement);
        int touchedFaceCount = 0;
        for (int i = 0; i < faceCuts.Length; i++)
        {
            if (faceCuts[i]?.HasData == true)
                touchedFaceCount++;
        }

        if (touchedFaceCount == 0)
        {
            CloneInput(vertices, faces, out outputVertices, out outputFaces);
            return true;
        }

        var globalVertices = new List<double>(vertices);

        // Untouched faces are copied verbatim; only the touched ones fan out. Reserving twice the whole
        // input face array costs hundreds of megabytes on a multi-million-face terrain for a few cuts.
        var globalFaces = new List<int>(EstimateOutputFaceCapacity(faceCount, touchedFaceCount));
        var pointLookup = new GlobalPointLookup(globalVertices, resolvedTolerance);
        var edgeSplits = new MeshEdgeSplitRegistry(globalVertices, pointLookup.Append, resolvedTolerance);
        var touchedFaceRanges = new List<(int Start, int End)>(touchedFaceCount);
        double touchedArea = 0.0;

        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            FaceCutData? cutData = faceCuts[faceIndex];
            if (cutData == null || !cutData.HasData)
            {
                globalFaces.Add(faces[faceIndex * 3]);
                globalFaces.Add(faces[(faceIndex * 3) + 1]);
                globalFaces.Add(faces[(faceIndex * 3) + 2]);
                continue;
            }

            var face = new FaceData(vertices, faces, faceIndex);
            touchedArea += Math.Abs(Cross(face.B.X - face.A.X, face.B.Y - face.A.Y, face.C.X - face.A.X, face.C.Y - face.A.Y));
            int start = globalFaces.Count / 3;
            if (!TriangulateTouchedFace(face, cutData, pointLookup, edgeSplits, globalFaces, resolvedTolerance, out errorMessage))
            {
                CloneInput(vertices, faces, out outputVertices, out outputFaces);
                return false;
            }

            touchedFaceRanges.Add((start, globalFaces.Count / 3));
        }

        if (!IsFaceByFaceResultConsistent(
                faces, faceCount, globalVertices, globalFaces, touchedFaceRanges, touchedArea, edgeSplits, out string? inconsistency))
        {
            // Each crossed face was triangulated on its own, so neighbours must agree about where their
            // shared edge was cut. On a terrain of slivers (contour bands, a split point a few millimetres off
            // an edge within tolerance) they can disagree: one face takes the point as on the edge, the other
            // as inside, and the two overlap. That is a folded mesh, never an answer — re-triangulate the
            // neighbourhood as one piece instead, which has no shared edge to disagree about, or decline.
            string? localError = "it cannot place isolated points";
            if (pointXy.Length == 0 &&
                TryInsertByLocalTriangulation(
                    vertices, vertexCount, faces, faceCount, constraints, tolerance,
                    out outputVertices, out outputVertexCount, out outputFaces, out outputFaceCount, out localError))
            {
                return true;
            }

            CloneInput(vertices, faces, out outputVertices, out outputFaces);
            outputVertexCount = vertexCount;
            outputFaceCount = faceCount;
            errorMessage = $"Face-by-face insertion produced an inconsistent mesh ({inconsistency}), and re-triangulating " +
                           $"the neighbourhood as one piece declined ({localError}).";
            return false;
        }

        outputVertices = globalVertices.ToArray();
        outputVertexCount = outputVertices.Length / 3;
        outputFaces = globalFaces.ToArray();
        outputFaceCount = outputFaces.Length / 3;
        return true;
    }

    /// <summary>
    /// Whether the re-triangulated faces exactly tile the faces they replaced: the same plan area (an overlap
    /// adds area, a gap removes it), no edge among them carries more than two faces, and every edge only one
    /// face uses is the terrain border - an input border edge, or a piece of one split on that edge. Any
    /// other single-use edge is a cut one side of an edge made and the other did not: a slit.
    /// </summary>
    private static bool IsFaceByFaceResultConsistent(
        int[] inputFaces,
        int inputFaceCount,
        List<double> vertices,
        List<int> faces,
        List<(int Start, int End)> touchedFaceRanges,
        double touchedArea,
        MeshEdgeSplitRegistry edgeSplits,
        out string? inconsistency)
    {
        inconsistency = null;
        int faceCount = faces.Count / 3;
        double emittedArea = 0.0;
        // Flat marks, not a hash set: every face of the terrain is tested against them, twice.
        var nearTouched = new bool[vertices.Count / 3];
        int nearTouchedCount = 0;
        foreach ((int start, int end) in touchedFaceRanges)
        {
            for (int f = start; f < end; f++)
            {
                int a = faces[f * 3], b = faces[(f * 3) + 1], c = faces[(f * 3) + 2];
                double ax = vertices[a * 3], ay = vertices[(a * 3) + 1];
                emittedArea += Math.Abs(Cross(
                    vertices[b * 3] - ax, vertices[(b * 3) + 1] - ay,
                    vertices[c * 3] - ax, vertices[(c * 3) + 1] - ay));
                foreach (int x in (ReadOnlySpan<int>)[a, b, c])
                {
                    if (!nearTouched[x])
                    {
                        nearTouched[x] = true;
                        nearTouchedCount++;
                    }
                }
            }
        }

        if (!double.IsFinite(emittedArea) || Math.Abs(emittedArea - touchedArea) > Math.Max(touchedArea, 1e-12) * 1e-7)
        {
            inconsistency = $"plan area {emittedArea * 0.5:G6} where the crossed faces cover {touchedArea * 0.5:G6}";
            return false;
        }

        Dictionary<long, int> edgeUses = IndexedMeshTools.CreateEdgeKeyMap<int>(nearTouchedCount * 6);
        for (int f = 0; f < faceCount; f++)
        {
            int a = faces[f * 3], b = faces[(f * 3) + 1], c = faces[(f * 3) + 2];
            if (!nearTouched[a] && !nearTouched[b] && !nearTouched[c])
                continue;

            for (int k = 0; k < 3; k++)
            {
                int u = faces[(f * 3) + k], w = faces[(f * 3) + ((k + 1) % 3)];
                long key = IndexedMeshTools.GetEdgeKey(u, w);
                int uses = edgeUses.GetValueOrDefault(key) + 1;
                if (uses > 2)
                {
                    inconsistency = $"edge {u}-{w} shared by more than two faces";
                    return false;
                }

                edgeUses[key] = uses;
            }
        }

        // Input border edges around the change. Every face on a vertex of the change is visited, so an edge
        // with such an endpoint has its full use count in both maps.
        Dictionary<long, int> inputUses = IndexedMeshTools.CreateEdgeKeyMap<int>(nearTouchedCount * 6);
        for (int f = 0; f < inputFaceCount; f++)
        {
            int a = inputFaces[f * 3], b = inputFaces[(f * 3) + 1], c = inputFaces[(f * 3) + 2];
            if (!nearTouched[a] && !nearTouched[b] && !nearTouched[c])
                continue;

            for (int k = 0; k < 3; k++)
            {
                long key = IndexedMeshTools.GetEdgeKey(inputFaces[(f * 3) + k], inputFaces[(f * 3) + ((k + 1) % 3)]);
                inputUses[key] = inputUses.GetValueOrDefault(key) + 1;
            }
        }

        foreach ((long key, int uses) in edgeUses)
        {
            if (uses != 1)
                continue;

            int u = (int)(key >> 32), w = (int)(key & 0xffffffff);
            if (!nearTouched[u] && !nearTouched[w])
                continue;

            if (!IsOnInputBorder(u, w, inputUses, edgeSplits))
            {
                inconsistency = $"edge {u}-{w} used by one face away from the terrain border";
                return false;
            }
        }

        return true;
    }

    private static bool IsOnInputBorder(int u, int w, Dictionary<long, int> inputUses, MeshEdgeSplitRegistry edgeSplits)
    {
        if (inputUses.TryGetValue(IndexedMeshTools.GetEdgeKey(u, w), out int uses))
            return uses == 1;

        // A sub-edge of a split border edge: each end is either an end of the host edge or a split placed on it.
        long host = -1;
        foreach (int end in new[] { u, w })
        {
            if (edgeSplits.TryGetHost(end, out long splitHost))
            {
                if (host >= 0 && host != splitHost)
                    return false;
                host = splitHost;
            }
        }

        if (host < 0)
            return false;

        int hostA = (int)(host >> 32), hostB = (int)(host & 0xffffffff);
        foreach (int end in new[] { u, w })
        {
            if (end != hostA && end != hostB && !(edgeSplits.TryGetHost(end, out long h) && h == host))
                return false;
        }

        return inputUses.TryGetValue(host, out int hostUses) && hostUses == 1;
    }

    private static void CloneInput(double[] vertices, int[] faces, out double[] outputVertices, out int[] outputFaces)
    {
        outputVertices = (double[])vertices.Clone();
        outputFaces = (int[])faces.Clone();
    }

    private static int EstimateOutputFaceCapacity(int faceCount, int touchedFaceCount)
    {
        // A cut face retriangulates into a handful of triangles. Overshooting only costs one growth
        // step; the untouched majority is copied one for one.
        long estimate = ((long)(faceCount - touchedFaceCount) * 3L) + ((long)touchedFaceCount * 18L);
        return (int)Math.Clamp(estimate, 3L, (long)int.MaxValue / 2L);
    }

    private static Bounds2D[] BuildFaceBounds(double[] vertices, int[] faces, int faceCount)
    {
        var bounds = new Bounds2D[faceCount];
        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            int i0 = faces[faceIndex * 3];
            int i1 = faces[(faceIndex * 3) + 1];
            int i2 = faces[(faceIndex * 3) + 2];
            double ax = vertices[i0 * 3];
            double ay = vertices[(i0 * 3) + 1];
            double bx = vertices[i1 * 3];
            double by = vertices[(i1 * 3) + 1];
            double cx = vertices[i2 * 3];
            double cy = vertices[(i2 * 3) + 1];
            bounds[faceIndex] = new Bounds2D(
                Math.Min(ax, Math.Min(bx, cx)),
                Math.Max(ax, Math.Max(bx, cx)),
                Math.Min(ay, Math.Min(by, cy)),
                Math.Max(ay, Math.Max(by, cy)));
        }

        return bounds;
    }

    private static List<CutSegment> BuildConstraintSegments(
        IReadOnlyList<ConstraintPolyline> constraints,
        double tolerance)
    {
        var sourceSegments = new List<CutSegment>();
        foreach (var constraint in constraints)
        {
            int pointCount = NormalizePointCount(constraint, tolerance);
            if (pointCount < 2)
                continue;

            for (int pointIndex = 0; pointIndex < pointCount - 1; pointIndex++)
                AddSourceSegment(sourceSegments, constraint, pointIndex, pointIndex + 1, tolerance);

            if (constraint.IsClosed)
                AddSourceSegment(sourceSegments, constraint, pointCount - 1, 0, tolerance);
        }

        return SplitAtMutualIntersections(sourceSegments, tolerance, CancellationProbe.None);
    }

    private static void AddSourceSegment(
        List<CutSegment> destination,
        ConstraintPolyline constraint,
        int startIndex,
        int endIndex,
        double tolerance)
    {
        var start = new Point2D(constraint.Points[startIndex * 3], constraint.Points[(startIndex * 3) + 1]);
        var end = new Point2D(constraint.Points[endIndex * 3], constraint.Points[(endIndex * 3) + 1]);
        if (DistanceSquared(start, end) <= tolerance * tolerance)
            return;

        destination.Add(new CutSegment(start, end));
    }

    private static int NormalizePointCount(ConstraintPolyline constraint, double tolerance)
    {
        if (!constraint.IsClosed || constraint.PointCount < 3)
            return constraint.PointCount;

        int last = constraint.PointCount - 1;
        double dx = constraint.Points[last * 3] - constraint.Points[0];
        double dy = constraint.Points[(last * 3) + 1] - constraint.Points[1];
        return (dx * dx) + (dy * dy) <= tolerance * tolerance
            ? last
            : constraint.PointCount;
    }

    private static FaceCutData[] MapConstraintSegmentsToFaces(
        double[] vertices,
        int[] faces,
        int faceCount,
        List<CutSegment> constraintSegments,
        double tolerance) =>
        MapConstraintSegmentsToFaces(vertices, faces, faceCount, constraintSegments, tolerance, Array.Empty<double>(), out _);

    private static FaceCutData[] MapConstraintSegmentsToFaces(
        double[] vertices,
        int[] faces,
        int faceCount,
        List<CutSegment> constraintSegments,
        double tolerance,
        double[] pointXy,
        out PointPlacement pointPlacement)
    {
        Bounds2D[] faceBounds = BuildFaceBounds(vertices, faces, faceCount);
        SpatialHashGrid2D grid = SpatialHashGrid2D.Build(faceBounds);
        var scratch = new SpatialHashGrid2D.QueryScratch(faceCount);
        var candidates = new List<int>(16);
        var result = new FaceCutData[faceCount];

        // Points go in before the segments' edge splits are reconciled, so a point that lands on an edge
        // is conformed into both neighbours by the same pass that conforms segment crossings.
        pointPlacement = PlacePoints(vertices, faces, pointXy, faceBounds, grid, scratch, candidates, result, tolerance);

        var edgePointBuffer = new EdgePoint[8];
        var parameterBuffer = new double[8];
        var clippedPieceBuffer = new SegmentPiece[7];

        foreach (CutSegment segment in constraintSegments)
        {
            var queryBounds = new Bounds2D(
                Math.Min(segment.Start.X, segment.End.X) - tolerance,
                Math.Max(segment.Start.X, segment.End.X) + tolerance,
                Math.Min(segment.Start.Y, segment.End.Y) - tolerance,
                Math.Max(segment.Start.Y, segment.End.Y) + tolerance);

            grid.GatherCandidates(queryBounds, candidates, scratch);
            foreach (int faceIndex in candidates)
            {
                if (!faceBounds[faceIndex].Intersects(queryBounds))
                    continue;

                var face = new FaceData(vertices, faces, faceIndex);
                AnalyzeSegmentAgainstFace(
                    face, segment, tolerance, EdgePointMerge.SameEdgeOnly,
                    edgePointBuffer, out int edgePointCount, parameterBuffer, clippedPieceBuffer, out int clippedPieceCount);
                if (edgePointCount == 0 && clippedPieceCount == 0)
                    continue;

                FaceCutData cuts = result[faceIndex] ??= new FaceCutData();
                for (int i = 0; i < edgePointCount; i++)
                    AddUniqueEdgePoint(cuts.EdgePoints, edgePointBuffer[i], face, tolerance, EdgePointMerge.SameEdgeOnly);

                for (int i = 0; i < clippedPieceCount; i++)
                {
                    SegmentPiece clippedPiece = clippedPieceBuffer[i];
                    int edgeIndex = GetPieceEdgeIndex(face, clippedPiece, tolerance);
                    if (edgeIndex >= 0)
                    {
                        AddUniqueEdgePoint(cuts.EdgePoints, new EdgePoint(edgeIndex, clippedPiece.Start), face, tolerance, EdgePointMerge.SameEdgeOnly);
                        AddUniqueEdgePoint(cuts.EdgePoints, new EdgePoint(edgeIndex, clippedPiece.End), face, tolerance, EdgePointMerge.SameEdgeOnly);
                        continue;
                    }

                    AddUniqueSegment(cuts.InternalSegments, clippedPiece, tolerance);
                    AddPieceEndpointEdgePoints(cuts.EdgePoints, face, clippedPiece, tolerance, EdgePointMerge.SameEdgeOnly);
                }
            }
        }

        ConformSharedEdgeSplits(vertices, faces, faceCount, result, tolerance);
        return result;
    }

    /// <summary>
    /// Registers each isolated point with the one face it lands in. The lowest-index containing face
    /// wins, so a point on a shared edge is recorded once and conformed into the neighbour afterwards.
    /// </summary>
    private static PointPlacement PlacePoints(
        double[] vertices,
        int[] faces,
        double[] pointXy,
        Bounds2D[] faceBounds,
        SpatialHashGrid2D grid,
        SpatialHashGrid2D.QueryScratch scratch,
        List<int> candidates,
        FaceCutData[] result,
        double tolerance)
    {
        int inserted = 0, onVertex = 0, outside = 0;
        for (int p = 0; p < pointXy.Length / 2; p++)
        {
            var point = new Point2D(pointXy[p * 2], pointXy[(p * 2) + 1]);
            var query = new Bounds2D(point.X - tolerance, point.X + tolerance, point.Y - tolerance, point.Y + tolerance);
            grid.GatherCandidates(query, candidates, scratch);
            candidates.Sort();

            bool placed = false;
            foreach (int faceIndex in candidates)
            {
                if (!faceBounds[faceIndex].Intersects(query))
                    continue;

                var face = new FaceData(vertices, faces, faceIndex);
                if (!face.ContainsPoint(point, tolerance))
                    continue;

                placed = true;
                if (face.IsNearVertex(point, tolerance))
                {
                    onVertex++;
                    break;
                }

                result[faceIndex] ??= new FaceCutData();
                int edgeIndex = face.GetEdgeIndex(point, tolerance);
                if (edgeIndex >= 0)
                {
                    AddUniqueEdgePoint(result[faceIndex].EdgePoints, new EdgePoint(edgeIndex, point), face, tolerance, EdgePointMerge.SameEdgeOnly);
                }
                else
                {
                    List<Point2D> interior = result[faceIndex].InteriorPoints;
                    bool duplicate = false;
                    foreach (Point2D existing in interior)
                        duplicate |= DistanceSquared(existing, point) <= tolerance * tolerance;
                    if (!duplicate)
                        interior.Add(point);
                }

                inserted++;
                break;
            }

            if (!placed)
                outside++;
        }

        return new PointPlacement(inserted, onVertex, outside);
    }

    /// <summary>
    /// Makes every shared edge conform: the two faces incident to an edge must subdivide it at the
    /// same set of points, so their sub-edges match one for one.
    ///
    /// Edge points are collected per face, and each face decides independently whether a segment
    /// touches it. Two neighbours can therefore disagree about one point on the edge they share --
    /// measured on a real terrain, one face split at t=[.345,.529,.688,.707] while its neighbour split
    /// the same edge at t=[.345,.529,.688,.698,.707]. The extra point leaves a vertex sitting in the
    /// interior of the other face's sub-edge: that sub-edge is used by one face only, so every
    /// downstream boundary analysis counts it as a naked edge and reports a hole that does not exist.
    ///
    /// The surface is complete either way -- no area is lost and no face is dropped -- but the mesh is
    /// non-conforming, which fails the watertightness gates and rejects wall breakline insertion. Two
    /// such points on a 2,148-face terrain were enough to report 3 boundary loops instead of 1.
    ///
    /// Taking the union is the correct reconciliation rather than the intersection: each point was put
    /// there because some constraint genuinely reaches that spot, and dropping it from the face that
    /// found it would move a constraint. A point on the shared edge is on both faces' planes -- along
    /// that edge both interpolate linearly between the same two endpoints -- so the split vertex takes
    /// the same elevation from either side.
    /// </summary>
    private static void ConformSharedEdgeSplits(
        double[] vertices,
        int[] faces,
        int faceCount,
        FaceCutData?[] cuts,
        double tolerance)
    {
        // Edge key -> the (face, local edge index) pairs using it. Only manifold pairs can disagree.
        var edgeUsers = new Dictionary<long, (int FaceA, int EdgeA, int FaceB, int EdgeB, int Count)>(
            faceCount, IndexedMeshTools.EdgeKeyComparer.Instance);

        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            int a = faces[faceIndex * 3];
            int b = faces[(faceIndex * 3) + 1];
            int c = faces[(faceIndex * 3) + 2];
            AccumulateEdgeUser(edgeUsers, a, b, faceIndex, 0);
            AccumulateEdgeUser(edgeUsers, b, c, faceIndex, 1);
            AccumulateEdgeUser(edgeUsers, c, a, faceIndex, 2);
        }

        var pointsOnEdge = new List<(double Parameter, Point2D Point)>(8);
        foreach (var entry in edgeUsers.Values)
        {
            if (entry.Count != 2)
                continue;

            FaceCutData? cutA = cuts[entry.FaceA];
            FaceCutData? cutB = cuts[entry.FaceB];
            if (cutA is null && cutB is null)
                continue;

            var faceA = new FaceData(vertices, faces, entry.FaceA);
            Point2D edgeStart = faceA.GetEdgeStart(entry.EdgeA);
            Point2D edgeEnd = faceA.GetEdgeEnd(entry.EdgeA);

            pointsOnEdge.Clear();
            GatherEdgePointsOn(cutA, entry.EdgeA, edgeStart, edgeEnd, tolerance, pointsOnEdge);
            GatherEdgePointsOn(cutB, entry.EdgeB, edgeStart, edgeEnd, tolerance, pointsOnEdge);
            if (pointsOnEdge.Count == 0)
                continue;

            var faceB = new FaceData(vertices, faces, entry.FaceB);
            double toleranceSquared = tolerance * tolerance;
            foreach (var (_, point) in pointsOnEdge)
            {
                // Only the shared edge's own endpoints absorb a split. A sliver's far corner can lie within
                // tolerance of the split too, but the face across the edge does not have that corner and
                // cuts the edge anyway; skipping the split here left the edge cut on one side only.
                if (DistanceSquared(point, edgeStart) <= toleranceSquared || DistanceSquared(point, edgeEnd) <= toleranceSquared)
                    continue;

                cutA ??= cuts[entry.FaceA] = new FaceCutData();
                AddUniqueEdgePoint(cutA.EdgePoints, new EdgePoint(entry.EdgeA, point), faceA, tolerance, EdgePointMerge.SameEdgeOnly);
                cutB ??= cuts[entry.FaceB] = new FaceCutData();
                AddUniqueEdgePoint(cutB.EdgePoints, new EdgePoint(entry.EdgeB, point), faceB, tolerance, EdgePointMerge.SameEdgeOnly);
            }
        }
    }

    private static void AccumulateEdgeUser(
        Dictionary<long, (int FaceA, int EdgeA, int FaceB, int EdgeB, int Count)> edgeUsers,
        int start,
        int end,
        int faceIndex,
        int edgeIndex)
    {
        long key = start < end
            ? ((long)start << 32) | (uint)end
            : ((long)end << 32) | (uint)start;

        if (!edgeUsers.TryGetValue(key, out var entry))
        {
            edgeUsers[key] = (faceIndex, edgeIndex, -1, -1, 1);
            return;
        }

        // A third user means non-manifold input; leave those edges alone rather than guess.
        edgeUsers[key] = entry.Count == 1
            ? (entry.FaceA, entry.EdgeA, faceIndex, edgeIndex, 2)
            : (entry.FaceA, entry.EdgeA, entry.FaceB, entry.EdgeB, entry.Count + 1);
    }

    /// <summary>
    /// Collects this face's split points lying on the given edge, keyed by parameter along it, skipping
    /// any already gathered within tolerance so the union does not double-insert a shared point.
    /// </summary>
    private static void GatherEdgePointsOn(
        FaceCutData? cut,
        int edgeIndex,
        Point2D edgeStart,
        Point2D edgeEnd,
        double tolerance,
        List<(double Parameter, Point2D Point)> destination)
    {
        if (cut is null)
            return;

        double toleranceSquared = tolerance * tolerance;
        foreach (EdgePoint edgePoint in cut.EdgePoints)
        {
            if (edgePoint.EdgeIndex != edgeIndex)
                continue;

            double parameter = ParameterOnEdge(edgeStart, edgeEnd, edgePoint.Point);
            if (parameter <= 0.0 || parameter >= 1.0)
                continue;

            bool alreadyPresent = false;
            for (int i = 0; i < destination.Count; i++)
            {
                if (DistanceSquared(destination[i].Point, edgePoint.Point) <= toleranceSquared)
                {
                    alreadyPresent = true;
                    break;
                }
            }

            if (!alreadyPresent)
                destination.Add((parameter, edgePoint.Point));
        }
    }

    private static bool TriangulateTouchedFace(
        FaceData face,
        FaceCutData cutData,
        GlobalPointLookup pointLookup,
        MeshEdgeSplitRegistry edgeSplits,
        List<int> globalFaces,
        double tolerance,
        out string? errorMessage)
    {
        errorMessage = null;

        var localPoints = new LocalPointBuilder(tolerance, protectCorners: false);
        var identities = new LocalPointIdentities();
        // Edge 0 runs A-B, edge 1 B-C, edge 2 C-A.
        int a = localPoints.Add(face.A, face.Az, 0b101);
        identities.SetCorner(a, face.I0);
        int b = localPoints.Add(face.B, face.Bz, 0b011);
        identities.SetCorner(b, face.I1);
        int c = localPoints.Add(face.C, face.Cz, 0b110);
        identities.SetCorner(c, face.I2);

        List<(double Parameter, int LocalIndex)>[] edgePointLists = CreateEdgeChains(a, b, c);

        foreach (EdgePoint edgePoint in cutData.EdgePoints)
        {
            // Only the edge's own endpoints absorb a split. The far corner of a sliver can sit within
            // tolerance of the edge too, but the face across the edge cannot see that corner and splits
            // the edge regardless, so this face must split it as well.
            Point2D edgeStart = face.GetEdgeStart(edgePoint.EdgeIndex), edgeEnd = face.GetEdgeEnd(edgePoint.EdgeIndex);
            if (DistanceSquared(edgePoint.Point, edgeStart) <= tolerance * tolerance ||
                DistanceSquared(edgePoint.Point, edgeEnd) <= tolerance * tolerance)
                continue;

            // Triangulate the split where it will land - exactly on the edge. Left a hair off it, the thin
            // triangle between it and the unsplit hull edge has area here and none once resolved: a cap.
            Point2D onEdge = SnapPointToEdge(edgeStart, edgeEnd, edgePoint.Point);
            int localIndex = localPoints.Add(onEdge, face.InterpolateZ(onEdge), 1 << edgePoint.EdgeIndex);
            identities.SetEdge(localIndex, edgePoint.EdgeIndex);
            double parameter = ParameterOnEdge(face.GetEdgeStart(edgePoint.EdgeIndex), face.GetEdgeEnd(edgePoint.EdgeIndex), edgePoint.Point);
            edgePointLists[edgePoint.EdgeIndex].Add((parameter, localIndex));
        }

        foreach (Point2D interiorPoint in cutData.InteriorPoints)
            localPoints.Add(interiorPoint, face.InterpolateZ(interiorPoint));

        var segments = new List<(int a, int b)>();
        var segmentKeys = IndexedMeshTools.CreateEdgeKeySet();

        foreach (SegmentPiece piece in cutData.InternalSegments)
        {
            int start = localPoints.Add(piece.Start, face.InterpolateZ(piece.Start));
            int end = localPoints.Add(piece.End, face.InterpolateZ(piece.End));
            if (start == end)
                continue;

            MeshConstraintTools.TryAddSegment(segments, segmentKeys, start, end);
        }

        AddEdgeChainSegments(edgePointLists, segments, segmentKeys);

        TriangulationOutcome outcome = TriangulationHelper.Triangulate(localPoints.Xy, localPoints.Count, segments, 0, 0, convex: true, segmentSplitting: 0);
        if (outcome.Mesh == null || MeshConstraintTools.ConstraintsWereDropped(outcome.Flags))
        {
            errorMessage = outcome.WarningMessage ?? "Path topology insertion failed.";
            return false;
        }

        TriangleNetExtractor.Result extracted = TriangleNetExtractor.Extract(outcome.Mesh);
        var extractedToGlobal = new int[extracted.VertexCount];
        for (int i = 0; i < extracted.VertexCount; i++)
        {
            var point = new Point2D(extracted.Xy[i * 2], extracted.Xy[(i * 2) + 1]);
            int local = extracted.SourceIds[i];
            bool isLocal = local >= 0 && local < localPoints.Count &&
                           localPoints.Xy[local * 2] == point.X && localPoints.Xy[(local * 2) + 1] == point.Y;

            // A corner is this face's own vertex, and a split of a mesh edge is resolved against that edge
            // alone, so both faces sharing the edge land on one vertex lying exactly on it. Resolving them by
            // XY instead let a split merge into whatever vertex was nearest within tolerance - on a band of
            // slivers, a split of the next edge over - and the two faces then disagreed about the edge.
            if (isLocal && identities.TryGetCorner(local, out int corner))
            {
                extractedToGlobal[i] = corner;
            }
            else if (isLocal && identities.TryGetEdge(local, out int edgeIndex))
            {
                (int start, int end) = edgeIndex switch
                {
                    0 => (face.I0, face.I1),
                    1 => (face.I1, face.I2),
                    _ => (face.I2, face.I0)
                };
                extractedToGlobal[i] = edgeSplits.Resolve(start, end, point.X, point.Y);
            }
            else
            {
                extractedToGlobal[i] = pointLookup.Resolve(point, face.InterpolateZ(point));
            }
        }

        for (int faceIndex = 0; faceIndex < extracted.FaceCount; faceIndex++)
        {
            int vertex0 = extracted.Faces[faceIndex * 3];
            int vertex1 = extracted.Faces[(faceIndex * 3) + 1];
            int vertex2 = extracted.Faces[(faceIndex * 3) + 2];
            var p0 = new Point2D(extracted.Xy[vertex0 * 2], extracted.Xy[(vertex0 * 2) + 1]);
            var p1 = new Point2D(extracted.Xy[vertex1 * 2], extracted.Xy[(vertex1 * 2) + 1]);
            var p2 = new Point2D(extracted.Xy[vertex2 * 2], extracted.Xy[(vertex2 * 2) + 1]);
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

            // Test again where the vertices actually land. A local point merged into a neighbour's vertex
            // can move onto this face's own edge: in a sliver whose two edges pass within tolerance of one
            // crossing, the triangle between the unsplit edge and that point has area here but none once
            // resolved, and keeping it keeps an edge the neighbour split.
            if (pointLookup.ProjectedCross(g0, g1, g2) < tolerance * tolerance * 1e-3)
                continue;

            globalFaces.Add(g0);
            globalFaces.Add(g1);
            globalFaces.Add(g2);
        }

        return true;
    }
}
