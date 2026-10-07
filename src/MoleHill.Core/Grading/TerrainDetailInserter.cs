using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

/// <summary>
/// Adds survey detail — spot points, breaklines, contours — to a finished terrain without re-triangulating
/// it. Only the faces the new data lands in are rebuilt; every other face, and every elevation an upstream
/// stage produced, is left exactly as it was.
///
/// A full rebuild from the mesh's own vertices (what Add Geometry used to do) re-Delaunays the whole
/// surface: it discards the edges grading, remeshing and walls chose, and merges vertices by XY alone, so
/// a steep face narrower than the merge tolerance collapses. Here the new data is inserted locally
/// (<see cref="MeshConstraintTopologyInserter"/>), given its own elevations, and then connected to its
/// true neighbours by Lawson flips that start only from the new vertices — the same neighbourhood an
/// incremental Delaunay insertion would give it.
///
/// Walls are never disturbed. A vertex on an existing hard constraint, or on a face at least
/// <c>wallFaceMinSlopeDeg</c> steep, keeps its elevation even when new data passes over it; edges on any
/// constraint, on the terrain boundary, or on a steep face are never flipped.
/// </summary>
internal static class TerrainDetailInserter
{
    internal sealed record Result(
        double[] Vertices,
        int VertexCount,
        int[] Faces,
        int FaceCount,
        int PointsInserted,
        int PointsOnExistingVertices,
        int PointsOutsideTerrain,
        int VerticesLifted,
        int VerticesHeldByWalls,
        int Flips);

    internal static bool TryInsert(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        double[] pointXyz,
        IReadOnlyList<ConstraintPolyline> newConstraints,
        IReadOnlyList<ConstraintPolyline> existingHardConstraints,
        IReadOnlyList<ConstraintPolyline> existingElevationConstraints,
        double tolerance,
        double pointMergeTolerance,
        double wallFaceMinSlopeDeg,
        out Result? result,
        out string? error)
    {
        result = null;
        tolerance = Math.Max(tolerance, 1e-9);

        // 1. A new point on an existing vertex adds nothing: the vertex already carries the surface there.
        //    This mirrors the rebuild's dedup, where existing vertices took priority over new spots.
        var keptPoints = new List<double>(pointXyz.Length);
        int onExisting = 0;
        if (pointXyz.Length > 0)
        {
            var vertexBounds = new Bounds2D[vertexCount];
            for (int v = 0; v < vertexCount; v++)
                vertexBounds[v] = Bounds2D.FromPoint(vertices[v * 3], vertices[(v * 3) + 1]);
            SpatialHashGrid2D vertexGrid = SpatialHashGrid2D.Build(vertexBounds);
            var scratch = new SpatialHashGrid2D.QueryScratch(vertexCount);
            var candidates = new List<int>();
            double mergeSquared = pointMergeTolerance * pointMergeTolerance;
            for (int p = 0; p < pointXyz.Length / 3; p++)
            {
                double x = pointXyz[p * 3], y = pointXyz[(p * 3) + 1];
                vertexGrid.GatherCandidates(
                    new Bounds2D(x - pointMergeTolerance, x + pointMergeTolerance, y - pointMergeTolerance, y + pointMergeTolerance),
                    candidates, scratch);
                bool duplicate = false;
                foreach (int v in candidates)
                {
                    double dx = vertices[v * 3] - x, dy = vertices[(v * 3) + 1] - y;
                    duplicate |= (dx * dx) + (dy * dy) <= mergeSquared;
                }

                if (duplicate)
                {
                    onExisting++;
                    continue;
                }

                keptPoints.Add(x);
                keptPoints.Add(y);
                keptPoints.Add(pointXyz[(p * 3) + 2]);
            }
        }

        var keptXy = new double[(keptPoints.Count / 3) * 2];
        for (int p = 0; p < keptPoints.Count / 3; p++)
        {
            keptXy[p * 2] = keptPoints[p * 3];
            keptXy[(p * 2) + 1] = keptPoints[(p * 3) + 1];
        }

        // 2. Topology: split only the faces the new data touches.
        if (!MeshConstraintTopologyInserter.TryInsert(
                new IndexedTriMesh(vertices, vertexCount, faces, faceCount), newConstraints, keptXy, tolerance,
                out IndexedTriMesh inserted, out MeshConstraintTopologyInserter.PointPlacement placement, out error))
        {
            return false;
        }

        (double[] outVertices, int outVertexCount, int[] outFaces, int outFaceCount) = inserted;

        // 3. Wall vertices keep their elevation: on an existing hard constraint, or on a steep face.
        //    Steepness is read before any lift, when new vertices still sit on their parent face's plane.
        bool[] steepBeforeLift = FeaturePolylineGraph.BuildFrozenFaceMask(outVertices, outFaces, outFaceCount, wallFaceMinSlopeDeg);
        var locked = new bool[outVertexCount];
        for (int f = 0; f < outFaceCount; f++)
        {
            if (!steepBeforeLift[f])
                continue;
            locked[outFaces[f * 3]] = locked[outFaces[(f * 3) + 1]] = locked[outFaces[(f * 3) + 2]] = true;
        }

        var hardIndex = new SegmentIndex(existingHardConstraints, tolerance);
        for (int v = 0; v < outVertexCount; v++)
        {
            if (!locked[v] && hardIndex.TryProject(outVertices[v * 3], outVertices[(v * 3) + 1], out _))
                locked[v] = true;
        }

        // 4. Elevations: new data wins wherever it lands, except on a wall.
        var pointIndex = new PointIndex(keptPoints, tolerance);
        var newIndex = new SegmentIndex(newConstraints, tolerance);
        var seeds = new List<int>();
        int lifted = 0, held = 0;
        for (int v = 0; v < outVertexCount; v++)
        {
            double x = outVertices[v * 3], y = outVertices[(v * 3) + 1];
            double z;
            bool onData = pointIndex.TryFind(x, y, out z) || newIndex.TryProject(x, y, out z);
            bool isNew = v >= vertexCount;
            if (!onData && !isNew)
                continue;

            if (locked[v])
            {
                if (onData)
                    held++;
                continue;
            }

            if (onData)
            {
                if (outVertices[(v * 3) + 2] != z)
                    lifted++;
                outVertices[(v * 3) + 2] = z;
            }

            seeds.Add(v);
        }

        // 5. Connect the new data to its neighbours. Edges on any constraint, on the boundary, or on a
        //    face that was a wall before the new data arrived are never flipped. Steepness *after* the
        //    lift is deliberately not a lock: a survey point well above its face makes that face steep,
        //    and those are exactly the faces the point must be allowed to reconnect. Flips never touch a
        //    locked face, so the pre-lift indexes stay valid throughout.
        var elevationIndex = new SegmentIndex(existingElevationConstraints, tolerance);
        double inputArea = PlanArea(vertices, faces, faceCount);
        int flips = LawsonFlipper.Run(
            outVertices, outFaces, outFaceCount, seeds,
            (a, b) => OnAnyConstraint(outVertices, a, b, hardIndex, elevationIndex, newIndex),
            face => steepBeforeLift[face]);

        // 6. The footprint must be exactly what came in; anything else means a bad insertion.
        double outputArea = PlanArea(outVertices, outFaces, outFaceCount);
        if (!double.IsFinite(outputArea) || Math.Abs(outputArea - inputArea) > Math.Max(inputArea, 1.0) * 1e-7)
        {
            error = "Local detail insertion changed the terrain footprint.";
            return false;
        }

        result = new Result(
            outVertices, outVertexCount, outFaces, outFaceCount,
            placement.Inserted, onExisting + placement.OnExistingVertex, placement.OutsideMesh,
            lifted, held, flips);
        return true;
    }

    private static bool OnAnyConstraint(double[] vertices, int a, int b, params SegmentIndex[] indexes)
    {
        double ax = vertices[a * 3], ay = vertices[(a * 3) + 1];
        double bx = vertices[b * 3], by = vertices[(b * 3) + 1];
        double mx = (ax + bx) * 0.5, my = (ay + by) * 0.5;
        foreach (SegmentIndex index in indexes)
        {
            if (index.TryProject(ax, ay, out _) && index.TryProject(bx, by, out _) && index.TryProject(mx, my, out _))
                return true;
        }

        return false;
    }

    private static double PlanArea(double[] vertices, int[] faces, int faceCount)
    {
        double area = 0.0;
        for (int f = 0; f < faceCount; f++)
        {
            int a = faces[f * 3], b = faces[(f * 3) + 1], c = faces[(f * 3) + 2];
            double cross = ((vertices[b * 3] - vertices[a * 3]) * (vertices[(c * 3) + 1] - vertices[(a * 3) + 1])) -
                           ((vertices[(b * 3) + 1] - vertices[(a * 3) + 1]) * (vertices[c * 3] - vertices[a * 3]));
            area += Math.Abs(cross) * 0.5;
        }

        return area;
    }

    /// <summary>New spot points, looked up by XY within tolerance.</summary>
    private sealed class PointIndex
    {
        private readonly List<double> _xyz;
        private readonly SpatialHashGrid2D? _grid;
        private readonly SpatialHashGrid2D.QueryScratch? _scratch;
        private readonly List<int> _candidates = new();
        private readonly double _tolerance;

        public PointIndex(List<double> xyz, double tolerance)
        {
            _xyz = xyz;
            _tolerance = tolerance;
            int count = xyz.Count / 3;
            if (count == 0)
                return;

            var bounds = new Bounds2D[count];
            for (int i = 0; i < count; i++)
                bounds[i] = new Bounds2D(xyz[i * 3] - tolerance, xyz[i * 3] + tolerance, xyz[(i * 3) + 1] - tolerance, xyz[(i * 3) + 1] + tolerance);
            _grid = SpatialHashGrid2D.Build(bounds);
            _scratch = new SpatialHashGrid2D.QueryScratch(count);
        }

        public bool TryFind(double x, double y, out double z)
        {
            z = 0.0;
            if (_grid == null)
                return false;

            _grid.GatherCandidates(Bounds2D.FromPoint(x, y), _candidates, _scratch!);
            double best = _tolerance * _tolerance;
            bool found = false;
            foreach (int i in _candidates)
            {
                double dx = _xyz[i * 3] - x, dy = _xyz[(i * 3) + 1] - y;
                double distance = (dx * dx) + (dy * dy);
                if (distance > best)
                    continue;
                best = distance;
                z = _xyz[(i * 3) + 2];
                found = true;
            }

            return found;
        }
    }

    /// <summary>Constraint polyline segments, projected onto by XY within tolerance; Z is interpolated.</summary>
    private sealed class SegmentIndex
    {
        private readonly List<(double Ax, double Ay, double Az, double Bx, double By, double Bz)> _segments = new();
        private readonly SpatialHashGrid2D? _grid;
        private readonly SpatialHashGrid2D.QueryScratch? _scratch;
        private readonly List<int> _candidates = new();
        private readonly double _tolerance;

        public SegmentIndex(IReadOnlyList<ConstraintPolyline> constraints, double tolerance)
        {
            _tolerance = tolerance;
            var bounds = new List<Bounds2D>();
            foreach (ConstraintPolyline constraint in constraints)
            {
                int count = Math.Min(constraint.PointCount, constraint.Points.Length / 3);
                int segmentCount = constraint.IsClosed && count > 2 ? count : count - 1;
                for (int s = 0; s < segmentCount; s++)
                {
                    int a = s * 3, b = ((s + 1) % count) * 3;
                    var segment = (constraint.Points[a], constraint.Points[a + 1], constraint.Points[a + 2],
                        constraint.Points[b], constraint.Points[b + 1], constraint.Points[b + 2]);
                    _segments.Add(segment);
                    bounds.Add(new Bounds2D(
                        Math.Min(segment.Item1, segment.Item4) - tolerance,
                        Math.Max(segment.Item1, segment.Item4) + tolerance,
                        Math.Min(segment.Item2, segment.Item5) - tolerance,
                        Math.Max(segment.Item2, segment.Item5) + tolerance));
                }
            }

            if (_segments.Count == 0)
                return;
            _grid = SpatialHashGrid2D.Build(bounds.ToArray());
            _scratch = new SpatialHashGrid2D.QueryScratch(_segments.Count);
        }

        public bool TryProject(double x, double y, out double z)
        {
            z = 0.0;
            if (_grid == null)
                return false;

            _grid.GatherCandidates(Bounds2D.FromPoint(x, y), _candidates, _scratch!);
            double best = _tolerance * _tolerance;
            bool found = false;
            foreach (int i in _candidates)
            {
                var s = _segments[i];
                double dx = s.Bx - s.Ax, dy = s.By - s.Ay;
                double lengthSquared = (dx * dx) + (dy * dy);
                double t = lengthSquared <= 1e-24 ? 0.0 : Math.Clamp((((x - s.Ax) * dx) + ((y - s.Ay) * dy)) / lengthSquared, 0.0, 1.0);
                double px = s.Ax + (t * dx) - x, py = s.Ay + (t * dy) - y;
                double distance = (px * px) + (py * py);
                if (distance > best)
                    continue;
                best = distance;
                z = s.Az + (t * (s.Bz - s.Az));
                found = true;
            }

            return found;
        }
    }
}
