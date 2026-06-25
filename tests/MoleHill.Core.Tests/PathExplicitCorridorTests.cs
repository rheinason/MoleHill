using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

public class PathExplicitCorridorTests
{
    private static (double[] v, int vc, int[] f, int fc) FlatTerrain(double z)
    {
        double[] v = { -60, -60, z, 60, -60, z, 60, 60, z, -60, 60, z };
        int[] f = { 0, 1, 2, 0, 2, 3 };
        return (v, 4, f, 2);
    }

    [Fact]
    public void Grade_StraightRoadBelowFlatTerrain_UsesExplicitCorridorAndIsWatertight()
    {
        var terrain = FlatTerrain(10.0);

        // Straight road at z=0 (10 below terrain), width 4, 45° batters → daylight reach ~10 each side.
        var paths = new[]
        {
            new PathGrader.PathDefinition(
                xyVertices: new[] { -20.0, 0.0, 20.0, 0.0 },
                zValues: new[] { 0.0, 0.0 },
                vertexCount: 2,
                width: 4.0,
                slopeAngleDeg: 45.0)
        };

        GradingResult? result = PathGrader.Grade(
            terrain.v, terrain.vc, terrain.f, terrain.fc, paths, out string? errorMessage);

        Assert.True(result != null, errorMessage);
        string diagnostics = string.Join(Environment.NewLine, result!.Diagnostics);
        Assert.Contains("explicit corridor", diagnostics, StringComparison.OrdinalIgnoreCase);

        MeshTopologyValidator.BoundaryGraphAnalysis topology =
            MeshTopologyValidator.AnalyzeBoundaryGraph(result.Faces, result.FaceCount);
        Assert.Equal(0, topology.NonManifoldEdgeCount);
        Assert.False(topology.HasOpenBoundaryChains);

        // Road surface: any face fully inside the corridor (|y|<=2, -20<=x<=20) must sit at z=0.
        for (int fi = 0; fi < result.FaceCount; fi++)
        {
            int a = result.Faces[fi * 3], b = result.Faces[fi * 3 + 1], c = result.Faces[fi * 3 + 2];
            if (InsideCorridor(result.Vertices, a) && InsideCorridor(result.Vertices, b) && InsideCorridor(result.Vertices, c))
            {
                Assert.True(Math.Abs(result.Vertices[a * 3 + 2]) < 1e-3, "Road vertex not at road elevation 0.");
                Assert.True(Math.Abs(result.Vertices[b * 3 + 2]) < 1e-3, "Road vertex not at road elevation 0.");
                Assert.True(Math.Abs(result.Vertices[c * 3 + 2]) < 1e-3, "Road vertex not at road elevation 0.");
            }
        }
    }

    private static bool InsideCorridor(double[] v, int i)
    {
        double x = v[i * 3];
        double y = v[i * 3 + 1];
        return x > -19.5 && x < 19.5 && y > -1.5 && y < 1.5;
    }

    // A curved road produces a non-convex daylight envelope (the inside-of-curve reach is shorter than
    // the outside). Triangle.NET fills the convex hull, so the explicit corridor fill must drop the
    // hull-skirt faces; otherwise they weld non-manifold and the explicit tier silently defers. This
    // asserts the explicit corridor tier is used AND the result is watertight on a curved road.
    [Fact]
    public void Grade_CurvedRoadBelowFlatTerrain_UsesExplicitCorridorAndIsWatertight()
    {
        var terrain = FlatTerrain(10.0);

        // Quarter-circle arc road (radius 20) at z=0 → annular-sector daylight (concave inner arc).
        const int n = 9;
        var xy = new double[n * 2];
        var z = new double[n];
        for (int i = 0; i < n; i++)
        {
            double t = (Math.PI / 2) * i / (n - 1);
            xy[i * 2] = 20.0 * Math.Cos(t) - 10.0;
            xy[i * 2 + 1] = 20.0 * Math.Sin(t) - 10.0;
            z[i] = 0.0;
        }

        var paths = new[]
        {
            new PathGrader.PathDefinition(xy, z, n, width: 4.0, slopeAngleDeg: 45.0)
        };

        GradingResult? result = PathGrader.Grade(
            terrain.v, terrain.vc, terrain.f, terrain.fc, paths, out string? errorMessage);

        Assert.True(result != null, errorMessage);
        string diagnostics = string.Join(Environment.NewLine, result!.Diagnostics);
        Assert.Contains("explicit corridor", diagnostics, StringComparison.OrdinalIgnoreCase);

        MeshTopologyValidator.BoundaryGraphAnalysis topology =
            MeshTopologyValidator.AnalyzeBoundaryGraph(result.Faces, result.FaceCount);
        Assert.Equal(0, topology.NonManifoldEdgeCount);
        Assert.False(topology.HasOpenBoundaryChains);
    }
}
