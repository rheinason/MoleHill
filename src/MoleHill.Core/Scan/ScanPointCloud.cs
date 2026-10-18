using MoleHill.Core.Engine;

namespace MoleHill.Core.Scan;

/// <summary>Captured cloud arrays. Optional classes use LAS codes (2 ground, 3–5 vegetation, 6 building).
/// Rasterisation bins observations directly; it never invents triangles between the source points.</summary>
public sealed class ScanPointCloud
{
    public ScanPointCloud(double[] xyz, int[]? colours = null, byte[]? classes = null)
    {
        ArgumentNullException.ThrowIfNull(xyz);
        if (xyz.Length % 3 != 0 || xyz.Any(value => !double.IsFinite(value)))
            throw new ArgumentException("Cloud coordinates must be finite XYZ triples.", nameof(xyz));
        if (colours != null && (colours.Length != xyz.Length / 3 || colours.Any(value => value < -1 || value > 0xffffff)))
            throw new ArgumentException("Cloud colours must align with points and be RGB or -1.", nameof(colours));
        if (classes != null && classes.Length != xyz.Length / 3)
            throw new ArgumentException("Cloud classes must align with points.", nameof(classes));
        Xyz = xyz; Colours = colours; Classes = classes;
    }

    public double[] Xyz { get; }
    public int[]? Colours { get; }
    public byte[]? Classes { get; }
    public int Count => Xyz.Length / 3;

    public sealed record Raster(ScanGrid Top, ScanGrid Lowest, ScanGrid ClassifiedGround, int[]? Colours, byte[]? Classes)
    {
        /// <summary>Only observed four-corner cells become roof-fit triangles; gaps are not bridged.</summary>
        public IndexedTriMesh RoofMesh(CancellationToken cancellationToken)
        {
            var vertices = new List<double>(); var faces = new List<int>();
            var map = new int[Top.NodeCount]; Array.Fill(map, -1);
            for (int row = 0; row + 1 < Top.Rows; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int col = 0; col + 1 < Top.Columns; col++)
                {
                    int a = row * Top.Columns + col, b = a + 1, c = b + Top.Columns, d = a + Top.Columns;
                    if (!double.IsFinite(Top.Values[a]) || !double.IsFinite(Top.Values[b]) ||
                        !double.IsFinite(Top.Values[c]) || !double.IsFinite(Top.Values[d]))
                        continue;
                    int va = Vertex(a), vb = Vertex(b), vc = Vertex(c), vd = Vertex(d);
                    faces.Add(va); faces.Add(vb); faces.Add(vc);
                    faces.Add(va); faces.Add(vc); faces.Add(vd);
                }
            }
            return IndexedTriMesh.FromArrays(vertices.ToArray(), faces.ToArray());

            int Vertex(int node)
            {
                if (map[node] >= 0) return map[node];
                map[node] = vertices.Count / 3;
                vertices.Add(Top.NodeX(node % Top.Columns)); vertices.Add(Top.NodeY(node / Top.Columns)); vertices.Add(Top.Values[node]);
                return map[node];
            }
        }
    }

    public double AutomaticCellSize()
    {
        if (Count == 0) return 1;
        var (minX, minY, maxX, maxY) = Extent();
        // Four observations per bin on average. Guard long, thin extents as well as area.
        double cell = Math.Max(Math.Sqrt(Math.Max((maxX - minX) * (maxY - minY), 1e-12) * 4 / Count),
            Math.Max(maxX - minX, maxY - minY) / (ScanRasterizer.MaximumNodeCount - 1));
        while ((Math.Floor((maxX - minX) / cell) + 2) * (Math.Floor((maxY - minY) / cell) + 2) > ScanRasterizer.MaximumNodeCount)
            cell *= 1.5;
        return cell;
    }

    public Raster Rasterize(double cell, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(cell) || cell <= 0) throw new ArgumentOutOfRangeException(nameof(cell));
        cancellationToken.ThrowIfCancellationRequested();
        if (Count == 0)
        {
            var empty = new ScanGrid(0, 0, cell, 0, 0, Array.Empty<double>());
            return new(empty, empty, empty, null, null);
        }
        var (minX, minY, maxX, maxY) = Extent();
        double cols = Math.Floor((maxX - minX) / cell + .5) + 1, rows = Math.Floor((maxY - minY) / cell + .5) + 1;
        if (!double.IsFinite(cols * rows) || cols * rows > ScanRasterizer.MaximumNodeCount)
            throw new ArgumentOutOfRangeException(nameof(cell), "Cloud raster exceeds the 16 million node limit; use a larger cell.");
        int columns = (int)cols, rowCount = (int)rows, count = checked(columns * rowCount);
        var top = new double[count]; var low = new double[count]; var ground = new double[count];
        Array.Fill(top, double.NaN); Array.Fill(low, double.NaN); Array.Fill(ground, double.NaN);
        int[]? rgb = Colours == null ? null : new int[count]; if (rgb != null) Array.Fill(rgb, -1);
        byte[]? classes = Classes == null ? null : new byte[count];
        for (int p = 0; p < Count; p++)
        {
            if ((p & 16383) == 0) cancellationToken.ThrowIfCancellationRequested();
            int x = (int)Math.Floor((Xyz[p * 3] - minX) / cell + .5), y = (int)Math.Floor((Xyz[p * 3 + 1] - minY) / cell + .5);
            int node = y * columns + x; double z = Xyz[p * 3 + 2];
            if (double.IsNaN(top[node]) || z > top[node])
            {
                top[node] = z;
                if (rgb != null) rgb[node] = Colours![p];
                if (classes != null) classes[node] = Classes![p];
            }
            if (double.IsNaN(low[node]) || z < low[node]) low[node] = z;
            if (Classes?[p] == 2 && (double.IsNaN(ground[node]) || z < ground[node])) ground[node] = z;
        }
        ScanGrid Grid(double[] heights) => new(minX, minY, cell, columns, rowCount, heights);
        return new(Grid(top), Grid(low), Grid(ground), rgb, classes);
    }

    private (double MinX, double MinY, double MaxX, double MaxY) Extent()
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        for (int i = 0; i < Count; i++)
        {
            minX = Math.Min(minX, Xyz[i * 3]); maxX = Math.Max(maxX, Xyz[i * 3]);
            minY = Math.Min(minY, Xyz[i * 3 + 1]); maxY = Math.Max(maxY, Xyz[i * 3 + 1]);
        }
        return (minX, minY, maxX, maxY);
    }
}
