namespace MoleHill.Rhino.Services;

internal static class TerrainBuildHeuristics
{
    private const int MaxLegacyPathTriangulationVertices = 25_000;
    private const int MaxLegacyPathTriangulationFaces = 50_000;
    private const double GradePathCurveSamplingWidthFactor = 0.5;
    private const int MaxLocalizedGradePathRoadEdgeFallbackVertices = 300;
    private const int MaxLocalizedGradePathRoadEdgeFallbackFaces = 500;
    private const int MinSplitKeepPreferredFaces = 10_000;

    public static bool ShouldUseLegacyPathTriangulation(
        TerrainBuildMode mode,
        bool hasPersistentHardConstraints,
        int vertexCount,
        int faceCount)
    {
        return mode == TerrainBuildMode.Preview ||
               (!hasPersistentHardConstraints &&
                vertexCount <= MaxLegacyPathTriangulationVertices &&
                faceCount <= MaxLegacyPathTriangulationFaces);
    }

    public static double GetGradePathCurveSamplingLength(double width)
    {
        return width > 0.0 ? width * GradePathCurveSamplingWidthFactor : 0.0;
    }

    public static double GetGradePathGeometryTolerance(double terrainTolerance)
    {
        return GetGradePathGeometryTolerance(terrainTolerance, global::Rhino.UnitSystem.Meters);
    }

    public static double GetGradePathGeometryTolerance(double terrainTolerance, global::Rhino.UnitSystem unitSystem)
    {
        return TerrainTolerancePolicy.Create(terrainTolerance, 1e-9, unitSystem).GradePathTolerance;
    }

    public static bool ShouldPreferLocalizedGradePathRoadEdgeFallback(
        int vertexCount,
        int faceCount)
    {
        return vertexCount <= MaxLocalizedGradePathRoadEdgeFallbackVertices ||
               faceCount <= MaxLocalizedGradePathRoadEdgeFallbackFaces;
    }

    public static bool ShouldPreferSplitKeepGradePath(
        TerrainBuildMode mode,
        bool hasPersistentHardConstraints,
        int faceCount)
    {
        return mode == TerrainBuildMode.Final &&
               hasPersistentHardConstraints &&
               faceCount >= MinSplitKeepPreferredFaces;
    }
}
