using MoleHill.Core.Engine;
using MoleHill.Core.Geometry;

namespace MoleHill.Core.Scan;

/// <summary>
/// Rasterises a scan's triangles (not its vertices) onto a plan lattice: the highest surface over each node
/// (the DSM) and the lowest. Rasterising vertices left 40% of 0.5 m cells empty on the reference scan;
/// every triangle covers the nodes inside it in plan, so a face-up surface leaves no gaps. Walls and other
/// near-vertical faces cover almost nothing in plan and do not need to.
/// </summary>
public static class ScanRasterizer
{
    /// <summary>The most nodes a raster may have; a finer cell on a large scan is refused.</summary>
    public const long MaximumNodeCount = 16_000_000;

    public readonly record struct Rasters(ScanGrid Top, ScanGrid Lowest);

    /// <summary>
    /// A cell about one triangle's side: the square root of plan extent over face count, which on the
    /// reference scan (13 triangles per m²) gives 0.28 m. Finer only splits triangles' interiors; coarser
    /// loses the steps the later stages look for. Never so fine that the raster passes
    /// <see cref="MaximumNodeCount"/>.
    /// </summary>
    public static double AutomaticCellSize(IndexedTriMesh mesh)
    {
        if (mesh.IsEmpty)
            return 1.0;
        (double minX, double minY, double maxX, double maxY) = PlanExtent(mesh);
        double area = Math.Max((maxX - minX) * (maxY - minY), 1e-12);
        double cell = Math.Sqrt(area / Math.Max(mesh.FaceCount, 1));
        cell = Math.Max(cell, Math.Sqrt(area / (MaximumNodeCount * 0.9)));
        while ((Math.Floor((maxX - minX) / cell) + 1) * (Math.Floor((maxY - minY) / cell) + 1) > MaximumNodeCount)
            cell *= 2.0;
        return cell;
    }

    public static Rasters Rasterize(IndexedTriMesh mesh, double cellSize, CancellationToken cancellationToken = default)
    {
        if (!(cellSize > 0.0) || !double.IsFinite(cellSize))
            throw new ArgumentOutOfRangeException(nameof(cellSize), "Cell size must be positive.");
        cancellationToken.ThrowIfCancellationRequested();

        (double minX, double minY, double maxX, double maxY) = PlanExtent(mesh);
        if (mesh.IsEmpty || !(maxX >= minX) || !(maxY >= minY))
        {
            var empty = new ScanGrid(0.0, 0.0, cellSize, 0, 0, Array.Empty<double>());
            return new Rasters(empty, empty);
        }

        // Check in floating point before casting: a tiny cell or a very long, thin scan can overflow
        // either dimension even when an area-only estimate looked safe.
        double columnCount = Math.Floor((maxX - minX) / cellSize) + 1;
        double rowCount = Math.Floor((maxY - minY) / cellSize) + 1;
        if (!double.IsFinite(columnCount * rowCount) || columnCount * rowCount > MaximumNodeCount)
            throw new ArgumentOutOfRangeException(nameof(cellSize), $"A {cellSize:G4} cell exceeds the raster limit of {MaximumNodeCount:N0} nodes.");
        int columns = (int)columnCount;
        int rows = (int)rowCount;

        var top = new double[columns * rows];
        var lowest = new double[columns * rows];
        Array.Fill(top, double.NaN);
        Array.Fill(lowest, double.NaN);
        double[] vertices = mesh.Vertices;
        int[] faces = mesh.Faces;
        double inverse = 1.0 / cellSize;
        const double edgeTolerance = -1e-9;
        for (int face = 0; face < mesh.FaceCount; face++)
        {
            if ((face & 0x3FFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            int a = faces[face * 3], b = faces[(face * 3) + 1], c = faces[(face * 3) + 2];
            double ax = vertices[a * 3], ay = vertices[(a * 3) + 1], az = vertices[(a * 3) + 2];
            double bx = vertices[b * 3], by = vertices[(b * 3) + 1], bz = vertices[(b * 3) + 2];
            double cx = vertices[c * 3], cy = vertices[(c * 3) + 1], cz = vertices[(c * 3) + 2];
            double area = Geometry2D.Orient(ax, ay, bx, by, cx, cy);
            double scale = Math.Max(Math.Max(Math.Abs(bx - ax), Math.Abs(by - ay)), Math.Max(Math.Abs(cx - ax), Math.Abs(cy - ay)));
            if (!(Math.Abs(area) > scale * scale * 1e-12))
                continue;

            int c0 = Math.Max(0, (int)Math.Ceiling(((Math.Min(ax, Math.Min(bx, cx)) - minX) * inverse) - 1e-9));
            int c1 = Math.Min(columns - 1, (int)Math.Floor(((Math.Max(ax, Math.Max(bx, cx)) - minX) * inverse) + 1e-9));
            int r0 = Math.Max(0, (int)Math.Ceiling(((Math.Min(ay, Math.Min(by, cy)) - minY) * inverse) - 1e-9));
            int r1 = Math.Min(rows - 1, (int)Math.Floor(((Math.Max(ay, Math.Max(by, cy)) - minY) * inverse) + 1e-9));
            if (c0 > c1 || r0 > r1)
                continue;

            double inverseArea = 1.0 / area;
            for (int row = r0; row <= r1; row++)
            {
                double y = minY + (row * cellSize);
                int offset = row * columns;
                for (int column = c0; column <= c1; column++)
                {
                    double x = minX + (column * cellSize);
                    double wa = Geometry2D.Orient(bx, by, cx, cy, x, y) * inverseArea;
                    if (wa < edgeTolerance)
                        continue;
                    double wb = Geometry2D.Orient(cx, cy, ax, ay, x, y) * inverseArea;
                    if (wb < edgeTolerance)
                        continue;
                    double wc = 1.0 - wa - wb;
                    if (wc < edgeTolerance)
                        continue;

                    double z = (wa * az) + (wb * bz) + (wc * cz);
                    ref double high = ref top[offset + column];
                    if (double.IsNaN(high) || z > high)
                        high = z;
                    ref double low = ref lowest[offset + column];
                    if (double.IsNaN(low) || z < low)
                        low = z;
                }
            }
        }

        return new Rasters(
            new ScanGrid(minX, minY, cellSize, columns, rows, top),
            new ScanGrid(minX, minY, cellSize, columns, rows, lowest));
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) PlanExtent(IndexedTriMesh mesh)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        double[] vertices = mesh.Vertices;
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            minX = Math.Min(minX, vertices[v * 3]);
            maxX = Math.Max(maxX, vertices[v * 3]);
            minY = Math.Min(minY, vertices[(v * 3) + 1]);
            maxY = Math.Max(maxY, vertices[(v * 3) + 1]);
        }

        return (minX, minY, maxX, maxY);
    }
}
