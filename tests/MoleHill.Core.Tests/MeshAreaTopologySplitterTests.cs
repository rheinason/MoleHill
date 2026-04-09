using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

public class MeshAreaTopologySplitterTests
{
    [Fact]
    public void SplitPreservingTopology_BoundaryOnExistingEdges_LeavesMeshTopologyUnchanged()
    {
        var result = MeshAreaSplitter.SplitPreservingTopology(
            new[]
            {
                0.0, 0.0, 0.0,
                2.0, 0.0, 0.0,
                2.0, 2.0, 0.0,
                0.0, 2.0, 0.0
            },
            4,
            new[]
            {
                0, 1, 2,
                0, 2, 3
            },
            2,
            new[]
            {
                new MeshAreaSplitter.AreaBoundary(
                    new[] { 0.0, 0.0, 2.0, 0.0, 2.0, 2.0 },
                    3)
            },
            0.001,
            out var errorMessage);

        Assert.True(result != null, errorMessage);
        Assert.Null(errorMessage);
        Assert.Equal(4, result!.VertexCount);
        Assert.Equal(2, result.FaceCount);
        Assert.Contains(result.FaceAreaIndex, area => area == 0);
        Assert.Contains(result.FaceAreaIndex, area => area == -1);
    }

    [Fact]
    public void SplitPreservingTopology_PreservesUntouchedFaceAndInterpolatesBoundaryElevation()
    {
        var vertices = CreateGridVertices(4, 4, 2.0);
        var faces = CreateGridFaces(4, 4);
        var result = MeshAreaSplitter.SplitPreservingTopology(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            new[]
            {
                new MeshAreaSplitter.AreaBoundary(
                    new[] { 2.5, 2.5, 3.5, 2.5, 3.5, 3.5, 2.5, 3.5 },
                    4)
            },
            0.001,
            out var errorMessage);

        Assert.True(result != null, errorMessage);
        Assert.Null(errorMessage);

        AssertVertexZ(result!, 2.5, 2.5, 5.0);
        AssertVertexZ(result, 3.5, 2.5, 6.0);
        AssertContainsFace(
            result,
            (0.0, 0.0),
            (2.0, 0.0),
            (2.0, 2.0));
    }

    [Fact]
    public void SplitPreservingTopology_CreatesSharedExactCutAcrossAdjacentTriangles()
    {
        var result = MeshAreaSplitter.SplitPreservingTopology(
            new[]
            {
                0.0, 0.0, 0.0,
                2.0, 0.0, 0.0,
                2.0, 2.0, 0.0,
                0.0, 2.0, 0.0
            },
            4,
            new[]
            {
                0, 1, 2,
                0, 2, 3
            },
            2,
            new[]
            {
                new MeshAreaSplitter.AreaBoundary(
                    new[] { 0.0, 0.0, 1.0, 0.0, 1.0, 2.0, 0.0, 2.0 },
                    4)
            },
            0.001,
            out var errorMessage);

        Assert.True(result != null, errorMessage);
        Assert.Null(errorMessage);

        AssertContainsEdge(result!, (1.0, 0.0), (1.0, 1.0));
        AssertContainsEdge(result, (1.0, 1.0), (1.0, 2.0));
    }

    [Fact]
    public void SplitPreservingTopology_SharedCutOnSlopedSurface_KeepsSingleInterpolatedBoundary()
    {
        var result = MeshAreaSplitter.SplitPreservingTopology(
            new[]
            {
                0.0, 0.0, 0.0,
                2.0, 0.0, 2.0,
                2.0, 2.0, 4.0,
                0.0, 2.0, 2.0
            },
            4,
            new[]
            {
                0, 1, 2,
                0, 2, 3
            },
            2,
            new[]
            {
                new MeshAreaSplitter.AreaBoundary(
                    new[] { 0.0, 0.0, 1.0, 0.0, 1.0, 2.0, 0.0, 2.0 },
                    4),
                new MeshAreaSplitter.AreaBoundary(
                    new[] { 1.0, 0.0, 2.0, 0.0, 2.0, 2.0, 1.0, 2.0 },
                    4)
            },
            0.001,
            out var errorMessage);

        Assert.True(result != null, errorMessage);
        Assert.Null(errorMessage);

        AssertContainsEdge(result!, (1.0, 0.0), (1.0, 1.0));
        AssertContainsEdge(result, (1.0, 1.0), (1.0, 2.0));

        AssertVertexZ(result, 1.0, 0.0, 1.0);
        AssertVertexZ(result, 1.0, 1.0, 2.0);
        AssertVertexZ(result, 1.0, 2.0, 3.0);

        Assert.Equal(1, CountVertices(result, 1.0, 0.0, 1e-9));
        Assert.Equal(1, CountVertices(result, 1.0, 1.0, 1e-9));
        Assert.Equal(1, CountVertices(result, 1.0, 2.0, 1e-9));
    }

    [Fact]
    public void SplitPreservingTopology_OverlappingAreas_LaterAreaWinsInOverlap()
    {
        double[] vertices =
        {
            0.0, 0.0, 0.0,
            4.0, 0.0, 0.0,
            4.0, 4.0, 0.0,
            0.0, 4.0, 0.0
        };
        int[] faces =
        {
            0, 1, 2,
            0, 2, 3
        };
        var outer = new MeshAreaSplitter.AreaBoundary(
            new[] { 0.5, 0.5, 3.5, 0.5, 3.5, 3.5, 0.5, 3.5 },
            4);
        var inner = new MeshAreaSplitter.AreaBoundary(
            new[] { 1.5, 1.5, 2.5, 1.5, 2.5, 2.5, 1.5, 2.5 },
            4);

        var result = MeshAreaSplitter.SplitPreservingTopology(
            vertices,
            4,
            faces,
            2,
            new[] { outer, inner },
            0.001,
            out var errorMessage);

        Assert.True(result != null, errorMessage);
        Assert.Null(errorMessage);

        int overlapFaceCount = 0;
        int outerOnlyFaceCount = 0;
        for (int faceIndex = 0; faceIndex < result!.FaceCount; faceIndex++)
        {
            var (cx, cy) = GetFaceCentroid(result, faceIndex);
            if (PadGrader.PointInPolygon(cx, cy, inner.XyVertices, inner.VertexCount))
            {
                Assert.Equal(1, result.FaceAreaIndex[faceIndex]);
                overlapFaceCount++;
            }
            else if (PadGrader.PointInPolygon(cx, cy, outer.XyVertices, outer.VertexCount))
            {
                Assert.Equal(0, result.FaceAreaIndex[faceIndex]);
                outerOnlyFaceCount++;
            }
        }

        Assert.True(overlapFaceCount > 0);
        Assert.True(outerOnlyFaceCount > 0);
    }

    private static double[] CreateGridVertices(int xCount, int yCount, double spacing)
    {
        var result = new double[xCount * yCount * 3];
        int index = 0;
        for (int y = 0; y < yCount; y++)
        {
            for (int x = 0; x < xCount; x++)
            {
                double px = x * spacing;
                double py = y * spacing;
                result[index++] = px;
                result[index++] = py;
                result[index++] = px + py;
            }
        }

        return result;
    }

    private static int[] CreateGridFaces(int xCount, int yCount)
    {
        var faces = new List<int>();
        for (int y = 0; y < yCount - 1; y++)
        {
            for (int x = 0; x < xCount - 1; x++)
            {
                int v00 = (y * xCount) + x;
                int v10 = v00 + 1;
                int v01 = v00 + xCount;
                int v11 = v01 + 1;

                faces.Add(v00);
                faces.Add(v10);
                faces.Add(v11);

                faces.Add(v00);
                faces.Add(v11);
                faces.Add(v01);
            }
        }

        return faces.ToArray();
    }

    private static (double X, double Y) GetFaceCentroid(MeshAreaSplitter.SplitResult result, int faceIndex)
    {
        int i0 = result.Faces[faceIndex * 3];
        int i1 = result.Faces[faceIndex * 3 + 1];
        int i2 = result.Faces[faceIndex * 3 + 2];
        double cx = (result.Vertices[i0 * 3] + result.Vertices[i1 * 3] + result.Vertices[i2 * 3]) / 3.0;
        double cy = (result.Vertices[i0 * 3 + 1] + result.Vertices[i1 * 3 + 1] + result.Vertices[i2 * 3 + 1]) / 3.0;
        return (cx, cy);
    }

    private static void AssertVertexZ(MeshAreaSplitter.SplitResult result, double x, double y, double expectedZ)
    {
        int index = FindVertex(result, x, y, 1e-9);
        Assert.True(index >= 0, $"Could not find vertex at ({x}, {y}).");
        Assert.Equal(expectedZ, result.Vertices[index * 3 + 2], 6);
    }

    private static int FindVertex(MeshAreaSplitter.SplitResult result, double x, double y, double tolerance)
    {
        for (int i = 0; i < result.VertexCount; i++)
        {
            double dx = result.Vertices[i * 3] - x;
            double dy = result.Vertices[i * 3 + 1] - y;
            if (Math.Abs(dx) <= tolerance && Math.Abs(dy) <= tolerance)
                return i;
        }

        return -1;
    }

    private static int CountVertices(MeshAreaSplitter.SplitResult result, double x, double y, double tolerance)
    {
        int count = 0;
        for (int i = 0; i < result.VertexCount; i++)
        {
            double dx = result.Vertices[i * 3] - x;
            double dy = result.Vertices[i * 3 + 1] - y;
            if (Math.Abs(dx) <= tolerance && Math.Abs(dy) <= tolerance)
                count++;
        }

        return count;
    }

    private static void AssertContainsFace(MeshAreaSplitter.SplitResult result, params (double X, double Y)[] expectedVertices)
    {
        Assert.True(ContainsFace(result, expectedVertices), "Expected face was not found in the split result.");
    }

    private static bool ContainsFace(MeshAreaSplitter.SplitResult result, params (double X, double Y)[] expectedVertices)
    {
        for (int faceIndex = 0; faceIndex < result.FaceCount; faceIndex++)
        {
            var actual = new (double X, double Y)[3];
            for (int corner = 0; corner < 3; corner++)
            {
                int vertexIndex = result.Faces[faceIndex * 3 + corner];
                actual[corner] = (result.Vertices[vertexIndex * 3], result.Vertices[vertexIndex * 3 + 1]);
            }

            if (SamePointSet(actual, expectedVertices))
                return true;
        }

        return false;
    }

    private static void AssertContainsEdge(MeshAreaSplitter.SplitResult result, (double X, double Y) start, (double X, double Y) end)
    {
        Assert.True(ContainsEdge(result, start, end), $"Expected edge ({start}) -> ({end}) was not found.");
    }

    private static bool ContainsEdge(MeshAreaSplitter.SplitResult result, (double X, double Y) start, (double X, double Y) end)
    {
        for (int faceIndex = 0; faceIndex < result.FaceCount; faceIndex++)
        {
            for (int edge = 0; edge < 3; edge++)
            {
                int i0 = result.Faces[faceIndex * 3 + edge];
                int i1 = result.Faces[faceIndex * 3 + ((edge + 1) % 3)];
                var p0 = (result.Vertices[i0 * 3], result.Vertices[i0 * 3 + 1]);
                var p1 = (result.Vertices[i1 * 3], result.Vertices[i1 * 3 + 1]);
                if ((SamePoint(p0, start) && SamePoint(p1, end)) ||
                    (SamePoint(p0, end) && SamePoint(p1, start)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool SamePoint((double X, double Y) left, (double X, double Y) right)
    {
        return Math.Abs(left.X - right.X) <= 1e-9 &&
               Math.Abs(left.Y - right.Y) <= 1e-9;
    }

    private static bool SamePointSet((double X, double Y)[] actual, (double X, double Y)[] expected)
    {
        if (actual.Length != expected.Length)
            return false;

        for (int i = 0; i < expected.Length; i++)
        {
            bool found = false;
            for (int j = 0; j < actual.Length; j++)
            {
                if (SamePoint(actual[j], expected[i]))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
                return false;
        }

        return true;
    }
}
