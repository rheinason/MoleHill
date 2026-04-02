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
}
