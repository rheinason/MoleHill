using MoleHill.Core.Engine;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// Feature setup reads boundary edges (incidence 1) and non-manifold edges (incidence &gt; 2) off one
/// shared whole-mesh edge-incidence pass. These pin both readings, including the interaction: a mesh
/// with a non-manifold edge still has its perimeter pinned and its offending faces quarantined.
/// </summary>
public class FeaturePolylineGraphIncidenceTests
{
    private static readonly IReadOnlyList<ConstraintPolyline> NoConstraints =
        Array.Empty<ConstraintPolyline>();

    [Fact]
    public void Build_UnitQuad_PinsEveryPerimeterVertex()
    {
        double[] vertices =
        {
            0, 0, 0,
            1, 0, 0,
            1, 1, 0,
            0, 1, 0
        };
        int[] faces = { 0, 1, 2, 0, 2, 3 };

        FeaturePolylineGraph graph = Build(vertices, faces);

        for (int vertex = 0; vertex < 4; vertex++)
            Assert.NotEqual(FeaturePolylineGraph.KindFree, graph.VertexKind[vertex]);
    }

    [Fact]
    public void Build_InteriorVertex_IsFree()
    {
        // A 2x2 quad sheet: vertex 4 is the shared centre, surrounded on all sides.
        (double[] vertices, int[] faces) = BuildSheet(3, 3);

        FeaturePolylineGraph graph = Build(vertices, faces);

        Assert.Equal(FeaturePolylineGraph.KindFree, graph.VertexKind[4]);
    }

    [Fact]
    public void Build_NonManifoldEdge_FreezesEveryFaceTouchingIt()
    {
        // Three triangles share edge 0-1: that edge has incidence 3.
        double[] vertices =
        {
            0, 0, 0,
            1, 0, 0,
            0.5, 1, 0,
            0.5, -1, 0,
            0.5, 0.5, 1
        };
        int[] faces =
        {
            0, 1, 2,
            0, 1, 3,
            0, 1, 4
        };

        FeaturePolylineGraph graph = Build(vertices, faces);

        Assert.All(graph.FrozenFaces, frozen => Assert.True(frozen));
        Assert.NotEqual(FeaturePolylineGraph.KindFree, graph.VertexKind[0]);
        Assert.NotEqual(FeaturePolylineGraph.KindFree, graph.VertexKind[1]);
    }

    [Fact]
    public void Build_NonManifoldSheet_StillPinsTheOuterBoundary()
    {
        // A clean 3x3 sheet plus one extra face duplicating an interior diagonal, so a non-manifold edge
        // and a real perimeter coexist. Both readings come from the same incidence pass.
        (double[] cleanVertices, int[] cleanFaces) = BuildSheet(3, 3);
        var vertices = new List<double>(cleanVertices) { 0.5, 0.5, 5.0 };
        int extra = (vertices.Count / 3) - 1;
        var faces = new List<int>(cleanFaces) { 0, 4, extra };

        FeaturePolylineGraph graph = Build(vertices.ToArray(), faces.ToArray());

        // Corner 0 sits on the perimeter, so it is pinned regardless.
        Assert.NotEqual(FeaturePolylineGraph.KindFree, graph.VertexKind[0]);

        // The added face and the two sheet faces sharing edge 0-4 are quarantined.
        Assert.True(graph.FrozenFaces[^1]);
        Assert.Equal(faces.Count / 3, graph.FrozenFaces.Length);
    }

    private static FeaturePolylineGraph Build(double[] vertices, int[] faces)
    {
        return FeaturePolylineGraph.Build(
            vertices,
            faces,
            faces.Length / 3,
            NoConstraints,
            creaseAngleDeg: 0.0,
            wallFaceMinSlopeDeg: 0.0,
            tolerance: 1e-6);
    }

    private static (double[] Vertices, int[] Faces) BuildSheet(int columns, int rows)
    {
        var vertices = new double[columns * rows * 3];
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                int index = (row * columns) + column;
                vertices[index * 3] = column * 0.5;
                vertices[(index * 3) + 1] = row * 0.5;
                vertices[(index * 3) + 2] = 0.0;
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
