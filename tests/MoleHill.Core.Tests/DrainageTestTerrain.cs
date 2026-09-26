namespace MoleHill.Core.Tests;

/// <summary>
/// A grid terrain with the hosted lane's analysis-heavy surface: a rolling sinusoid plus smoothed value
/// noise, which gives the drainage analyses real depressions to find. Deterministic. Used both to profile
/// the drainage stages without Rhino and to check that an optimisation leaves their output unchanged.
/// </summary>
internal static class DrainageTestTerrain
{
    public sealed record Mesh(double[] Vertices, int VertexCount, int[] Faces, int FaceCount);

    public static Mesh Create(int side, double noiseCell = 2.5, double noiseAmplitude = 0.4)
    {
        var vertices = new double[side * side * 3];
        for (int x = 0; x < side; x++)
        {
            for (int y = 0; y < side; y++)
            {
                int v = (x * side) + y;
                vertices[v * 3] = x;
                vertices[(v * 3) + 1] = y;
                vertices[(v * 3) + 2] = Elevation(x, y, noiseCell, noiseAmplitude);
            }
        }

        int cells = side - 1;
        var faces = new int[cells * cells * 6];
        int f = 0;
        for (int x = 0; x < cells; x++)
        {
            for (int y = 0; y < cells; y++)
            {
                int a = (x * side) + y;
                int b = ((x + 1) * side) + y;
                int c = ((x + 1) * side) + y + 1;
                int d = (x * side) + y + 1;
                // Alternate the diagonal so the mesh has no directional bias.
                if (((x + y) & 1) == 0)
                {
                    faces[f++] = a; faces[f++] = b; faces[f++] = c;
                    faces[f++] = a; faces[f++] = c; faces[f++] = d;
                }
                else
                {
                    faces[f++] = a; faces[f++] = b; faces[f++] = d;
                    faces[f++] = b; faces[f++] = c; faces[f++] = d;
                }
            }
        }

        return new Mesh(vertices, side * side, faces, faces.Length / 3);
    }

    private static double Elevation(double x, double y, double noiseCell, double noiseAmplitude)
    {
        double gx = x / noiseCell;
        double gy = y / noiseCell;
        int ix = (int)Math.Floor(gx);
        int iy = (int)Math.Floor(gy);
        double fx = gx - ix;
        double fy = gy - iy;
        double sx = fx * fx * (3 - 2 * fx);
        double sy = fy * fy * (3 - 2 * fy);
        double a = Lattice(ix, iy), b = Lattice(ix + 1, iy), c = Lattice(ix, iy + 1), d = Lattice(ix + 1, iy + 1);
        double noise = a + (b - a) * sx + (c - a) * sy + (a - b - c + d) * sx * sy;
        return 3.0 * Math.Sin(x * 0.05) + 2.0 * Math.Cos(y * 0.07) + noiseAmplitude * noise;
    }

    private static double Lattice(int x, int y)
    {
        unchecked
        {
            uint h = (uint)x * 374761393u + (uint)y * 668265263u;
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return h / (double)uint.MaxValue * 2.0 - 1.0;
        }
    }
}
