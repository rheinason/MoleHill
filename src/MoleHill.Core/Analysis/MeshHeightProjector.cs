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
    private readonly Dictionary<long, List<int>> _grid;
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
        _grid = new Dictionary<long, List<int>>(Math.Max(faceCount, 0));

        if (vertexCount <= 0 || faceCount <= 0)
        {
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
        double cellSize = cellSizeHint > 0.0
            ? Math.Max(cellSizeHint, 1e-6)
            : Math.Max(span / gridResolution, 1e-6);
        _invCell = 1.0 / cellSize;

        for (int face = 0; face < faceCount; face++)
            AddFaceToGrid(face);
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

        double insideTolerance = Math.Max(tolerance, 1e-8);
        double distinctZTolerance = Math.Max(tolerance * 4.0, 1e-8);
        bool found = false;
        bool requiresFallback = false;
        long cellX = (long)Math.Floor(x * _invCell);
        long cellY = (long)Math.Floor(y * _invCell);

        // Faces are registered in every grid cell touched by their XY bounding box, so a triangle
        // containing (x,y) must already be in the owning cell. The former 3x3 scan retested the same
        // faces from neighbouring buckets up to nine times on regular meshes.
        long key = HashCell(cellX, cellY);
        if (_grid.TryGetValue(key, out var faceIndices))
        {
            foreach (int face in faceIndices)
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

                if (w0 < -insideTolerance || w1 < -insideTolerance || w2 < -insideTolerance)
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

    private void AddFaceToGrid(int face)
    {
        if (!TryReadFace(face, out var triangle))
            return;

        long minCellX = (long)Math.Floor(Math.Min(triangle.X0, Math.Min(triangle.X1, triangle.X2)) * _invCell);
        long maxCellX = (long)Math.Floor(Math.Max(triangle.X0, Math.Max(triangle.X1, triangle.X2)) * _invCell);
        long minCellY = (long)Math.Floor(Math.Min(triangle.Y0, Math.Min(triangle.Y1, triangle.Y2)) * _invCell);
        long maxCellY = (long)Math.Floor(Math.Max(triangle.Y0, Math.Max(triangle.Y1, triangle.Y2)) * _invCell);

        for (long cellY = minCellY; cellY <= maxCellY; cellY++)
        {
            for (long cellX = minCellX; cellX <= maxCellX; cellX++)
            {
                long key = HashCell(cellX, cellY);
                if (!_grid.TryGetValue(key, out var list))
                {
                    list = new List<int>();
                    _grid[key] = list;
                }

                list.Add(face);
            }
        }
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
        if (Math.Abs(denom) < 1e-16)
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
        if (normalLength <= 1e-16)
            return true;

        return Math.Abs(nz) / normalLength < NearVerticalNormalZRatio;
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
