using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// Guards the constraint topology inserter's crossing / overlap / duplicate handling. These pin the
/// behaviour that the indexed constraint-pair discovery and the on-demand face geometry must preserve.
/// </summary>
public class MeshConstraintTopologyInserterTests
{
    private const double Tolerance = 1e-6;

    [Fact]
    public void TryInsert_NoConstraints_ReturnsIndependentCopyOfInput()
    {
        (double[] vertices, int[] faces) = BuildGrid(2, 2);

        bool ok = MeshConstraintTopologyInserter.TryInsert(
            new IndexedTriMesh(vertices, vertices.Length / 3, faces, faces.Length / 3),
            Array.Empty<ConstraintPolyline>(),
            Tolerance,
            out IndexedTriMesh insertedMesh,
            out string? error);
        double[] outVertices = insertedMesh.Vertices;
        int outVertexCount = insertedMesh.VertexCount;
        int[] outFaces = insertedMesh.Faces;
        int outFaceCount = insertedMesh.FaceCount;

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(vertices, outVertices);
        Assert.Equal(faces, outFaces);
        Assert.Equal(vertices.Length / 3, outVertexCount);
        Assert.Equal(faces.Length / 3, outFaceCount);

        // The caller owns the output; mutating it must not disturb the input arrays.
        outVertices[0] = 999.0;
        outFaces[0] = 12345;
        Assert.NotEqual(999.0, vertices[0]);
        Assert.NotEqual(12345, faces[0]);
    }

    [Fact]
    public void TryInsert_ConstraintAwayFromMesh_LeavesTopologyUnchanged()
    {
        (double[] vertices, int[] faces) = BuildGrid(2, 2);
        var constraint = Open(50.0, 50.0, 60.0, 60.0);

        bool ok = Insert(vertices, faces, new[] { constraint }, out _, out int outFaceCount, out _, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(faces.Length / 3, outFaceCount);
    }

    [Fact]
    public void TryInsert_CrossingConstraints_SplitsBothAtTheIntersection()
    {
        (double[] vertices, int[] faces) = BuildGrid(4, 4);
        var horizontal = Open(0.5, 2.0, 3.5, 2.0);
        var vertical = Open(2.0, 0.5, 2.0, 3.5);

        bool ok = Insert(vertices, faces, new[] { horizontal, vertical }, out double[] outVertices, out int outFaceCount, out int[] outFaces, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.True(outFaceCount > faces.Length / 3);

        // The crossing point itself has to exist as a vertex, otherwise the two constraints do not
        // share topology where they meet.
        Assert.True(HasVertexAt(outVertices, 2.0, 2.0));
        AssertIndicesInRange(outFaces, outFaceCount, outVertices.Length / 3);
    }

    [Fact]
    public void TryInsert_DuplicateConstraint_MatchesTheSingleConstraintResult()
    {
        (double[] vertices, int[] faces) = BuildGrid(3, 3);
        var single = Open(0.25, 1.5, 2.75, 1.5);

        bool okSingle = Insert(vertices, faces, new[] { single }, out double[] singleVertices, out int singleFaceCount, out int[] singleFaces, out string? singleError);
        bool okDuplicate = Insert(vertices, faces, new[] { single, Open(0.25, 1.5, 2.75, 1.5) }, out double[] duplicateVertices, out int duplicateFaceCount, out int[] duplicateFaces, out string? duplicateError);

        Assert.True(okSingle);
        Assert.True(okDuplicate);
        Assert.Null(singleError);
        Assert.Null(duplicateError);
        Assert.Equal(singleFaceCount, duplicateFaceCount);
        Assert.Equal(singleVertices, duplicateVertices);
        Assert.Equal(singleFaces, duplicateFaces);
    }

    [Fact]
    public void TryInsert_CollinearOverlappingConstraints_ProduceOneSharedTopology()
    {
        (double[] vertices, int[] faces) = BuildGrid(4, 4);
        var first = Open(0.5, 2.0, 2.5, 2.0);
        var second = Open(1.5, 2.0, 3.5, 2.0);

        bool ok = Insert(vertices, faces, new[] { first, second }, out double[] outVertices, out int outFaceCount, out int[] outFaces, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        AssertIndicesInRange(outFaces, outFaceCount, outVertices.Length / 3);

        // The overlap endpoints are split points on both constraints.
        Assert.True(HasVertexAt(outVertices, 1.5, 2.0));
        Assert.True(HasVertexAt(outVertices, 2.5, 2.0));
        Assert.True(NoDuplicateVertices(outVertices));
    }

    [Fact]
    public void TryInsert_ClosedConstraintLoop_KeepsEveryCornerOnce()
    {
        (double[] vertices, int[] faces) = BuildGrid(4, 4);
        var loop = Closed(1.25, 1.25, 2.75, 1.25, 2.75, 2.75, 1.25, 2.75);

        bool ok = Insert(vertices, faces, new[] { loop }, out double[] outVertices, out int outFaceCount, out int[] outFaces, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        AssertIndicesInRange(outFaces, outFaceCount, outVertices.Length / 3);
        Assert.True(HasVertexAt(outVertices, 1.25, 1.25));
        Assert.True(HasVertexAt(outVertices, 2.75, 2.75));
        Assert.True(NoDuplicateVertices(outVertices));
    }

    [Fact]
    public void TryInsert_ConstraintOrderIsIrrelevantToTheResultSize()
    {
        (double[] vertices, int[] faces) = BuildGrid(4, 4);
        var a = Open(0.5, 2.0, 3.5, 2.0);
        var b = Open(2.0, 0.5, 2.0, 3.5);

        Insert(vertices, faces, new[] { a, b }, out double[] forwardVertices, out int forwardFaceCount, out _, out _);
        Insert(vertices, faces, new[] { b, a }, out double[] reverseVertices, out int reverseFaceCount, out _, out _);

        Assert.Equal(forwardFaceCount, reverseFaceCount);
        Assert.Equal(forwardVertices.Length, reverseVertices.Length);
    }

    [Fact]
    public void TryInsert_PreservesInterpolatedElevationOnASlopedMesh()
    {
        (double[] vertices, int[] faces) = BuildGrid(3, 3, (x, y) => x + (2.0 * y));
        var constraint = Open(0.25, 1.5, 2.75, 1.5);

        bool ok = Insert(vertices, faces, new[] { constraint }, out double[] outVertices, out int outFaceCount, out int[] outFaces, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        AssertIndicesInRange(outFaces, outFaceCount, outVertices.Length / 3);

        for (int i = 0; i < outVertices.Length / 3; i++)
        {
            double x = outVertices[i * 3];
            double y = outVertices[(i * 3) + 1];
            double z = outVertices[(i * 3) + 2];
            Assert.Equal(x + (2.0 * y), z, 6);
        }
    }

    [Fact]
    public void TryInsert_EmptyMesh_ReportsAnError()
    {
        bool ok = MeshConstraintTopologyInserter.TryInsert(
            new IndexedTriMesh(Array.Empty<double>(), 0, Array.Empty<int>(), 0),
            new[] { Open(0.0, 0.0, 1.0, 1.0) },
            Tolerance,
            out IndexedTriMesh insertedMesh2,
            out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    private static bool Insert(
        double[] vertices,
        int[] faces,
        ConstraintPolyline[] constraints,
        out double[] outVertices,
        out int outFaceCount,
        out int[] outFaces,
        out string? error)
    {
        bool inserted3 = MeshConstraintTopologyInserter.TryInsert(
            new IndexedTriMesh(vertices, vertices.Length / 3, faces, faces.Length / 3),
            constraints,
            Tolerance,
            out IndexedTriMesh insertedMesh3,
            out error);
        outVertices = insertedMesh3.Vertices;
        outFaces = insertedMesh3.Faces;
        outFaceCount = insertedMesh3.FaceCount;
        return inserted3;
    }

    private static ConstraintPolyline Open(params double[] xy)
    {
        return Build(xy, isClosed: false);
    }

    private static ConstraintPolyline Closed(params double[] xy)
    {
        return Build(xy, isClosed: true);
    }

    private static ConstraintPolyline Build(double[] xy, bool isClosed)
    {
        int pointCount = xy.Length / 2;
        var points = new double[pointCount * 3];
        for (int i = 0; i < pointCount; i++)
        {
            points[i * 3] = xy[i * 2];
            points[(i * 3) + 1] = xy[(i * 2) + 1];
            points[(i * 3) + 2] = 0.0;
        }

        return new ConstraintPolyline(points, pointCount, isClosed);
    }

    private static (double[] Vertices, int[] Faces) BuildGrid(int columns, int rows, Func<double, double, double>? height = null)
    {
        int vertexColumns = columns + 1;
        int vertexRows = rows + 1;
        var vertices = new double[vertexColumns * vertexRows * 3];
        for (int row = 0; row < vertexRows; row++)
        {
            for (int column = 0; column < vertexColumns; column++)
            {
                int index = (row * vertexColumns) + column;
                vertices[index * 3] = column;
                vertices[(index * 3) + 1] = row;
                vertices[(index * 3) + 2] = height?.Invoke(column, row) ?? 0.0;
            }
        }

        var faces = new List<int>(columns * rows * 6);
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                int bottomLeft = (row * vertexColumns) + column;
                int bottomRight = bottomLeft + 1;
                int topLeft = bottomLeft + vertexColumns;
                int topRight = topLeft + 1;

                faces.Add(bottomLeft);
                faces.Add(bottomRight);
                faces.Add(topRight);

                faces.Add(bottomLeft);
                faces.Add(topRight);
                faces.Add(topLeft);
            }
        }

        return (vertices, faces.ToArray());
    }

    private static bool HasVertexAt(double[] vertices, double x, double y)
    {
        for (int i = 0; i < vertices.Length / 3; i++)
        {
            double dx = vertices[i * 3] - x;
            double dy = vertices[(i * 3) + 1] - y;
            if ((dx * dx) + (dy * dy) <= 1e-12)
                return true;
        }

        return false;
    }

    private static bool NoDuplicateVertices(double[] vertices)
    {
        int count = vertices.Length / 3;
        for (int i = 0; i < count; i++)
        {
            for (int j = i + 1; j < count; j++)
            {
                double dx = vertices[i * 3] - vertices[j * 3];
                double dy = vertices[(i * 3) + 1] - vertices[(j * 3) + 1];
                if ((dx * dx) + (dy * dy) <= 1e-18)
                    return false;
            }
        }

        return true;
    }

    private static void AssertIndicesInRange(int[] faces, int faceCount, int vertexCount)
    {
        for (int i = 0; i < faceCount * 3; i++)
            Assert.InRange(faces[i], 0, vertexCount - 1);
    }
}
