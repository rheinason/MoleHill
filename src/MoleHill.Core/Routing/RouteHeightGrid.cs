using MoleHill.Core.Engine;
using MoleHill.Core.Geometry;

namespace MoleHill.Core.Routing;

/// <summary>
/// The terrain as the route search sees it: ground heights sampled on a regular plan grid, NaN where
/// there is no terrain (outside the mesh, or a hole in it).
/// </summary>
/// <remarks>
/// Built by rasterising the triangles directly — every face visits only the grid nodes inside its plan
/// bounding box — rather than by projecting each node, so the cost is one pass over faces plus one over
/// nodes. Where faces overlap in plan (an overhang, the two sides of a near-vertical wall) the node takes
/// the highest one: a route walks on the top surface. Near-vertical and degenerate faces cover no node.
/// </remarks>
public sealed class RouteHeightGrid
{
    /// <summary>Above this many nodes the automatic cell size coarsens rather than refines.</summary>
    public const int TargetNodeCount = 1_000_000;

    /// <summary>Cells across the routed distance at the automatic size.</summary>
    public const double SpanCells = 200.0;

    /// <summary>Hard ceiling, applied to an explicit cell size as well, so a typo cannot exhaust memory.</summary>
    public const int MaximumNodeCount = 6_000_000;

    private readonly double[] _heights;

    private RouteHeightGrid(double originX, double originY, double cellSize, int columns, int rows, double[] heights)
    {
        OriginX = originX;
        OriginY = originY;
        CellSize = cellSize;
        Columns = columns;
        Rows = rows;
        _heights = heights;
    }

    public double OriginX { get; }

    public double OriginY { get; }

    public double CellSize { get; }

    public int Columns { get; }

    public int Rows { get; }

    public int NodeCount => Columns * Rows;

    /// <summary>Height of node (column, row); NaN when the node is off the terrain.</summary>
    public double Height(int column, int row) => _heights[(row * Columns) + column];

    internal double HeightAt(int node) => _heights[node];

    internal double NodeX(int column) => OriginX + (column * CellSize);

    internal double NodeY(int row) => OriginY + (row * CellSize);

    /// <summary>
    /// The cell size the search uses when the user has not chosen one: <see cref="SpanCells"/> cells across
    /// the distance being routed (1.3 m for a 250 m route), or coarser where <see cref="TargetNodeCount"/>
    /// nodes would not cover the domain. It used to be a thousandth of the distance, as fine as a million
    /// nodes allowed: a 250 m route searched at 0.43 m came out as staircases of sub-metre segments that
    /// no plan simplification could make into a line a designer would draw (2026-10-16).
    /// </summary>
    public static double AutomaticCellSize(double domainWidth, double domainHeight, double routeSpan)
    {
        double area = Math.Max(domainWidth, 0.0) * Math.Max(domainHeight, 0.0);
        double byCount = area > 0.0 ? Math.Sqrt(area / TargetNodeCount) : 0.0;
        double bySpan = routeSpan > 0.0 ? routeSpan / SpanCells : 0.0;
        double cell = Math.Max(byCount, bySpan);
        return cell > 0.0 && double.IsFinite(cell) ? cell : 1.0;
    }

    /// <summary>The smallest cell size that keeps the domain within <see cref="MaximumNodeCount"/>.</summary>
    public static double MinimumCellSize(double domainWidth, double domainHeight)
    {
        double area = Math.Max(domainWidth, 0.0) * Math.Max(domainHeight, 0.0);
        return area > 0.0 ? Math.Sqrt(area / MaximumNodeCount) * 1.001 : 0.0;
    }

    /// <summary>
    /// Samples <paramref name="mesh"/> over the plan rectangle on a grid of <paramref name="cellSize"/>.
    /// The rectangle is clipped to the mesh's own plan extent, since nothing outside it can be ground.
    /// </summary>
    public static RouteHeightGrid FromMesh(
        IndexedTriMesh mesh,
        double minX,
        double minY,
        double maxX,
        double maxY,
        double cellSize,
        CancellationToken cancellationToken = default)
    {
        if (!(cellSize > 0.0) || !double.IsFinite(cellSize))
            throw new ArgumentOutOfRangeException(nameof(cellSize), "Cell size must be positive.");

        double[] vertices = mesh.Vertices;
        int[] faces = mesh.Faces;
        double meshMinX = double.MaxValue, meshMinY = double.MaxValue;
        double meshMaxX = double.MinValue, meshMaxY = double.MinValue;
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            meshMinX = Math.Min(meshMinX, vertices[v * 3]);
            meshMaxX = Math.Max(meshMaxX, vertices[v * 3]);
            meshMinY = Math.Min(meshMinY, vertices[(v * 3) + 1]);
            meshMaxY = Math.Max(meshMaxY, vertices[(v * 3) + 1]);
        }

        minX = Math.Max(minX, meshMinX);
        minY = Math.Max(minY, meshMinY);
        maxX = Math.Min(maxX, meshMaxX);
        maxY = Math.Min(maxY, meshMaxY);
        if (mesh.IsEmpty || maxX < minX || maxY < minY)
            return new RouteHeightGrid(minX, minY, cellSize, 0, 0, Array.Empty<double>());

        int columns = (int)Math.Floor((maxX - minX) / cellSize) + 1;
        int rows = (int)Math.Floor((maxY - minY) / cellSize) + 1;
        if ((long)columns * rows > MaximumNodeCount)
            throw new ArgumentOutOfRangeException(nameof(cellSize), "Cell size is too fine for the routing domain.");

        var heights = new double[columns * rows];
        Array.Fill(heights, double.NaN);
        double inverse = 1.0 / cellSize;

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
            const double edgeTolerance = -1e-9;
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
                    ref double slot = ref heights[offset + column];
                    if (double.IsNaN(slot) || z > slot)
                        slot = z;
                }
            }
        }

        return new RouteHeightGrid(minX, minY, cellSize, columns, rows, heights);
    }

    /// <summary>A grid from a height function — test terrains and analytic surfaces. NaN means no ground.</summary>
    public static RouteHeightGrid FromFunction(
        double originX,
        double originY,
        double cellSize,
        int columns,
        int rows,
        Func<double, double, double> height)
    {
        var heights = new double[columns * rows];
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
                heights[(row * columns) + column] = height(originX + (column * cellSize), originY + (row * cellSize));
        }

        return new RouteHeightGrid(originX, originY, cellSize, columns, rows, heights);
    }

    /// <summary>
    /// Bilinear ground height at a plan point. Off-terrain corners are left out and the remaining
    /// weights renormalised, so a point beside a hole still reads the ground it stands on; false only
    /// when the point is outside the grid or every surrounding node is off the terrain.
    /// </summary>
    public bool TrySample(double x, double y, out double z) => TrySample(x, y, 1e-9, out z);

    /// <summary>
    /// <see cref="TrySample(double, double, out double)"/>, failing unless on-terrain corners carry at
    /// least <paramref name="minimumWeight"/> of the bilinear weight. At 0.5 a point is "on the ground"
    /// along any edge between two on-terrain nodes, and off it once it is nearer the hole than the ground.
    /// </summary>
    internal bool TrySample(double x, double y, double minimumWeight, out double z)
    {
        z = double.NaN;
        if (Columns == 0 || Rows == 0)
            return false;

        double u = (x - OriginX) / CellSize;
        double v = (y - OriginY) / CellSize;
        if (u < -1e-9 || v < -1e-9 || u > Columns - 1 + 1e-9 || v > Rows - 1 + 1e-9)
            return false;

        int c0 = Math.Clamp((int)Math.Floor(u), 0, Math.Max(0, Columns - 2));
        int r0 = Math.Clamp((int)Math.Floor(v), 0, Math.Max(0, Rows - 2));
        int c1 = Math.Min(c0 + 1, Columns - 1);
        int r1 = Math.Min(r0 + 1, Rows - 1);
        double fu = Math.Clamp(u - c0, 0.0, 1.0);
        double fv = Math.Clamp(v - r0, 0.0, 1.0);

        double sum = 0.0, weight = 0.0;
        Accumulate(Height(c0, r0), (1.0 - fu) * (1.0 - fv), ref sum, ref weight);
        Accumulate(Height(c1, r0), fu * (1.0 - fv), ref sum, ref weight);
        Accumulate(Height(c0, r1), (1.0 - fu) * fv, ref sum, ref weight);
        Accumulate(Height(c1, r1), fu * fv, ref sum, ref weight);
        if (!(weight >= minimumWeight) || !(weight > 0.0))
            return false;

        z = sum / weight;
        return true;
    }

    /// <summary>Nearest on-terrain node to a plan point within <paramref name="searchRadiusCells"/>, or -1.</summary>
    internal int NearestGroundNode(double x, double y, int searchRadiusCells)
    {
        if (Columns == 0 || Rows == 0)
            return -1;

        int column = (int)Math.Round((x - OriginX) / CellSize);
        int row = (int)Math.Round((y - OriginY) / CellSize);
        int best = -1;
        double bestDistance = double.MaxValue;
        for (int dr = -searchRadiusCells; dr <= searchRadiusCells; dr++)
        {
            int r = row + dr;
            if (r < 0 || r >= Rows)
                continue;
            for (int dc = -searchRadiusCells; dc <= searchRadiusCells; dc++)
            {
                int c = column + dc;
                if (c < 0 || c >= Columns || double.IsNaN(Height(c, r)))
                    continue;

                double distance = Geometry2D.DistanceSquared(x, y, NodeX(c), NodeY(r));
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = (r * Columns) + c;
                }
            }
        }

        return best;
    }

    private static void Accumulate(double height, double w, ref double sum, ref double weight)
    {
        if (double.IsNaN(height) || w <= 0.0)
            return;
        sum += height * w;
        weight += w;
    }
}
