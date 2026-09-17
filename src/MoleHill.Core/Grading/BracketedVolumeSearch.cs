// Bounded cut/fill net-volume search for exploratory grading alternatives.
namespace MoleHill.Core.Grading;

public readonly record struct VolumeSearchSample(
    double Elevation,
    double Cut,
    double Fill,
    bool GradingFallback = false,
    string? Diagnostic = null)
{
    public double Net => Cut - Fill;
}

public sealed class VolumeSearchResult
{
    public required string Status { get; init; }
    public required IReadOnlyList<VolumeSearchSample> Samples { get; init; }
    public VolumeSearchSample? Best { get; init; }
    public string? Diagnostic { get; init; }
}

public static class BracketedVolumeSearch
{
    public static VolumeSearchResult Search(
        Func<double, VolumeSearchSample?> evaluate,
        double minimumElevation,
        double maximumElevation,
        double targetNet,
        double volumeTolerance,
        int iterationCap = 24)
    {
        ArgumentNullException.ThrowIfNull(evaluate);
        if (!double.IsFinite(minimumElevation) || !double.IsFinite(maximumElevation) ||
            minimumElevation >= maximumElevation || !double.IsFinite(targetNet) ||
            !double.IsFinite(volumeTolerance) || volumeTolerance < 0 || iterationCap < 0)
            throw new ArgumentOutOfRangeException(nameof(minimumElevation), "Search bounds, target, tolerance, and cap must be finite and valid.");

        var samples = new List<VolumeSearchSample>();
        VolumeSearchSample? best = null;
        string? failure = null;

        bool TrySample(double elevation, out VolumeSearchSample sample)
        {
            VolumeSearchSample? evaluated = evaluate(elevation);
            if (evaluated is not { } value ||
                !double.IsFinite(value.Cut) || !double.IsFinite(value.Fill) ||
                value.Cut < 0 || value.Fill < 0)
            {
                failure = $"Grading or volume measurement failed at elevation {elevation:G17}.";
                sample = default;
                return false;
            }

            sample = value with { Elevation = elevation };
            samples.Add(sample);
            if (best is null || Math.Abs(sample.Net - targetNet) < Math.Abs(best.Value.Net - targetNet))
                best = sample;
            return true;
        }

        VolumeSearchResult Finish(string proposedStatus, string? diagnostic = null)
        {
            bool fallback = samples.Any(sample => sample.GradingFallback);
            bool nonMonotone = IsNonMonotone(samples);
            string status = failure != null || fallback ? "GradingFallback" :
                nonMonotone ? "NonMonotone" : proposedStatus;
            string? reason = failure ?? diagnostic;
            if (fallback)
                reason = string.Join(" ", samples.Where(sample => sample.GradingFallback)
                    .Select(sample => sample.Diagnostic ?? "A grading tier fell back."));
            if (nonMonotone && !fallback)
                reason = "Measured net volume is not monotonic across sampled elevations.";
            return new VolumeSearchResult
            {
                Status = status,
                Samples = samples.ToArray(),
                Best = best,
                Diagnostic = reason
            };
        }

        if (!TrySample(minimumElevation, out VolumeSearchSample lower))
            return Finish("GradingFallback");
        if (!TrySample(maximumElevation, out VolumeSearchSample upper))
            return Finish("GradingFallback");

        double lowerError = lower.Net - targetNet;
        double upperError = upper.Net - targetNet;
        if (Math.Sign(lowerError) == Math.Sign(upperError) &&
            Math.Abs(lowerError) > volumeTolerance && Math.Abs(upperError) > volumeTolerance)
            return Finish("NoBracket", "The target net volume is outside the measured endpoint range.");
        if (Math.Abs(lowerError) <= volumeTolerance || Math.Abs(upperError) <= volumeTolerance)
            return Finish("Converged");

        for (int iteration = 0; iteration < iterationCap; iteration++)
        {
            double midpoint = lower.Elevation + (upper.Elevation - lower.Elevation) * 0.5;
            if (midpoint == lower.Elevation || midpoint == upper.Elevation)
                break;
            if (!TrySample(midpoint, out VolumeSearchSample middle))
                return Finish("GradingFallback");
            double middleError = middle.Net - targetNet;
            if (Math.Abs(middleError) <= volumeTolerance)
                return Finish("Converged");
            if (Math.Sign(middleError) == Math.Sign(lowerError))
            {
                lower = middle;
                lowerError = middleError;
            }
            else
            {
                upper = middle;
                upperError = middleError;
            }
        }

        return Finish("IterationCap", "The search reached its iteration cap before meeting the volume tolerance.");
    }

    private static bool IsNonMonotone(IReadOnlyList<VolumeSearchSample> samples)
    {
        if (samples.Count < 3)
            return false;
        VolumeSearchSample[] ordered = samples.OrderBy(sample => sample.Elevation).ToArray();
        double endpointDirection = ordered[^1].Net - ordered[0].Net;
        for (int index = 1; index < ordered.Length; index++)
        {
            double step = ordered[index].Net - ordered[index - 1].Net;
            if (endpointDirection > 0 && step < -1e-9 || endpointDirection < 0 && step > 1e-9 ||
                endpointDirection == 0 && Math.Abs(step) > 1e-9)
                return true;
        }
        return false;
    }
}
