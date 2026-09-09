namespace MoleHill.Core.Analysis;

/// <summary>
/// Fast XY-to-Z lookup for 2.5D triangle meshes.
/// </summary>
public sealed class MeshHeightProjector
{
    private const long HashPrimeX = 0x100000001L;
    private const long HashPrimeY = 0x27d4eb2dL;
    private const double NearVerticalNormalZRatio = 1e-6;

    private readonly double[] _vertices;
    private readonly int[] _faces;
    // Flat CSR cells: key -> slot, slot -> [_cellStart[slot], _cellStart[slot + 1]) in _cellFaces.
    // A List per occupied cell allocated millions of small objects on a large terrain, and reserving the
    // dictionary by face count over-reserved it by about 4x (the default cell size targets faceCount / 4
    // cells). Both are built once and never mutated afterwards, so a flat layout costs nothing.
    private readonly Dictionary<long, int> _cellSlots;
    private int[] _cellStart = Array.Empty<int>();
    private int[] _cellFaces = Array.Empty<int>();
    private readonly double _invCell;

    public enum ProjectionStatus
    {
        Projected,
        OutsideMesh,
        RequiresFallback
    }

    public double BoundsDiagonal { get; }

    public MeshHeightProjector(double[] vertices, int vertexCount, int[] faces, int faceCount, double cellSizeHint = 0.0)
    {
        _vertices = vertices;
        _faces = faces;
        _cellSlots = new Dictionary<long, int>(Math.Max(16, faceCount / 2));

        if (vertexCount <= 0 || faceCount <= 0)
        {
            _cellStart = new int[1];
            _invCell = 1.0;
            BoundsDiagonal = 0.0;
            return;
        }

        double minX = double.MaxValue;
        double maxX = double.MinValue;
        double minY = double.MaxValue;
        double maxY = double.MinValue;
        for (int index = 0; index < vertexCount; index++)
        {
            double x = vertices[index * 3];
            double y = vertices[index * 3 + 1];
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
        }

        double spanX = maxX - minX;
        double spanY = maxY - minY;
        double span = Math.Max(spanX, spanY);
        BoundsDiagonal = Math.Sqrt((spanX * spanX) + (spanY * spanY));
        int gridResolution = Math.Max(1, (int)Math.Sqrt(faceCount / 4.0));
        double requestedCellSize = cellSizeHint > 0.0
            ? cellSizeHint
            : span / gridResolution;
        double cellSize = Engine.ScaleAwareTolerance.ResolveLength(requestedCellSize, span);
        _invCell = 1.0 / cellSize;

        BuildCells(faceCount);
    }

    /// <summary>
    /// Two passes over the faces: assign a slot to every occupied cell and count its memberships, then
    /// fill. Faces are visited in source order in both, so each cell's run stays ascending — the order
    /// the per-cell lists had, which the first-match rule in <see cref="TryProjectZ"/> depends on.
    /// </summary>
    private void BuildCells(int faceCount)
    {
        var counts = new List<int>(Math.Max(16, faceCount / 2));
        for (int face = 0; face < faceCount; face++)
        {
            if (!TryGetFaceCellRange(face, out long minCellX, out long maxCellX, out long minCellY, out long maxCellY))
                continue;

            for (long cellY = minCellY; cellY <= maxCellY; cellY++)
            {
                for (long cellX = minCellX; cellX <= maxCellX; cellX++)
                {
                    long key = HashCell(cellX, cellY);
                    if (!_cellSlots.TryGetValue(key, out int slot))
                    {
                        slot = counts.Count;
                        _cellSlots[key] = slot;
                        counts.Add(0);
                    }

                    counts[slot]++;
                }
            }
        }

        _cellStart = new int[counts.Count + 1];
        int running = 0;
        for (int slot = 0; slot < counts.Count; slot++)
        {
            _cellStart[slot] = running;
            running += counts[slot];
        }

        _cellStart[counts.Count] = running;

        _cellFaces = new int[running];
        var cursor = new int[counts.Count];
        Array.Copy(_cellStart, cursor, counts.Count);
        for (int face = 0; face < faceCount; face++)
        {
            if (!TryGetFaceCellRange(face, out long minCellX, out long maxCellX, out long minCellY, out long maxCellY))
                continue;

            for (long cellY = minCellY; cellY <= maxCellY; cellY++)
            {
                for (long cellX = minCellX; cellX <= maxCellX; cellX++)
                    _cellFaces[cursor[_cellSlots[HashCell(cellX, cellY)]]++] = face;
            }
        }
    }

    public bool TryProjectZ(
        double x,
        double y,
        double sampleZ,
        double tolerance,
        out double z,
        out ProjectionStatus status)
    {
        z = 0.0;
        status = ProjectionStatus.OutsideMesh;

        double insideTolerance = Engine.ScaleAwareTolerance.ResolveLength(tolerance, BoundsDiagonal);
        double distinctZTolerance = Engine.ScaleAwareTolerance.ResolveLength(tolerance * 4.0, BoundsDiagonal);
        bool found = false;
        bool requiresFallback = false;
        long cellX = (long)Math.Floor(x * _invCell);
        long cellY = (long)Math.Floor(y * _invCell);

        // Faces are registered in every grid cell touched by their XY bounding box, so a triangle
        // containing (x,y) must already be in the owning cell. The former 3x3 scan retested the same
        // faces from neighbouring buckets up to nine times on regular meshes.
        {
            foreach (int face in CellFaces(cellX, cellY))
            {
                if (!TryReadFace(face, out var triangle))
                    continue;

                if (IsNearVerticalOrDegenerate(triangle))
                {
                    if (IsPointNearFaceFootprint(x, y, triangle, insideTolerance))
                        requiresFallback = true;
                    continue;
                }

                if (!TryGetBarycentric(x, y, triangle, out double w0, out double w1, out double w2))
                    continue;

                double barycentricTolerance = BarycentricTolerance(triangle, insideTolerance);
                if (w0 < -barycentricTolerance || w1 < -barycentricTolerance || w2 < -barycentricTolerance)
                    continue;

                double faceZ = (w0 * triangle.Z0) + (w1 * triangle.Z1) + (w2 * triangle.Z2);
                if (!found)
                {
                    z = faceZ;
                    found = true;
                    continue;
                }

                if (Math.Abs(faceZ - z) > distinctZTolerance)
                {
                    status = ProjectionStatus.RequiresFallback;
                    z = 0.0;
                    return false;
                }

                if (Math.Abs(faceZ - sampleZ) < Math.Abs(z - sampleZ))
                    z = faceZ;
            }
        }

        if (requiresFallback)
        {
            status = ProjectionStatus.RequiresFallback;
            z = 0.0;
            return false;
        }

        if (!found)
            return false;

        status = ProjectionStatus.Projected;
        return true;
    }

    private bool TryGetFaceCellRange(int face, out long minCellX, out long maxCellX, out long minCellY, out long maxCellY)
    {
        minCellX = maxCellX = minCellY = maxCellY = 0;
        if (!TryReadFace(face, out var triangle))
            return false;

        minCellX = (long)Math.Floor(Math.Min(triangle.X0, Math.Min(triangle.X1, triangle.X2)) * _invCell);
        maxCellX = (long)Math.Floor(Math.Max(triangle.X0, Math.Max(triangle.X1, triangle.X2)) * _invCell);
        minCellY = (long)Math.Floor(Math.Min(triangle.Y0, Math.Min(triangle.Y1, triangle.Y2)) * _invCell);
        maxCellY = (long)Math.Floor(Math.Max(triangle.Y0, Math.Max(triangle.Y1, triangle.Y2)) * _invCell);
        return true;
    }

    /// <summary>Faces registered in cell (cellX, cellY), ascending. Empty when unoccupied.</summary>
    private ReadOnlySpan<int> CellFaces(long cellX, long cellY)
    {
        if (!_cellSlots.TryGetValue(HashCell(cellX, cellY), out int slot))
            return ReadOnlySpan<int>.Empty;

        int start = _cellStart[slot];
        return _cellFaces.AsSpan(start, _cellStart[slot + 1] - start);
    }

    private bool TryReadFace(int face, out Triangle triangle)
    {
        triangle = default;
        int offset = face * 3;
        if (offset < 0 || offset + 2 >= _faces.Length)
            return false;

        int i0 = _faces[offset];
        int i1 = _faces[offset + 1];
        int i2 = _faces[offset + 2];
        if (!IsValidVertexIndex(i0) || !IsValidVertexIndex(i1) || !IsValidVertexIndex(i2))
            return false;

        triangle = new Triangle(
            _vertices[i0 * 3],
            _vertices[i0 * 3 + 1],
            _vertices[i0 * 3 + 2],
            _vertices[i1 * 3],
            _vertices[i1 * 3 + 1],
            _vertices[i1 * 3 + 2],
            _vertices[i2 * 3],
            _vertices[i2 * 3 + 1],
            _vertices[i2 * 3 + 2]);
        return true;
    }

    private bool IsValidVertexIndex(int index) => index >= 0 && (index * 3) + 2 < _vertices.Length;

    private static bool TryGetBarycentric(
        double x,
        double y,
        Triangle triangle,
        out double w0,
        out double w1,
        out double w2)
    {
        double denom =
            ((triangle.Y1 - triangle.Y2) * (triangle.X0 - triangle.X2)) +
            ((triangle.X2 - triangle.X1) * (triangle.Y0 - triangle.Y2));
        double xyEdgeScale = MaxXyEdgeLength(triangle);
        double denominatorFloor = Math.Max(xyEdgeScale * xyEdgeScale * 1e-12, double.Epsilon);
        if (Math.Abs(denom) <= denominatorFloor)
        {
            w0 = w1 = w2 = 0.0;
            return false;
        }

        w0 = (((triangle.Y1 - triangle.Y2) * (x - triangle.X2)) + ((triangle.X2 - triangle.X1) * (y - triangle.Y2))) / denom;
        w1 = (((triangle.Y2 - triangle.Y0) * (x - triangle.X2)) + ((triangle.X0 - triangle.X2) * (y - triangle.Y2))) / denom;
        w2 = 1.0 - w0 - w1;
        return true;
    }

    private static bool IsNearVerticalOrDegenerate(Triangle triangle)
    {
        double e1x = triangle.X1 - triangle.X0;
        double e1y = triangle.Y1 - triangle.Y0;
        double e1z = triangle.Z1 - triangle.Z0;
        double e2x = triangle.X2 - triangle.X0;
        double e2y = triangle.Y2 - triangle.Y0;
        double e2z = triangle.Z2 - triangle.Z0;

        double nx = (e1y * e2z) - (e1z * e2y);
        double ny = (e1z * e2x) - (e1x * e2z);
        double nz = (e1x * e2y) - (e1y * e2x);
        double normalLength = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
        double edge1Length = Math.Sqrt((e1x * e1x) + (e1y * e1y) + (e1z * e1z));
        double edge2Length = Math.Sqrt((e2x * e2x) + (e2y * e2y) + (e2z * e2z));
        double edge3X = triangle.X2 - triangle.X1;
        double edge3Y = triangle.Y2 - triangle.Y1;
        double edge3Z = triangle.Z2 - triangle.Z1;
        double edge3Length = Math.Sqrt((edge3X * edge3X) + (edge3Y * edge3Y) + (edge3Z * edge3Z));
        double edgeScale = Math.Max(edge1Length, Math.Max(edge2Length, edge3Length));
        double normalFloor = Math.Max(edgeScale * edgeScale * 1e-12, double.Epsilon);
        if (!double.IsFinite(normalLength) || normalLength <= normalFloor)
            return true;

        return Math.Abs(nz) / normalLength < NearVerticalNormalZRatio;
    }

    private static double BarycentricTolerance(Triangle triangle, double lengthTolerance)
    {
        double maxEdgeLength = MaxXyEdgeLength(triangle);
        if (!(maxEdgeLength > 0.0) || !double.IsFinite(maxEdgeLength))
            return 1e-12;

        double doubleArea = Math.Abs(
            ((triangle.Y1 - triangle.Y2) * (triangle.X0 - triangle.X2)) +
            ((triangle.X2 - triangle.X1) * (triangle.Y0 - triangle.Y2)));
        double minimumAltitude = doubleArea / maxEdgeLength;
        double altitudeFloor = Engine.ScaleAwareTolerance.LengthFloor(maxEdgeLength);
        return Math.Clamp(lengthTolerance / Math.Max(minimumAltitude, altitudeFloor), 1e-12, 1e-2);
    }

    private static double MaxXyEdgeLength(Triangle triangle)
    {
        double edge01 = Math.Sqrt(
            ((triangle.X1 - triangle.X0) * (triangle.X1 - triangle.X0)) +
            ((triangle.Y1 - triangle.Y0) * (triangle.Y1 - triangle.Y0)));
        double edge12 = Math.Sqrt(
            ((triangle.X2 - triangle.X1) * (triangle.X2 - triangle.X1)) +
            ((triangle.Y2 - triangle.Y1) * (triangle.Y2 - triangle.Y1)));
        double edge20 = Math.Sqrt(
            ((triangle.X0 - triangle.X2) * (triangle.X0 - triangle.X2)) +
            ((triangle.Y0 - triangle.Y2) * (triangle.Y0 - triangle.Y2)));
        return Math.Max(edge01, Math.Max(edge12, edge20));
    }

    private static bool IsPointNearFaceFootprint(double x, double y, Triangle triangle, double tolerance)
    {
        double minX = Math.Min(triangle.X0, Math.Min(triangle.X1, triangle.X2)) - tolerance;
        double maxX = Math.Max(triangle.X0, Math.Max(triangle.X1, triangle.X2)) + tolerance;
        double minY = Math.Min(triangle.Y0, Math.Min(triangle.Y1, triangle.Y2)) - tolerance;
        double maxY = Math.Max(triangle.Y0, Math.Max(triangle.Y1, triangle.Y2)) + tolerance;
        if (x < minX || x > maxX || y < minY || y > maxY)
            return false;

        return DistancePointSegmentSquared(x, y, triangle.X0, triangle.Y0, triangle.X1, triangle.Y1) <= tolerance * tolerance ||
            DistancePointSegmentSquared(x, y, triangle.X1, triangle.Y1, triangle.X2, triangle.Y2) <= tolerance * tolerance ||
            DistancePointSegmentSquared(x, y, triangle.X2, triangle.Y2, triangle.X0, triangle.Y0) <= tolerance * tolerance;
    }

    private static double DistancePointSegmentSquared(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax;
        double dy = by - ay;
        double lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared <= 1e-24)
        {
            double pointDx = px - ax;
            double pointDy = py - ay;
            return (pointDx * pointDx) + (pointDy * pointDy);
        }

        double t = (((px - ax) * dx) + ((py - ay) * dy)) / lengthSquared;
        t = Math.Clamp(t, 0.0, 1.0);
        double cx = ax + (t * dx);
        double cy = ay + (t * dy);
        double cxDx = px - cx;
        double cyDy = py - cy;
        return (cxDx * cxDx) + (cyDy * cyDy);
    }

    private static long HashCell(long x, long y) => (x * HashPrimeX) ^ (y * HashPrimeY);

    private readonly record struct Triangle(
        double X0,
        double Y0,
        double Z0,
        double X1,
        double Y1,
        double Z1,
        double X2,
        double Y2,
        double Z2);
}
