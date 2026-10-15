using MoleHill.Core.Engine;

namespace MoleHill.Core.Analysis;

/// <summary>
/// Measures the terrain's curvature at a stated landform size: the derivative field every curvature
/// measure and landform class is read from (<see cref="CurvatureField"/>).
/// </summary>
/// <remarks>
/// <para><b>Curvature is measured at a scale, never per triangle.</b> A triangle is flat and the bend
/// between two of them is a fact about the triangulation, so per-face curvature would map the mesh, not
/// the ground. Instead each vertex gets a weighted least-squares quadric
/// <c>z = a x2 + b y2 + c xy + d x + e y + f</c> fitted over every sample within the landform size of it,
/// and its derivatives are read off at the vertex. Detail much smaller than that radius averages out of the
/// fit; landforms about that size and larger are what it measures. That is the control: a 0.3 m hump on a
/// 40 m mound disappears at a 10 m landform size and dominates at 0.5 m.</para>
/// <para><b>The weights make it triangulation-independent.</b> Each sample is weighted by the plan area it
/// stands for (a third of each incident face's plan area), tapered smoothly to zero at the radius by
/// <c>(1 - (d/R)2)2</c>. A dense patch of survey therefore counts for the ground it covers, not for how many
/// points it holds, and the taper has no hard edge for a sample to flicker across.</para>
/// <para><b>Samples are binned, so the cost does not grow with the radius.</b> Vertices are aggregated into
/// plan cells a quarter of the radius wide (area-weighted centroid per cell), so a fit sees about fifty
/// samples whatever the radius, and a large landform size over a dense survey stays as quick as a small
/// one. Cells are never coarser than the radius needs, so a dense patch keeps its detail at a small size.
/// Cells are found through a sorted key array, not a hash, so the lookup is allocation-free and
/// deterministic.</para>
/// <para><b>Sparse ground widens the fit rather than failing.</b> Where fewer than
/// <see cref="MinimumSamples"/> samples fall inside the radius, or they do not determine a quadric
/// (collinear, as along a one-row strip), the radius grows by half again until they do. A landform size
/// smaller than the mesh spacing therefore measures at the mesh's own resolution. Ground that still
/// cannot be fitted is left unmeasured (NaN), never given a curvature of zero.</para>
/// <para>Per face, the three vertex fits are averaged. Vertices are fitted in parallel, each writing only
/// its own slot over samples visited in a fixed order, so the result is deterministic.</para>
/// </remarks>
public static class CurvatureAnalyzer
{
    /// <summary>Fewest samples a fit accepts before it widens. A quadric has six unknowns.</summary>
    private const int MinimumSamples = 10;

    /// <summary>How many times a fit may widen by half again: about 130x the requested radius.</summary>
    private const int MaxWidenings = 12;

    /// <summary>Cells per radius. Four gives roughly fifty samples in a full disc.</summary>
    private const double CellsPerRadius = 4.0;

    private const int ParallelThreshold = 2_000;

    /// <summary>
    /// Fits the surface at every vertex and averages the fits per face.
    /// </summary>
    /// <param name="mesh">The terrain. Faces with an out-of-range index are left unmeasured.</param>
    /// <param name="landformSize">Fit radius in model units. Zero or less fits each vertex over its nearest
    /// samples, which is the mesh's own resolution.</param>
    /// <param name="shouldCancel">Polled between blocks of vertices; throws
    /// <see cref="OperationCanceledException"/> when it returns true.</param>
    public static CurvatureField Measure(IndexedTriMesh mesh, double landformSize, Func<bool>? shouldCancel = null)
    {
        (double[] vertices, int vertexCount, int[] faces, int faceCount) = mesh;
        double radius = double.IsFinite(landformSize) && landformSize > 0.0 ? landformSize : 0.0;

        var faceDerivatives = new double[faceCount * 5];
        Array.Fill(faceDerivatives, double.NaN);
        var planAreas = new double[faceCount];
        var field = new CurvatureField(radius, faceDerivatives, planAreas, faceCount);
        if (faceCount == 0 || vertexCount == 0)
            return field;

        // Plan area per face, and the share of it each corner stands for.
        var weights = new double[vertexCount];
        var referenced = new bool[vertexCount];
        var validFace = new bool[faceCount];
        double totalArea = 0.0;
        int referencedCount = 0;
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        for (int f = 0; f < faceCount; f++)
        {
            int a = faces[f * 3];
            int b = faces[(f * 3) + 1];
            int c = faces[(f * 3) + 2];
            if ((uint)a >= (uint)vertexCount || (uint)b >= (uint)vertexCount || (uint)c >= (uint)vertexCount)
                continue;

            double area = Math.Abs(
                ((vertices[b * 3] - vertices[a * 3]) * (vertices[(c * 3) + 1] - vertices[(a * 3) + 1])) -
                ((vertices[(b * 3) + 1] - vertices[(a * 3) + 1]) * (vertices[c * 3] - vertices[a * 3]))) * 0.5;
            if (!double.IsFinite(area))
                continue;

            validFace[f] = true;
            planAreas[f] = area;
            totalArea += area;
            AddCorner(a, area);
            AddCorner(b, area);
            AddCorner(c, area);
        }

        void AddCorner(int v, double area)
        {
            weights[v] += area / 3.0;
            if (referenced[v])
                return;

            referenced[v] = true;
            referencedCount++;
            double x = vertices[v * 3];
            double y = vertices[(v * 3) + 1];
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        double extentX = maxX - minX;
        double extentY = maxY - minY;
        if (referencedCount == 0 || !(totalArea > 0.0) || !double.IsFinite(extentX) || !double.IsFinite(extentY))
            return field;

        // A vertex on vertical faces only (a wall's foot can be) stands for no plan area, but it is still
        // ground, so it keeps a token weight rather than vanishing from every fit.
        double minimumWeight = totalArea / referencedCount * 1e-6;

        // Cells a quarter of the radius wide, but never finer than an eighth of the average spacing: past
        // that, cells only hold one vertex each and the lookup pays for empty ones.
        double spacing = Math.Sqrt(Math.Max(extentX * extentY, totalArea) / referencedCount);
        double cell = Math.Max(radius > 0.0 ? radius / CellsPerRadius : spacing * 0.5, spacing / 8.0);
        if (!(cell > 0.0) || !double.IsFinite(cell))
            return field;

        SampleGrid grid = SampleGrid.Build(vertices, vertexCount, referenced, weights, minimumWeight, minX, minY, extentX, extentY, cell);

        // Nothing is measured beyond the whole terrain: a fit that wide would describe nothing local.
        double maxRadius = Math.Max(Math.Sqrt((extentX * extentX) + (extentY * extentY)), cell) * 2.0;
        double startRadius = Math.Max(radius, cell);

        var vertexDerivatives = new double[vertexCount * 5];
        void FitVertex(int v)
        {
            int offset = v * 5;
            if (!referenced[v])
            {
                vertexDerivatives[offset] = double.NaN;
                return;
            }

            double x0 = vertices[v * 3];
            double y0 = vertices[(v * 3) + 1];
            double z0 = vertices[(v * 3) + 2];
            double fitRadius = startRadius;
            for (int attempt = 0; attempt <= MaxWidenings; attempt++)
            {
                // The first fit always runs, however large the requested radius.
                if (attempt > 0 && fitRadius > maxRadius)
                    break;

                if (grid.TryFit(x0, y0, z0, fitRadius, vertexDerivatives.AsSpan(offset, 5)))
                    return;

                fitRadius *= 1.5;
            }

            vertexDerivatives[offset] = double.NaN;
        }

        if (vertexCount >= ParallelThreshold)
        {
            const int block = 1024;
            int blocks = (vertexCount + block - 1) / block;
            bool cancelled = false;
            Parallel.For(0, blocks, (index, state) =>
            {
                if (shouldCancel?.Invoke() == true)
                {
                    cancelled = true;
                    state.Stop();
                    return;
                }

                int end = Math.Min(vertexCount, (index + 1) * block);
                for (int v = index * block; v < end; v++)
                    FitVertex(v);
            });

            if (cancelled)
                throw new OperationCanceledException();
        }
        else
        {
            if (shouldCancel?.Invoke() == true)
                throw new OperationCanceledException();
            for (int v = 0; v < vertexCount; v++)
                FitVertex(v);
        }

        for (int f = 0; f < faceCount; f++)
        {
            if (!validFace[f])
                continue;

            int a = faces[f * 3] * 5;
            int b = faces[(f * 3) + 1] * 5;
            int c = faces[(f * 3) + 2] * 5;
            if (double.IsNaN(vertexDerivatives[a]) || double.IsNaN(vertexDerivatives[b]) || double.IsNaN(vertexDerivatives[c]))
                continue;

            for (int k = 0; k < 5; k++)
                faceDerivatives[(f * 5) + k] = (vertexDerivatives[a + k] + vertexDerivatives[b + k] + vertexDerivatives[c + k]) / 3.0;
        }

        return field;
    }

    /// <summary>
    /// The area-weighted samples in plan cells, sorted by cell key (row-major), with each cell's centroid.
    /// </summary>
    private sealed class SampleGrid
    {
        private readonly long[] _keys;
        private readonly double[] _x;
        private readonly double[] _y;
        private readonly double[] _z;
        private readonly double[] _w;
        private readonly double _minX;
        private readonly double _minY;
        private readonly double _cell;
        private readonly long _columns;
        private readonly long _rows;

        private SampleGrid(long[] keys, double[] x, double[] y, double[] z, double[] w, double minX, double minY, double cell, long columns, long rows)
        {
            _keys = keys;
            _x = x;
            _y = y;
            _z = z;
            _w = w;
            _minX = minX;
            _minY = minY;
            _cell = cell;
            _columns = columns;
            _rows = rows;
        }

        public static SampleGrid Build(
            double[] vertices,
            int vertexCount,
            bool[] referenced,
            double[] weights,
            double minimumWeight,
            double minX,
            double minY,
            double extentX,
            double extentY,
            double cell)
        {
            long columns = (long)Math.Floor(extentX / cell) + 1;
            long rows = (long)Math.Floor(extentY / cell) + 1;

            var keys = new long[vertexCount];
            var order = new int[vertexCount];
            int count = 0;
            for (int v = 0; v < vertexCount; v++)
            {
                if (!referenced[v])
                    continue;

                long column = Math.Clamp((long)((vertices[v * 3] - minX) / cell), 0, columns - 1);
                long row = Math.Clamp((long)((vertices[(v * 3) + 1] - minY) / cell), 0, rows - 1);
                keys[count] = (row * columns) + column;
                order[count] = v;
                count++;
            }

            // Sorting by (key, vertex) keeps the accumulation order within a cell fixed.
            var packed = new (long Key, int Vertex)[count];
            for (int i = 0; i < count; i++)
                packed[i] = (keys[i], order[i]);
            Array.Sort(packed);

            var sampleKeys = new List<long>();
            var sx = new List<double>();
            var sy = new List<double>();
            var sz = new List<double>();
            var sw = new List<double>();
            int index = 0;
            while (index < count)
            {
                long key = packed[index].Key;
                double wSum = 0.0, xSum = 0.0, ySum = 0.0, zSum = 0.0;
                for (; index < count && packed[index].Key == key; index++)
                {
                    int v = packed[index].Vertex;
                    double weight = Math.Max(weights[v], minimumWeight);
                    wSum += weight;
                    xSum += weight * vertices[v * 3];
                    ySum += weight * vertices[(v * 3) + 1];
                    zSum += weight * vertices[(v * 3) + 2];
                }

                sampleKeys.Add(key);
                sx.Add(xSum / wSum);
                sy.Add(ySum / wSum);
                sz.Add(zSum / wSum);
                sw.Add(wSum);
            }

            return new SampleGrid(sampleKeys.ToArray(), sx.ToArray(), sy.ToArray(), sz.ToArray(), sw.ToArray(), minX, minY, cell, columns, rows);
        }

        /// <summary>
        /// Fits the weighted quadric centred on (x0, y0) over the samples within <paramref name="radius"/> and
        /// writes p, q, r, s, t. False when too few samples fall inside or they do not determine a quadric.
        /// </summary>
        public bool TryFit(double x0, double y0, double z0, double radius, Span<double> derivatives)
        {
            long column = (long)Math.Floor((x0 - _minX) / _cell);
            long row = (long)Math.Floor((y0 - _minY) / _cell);
            long reach = (long)Math.Ceiling(radius / _cell);
            long rowFirst = Math.Max(0, row - reach);
            long rowLast = Math.Min(_rows - 1, row + reach);
            long columnFirst = Math.Max(0, column - reach);
            long columnLast = Math.Min(_columns - 1, column + reach);
            if (rowFirst > rowLast || columnFirst > columnLast)
                return false;

            double inverseRadius = 1.0 / radius;

            // Weighted moments of the monomials up to degree four, in coordinates scaled by the radius.
            double m00 = 0, m10 = 0, m01 = 0, m20 = 0, m11 = 0, m02 = 0;
            double m30 = 0, m21 = 0, m12 = 0, m03 = 0, m40 = 0, m31 = 0, m22 = 0, m13 = 0, m04 = 0;
            double z00 = 0, z10 = 0, z01 = 0, z20 = 0, z11 = 0, z02 = 0;
            int samples = 0;

            for (long r = rowFirst; r <= rowLast; r++)
            {
                long first = (r * _columns) + columnFirst;
                long last = (r * _columns) + columnLast;
                for (int i = LowerBound(_keys, first); i < _keys.Length && _keys[i] <= last; i++)
                {
                    double u = (_x[i] - x0) * inverseRadius;
                    double v = (_y[i] - y0) * inverseRadius;
                    double d2 = (u * u) + (v * v);
                    if (d2 >= 1.0)
                        continue;

                    double taper = 1.0 - d2;
                    double w = taper * taper * _w[i];
                    double z = _z[i] - z0;
                    double uu = u * u, uv = u * v, vv = v * v;

                    m00 += w;
                    m10 += w * u;
                    m01 += w * v;
                    m20 += w * uu;
                    m11 += w * uv;
                    m02 += w * vv;
                    m30 += w * uu * u;
                    m21 += w * uu * v;
                    m12 += w * u * vv;
                    m03 += w * vv * v;
                    m40 += w * uu * uu;
                    m31 += w * uu * uv;
                    m22 += w * uu * vv;
                    m13 += w * uv * vv;
                    m04 += w * vv * vv;
                    z00 += w * z;
                    z10 += w * z * u;
                    z01 += w * z * v;
                    z20 += w * z * uu;
                    z11 += w * z * uv;
                    z02 += w * z * vv;
                    samples++;
                }
            }

            if (samples < MinimumSamples || !(m00 > 0.0))
                return false;

            // Unknowns in order [a, b, c, d, e, f] for the basis [u2, v2, uv, u, v, 1].
            Span<double> m = stackalloc double[36];
            Span<double> rhs = stackalloc double[6];
            Set(m, 0, 0, m40); Set(m, 0, 1, m22); Set(m, 0, 2, m31); Set(m, 0, 3, m30); Set(m, 0, 4, m21); Set(m, 0, 5, m20);
            Set(m, 1, 1, m04); Set(m, 1, 2, m13); Set(m, 1, 3, m12); Set(m, 1, 4, m03); Set(m, 1, 5, m02);
            Set(m, 2, 2, m22); Set(m, 2, 3, m21); Set(m, 2, 4, m12); Set(m, 2, 5, m11);
            Set(m, 3, 3, m20); Set(m, 3, 4, m11); Set(m, 3, 5, m10);
            Set(m, 4, 4, m02); Set(m, 4, 5, m01);
            Set(m, 5, 5, m00);
            rhs[0] = z20; rhs[1] = z02; rhs[2] = z11; rhs[3] = z10; rhs[4] = z01; rhs[5] = z00;

            if (!SolveCholesky(m, rhs))
                return false;

            double a = rhs[0], b = rhs[1], c = rhs[2], d = rhs[3], e = rhs[4];
            double scale2 = inverseRadius * inverseRadius;
            derivatives[0] = d * inverseRadius;
            derivatives[1] = e * inverseRadius;
            derivatives[2] = 2.0 * a * scale2;
            derivatives[3] = c * scale2;
            derivatives[4] = 2.0 * b * scale2;
            for (int k = 0; k < 5; k++)
            {
                if (!double.IsFinite(derivatives[k]))
                    return false;
            }

            return true;
        }

        private static void Set(Span<double> m, int row, int column, double value)
        {
            m[(row * 6) + column] = value;
            m[(column * 6) + row] = value;
        }

        /// <summary>
        /// Solves the symmetric 6x6 system in place (solution in <paramref name="rhs"/>). False when a pivot
        /// collapses relative to its own diagonal: the samples do not determine that coefficient.
        /// </summary>
        private static bool SolveCholesky(Span<double> m, Span<double> rhs)
        {
            const int n = 6;
            const double relativePivot = 1e-9;
            Span<double> diagonal = stackalloc double[n];
            for (int i = 0; i < n; i++)
                diagonal[i] = m[(i * n) + i];

            for (int j = 0; j < n; j++)
            {
                double sum = m[(j * n) + j];
                for (int k = 0; k < j; k++)
                    sum -= m[(j * n) + k] * m[(j * n) + k];
                if (!(sum > relativePivot * diagonal[j]))
                    return false;

                double pivot = Math.Sqrt(sum);
                m[(j * n) + j] = pivot;
                for (int i = j + 1; i < n; i++)
                {
                    double value = m[(i * n) + j];
                    for (int k = 0; k < j; k++)
                        value -= m[(i * n) + k] * m[(j * n) + k];
                    m[(i * n) + j] = value / pivot;
                }
            }

            for (int i = 0; i < n; i++)
            {
                double value = rhs[i];
                for (int k = 0; k < i; k++)
                    value -= m[(i * n) + k] * rhs[k];
                rhs[i] = value / m[(i * n) + i];
            }

            for (int i = n - 1; i >= 0; i--)
            {
                double value = rhs[i];
                for (int k = i + 1; k < n; k++)
                    value -= m[(k * n) + i] * rhs[k];
                rhs[i] = value / m[(i * n) + i];
            }

            return true;
        }

        private static int LowerBound(long[] keys, long value)
        {
            int low = 0;
            int high = keys.Length;
            while (low < high)
            {
                int mid = (int)((uint)(low + high) >> 1);
                if (keys[mid] < value)
                    low = mid + 1;
                else
                    high = mid;
            }

            return low;
        }
    }
}
