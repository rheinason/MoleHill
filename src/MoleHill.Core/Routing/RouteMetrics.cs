namespace MoleHill.Core.Routing;

/// <summary>
/// The readout of a route's polyline against the ground. Grades are rise over run, measured on the
/// polyline's own segments; volumes are profile-minus-ground over the corridor width, as a flat prism.
/// </summary>
public readonly record struct RouteMetrics(
    double PlanLength,
    double MaxGrade,
    double MeanGrade,
    double CutVolume,
    double FillVolume,
    double MaxCutDepth,
    double MaxFillDepth)
{
    /// <summary>
    /// Measures a polyline against the ground. Grades come from the segments; earthworks are integrated
    /// at stations no further apart than <paramref name="sampleSpacing"/>, so a long straight segment
    /// over rolling ground is measured along its length, not at its ends.
    /// </summary>
    public static RouteMetrics Measure(
        IReadOnlyList<double> pointsXyz,
        Func<double, double, double?> ground,
        double corridorWidth,
        double sampleSpacing)
    {
        int count = pointsXyz.Count / 3;
        double length = 0.0, rise = 0.0, maxGrade = 0.0;
        double cut = 0.0, fill = 0.0, maxCut = 0.0, maxFill = 0.0;
        double spacing = sampleSpacing > 0.0 ? sampleSpacing : double.PositiveInfinity;

        for (int i = 1; i < count; i++)
        {
            double ax = pointsXyz[(i - 1) * 3], ay = pointsXyz[((i - 1) * 3) + 1], az = pointsXyz[((i - 1) * 3) + 2];
            double bx = pointsXyz[i * 3], by = pointsXyz[(i * 3) + 1], bz = pointsXyz[(i * 3) + 2];
            double plan = Math.Sqrt(((bx - ax) * (bx - ax)) + ((by - ay) * (by - ay)));
            if (plan <= 0.0)
                continue;

            double dz = Math.Abs(bz - az);
            length += plan;
            rise += dz;
            maxGrade = Math.Max(maxGrade, dz / plan);

            int pieces = Math.Max(1, (int)Math.Ceiling(plan / spacing));
            double piece = plan / pieces;
            for (int k = 0; k < pieces; k++)
            {
                double t = (k + 0.5) / pieces;
                double? groundZ = ground(ax + ((bx - ax) * t), ay + ((by - ay) * t));
                if (groundZ is not double g)
                    continue;

                double depth = (az + ((bz - az) * t)) - g;
                if (depth > 0.0)
                {
                    fill += depth * piece;
                    maxFill = Math.Max(maxFill, depth);
                }
                else
                {
                    cut -= depth * piece;
                    maxCut = Math.Max(maxCut, -depth);
                }
            }
        }

        return new RouteMetrics(
            length,
            maxGrade,
            length > 0.0 ? rise / length : 0.0,
            cut * corridorWidth,
            fill * corridorWidth,
            maxCut,
            maxFill);
    }
}
