namespace MoleHill.Core.Sculpting;

/// <summary>
/// Cursor-to-terrain ray casting for a sculpt session. Sculpting moves vertex Z only, so every face's
/// XY footprint is fixed for the session: the faces are bucketed into a uniform XY grid once, and each
/// cast walks the ray's XY projection through that grid front to back (2D DDA), testing the faces of
/// each cell against the CURRENT vertex Z. Nothing has to be rebuilt after a dab.
///
/// This replaces a Rhino <c>Intersection.MeshLine</c> on the working mesh, which rebuilds its search tree
/// whenever a vertex moves: 702 ms per mouse move on a 540,416-face terrain, i.e. the whole frame.
/// A caster is only valid for the topology it was built from; rebuild it when the faces change.
/// </summary>
public sealed class SculptRayCaster
{
    private const int MaxCellsPerAxis = 4096;
    private const double TargetFacesPerCell = 2.0;

    private readonly int[] _faces;
    private readonly double _minX;
    private readonly double _minY;
    private readonly double _maxX;
    private readonly double _maxY;
    private readonly double _cellSize;
    private readonly int _columns;
    private readonly int _rows;
    private readonly int[] _cellOffsets;
    private readonly int[] _cellFaces;
    private readonly int[] _faceStamps;
    private int _stamp;

    public SculptRayCaster(double[] vertices, int vertexCount, int[] faces, int faceCount)
    {
        _faces = faces;
        _faceStamps = new int[faceCount];

        _minX = double.MaxValue;
        _minY = double.MaxValue;
        _maxX = double.MinValue;
        _maxY = double.MinValue;
        for (int i = 0; i < vertexCount; i++)
        {
            double x = vertices[i * 3];
            double y = vertices[i * 3 + 1];
            if (x < _minX) _minX = x;
            if (x > _maxX) _maxX = x;
            if (y < _minY) _minY = y;
            if (y > _maxY) _maxY = y;
        }

        if (faceCount == 0 || vertexCount == 0)
        {
            _cellSize = 1.0;
            _columns = _rows = 0;
            _cellOffsets = new int[1];
            _cellFaces = Array.Empty<int>();
            return;
        }

        double width = Math.Max(_maxX - _minX, 1e-9);
        double height = Math.Max(_maxY - _minY, 1e-9);
        double cell = Math.Sqrt(width * height * TargetFacesPerCell / faceCount);
        cell = Math.Max(cell, Math.Max(width, height) / MaxCellsPerAxis);
        _cellSize = cell;
        _columns = Math.Max(1, (int)Math.Ceiling(width / cell));
        _rows = Math.Max(1, (int)Math.Ceiling(height / cell));

        // Two-pass CSR fill: count each face into every cell its XY bounds overlap, then place.
        var counts = new int[_columns * _rows + 1];
        for (int pass = 0; pass < 2; pass++)
        {
            int[]? cursors = null;
            if (pass == 1)
            {
                for (int c = 1; c < counts.Length; c++)
                    counts[c] += counts[c - 1];
                cursors = new int[_columns * _rows];
                Array.Copy(counts, cursors, cursors.Length);
                _cellFaces = new int[counts[^1]];
            }

            for (int f = 0; f < faceCount; f++)
            {
                FaceCellRange(vertices, f, out int c0, out int c1, out int r0, out int r1);
                for (int r = r0; r <= r1; r++)
                {
                    for (int c = c0; c <= c1; c++)
                    {
                        int index = r * _columns + c;
                        if (pass == 0)
                            counts[index + 1]++;
                        else
                            _cellFaces![cursors![index]++] = f;
                    }
                }
            }
        }

        _cellOffsets = counts;
        _cellFaces ??= Array.Empty<int>();
    }

    /// <summary>
    /// Nearest hit of the segment <c>origin + t * direction</c>, t in [0, 1], against the mesh with the
    /// given (current) vertex positions. Returns the segment parameter of the hit nearest the origin.
    /// </summary>
    public bool TryIntersect(
        double[] vertices,
        double ox, double oy, double oz,
        double dx, double dy, double dz,
        out double hitT)
    {
        hitT = double.PositiveInfinity;
        if (_columns == 0)
            return false;

        // Clip the segment's XY projection to the grid bounds (slab test).
        double tEnter = 0.0;
        double tExit = 1.0;
        if (!ClipSlab(ox, dx, _minX, _maxX, ref tEnter, ref tExit) ||
            !ClipSlab(oy, dy, _minY, _maxY, ref tEnter, ref tExit))
            return false;

        if (++_stamp == int.MaxValue)
        {
            Array.Clear(_faceStamps);
            _stamp = 1;
        }

        double startX = ox + dx * tEnter;
        double startY = oy + dy * tEnter;
        int col = Math.Clamp((int)Math.Floor((startX - _minX) / _cellSize), 0, _columns - 1);
        int row = Math.Clamp((int)Math.Floor((startY - _minY) / _cellSize), 0, _rows - 1);

        int stepX = dx > 0 ? 1 : dx < 0 ? -1 : 0;
        int stepY = dy > 0 ? 1 : dy < 0 ? -1 : 0;
        double tDeltaX = stepX != 0 ? _cellSize / Math.Abs(dx) : double.PositiveInfinity;
        double tDeltaY = stepY != 0 ? _cellSize / Math.Abs(dy) : double.PositiveInfinity;
        double tMaxX = stepX > 0 ? (_minX + (col + 1) * _cellSize - ox) / dx
            : stepX < 0 ? (_minX + col * _cellSize - ox) / dx
            : double.PositiveInfinity;
        double tMaxY = stepY > 0 ? (_minY + (row + 1) * _cellSize - oy) / dy
            : stepY < 0 ? (_minY + row * _cellSize - oy) / dy
            : double.PositiveInfinity;

        double best = double.PositiveInfinity;
        while (true)
        {
            int cell = row * _columns + col;
            for (int k = _cellOffsets[cell]; k < _cellOffsets[cell + 1]; k++)
            {
                int f = _cellFaces[k];
                if (_faceStamps[f] == _stamp)
                    continue;
                _faceStamps[f] = _stamp;

                if (TryIntersectFace(vertices, f, ox, oy, oz, dx, dy, dz, out double t) &&
                    t >= 0.0 && t <= 1.0 && t < best)
                {
                    best = t;
                }
            }

            // A face spans several cells, so a hit found here may lie beyond this cell; it is final
            // only once the walk has passed its parameter.
            double cellExit = Math.Min(Math.Min(tMaxX, tMaxY), tExit);
            if (best <= cellExit || cellExit >= tExit)
                break;

            if (tMaxX < tMaxY)
            {
                col += stepX;
                tMaxX += tDeltaX;
            }
            else
            {
                row += stepY;
                tMaxY += tDeltaY;
            }

            if (col < 0 || col >= _columns || row < 0 || row >= _rows)
                break;
        }

        if (double.IsPositiveInfinity(best))
            return false;

        hitT = best;
        return true;
    }

    /// <summary>
    /// The face under (<paramref name="x"/>, <paramref name="y"/>) and the barycentric weights of that
    /// point in it, so a point lying on the mesh can follow its Z as the vertices move. Where faces stack
    /// in plan (a wall over the ground beside it), the face whose surface passes nearest
    /// <paramref name="z"/> wins. Weights are of the face's first, second and third corner.
    /// </summary>
    public bool TryLocate(
        double[] vertices,
        double x, double y, double z,
        out int face,
        out double w0, out double w1, out double w2)
    {
        face = -1;
        w0 = w1 = w2 = 0.0;
        if (_columns == 0 || x < _minX || x > _maxX || y < _minY || y > _maxY)
            return false;

        int col = Math.Clamp((int)Math.Floor((x - _minX) / _cellSize), 0, _columns - 1);
        int row = Math.Clamp((int)Math.Floor((y - _minY) / _cellSize), 0, _rows - 1);
        int cell = row * _columns + col;
        double bestGap = double.PositiveInfinity;
        for (int k = _cellOffsets[cell]; k < _cellOffsets[cell + 1]; k++)
        {
            int f = _cellFaces[k];
            int a = _faces[f * 3] * 3, b = _faces[f * 3 + 1] * 3, c = _faces[f * 3 + 2] * 3;
            double ax = vertices[a], ay = vertices[a + 1];
            double bx = vertices[b], by = vertices[b + 1];
            double cx = vertices[c], cy = vertices[c + 1];
            double det = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
            double scale = Math.Abs(ax - cx) + Math.Abs(ay - cy) + Math.Abs(bx - cx) + Math.Abs(by - cy);
            if (Math.Abs(det) <= scale * scale * 1e-12)
                continue; // vertical in plan: a wall face has no footprint to stand on

            double l0 = ((by - cy) * (x - cx) + (cx - bx) * (y - cy)) / det;
            double l1 = ((cy - ay) * (x - cx) + (ax - cx) * (y - cy)) / det;
            double l2 = 1.0 - l0 - l1;
            const double edgeSlack = -1e-9;
            if (l0 < edgeSlack || l1 < edgeSlack || l2 < edgeSlack)
                continue;

            double gap = Math.Abs(l0 * vertices[a + 2] + l1 * vertices[b + 2] + l2 * vertices[c + 2] - z);
            if (gap < bestGap)
            {
                bestGap = gap;
                face = f;
                w0 = l0;
                w1 = l1;
                w2 = l2;
            }
        }

        return face >= 0;
    }

    private static bool ClipSlab(double origin, double direction, double min, double max, ref double tEnter, ref double tExit)
    {
        if (Math.Abs(direction) < 1e-300)
            return origin >= min && origin <= max;

        double t0 = (min - origin) / direction;
        double t1 = (max - origin) / direction;
        if (t0 > t1)
            (t0, t1) = (t1, t0);

        tEnter = Math.Max(tEnter, t0);
        tExit = Math.Min(tExit, t1);
        return tEnter <= tExit;
    }

    private void FaceCellRange(double[] vertices, int f, out int c0, out int c1, out int r0, out int r1)
    {
        int a = _faces[f * 3], b = _faces[f * 3 + 1], c = _faces[f * 3 + 2];
        double fxMin = Math.Min(vertices[a * 3], Math.Min(vertices[b * 3], vertices[c * 3]));
        double fxMax = Math.Max(vertices[a * 3], Math.Max(vertices[b * 3], vertices[c * 3]));
        double fyMin = Math.Min(vertices[a * 3 + 1], Math.Min(vertices[b * 3 + 1], vertices[c * 3 + 1]));
        double fyMax = Math.Max(vertices[a * 3 + 1], Math.Max(vertices[b * 3 + 1], vertices[c * 3 + 1]));
        c0 = Math.Clamp((int)Math.Floor((fxMin - _minX) / _cellSize), 0, _columns - 1);
        c1 = Math.Clamp((int)Math.Floor((fxMax - _minX) / _cellSize), 0, _columns - 1);
        r0 = Math.Clamp((int)Math.Floor((fyMin - _minY) / _cellSize), 0, _rows - 1);
        r1 = Math.Clamp((int)Math.Floor((fyMax - _minY) / _cellSize), 0, _rows - 1);
    }

    /// <summary>Two-sided Möller–Trumbore; t is the segment parameter.</summary>
    private bool TryIntersectFace(
        double[] v,
        int f,
        double ox, double oy, double oz,
        double dx, double dy, double dz,
        out double t)
    {
        t = 0.0;
        int a = _faces[f * 3] * 3, b = _faces[f * 3 + 1] * 3, c = _faces[f * 3 + 2] * 3;
        double e1x = v[b] - v[a], e1y = v[b + 1] - v[a + 1], e1z = v[b + 2] - v[a + 2];
        double e2x = v[c] - v[a], e2y = v[c + 1] - v[a + 1], e2z = v[c + 2] - v[a + 2];

        double px = dy * e2z - dz * e2y;
        double py = dz * e2x - dx * e2z;
        double pz = dx * e2y - dy * e2x;
        double det = e1x * px + e1y * py + e1z * pz;
        double scale = (Math.Abs(e1x) + Math.Abs(e1y) + Math.Abs(e1z)) *
                       (Math.Abs(e2x) + Math.Abs(e2y) + Math.Abs(e2z)) *
                       (Math.Abs(dx) + Math.Abs(dy) + Math.Abs(dz));
        if (Math.Abs(det) <= scale * 1e-15)
            return false;

        double inv = 1.0 / det;
        double sx = ox - v[a], sy = oy - v[a + 1], sz = oz - v[a + 2];
        double u = (sx * px + sy * py + sz * pz) * inv;
        if (u < 0.0 || u > 1.0)
            return false;

        double qx = sy * e1z - sz * e1y;
        double qy = sz * e1x - sx * e1z;
        double qz = sx * e1y - sy * e1x;
        double w = (dx * qx + dy * qy + dz * qz) * inv;
        if (w < 0.0 || u + w > 1.0)
            return false;

        t = (e2x * qx + e2y * qy + e2z * qz) * inv;
        return true;
    }
}
