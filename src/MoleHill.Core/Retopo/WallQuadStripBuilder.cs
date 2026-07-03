namespace MoleHill.Core.Retopo;

/// <summary>
/// Builds a retaining wall as a clean strip of quads between its paired <b>top</b> (crest) and <b>toe</b>
/// (base) rails — the part the 2.5D heightfield retopo can't represent, since a near-vertical wall is a sliver
/// in plan. Columns follow the rail stations; rows run up the wall (one per ~edge length), so the crest and
/// toe read as edge loops and the wall stays crisp under subdivision.
/// </summary>
public static class WallQuadStripBuilder
{
    public sealed class Strip
    {
        public double[] Vertices { get; init; } = Array.Empty<double>();

        public int[] Quads { get; init; } = Array.Empty<int>();

        public int VertexCount => Vertices.Length / 3;

        public int QuadCount => Quads.Length / 4;
    }

    /// <param name="top">Crest rail, flat XYZ.</param>
    /// <param name="toe">Toe rail, flat XYZ; station-corresponding to <paramref name="top"/>.</param>
    /// <param name="closed">Whether the rails form a closed loop (wraps the last column back to the first).</param>
    /// <param name="edgeLength">Target quad size; sets the number of rows up the wall.</param>
    public static Strip Build(double[] top, double[] toe, bool closed, double edgeLength)
    {
        int columns = Math.Min(top.Length / 3, toe.Length / 3);
        if (columns < 2)
            return new Strip();

        double heightSum = 0.0;
        for (int k = 0; k < columns; k++)
        {
            double dx = top[k * 3] - toe[k * 3];
            double dy = top[k * 3 + 1] - toe[k * 3 + 1];
            double dz = top[k * 3 + 2] - toe[k * 3 + 2];
            heightSum += Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        }

        double averageHeight = heightSum / columns;
        int rows = Math.Max(1, (int)Math.Ceiling(averageHeight / Math.Max(edgeLength, 1e-6)));
        int perColumn = rows + 1;

        var vertices = new double[columns * perColumn * 3];
        for (int k = 0; k < columns; k++)
        {
            double ox = toe[k * 3], oy = toe[k * 3 + 1], oz = toe[k * 3 + 2];
            double tx = top[k * 3], ty = top[k * 3 + 1], tz = top[k * 3 + 2];
            for (int r = 0; r <= rows; r++)
            {
                double s = (double)r / rows; // r = 0 → toe rail, r = rows → top rail
                int idx = ((k * perColumn) + r) * 3;
                vertices[idx] = ox + ((tx - ox) * s);
                vertices[idx + 1] = oy + ((ty - oy) * s);
                vertices[idx + 2] = oz + ((tz - oz) * s);
            }
        }

        // Wrap the last column to the first only when closed AND the endpoints don't already coincide.
        bool wrap = closed && !EndpointsCoincide(top, toe, columns);
        int lastColumn = wrap ? columns : columns - 1;

        var quads = new List<int>(lastColumn * rows * 4);
        for (int k = 0; k < lastColumn; k++)
        {
            int k1 = (k + 1) % columns;
            for (int r = 0; r < rows; r++)
            {
                quads.Add((k * perColumn) + r);
                quads.Add((k1 * perColumn) + r);
                quads.Add((k1 * perColumn) + r + 1);
                quads.Add((k * perColumn) + r + 1);
            }
        }

        return new Strip { Vertices = vertices, Quads = quads.ToArray() };
    }

    private static bool EndpointsCoincide(double[] top, double[] toe, int columns)
    {
        int last = columns - 1;
        return Near(top, 0, last) && Near(toe, 0, last);

        static bool Near(double[] p, int i, int j)
        {
            double dx = p[i * 3] - p[j * 3];
            double dy = p[i * 3 + 1] - p[j * 3 + 1];
            double dz = p[i * 3 + 2] - p[j * 3 + 2];
            return ((dx * dx) + (dy * dy) + (dz * dz)) < 1e-12;
        }
    }
}
