namespace MoleHill.Core.Analysis;

/// <summary>
/// Computes per-face slope values from a triangle mesh.
/// </summary>
public static class SlopeAnalyzer
{
    private const int ParallelSlopeThreshold = 20_000;

    public readonly struct ColorStop
    {
        public ColorStop(double position, byte r, byte g, byte b)
        {
            Position = double.IsNaN(position) ? 0.0 : Math.Clamp(position, 0.0, 1.0);
            R = r;
            G = g;
            B = b;
        }

        public double Position { get; }

        public byte R { get; }

        public byte G { get; }

        public byte B { get; }
    }

    private static readonly ColorStop[] DefaultPalette =
    {
        new(0.0, 0, 200, 0),
        new(0.5, 255, 255, 0),
        new(1.0, 255, 0, 0)
    };

    public enum SlopeUnit
    {
        Ratio = 0,
        Percent = 1,
        Degrees = 2,
        Promille = 3
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

        /// <summary>Per-face RGB colors (flat: [r0,g0,b0, r1,g1,b1, ...], 0-255).</summary>
        public byte[] FaceColors { get; }

        /// <summary>Number of faces.</summary>
        public int FaceCount { get; }

        /// <summary>Low end of the display range actually used for color mapping.</summary>
        public double ColorLow { get; }

        /// <summary>High end of the display range actually used for color mapping.</summary>
        public double ColorHigh { get; }

        public SlopeResult(
            double[] slopes,
            double min,
            double max,
            double average,
            byte[] faceColors,
            int faceCount,
            double colorLow,
            double colorHigh)
        {
            Slopes = slopes;
            Min = min;
            Max = max;
            Average = average;
            FaceColors = faceColors;
            FaceCount = faceCount;
            ColorLow = colorLow;
            ColorHigh = colorHigh;
        }
    }

    /// <summary>
    /// Summary-only slope analysis on a triangle mesh. Does not allocate per-face slopes or colors.
    /// </summary>
    public sealed class SlopeSummary
    {
        public double Min { get; }

        public double Max { get; }

        public double Average { get; }

        public int FaceCount { get; }

        public double ColorLow { get; }

        public double ColorHigh { get; }

        public SlopeSummary(double min, double max, double average, int faceCount, double colorLow, double colorHigh)
        {
            Min = min;
            Max = max;
            Average = average;
            FaceCount = faceCount;
            ColorLow = colorLow;
            ColorHigh = colorHigh;
        }
    }

    /// <summary>
    /// Compute min, max, and area-weighted average slope without allocating per-face colors.
    /// </summary>
    public static SlopeSummary Summarize(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        SlopeUnit unit,
        double colorLow = 0,
        double colorHigh = 0)
    {
        SlopeAccumulator accumulator = faceCount >= ParallelSlopeThreshold
            ? AccumulateSlopesParallel(vertices, faces, faceCount, unit, null)
            : AccumulateSlopes(vertices, faces, faceCount, unit, null);

        return CreateSummary(accumulator, faceCount, colorLow, colorHigh);
    }

    /// <summary>
    /// Compute per-face slopes for a triangle mesh.
    /// </summary>
    /// <param name="vertices">Flat XYZ: [x0,y0,z0, x1,y1,z1, ...]</param>
    /// <param name="vertexCount">Number of vertices.</param>
    /// <param name="faces">Triangle indices: [i0,i1,i2, ...]</param>
    /// <param name="faceCount">Number of triangles.</param>
    /// <param name="unit">Slope unit (ratio, percent, degrees).</param>
    /// <param name="colorLow">Low end of color range (in the chosen unit). Values at or below use the first palette stop.</param>
    /// <param name="colorHigh">High end of color range (in the chosen unit). Values at or above use the last palette stop. 0 = auto.</param>
    /// <param name="palette">Optional normalized color stops. Null falls back to the default green-yellow-red ramp.</param>
    public static SlopeResult Analyze(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        SlopeUnit unit,
        double colorLow = 0,
        double colorHigh = 0,
        IReadOnlyList<ColorStop>? palette = null)
    {
        var slopes = new double[faceCount];
        SlopeAccumulator accumulator = faceCount >= ParallelSlopeThreshold
            ? AccumulateSlopesParallel(vertices, faces, faceCount, unit, slopes)
            : AccumulateSlopes(vertices, faces, faceCount, unit, slopes);
        SlopeSummary summary = CreateSummary(accumulator, faceCount, colorLow, colorHigh);

        var colors = new byte[faceCount * 3];
        var effectivePalette = ResolvePalette(palette);
        if (faceCount >= ParallelSlopeThreshold)
        {
            Parallel.For(0, faceCount, f =>
            {
                SlopeToColor(slopes[f], summary.ColorLow, summary.ColorHigh, effectivePalette, out byte r, out byte g, out byte b);
                colors[f * 3] = r;
                colors[f * 3 + 1] = g;
                colors[f * 3 + 2] = b;
            });
        }
        else
        {
            for (int f = 0; f < faceCount; f++)
            {
                SlopeToColor(slopes[f], summary.ColorLow, summary.ColorHigh, effectivePalette, out byte r, out byte g, out byte b);
                colors[f * 3] = r;
                colors[f * 3 + 1] = g;
                colors[f * 3 + 2] = b;
            }
        }

        return new SlopeResult(
            slopes,
            summary.Min,
            summary.Max,
            summary.Average,
            colors,
            faceCount,
            summary.ColorLow,
            summary.ColorHigh);
    }

    private static SlopeAccumulator AccumulateSlopes(
        double[] vertices,
        int[] faces,
        int faceCount,
        SlopeUnit unit,
        double[]? slopes)
    {
        var accumulator = SlopeAccumulator.Create();
        for (int f = 0; f < faceCount; f++)
        {
            double slope = ComputeFaceSlope(vertices, faces, f, unit, out double area);
            if (slopes != null)
                slopes[f] = slope;
            accumulator.Add(slope, area);
        }

        return accumulator;
    }

    private static SlopeAccumulator AccumulateSlopesParallel(
        double[] vertices,
        int[] faces,
        int faceCount,
        SlopeUnit unit,
        double[]? slopes)
    {
        var accumulator = SlopeAccumulator.Create();
        object gate = new();
        Parallel.For(0, faceCount,
            () => SlopeAccumulator.Create(),
            (f, _, local) =>
            {
                double slope = ComputeFaceSlope(vertices, faces, f, unit, out double area);
                if (slopes != null)
                    slopes[f] = slope;
                local.Add(slope, area);
                return local;
            },
            local =>
            {
                lock (gate)
                {
                    accumulator.Merge(local);
                }
            });

        return accumulator;
    }

    private static SlopeSummary CreateSummary(SlopeAccumulator accumulator, int faceCount, double colorLow, double colorHigh)
    {
        double min = accumulator.HasFinite ? accumulator.Min : 0.0;
        double max = accumulator.HasFinite ? accumulator.Max : 0.0;
        double average = accumulator.TotalArea > 0 ? accumulator.WeightedSum / accumulator.TotalArea : 0.0;
        double lo = colorLow;
        double hi = colorHigh > lo ? colorHigh : max;
        if (hi <= lo)
            hi = lo + 1;

        return new SlopeSummary(min, max, average, faceCount, lo, hi);
    }

    private static double ComputeFaceSlope(double[] vertices, int[] faces, int faceIndex, SlopeUnit unit, out double area)
    {
        int i0 = faces[faceIndex * 3];
        int i1 = faces[faceIndex * 3 + 1];
        int i2 = faces[faceIndex * 3 + 2];

        double ax = vertices[i0 * 3];
        double ay = vertices[i0 * 3 + 1];
        double az = vertices[i0 * 3 + 2];
        double bx = vertices[i1 * 3];
        double by = vertices[i1 * 3 + 1];
        double bz = vertices[i1 * 3 + 2];
        double cx = vertices[i2 * 3];
        double cy = vertices[i2 * 3 + 1];
        double cz = vertices[i2 * 3 + 2];

        double e1x = bx - ax;
        double e1y = by - ay;
        double e1z = bz - az;
        double e2x = cx - ax;
        double e2y = cy - ay;
        double e2z = cz - az;

        double nx = e1y * e2z - e1z * e2y;
        double ny = e1z * e2x - e1x * e2z;
        double nz = e1x * e2y - e1y * e2x;

        double normalLen = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        area = normalLen * 0.5;

        double absNz = Math.Abs(nz);
        double slopeRatio = absNz < 1e-12
            ? double.PositiveInfinity
            : Math.Sqrt(nx * nx + ny * ny) / absNz;

        return ConvertRatioToUnit(slopeRatio, unit);
    }

    private struct SlopeAccumulator
    {
        public double Min;
        public double Max;
        public double WeightedSum;
        public double TotalArea;
        public bool HasFinite;

        public static SlopeAccumulator Create()
        {
            return new SlopeAccumulator
            {
                Min = double.MaxValue,
                Max = double.MinValue
            };
        }

        public void Add(double slope, double area)
        {
            if (double.IsInfinity(slope) || double.IsNaN(slope))
                return;

            if (slope < Min)
                Min = slope;
            if (slope > Max)
                Max = slope;

            WeightedSum += slope * area;
            TotalArea += area;
            HasFinite = true;
        }

        public void Merge(SlopeAccumulator other)
        {
            if (!other.HasFinite)
                return;

            if (!HasFinite)
            {
                Min = other.Min;
                Max = other.Max;
            }
            else
            {
                Min = Math.Min(Min, other.Min);
                Max = Math.Max(Max, other.Max);
            }

            WeightedSum += other.WeightedSum;
            TotalArea += other.TotalArea;
            HasFinite = true;
        }
    }

    public static double ConvertRatioToUnit(double slopeRatio, SlopeUnit unit)
    {
        return unit switch
        {
            SlopeUnit.Ratio => slopeRatio,
            SlopeUnit.Percent => slopeRatio * 100.0,
            SlopeUnit.Degrees => Math.Atan(slopeRatio) * (180.0 / Math.PI),
            SlopeUnit.Promille => slopeRatio * 1000.0,
            _ => slopeRatio
        };
    }

    public static double ConvertUnitToRatio(double slopeValue, SlopeUnit unit)
    {
        return unit switch
        {
            SlopeUnit.Ratio => slopeValue,
            SlopeUnit.Percent => slopeValue / 100.0,
            SlopeUnit.Degrees => Math.Tan(slopeValue * (Math.PI / 180.0)),
            SlopeUnit.Promille => slopeValue / 1000.0,
            _ => slopeValue
        };
    }

    /// <summary>
    /// Map slope value to the supplied gradient within the given range.
    /// </summary>
    private static void SlopeToColor(
        double slope,
        double lo,
        double hi,
        IReadOnlyList<ColorStop> palette,
        out byte r,
        out byte g,
        out byte b)
    {
        if (palette.Count == 0)
        {
            r = 0;
            g = 200;
            b = 0;
            return;
        }

        if (double.IsInfinity(slope) || double.IsNaN(slope) || slope >= hi)
        {
            var last = palette[^1];
            r = last.R;
            g = last.G;
            b = last.B;
            return;
        }

        if (slope <= lo)
        {
            var first = palette[0];
            r = first.R;
            g = first.G;
            b = first.B;
            return;
        }

        double t = (slope - lo) / (hi - lo);
        ColorStop previous = palette[0];

        for (int i = 1; i < palette.Count; i++)
        {
            ColorStop current = palette[i];
            if (t > current.Position)
            {
                previous = current;
                continue;
            }

            double segment = current.Position - previous.Position;
            if (segment <= 1e-9)
            {
                r = current.R;
                g = current.G;
                b = current.B;
                return;
            }

            double localT = (t - previous.Position) / segment;
            r = InterpolateChannel(previous.R, current.R, localT);
            g = InterpolateChannel(previous.G, current.G, localT);
            b = InterpolateChannel(previous.B, current.B, localT);
            return;
        }

        var fallback = palette[^1];
        r = fallback.R;
        g = fallback.G;
        b = fallback.B;
    }

    private static IReadOnlyList<ColorStop> ResolvePalette(IReadOnlyList<ColorStop>? palette)
    {
        if (palette == null || palette.Count == 0)
            return DefaultPalette;

        if (palette.Count == 1)
            return new[] { new ColorStop(0.0, palette[0].R, palette[0].G, palette[0].B) };

        return palette
            .OrderBy(stop => stop.Position)
            .Select(stop => new ColorStop(stop.Position, stop.R, stop.G, stop.B))
            .ToArray();
    }

    private static byte InterpolateChannel(byte a, byte b, double t)
    {
        double clamped = Math.Clamp(t, 0.0, 1.0);
        return (byte)Math.Round(a + ((b - a) * clamped));
    }
}
