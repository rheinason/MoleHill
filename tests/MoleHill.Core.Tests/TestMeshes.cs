namespace MoleHill.Core.Tests;

/// <summary>
/// Regular grid meshes in the flat array format, shared by tests that need a plain terrain to run an
/// algorithm over. Vertices are row-major (<c>y * cols + x</c>); every cell is split along the
/// lower-left to upper-right diagonal, wound <c>a,b,d</c> then <c>a,d,c</c>, which is the layout the
/// per-file copies this replaced all used. A test needing another layout builds its own.
/// </summary>
internal static class TestMeshes
{
    /// <summary>
    /// Flat XYZ vertices of a <paramref name="cols"/> by <paramref name="rows"/> grid at
    /// <paramref name="spacing"/>. <paramref name="z"/> receives world X and Y; it defaults to a plane at 0.
    /// </summary>
    public static double[] GridVertices(int cols, int rows, double spacing = 1.0, Func<double, double, double>? z = null)
    {
        var vertices = new double[cols * rows * 3];
        int index = 0;
        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < cols; x++)
            {
                double worldX = x * spacing;
                double worldY = y * spacing;
                vertices[index++] = worldX;
                vertices[index++] = worldY;
                vertices[index++] = z == null ? 0.0 : z(worldX, worldY);
            }
        }

        return vertices;
    }

    /// <summary>
    /// Triangles for a <paramref name="cols"/> by <paramref name="rows"/> vertex grid. With
    /// <paramref name="flipDiagonal"/> each cell is split along the other diagonal
    /// (<c>a,b,d</c> then <c>b,c,d</c> with <c>d</c> the upper-left corner).
    /// </summary>
    public static int[] GridFaces(int cols, int rows, bool flipDiagonal = false)
    {
        var faces = new int[(cols - 1) * (rows - 1) * 6];
        int next = 0;
        for (int y = 0; y < rows - 1; y++)
        {
            for (int x = 0; x < cols - 1; x++)
            {
                int a = (y * cols) + x;
                int b = a + 1;
                int c = a + cols;
                int d = c + 1;
                if (flipDiagonal)
                {
                    faces[next++] = a; faces[next++] = b; faces[next++] = c;
                    faces[next++] = b; faces[next++] = d; faces[next++] = c;
                }
                else
                {
                    faces[next++] = a; faces[next++] = b; faces[next++] = d;
                    faces[next++] = a; faces[next++] = d; faces[next++] = c;
                }
            }
        }

        return faces;
    }

    /// <summary>
    /// A square grid of <paramref name="vertexSide"/> vertices a side with the counts the Core engines take
    /// alongside their arrays.
    /// </summary>
    public static void Grid(
        int vertexSide,
        Func<double, double, double>? z,
        out double[] vertices,
        out int vertexCount,
        out int[] faces,
        out int faceCount,
        double spacing = 1.0)
    {
        vertices = GridVertices(vertexSide, vertexSide, spacing, z);
        vertexCount = vertexSide * vertexSide;
        faces = GridFaces(vertexSide, vertexSide);
        faceCount = faces.Length / 3;
    }
}
