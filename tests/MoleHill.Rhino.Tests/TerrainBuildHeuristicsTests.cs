using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class TerrainBuildHeuristicsTests
{
    [Fact]
    public void ShouldUseLegacyPathTriangulation_FinalBuildWithPersistentHardConstraints_ReturnsFalse()
    {
        bool useLegacy = TerrainBuildHeuristics.ShouldUseLegacyPathTriangulation(
            TerrainBuildMode.Final,
            hasPersistentHardConstraints: true,
            vertexCount: 8_000,
            faceCount: 16_000);

        Assert.False(useLegacy);
    }

    [Fact]
    public void ShouldUseLegacyPathTriangulation_PreviewWithoutPersistentHardConstraints_ReturnsTrue()
    {
        bool useLegacy = TerrainBuildHeuristics.ShouldUseLegacyPathTriangulation(
            TerrainBuildMode.Preview,
            hasPersistentHardConstraints: false,
            vertexCount: 80_000,
            faceCount: 160_000);

        Assert.True(useLegacy);
    }

    [Fact]
    public void ShouldUseLegacyPathTriangulation_FinalDenseMeshWithoutPersistentHardConstraints_ReturnsFalse()
    {
        bool useLegacy = TerrainBuildHeuristics.ShouldUseLegacyPathTriangulation(
            TerrainBuildMode.Final,
            hasPersistentHardConstraints: false,
            vertexCount: 30_000,
            faceCount: 60_000);

        Assert.False(useLegacy);
    }

    [Fact]
    public void GetGradePathCurveSamplingLength_PositiveWidth_ReturnsHalfWidth()
    {
        double samplingLength = TerrainBuildHeuristics.GetGradePathCurveSamplingLength(6.0);

        Assert.Equal(3.0, samplingLength, 6);
    }

    [Fact]
    public void GetGradePathCurveSamplingLength_NonPositiveWidth_ReturnsZero()
    {
        Assert.Equal(0.0, TerrainBuildHeuristics.GetGradePathCurveSamplingLength(0.0), 6);
        Assert.Equal(0.0, TerrainBuildHeuristics.GetGradePathCurveSamplingLength(-2.0), 6);
    }

    [Fact]
    public void GetGradePathGeometryTolerance_CoarseTerrainTolerance_ClampsToPathLocalMaximum()
    {
        double tolerance = TerrainBuildHeuristics.GetGradePathGeometryTolerance(0.25);

        Assert.Equal(2e-3, tolerance, 9);
    }

    [Fact]
    public void GetGradePathGeometryTolerance_TinyTerrainTolerance_ClampsToMinimum()
    {
        double tolerance = TerrainBuildHeuristics.GetGradePathGeometryTolerance(1e-9);

        Assert.Equal(1e-6, tolerance, 12);
    }

    [Fact]
    public void ShouldPreferLocalizedGradePathRoadEdgeFallback_CoarseMesh_ReturnsTrue()
    {
        bool preferLocalized = TerrainBuildHeuristics.ShouldPreferLocalizedGradePathRoadEdgeFallback(
            vertexCount: 25,
            faceCount: 41);

        Assert.True(preferLocalized);
    }

    [Fact]
    public void ShouldPreferLocalizedGradePathRoadEdgeFallback_DenseMesh_ReturnsFalse()
    {
        bool preferLocalized = TerrainBuildHeuristics.ShouldPreferLocalizedGradePathRoadEdgeFallback(
            vertexCount: 2_500,
            faceCount: 5_000);

        Assert.False(preferLocalized);
    }
}
