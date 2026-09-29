namespace MoleHill.Core.Engine;

/// <summary>
/// Detects terrains too far from the world origin for the build to keep their detail.
/// </summary>
/// <remarks>
/// Each build stage hands its mesh to the next as a Rhino mesh, whose vertices are single precision, so a
/// coordinate is rounded to float32 at every hand-off. The rounding step grows with distance from the
/// origin — about 0.001 m at 8 km and 0.5 m at a UTM northing of 6,000 km — and once it approaches the
/// model tolerance, points on a line drift off it, near-coincident vertices merge or split, and thin
/// faces appear. Moving the project to the origin (<c>mhOrientToOrigin</c>) removes the problem.
/// </remarks>
public static class CoordinatePrecision
{
    /// <summary>Float32 spacing at magnitude m is m · 2^-23.</summary>
    private const double Float32RelativeStep = 1.0 / 8_388_608.0;

    /// <summary>Warn once the float32 step reaches this fraction of the model tolerance.</summary>
    private const double ToleranceFraction = 0.1;

    /// <summary>Largest plan distance from the origin (|x| or |y|) in flat XY coordinates.</summary>
    public static double MaxPlanMagnitude(double[] xyCoords, int vertexCount)
    {
        double max = 0.0;
        int count = Math.Min(vertexCount, xyCoords.Length / 2);
        for (int i = 0; i < count; i++)
        {
            double x = Math.Abs(xyCoords[i * 2]);
            double y = Math.Abs(xyCoords[i * 2 + 1]);
            if (double.IsFinite(x) && x > max)
                max = x;
            if (double.IsFinite(y) && y > max)
                max = y;
        }

        return max;
    }

    /// <summary>
    /// Distance from the origin beyond which single-precision rounding reaches a tenth of
    /// <paramref name="modelTolerance"/>.
    /// </summary>
    public static double SafeDistance(double modelTolerance) =>
        modelTolerance > 0 ? modelTolerance * ToleranceFraction / Float32RelativeStep : double.PositiveInfinity;

    /// <summary>True when <paramref name="magnitude"/> is beyond <see cref="SafeDistance"/>.</summary>
    public static bool IsTooFarFromOrigin(double magnitude, double modelTolerance) =>
        magnitude > SafeDistance(modelTolerance);

    /// <summary>The single-precision rounding step at <paramref name="magnitude"/>.</summary>
    public static double RoundingStep(double magnitude) => magnitude * Float32RelativeStep;
}
