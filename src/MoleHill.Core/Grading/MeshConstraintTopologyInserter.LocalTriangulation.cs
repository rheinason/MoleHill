using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

/// <summary>
/// Constraint insertion by re-triangulating the neighbourhood of the constraints as one piece.
/// </summary>
internal static partial class MeshConstraintTopologyInserter
{
    /// <summary>
    /// Inserts <paramref name="constraints"/> by re-triangulating, as one constrained Delaunay triangulation,
    /// every face they cross plus every face sharing a vertex with those. Each existing vertex of that patch
    /// is kept exactly and at its index, the patch's own boundary edges are hard segments, and no refinement
    /// point is added, so nothing outside the patch changes and the terrain between the constraints keeps its
    /// shape (a wall rail stays a crease, not a smoothed band). New vertices take the elevation of the face
    /// they land in; the caller assigns constraint elevations afterwards.
    /// </summary>
    /// <remarks>
    /// This is the fallback for <see cref="TryInsert(double[], int, int[], int, IReadOnlyList{ConstraintPolyline}, double, out double[], out int, out int[], out int, out string?)"/>,
    /// which triangulates each crossed face on its own and must agree with every neighbour about where their
    /// shared edge was cut. When an upstream conform has left a vertex a millimetre off a neighbouring edge
    /// (the area splitter snaps up to <see cref="MeshAreaTopologySplitter.ConformSnapToleranceFactor"/> times
    /// the tolerance), the two faces can disagree, and the result carries a needle and a naked edge. Measured
    /// on a ring wall inserted beside a graded path: one extra boundary loop and two non-manifold edges, in
    /// both wall modes, at every tolerance from 1 to 10 mm. A single triangulation has no shared edge to
    /// disagree about. The result is still checked: its plan area must equal the patch's, and its boundary
    /// must be exactly the patch boundary, or it is refused.
    /// </remarks>
    internal static bool TryInsertByLocalTriangulation(
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
        out string? errorMessage)
    {
        CloneInput(vertices, faces, out outputVertices, out outputFaces);
        outputVertexCount = vertexCount;
        outputFaceCount = faceCount;
        errorMessage = null;

        double resolvedTolerance = Math.Max(tolerance, 1e-9);
        List<ConstraintSegment> segments = BuildConstraintSegments(constraints, resolvedTolerance);
        if (segments.Count == 0 || faceCount == 0)
        {
            errorMessage = "Local triangulation has no constraint segments to insert.";
            return false;
        }

        FaceCutData[] cuts = MapConstraintSegmentsToFaces(vertices, faces, faceCount, segments, resolvedTolerance);

        // The patch: every crossed face, then every face sharing a vertex with one, so no constraint reaches
        // the patch boundary except where the terrain itself ends.
        MeshVertexAdjacency adjacency = MeshVertexAdjacency.Build(
            new List<int>(faces.AsSpan(0, faceCount * 3).ToArray()), faceCount, vertexCount);
        var inPatch = new bool[faceCount];
        var crossed = new List<int>();
        for (int f = 0; f < faceCount; f++)
        {
            if (cuts[f]?.HasData == true)
                crossed.Add(f);
        }

        if (crossed.Count == 0)
        {
            errorMessage = "Local triangulation found no faces crossed by the constraints.";
            return false;
        }

        foreach (int f in crossed)
        {
            for (int k = 0; k < 3; k++)
            {
                foreach (int neighbour in adjacency.FacesOf(faces[(f * 3) + k]))
                    inPatch[neighbour] = true;
            }
        }

        var patch = new List<int>();
        for (int f = 0; f < faceCount; f++)
        {
            if (inPatch[f])
                patch.Add(f);
        }

        // Patch boundary: the edges exactly one patch face uses.
        Dictionary<long, int> edgeUses = IndexedMeshTools.CreateEdgeKeyMap<int>(patch.Count * 3);
        foreach (int f in patch)
        {
            for (int k = 0; k < 3; k++)
            {
                long key = IndexedMeshTools.GetEdgeKey(faces[(f * 3) + k], faces[(f * 3) + ((k + 1) % 3)]);
                edgeUses[key] = edgeUses.GetValueOrDefault(key) + 1;
            }
        }

        var boundaryEdges = new HashSet<long>(IndexedMeshTools.EdgeKeyComparer.Instance);
        foreach ((long key, int uses) in edgeUses)
        {
            if (uses == 1)
                boundaryEdges.Add(key);
        }

        // Points: every patch vertex exactly, at its own index; then the constraint pieces' ends, each snapped
        // onto a patch vertex within tolerance.
        var localToGlobal = new List<int>();
        var globalToLocal = new Dictionary<int, int>();
        var xy = new List<double>();
        foreach (int f in patch)
        {
            for (int k = 0; k < 3; k++)
            {
                int g = faces[(f * 3) + k];
                if (globalToLocal.ContainsKey(g))
                    continue;
                globalToLocal[g] = localToGlobal.Count;
                localToGlobal.Add(g);
                xy.Add(vertices[g * 3]);
                xy.Add(vertices[(g * 3) + 1]);
            }
        }

        int existingCount = localToGlobal.Count;
        double toleranceSquared = resolvedTolerance * resolvedTolerance;
        double cell = resolvedTolerance * 2.0;
        var buckets = new Dictionary<(long, long), List<int>>();
        (long, long) CellOf(double x, double y) => ((long)Math.Floor(x / cell), (long)Math.Floor(y / cell));
        void Index(int i)
        {
            (long, long) key = CellOf(xy[i * 2], xy[(i * 2) + 1]);
            if (!buckets.TryGetValue(key, out List<int>? list))
                buckets[key] = list = new List<int>(1);
            list.Add(i);
        }

        for (int i = 0; i < existingCount; i++)
            Index(i);

        int AddPoint(Point2D p)
        {
            (long cx, long cy) = CellOf(p.X, p.Y);
            int best = -1;
            double bestDistance = toleranceSquared;
            for (long dx = -1; dx <= 1; dx++)
            {
                for (long dy = -1; dy <= 1; dy++)
                {
                    if (!buckets.TryGetValue((cx + dx, cy + dy), out List<int>? list))
                        continue;
                    foreach (int i in list)
                    {
                        double ex = xy[i * 2] - p.X, ey = xy[(i * 2) + 1] - p.Y;
                        double distance = (ex * ex) + (ey * ey);
                        if (distance <= bestDistance)
                        {
                            bestDistance = distance;
                            best = i;
                        }
                    }
                }
            }

            if (best >= 0)
                return best;

            int index = xy.Count / 2;
            xy.Add(p.X);
            xy.Add(p.Y);
            Index(index);
            return index;
        }

        var hardSegments = new List<(int A, int B)>();
        var segmentKeys = new HashSet<long>(IndexedMeshTools.EdgeKeyComparer.Instance);
        void AddSegment(int a, int b)
        {
            if (a != b && segmentKeys.Add(IndexedMeshTools.GetEdgeKey(a, b)))
                hardSegments.Add((a, b));
        }

        foreach (long key in boundaryEdges)
            AddSegment(globalToLocal[(int)(key >> 32)], globalToLocal[(int)(key & 0xFFFFFFFFL)]);

        foreach (ConstraintSegment source in segments)
        {
            var intervals = new List<(double Start, double End)>();
            foreach (int f in patch)
            {
                foreach (SegmentPiece piece in ClipSegmentToTriangle(new FaceData(vertices, faces, f), source, resolvedTolerance))
                {
                    intervals.Add((ParameterOnEdge(source.Start, source.End, piece.Start),
                        ParameterOnEdge(source.Start, source.End, piece.End)));
                }
            }

            intervals.Sort(static (a, b) => a.Start.CompareTo(b.Start));
            for (int i = 0; i < intervals.Count; i++)
            {
                double start = intervals[i].Start, end = intervals[i].End;
                while (i + 1 < intervals.Count && intervals[i + 1].Start <= end + 1e-8)
                    end = Math.Max(end, intervals[++i].End);
                AddSegment(AddPoint(Lerp(source.Start, source.End, start)), AddPoint(Lerp(source.Start, source.End, end)));
            }
        }

        var polygon = new TriangleNet.Geometry.Polygon();
        int pointCount = xy.Count / 2;
        var nodes = new TriangleNet.Geometry.Vertex[pointCount];
        for (int i = 0; i < pointCount; i++)
        {
            nodes[i] = new TriangleNet.Geometry.Vertex(xy[i * 2], xy[(i * 2) + 1]) { ID = i };
            polygon.Add(nodes[i]);
        }

        foreach ((int a, int b) in hardSegments)
            polygon.Add(new TriangleNet.Geometry.Segment(nodes[a], nodes[b]));

        TriangleNetExtractor.Result extracted;
        try
        {
            // Not Convex: Triangle carves away everything outside the patch boundary segments itself. Keeping the
            // convex hull and locating centroids let micrometre-wide fillers between near-collinear boundary
            // edges through (the face grid's barycentric slop is millimetres on a 48 m sliver), and the area
            // check then refused every rail beside a plain-Delaunay hull fan.
            extracted = TriangleNetExtractor.Extract(TriangulationHelper.TriangulatePolygon(
                polygon,
                new TriangleNet.Meshing.ConstraintOptions { Convex = false, ConformingDelaunay = false }));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException)
        {
            errorMessage = $"Local triangulation failed: {ex.Message}";
            return false;
        }

        // Keep the triangles inside the patch: carving only reaches in from the outside, so a hole in the patch
        // (a face it surrounds but does not contain) is still filled and must be dropped here.
        var patchFaces = new int[patch.Count * 3];
        for (int i = 0; i < patch.Count; i++)
        {
            patchFaces[i * 3] = faces[patch[i] * 3];
            patchFaces[(i * 3) + 1] = faces[(patch[i] * 3) + 1];
            patchFaces[(i * 3) + 2] = faces[(patch[i] * 3) + 2];
        }

        var patchGrid = new TerrainFaceGrid(vertices, vertexCount, patchFaces, patch.Count);
        var coordinateToLocal = new Dictionary<(double, double), int>(pointCount);
        for (int i = 0; i < pointCount; i++)
            coordinateToLocal.TryAdd((xy[i * 2], xy[(i * 2) + 1]), i);

        var globalVertices = new List<double>(vertices.AsSpan(0, vertexCount * 3).ToArray());
        var extractedToGlobal = new int[extracted.VertexCount];
        for (int i = 0; i < extracted.VertexCount; i++)
        {
            double x = extracted.Xy[i * 2], y = extracted.Xy[(i * 2) + 1];
            if (coordinateToLocal.TryGetValue((x, y), out int local) && local < existingCount)
            {
                extractedToGlobal[i] = localToGlobal[local];
                continue;
            }

            extractedToGlobal[i] = globalVertices.Count / 3;
            globalVertices.Add(x);
            globalVertices.Add(y);
            globalVertices.Add(patchGrid.InterpolateZ(x, y));
        }

        var newPatchFaces = new List<int>(patch.Count * 4);
        double patchArea = 0.0, newArea = 0.0;
        foreach (int f in patch)
            patchArea += Math.Abs(TwiceArea(vertices, faces[f * 3], faces[(f * 3) + 1], faces[(f * 3) + 2]));

        for (int t = 0; t < extracted.FaceCount; t++)
        {
            int a = extracted.Faces[t * 3], b = extracted.Faces[(t * 3) + 1], c = extracted.Faces[(t * 3) + 2];
            double cx = (extracted.Xy[a * 2] + extracted.Xy[b * 2] + extracted.Xy[c * 2]) / 3.0;
            double cy = (extracted.Xy[(a * 2) + 1] + extracted.Xy[(b * 2) + 1] + extracted.Xy[(c * 2) + 1]) / 3.0;
            if (!patchGrid.TryFindFace(cx, cy, out _, out _, out _, out _))
                continue;

            int ga = extractedToGlobal[a], gb = extractedToGlobal[b], gc = extractedToGlobal[c];
            newPatchFaces.Add(ga);
            newPatchFaces.Add(gb);
            newPatchFaces.Add(gc);
            newArea += Math.Abs(
                ((globalVertices[gb * 3] - globalVertices[ga * 3]) * (globalVertices[(gc * 3) + 1] - globalVertices[(ga * 3) + 1])) -
                ((globalVertices[(gb * 3) + 1] - globalVertices[(ga * 3) + 1]) * (globalVertices[gc * 3] - globalVertices[ga * 3])));
        }

        // The patch must come back as exactly the same region: same plan area, same boundary.
        if (Math.Abs(newArea - patchArea) > Math.Max(patchArea * 1e-9, toleranceSquared))
        {
            errorMessage = "Local triangulation changed the patch area.";
            return false;
        }

        Dictionary<long, int> newUses = IndexedMeshTools.CreateEdgeKeyMap<int>(newPatchFaces.Count);
        for (int i = 0; i < newPatchFaces.Count; i += 3)
        {
            for (int k = 0; k < 3; k++)
            {
                long key = IndexedMeshTools.GetEdgeKey(newPatchFaces[i + k], newPatchFaces[i + ((k + 1) % 3)]);
                newUses[key] = newUses.GetValueOrDefault(key) + 1;
            }
        }

        // Every new boundary edge is an original patch-boundary edge, or a piece of one that is also the
        // terrain's own outer edge (a constraint running off the terrain splits it, and nothing lies beyond
        // to disagree). Interior patch-boundary edges must come back whole. Together with an unchanged total
        // boundary length, that is the same boundary.
        var outerEdges = new List<long>();
        foreach (long key in boundaryEdges)
        {
            int a = (int)(key >> 32), b = (int)(key & 0xFFFFFFFFL);
            bool shared = false;
            foreach (int f in adjacency.FacesOf(a))
            {
                if (inPatch[f])
                    continue;
                if (faces[f * 3] == b || faces[(f * 3) + 1] == b || faces[(f * 3) + 2] == b)
                {
                    shared = true;
                    break;
                }
            }

            if (!shared)
                outerEdges.Add(key);
        }

        double EdgeLength(List<double> v, long key)
        {
            int a = (int)(key >> 32), b = (int)(key & 0xFFFFFFFFL);
            return Math.Sqrt(Sq(v[b * 3] - v[a * 3]) + Sq(v[(b * 3) + 1] - v[(a * 3) + 1]));
        }

        bool OnOuterEdge(double x, double y)
        {
            foreach (long key in outerEdges)
            {
                int a = (int)(key >> 32), b = (int)(key & 0xFFFFFFFFL);
                var start = new Point2D(vertices[a * 3], vertices[(a * 3) + 1]);
                var end = new Point2D(vertices[b * 3], vertices[(b * 3) + 1]);
                if (PointOnSegment(new Point2D(x, y), start, end, Math.Max(1e-9, resolvedTolerance * 1e-3)))
                    return true;
            }

            return false;
        }

        var verticesList = globalVertices;
        double originalBoundaryLength = 0.0, newBoundaryLength = 0.0;
        foreach (long key in boundaryEdges)
            originalBoundaryLength += EdgeLength(verticesList, key);
        foreach ((long key, int uses) in newUses)
        {
            if (uses > 2)
            {
                errorMessage = "Local triangulation produced a non-manifold edge.";
                return false;
            }

            if (uses != 1)
                continue;

            newBoundaryLength += EdgeLength(verticesList, key);
            if (boundaryEdges.Contains(key))
                continue;

            int a = (int)(key >> 32), b = (int)(key & 0xFFFFFFFFL);
            if (!OnOuterEdge(verticesList[a * 3], verticesList[(a * 3) + 1]) ||
                !OnOuterEdge(verticesList[b * 3], verticesList[(b * 3) + 1]))
            {
                errorMessage = "Local triangulation moved the patch boundary.";
                return false;
            }
        }

        if (Math.Abs(newBoundaryLength - originalBoundaryLength) > Math.Max(originalBoundaryLength * 1e-9, resolvedTolerance * 1e-3))
        {
            errorMessage = "Local triangulation changed the patch boundary's length.";
            return false;
        }

        var outFaces = new List<int>((faceCount * 3) + newPatchFaces.Count);
        for (int f = 0; f < faceCount; f++)
        {
            if (inPatch[f])
                continue;
            outFaces.Add(faces[f * 3]);
            outFaces.Add(faces[(f * 3) + 1]);
            outFaces.Add(faces[(f * 3) + 2]);
        }

        outFaces.AddRange(newPatchFaces);
        outputVertices = globalVertices.ToArray();
        outputVertexCount = outputVertices.Length / 3;
        outputFaces = outFaces.ToArray();
        outputFaceCount = outputFaces.Length / 3;
        return true;

        static double Sq(double value) => value * value;

        static double TwiceArea(double[] v, int a, int b, int c) =>
            ((v[b * 3] - v[a * 3]) * (v[(c * 3) + 1] - v[(a * 3) + 1])) -
            ((v[(b * 3) + 1] - v[(a * 3) + 1]) * (v[c * 3] - v[a * 3]));
    }
}
