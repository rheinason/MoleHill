using MoleHill.Core.Analysis;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class MeshRegularityAnalyzerTests
{
    [Fact]
    public void Analyze_SparseMesh_ReportsVerySparse()
    {
        var summary = MeshRegularityAnalyzer.Analyze(
            new double[] { 0, 0, 0, 10, 0, 0, 0, 10, 0 },
            new[] { 0, 1, 2 });

        Assert.True(MeshRegularityAnalyzer.IsVerySparse(summary));
    }

    [Fact]
    public void Analyze_SkinnyTriangles_ReportsVerySkinnyTriangles()
    {
        var summary = MeshRegularityAnalyzer.Analyze(
            new double[] { 0, 0, 0, 100, 0, 0, 0.01, 0.001, 0 },
            new[] { 0, 1, 2 });

        Assert.True(MeshRegularityAnalyzer.HasVerySkinnyTriangles(summary));
    }

    [Fact]
    public void Analyze_RegularDenseGrid_IsNotSparseOrSkinny()
    {
        const int size = 20;
        var vertices = new double[size * size * 3];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int index = y * size + x;
            vertices[index * 3] = x;
            vertices[index * 3 + 1] = y;
        }

        var faces = new List<int>();
        for (int y = 0; y < size - 1; y++)
        for (int x = 0; x < size - 1; x++)
        {
            int a = y * size + x;
            int b = a + 1;
            int c = a + size;
            int d = c + 1;
            faces.AddRange(new[] { a, b, d, a, d, c });
        }

        var summary = MeshRegularityAnalyzer.Analyze(vertices, faces.ToArray());

        Assert.False(MeshRegularityAnalyzer.IsVerySparse(summary));
        Assert.False(MeshRegularityAnalyzer.HasVerySkinnyTriangles(summary));
    }
}
