using TriangleNet;
using TriangleNet.Geometry;
using TriangleNet.Meshing;
using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

/// <summary>
/// Grades a terrain mesh by flattening areas inside boundary curves
/// to a target elevation, with controlled slope transitions.
/// Re-triangulates the entire mesh with pad boundaries as constrained edges.
/// </summary>
public static class PadGrader
{
    public sealed class PadBoundary
    {
        public double[] XyVertices { get; }
        public int VertexCount { get; }
        public double TargetZ { get; }
        public double SlopeAngleDeg { get; }
        public double MaxDistance { get; }

        public PadBoundary(double[] xyVertices, int vertexCount, double targetZ,
                           double slopeAngleDeg = 33.0, double maxDistance = 0.0)
        {
            XyVertices = xyVertices;
            VertexCount = vertexCount;
            TargetZ = targetZ;
            SlopeAngleDeg = Math.Max(0.1, Math.Min(89.9, slopeAngleDeg));
            MaxDistance = maxDistance;
        }
    }

    public sealed class LockCurve
    {
        public double[] XyVertices { get; }
        public int VertexCount { get; }

        public LockCurve(double[] xyVertices, int vertexCount)
        {
            XyVertices = xyVertices;
            VertexCount = vertexCount;
        }
    }

    /// <summary>
    /// Apply pad grading to a terrain mesh.
    /// Each pad carries its own slope angle and max distance.
    /// Later pads in the array override earlier ones in overlapping zones.
    /// </summary>
    public static GradingResult? Grade(
        double[] vertices, int vertexCount,
        int[] faces, int faceCount,
        PadBoundary[] pads,
        LockCurve[]? lockCurves,
        double maxArea,
        double minAngle,
        out string? errorMessage)
    {
        errorMessage = null;
        const double dedupTol = 1e-3;

        if (pads.Length == 0)
        {
            errorMessage = "No pad boundaries provided.";
            return null;
        }

        // ── Step 1: Build combined vertex + segment set ──
        var xyList = new List<double>(vertexCount * 2);
        var zList = new List<double>(vertexCount);
        var segList = new List<(int a, int b)>();

        var vertHash = new SpatialHash(dedupTol);

        for (int i = 0; i < vertexCount; i++)
        {
            double x = vertices[i * 3];
            double y = vertices[i * 3 + 1];
            xyList.Add(x);
            xyList.Add(y);
            zList.Add(vertices[i * 3 + 2]);
            vertHash.Insert(i, x, y);
        }

        var faceGrid = new FaceGrid(vertices, vertexCount, faces, faceCount);

        // Add mesh boundary edges as constraints (keeps triangulation within original mesh)
        var edgeFaceCount = new Dictionary<long, int>();
        for (int f = 0; f < faceCount; f++)
        {
            int a = faces[f * 3], b = faces[f * 3 + 1], c = faces[f * 3 + 2];
            IncrEdge(edgeFaceCount, a, b);
            IncrEdge(edgeFaceCount, b, c);
            IncrEdge(edgeFaceCount, c, a);
        }
        foreach (var kvp in edgeFaceCount)
        {
            if (kvp.Value == 1) // naked edge = mesh boundary
            {
                int a = (int)(kvp.Key >> 32);
                int b = (int)(kvp.Key & 0xFFFFFFFFL);
                segList.Add((a, b));
            }
        }

        foreach (var pad in pads)
        {
            var padIndices = new int[pad.VertexCount];
            for (int i = 0; i < pad.VertexCount; i++)
            {
                double px = pad.XyVertices[i * 2];
                double py = pad.XyVertices[i * 2 + 1];

                int near = vertHash.FindNearest(xyList, px, py, dedupTol);
                if (near >= 0)
                {
                    padIndices[i] = near;
                }
                else
                {
                    padIndices[i] = zList.Count;
                    xyList.Add(px);
                    xyList.Add(py);
                    zList.Add(faceGrid.InterpolateZ(px, py));
                    vertHash.Insert(padIndices[i], px, py);
                }
            }

            for (int i = 0; i < pad.VertexCount; i++)
            {
                int a = padIndices[i];
                int b = padIndices[(i + 1) % pad.VertexCount];
                if (a != b) segList.Add((a, b));
            }
        }

        if (lockCurves != null)
        {
            foreach (var lc in lockCurves)
            {
                var lcIndices = new int[lc.VertexCount];
                for (int i = 0; i < lc.VertexCount; i++)
                {
                    double lx = lc.XyVertices[i * 2];
                    double ly = lc.XyVertices[i * 2 + 1];

                    int near = vertHash.FindNearest(xyList, lx, ly, dedupTol);
                    if (near >= 0)
                    {
                        lcIndices[i] = near;
                    }
                    else
                    {
                        lcIndices[i] = zList.Count;
                        xyList.Add(lx);
                        xyList.Add(ly);
                        zList.Add(faceGrid.InterpolateZ(lx, ly));
                        vertHash.Insert(lcIndices[i], lx, ly);
                    }
                }

                for (int i = 0; i < lc.VertexCount - 1; i++)
                {
                    if (lcIndices[i] != lcIndices[i + 1])
                        segList.Add((lcIndices[i], lcIndices[i + 1]));
                }
            }
        }

        // ── Step 2: Triangulate ──
        int totalVerts = zList.Count;
        if (totalVerts < 3)
        {
            errorMessage = "Too few vertices for triangulation.";
            return null;
        }

        var triMesh = TriangulationHelper.Triangulate(
            xyList, totalVerts, segList,
            maxArea, minAngle,
            out string? triWarning,
            convex: false);

        if (triMesh == null)
        {
            errorMessage = triWarning ?? "Triangulation failed.";
            return null;
        }

        if (triWarning != null)
            errorMessage = triWarning;

        // ── Step 3: Map Triangle.NET output ──
        var outVerts = triMesh.Vertices.ToList();
        var outTris = triMesh.Triangles.ToList();
        int outVertCount = outVerts.Count;
        int outFaceCount = outTris.Count;

        var outXy = new double[outVertCount * 2];
        var origZ = new double[outVertCount];
        var newZ = new double[outVertCount];
        var idToIdx = new Dictionary<int, int>(outVertCount);

        for (int i = 0; i < outVertCount; i++)
        {
            var mv = outVerts[i];
            idToIdx[mv.ID] = i;
            outXy[i * 2] = mv.X;
            outXy[i * 2 + 1] = mv.Y;

            if (mv.ID >= 0 && mv.ID < totalVerts)
            {
                origZ[i] = zList[mv.ID];
                newZ[i] = zList[mv.ID];
            }
            else
            {
                double iz = faceGrid.InterpolateZ(mv.X, mv.Y);
                origZ[i] = iz;
                newZ[i] = iz;
            }
        }

        // ── Step 4: Apply grading — nearest pad wins for transition zones ──
        // Compute global influence bbox for fast filtering
        double globalMinX = double.MaxValue, globalMaxX = double.MinValue;
        double globalMinY = double.MaxValue, globalMaxY = double.MinValue;
        double globalMaxTrans = 0;

        for (int p = 0; p < pads.Length; p++)
        {
            var pad = pads[p];
            double sr = Math.Tan(pad.SlopeAngleDeg * Math.PI / 180.0);

            for (int i = 0; i < pad.VertexCount; i++)
            {
                double vx = pad.XyVertices[i * 2], vy = pad.XyVertices[i * 2 + 1];
                if (vx < globalMinX) globalMinX = vx; if (vx > globalMaxX) globalMaxX = vx;
                if (vy < globalMinY) globalMinY = vy; if (vy > globalMaxY) globalMaxY = vy;
            }

            double maxZDiff = 0;
            for (int i = 0; i < outVertCount; i++)
            {
                double dz = Math.Abs(origZ[i] - pad.TargetZ);
                if (dz > maxZDiff) maxZDiff = dz;
            }
            double td = sr > 1e-12 ? maxZDiff / sr : 100.0;
            if (pad.MaxDistance > 0) td = Math.Min(td, pad.MaxDistance);
            if (td > globalMaxTrans) globalMaxTrans = td;
        }

        globalMinX -= globalMaxTrans; globalMaxX += globalMaxTrans;
        globalMinY -= globalMaxTrans; globalMaxY += globalMaxTrans;

        for (int i = 0; i < outVertCount; i++)
        {
            double px = outXy[i * 2], py = outXy[i * 2 + 1];

            if (px < globalMinX || px > globalMaxX || py < globalMinY || py > globalMaxY)
                continue;

            // Check if inside any pad (last pad wins for overlapping interiors)
            int insidePadIdx = -1;
            for (int p = pads.Length - 1; p >= 0; p--)
            {
                if (PointInPolygon(px, py, pads[p].XyVertices, pads[p].VertexCount))
                {
                    insidePadIdx = p;
                    break;
                }
            }

            if (insidePadIdx >= 0)
            {
                newZ[i] = pads[insidePadIdx].TargetZ;
                continue;
            }

            // Transition zone: find nearest pad boundary, use that pad's settings
            double nearestDist = double.MaxValue;
            int nearestPadIdx = -1;
            for (int p = 0; p < pads.Length; p++)
            {
                double dist = DistToPolygon(px, py, pads[p].XyVertices, pads[p].VertexCount);
                if (dist < nearestDist) { nearestDist = dist; nearestPadIdx = p; }
            }

            if (nearestPadIdx >= 0)
            {
                var pad = pads[nearestPadIdx];
                double slopeRatio = Math.Tan(pad.SlopeAngleDeg * Math.PI / 180.0);
                double dz = origZ[i] - pad.TargetZ;
                double absDz = Math.Abs(dz);

                double neededDist = slopeRatio > 1e-12 ? absDz / slopeRatio : double.MaxValue;
                if (pad.MaxDistance > 0) neededDist = Math.Min(neededDist, pad.MaxDistance);

                if (nearestDist < neededDist)
                {
                    double rise = nearestDist * slopeRatio;
                    if (rise < absDz)
                        newZ[i] = pad.TargetZ + Math.Sign(dz) * rise;
                }
            }
        }

        // ── Step 5: Build output arrays ──
        var finalVerts = new double[outVertCount * 3];
        for (int i = 0; i < outVertCount; i++)
        {
            finalVerts[i * 3] = outXy[i * 2];
            finalVerts[i * 3 + 1] = outXy[i * 2 + 1];
            finalVerts[i * 3 + 2] = newZ[i];
        }

        var finalFaces = new int[outFaceCount * 3];
        for (int i = 0; i < outFaceCount; i++)
        {
            var tri = outTris[i];
            finalFaces[i * 3] = idToIdx.GetValueOrDefault(tri.GetVertex(0).ID, 0);
            finalFaces[i * 3 + 1] = idToIdx.GetValueOrDefault(tri.GetVertex(1).ID, 0);
            finalFaces[i * 3 + 2] = idToIdx.GetValueOrDefault(tri.GetVertex(2).ID, 0);
        }

        // ── Step 6: Compute volumes ──
        double cutVol = 0, fillVol = 0;
        for (int f = 0; f < outFaceCount; f++)
        {
            int i0 = finalFaces[f * 3], i1 = finalFaces[f * 3 + 1], i2 = finalFaces[f * 3 + 2];

            double area2d = Math.Abs(
                (outXy[i1 * 2] - outXy[i0 * 2]) * (outXy[i2 * 2 + 1] - outXy[i0 * 2 + 1])
              - (outXy[i2 * 2] - outXy[i0 * 2]) * (outXy[i1 * 2 + 1] - outXy[i0 * 2 + 1])
            ) * 0.5;

            double dz0 = newZ[i0] - origZ[i0];
            double dz1 = newZ[i1] - origZ[i1];
            double dz2 = newZ[i2] - origZ[i2];
            double avgDz = (dz0 + dz1 + dz2) / 3.0;

            double vol = area2d * avgDz;
            if (vol > 0) fillVol += vol;
            else cutVol += -vol;
        }

        // ── Step 7: Daylight line ──
        var daylightPts = new List<double>();
        var processedEdges = new HashSet<long>();

        for (int f = 0; f < outFaceCount; f++)
        {
            int i0 = finalFaces[f * 3], i1 = finalFaces[f * 3 + 1], i2 = finalFaces[f * 3 + 2];
            CheckDaylightEdge(i0, i1, outXy, newZ, origZ, processedEdges, daylightPts);
            CheckDaylightEdge(i1, i2, outXy, newZ, origZ, processedEdges, daylightPts);
            CheckDaylightEdge(i2, i0, outXy, newZ, origZ, processedEdges, daylightPts);
        }

        return new GradingResult(
            finalVerts, outVertCount,
            finalFaces, outFaceCount,
            cutVol, fillVol,
            daylightPts.ToArray(), daylightPts.Count / 3);
    }

    private static void CheckDaylightEdge(int a, int b,
        double[] xy, double[] newZ, double[] origZ,
        HashSet<long> processed, List<double> pts)
    {
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        if (!processed.Add(key)) return;

        double dzA = newZ[a] - origZ[a];
        double dzB = newZ[b] - origZ[b];
        const double threshold = 0.001;

        if ((dzA > threshold && dzB < -threshold) || (dzA < -threshold && dzB > threshold))
        {
            double t = dzA / (dzA - dzB);
            pts.Add(xy[a * 2] + t * (xy[b * 2] - xy[a * 2]));
            pts.Add(xy[a * 2 + 1] + t * (xy[b * 2 + 1] - xy[a * 2 + 1]));
            pts.Add(newZ[a] + t * (newZ[b] - newZ[a]));
        }
        else if (Math.Abs(dzA) <= threshold && Math.Abs(dzB) > threshold)
        {
            pts.Add(xy[a * 2]); pts.Add(xy[a * 2 + 1]); pts.Add(newZ[a]);
        }
        else if (Math.Abs(dzB) <= threshold && Math.Abs(dzA) > threshold)
        {
            pts.Add(xy[b * 2]); pts.Add(xy[b * 2 + 1]); pts.Add(newZ[b]);
        }
    }

    // ── Spatial data structures ──

    internal sealed class SpatialHash
    {
        private readonly double _cellSize;
        private readonly double _invCell;
        private readonly Dictionary<long, List<int>> _grid = new();

        public SpatialHash(double tolerance)
        {
            _cellSize = Math.Max(tolerance * 2, 1e-10);
            _invCell = 1.0 / _cellSize;
        }

        public void Insert(int index, double x, double y)
        {
            long key = CellKey(x, y);
            if (!_grid.TryGetValue(key, out var list))
            {
                list = new List<int>();
                _grid[key] = list;
            }
            list.Add(index);
        }

        public int FindNearest(List<double> xyList, double px, double py, double tolerance)
        {
            double tolSq = tolerance * tolerance;
            long cx = (long)Math.Floor(px * _invCell);
            long cy = (long)Math.Floor(py * _invCell);

            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                {
                    long key = PackKey(cx + dx, cy + dy);
                    if (_grid.TryGetValue(key, out var indices))
                        foreach (int idx in indices)
                        {
                            double ex = xyList[idx * 2], ey = xyList[idx * 2 + 1];
                            double d2 = (px - ex) * (px - ex) + (py - ey) * (py - ey);
                            if (d2 < tolSq) return idx;
                        }
                }
            return -1;
        }

        private long CellKey(double x, double y) =>
            PackKey((long)Math.Floor(x * _invCell), (long)Math.Floor(y * _invCell));

        private static long PackKey(long cx, long cy) =>
            (cx * 0x100000001L) ^ (cy * 0x27d4eb2dL);
    }

    internal sealed class FaceGrid
    {
        private readonly double[] _verts;
        private readonly int[] _faces;
        private readonly int _faceCount;
        private readonly Dictionary<long, List<int>> _grid;
        private readonly double _cellSize;
        private readonly double _invCell;

        public FaceGrid(double[] vertices, int vertexCount, int[] faces, int faceCount)
        {
            _verts = vertices;
            _faces = faces;
            _faceCount = faceCount;

            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;
            for (int i = 0; i < vertexCount; i++)
            {
                double x = vertices[i * 3], y = vertices[i * 3 + 1];
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
            }

            double span = Math.Max(maxX - minX, maxY - minY);
            int gridRes = Math.Max(1, (int)Math.Sqrt(faceCount / 4.0));
            _cellSize = Math.Max(span / gridRes, 1e-6);
            _invCell = 1.0 / _cellSize;

            _grid = new Dictionary<long, List<int>>(faceCount);
            for (int f = 0; f < faceCount; f++)
            {
                int i0 = faces[f * 3], i1 = faces[f * 3 + 1], i2 = faces[f * 3 + 2];
                double x0 = vertices[i0 * 3], y0 = vertices[i0 * 3 + 1];
                double x1 = vertices[i1 * 3], y1 = vertices[i1 * 3 + 1];
                double x2 = vertices[i2 * 3], y2 = vertices[i2 * 3 + 1];

                long cMinX = (long)Math.Floor(Math.Min(x0, Math.Min(x1, x2)) * _invCell);
                long cMaxX = (long)Math.Floor(Math.Max(x0, Math.Max(x1, x2)) * _invCell);
                long cMinY = (long)Math.Floor(Math.Min(y0, Math.Min(y1, y2)) * _invCell);
                long cMaxY = (long)Math.Floor(Math.Max(y0, Math.Max(y1, y2)) * _invCell);

                for (long cy = cMinY; cy <= cMaxY; cy++)
                    for (long cx = cMinX; cx <= cMaxX; cx++)
                    {
                        long key = (cx * 0x100000001L) ^ (cy * 0x27d4eb2dL);
                        if (!_grid.TryGetValue(key, out var list))
                        {
                            list = new List<int>();
                            _grid[key] = list;
                        }
                        list.Add(f);
                    }
            }
        }

        public double InterpolateZ(double px, double py)
        {
            const double tol = 1e-4;
            long cx = (long)Math.Floor(px * _invCell);
            long cy = (long)Math.Floor(py * _invCell);

            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                {
                    long key = ((cx + dx) * 0x100000001L) ^ ((cy + dy) * 0x27d4eb2dL);
                    if (!_grid.TryGetValue(key, out var faceIndices)) continue;

                    foreach (int f in faceIndices)
                    {
                        int i0 = _faces[f * 3], i1 = _faces[f * 3 + 1], i2 = _faces[f * 3 + 2];
                        double x0 = _verts[i0 * 3], y0 = _verts[i0 * 3 + 1], z0 = _verts[i0 * 3 + 2];
                        double x1 = _verts[i1 * 3], y1 = _verts[i1 * 3 + 1], z1 = _verts[i1 * 3 + 2];
                        double x2 = _verts[i2 * 3], y2 = _verts[i2 * 3 + 1], z2 = _verts[i2 * 3 + 2];

                        double denom = (y1 - y2) * (x0 - x2) + (x2 - x1) * (y0 - y2);
                        if (Math.Abs(denom) < 1e-12) continue;

                        double w0 = ((y1 - y2) * (px - x2) + (x2 - x1) * (py - y2)) / denom;
                        double w1 = ((y2 - y0) * (px - x2) + (x0 - x2) * (py - y2)) / denom;
                        double w2 = 1.0 - w0 - w1;

                        if (w0 >= -tol && w1 >= -tol && w2 >= -tol)
                            return w0 * z0 + w1 * z1 + w2 * z2;
                    }
                }

            return NearestVertexZ(px, py);
        }

        private double NearestVertexZ(double px, double py)
        {
            double nearestZ = 0, nearestDistSq = double.MaxValue;
            int vCount = _verts.Length / 3;
            for (int i = 0; i < vCount; i++)
            {
                double dx = _verts[i * 3] - px, dy = _verts[i * 3 + 1] - py;
                double distSq = dx * dx + dy * dy;
                if (distSq < nearestDistSq) { nearestDistSq = distSq; nearestZ = _verts[i * 3 + 2]; }
            }
            return nearestZ;
        }
    }

    // ── Geometry helpers (public for cross-assembly use) ──

    public static bool PointInPolygon(double px, double py, double[] polyXy, int polyVertCount)
    {
        bool inside = false;
        for (int i = 0, j = polyVertCount - 1; i < polyVertCount; j = i++)
        {
            double xi = polyXy[i * 2], yi = polyXy[i * 2 + 1];
            double xj = polyXy[j * 2], yj = polyXy[j * 2 + 1];

            if (((yi > py) != (yj > py)) &&
                (px < (xj - xi) * (py - yi) / (yj - yi) + xi))
                inside = !inside;
        }
        return inside;
    }

    public static double DistToPolygon(double px, double py, double[] polyXy, int polyVertCount)
    {
        double minDist = double.MaxValue;
        for (int i = 0, j = polyVertCount - 1; i < polyVertCount; j = i++)
        {
            double dist = DistToSegment(px, py,
                polyXy[j * 2], polyXy[j * 2 + 1],
                polyXy[i * 2], polyXy[i * 2 + 1]);
            if (dist < minDist) minDist = dist;
        }
        return minDist;
    }

    private static double DistToSegment(double px, double py,
                                         double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay;
        double lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-20)
            return Math.Sqrt((px - ax) * (px - ax) + (py - ay) * (py - ay));

        double t = Math.Max(0, Math.Min(1, ((px - ax) * dx + (py - ay) * dy) / lenSq));
        double cx = ax + t * dx, cy = ay + t * dy;
        return Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
    }

    public static int FindNearVertex(List<double> xyList, double px, double py, double tolerance)
    {
        double tolSq = tolerance * tolerance;
        int count = xyList.Count / 2;
        for (int i = 0; i < count; i++)
        {
            double dx = xyList[i * 2] - px, dy = xyList[i * 2 + 1] - py;
            if (dx * dx + dy * dy < tolSq) return i;
        }
        return -1;
    }

    private static void IncrEdge(Dictionary<long, int> dict, int a, int b)
    {
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        dict[key] = dict.GetValueOrDefault(key, 0) + 1;
    }

    public static double InterpolateZ(double[] vertices, int[] faces, int faceCount,
                                         double px, double py)
    {
        const double tol = 1e-4;
        for (int f = 0; f < faceCount; f++)
        {
            int i0 = faces[f * 3], i1 = faces[f * 3 + 1], i2 = faces[f * 3 + 2];
            double x0 = vertices[i0 * 3], y0 = vertices[i0 * 3 + 1], z0 = vertices[i0 * 3 + 2];
            double x1 = vertices[i1 * 3], y1 = vertices[i1 * 3 + 1], z1 = vertices[i1 * 3 + 2];
            double x2 = vertices[i2 * 3], y2 = vertices[i2 * 3 + 1], z2 = vertices[i2 * 3 + 2];

            double denom = (y1 - y2) * (x0 - x2) + (x2 - x1) * (y0 - y2);
            if (Math.Abs(denom) < 1e-12) continue;

            double w0 = ((y1 - y2) * (px - x2) + (x2 - x1) * (py - y2)) / denom;
            double w1 = ((y2 - y0) * (px - x2) + (x0 - x2) * (py - y2)) / denom;
            double w2 = 1.0 - w0 - w1;

            if (w0 >= -tol && w1 >= -tol && w2 >= -tol)
                return w0 * z0 + w1 * z1 + w2 * z2;
        }

        double nearestZ = 0, nearestDistSq = double.MaxValue;
        int vCount = vertices.Length / 3;
        for (int i = 0; i < vCount; i++)
        {
            double dx = vertices[i * 3] - px, dy = vertices[i * 3 + 1] - py;
            double distSq = dx * dx + dy * dy;
            if (distSq < nearestDistSq) { nearestDistSq = distSq; nearestZ = vertices[i * 3 + 2]; }
        }
        return nearestZ;
    }
}
