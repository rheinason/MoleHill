namespace MoleHill.Core.Grading;

internal class TerrainFaceGrid
{
    private readonly double[] _verts;
    private readonly int[] _faces;
    private readonly Dictionary<long, List<int>> _grid;
    private readonly double _invCell;

    public double BoundsDiagonal { get; }

    public TerrainFaceGrid(double[] vertices, int vertexCount, int[] faces, int faceCount, double cellSizeHint = 0.0)
    {
        _verts = vertices;
        _faces = faces;

        double minX = double.MaxValue;
        double maxX = double.MinValue;
        double minY = double.MaxValue;
        double maxY = double.MinValue;
        for (int i = 0; i < vertexCount; i++)
        {
            double x = vertices[i * 3];
            double y = vertices[i * 3 + 1];
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        double span = Math.Max(maxX - minX, maxY - minY);
        BoundsDiagonal = Math.Sqrt(((maxX - minX) * (maxX - minX)) + ((maxY - minY) * (maxY - minY)));
        // The default cell (span-average by face count) degrades badly on strongly non-uniform meshes
        // (huge outer faces + tiny graded-corridor faces put hundreds of faces per corridor cell).
        // Callers doing many point queries at a known working scale pass that scale as the hint.
        int gridRes = Math.Max(1, (int)Math.Sqrt(faceCount / 4.0));
        double cellSize = cellSizeHint > 0 ? Math.Max(cellSizeHint, 1e-6) : Math.Max(span / gridRes, 1e-6);
        _invCell = 1.0 / cellSize;

        _grid = new Dictionary<long, List<int>>(faceCount);
        for (int f = 0; f < faceCount; f++)
        {
            int i0 = faces[f * 3];
            int i1 = faces[f * 3 + 1];
            int i2 = faces[f * 3 + 2];
            double x0 = vertices[i0 * 3];
            double y0 = vertices[i0 * 3 + 1];
            double x1 = vertices[i1 * 3];
            double y1 = vertices[i1 * 3 + 1];
            double x2 = vertices[i2 * 3];
            double y2 = vertices[i2 * 3 + 1];

            long cMinX = (long)Math.Floor(Math.Min(x0, Math.Min(x1, x2)) * _invCell);
            long cMaxX = (long)Math.Floor(Math.Max(x0, Math.Max(x1, x2)) * _invCell);
            long cMinY = (long)Math.Floor(Math.Min(y0, Math.Min(y1, y2)) * _invCell);
            long cMaxY = (long)Math.Floor(Math.Max(y0, Math.Max(y1, y2)) * _invCell);

            for (long cy = cMinY; cy <= cMaxY; cy++)
            {
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
    }

    public bool TryFindRayDaylightReach(
        double edgeX,
        double edgeY,
        double edgeZ,
        double dirX,
        double dirY,
        double slopeRatio,
        double branchSign,
        double maxReach,
        out double daylightReach,
        out double bestApproachReach)
    {
        daylightReach = 0.0;
        bestApproachReach = 0.0;
        if (maxReach <= 1e-9)
            return false;

        const double insideTolerance = 1e-8;
        const double diffTolerance = 1e-5;
        double bestAbsDiff = double.MaxValue;
        double bestReach = 0.0;
        int faceCount = _faces.Length / 3;

        for (int f = 0; f < faceCount; f++)
        {
            int i0 = _faces[f * 3];
            int i1 = _faces[f * 3 + 1];
            int i2 = _faces[f * 3 + 2];
            double x0 = _verts[i0 * 3];
            double y0 = _verts[i0 * 3 + 1];
            double z0 = _verts[i0 * 3 + 2];
            double x1 = _verts[i1 * 3];
            double y1 = _verts[i1 * 3 + 1];
            double z1 = _verts[i1 * 3 + 2];
            double x2 = _verts[i2 * 3];
            double y2 = _verts[i2 * 3 + 1];
            double z2 = _verts[i2 * 3 + 2];

            double denom = ((y1 - y2) * (x0 - x2)) + ((x2 - x1) * (y0 - y2));
            if (Math.Abs(denom) <= 1e-16)
                continue;

            ComputeBarycentric(edgeX, edgeY, x0, y0, x1, y1, x2, y2, denom, out double a0, out double b0, out double c0);
            ComputeBarycentric(edgeX + (dirX * maxReach), edgeY + (dirY * maxReach), x0, y0, x1, y1, x2, y2, denom, out double a1, out double b1, out double c1);

            double low = 0.0;
            double high = maxReach;
            if (!ClipRayInterval(a0, (a1 - a0) / maxReach, -insideTolerance, ref low, ref high) ||
                !ClipRayInterval(b0, (b1 - b0) / maxReach, -insideTolerance, ref low, ref high) ||
                !ClipRayInterval(c0, (c1 - c0) / maxReach, -insideTolerance, ref low, ref high))
            {
                continue;
            }

            if (high < 1e-8)
                continue;

            low = Math.Max(low, 1e-8);
            double lowDiff = RayTerrainGradeDifference(edgeX, edgeY, edgeZ, dirX, dirY, slopeRatio, branchSign, low, x0, y0, z0, x1, y1, z1, x2, y2, z2, denom);
            double highDiff = RayTerrainGradeDifference(edgeX, edgeY, edgeZ, dirX, dirY, slopeRatio, branchSign, high, x0, y0, z0, x1, y1, z1, x2, y2, z2, denom);

            double lowAbsDiff = Math.Abs(lowDiff);
            if (lowAbsDiff < bestAbsDiff)
            {
                bestAbsDiff = lowAbsDiff;
                bestReach = low;
            }

            double highAbsDiff = Math.Abs(highDiff);
            if (highAbsDiff < bestAbsDiff)
            {
                bestAbsDiff = highAbsDiff;
                bestReach = high;
            }

            if (Math.Abs(lowDiff) <= diffTolerance)
            {
                daylightReach = low;
                return true;
            }

            if (Math.Abs(highDiff) <= diffTolerance)
            {
                daylightReach = high;
                return true;
            }

            if (lowDiff > 0.0 && highDiff < 0.0 || lowDiff < 0.0 && highDiff > 0.0)
            {
                double t = low + ((0.0 - lowDiff) / (highDiff - lowDiff) * (high - low));
                if (t >= 0.0 && t <= maxReach)
                {
                    daylightReach = t;
                    return true;
                }
            }
        }

        bestApproachReach = bestReach;
        return false;
    }

    public double InterpolateZ(double px, double py)
    {
        return TryInterpolateZ(px, py, out double z) ? z : NearestVertexZ(px, py);
    }

    /// <summary>
    /// Containing face and barycentric weights at (px, py); false when the point lies outside the
    /// mesh. Same search as <see cref="TryInterpolateZ"/> — used to interpolate per-vertex attributes
    /// other than Z (e.g. a direction field).
    /// </summary>
    public bool TryFindFace(double px, double py, out int face, out double w0, out double w1, out double w2)
    {
        const double tol = 1e-4;
        face = -1;
        w0 = w1 = w2 = 0.0;
        long cx = (long)Math.Floor(px * _invCell);
        long cy = (long)Math.Floor(py * _invCell);

        for (long dx = -1; dx <= 1; dx++)
        {
            for (long dy = -1; dy <= 1; dy++)
            {
                long key = ((cx + dx) * 0x100000001L) ^ ((cy + dy) * 0x27d4eb2dL);
                if (!_grid.TryGetValue(key, out var faceIndices))
                    continue;

                foreach (int f in faceIndices)
                {
                    int i0 = _faces[f * 3];
                    int i1 = _faces[f * 3 + 1];
                    int i2 = _faces[f * 3 + 2];
                    double x0 = _verts[i0 * 3], y0 = _verts[i0 * 3 + 1];
                    double x1 = _verts[i1 * 3], y1 = _verts[i1 * 3 + 1];
                    double x2 = _verts[i2 * 3], y2 = _verts[i2 * 3 + 1];

                    double denom = (y1 - y2) * (x0 - x2) + (x2 - x1) * (y0 - y2);
                    if (Math.Abs(denom) < 1e-12)
                        continue;

                    double b0 = ((y1 - y2) * (px - x2) + (x2 - x1) * (py - y2)) / denom;
                    double b1 = ((y2 - y0) * (px - x2) + (x0 - x2) * (py - y2)) / denom;
                    double b2 = 1.0 - b0 - b1;
                    if (b0 >= -tol && b1 >= -tol && b2 >= -tol)
                    {
                        face = f;
                        w0 = b0;
                        w1 = b1;
                        w2 = b2;
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>Barycentric Z at (px, py) when a containing face exists; false when the point lies
    /// outside the mesh (no nearest-vertex fallback — callers that must not extrapolate use this).</summary>
    public bool TryInterpolateZ(double px, double py, out double z)
    {
        const double tol = 1e-4;
        long cx = (long)Math.Floor(px * _invCell);
        long cy = (long)Math.Floor(py * _invCell);

        for (long dx = -1; dx <= 1; dx++)
        {
            for (long dy = -1; dy <= 1; dy++)
            {
                long key = ((cx + dx) * 0x100000001L) ^ ((cy + dy) * 0x27d4eb2dL);
                if (!_grid.TryGetValue(key, out var faceIndices))
                    continue;

                foreach (int f in faceIndices)
                {
                    int i0 = _faces[f * 3];
                    int i1 = _faces[f * 3 + 1];
                    int i2 = _faces[f * 3 + 2];
                    double x0 = _verts[i0 * 3];
                    double y0 = _verts[i0 * 3 + 1];
                    double z0 = _verts[i0 * 3 + 2];
                    double x1 = _verts[i1 * 3];
                    double y1 = _verts[i1 * 3 + 1];
                    double z1 = _verts[i1 * 3 + 2];
                    double x2 = _verts[i2 * 3];
                    double y2 = _verts[i2 * 3 + 1];
                    double z2 = _verts[i2 * 3 + 2];

                    double denom = (y1 - y2) * (x0 - x2) + (x2 - x1) * (y0 - y2);
                    if (Math.Abs(denom) < 1e-12)
                        continue;

                    double w0 = ((y1 - y2) * (px - x2) + (x2 - x1) * (py - y2)) / denom;
                    double w1 = ((y2 - y0) * (px - x2) + (x0 - x2) * (py - y2)) / denom;
                    double w2 = 1.0 - w0 - w1;

                    if (w0 >= -tol && w1 >= -tol && w2 >= -tol)
                    {
                        z = w0 * z0 + w1 * z1 + w2 * z2;
                        return true;
                    }
                }
            }
        }

        z = 0.0;
        return false;
    }

    private static void ComputeBarycentric(
        double px,
        double py,
        double x0,
        double y0,
        double x1,
        double y1,
        double x2,
        double y2,
        double denom,
        out double w0,
        out double w1,
        out double w2)
    {
        w0 = (((y1 - y2) * (px - x2)) + ((x2 - x1) * (py - y2))) / denom;
        w1 = (((y2 - y0) * (px - x2)) + ((x0 - x2) * (py - y2))) / denom;
        w2 = 1.0 - w0 - w1;
    }

    private static bool ClipRayInterval(double valueAtZero, double slope, double minimumValue, ref double low, ref double high)
    {
        if (Math.Abs(slope) <= 1e-16)
            return valueAtZero >= minimumValue;

        double crossing = (minimumValue - valueAtZero) / slope;
        if (slope > 0.0)
            low = Math.Max(low, crossing);
        else
            high = Math.Min(high, crossing);

        return low <= high;
    }

    private static double RayTerrainGradeDifference(
        double edgeX,
        double edgeY,
        double edgeZ,
        double dirX,
        double dirY,
        double slopeRatio,
        double branchSign,
        double reach,
        double x0,
        double y0,
        double z0,
        double x1,
        double y1,
        double z1,
        double x2,
        double y2,
        double z2,
        double denom)
    {
        double px = edgeX + (dirX * reach);
        double py = edgeY + (dirY * reach);
        ComputeBarycentric(px, py, x0, y0, x1, y1, x2, y2, denom, out double w0, out double w1, out double w2);
        double terrainZ = (w0 * z0) + (w1 * z1) + (w2 * z2);
        double gradeZ = edgeZ + (branchSign * slopeRatio * reach);
        return terrainZ - gradeZ;
    }

    private double NearestVertexZ(double px, double py)
    {
        double nearestZ = 0;
        double nearestDistSq = double.MaxValue;
        int vCount = _verts.Length / 3;
        for (int i = 0; i < vCount; i++)
        {
            double dx = _verts[i * 3] - px;
            double dy = _verts[i * 3 + 1] - py;
            double distSq = dx * dx + dy * dy;
            if (distSq < nearestDistSq)
            {
                nearestDistSq = distSq;
                nearestZ = _verts[i * 3 + 2];
            }
        }

        return nearestZ;
    }
}
