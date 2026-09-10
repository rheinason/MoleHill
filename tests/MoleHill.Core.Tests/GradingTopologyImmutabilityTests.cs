using MoleHill.Core.Grading;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// The retained grading topology is shared by reference between the UI-owned runtime cache, worker
/// copies, and the stage that reads it — no defensive deep copy on the build path. That is only sound
/// while its consumers treat the arrays as read-only, which is what these pin. If any of these start
/// failing, the sharing in <c>TerrainBuildService.ApplyGradePad</c> has to go back to copying.
/// </summary>
public class GradingTopologyImmutabilityTests
{
    [Fact]
    public void ApplyGradingZ_DoesNotWriteIntoTheTopologyVertices()
    {
        (double[] vertices, int[] faces) = Sheet(12, 4.0);
        var original = (double[])vertices.Clone();
        PadGrader.PadBoundary[] pads = { Pad(1.0, 1.0, 3.0, 3.0, elevation: 5.0) };

        double[] graded = PadGrader.ApplyGradingZ(vertices, vertices.Length / 3, faces, faces.Length / 3, pads);

        Assert.Equal(original, vertices);
        Assert.NotSame(vertices, graded);
    }

    [Fact]
    public void ApplyGradingZ_WithNoPads_DoesNotWriteIntoTheTopologyVertices()
    {
        (double[] vertices, int[] faces) = Sheet(8, 4.0);
        var original = (double[])vertices.Clone();

        double[] graded = PadGrader.ApplyGradingZ(
            vertices,
            vertices.Length / 3,
            faces,
            faces.Length / 3,
            Array.Empty<PadGrader.PadBoundary>());

        Assert.Equal(original, vertices);
        Assert.NotSame(vertices, graded);
    }

    [Fact]
    public void ApplyGradingZ_VertexOnlyOverload_DoesNotWriteIntoTheTopologyVertices()
    {
        (double[] vertices, _) = Sheet(10, 4.0);
        var original = (double[])vertices.Clone();
        PadGrader.PadBoundary[] pads = { Pad(1.0, 1.0, 3.0, 3.0, elevation: -2.0) };

        double[] graded = PadGrader.ApplyGradingZ(vertices, vertices.Length / 3, pads);

        Assert.Equal(original, vertices);
        Assert.NotSame(vertices, graded);
    }

    [Fact]
    public void ApplyGradingZ_DoesNotWriteIntoTheTopologyFaces()
    {
        (double[] vertices, int[] faces) = Sheet(12, 4.0);
        var original = (int[])faces.Clone();
        PadGrader.PadBoundary[] pads = { Pad(1.0, 1.0, 3.0, 3.0, elevation: 5.0) };

        PadGrader.ApplyGradingZ(vertices, vertices.Length / 3, faces, faces.Length / 3, pads);

        Assert.Equal(original, faces);
    }

    [Fact]
    public void ApplyGradingZ_RepeatedOnTheSameArrays_ReturnsTheSameResult()
    {
        // The decisive property for sharing: reusing one retained topology across builds must not drift,
        // which it would if a previous call had left anything behind in the input.
        (double[] vertices, int[] faces) = Sheet(12, 4.0);
        PadGrader.PadBoundary[] pads = { Pad(1.0, 1.0, 3.0, 3.0, elevation: 5.0) };

        double[] first = PadGrader.ApplyGradingZ(vertices, vertices.Length / 3, faces, faces.Length / 3, pads);
        double[] second = PadGrader.ApplyGradingZ(vertices, vertices.Length / 3, faces, faces.Length / 3, pads);
        double[] third = PadGrader.ApplyGradingZ(vertices, vertices.Length / 3, faces, faces.Length / 3, pads);

        Assert.Equal(first, second);
        Assert.Equal(first, third);
    }

    private static PadGrader.PadBoundary Pad(double minX, double minY, double maxX, double maxY, double elevation)
    {
        return new PadGrader.PadBoundary(
            new[] { minX, minY, maxX, minY, maxX, maxY, minX, maxY },
            4,
            elevation,
            3.0,
            3.0);
    }

    private static (double[] Vertices, int[] Faces) Sheet(int n, double size)
    {
        double spacing = size / (n - 1);
        var vertices = new double[n * n * 3];
        for (int j = 0; j < n; j++)
        {
            for (int i = 0; i < n; i++)
            {
                int v = (j * n) + i;
                vertices[v * 3] = i * spacing;
                vertices[(v * 3) + 1] = j * spacing;
                vertices[(v * 3) + 2] = 0.1 * i;
            }
        }

        var faces = new int[(n - 1) * (n - 1) * 6];
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

        return (vertices, faces);
    }
}
