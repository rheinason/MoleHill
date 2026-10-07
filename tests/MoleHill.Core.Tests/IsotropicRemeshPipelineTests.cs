using MoleHill.Core.Engine;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// The isotropic Remesh both hosts run. The Rhino card and the Grasshopper component call this one sequence;
/// these pin that it is exactly the remesher plus its hand-off clean-up, and that the card's own breaklines
/// are inserted before the remesh pins them.
/// </summary>
public class IsotropicRemeshPipelineTests
{
    private static IndexedTriMesh Terrain() => IndexedTriMesh.FromArrays(
        TestMeshes.GridVertices(25, 25, 1.0, (x, y) => (0.05 * x) + (0.5 * Math.Sin(y * 0.4))),
        TestMeshes.GridFaces(25, 25));

    [Fact]
    public void Run_WithoutBreaklines_EqualsTheTiledRemeshAndItsCollapse()
    {
        IndexedTriMesh terrain = Terrain();
        var options = new IsotropicRemesher.Options
        {
            TargetEdgeLength = 1.5,
            Tolerance = 1e-4,
            WallFaceMinSlopeDeg = 70.0,
            Iterations = 5
        };
        IsotropicRemesher.Result direct = TiledIsotropicRemesher.Remesh(
            terrain.Vertices, terrain.Faces, Array.Empty<ConstraintPolyline>(), options, 0.0, null, out _);
        (double[] expectedVertices, int[] expectedFaces) = FloatCoincidentEdgeCollapser.Collapse(direct.Vertices, direct.Faces, out _, out _);

        IsotropicRemeshPipeline.Outcome outcome = IsotropicRemeshPipeline.Run(new IsotropicRemeshPipeline.Request
        {
            Terrain = terrain,
            EdgeLength = 1.5,
            Tolerance = 1e-4
        });

        Assert.True(outcome.Success, outcome.Remesh?.Warning);
        Assert.Equal(1.5, outcome.Target);
        Assert.NotNull(outcome.Memo);
        Assert.Equal(expectedVertices, outcome.Vertices);
        Assert.Equal(expectedFaces, outcome.Faces);
    }

    [Fact]
    public void Run_InsertsItsBreaklineAndKeepsItAsEdges()
    {
        // A diagonal across the grid's own diagonals: not an existing edge, so it only survives if inserted.
        var breakline = new ConstraintPolyline(new[] { 3.3, 4.1, 0.0, 20.7, 18.9, 0.0 }, 2, false);

        IsotropicRemeshPipeline.Outcome outcome = IsotropicRemeshPipeline.Run(new IsotropicRemeshPipeline.Request
        {
            Terrain = Terrain(),
            InsertedConstraints = new[] { breakline },
            PinnedConstraints = new[] { breakline },
            EdgeLength = 1.5,
            Tolerance = 1e-4
        });

        Assert.True(outcome.Success, outcome.InsertError ?? outcome.Remesh?.Warning);
        Assert.Contains(VertexIndexNear(outcome.Vertices, 3.3, 4.1), Enumerable.Range(0, outcome.Vertices.Length / 3));
        Assert.Contains(VertexIndexNear(outcome.Vertices, 20.7, 18.9), Enumerable.Range(0, outcome.Vertices.Length / 3));
        Assert.True(BreaklineCoveredByEdges(outcome.Vertices, outcome.Faces, 3.3, 4.1, 20.7, 18.9),
            "the breakline should run along mesh edges after the remesh");
    }

    [Fact]
    public void Run_AutomaticTargetOnTiles_IsSnappedToTheRoundedStep()
    {
        IndexedTriMesh terrain = Terrain();

        IsotropicRemeshPipeline.Outcome outcome = IsotropicRemeshPipeline.Run(new IsotropicRemeshPipeline.Request
        {
            Terrain = terrain,
            Tolerance = 1e-4
        });

        double expected = TiledIsotropicRemesher.RoundedTarget(
            IsotropicRemesher.EstimateFaceCountPreservingTarget(terrain.Vertices, terrain.Faces));
        Assert.True(outcome.Success, outcome.Remesh?.Warning);
        Assert.Equal(expected, outcome.Target);
    }

    private static int VertexIndexNear(double[] vertices, double x, double y)
    {
        for (int i = 0; i < vertices.Length / 3; i++)
        {
            if (Math.Abs(vertices[i * 3] - x) < 1e-6 && Math.Abs(vertices[(i * 3) + 1] - y) < 1e-6)
                return i;
        }

        return -1;
    }

    /// <summary>True when the plan segment a-b is a chain of mesh edges whose vertices all lie on it.</summary>
    private static bool BreaklineCoveredByEdges(double[] vertices, int[] faces, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax, dy = by - ay, length = Math.Sqrt((dx * dx) + (dy * dy));
        bool OnLine(int v)
        {
            double px = vertices[v * 3] - ax, py = vertices[(v * 3) + 1] - ay;
            double t = ((px * dx) + (py * dy)) / (length * length);
            return t >= -1e-9 && t <= 1 + 1e-9 && Math.Abs((px * dy) - (py * dx)) / length < 1e-6;
        }

        var edges = new HashSet<(int, int)>();
        for (int f = 0; f < faces.Length / 3; f++)
        {
            for (int k = 0; k < 3; k++)
            {
                int u = faces[(f * 3) + k], w = faces[(f * 3) + ((k + 1) % 3)];
                if (OnLine(u) && OnLine(w))
                    edges.Add(u < w ? (u, w) : (w, u));
            }
        }

        double covered = 0.0;
        foreach ((int u, int w) in edges)
        {
            double ex = vertices[w * 3] - vertices[u * 3], ey = vertices[(w * 3) + 1] - vertices[(u * 3) + 1];
            covered += Math.Sqrt((ex * ex) + (ey * ey));
        }

        return Math.Abs(covered - length) < 1e-6;
    }
}
