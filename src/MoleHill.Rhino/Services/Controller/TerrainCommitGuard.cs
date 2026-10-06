namespace MoleHill.Rhino.Services;

internal static class TerrainCommitGuard
{
    private const double NumericEpsilon = 1e-9;

    public static bool HasMeaningfulNumericChange(double currentValue, double candidateValue)
    {
        if (double.IsNaN(currentValue) || double.IsNaN(candidateValue) ||
            double.IsInfinity(currentValue) || double.IsInfinity(candidateValue))
        {
            return !currentValue.Equals(candidateValue);
        }

        return Math.Abs(candidateValue - currentValue) > NumericEpsilon;
    }
}
