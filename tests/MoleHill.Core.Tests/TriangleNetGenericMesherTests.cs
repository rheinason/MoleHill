using TriangleNet.Geometry;
using TriangleNet.Meshing;
using Xunit;

namespace MoleHill.Core.Tests;

public class TriangleNetGenericMesherTests
{
    [Fact]
    public void PlainTriangulation_CanCreateQualityMesherLazilyDuringRefine()
    {
        IMesh mesh = new GenericMesher().Triangulate(
            BuildSquare(),
            new ConstraintOptions
            {
                ConformingDelaunay = false,
                Convex = true
            });
        int initialFaceCount = mesh.Triangles.Count;

        mesh.Refine(
            new QualityOptions
            {
                MaximumArea = 5.0,
                SteinerPoints = 100
            },
            delaunay: false);

        Assert.True(mesh.Triangles.Count > initialFaceCount);
    }

    [Fact]
    public void QualityTriangulation_StillRefinesDuringInitialTriangulation()
    {
        IMesh mesh = new GenericMesher().Triangulate(
            BuildSquare(),
            new ConstraintOptions
            {
                ConformingDelaunay = false,
                Convex = true
            },
            new QualityOptions
            {
                MaximumArea = 5.0,
                SteinerPoints = 100
            });

        Assert.True(mesh.Triangles.Count > 2);
    }

    private static Polygon BuildSquare()
    {
        var polygon = new Polygon(4);
        polygon.Add(new Vertex(0.0, 0.0));
        polygon.Add(new Vertex(10.0, 0.0));
        polygon.Add(new Vertex(10.0, 10.0));
        polygon.Add(new Vertex(0.0, 10.0));
        return polygon;
    }
}
