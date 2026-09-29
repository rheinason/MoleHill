using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// Closed one-sided rails whose batter runs inward on a ring too small for it: the rays cross before they
/// meet the ground, and the surface is a cone. On a terraced ring wall the daylight ring turned inside out
/// beyond what the Clipper union could resolve, and that one loop deferred the whole grade to the
/// constraint-insertion tier, which does not put the rails into the mesh, so the wall stage lost its walls.
/// </summary>
public class OneSidedRailRingTests
{
    private const string TerraceResource = "MoleHill.Core.Tests.TestData.TerraceRingWallRailsCase.json";

    private sealed record CapturedGrade(double[] Xy, double[] Z, int VertexCount, double SlopeAngleDeg, double FillSlopeAngleDeg,
        double MaxDistance, bool IsClosed, double[] Normals);

    private sealed record TerraceCase(string Case, double Tolerance, double[] Vertices, int[] Faces, List<CapturedGrade> Grades);

    /// <summary>The captured terrace: every rail conformed in place, so the wall stage can adopt all four.</summary>
    [Fact]
    public void Grade_TerracedRingWalls_StaysInSplitKeepAndKeepsEveryRail()
    {
        using Stream stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(TerraceResource)
            ?? throw new InvalidOperationException($"Missing {TerraceResource}.");
        TerraceCase capture = System.Text.Json.JsonSerializer.Deserialize<TerraceCase>(stream)!;
        PathGrader.PathDefinition[] grades = capture.Grades.Select(g => new PathGrader.PathDefinition(
            g.Xy, g.Z, g.VertexCount, width: 0.0, slopeAngleDeg: g.SlopeAngleDeg, maxDistance: g.MaxDistance,
            fillSlopeAngleDeg: g.FillSlopeAngleDeg, isClosed: g.IsClosed, outwardNormals: g.Normals)).ToArray();

        GradingResult? result = PathGrader.Grade(
            capture.Vertices, capture.Vertices.Length / 3, capture.Faces, capture.Faces.Length / 3, grades,
            Array.Empty<SurfaceRemesher.ConstraintPolyline>(), out string? warning, capture.Tolerance, false);

        Assert.True(result != null, warning);
        Assert.Contains(result!.Diagnostics, d => d.Contains("terrain conform", StringComparison.Ordinal));
        var topology = MeshTopologyValidator.AnalyzeBoundaryGraph(result.Faces, result.FaceCount);
        Assert.Equal(1, topology.BoundaryComponentCount);
        Assert.Equal(0, topology.NonManifoldEdgeCount);

        var rails = grades.Select(g =>
        {
            var points = new double[g.VertexCount * 3];
            for (int i = 0; i < g.VertexCount; i++)
            {
                points[i * 3] = g.XyVertices[i * 2];
                points[(i * 3) + 1] = g.XyVertices[(i * 2) + 1];
                points[(i * 3) + 2] = g.ZValues[i];
            }

            return new SurfaceRemesher.ConstraintPolyline(points, g.VertexCount, g.IsClosed, PreserveInputElevation: true);
        }).ToList();
        InsertedConstraintTracer.TraceAll(rails, result.Vertices, result.VertexCount, result.Faces, result.FaceCount, 0.01, out int traced);
        Assert.Equal(rails.Count, traced);
    }

    [Fact]
    public void Grade_InwardRingTooSmallToDaylight_StaysInSplitKeepAndKeepsTheRail()
    {
        (double[] vertices, int[] faces) = Grid(40, 40, 1.0);
        const int stations = 32;
        const double cx = 20.0, cy = 20.0, radius = 5.0, height = 4.0;
        var xy = new double[stations * 2];
        var z = new double[stations];
        var normals = new double[stations * 2];
        var rail = new double[stations * 3];
        for (int k = 0; k < stations; k++)
        {
            double a = 2 * Math.PI * k / stations;
            xy[k * 2] = cx + (radius * Math.Cos(a));
            xy[(k * 2) + 1] = cy + (radius * Math.Sin(a));
            z[k] = height;
            normals[k * 2] = -Math.Cos(a); // away from a partner outside the ring: inward
            normals[(k * 2) + 1] = -Math.Sin(a);
            rail[k * 3] = xy[k * 2];
            rail[(k * 3) + 1] = xy[(k * 2) + 1];
            rail[(k * 3) + 2] = height;
        }

        // At 30 degrees a 4 m batter needs 6.9 m to reach the ground; the ring is 5 m across its radius.
        var path = new PathGrader.PathDefinition(xy, z, stations, width: 0.0, slopeAngleDeg: 30.0, isClosed: true, outwardNormals: normals);
        GradingResult? result = PathGrader.Grade(
            vertices, vertices.Length / 3, faces, faces.Length / 3, new[] { path },
            Array.Empty<SurfaceRemesher.ConstraintPolyline>(), out string? warning, 0.001, false);

        Assert.True(result != null, warning);
        Assert.Contains(result!.Diagnostics, d => d.Contains("terrain conform", StringComparison.Ordinal));
        var topology = MeshTopologyValidator.AnalyzeBoundaryGraph(result.Faces, result.FaceCount);
        Assert.Equal(1, topology.BoundaryComponentCount);
        Assert.Equal(0, topology.NonManifoldEdgeCount);

        InsertedConstraintTracer.TraceAll(
            new[] { new SurfaceRemesher.ConstraintPolyline(rail, stations, true, PreserveInputElevation: true) },
            result.Vertices, result.VertexCount, result.Faces, result.FaceCount, 0.01, out int traced);
        Assert.Equal(1, traced);

        // The centre is graded as the cone's tip: well above the ground, below the rail.
        double centre = new TerrainFaceGrid(result.Vertices, result.VertexCount, result.Faces, result.FaceCount).InterpolateZ(cx, cy);
        Assert.InRange(centre, height - (radius * Math.Tan(Math.PI / 6)) - 0.3, height - 0.5);
    }

    private static (double[] vertices, int[] faces) Grid(int nx, int ny, double step)
    {
        var v = new List<double>();
        for (int j = 0; j <= ny; j++)
            for (int i = 0; i <= nx; i++)
                v.AddRange(new[] { i * step, j * step, 0.0 });

        var f = new List<int>();
        for (int j = 0; j < ny; j++)
        {
            for (int i = 0; i < nx; i++)
            {
                int a = (j * (nx + 1)) + i, b = a + 1, c = a + nx + 1, d = c + 1;
                f.AddRange(new[] { a, b, d, a, d, c });
            }
        }

        return (v.ToArray(), f.ToArray());
    }
}
