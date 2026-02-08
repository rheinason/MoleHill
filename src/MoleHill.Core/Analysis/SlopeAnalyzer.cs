namespace MoleHill.Core.Analysis;

/// <summary>
/// Computes per-face slope values from a triangle mesh.
/// </summary>
public static class SlopeAnalyzer
{
    public enum SlopeUnit
    {
        Ratio = 0,
        Percent = 1,
        Degrees = 2
    }

    /// <summary>
    /// Result of slope analysis on a triangle mesh.
    /// </summary>
    public sealed class SlopeResult
    {
        /// <summary>Per-face slope values in the requested unit.</summary>
        public double[] Slopes { get; }

        /// <summary>Minimum slope value.</summary>
        public double Min { get; }

        /// <summary>Maximum slope value.</summary>
        public double Max { get; }

        /// <summary>Area-weighted average slope.</summary>
        public double Average { get; }

        /// <summary>Per-face RGB colors (flat: [r0,g0,b0, r1,g1,b1, …], 0–255).</summary>
        public byte[] FaceColors { get; }

        /// <summary>Number of faces.</summary>
        public int FaceCount { get; }

        public SlopeResult(double[] slopes, double min, double max, double average,
                           byte[] faceColors, int faceCount)
        {
            Slopes = slopes;
            Min = min;
            Max = max;
            Average = average;
            FaceColors = faceColors;
            FaceCount = faceCount;
        }
    }

    /// <summary>
    /// Compute per-face slopes for a triangle mesh.
    /// </summary>
    /// <param name="vertices">Flat XYZ: [x0,y0,z0, x1,y1,z1, …]</param>
    /// <param name="vertexCount">Number of vertices.</param>
    /// <param name="faces">Triangle indices: [i0,i1,i2, …]</param>
    /// <param name="faceCount">Number of triangles.</param>
    /// <param name="unit">Slope unit (ratio, percent, degrees).</param>
    /// <param name="colorLow">Low end of color range (in the chosen unit). Values at or below → green.</param>
    /// <param name="colorHigh">High end of color range (in the chosen unit). Values at or above → red. 0 = auto.</param>
    public static SlopeResult Analyze(double[] vertices, int vertexCount,
                                       int[] faces, int faceCount,
                                       SlopeUnit unit,
                                       double colorLow = 0,
                                       double colorHigh = 0)
    {
        var slopes = new double[faceCount];
        var colors = new byte[faceCount * 3];
        double min = double.MaxValue;
        double max = double.MinValue;
        double weightedSum = 0;
        double totalArea = 0;

        for (int f = 0; f < faceCount; f++)
        {
            int i0 = faces[f * 3];
            int i1 = faces[f * 3 + 1];
            int i2 = faces[f * 3 + 2];

            double ax = vertices[i0 * 3], ay = vertices[i0 * 3 + 1], az = vertices[i0 * 3 + 2];
            double bx = vertices[i1 * 3], by = vertices[i1 * 3 + 1], bz = vertices[i1 * 3 + 2];
            double cx = vertices[i2 * 3], cy = vertices[i2 * 3 + 1], cz = vertices[i2 * 3 + 2];

            // Edge vectors
            double e1x = bx - ax, e1y = by - ay, e1z = bz - az;
            double e2x = cx - ax, e2y = cy - ay, e2z = cz - az;

            // Normal = e1 × e2
            double nx = e1y * e2z - e1z * e2y;
            double ny = e1z * e2x - e1x * e2z;
            double nz = e1x * e2y - e1y * e2x;

            // 3D face area = 0.5 * |normal|
            double normalLen = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            double area = normalLen * 0.5;

            // Slope ratio = sqrt(Nx² + Ny²) / |Nz|
            double absNz = Math.Abs(nz);
            double slopeRatio;
            if (absNz < 1e-12)
                slopeRatio = double.PositiveInfinity; // vertical face
            else
                slopeRatio = Math.Sqrt(nx * nx + ny * ny) / absNz;

            double slope = unit switch
            {
                SlopeUnit.Ratio => slopeRatio,
                SlopeUnit.Percent => slopeRatio * 100.0,
                SlopeUnit.Degrees => Math.Atan(slopeRatio) * (180.0 / Math.PI),
                _ => slopeRatio
            };

            slopes[f] = slope;

            if (!double.IsInfinity(slope) && !double.IsNaN(slope))
            {
                if (slope < min) min = slope;
                if (slope > max) max = slope;
                weightedSum += slope * area;
                totalArea += area;
            }
        }

        if (min == double.MaxValue) min = 0;
        if (max == double.MinValue) max = 0;

        double average = totalArea > 0 ? weightedSum / totalArea : 0;

        // Determine color range
        double lo = colorLow;
        double hi = colorHigh > lo ? colorHigh : max;
        if (hi <= lo) hi = lo + 1; // avoid division by zero

        // Apply colors with the determined range
        for (int f = 0; f < faceCount; f++)
        {
            SlopeToColor(slopes[f], lo, hi, out byte r, out byte g, out byte b);
            colors[f * 3] = r;
            colors[f * 3 + 1] = g;
            colors[f * 3 + 2] = b;
        }

        return new SlopeResult(slopes, min, max, average, colors, faceCount);
    }

    /// <summary>
    /// Map slope value to green→yellow→red gradient within the given range.
    /// </summary>
    private static void SlopeToColor(double slope, double lo, double hi,
                                      out byte r, out byte g, out byte b)
    {
        if (double.IsInfinity(slope) || double.IsNaN(slope) || slope >= hi)
        {
            r = 255; g = 0; b = 0;
            return;
        }

        if (slope <= lo)
        {
            r = 0; g = 200; b = 0;
            return;
        }

        // Normalize to 0..1 within the range
        double t = (slope - lo) / (hi - lo);

        if (t <= 0.5)
        {
            // Green → Yellow (0→0.5)
            double s = t / 0.5;
            r = (byte)(s * 255);
            g = (byte)(200 + s * 55); // 200→255
            b = 0;
        }
        else
        {
            // Yellow → Red (0.5→1.0)
            double s = (t - 0.5) / 0.5;
            r = 255;
            g = (byte)(255 * (1.0 - s));
            b = 0;
        }
    }
}
