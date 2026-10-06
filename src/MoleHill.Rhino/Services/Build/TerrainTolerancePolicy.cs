using MoleHill.Shared;
using Rhino;

namespace MoleHill.Rhino.Services;

internal static class TerrainTolerancePolicy
{
    private const double DefaultDetailSizeMeters = 0.25;
    private const double LegacyToleranceMigrationThresholdMeters = 0.01;
    private const double MinGradePathToleranceMeters = 1e-6;
    private const double MaxGradePathToleranceMeters = 2e-3;
    private const double MinNumericalToleranceMeters = 1e-9;

    public readonly record struct Profile(
        double DetailSize,
        double DocumentTolerance,
        double InputMergeTolerance,
        double CurveChordTolerance,
        double CurveSimplifyTolerance,
        double RemeshConstraintTolerance,
        double GradePathTolerance,
        double GradePadTolerance)
    {
        public double RetainingWallTolerance(double maxWallWidth)
        {
            double wallScale = maxWallWidth > 0.0
                ? Math.Min(DetailSize, maxWallWidth)
                : DetailSize;
            return ClampWithFloor(
                wallScale * 0.02,
                DocumentTolerance,
                wallScale * 0.10);
        }
    }

    public static double DefaultDetailSize(UnitSystem unitSystem)
    {
        return ModelUnits.FromMeters(DefaultDetailSizeMeters, unitSystem);
    }

    public static double DefaultDetailSize(ModelUnitContext unitContext) =>
        unitContext.FromMeters(DefaultDetailSizeMeters);

    public static double LegacyToleranceMigrationThreshold(UnitSystem unitSystem)
    {
        return ModelUnits.FromMeters(LegacyToleranceMigrationThresholdMeters, unitSystem);
    }

    public static bool ShouldPromoteLegacyDetailSize(double value, UnitSystem unitSystem)
    {
        return value <= 0.0 || value <= LegacyToleranceMigrationThreshold(unitSystem);
    }

    public static bool ShouldPromoteLegacyDetailSize(double value, ModelUnitContext unitContext) =>
        value <= 0.0 || value <= unitContext.FromMeters(LegacyToleranceMigrationThresholdMeters);

    public static Profile Create(double detailSize, double documentTolerance, UnitSystem unitSystem)
    {
        ModelUnitContext context = ModelUnitContext.FromUnitSystem(unitSystem, documentTolerance);
        if (!context.IsSupported)
            context = ModelUnitContext.FromUnitSystem(UnitSystem.Meters, documentTolerance);
        return Create(detailSize, documentTolerance, context);
    }

    public static Profile Create(
        double detailSize,
        double documentTolerance,
        ModelUnitContext unitContext)
    {
        if (!unitContext.IsSupported)
            throw new InvalidOperationException(ModelUnitGuard.RequiredMessage);

        double resolvedDetailSize = detailSize > 0.0
            ? detailSize
            : DefaultDetailSize(unitContext);
        double numericalFloor = unitContext.FromMeters(MinNumericalToleranceMeters);
        double resolvedDocumentTolerance = Math.Max(documentTolerance, numericalFloor);

        return new Profile(
            resolvedDetailSize,
            resolvedDocumentTolerance,
            InputMergeTolerance: ClampWithFloor(
                resolvedDetailSize * 0.05,
                resolvedDocumentTolerance,
                resolvedDetailSize * 0.25),
            CurveChordTolerance: ClampWithFloor(
                resolvedDetailSize * 0.05,
                resolvedDocumentTolerance,
                resolvedDetailSize),
            CurveSimplifyTolerance: ClampWithFloor(
                resolvedDetailSize * 0.02,
                resolvedDocumentTolerance,
                resolvedDetailSize * 0.10),
            RemeshConstraintTolerance: ClampWithFloor(
                resolvedDetailSize * 0.02,
                resolvedDocumentTolerance,
                resolvedDetailSize * 0.10),
            GradePathTolerance: Math.Clamp(
                resolvedDetailSize * 0.01,
                unitContext.FromMeters(MinGradePathToleranceMeters),
                unitContext.FromMeters(MaxGradePathToleranceMeters)),
            GradePadTolerance: ClampWithFloor(
                resolvedDetailSize * 0.02,
                resolvedDocumentTolerance,
                resolvedDetailSize * 0.10));
    }

    private static double ClampWithFloor(double value, double floor, double ceiling)
    {
        double resolvedFloor = Math.Max(floor, double.Epsilon);
        double resolvedCeiling = Math.Max(ceiling, resolvedFloor);
        return Math.Clamp(value, resolvedFloor, resolvedCeiling);
    }
}
