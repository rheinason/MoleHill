using MoleHill.Core.Engine;
using MoleHill.Core.Sculpting;
using Xunit;

namespace MoleHill.Core.Tests;

public class SculptFieldRasterizerTests
{
    private const double CellSize = 0.25;

    /// <summary>Regular triangulated grid over [0, size]^2 with (n x n) vertices, optionally offset.</summary>
    private static (double[] Vertices, int VertexCount, int[] Faces, int FaceCount) BuildGridMesh(
        int n, double size, double offset = 0.0, Func<double, double, double>? height = null)
    {
        double spacing = size / (n - 1);
        int vertexCount = n * n;
        var vertices = new double[vertexCount * 3];
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                int v = j * n + i;
                double x = offset + i * spacing;
                double y = offset + j * spacing;
                vertices[v * 3] = x;
                vertices[v * 3 + 1] = y;
                vertices[v * 3 + 2] = height?.Invoke(x, y) ?? 0.0;
            }
        }

        int faceCount = (n - 1) * (n - 1) * 2;
        var faces = new int[faceCount * 3];
        int f = 0;
        for (int j = 0; j < n - 1; j++)
        {
            for (int i = 0; i < n - 1; i++)
            {
                int v00 = j * n + i;
                int v10 = v00 + 1;
                int v01 = v00 + n;
                int v11 = v01 + 1;
                faces[f++] = v00; faces[f++] = v10; faces[f++] = v11;
                faces[f++] = v00; faces[f++] = v11; faces[f++] = v01;
            }
        }

        return (vertices, vertexCount, faces, faceCount);
    }

    /// <summary>Applies a smooth bump of the given height at (cx, cy) with the given radius to a mesh's Z.</summary>
    private static void ApplyBump(double[] vertices, int vertexCount, double cx, double cy, double radius, double height)
    {
        for (int i = 0; i < vertexCount; i++)
        {
            double dx = vertices[i * 3] - cx;
            double dy = vertices[i * 3 + 1] - cy;
            double d = Math.Sqrt(dx * dx + dy * dy) / radius;
            vertices[i * 3 + 2] += height * SculptFalloffs.Evaluate(SculptFalloff.Smooth, d);
        }
    }

    [Fact]
    public void Rasterize_ThenReplayOnSameMesh_MatchesWithinCellTolerance()
    {
        var (vertices, vertexCount, faces, faceCount) = BuildGridMesh(21, 10.0);
        var baseZ = new double[vertexCount];
        ApplyBump(vertices, vertexCount, 5.0, 5.0, 3.0, 2.0);

        var field = new SculptDisplacementField(CellSize);
        SculptFieldRasterizer.Rasterize(new IndexedTriMesh(vertices, vertexCount, faces, faceCount), baseZ, field, 2.0, 8.0, 2.0, 8.0);

        for (int i = 0; i < vertexCount; i++)
        {
            double replayed = baseZ[i] + field.Sample(vertices[i * 3], vertices[i * 3 + 1]);
            Assert.Equal(vertices[i * 3 + 2], replayed, 3);
        }
    }

    [Fact]
    public void Rasterize_OutsideDirtyBounds_LeavesFieldUntouched()
    {
        var (vertices, vertexCount, faces, faceCount) = BuildGridMesh(21, 10.0);
        var baseZ = new double[vertexCount];
        ApplyBump(vertices, vertexCount, 5.0, 5.0, 3.0, 2.0);

        var field = new SculptDisplacementField(CellSize);
        field.SetSample(0, 0, 9f); // pre-existing sample far from the dirty region

        SculptFieldRasterizer.Rasterize(new IndexedTriMesh(vertices, vertexCount, faces, faceCount), baseZ, field, 4.0, 6.0, 4.0, 6.0);

        Assert.Equal(9f, field.GetSample(0, 0));
    }

    [Fact]
    public void Rasterize_ThenReplayOnRetriangulatedMesh_PreservesShapeWithinTolerance()
    {
        // Sculpt on a coarse grid, commit to the field, then replay on a finer, shifted triangulation
        // of the same base surface — the stackability contract.
        var (vertices, vertexCount, faces, faceCount) = BuildGridMesh(21, 10.0);
        var baseZ = new double[vertexCount];
        ApplyBump(vertices, vertexCount, 5.0, 5.0, 3.0, 2.0);

        var field = new SculptDisplacementField(CellSize);
        SculptFieldRasterizer.Rasterize(new IndexedTriMesh(vertices, vertexCount, faces, faceCount), baseZ, field, 1.0, 9.0, 1.0, 9.0);

        var (fineVertices, fineCount, _, _) = BuildGridMesh(41, 9.0, offset: 0.4);
        for (int i = 0; i < fineCount; i++)
        {
            double x = fineVertices[i * 3];
            double y = fineVertices[i * 3 + 1];
            double expected = 2.0 * SculptFalloffs.Evaluate(
                SculptFalloff.Smooth, Math.Sqrt((x - 5.0) * (x - 5.0) + (y - 5.0) * (y - 5.0)) / 3.0);
            double replayed = field.Sample(x, y);
            // Error sources: coarse-mesh PL approximation of the bump + bilinear field resolution.
            Assert.True(Math.Abs(replayed - expected) < 0.08,
                $"replay at ({x:F2},{y:F2}) = {replayed:F4}, expected {expected:F4}");
        }
    }

    [Fact]
    public void Rasterize_SecondStroke_MergesWithExistingDisplacement()
    {
        var (vertices, vertexCount, faces, faceCount) = BuildGridMesh(21, 10.0);
        var baseZ = new double[vertexCount];

        // Stroke 1: bump at (3.5, 5), committed.
        ApplyBump(vertices, vertexCount, 3.5, 5.0, 2.0, 1.0);
        var field = new SculptDisplacementField(CellSize);
        SculptFieldRasterizer.Rasterize(new IndexedTriMesh(vertices, vertexCount, faces, faceCount), baseZ, field, 1.0, 6.0, 2.5, 7.5);

        // Stroke 2: overlapping bump at (6, 5), committed over the same field.
        ApplyBump(vertices, vertexCount, 6.0, 5.0, 2.0, 1.0);
        SculptFieldRasterizer.Rasterize(new IndexedTriMesh(vertices, vertexCount, faces, faceCount), baseZ, field, 3.5, 8.5, 2.5, 7.5);

        for (int i = 0; i < vertexCount; i++)
        {
            double replayed = baseZ[i] + field.Sample(vertices[i * 3], vertices[i * 3 + 1]);
            Assert.Equal(vertices[i * 3 + 2], replayed, 3);
        }
    }

    [Fact]
    public void Rasterize_WithConstraint_PreservesProtectedFieldAndStoresRawFeatherValue()
    {
        var (vertices, vertexCount, faces, faceCount) = BuildGridMesh(9, 2.0);
        var baseZ = new double[vertexCount];
        var field = new SculptDisplacementField(CellSize);
        field.SetSample(4, 4, 7f); // (1, 1), inside the protected polygon

        var mask = new SculptConstraintMask(featherDistance: 1.0);
        mask.AddPolygon(new[] { 0.75, 0.75, 1.25, 0.75, 1.25, 1.25, 0.75, 1.25 }, 4);

        for (int i = 0; i < vertexCount; i++)
        {
            double x = vertices[i * 3];
            double y = vertices[i * 3 + 1];
            vertices[i * 3 + 2] = 2.0 * mask.EvaluateInfluence(x, y);
        }

        SculptFieldRasterizer.Rasterize(new IndexedTriMesh(vertices, vertexCount, faces, faceCount), baseZ, field, 0.0, 2.0, 0.0, 2.0, mask);

        Assert.Equal(7f, field.GetSample(4, 4));
        Assert.Equal(2.0, field.GetSample(6, 4), 5); // x=1.5, halfway through the feather
    }
}
