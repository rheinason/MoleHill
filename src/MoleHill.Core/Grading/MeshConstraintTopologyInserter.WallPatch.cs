using MoleHill.Core.Engine;
using static MoleHill.Core.Grading.FaceCutGeometry;

namespace MoleHill.Core.Grading;

/// <summary>
/// Bounded wall-patch triangulation with conforming neighbour splits and lifted elevation sampling.
/// </summary>
internal static partial class MeshConstraintTopologyInserter
{
    internal static bool TryBuildWallPatchCandidate(
        double[] vertices, int[] faces,
        IReadOnlyList<ConstraintPolyline> constraints,
        double tolerance, int segmentSplitting,
        out double[] outputVertices, out int[] outputFaces, out string? error, int neighbourRings = 0, double maxArea = 0)
    {
        return TryBuildWallPatchCandidate(vertices, faces, constraints, tolerance, segmentSplitting,
            out outputVertices, out outputFaces, out error, out _, neighbourRings, maxArea);
    }

    /// <param name="maxOutputFaces">
    /// Face budget for the candidate. When the patch's own triangles plus the untouched faces already
    /// exceed it, the candidate is abandoned before any elevation sampling or neighbour stitching and
    /// <paramref name="exceededFaceBudget"/> is set; the returned arrays are then the unchanged input.
    /// </param>
    internal static bool TryBuildWallPatchCandidate(
        double[] vertices, int[] faces,
        IReadOnlyList<ConstraintPolyline> constraints,
        double tolerance, int segmentSplitting,
        out double[] outputVertices, out int[] outputFaces, out string? error, out bool exceededFaceBudget,
        int neighbourRings = 0, double maxArea = 0, int maxOutputFaces = int.MaxValue)
    {
        CloneInput(vertices, faces, out outputVertices, out outputFaces);
        error = null;
        exceededFaceBudget = false;
        int faceCount = faces.Length / 3;
        if (faceCount == 0 || faceCount > 4096)
        {
            error = "Wall patch requires 1–4096 input faces.";
            return false;
        }

        // Build the elevation reference before introducing Steiner points. The production inserter
        // preserves upstream Z and the host subsequently lifts rail vertices. Sampling that lifted
        // surface extends the same wall shape to points inside the refined band.
        if (!TryInsertCore(vertices, vertices.Length / 3, faces, faceCount, constraints, Array.Empty<double>(), tolerance,
                out var referenceVertices, out _, out var referenceFaces, out _, out _, out error)) return false;
        for (int i = 0; i < referenceVertices.Length; i += 3)
        {
            var p = new Point2D(referenceVertices[i], referenceVertices[i + 1]);
            double best = tolerance * tolerance;
            foreach (var rail in constraints)
            {
                if (!rail.PreserveInputElevation) continue;
                int count = NormalizePointCount(rail, tolerance);
                for (int s = 0; s < count - (rail.IsClosed ? 0 : 1); s++)
                {
                    int a = s * 3, b = ((s + 1) % count) * 3;
                    var start = new Point2D(rail.Points[a], rail.Points[a + 1]);
                    var end = new Point2D(rail.Points[b], rail.Points[b + 1]);
                    if (DistanceSquared(start, end) <= 1e-20) continue;
                    double t = Math.Clamp(ParameterOnEdge(start, end, p), 0, 1);
                    double distance = DistanceSquared(p, Lerp(start, end, t));
                    if (distance > best) continue;
                    best = distance;
                    referenceVertices[i + 2] = rail.Points[a + 2] + t * (rail.Points[b + 2] - rail.Points[a + 2]);
                }
            }
        }

        var sourceSegments = BuildConstraintSegments(constraints, tolerance);
        var cuts = MapConstraintSegmentsToFaces(vertices, faces, faceCount, sourceSegments, tolerance);
        var patch = Enumerable.Range(0, faceCount).Where(i => cuts[i]?.HasData == true).ToArray();
        if (patch.Length == 0) return true;
        var patchSet = patch.ToHashSet();
        var edgeOwners = new Dictionary<long, List<(int Face, int Edge)>>(IndexedMeshTools.EdgeKeyComparer.Instance);
        for (int f = 0; f < faceCount; f++)
        {
            for (int e = 0; e < 3; e++)
            {
                int a = faces[f * 3 + e], b = faces[f * 3 + (e + 1) % 3];
                long key = ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
                if (!edgeOwners.TryGetValue(key, out var owners)) edgeOwners[key] = owners = new();
                owners.Add((f, e));
            }
        }
        for (int ring = 0; ring < neighbourRings; ring++)
        {
            var neighbours = edgeOwners.Values.Where(o => o.Any(v => patchSet.Contains(v.Face)))
                .SelectMany(o => o.Select(v => v.Face)).ToArray();
            patchSet.UnionWith(neighbours);
        }
        patch = patchSet.OrderBy(i => i).ToArray();
        var boundary = edgeOwners.Values.Where(o => o.Count(v => patchSet.Contains(v.Face)) == 1).ToArray();
        var points = new LocalPointBuilder(tolerance, protectCorners: false);
        var segments = new List<(int a, int b)>();
        var keys = new HashSet<long>(IndexedMeshTools.EdgeKeyComparer.Instance);
        // Sample runs once per patch vertex, so index the reference faces rather than scanning them all.
        // Bounds are padded past ContainsPoint's barycentric slack; candidates are visited in index order
        // so the lowest-index containing face still wins, and a miss falls back to the full scan, so the
        // result is exactly what the linear scan returned.
        int referenceFaceCount = referenceFaces.Length / 3;
        Bounds2D[] referenceBounds = BuildFaceBounds(referenceVertices, referenceFaces, referenceFaceCount);
        for (int f = 0; f < referenceFaceCount; f++)
        {
            Bounds2D b = referenceBounds[f];
            double pad = tolerance + (1e-4 * Math.Max(b.MaxX - b.MinX, b.MaxY - b.MinY));
            referenceBounds[f] = new Bounds2D(b.MinX - pad, b.MaxX + pad, b.MinY - pad, b.MaxY + pad);
        }
        SpatialHashGrid2D referenceGrid = SpatialHashGrid2D.Build(referenceBounds);
        var sampleScratch = new SpatialHashGrid2D.QueryScratch(referenceFaceCount);
        var sampleCandidates = new List<int>();
        double Sample(Point2D p)
        {
            referenceGrid.GatherCandidates(Bounds2D.FromPoint(p.X, p.Y), sampleCandidates, sampleScratch);
            sampleCandidates.Sort();
            foreach (int f in sampleCandidates)
            {
                var face = new FaceData(referenceVertices, referenceFaces, f);
                if (face.ContainsPoint(p, tolerance)) return face.InterpolateZ(p);
            }
            for (int f = 0; f < referenceFaceCount; f++)
            {
                var face = new FaceData(referenceVertices, referenceFaces, f);
                if (face.ContainsPoint(p, tolerance)) return face.InterpolateZ(p);
            }
            throw new InvalidOperationException("Patch vertex is outside the original terrain.");
        }
        void Add(Point2D a, Point2D b)
        {
            int ia = points.Add(a, Sample(a)), ib = points.Add(b, Sample(b));
            if (ia != ib) MeshConstraintTools.TryAddSegment(segments, keys, ia, ib);
        }
        foreach (var owners in boundary)
        {
            var owner = owners.First(o => patchSet.Contains(o.Face));
            var face = new FaceData(vertices, faces, owner.Face);
            Add(face.GetEdgeStart(owner.Edge), face.GetEdgeEnd(owner.Edge));
        }
        // Keep authored vertices, but discard intersections with the removed internal face edges.
        foreach (int f in patch)
        {
            var face = new FaceData(vertices, faces, f);
            points.Add(face.A, face.Az); points.Add(face.B, face.Bz); points.Add(face.C, face.Cz);
        }
        foreach (var source in sourceSegments)
        {
            var intervals = new List<(double Start, double End)>();
            foreach (int f in patch)
                foreach (var piece in ClipSegmentToTriangle(new FaceData(vertices, faces, f), source, tolerance))
                    intervals.Add((ParameterOnEdge(source.Start, source.End, piece.Start),
                        ParameterOnEdge(source.Start, source.End, piece.End)));
            intervals.Sort((a, b) => a.Start.CompareTo(b.Start));
            for (int i = 0; i < intervals.Count; i++)
            {
                double start = intervals[i].Start, end = intervals[i].End;
                while (i + 1 < intervals.Count && intervals[i + 1].Start <= end + 1e-8)
                    end = Math.Max(end, intervals[++i].End);
                Add(Lerp(source.Start, source.End, start), Lerp(source.Start, source.End, end));
            }
        }
        var polygon = new TriangleNet.Geometry.Polygon();
        var nodes = Enumerable.Range(0, points.Count)
            .Select(i => new TriangleNet.Geometry.Vertex(points.Xy[i * 2], points.Xy[i * 2 + 1])).ToArray();
        foreach (var node in nodes) polygon.Add(node);
        foreach (var (a, b) in segments) polygon.Add(new TriangleNet.Geometry.Segment(nodes[a], nodes[b]));
        var mesh = TriangulationHelper.TriangulatePolygon(polygon,
            new TriangleNet.Meshing.ConstraintOptions { Convex = true, ConformingDelaunay = true, SegmentSplitting = segmentSplitting },
            new TriangleNet.Meshing.QualityOptions { MinimumAngle = 20, MaximumArea = maxArea, SteinerPoints = 10000 });
        var extracted = TriangleNetExtractor.Extract(mesh);
        Point2D Point(int i) => new(extracted.Xy[i * 2], extracted.Xy[i * 2 + 1]);
        var keptFaces = new List<int>(extracted.FaceCount);
        for (int f = 0; f < extracted.FaceCount; f++)
        {
            var pa = Point(extracted.Faces[f * 3]); var pb = Point(extracted.Faces[f * 3 + 1]); var pc = Point(extracted.Faces[f * 3 + 2]);
            var center = new Point2D((pa.X + pb.X + pc.X) / 3, (pa.Y + pb.Y + pc.Y) / 3);
            if (patch.Any(i => new FaceData(vertices, faces, i).ContainsPoint(center, tolerance))) keptFaces.Add(f);
        }
        // Neighbour bisection below only adds faces, so this is a lower bound on the output size:
        // exceeding it here means the finished candidate would too, without sampling a single vertex.
        if ((long)keptFaces.Count + (faceCount - patch.Length) > maxOutputFaces)
        {
            exceededFaceBudget = true;
            error = $"Wall patch exceeds {maxOutputFaces:N0} output faces.";
            return false;
        }
        var globalVertices = new List<double>(vertices);
        for (int i = 2; i < globalVertices.Count; i += 3) globalVertices[i] = referenceVertices[i];
        // Model-tolerance welding can erase short, valid refined edges. Match only numerical duplicates.
        var lookup = new GlobalPointLookup(globalVertices, Math.Max(1e-9, tolerance * 1e-6));
        var globalFaces = new List<int>();
        var mapped = new Dictionary<int, int>();
        int Map(int i)
        {
            if (!mapped.TryGetValue(i, out int g))
            {
                var p = Point(i);
                mapped[i] = g = lookup.Resolve(p, Sample(p));
            }
            return g;
        }
        foreach (int f in keptFaces)
        {
            int a = extracted.Faces[f * 3], b = extracted.Faces[f * 3 + 1], c = extracted.Faces[f * 3 + 2];
            globalFaces.Add(Map(a)); globalFaces.Add(Map(b)); globalFaces.Add(Map(c));
        }
        // Propagate every boundary Steiner point to its untouched neighbour before triangulating it.
        var neighbourCuts = new Dictionary<int, FaceCutData>();
        foreach (var owners in boundary)
        {
            foreach (var owner in owners.Where(o => !patchSet.Contains(o.Face)))
            {
                var face = new FaceData(vertices, faces, owner.Face);
                foreach (int i in mapped.Keys)
                {
                    var p = Point(i);
                    if (face.IsNearVertex(p, 1e-9) ||
                        !PointOnSegment(p, face.GetEdgeStart(owner.Edge), face.GetEdgeEnd(owner.Edge), 1e-8)) continue;
                    if (!neighbourCuts.TryGetValue(owner.Face, out var cut)) neighbourCuts[owner.Face] = cut = new();
                    cut.EdgePoints.Add(new EdgePoint(owner.Edge, p));
                }
            }
        }
        for (int f = 0; f < faceCount; f++)
        {
            if (patchSet.Contains(f)) continue;
            if (neighbourCuts.TryGetValue(f, out var cut))
            {
                var face = new FaceData(vertices, faces, f);
                var triangles = new List<(int A, int B, int C)> { (face.I0, face.I1, face.I2) };
                foreach (var group in cut.EdgePoints.GroupBy(p => p.EdgeIndex))
                {
                    int a = faces[f * 3 + group.Key], b = faces[f * 3 + (group.Key + 1) % 3];
                    foreach (var edgePoint in group.OrderBy(p => ParameterOnEdge(face.GetEdgeStart(group.Key), face.GetEdgeEnd(group.Key), p.Point)))
                    {
                        int p = lookup.Resolve(edgePoint.Point, face.InterpolateZ(edgePoint.Point));
                        if (p == a || p == b) continue;
                        int t = triangles.FindIndex(t => (t.A == a && t.B == b) || (t.B == a && t.C == b) || (t.C == a && t.A == b));
                        if (t < 0) throw new InvalidOperationException("Missing neighbour edge during bisection.");
                        var triangle = triangles[t];
                        int c = triangle.A != a && triangle.A != b ? triangle.A : triangle.B != a && triangle.B != b ? triangle.B : triangle.C;
                        triangles[t] = (a, p, c);
                        triangles.Add((p, b, c));
                        a = p;
                    }
                }
                foreach (var (a, b, c) in triangles)
                {
                    globalFaces.Add(a); globalFaces.Add(b); globalFaces.Add(c);
                }
            }
            else
            {
                globalFaces.Add(faces[f * 3]); globalFaces.Add(faces[f * 3 + 1]); globalFaces.Add(faces[f * 3 + 2]);
            }
        }
        outputVertices = globalVertices.ToArray(); outputFaces = globalFaces.ToArray();
        return true;
    }
}
