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
    public void GetGradePathGeometryTolerance_DefaultMillimeterDetailSize_ClampsToConvertedMaximum()
    {
        double tolerance = TerrainBuildHeuristics.GetGradePathGeometryTolerance(250.0, global::Rhino.UnitSystem.Millimeters);

        Assert.Equal(2.0, tolerance, 9);
    }

    [Fact]
    public void GetGradePathGeometryTolerance_TinyTerrainTolerance_ClampsToMinimum()
    {
        double tolerance = TerrainBuildHeuristics.GetGradePathGeometryTolerance(1e-9);

        Assert.Equal(1e-6, tolerance, 12);
    }

    [Fact]
    public void DefaultDetailSize_Millimeters_ConvertsQuarterMeter()
    {
        double detailSize = TerrainTolerancePolicy.DefaultDetailSize(global::Rhino.UnitSystem.Millimeters);

        Assert.Equal(250.0, detailSize, 9);
    }

    [Theory]
    [InlineData(0.0, true)]
    [InlineData(0.01, true)]
    [InlineData(0.011, false)]
    public void ShouldPromoteLegacyDetailSize_MeterValues_UsesCentimeterThreshold(double value, bool expected)
    {
        bool promote = TerrainTolerancePolicy.ShouldPromoteLegacyDetailSize(value, global::Rhino.UnitSystem.Meters);

        Assert.Equal(expected, promote);
    }

    [Fact]
    public void Create_DefaultMeterDetailSize_DerivesExpectedOperationTolerances()
    {
        var profile = TerrainTolerancePolicy.Create(
            detailSize: 0.25,
            documentTolerance: 0.001,
            global::Rhino.UnitSystem.Meters);

        Assert.Equal(0.25, profile.DetailSize, 9);
        Assert.Equal(0.0125, profile.InputMergeTolerance, 9);
        Assert.Equal(0.0125, profile.CurveChordTolerance, 9);
        Assert.Equal(0.005, profile.RemeshConstraintTolerance, 9);
        Assert.Equal(0.002, profile.GradePathTolerance, 9);
        Assert.Equal(0.005, profile.GradePadTolerance, 9);
    }

    [Fact]
    public void RetainingWallTolerance_UsesWallWidthWhenSmallerThanDetailSize()
    {
        var profile = TerrainTolerancePolicy.Create(
            detailSize: 0.25,
            documentTolerance: 0.001,
            global::Rhino.UnitSystem.Meters);

        double tolerance = profile.RetainingWallTolerance(maxWallWidth: 0.04);

        Assert.Equal(0.001, tolerance, 9);
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
