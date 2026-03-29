using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

public class PadGraderTests
{
    [Fact]
    public void TriangulatePadTopology_ReturnsStableTopology_ForSameXyInputs()
    {
        var vertices = BuildGridVertices(5, 0.75);
        var faces = BuildGridFaces(5);
        var pads = BuildPads();

        bool first = PadGrader.TryTriangulateTopology(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            pads,
            null,
            0.0,
            0.0,
            out var topologyVerticesA,
            out var topologyVertexCountA,
            out var topologyFacesA,
            out var topologyFaceCountA,
            out var warningA);

        bool second = PadGrader.TryTriangulateTopology(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            pads,
            null,
            0.0,
            0.0,
            out var topologyVerticesB,
            out var topologyVertexCountB,
            out var topologyFacesB,
            out var topologyFaceCountB,
            out var warningB);

        Assert.True(first, warningA);
        Assert.True(second, warningB);
        Assert.Equal(topologyVertexCountA, topologyVertexCountB);
        Assert.Equal(topologyFaceCountA, topologyFaceCountB);
        Assert.Equal(topologyVerticesA, topologyVerticesB);
        Assert.Equal(topologyFacesA, topologyFacesB);
    }

    [Fact]
    public void ApplyGradingZ_DoesNotChangeVertexOrFaceCount()
    {
        var vertices = BuildGridVertices(5, 0.75);
        var faces = BuildGridFaces(5);
        var pads = BuildPads();

        bool success = PadGrader.TryTriangulateTopology(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            pads,
            null,
            0.0,
            0.0,
            out var topologyVertices,
            out var topologyVertexCount,
            out var topologyFaces,
            out var topologyFaceCount,
            out var warning);

        Assert.True(success, warning);

        double[] gradedVertices = PadGrader.ApplyGradingZ(topologyVertices, topologyVertexCount, pads);

        Assert.Equal(topologyVertices.Length, gradedVertices.Length);
        Assert.Equal(topologyFaceCount * 3, topologyFaces.Length);
        for (int i = 0; i < topologyVertexCount; i++)
        {
            Assert.Equal(topologyVertices[i * 3], gradedVertices[i * 3], 12);
            Assert.Equal(topologyVertices[i * 3 + 1], gradedVertices[i * 3 + 1], 12);
        }
    }

    [Fact]
    public void ApplyGradingZ_PreservesLaterPadWinsOrdering()
    {
        var vertices = BuildGridVertices(5, 0.75);
        var faces = BuildGridFaces(5);
        var pads = BuildPads();

        bool success = PadGrader.TryTriangulateTopology(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            pads,
            null,
            0.0,
            0.0,
            out var topologyVertices,
            out var topologyVertexCount,
            out _,
            out _,
            out var warning);

        Assert.True(success, warning);

        double[] gradedVertices = PadGrader.ApplyGradingZ(topologyVertices, topologyVertexCount, pads);

        int centerIndex = FindVertexIndex(gradedVertices, topologyVertexCount, 1.5, 1.5);
        int outerOnlyIndex = FindVertexIndex(gradedVertices, topologyVertexCount, 0.75, 0.75);

        Assert.Equal(2.0, gradedVertices[centerIndex * 3 + 2], 6);
        Assert.Equal(1.0, gradedVertices[outerOnlyIndex * 3 + 2], 6);
    }

    [Fact]
    public void ApplyGradingZ_EvaluatesPlanarPadSurfaceInsideBoundary()
    {
        double[] vertices = BuildGridVertices(5, 1.0);
        int[] faces = BuildGridFaces(5);
        var pads = new[] { BuildAngledPad() };

        bool success = PadGrader.TryTriangulateTopology(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            pads,
            null,
            0.0,
            0.0,
            out var topologyVertices,
            out var topologyVertexCount,
            out _,
            out _,
            out var warning);

        Assert.True(success, warning);

        double[] gradedVertices = PadGrader.ApplyGradingZ(topologyVertices, topologyVertexCount, pads);

        int interiorIndex = FindVertexIndex(gradedVertices, topologyVertexCount, 2.0, 2.0);
        int boundaryIndex = FindVertexIndex(gradedVertices, topologyVertexCount, 3.0, 2.0);

        Assert.Equal(1.0, gradedVertices[interiorIndex * 3 + 2], 6);
        Assert.Equal(2.0, gradedVertices[boundaryIndex * 3 + 2], 6);
    }

    [Fact]
    public void ApplyGradingZ_UsesBoundaryZForAngledDaylight()
    {
        double[] vertices = BuildGridVertices(5, 1.0);
        int[] faces = BuildGridFaces(5);
        var pads = new[] { BuildAngledPad() };

        bool success = PadGrader.TryTriangulateTopology(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            pads,
            null,
            0.0,
            0.0,
            out var topologyVertices,
            out var topologyVertexCount,
            out _,
            out _,
            out var warning);

        Assert.True(success, warning);

        double[] gradedVertices = PadGrader.ApplyGradingZ(topologyVertices, topologyVertexCount, pads);

        int outsideIndex = FindVertexIndex(gradedVertices, topologyVertexCount, 4.0, 2.0);
        Assert.Equal(1.0, gradedVertices[outsideIndex * 3 + 2], 6);
    }

    [Fact]
    public void Grade_ProducesSameResultAs_SplitPath_ForIdenticalInputs()
    {
        var vertices = BuildGridVertices(5, 0.75);
        var faces = BuildGridFaces(5);
        var pads = BuildPads();

        var legacy = PadGrader.Grade(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            pads,
            null,
            0.0,
            0.0,
            out var legacyWarning);

        Assert.NotNull(legacy);
        Assert.True(string.IsNullOrWhiteSpace(legacyWarning) || !legacyWarning.Contains("failed", StringComparison.OrdinalIgnoreCase));

        bool success = PadGrader.TryTriangulateTopology(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            pads,
            null,
            0.0,
            0.0,
            out var topologyVertices,
            out var topologyVertexCount,
            out var topologyFaces,
            out var topologyFaceCount,
            out var splitWarning);

        Assert.True(success, splitWarning);

        double[] splitVertices = PadGrader.ApplyGradingZ(topologyVertices, topologyVertexCount, pads);

        Assert.Equal(topologyVertexCount, legacy!.VertexCount);
        Assert.Equal(topologyFaceCount, legacy.FaceCount);
        Assert.Equal(topologyFaces, legacy.Faces);
        Assert.Equal(splitVertices.Length, legacy.Vertices.Length);
        for (int i = 0; i < splitVertices.Length; i++)
            Assert.Equal(splitVertices[i], legacy.Vertices[i], 6);
    }

    [Fact]
    public void TryTriangulateTopology_InsertsShoulderVertices_WhenOffsetFitsInsideBoundary()
    {
        double[] vertices = BuildGridVertices(9, 1.0);
        int[] faces = BuildGridFaces(9);
        var pads = new[]
        {
            new PadGrader.PadBoundary(
                new[]
                {
                    3.0, 3.0,
                    5.0, 3.0,
                    5.0, 5.0,
                    3.0, 5.0
                },
                4,
                2.0,
                slopeAngleDeg: 33.0,
                maxDistance: 1.5)
        };

        bool success = PadGrader.TryTriangulateTopology(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            pads,
            null,
            0.0,
            0.0,
            out var topologyVertices,
            out _,
            out _,
            out _,
            out var warning);

        Assert.True(success, warning);
        Assert.True(ContainsVertex(topologyVertices, 1.5, 1.5));
    }

    [Fact]
    public void TryTriangulateTopology_LockCurve_PreservesConstraintIntersections()
    {
        double[] vertices =
        {
            0.0, 0.0, 0.0,
            4.0, 0.0, 0.0,
            4.0, 4.0, 0.0,
            0.0, 4.0, 0.0
        };
        int[] faces = BuildSquareFaces();
        var pads = new[]
        {
            new PadGrader.PadBoundary(
                new[]
                {
                    1.0, 1.0,
                    3.0, 1.0,
                    3.0, 3.0,
                    1.0, 3.0
                },
                4,
                1.0)
        };
        var locks = new[]
        {
            new PadGrader.LockCurve(new[] { 0.0, 2.0, 4.0, 2.0 }, 2)
        };

        bool success = PadGrader.TryTriangulateTopology(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            pads,
            locks,
            0.0,
            0.0,
            out var topologyVertices,
            out _,
            out _,
            out _,
            out var warning);

        Assert.True(success, warning);
        Assert.True(ContainsVertex(topologyVertices, 1.0, 2.0));
        Assert.True(ContainsVertex(topologyVertices, 3.0, 2.0));
    }

    private static PadGrader.PadBoundary[] BuildPads()
    {
        return new[]
        {
            new PadGrader.PadBoundary(
                new[]
                {
                    0.375, 0.375,
                    2.625, 0.375,
                    2.625, 2.625,
                    0.375, 2.625
                },
                4,
                1.0),
            new PadGrader.PadBoundary(
                new[]
                {
                    1.125, 1.125,
                    1.875, 1.125,
                    1.875, 1.875,
                    1.125, 1.875
                },
                4,
                2.0)
        };
    }

    private static PadGrader.PadBoundary BuildAngledPad()
    {
        return PadGrader.PadBoundary.CreatePlanar(
            new[]
            {
                1.0, 1.0, 0.0,
                3.0, 1.0, 2.0,
                3.0, 3.0, 2.0,
                1.0, 3.0, 0.0
            },
            4,
            planeXCoeff: 1.0,
            planeYCoeff: 0.0,
            planeConstant: -1.0,
            slopeAngleDeg: 45.0);
    }

    private static double[] BuildGridVertices(int size, double spacing)
    {
        var vertices = new double[size * size * 3];
        int index = 0;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                vertices[index * 3] = x * spacing;
                vertices[index * 3 + 1] = y * spacing;
                vertices[index * 3 + 2] = 0.0;
                index++;
            }
        }

        return vertices;
    }

    private static int[] BuildGridFaces(int size)
    {
        var faces = new List<int>((size - 1) * (size - 1) * 6);
        for (int y = 0; y < size - 1; y++)
        {
            for (int x = 0; x < size - 1; x++)
            {
                int a = y * size + x;
                int b = a + 1;
                int c = a + size;
                int d = c + 1;

                faces.Add(a);
                faces.Add(b);
                faces.Add(d);

                faces.Add(a);
                faces.Add(d);
                faces.Add(c);
            }
        }

        return faces.ToArray();
    }

    private static int[] BuildSquareFaces()
    {
        return new[]
        {
            0, 1, 2,
            0, 2, 3
        };
    }

    private static int FindVertexIndex(double[] vertices, int vertexCount, double x, double y)
    {
        for (int i = 0; i < vertexCount; i++)
        {
            if (Math.Abs(vertices[i * 3] - x) <= 1e-6 &&
                Math.Abs(vertices[i * 3 + 1] - y) <= 1e-6)
            {
                return i;
            }
        }

        throw new Xunit.Sdk.XunitException($"Could not find vertex at ({x}, {y}).");
    }

    private static bool ContainsVertex(double[] vertices, double x, double y)
    {
        int vertexCount = vertices.Length / 3;
        for (int i = 0; i < vertexCount; i++)
        {
            if (Math.Abs(vertices[i * 3] - x) <= 1e-6 &&
                Math.Abs(vertices[i * 3 + 1] - y) <= 1e-6)
            {
                return true;
            }
        }

        return false;
    }
}
