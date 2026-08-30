namespace MoleHill.Core.Analysis;

/// <summary>How an auto-fitted range is anchored once its data spread is known.</summary>
public enum RangeShape
{
    /// <summary>Low is pinned to zero. Slope: a ramp that starts anywhere but zero misreads flat ground.</summary>
    FromZero = 0,

    /// <summary>Low and high both follow the data. Elevation.</summary>
    MinMax = 1,

    /// <summary>Low and high are mirrored about zero. Cut/fill, so the neutral colour sits at no change.</summary>
    SymmetricAboutZero = 2
}

/// <summary>
/// The range of values mapped across an analysis palette, and the single owner of what "auto-fit" means.
///
/// Auto-fit is a trimmed percentile, not the raw extremes: a terrain with one near-vertical retaining wall
/// face has a maximum slope in the hundreds of percent, and fitting the ramp to it paints every real
/// gradient the same green. Values outside the fitted range still draw — they clamp to the end colours —
/// so nothing is hidden, it is only no longer allowed to flatten everything else.
///
/// Every caller that colours faces, writes a summary, or draws a legend resolves through here, so the mesh
/// and the numbers beside it cannot drift apart.
/// </summary>
public readonly record struct AnalysisRange(double Low, double High, bool IsAuto)
{
    /// <summary>Fraction of the (weighted) distribution trimmed from each end before fitting.</summary>
    private const double TrimFraction = 0.02;

    private const int HistogramBins = 2048;

    public double Span => High - Low;

    /// <summary>A safe 0..1 range, used when there is nothing to fit.</summary>
    public static AnalysisRange Unit => new(0.0, 1.0, false);

    /// <summary>Normalized position of a value inside the range, clamped to 0..1.</summary>
    public double Normalize(double value)
    {
        double span = Span;
        if (!double.IsFinite(span) || span <= 0.0)
            return 0.0;
        return Math.Clamp((value - Low) / span, 0.0, 1.0);
    }

    /// <summary>Guarantees a strictly increasing range, so division by the span is always safe.</summary>
    public AnalysisRange EnsureNonDegenerate()
    {
        if (double.IsFinite(Low) && double.IsFinite(High) && High > Low)
            return this;

        double low = double.IsFinite(Low) ? Low : 0.0;
        return new AnalysisRange(low, low + 1.0, IsAuto);
    }

    /// <summary>
    /// Resolves the range for a set of values. When <paramref name="auto"/> is false the requested bounds
    /// are used as given (normalized for the shape); otherwise the values are fitted.
    /// </summary>
    /// <param name="weights">Per-value weights (face areas). Empty for an unweighted fit.</param>
    public static AnalysisRange Resolve(
        ReadOnlySpan<double> values,
        ReadOnlySpan<double> weights,
        bool auto,
        double requestedLow,
        double requestedHigh,
        RangeShape shape)
    {
        if (!auto)
            return FromRequested(requestedLow, requestedHigh, shape);

        return Histogram.Build(values, weights).ResolveAuto(shape);
    }

    /// <summary>Applies explicit user bounds, honouring the shape's invariants.</summary>
    public static AnalysisRange FromRequested(double requestedLow, double requestedHigh, RangeShape shape)
    {
        double low = double.IsFinite(requestedLow) ? requestedLow : 0.0;
        double high = double.IsFinite(requestedHigh) ? requestedHigh : low + 1.0;

        switch (shape)
        {
            case RangeShape.FromZero:
                low = Math.Max(0.0, low);
                high = Math.Max(0.0, high);
                break;

            case RangeShape.SymmetricAboutZero:
                double magnitude = Math.Max(Math.Abs(low), Math.Abs(high));
                low = -magnitude;
                high = magnitude;
                break;
        }

        return new AnalysisRange(low, high, false).EnsureNonDegenerate();
    }

    /// <summary>
    /// Rounds a fitted range outward to readable numbers, so a legend reads "0 to 35%" rather than
    /// "0 to 34.7183%". Never rounds inward — the fitted spread stays covered.
    /// </summary>
    public static AnalysisRange SnapOutward(double low, double high, RangeShape shape)
    {
        // The shape is applied before snapping, not after: a slope range must start at zero before its
        // width decides the step, or a mesh whose gradients all sit near 50% snaps to 50..51 instead of
        // 0..50 and the whole ramp collapses into its top band.
        (low, high) = ApplyShape(low, high, shape);

        double span = high - low;
        if (!double.IsFinite(span) || span <= 0.0)
        {
            // Nothing to spread across — a perfectly flat surface. Give the ramp a unit of room.
            return shape == RangeShape.SymmetricAboutZero
                ? new AnalysisRange(-1.0, 1.0, true)
                : new AnalysisRange(low, low + 1.0, true).EnsureNonDegenerate();
        }

        double step = NiceStep(span / 8.0);
        double snappedLow = Math.Floor(low / step) * step;
        double snappedHigh = Math.Ceiling(high / step) * step;
        (snappedLow, snappedHigh) = ApplyShape(snappedLow, snappedHigh, shape);

        return new AnalysisRange(snappedLow, snappedHigh, true).EnsureNonDegenerate();
    }

    /// <summary>Enforces a shape's anchoring invariant on a pair of bounds.</summary>
    private static (double Low, double High) ApplyShape(double low, double high, RangeShape shape)
    {
        switch (shape)
        {
            case RangeShape.FromZero:
                return (0.0, Math.Max(0.0, high));

            case RangeShape.SymmetricAboutZero:
                double magnitude = Math.Max(Math.Abs(low), Math.Abs(high));
                return (-magnitude, magnitude);

            default:
                return (low, high);
        }
    }

    /// <summary>
    /// The 1-2-5-10 "nice number" at or above <paramref name="rough"/>. Shared by range snapping and by
    /// <see cref="AnalysisColorMapper.ResolveInterval"/> so automatic bands land on the same numbers a
    /// draftsman would have chosen.
    /// </summary>
    public static double NiceStep(double rough)
    {
        if (!double.IsFinite(rough) || rough <= 0.0)
            return 1.0;

        double magnitude = Math.Pow(10.0, Math.Floor(Math.Log10(rough)));
        double normalized = rough / magnitude;
        double nice = normalized <= 1.0 ? 1.0 : normalized <= 2.0 ? 2.0 : normalized <= 5.0 ? 5.0 : 10.0;
        return nice * magnitude;
    }

    /// <summary>
    /// A fixed-bin weighted histogram, used to take percentiles without sorting or retaining the values.
    /// Callers that stream (a per-face loop that does not keep a slope array) build it in two cheap passes;
    /// callers that already hold the values use <see cref="Build"/>.
    /// </summary>
    public sealed class Histogram
    {
        private readonly double[] _bins = new double[HistogramBins];
        private double _min = double.MaxValue;
        private double _max = double.MinValue;
        private double _totalWeight;
        private bool _boundsFrozen;

        public bool HasData => _totalWeight > 0.0;

        public double Min => _boundsFrozen && _min <= _max ? _min : 0.0;

        public double Max => _boundsFrozen && _min <= _max ? _max : 0.0;

        /// <summary>Pass one: learn the extent. Non-finite values (vertical faces) are ignored outright.</summary>
        public void Observe(double value)
        {
            if (!double.IsFinite(value))
                return;

            if (value < _min)
                _min = value;
            if (value > _max)
                _max = value;
        }

        /// <summary>Ends pass one. Values added afterwards land in bins spanning the observed extent.</summary>
        public void FreezeBounds()
        {
            _boundsFrozen = true;
        }

        /// <summary>Pass two: accumulate weight. Requires <see cref="FreezeBounds"/> to have been called.</summary>
        public void Add(double value, double weight)
        {
            if (!double.IsFinite(value) || !double.IsFinite(weight) || weight <= 0.0)
                return;

            _bins[BinOf(value)] += weight;
            _totalWeight += weight;
        }

        public static Histogram Build(ReadOnlySpan<double> values, ReadOnlySpan<double> weights)
        {
            var histogram = new Histogram();
            for (int i = 0; i < values.Length; i++)
                histogram.Observe(values[i]);
            histogram.FreezeBounds();

            bool weighted = weights.Length == values.Length;
            for (int i = 0; i < values.Length; i++)
                histogram.Add(values[i], weighted ? weights[i] : 1.0);

            return histogram;
        }

        /// <summary>The trimmed, outward-snapped range for this distribution.</summary>
        public AnalysisRange ResolveAuto(RangeShape shape)
        {
            if (!HasData)
            {
                return shape == RangeShape.SymmetricAboutZero
                    ? new AnalysisRange(-1.0, 1.0, true)
                    : new AnalysisRange(0.0, 1.0, true);
            }

            double low = Percentile(TrimFraction);
            double high = Percentile(1.0 - TrimFraction);

            // A distribution concentrated in one bin (perfectly flat ground) trims to nothing; fall back to
            // the observed extent so the ramp still has somewhere to go.
            if (high - low <= 0.0)
            {
                low = _min;
                high = _max;
            }

            if (shape == RangeShape.SymmetricAboutZero)
            {
                double magnitude = Math.Max(Math.Abs(low), Math.Abs(high));
                low = -magnitude;
                high = magnitude;
            }

            return SnapOutward(low, high, shape);
        }

        /// <summary>
        /// Downsamples the distribution to <paramref name="binCount"/> bars scaled so the tallest is 1.0 —
        /// the shape a legend draws behind its ramp, which only needs relative height. Returns an empty
        /// array when there is nothing to show, so a caller can treat "no histogram" and "no data" alike.
        /// </summary>
        public double[] Resample(int binCount)
        {
            if (binCount <= 0 || !HasData)
                return Array.Empty<double>();

            var bars = new double[binCount];
            double scale = (double)binCount / _bins.Length;
            for (int bin = 0; bin < _bins.Length; bin++)
            {
                double weight = _bins[bin];
                if (weight <= 0.0)
                    continue;

                int target = (int)(bin * scale);
                bars[target < binCount ? target : binCount - 1] += weight;
            }

            double peak = 0.0;
            for (int i = 0; i < bars.Length; i++)
            {
                if (bars[i] > peak)
                    peak = bars[i];
            }

            if (peak <= 0.0)
                return Array.Empty<double>();

            for (int i = 0; i < bars.Length; i++)
                bars[i] /= peak;

            return bars;
        }

        /// <summary>Weighted percentile, linearly interpolated inside the containing bin.</summary>
        public double Percentile(double fraction)
        {
            if (!HasData)
                return 0.0;

            double target = Math.Clamp(fraction, 0.0, 1.0) * _totalWeight;
            double cumulative = 0.0;
            for (int bin = 0; bin < _bins.Length; bin++)
            {
                double weight = _bins[bin];
                if (weight <= 0.0)
                    continue;

                if (cumulative + weight >= target)
                {
                    double within = Math.Clamp((target - cumulative) / weight, 0.0, 1.0);
                    return ValueAt(bin + within);
                }

                cumulative += weight;
            }

            return _max;
        }

        private int BinOf(double value)
        {
            double span = _max - _min;
            if (!_boundsFrozen || !double.IsFinite(span) || span <= 0.0)
                return 0;

            int bin = (int)((value - _min) / span * (_bins.Length - 1));
            return Math.Clamp(bin, 0, _bins.Length - 1);
        }

        private double ValueAt(double binPosition)
        {
            double span = _max - _min;
            if (!double.IsFinite(span) || span <= 0.0)
                return _min;

            return _min + (binPosition / (_bins.Length - 1) * span);
        }
    }
}
