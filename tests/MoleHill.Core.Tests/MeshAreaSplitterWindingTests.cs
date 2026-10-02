using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// A face the zone split re-triangulates comes back from Triangle.NET counter-clockwise in plan. A face that ran
/// clockwise (a sliver leaning past vertical beside a wall) must keep its own winding, or its pieces traverse the
/// edges they share with untouched neighbours in the same direction.
/// </summary>
public class MeshAreaSplitterWindingTests
{
    [Fact]
    public void SplitPreservingTopology_ClockwiseMesh_KeepsItsWindingThroughTheCut()
    {
        // A 4 x 4 grid of squares, every triangle wound clockwise in plan.
        const int n = 4;
        var vertices = new List<double>();
        for (int j = 0; j <= n; j++)
            for (int i = 0; i <= n; i++)
                vertices.AddRange(new[] { (double)i, j, 0.1 * i });
        var faces = new List<int>();
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                int a = j * (n + 1) + i, b = a + 1, c = a + n + 2, d = a + n + 1;
                faces.AddRange(new[] { a, c, b, a, d, c });
            }

        var boundary = new MeshAreaSplitter.AreaBoundary(new[] { 0.5, 0.5, 2.5, 0.7, 2.3, 2.6, 0.6, 2.4 }, 4);
        MeshAreaSplitter.SplitResult? result = MeshAreaSplitter.SplitPreservingTopology(
            vertices.ToArray(), vertices.Count / 3, faces.ToArray(), faces.Count / 3, new[] { boundary }, 1e-6, out string? error);

        Assert.NotNull(result);
        Assert.True(result!.FaceCount > faces.Count / 3, error);
        Assert.True(MeshArrayNormalizer.HasConsistentWinding(result.Faces, result.FaceCount));
        for (int t = 0; t < result.FaceCount; t++)
        {
            int a = result.Faces[t * 3], b = result.Faces[t * 3 + 1], c = result.Faces[t * 3 + 2];
            double cross = ((result.Vertices[b * 3] - result.Vertices[a * 3]) * (result.Vertices[c * 3 + 1] - result.Vertices[a * 3 + 1])) -
                           ((result.Vertices[b * 3 + 1] - result.Vertices[a * 3 + 1]) * (result.Vertices[c * 3] - result.Vertices[a * 3]));
            Assert.True(cross < 0, $"face {t} is counter-clockwise");
        }
    }
}
