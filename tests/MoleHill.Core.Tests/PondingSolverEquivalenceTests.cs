using MoleHill.Core.Analysis;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// The per-basin <see cref="PondingSolver"/> must report exactly what the whole-terrain version did — same
/// ponds, same order, and bit-identical levels, volumes and shoreline coordinates. A speed-up that moves a
/// shoreline by one ulp is still a change to what gets baked, so the comparison is exact, not toleranced.
/// </summary>
public class PondingSolverEquivalenceTests
{
    public static TheoryData<int, double, double, double> Terrains => new()
    {
        // side, noise cell, noise amplitude, minimum depth
        { 60, 2.5, 0.4, 0.05 },   // hundreds of small depressions
        { 60, 1.0, 0.15, 0.05 },  // near-white noise: many one-face dimples
        { 200, 7.0, 0.4, 0.05 },  // fewer, larger basins
        { 60, 2.5, 0.4, 0.0 },    // every sink reported, however shallow
        { 200, 12.0, 1.5, 0.05 }, // a few deep ponds
    };

    [Theory]
    [MemberData(nameof(Terrains))]
    public void Solve_MatchesTheWholeTerrainReference(int side, double noiseCell, double noiseAmplitude, double minimumDepth)
    {
        DrainageTestTerrain.Mesh mesh = DrainageTestTerrain.Create(side, noiseCell, noiseAmplitude);
        BasinGraph graph = DrainageBasinAnalyzer.Analyze(mesh.Vertices, mesh.VertexCount, mesh.Faces, mesh.FaceCount);
        var options = new PondingSolver.Options { MinimumDepth = minimumDepth, Tolerance = 1e-6 };

        IReadOnlyList<PondingSolver.Pond> expected = PondingSolverReference.Solve(graph, mesh.Vertices, mesh.VertexCount, mesh.Faces, options);
        IReadOnlyList<PondingSolver.Pond> actual = PondingSolver.Solve(graph, mesh.Vertices, mesh.VertexCount, mesh.Faces, options);

        Assert.True(expected.Count > 0, "The fixture must produce ponds, or the comparison proves nothing.");
        AssertSamePonds(expected, actual);
    }

    [Fact]
    public void Solve_BundedPad_MatchesTheWholeTerrainReference()
    {
        // The case TraceShoreline's sign convention exists for: a flat floor wholly at or below its rim.
        DrainageTestTerrain.Mesh mesh = DrainageTestTerrain.Create(40, 7.0, 0.0);
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            double x = mesh.Vertices[v * 3];
            double y = mesh.Vertices[(v * 3) + 1];
            bool inside = x >= 12 && x <= 26 && y >= 12 && y <= 26;
            bool bund = !inside && x >= 10 && x <= 28 && y >= 10 && y <= 28;
            if (inside)
                mesh.Vertices[(v * 3) + 2] = 1.0;
            else if (bund)
                mesh.Vertices[(v * 3) + 2] = 1.5;
        }

        BasinGraph graph = DrainageBasinAnalyzer.Analyze(mesh.Vertices, mesh.VertexCount, mesh.Faces, mesh.FaceCount);
        IReadOnlyList<PondingSolver.Pond> expected = PondingSolverReference.Solve(graph, mesh.Vertices, mesh.VertexCount, mesh.Faces);
        IReadOnlyList<PondingSolver.Pond> actual = PondingSolver.Solve(graph, mesh.Vertices, mesh.VertexCount, mesh.Faces);

        Assert.NotEmpty(expected);
        AssertSamePonds(expected, actual);
    }

    private static void AssertSamePonds(IReadOnlyList<PondingSolver.Pond> expected, IReadOnlyList<PondingSolver.Pond> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            PondingSolver.Pond e = expected[i];
            PondingSolver.Pond a = actual[i];
            Assert.Equal(e.BasinIndex, a.BasinIndex);
            Assert.Equal(e.SpillZ, a.SpillZ);
            Assert.Equal(e.FloorZ, a.FloorZ);
            Assert.Equal(e.Volume, a.Volume);
            Assert.Equal(e.PlanArea, a.PlanArea);
            Assert.Equal(e.SpillX, a.SpillX);
            Assert.Equal(e.SpillY, a.SpillY);
            Assert.Equal(e.SpillsOffTerrain, a.SpillsOffTerrain);
            Assert.Equal(e.Outlines.Count, a.Outlines.Count);
            for (int loop = 0; loop < e.Outlines.Count; loop++)
                Assert.Equal(e.Outlines[loop], a.Outlines[loop]);
        }
    }
}
