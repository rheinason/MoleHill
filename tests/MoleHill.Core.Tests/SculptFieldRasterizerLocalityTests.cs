using MoleHill.Core.Engine;
using MoleHill.Core.Sculpting;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// The stroke commit builds its raw-delta surface and face grid over only the faces under the sampled
/// rectangle. These pin the two things that must not change: the field the commit writes, and the fact
/// that nothing outside the dirty rectangle is touched — regardless of how large the terrain around the
/// stroke is.
/// </summary>
public class SculptFieldRasterizerLocalityTests
{
    private const double CellSize = 0.25;

    [Fact]
    public void Rasterize_SameStrokeOnALargerTerrain_WritesTheSameField()
    {
        // Identical stroke, identical local geometry, but one terrain extends far past it. Localizing
        // the delta surface must not change a single sample.
        SculptDisplacementField small = CommitBump(sheetSize: 10.0, resolution: 41);
        SculptDisplacementField large = CommitBump(sheetSize: 40.0, resolution: 161);

        Assert.False(small.IsEmpty);
        AssertSameSamples(small, large);
    }

    [Fact]
    public void Rasterize_LeavesSamplesOutsideTheDirtyRectangleAlone()
    {
        (double[] vertices, int vertexCount, int[] faces, int faceCount) = Grid(81, 20.0);
        var baseZ = new double[vertexCount];
        for (int i = 0; i < vertexCount; i++)
            baseZ[i] = vertices[(i * 3) + 2];

        var field = new SculptDisplacementField(CellSize);
        field.SetSample(200, 200, 7.5f);

        Bump(vertices, vertexCount, 10.0, 10.0, 2.0, 3.0);
        SculptFieldRasterizer.Rasterize(new IndexedTriMesh(vertices, vertexCount, faces, faceCount), baseZ, field, dirtyMinX: 8.0, dirtyMaxX: 12.0, dirtyMinY: 8.0, dirtyMaxY: 12.0);

        Assert.Equal(7.5f, field.GetSample(200, 200));
        Assert.True(Math.Abs(field.GetSample(40, 40)) > 1e-6, "The stroke centre should have been written.");
    }

    [Fact]
    public void Rasterize_DirtyRectangleOffTheMesh_WritesNothing()
    {
        (double[] vertices, int vertexCount, int[] faces, int faceCount) = Grid(21, 5.0);
        var baseZ = new double[vertexCount];
        var field = new SculptDisplacementField(CellSize);

        SculptFieldRasterizer.Rasterize(new IndexedTriMesh(vertices, vertexCount, faces, faceCount), baseZ, field, dirtyMinX: 400.0, dirtyMaxX: 404.0, dirtyMinY: 400.0, dirtyMaxY: 404.0);

        Assert.True(field.IsEmpty);
    }

    [Fact]
    public void Rasterize_RecordsTheUnmaskedDisplacementUnderAFeatheredMask()
    {
        // Inside the feather ramp the working mesh carries influence x delta, so the field must record
        // delta itself — the divide happens per vertex, which localization must not disturb.
        (double[] vertices, int vertexCount, int[] faces, int faceCount) = Grid(81, 20.0);
        var baseZ = new double[vertexCount];

        var mask = new SculptConstraintMask(featherDistance: 2.0);
        mask.AddPolygon(new double[] { 0.0, 0.0, 4.0, 0.0, 4.0, 4.0, 0.0, 4.0 }, 4);

        for (int i = 0; i < vertexCount; i++)
        {
            double x = vertices[i * 3];
            double y = vertices[(i * 3) + 1];
            vertices[(i * 3) + 2] = 2.0 * mask.EvaluateInfluence(x, y);
        }

        var field = new SculptDisplacementField(CellSize);
        SculptFieldRasterizer.Rasterize(new IndexedTriMesh(vertices, vertexCount, faces, faceCount), baseZ, field, dirtyMinX: 5.0, dirtyMaxX: 15.0, dirtyMinY: 5.0, dirtyMaxY: 15.0, constraintMask: mask);

        // Well outside the feather, influence is 1 and the raw delta is the full 2.0.
        Assert.Equal(2.0f, field.GetSample(48, 48), 3);
    }

    private static SculptDisplacementField CommitBump(double sheetSize, int resolution)
    {
        (double[] vertices, int vertexCount, int[] faces, int faceCount) = Grid(resolution, sheetSize);
        var baseZ = new double[vertexCount];
        Bump(vertices, vertexCount, 5.0, 5.0, 1.5, 2.0);

        var field = new SculptDisplacementField(CellSize);
        SculptFieldRasterizer.Rasterize(new IndexedTriMesh(vertices, vertexCount, faces, faceCount), baseZ, field, dirtyMinX: 3.5, dirtyMaxX: 6.5, dirtyMinY: 3.5, dirtyMaxY: 6.5);
        return field;
    }

    private static void AssertSameSamples(SculptDisplacementField expected, SculptDisplacementField actual)
    {
        for (int gj = 10; gj <= 30; gj++)
        {
            for (int gi = 10; gi <= 30; gi++)
                Assert.Equal(expected.GetSample(gi, gj), actual.GetSample(gi, gj), 5);
        }
    }

    private static void Bump(double[] vertices, int vertexCount, double cx, double cy, double radius, double height)
    {
        for (int i = 0; i < vertexCount; i++)
        {
            double dx = vertices[i * 3] - cx;
            double dy = vertices[(i * 3) + 1] - cy;
            double d = Math.Sqrt((dx * dx) + (dy * dy)) / radius;
            vertices[(i * 3) + 2] += height * SculptFalloffs.Evaluate(SculptFalloff.Smooth, d);
        }
    }

    /// <summary>Regular triangulated sheet over [0, size]^2 with (n x n) vertices, spacing size/(n-1).</summary>
    private static (double[] Vertices, int VertexCount, int[] Faces, int FaceCount) Grid(int n, double size)
    {
        double spacing = size / (n - 1);
        int vertexCount = n * n;
        var vertices = new double[vertexCount * 3];
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                int v = (j * n) + i;
                vertices[v * 3] = i * spacing;
                vertices[(v * 3) + 1] = j * spacing;
                vertices[(v * 3) + 2] = 0.0;
            }
        }

        int faceCount = (n - 1) * (n - 1) * 2;
        var faces = new int[faceCount * 3];
        int f = 0;
        for (int j = 0; j < n - 1; j++)
        {
            for (int i = 0; i < n - 1; i++)
            {
                int v00 = (j * n) + i;
                int v10 = v00 + 1;
                int v01 = v00 + n;
                int v11 = v01 + 1;
                faces[f++] = v00; faces[f++] = v10; faces[f++] = v11;
                faces[f++] = v00; faces[f++] = v11; faces[f++] = v01;
            }
        }

        return (vertices, vertexCount, faces, faceCount);
    }
}
