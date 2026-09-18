using MoleHill.Core.Engine;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// A constraint is pinned along its whole run of mesh edges, not only where one mesh edge spans a whole
/// constraint segment. Upstream stages routinely put vertices partway along a constraint — the retaining
/// wall quality patch refines every rail and breakline it crosses — and matching only whole-segment edges
/// silently dropped every such constraint from the remesh, walls and breaklines alike.
/// </summary>
public class FeaturePolylineGraphConstraintTests
{
    [Fact]
    public void Build_NearbyUnconnectedVertex_DoesNotInterruptConstraintEdge()
    {
        var (original, faces) = BuildSheet(5, 3);
        var vertices = original.Concat(new[] { 0.75, 0.5000005, 0.0 }).ToArray();
        var graph = Build(vertices, faces, Line((0, 0.5), (2, 0.5)));
        Assert.Contains(EdgeKey(6, 7), graph.FeatureEdgeChains.Keys);
    }

    [Fact]
    public void Build_ConstraintFarBeyondMesh_DoesNotOverflowCellWalk()
    {
        var (vertices, faces) = BuildSheet(5, 3);
        var graph = Build(vertices, faces, Line((-1e10, 0.5), (1e10, 0.5)));
        for (int column = 0; column < 4; column++)
            Assert.Contains(EdgeKey(5 + column, 6 + column), graph.FeatureEdgeChains.Keys);
    }

    [Fact]
    public void Build_ConstraintSegmentSplitByMeshVertices_PinsEveryEdgeAlongIt()
    {
        // 5 x 3 sheet at 0.5 spacing; the middle row y = 0.5 runs x = 0 .. 2 through three interior vertices.
        (double[] vertices, int[] faces) = BuildSheet(5, 3);
        var constraint = Line((0, 0.5), (2, 0.5));

        FeaturePolylineGraph graph = Build(vertices, faces, constraint);

        for (int column = 0; column < 4; column++)
        {
            int a = 5 + column, b = 6 + column;
            Assert.True(graph.FeatureEdgeChains.ContainsKey(EdgeKey(a, b)), $"edge {a}-{b} along the constraint is not pinned");
        }

        for (int column = 1; column < 4; column++)
            Assert.NotEqual(FeaturePolylineGraph.KindFree, graph.VertexKind[5 + column]);
    }

    [Fact]
    public void Build_ConstraintWithEndpointsBetweenMeshVertices_PinsTheEdgesItCovers()
    {
        // The constraint starts and ends inside mesh edges: only the whole edges it covers are pinned.
        (double[] vertices, int[] faces) = BuildSheet(5, 3);
        var constraint = Line((0.25, 0.5), (1.75, 0.5));

        FeaturePolylineGraph graph = Build(vertices, faces, constraint);

        Assert.True(graph.FeatureEdgeChains.ContainsKey(EdgeKey(6, 7)));
        Assert.True(graph.FeatureEdgeChains.ContainsKey(EdgeKey(7, 8)));
        Assert.False(graph.FeatureEdgeChains.ContainsKey(EdgeKey(5, 6)));
        Assert.False(graph.FeatureEdgeChains.ContainsKey(EdgeKey(8, 9)));
    }

    [Fact]
    public void Build_ConstraintCrossingMeshEdges_PinsNothingOffTheLine()
    {
        // A diagonal that no run of mesh edges follows must not pin some unrelated nearby edge.
        (double[] vertices, int[] faces) = BuildSheet(5, 3);
        var constraint = Line((0, 0.1), (2, 0.9));

        FeaturePolylineGraph graph = Build(vertices, faces, constraint);

        int interior = 0;
        for (int column = 1; column < 4; column++)
            if (graph.VertexKind[5 + column] != FeaturePolylineGraph.KindFree)
                interior++;
        Assert.Equal(0, interior);
    }

    [Fact]
    public void Remesh_ConstraintSplitUpstream_StaysAMeshEdgeChain()
    {
        // End to end: a breakline refined upstream into many short edges must survive a coarse remesh as
        // a straight chain of mesh edges, i.e. no remeshed vertex may leave the line and no edge cross it.
        (double[] vertices, int[] faces) = BuildSheet(21, 11, spacing: 0.5);
        var constraint = Line((0, 2.5), (10, 2.5));

        IsotropicRemesher.Result result = IsotropicRemesher.Remesh(vertices, faces, new[] { constraint },
            new IsotropicRemesher.Options { TargetEdgeLength = 2.0, Tolerance = 0.01, CreaseAngleDeg = 30, Iterations = 5 });

        Assert.True(result.Success, result.Warning);
        for (int f = 0; f < result.Faces.Length; f += 3)
        {
            double below = 0, above = 0;
            for (int k = 0; k < 3; k++)
            {
                double y = result.Vertices[result.Faces[f + k] * 3 + 1] - 2.5;
                if (y < -1e-9) below++;
                if (y > 1e-9) above++;
            }

            Assert.False(below > 0 && above > 0, $"face {f / 3} straddles the constraint");
        }
    }

    private static FeaturePolylineGraph Build(double[] vertices, int[] faces, SurfaceRemesher.ConstraintPolyline constraint) =>
        FeaturePolylineGraph.Build(vertices, faces, faces.Length / 3, new[] { constraint },
            creaseAngleDeg: 0.0, wallFaceMinSlopeDeg: 0.0, tolerance: 1e-6);

    private static SurfaceRemesher.ConstraintPolyline Line((double X, double Y) start, (double X, double Y) end) =>
        new(new[] { start.X, start.Y, 0, end.X, end.Y, 0 }, 2, false, false);

    private static long EdgeKey(int a, int b) => ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);

    private static (double[] Vertices, int[] Faces) BuildSheet(int columns, int rows, double spacing = 0.5)
    {
        var vertices = new double[columns * rows * 3];
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                int index = (row * columns) + column;
                vertices[index * 3] = column * spacing;
                vertices[(index * 3) + 1] = row * spacing;
            }
        }

        var faces = new List<int>();
        for (int row = 0; row < rows - 1; row++)
        {
            for (int column = 0; column < columns - 1; column++)
            {
                int v00 = (row * columns) + column;
                int v10 = v00 + 1;
                int v01 = v00 + columns;
                int v11 = v01 + 1;
                faces.Add(v00); faces.Add(v10); faces.Add(v11);
                faces.Add(v00); faces.Add(v11); faces.Add(v01);
            }
        }

        return (vertices, faces.ToArray());
    }
}
