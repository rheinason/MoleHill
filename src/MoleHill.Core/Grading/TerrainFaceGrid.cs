using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

// Uniform-grid terrain face lookup for point interpolation and finite daylight-ray traversal.
internal class TerrainFaceGrid
{
    /// <summary>
    /// Reusable candidate buffer for one ray traversal.
    /// </summary>
    /// <remarks>
    /// This used to carry a face-sized stamp array to reject a face already seen in an earlier cell.
    /// The buffer is thread-static, so on a multi-million-face terrain every thread-pool worker that ever
    /// ran a ray query held tens of megabytes for the life of the process — long after the build. The
    /// candidate list is sorted before use anyway, so duplicates are removed by a unique pass over the
    /// sorted list instead, and the buffer now retains only as much as the widest ray corridor.
    /// </remarks>
    private sealed class RayQueryScratch
    {
        public readonly List<int> Candidates = new();

        public void Begin() => Candidates.Clear();

        public void Add(int face) => Candidates.Add(face);

        /// <summary>Sorts the candidates into source face order and drops repeats in place.</summary>
        public void SortAndDeduplicate()
        {
            Candidates.Sort();

            int write = 0;
            for (int read = 0; read < Candidates.Count; read++)
            {
                if (write > 0 && Candidates[read] == Candidates[write - 1])
                    continue;

                Candidates[write++] = Candidates[read];
            }

            Candidates.RemoveRange(write, Candidates.Count - write);
        }
    }

    [ThreadStatic]
    private static RayQueryScratch? _rayQueryScratch;

    private readonly double[] _verts;
    private readonly int[] _faces;
    // Faces by covered cell, ascending within each cell, in one of two layouts. When every face's cells lie
    // within the vertex bounds (the normal case) they are a dense row-major CSR table over that range:
    // counting and filling plain arrays, where the hashed index took ~40M dictionary operations to build
    // over the 1 m park (3.9 s of a 6.9 s incremental Remesh), and every query probe is an array read. Otherwise
    // the hashed index. Both hold the same runs, so every query sees the same faces in the same order.
    private readonly CellMembershipIndex? _cells;
    private readonly int[]? _denseStart;
    private readonly int[]? _denseItems;
    private readonly long _denseMinX;
    private readonly long _denseMinY;
    private readonly long _denseWidth;
    private readonly long _denseHeight;
    private readonly double _invCell;
    private readonly int _faceCount;

    public double BoundsDiagonal { get; }

    public TerrainFaceGrid(double[] vertices, int vertexCount, int[] faces, int faceCount, double cellSizeHint = 0.0)
    {
        _verts = vertices;
        _faces = faces;
        _faceCount = faceCount;

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
        double requestedCellSize = cellSizeHint > 0 ? cellSizeHint : span / gridRes;
        double cellSize = ScaleAwareTolerance.ResolveLength(requestedCellSize, span);
        _invCell = 1.0 / cellSize;

        // Pass 1: count every membership. Pass 2: fill. Faces are visited in source order, so each cell's
        // run stays ascending - the order point location's first-match rule depends on.
        if (vertexCount > 0 && TryBuildDense(vertices, faces, faceCount, minX, minY, maxX, maxY,
                out _denseStart, out _denseItems, out _denseMinX, out _denseMinY, out _denseWidth, out _denseHeight))
        {
            return;
        }

        var cells = new CellMembershipIndex.Builder(faceCount / 2);
        for (int f = 0; f < faceCount; f++)
        {
            GetFaceCellRange(vertices, faces, f, out long cMinX, out long cMaxX, out long cMinY, out long cMaxY);
            for (long cy = cMinY; cy <= cMaxY; cy++)
            {
                for (long cx = cMinX; cx <= cMaxX; cx++)
                    cells.Count(CellKey(cx, cy));
            }
        }

        cells.BeginFill();
        for (int f = 0; f < faceCount; f++)
        {
            GetFaceCellRange(vertices, faces, f, out long cMinX, out long cMaxX, out long cMinY, out long cMaxY);
            for (long cy = cMinY; cy <= cMaxY; cy++)
            {
                for (long cx = cMinX; cx <= cMaxX; cx++)
                    cells.Add(CellKey(cx, cy), f);
            }
        }

        _cells = cells.Build();
    }

    /// <summary>
    /// The dense layout, when the cell range is compact (a few table entries per face) and every face's cells
    /// lie inside it; false otherwise (a non-finite coordinate, a face on an uncounted vertex).
    /// </summary>
    private bool TryBuildDense(
        double[] vertices,
        int[] faces,
        int faceCount,
        double minX,
        double minY,
        double maxX,
        double maxY,
        out int[]? start,
        out int[]? items,
        out long minCellX,
        out long minCellY,
        out long width,
        out long height)
    {
        start = null;
        items = null;
        minCellX = (long)Math.Floor(minX * _invCell);
        minCellY = (long)Math.Floor(minY * _invCell);
        width = (long)Math.Floor(maxX * _invCell) - minCellX + 1;
        height = (long)Math.Floor(maxY * _invCell) - minCellY + 1;
        if (width <= 0 || height <= 0 || width * height > Math.Max(1024L, 4L * faceCount) || width * height >= int.MaxValue)
            return false;

        var counts = new int[(width * height) + 1];
        for (int f = 0; f < faceCount; f++)
        {
            GetFaceCellRange(vertices, faces, f, out long cMinX, out long cMaxX, out long cMinY, out long cMaxY);
            if (cMinX < minCellX || cMinY < minCellY || cMaxX - minCellX >= width || cMaxY - minCellY >= height)
                return false;
            for (long cy = cMinY; cy <= cMaxY; cy++)
            {
                long row = (cy - minCellY) * width;
                for (long cx = cMinX; cx <= cMaxX; cx++)
                    counts[row + (cx - minCellX) + 1]++;
            }
        }

        for (long i = 1; i < counts.LongLength; i++)
            counts[i] += counts[i - 1];

        var cursor = (int[])counts.Clone();
        var filled = new int[counts[^1]];
        for (int f = 0; f < faceCount; f++)
        {
            GetFaceCellRange(vertices, faces, f, out long cMinX, out long cMaxX, out long cMinY, out long cMaxY);
            for (long cy = cMinY; cy <= cMaxY; cy++)
            {
                long row = (cy - minCellY) * width;
                for (long cx = cMinX; cx <= cMaxX; cx++)
                    filled[cursor[row + (cx - minCellX)]++] = f;
            }
        }

        start = counts;
        items = filled;
        return true;
    }

    private void GetFaceCellRange(
        double[] vertices,
        int[] faces,
        int face,
        out long cMinX,
        out long cMaxX,
        out long cMinY,
        out long cMaxY)
    {
        int i0 = faces[face * 3];
        int i1 = faces[face * 3 + 1];
        int i2 = faces[face * 3 + 2];
        double x0 = vertices[i0 * 3];
        double y0 = vertices[i0 * 3 + 1];
        double x1 = vertices[i1 * 3];
        double y1 = vertices[i1 * 3 + 1];
        double x2 = vertices[i2 * 3];
        double y2 = vertices[i2 * 3 + 1];

        cMinX = (long)Math.Floor(Math.Min(x0, Math.Min(x1, x2)) * _invCell);
        cMaxX = (long)Math.Floor(Math.Max(x0, Math.Max(x1, x2)) * _invCell);
        cMinY = (long)Math.Floor(Math.Min(y0, Math.Min(y1, y2)) * _invCell);
        cMaxY = (long)Math.Floor(Math.Max(y0, Math.Max(y1, y2)) * _invCell);
    }

    private static long CellKey(long cellX, long cellY) => (cellX * 0x100000001L) ^ (cellY * 0x27d4eb2dL);

    /// <summary>Faces registered in the cell containing (cellX, cellY), ascending. Empty when unoccupied.</summary>
    private ReadOnlySpan<int> CellFaces(long cellX, long cellY)
    {
        if (_denseStart == null)
            return _cells!.Items(CellKey(cellX, cellY));

        long x = cellX - _denseMinX;
        long y = cellY - _denseMinY;
        if ((ulong)x >= (ulong)_denseWidth || (ulong)y >= (ulong)_denseHeight)
            return ReadOnlySpan<int>.Empty;

        long cell = (y * _denseWidth) + x;
        int from = _denseStart[cell];
        return _denseItems.AsSpan(from, _denseStart[cell + 1] - from);
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
        if (!TryGatherRayCandidates(edgeX, edgeY, dirX, dirY, maxReach, out List<int> candidates))
        {
            return TryFindRayDaylightReachCore(
                edgeX,
                edgeY,
                edgeZ,
                dirX,
                dirY,
                slopeRatio,
                branchSign,
                maxReach,
                candidates: null,
                out daylightReach,
                out bestApproachReach);
        }

        return TryFindRayDaylightReachCore(
            edgeX,
            edgeY,
            edgeZ,
            dirX,
            dirY,
            slopeRatio,
            branchSign,
            maxReach,
            candidates,
            out daylightReach,
            out bestApproachReach);
    }

    internal bool TryFindRayDaylightReachLinearForDiagnostics(
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
        return TryFindRayDaylightReachCore(
            edgeX,
            edgeY,
            edgeZ,
            dirX,
            dirY,
            slopeRatio,
            branchSign,
            maxReach,
            candidates: null,
            out daylightReach,
            out bestApproachReach);
    }

    private bool TryFindRayDaylightReachCore(
        double edgeX,
        double edgeY,
        double edgeZ,
        double dirX,
        double dirY,
        double slopeRatio,
        double branchSign,
        double maxReach,
        List<int>? candidates,
        out double daylightReach,
        out double bestApproachReach)
    {
        daylightReach = 0.0;
        bestApproachReach = 0.0;
        if (maxReach <= 1e-9)
            return false;

        const double diffTolerance = 1e-5;
        double bestAbsDiff = double.MaxValue;
        double bestReach = 0.0;
        int candidateCount = candidates?.Count ?? _faceCount;

        for (int candidateIndex = 0; candidateIndex < candidateCount; candidateIndex++)
        {
            int f = candidates is null ? candidateIndex : candidates[candidateIndex];
            if (!TryClipRayToFace(f, edgeX, edgeY, dirX, dirY, maxReach, out double low, out double high, out double denom))
                continue;

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

    /// <summary>
    /// The part of the ray [0, <paramref name="maxReach"/>] inside face <paramref name="f"/>, within the
    /// barycentric inside-tolerance; false when the ray misses it. The one definition of "the ray crosses
    /// this face": the daylight search skips every face this rejects, and the candidate gather applies
    /// the same test so that only crossed faces are kept and sorted.
    /// </summary>
    private bool TryClipRayToFace(
        int f,
        double edgeX,
        double edgeY,
        double dirX,
        double dirY,
        double maxReach,
        out double low,
        out double high,
        out double denom)
    {
        const double insideTolerance = 1e-8;
        low = 0.0;
        high = maxReach;
        int i0 = _faces[f * 3];
        int i1 = _faces[f * 3 + 1];
        int i2 = _faces[f * 3 + 2];
        double x0 = _verts[i0 * 3];
        double y0 = _verts[i0 * 3 + 1];
        double x1 = _verts[i1 * 3];
        double y1 = _verts[i1 * 3 + 1];
        double x2 = _verts[i2 * 3];
        double y2 = _verts[i2 * 3 + 1];

        denom = ((y1 - y2) * (x0 - x2)) + ((x2 - x1) * (y0 - y2));
        if (Math.Abs(denom) <= 1e-16)
            return false;

        ComputeBarycentric(edgeX, edgeY, x0, y0, x1, y1, x2, y2, denom, out double a0, out double b0, out double c0);
        ComputeBarycentric(edgeX + (dirX * maxReach), edgeY + (dirY * maxReach), x0, y0, x1, y1, x2, y2, denom, out double a1, out double b1, out double c1);

        if (!ClipRayInterval(a0, (a1 - a0) / maxReach, -insideTolerance, ref low, ref high) ||
            !ClipRayInterval(b0, (b1 - b0) / maxReach, -insideTolerance, ref low, ref high) ||
            !ClipRayInterval(c0, (c1 - c0) / maxReach, -insideTolerance, ref low, ref high))
        {
            return false;
        }

        return high >= 1e-8;
    }

    private bool TryGatherRayCandidates(
        double edgeX,
        double edgeY,
        double dirX,
        double dirY,
        double maxReach,
        out List<int> candidates)
    {
        RayQueryScratch scratch = _rayQueryScratch ??= new RayQueryScratch();
        scratch.Begin();
        candidates = scratch.Candidates;

        double endX = edgeX + (dirX * maxReach);
        double endY = edgeY + (dirY * maxReach);
        if (!double.IsFinite(edgeX) ||
            !double.IsFinite(edgeY) ||
            !double.IsFinite(endX) ||
            !double.IsFinite(endY))
        {
            return false;
        }

        double scaledMinX = Math.Floor(Math.Min(edgeX, endX) * _invCell);
        double scaledMaxX = Math.Floor(Math.Max(edgeX, endX) * _invCell);
        double scaledMinY = Math.Floor(Math.Min(edgeY, endY) * _invCell);
        double scaledMaxY = Math.Floor(Math.Max(edgeY, endY) * _invCell);
        if (!double.IsFinite(scaledMinX) ||
            !double.IsFinite(scaledMaxX) ||
            !double.IsFinite(scaledMinY) ||
            !double.IsFinite(scaledMaxY) ||
            scaledMinX <= long.MinValue + 1.0 ||
            scaledMaxX >= long.MaxValue - 1.0 ||
            scaledMinY <= long.MinValue + 1.0 ||
            scaledMaxY >= long.MaxValue - 1.0)
        {
            return false;
        }

        // Include one neighbouring cell around the ray AABB. This retains the barycentric
        // inside-tolerance behaviour for rays that run exactly on, or just outside, a face edge.
        long minCellX = (long)scaledMinX - 1;
        long maxCellX = (long)scaledMaxX + 1;
        long minCellY = (long)scaledMinY - 1;
        long maxCellY = (long)scaledMaxY + 1;

        double columnCount = (double)maxCellX - minCellX + 1.0;
        double rowCount = (double)maxCellY - minCellY + 1.0;
        double cellVisitLimit = Math.Max(4_096.0, _faceCount * 4.0);
        if (columnCount <= 0.0 ||
            rowCount <= 0.0 ||
            columnCount > cellVisitLimit ||
            rowCount > cellVisitLimit ||
            columnCount * rowCount > cellVisitLimit)
        {
            return false;
        }

        // Only faces the ray actually crosses are kept. The daylight search skips every other face without
        // touching its state, so dropping them here changes nothing, and it matters: the cell box of a
        // diagonal ray covers thousands of faces the ray never meets, and sorting all of them was a third
        // of PadGrader.Grade on the geometry-heavy fixture. A face in several traversed cells is tested
        // once per cell, which is cheaper than the face-sized stamp array this class deliberately gave up.
        //
        // Cells are walked as a corridor along the ray, not the ray's whole bounding box: per row, only
        // the columns the ray spans across that row and the rows either side of it, plus one column each
        // way. A face the ray crosses holds a ray point p in its bounds (to within the barycentric
        // tolerance), so it is registered in a cell at most one row and column from p's, and that cell's
        // row sees p's x in its span - the same one-cell margin the box padding provided. On a diagonal
        // ray the box is mostly cells the ray never comes near.
        double cellSize = 1.0 / _invCell;
        double rayDx = endX - edgeX;
        double rayDy = endY - edgeY;
        for (long cy = minCellY; cy <= maxCellY; cy++)
        {
            double spanMinX;
            double spanMaxX;
            if (Math.Abs(rayDy) <= 1e-12 * Math.Max(1.0, Math.Abs(rayDx)))
            {
                spanMinX = Math.Min(edgeX, endX);
                spanMaxX = Math.Max(edgeX, endX);
            }
            else
            {
                double t0 = (((cy - 1) * cellSize) - edgeY) / rayDy;
                double t1 = (((cy + 2) * cellSize) - edgeY) / rayDy;
                double tLow = Math.Clamp(Math.Min(t0, t1), 0.0, 1.0);
                double tHigh = Math.Clamp(Math.Max(t0, t1), 0.0, 1.0);
                double xa = edgeX + (rayDx * tLow);
                double xb = edgeX + (rayDx * tHigh);
                spanMinX = Math.Min(xa, xb);
                spanMaxX = Math.Max(xa, xb);
            }

            long rowMinX = Math.Max(minCellX, (long)Math.Floor(spanMinX * _invCell) - 1);
            long rowMaxX = Math.Min(maxCellX, (long)Math.Floor(spanMaxX * _invCell) + 1);
            for (long cx = rowMinX; cx <= rowMaxX; cx++)
            {
                foreach (int face in CellFaces(cx, cy))
                {
                    if (TryClipRayToFace(face, edgeX, edgeY, dirX, dirY, maxReach, out _, out _, out _))
                        scratch.Add(face);
                }
            }
        }

        // The previous linear scan visited faces in source order and returned the first hit.
        // Grid bucket order is spatial, so restore source order before evaluating candidates. The same
        // sort removes the repeats a face spanning several traversed cells contributes.
        scratch.SortAndDeduplicate();
        return true;
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
                foreach (int f in CellFaces(cx + dx, cy + dy))
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

    /// <summary>
    /// The longest plan edge of the face under (px, py), or 0 when the point lies outside the mesh: the
    /// terrain's own resolution there, which is as finely as anything sampled against it can be resolved.
    /// </summary>
    internal double FaceEdgeLengthAt(double px, double py)
    {
        if (!TryFindFace(px, py, out int face, out _, out _, out _))
            return 0.0;

        double longest = 0.0;
        for (int k = 0; k < 3; k++)
        {
            int a = _faces[face * 3 + k];
            int b = _faces[face * 3 + ((k + 1) % 3)];
            double dx = _verts[b * 3] - _verts[a * 3];
            double dy = _verts[b * 3 + 1] - _verts[a * 3 + 1];
            longest = Math.Max(longest, Math.Sqrt((dx * dx) + (dy * dy)));
        }

        return longest;
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
                foreach (int f in CellFaces(cx + dx, cy + dy))
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
