using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using MoleHill.Shared;
using Rhino;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Rhino.Tests;

public sealed class ReferenceComparisonCacheTests
{
    [Fact]
    public void ReferenceComparisonCacheKey_DifferentCurrentArrays_AreDifferentEntries()
    {
        var firstVertices = new[] { 0.0, 0.0, 1.0 };
        var firstFaces = new[] { 0, 0, 0 };
        var secondVertices = (double[])firstVertices.Clone();
        var secondFaces = (int[])firstFaces.Clone();

        var first = new TerrainBuildService.ReferenceComparisonCacheKey(
            firstVertices, firstFaces, 1, 2, 3, false);
        var sameGeometryInstance = new TerrainBuildService.ReferenceComparisonCacheKey(
            firstVertices, firstFaces, 1, 2, 3, false);
        var differentGeometryInstance = new TerrainBuildService.ReferenceComparisonCacheKey(
            secondVertices, secondFaces, 1, 2, 3, false);

        Assert.Equal(first, sameGeometryInstance);
        Assert.NotEqual(first, differentGeometryInstance);
    }

    [Fact]
    public void ReferenceProjectionCacheKey_DependsOnlyOnTheReference()
    {
        // The projection cache is shared across the whole build - terrain analyses and every zone
        // piece - so its key must not carry anything about the current geometry, and must still
        // separate different references.
        var first = new TerrainBuildService.ReferenceProjectionCacheKey(11, 22, false);
        var same = new TerrainBuildService.ReferenceProjectionCacheKey(11, 22, false);
        var differentReference = new TerrainBuildService.ReferenceProjectionCacheKey(12, 22, false);
        var differentReferenceTerrain = new TerrainBuildService.ReferenceProjectionCacheKey(11, 23, false);
        var estimated = new TerrainBuildService.ReferenceProjectionCacheKey(11, 22, true);

        Assert.Equal(first, same);
        Assert.NotEqual(first, differentReference);
        Assert.NotEqual(first, differentReferenceTerrain);
        Assert.NotEqual(first, estimated);
    }

    [Fact]
    public void ReferenceComparisonCacheKey_SameReferenceDifferentCurrentGeometry_StaysSeparate()
    {
        // The statistics cache is the one that must stay per current mesh: sharing one projector must
        // never let two zone pieces share a volume.
        var reference = new TerrainBuildService.ReferenceProjectionCacheKey(11, 22, false);
        var pieceA = new TerrainBuildService.ReferenceComparisonCacheKey(
            new[] { 0.0 }, new[] { 0 }, reference.ReferenceFingerprint, 0, reference.ReferenceTerrainFingerprint, false);
        var pieceB = new TerrainBuildService.ReferenceComparisonCacheKey(
            new[] { 0.0 }, new[] { 0 }, reference.ReferenceFingerprint, 0, reference.ReferenceTerrainFingerprint, false);

        Assert.NotEqual(pieceA, pieceB);
    }

    [RhinoNativeFact]
    public void ComputeReferenceComparisonStats_DifferentCurrentMeshes_SharesProjectionButNotStatistics()
    {
        using var referenceMesh = CreateReferenceMesh();
        using var lowMesh = CreateTriangleMesh(0.0, 1.0);
        using var highMesh = CreateTriangleMesh(2.0, 3.0);
        var snapshot = new TerrainBuildSnapshot
        {
            Terrain = new TerrainDefinition(),
            ModelAbsoluteTolerance = 0.001,
            ModelUnitSystem = UnitSystem.Meters
        };
        var reference = new SourceReferenceSet();
        var boundary = new SourceReferenceSet();
        var statisticsCache = new Dictionary<
            TerrainBuildService.ReferenceComparisonCacheKey,
            TerrainBuildService.ReferenceComparisonStats>();
        var projectionCache = new Dictionary<
            TerrainBuildService.ReferenceProjectionCacheKey,
            TerrainBuildService.ReferenceProjectionContext>();
        var build = new TerrainBuildResult();

        TerrainBuildService.ReferenceComparisonStats low = Calculate(lowMesh);
        TerrainBuildService.ReferenceComparisonStats high = Calculate(highMesh);

        Assert.Equal(0.5, low.FillVolume, 8);
        Assert.Equal(1.5, high.FillVolume, 8);
        Assert.Equal(2, statisticsCache.Count);
        Assert.Single(projectionCache);
        Assert.Equal(1, low.GridProjectionCount);
        Assert.Equal(1, high.GridProjectionCount);

        TerrainBuildService.ReferenceComparisonStats Calculate(Mesh currentMesh)
        {
            Assert.True(RhinoGeometryConversions.TryExtractMeshData(
                currentMesh,
                out double[] vertices,
                out int vertexCount,
                out int[] faces,
                out int faceCount,
                out string? error), error);
            Assert.Equal(3, vertexCount);
            Assert.Equal(1, faceCount);

            return TerrainBuildService.ComputeReferenceComparisonStats(
                snapshot,
                referenceMesh,
                vertices,
                faces,
                reference,
                boundary,
                build,
                statisticsCache,
                projectionCache,
                shouldCancel: null);
        }
    }

    private static Mesh CreateReferenceMesh()
    {
        var mesh = new Mesh();
        mesh.Vertices.Add(0, 0, 0);
        mesh.Vertices.Add(4, 0, 0);
        mesh.Vertices.Add(4, 1, 0);
        mesh.Vertices.Add(0, 1, 0);
        mesh.Faces.AddFace(0, 1, 2);
        mesh.Faces.AddFace(0, 2, 3);
        mesh.Normals.ComputeNormals();
        return mesh;
    }

    private static Mesh CreateTriangleMesh(double x, double z)
    {
        var mesh = new Mesh();
        mesh.Vertices.Add(x, 0, z);
        mesh.Vertices.Add(x + 1, 0, z);
        mesh.Vertices.Add(x, 1, z);
        mesh.Faces.AddFace(0, 1, 2);
        mesh.Normals.ComputeNormals();
        return mesh;
    }
}
