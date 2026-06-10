using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace MoleHill.Core.Tests;

internal static class PadDiagnosticAssert
{
    public static double ExtractMaxBatterSlope(string diagnostics)
    {
        double maxSlope = 0.0;
        foreach (Match match in Regex.Matches(
                     diagnostics,
                     @"batter slope check: target=[0-9,.+-]+ deg, faces=[0-9]+, min=[0-9,.+-]+, avg=[0-9,.+-]+, max=([0-9,.+-]+),",
                     RegexOptions.IgnoreCase))
        {
            string value = match.Groups[1].Value.Replace(',', '.');
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                maxSlope = Math.Max(maxSlope, parsed);
        }

        return maxSlope;
    }

    public static void AssertNoFallback(string diagnostics)
    {
        Assert.DoesNotContain("Constraints could not be enforced", diagnostics, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fallback used", diagnostics, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("using split local patch", diagnostics, StringComparison.OrdinalIgnoreCase);
    }
}
