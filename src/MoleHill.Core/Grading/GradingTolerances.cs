namespace MoleHill.Core.Grading;

internal static class GradingTolerances
{
    internal const double DefaultModelTolerance = 1e-3;

    internal static double ModelToleranceOrDefault(double tolerance)
    {
        return double.IsFinite(tolerance) && tolerance > 0.0
            ? tolerance
            : DefaultModelTolerance;
    }

    internal static double AtGradeZTolerance(double modelTolerance)
    {
        return Math.Max(ModelToleranceOrDefault(modelTolerance), 1e-12);
    }

    internal static double VertexAdjustmentZTolerance(double modelTolerance)
    {
        return Math.Max(ModelToleranceOrDefault(modelTolerance), 1e-12);
    }

    internal static double ConstraintSnapTolerance(double modelTolerance)
    {
        return Math.Max(ModelToleranceOrDefault(modelTolerance) * 10.0, 1e-12);
    }
}
