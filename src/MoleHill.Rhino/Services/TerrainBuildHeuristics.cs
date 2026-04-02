namespace MoleHill.Rhino.Services;

internal static class TerrainBuildHeuristics
{
    private const int MaxLegacyPathTriangulationVertices = 25_000;
    private const int MaxLegacyPathTriangulationFaces = 50_000;
    private const double GradePathCurveSamplingWidthFactor = 0.5;

    public static bool ShouldUseLegacyPathTriangulation(
        TerrainBuildMode mode,
        bool hasPersistentHardConstraints,
        int vertexCount,
        int faceCount)
    {
        // Shoulder/guide constraints are now clipped at barriers before triangulation,
        // so the legacy Z-only path is safe even when persistent hard constraints exist.
        _ = hasPersistentHardConstraints;
        return mode == TerrainBuildMode.Preview ||
               (vertexCount <= MaxLegacyPathTriangulationVertices &&
                faceCount <= MaxLegacyPathTriangulationFaces);
    }

    public static double GetGradePathCurveSamplingLength(double width)
    {
        return width > 0.0 ? width * GradePathCurveSamplingWidthFactor : 0.0;
    }
}
