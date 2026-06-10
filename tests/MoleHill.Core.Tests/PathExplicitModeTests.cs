using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// Locks in that a straightforward road on realistic terrain is graded by the explicit corridor
/// engine (ruled road surface + welded side batters), not the local-insertion fallback. Guards
/// against a silent regression that lets the preferred path quietly defer to the fallback.
/// </summary>
public class PathExplicitModeTests
{
    // A sloped grid terrain: z rises with y, dense enough for the explicit corridor to carve and weld.
    private static (double[] v, int vc, int[] f, int fc) SlopedGrid(int n, double step, double zPerUnitY)
    {
        var xy = new List<double>();
        var z = new List<double>();
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                double x = i * step;
                double y = j * step;
                xy.Add(x); xy.Add(y);
                z.Add(y * zPerUnitY);
            }
        }

        var outcome = TriangulationHelper.Triangulate(xy, z.Count, new List<(int, int)>(), 0, 0, convex: false, 0);
        var ex = TriangleNetExtractor.Extract(outcome.Mesh!);
        var v = new double[ex.VertexCount * 3];
        for (int i = 0; i < ex.VertexCount; i++)
        {
            v[i * 3] = ex.Xy[i * 2];
            v[i * 3 + 1] = ex.Xy[i * 2 + 1];
            int src = ex.SourceIds[i];
            v[i * 3 + 2] = src >= 0 && src < z.Count ? z[src] : 0.0;
        }

        return (v, ex.VertexCount, ex.Faces, ex.FaceCount);
    }

    [Fact]
    public void Grade_StraightRoadOnSlopedTerrain_UsesExplicitCorridor()
    {
        var t = SlopedGrid(n: 11, step: 10.0, zPerUnitY: 0.1); // 100x100, z 0..10

        // A road running across the slope at mid-height, level profile, so both shoulders daylight.
        var path = new PathGrader.PathDefinition(
            xyVertices: new[] { 20.0, 50.0, 80.0, 50.0 },
            zValues: new[] { 5.0, 5.0 },
            vertexCount: 2,
            width: 8.0,
            slopeAngleDeg: 33.0,
            maxDistance: 0.0);

        GradingResult? result = PathGrader.Grade(
            t.v, t.vc, t.f, t.fc, new[] { path }, out string? errorMessage);

        Assert.NotNull(result);
        Assert.True(string.IsNullOrWhiteSpace(errorMessage) || !errorMessage.Contains("failed", StringComparison.OrdinalIgnoreCase), errorMessage);
        string diagnostics = string.Join(Environment.NewLine, result!.Diagnostics);
        Assert.Contains("explicit corridor construction", diagnostics, StringComparison.OrdinalIgnoreCase);
        PadInvariantAssert.AssertWatertightManifold(result, diagnostics);
    }
}
