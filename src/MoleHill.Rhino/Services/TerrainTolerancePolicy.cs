using Rhino;

namespace MoleHill.Rhino.Services;

internal static class TerrainTolerancePolicy
{
    private const double DefaultDetailSizeMeters = 0.25;
    private const double LegacyToleranceMigrationThresholdMeters = 0.01;
    private const double MinGradePathToleranceMeters = 1e-6;
    private const double MaxGradePathToleranceMeters = 2e-3;

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

    public static double LegacyToleranceMigrationThreshold(UnitSystem unitSystem)
    {
        return ModelUnits.FromMeters(LegacyToleranceMigrationThresholdMeters, unitSystem);
    }

    public static bool ShouldPromoteLegacyDetailSize(double value, UnitSystem unitSystem)
    {
        return value <= 0.0 || value <= LegacyToleranceMigrationThreshold(unitSystem);
    }

    public static Profile Create(double detailSize, double documentTolerance, UnitSystem unitSystem)
    {
        double resolvedDetailSize = detailSize > 0.0
            ? detailSize
            : DefaultDetailSize(unitSystem);
        double resolvedDocumentTolerance = Math.Max(documentTolerance, 1e-9);

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
                ModelUnits.FromMeters(MinGradePathToleranceMeters, unitSystem),
                ModelUnits.FromMeters(MaxGradePathToleranceMeters, unitSystem)),
            GradePadTolerance: ClampWithFloor(
                resolvedDetailSize * 0.02,
                resolvedDocumentTolerance,
                resolvedDetailSize * 0.10));
    }

    private static double ClampWithFloor(double value, double floor, double ceiling)
    {
        double resolvedFloor = Math.Max(floor, 1e-9);
        double resolvedCeiling = Math.Max(ceiling, resolvedFloor);
        return Math.Clamp(value, resolvedFloor, resolvedCeiling);
    }
}
