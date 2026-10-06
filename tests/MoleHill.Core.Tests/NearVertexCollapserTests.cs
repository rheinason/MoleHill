using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class NearVertexCollapserTests
{
    // Square 0-1-2-3 fanned from vertex 4, which a re-triangulation created 0.7 mm from corner 0.
    private static List<double> Vertices() => [0, 0, 0, 10, 0, 0, 10, 10, 0, 0, 10, 0, 0.0005, 0.0005, 0];

    [Fact]
    public void Collapse_NewVertexWithinTolerance_FusesItIntoTheInputVertex()
    {
        var faces = new List<int> { 0, 1, 4, 4, 1, 2, 4, 2, 3, 0, 4, 3 };

        int collapsed = NearVertexCollapser.Collapse(Vertices(), faces, firstNewVertex: 4, tolerance: 0.001);

        Assert.Equal(1, collapsed);
        Assert.Equal(new[] { 0, 1, 2, 0, 2, 3 }, faces);
    }

    [Fact]
    public void Collapse_OnlyInputVertices_ChangesNothing()
    {
        var faces = new List<int> { 0, 1, 4, 4, 1, 2, 4, 2, 3, 0, 4, 3 };

        Assert.Equal(0, NearVertexCollapser.Collapse(Vertices(), faces, firstNewVertex: 5, tolerance: 0.001));
        Assert.Equal(12, faces.Count);
    }

    [Fact]
    public void Collapse_NewVertexFartherThanTolerance_IsKept()
    {
        var faces = new List<int> { 0, 1, 4, 4, 1, 2, 4, 2, 3, 0, 4, 3 };

        Assert.Equal(0, NearVertexCollapser.Collapse(Vertices(), faces, firstNewVertex: 4, tolerance: 0.0005));
    }
}
