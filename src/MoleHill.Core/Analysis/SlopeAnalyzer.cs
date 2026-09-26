namespace MoleHill.Core.Analysis;

/// <summary>
/// Computes per-face slope values from a triangle mesh.
/// </summary>
public static class SlopeAnalyzer
{
    private const int ParallelSlopeThreshold = 20_000;

    /// <summary>Slope is measured from the face normal, so the shape a fitted range takes is always
    /// "starts at flat ground".</summary>
    private const RangeShape SlopeRangeShape = RangeShape.FromZero;

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

        /// <summary>The range actually mapped across the palette.</summary>
        public AnalysisRange Range { get; }

        /// <summary>Low end of the display range actually used for color mapping.</summary>
        public double ColorLow => Range.Low;

        /// <summary>High end of the display range actually used for color mapping.</summary>
        public double ColorHigh => Range.High;

        public SlopeResult(
            double[] slopes,
            double min,
            double max,
            double average,
            byte[] faceColors,
            int faceCount,
            AnalysisRange range)
        {
            Slopes = slopes;
            Min = min;
            Max = max;
            Average = average;
            FaceColors = faceColors;
            FaceCount = faceCount;
            Range = range;
        }
    }

    /// <summary>
    /// Summary-only slope analysis on a triangle mesh. Does not allocate per-face colors.
    /// </summary>
    public sealed class SlopeSummary
    {
        public double Min { get; }

        public double Max { get; }

        public double Average { get; }

        public int FaceCount { get; }

        /// <summary>The range actually mapped across the palette.</summary>
        public AnalysisRange Range { get; }

        public double ColorLow => Range.Low;

        public double ColorHigh => Range.High;

        public SlopeSummary(double min, double max, double average, int faceCount, AnalysisRange range)
        {
            Min = min;
            Max = max;
            Average = average;
            FaceCount = faceCount;
            Range = range;
        }
    }

    /// <summary>
    /// Compute min, max, and area-weighted average slope without allocating per-face colors.
    /// </summary>
    /// <param name="autoRange">Fit the display range to the slope distribution rather than to
    /// <paramref name="requestedLow"/>/<paramref name="requestedHigh"/>. See <see cref="AnalysisRange"/>.</param>
    public static SlopeSummary Summarize(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        SlopeUnit unit,
        bool autoRange = true,
        double requestedLow = 0,
        double requestedHigh = 0)
    {
        // Fitting the range needs the distribution, so the slopes and plan areas are collected for the
        // auto path only. With explicit bounds this stays the allocation-free summary it was.
        double[]? slopes = autoRange ? new double[faceCount] : null;
        double[]? planAreas = autoRange ? new double[faceCount] : null;

        SlopeAccumulator accumulator = faceCount >= ParallelSlopeThreshold
            ? AccumulateSlopesParallel(vertices, faces, faceCount, unit, slopes, planAreas)
            : AccumulateSlopes(vertices, faces, faceCount, unit, slopes, planAreas);

        AnalysisRange range = ResolveRange(slopes, planAreas, autoRange, requestedLow, requestedHigh);
        return CreateSummary(accumulator, faceCount, range);
    }

    /// <summary>
    /// Compute per-face slopes for a triangle mesh.
    /// </summary>
    /// <param name="vertices">Flat XYZ: [x0,y0,z0, x1,y1,z1, ...]</param>
    /// <param name="vertexCount">Number of vertices.</param>
    /// <param name="faces">Triangle indices: [i0,i1,i2, ...]</param>
    /// <param name="faceCount">Number of triangles.</param>
    /// <param name="unit">Slope unit (ratio, percent, degrees).</param>
    /// <param name="autoRange">Fit the display range to the slope distribution rather than to the
    /// requested bounds.</param>
    /// <param name="requestedLow">Low end of the color range in the chosen unit, when not auto-fitting.</param>
    /// <param name="requestedHigh">High end of the color range in the chosen unit, when not auto-fitting.</param>
    /// <param name="palette">Optional normalized color stops. Null falls back to the default green-yellow-red ramp.</param>
    /// <param name="mode">Smooth gradient or stepped bands.</param>
    /// <param name="interval">Band width in the chosen unit for stepped mode; 0 picks a readable step.</param>
    public static SlopeResult Analyze(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        SlopeUnit unit,
        bool autoRange = true,
        double requestedLow = 0,
        double requestedHigh = 0,
        IReadOnlyList<ColorStop>? palette = null,
        AnalysisColorMapper.Mode mode = AnalysisColorMapper.Mode.Gradient,
        double interval = 0.0)
    {
        var slopes = new double[faceCount];
        var planAreas = new double[faceCount];
        SlopeAccumulator accumulator = faceCount >= ParallelSlopeThreshold
            ? AccumulateSlopesParallel(vertices, faces, faceCount, unit, slopes, planAreas)
            : AccumulateSlopes(vertices, faces, faceCount, unit, slopes, planAreas);

        AnalysisRange range = ResolveRange(slopes, planAreas, autoRange, requestedLow, requestedHigh);
        SlopeSummary summary = CreateSummary(accumulator, faceCount, range);

        var colors = new byte[faceCount * 3];
        var effectivePalette = ResolvePalette(palette);

        // Bands are resolved once and shared by every face, and are the same list the legend draws.
        IReadOnlyList<AnalysisColorMapper.Band>? bands =
            AnalysisColorMapper.ResolveBandsFor(range, mode, interval, effectivePalette);

        void ColorFace(int f)
        {
            ColorStop color = AnalysisColorMapper.SampleResolved(slopes[f], range, mode, bands, effectivePalette);
            colors[f * 3] = color.R;
            colors[f * 3 + 1] = color.G;
            colors[f * 3 + 2] = color.B;
        }

        if (faceCount >= ParallelSlopeThreshold)
            Parallel.For(0, faceCount, ColorFace);
        else
            for (int f = 0; f < faceCount; f++)
                ColorFace(f);

        return new SlopeResult(
            slopes,
            summary.Min,
            summary.Max,
            summary.Average,
            colors,
            faceCount,
            range);
    }

    /// <summary>The display range for a set of slopes. <paramref name="planAreas"/> weights the auto fit.</summary>
    private static AnalysisRange ResolveRange(
        double[]? slopes,
        double[]? planAreas,
        bool autoRange,
        double requestedLow,
        double requestedHigh)
    {
        if (!autoRange || slopes == null)
            return AnalysisRange.FromRequested(requestedLow, requestedHigh, SlopeRangeShape);

        return AnalysisRange.Resolve(
            slopes,
            planAreas ?? ReadOnlySpan<double>.Empty,
            auto: true,
            requestedLow,
            requestedHigh,
            SlopeRangeShape);
    }

    private static SlopeAccumulator AccumulateSlopes(
        double[] vertices,
        int[] faces,
        int faceCount,
        SlopeUnit unit,
        double[]? slopes,
        double[]? planAreas)
    {
        var accumulator = SlopeAccumulator.Create();
        for (int f = 0; f < faceCount; f++)
        {
            double slope = ComputeFaceSlope(vertices, faces, f, unit, out double area, out double planArea);
            if (slopes != null)
                slopes[f] = slope;
            if (planAreas != null)
                planAreas[f] = planArea;
            accumulator.Add(slope, area);
        }

        return accumulator;
    }

    private static SlopeAccumulator AccumulateSlopesParallel(
        double[] vertices,
        int[] faces,
        int faceCount,
        SlopeUnit unit,
        double[]? slopes,
        double[]? planAreas)
    {
        var accumulator = SlopeAccumulator.Create();
        object gate = new();
        Parallel.For(0, faceCount,
            () => SlopeAccumulator.Create(),
            (f, _, local) =>
            {
                double slope = ComputeFaceSlope(vertices, faces, f, unit, out double area, out double planArea);
                if (slopes != null)
                    slopes[f] = slope;
                if (planAreas != null)
                    planAreas[f] = planArea;
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

    private static SlopeSummary CreateSummary(SlopeAccumulator accumulator, int faceCount, AnalysisRange range)
    {
        double min = accumulator.HasFinite ? accumulator.Min : 0.0;
        double max = accumulator.HasFinite ? accumulator.Max : 0.0;
        double average = accumulator.TotalArea > 0 ? accumulator.WeightedSum / accumulator.TotalArea : 0.0;

        return new SlopeSummary(min, max, average, faceCount, range.EnsureNonDegenerate());
    }

    /// <param name="area">True 3D surface area of the face.</param>
    /// <param name="planArea">The face's area projected onto XY. This, not the 3D area, is what weights a
    /// fitted colour range: a slope map is read in plan, and a near-vertical retaining wall covers a lot of
    /// surface but almost no ground. Weighting by 3D area does the opposite of what is wanted — it gives the
    /// wall <em>more</em> influence than the terrain it retains.</param>
    private static double ComputeFaceSlope(
        double[] vertices,
        int[] faces,
        int faceIndex,
        SlopeUnit unit,
        out double area,
        out double planArea)
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
        planArea = absNz * 0.5;
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
}
