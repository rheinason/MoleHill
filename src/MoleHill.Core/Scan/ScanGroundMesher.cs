using MoleHill.Core.Engine;

namespace MoleHill.Core.Scan;

/// <summary>
/// Turns a ground grid into a terrain mesh at a coarser spacing, for contouring (S7). Each coarse node is the
/// mean of the ground within its cell, which also evens out the scan's centimetre noise; each coarse cell
/// whose four corners have ground becomes two triangles, split along the diagonal whose ends differ least
/// in height.
/// <para>The mesh's border must be simple loops, which is what <c>TerrainCurveRecovery</c> needs and
/// what the scan with its houses in did not give: only the largest edge-connected piece is kept, and where two
/// cells touch only at a corner (a vertex two border loops would share), the one with fewer neighbours is
/// dropped, until no such corner is left.</para>
/// </summary>
public static class ScanGroundMesher
{
    public static IndexedTriMesh Mesh(ScanGrid ground, double spacing)
    {
        ArgumentNullException.ThrowIfNull(ground);
        if (!(spacing > 0.0) || !double.IsFinite(spacing))
            throw new ArgumentOutOfRangeException(nameof(spacing), "Spacing must be finite and positive.");
        double requestedFactor = Math.Max(1.0, Math.Round(spacing / ground.CellSize));
        if (requestedFactor > Math.Max(ground.Columns, ground.Rows))
            return IndexedTriMesh.Empty;
        int factor = (int)requestedFactor;
        int columns = ((ground.Columns - 1) / factor) + 1;
        int rows = ((ground.Rows - 1) / factor) + 1;
        if (ground.Columns == 0 || ground.Rows == 0 || columns < 2 || rows < 2)
            return IndexedTriMesh.Empty;

        // Coarse node (I, J) sits on fine node (I f, J f) and averages the fine nodes within half a coarse cell.
        var heights = new double[columns * rows];
        int reach = factor / 2;
        for (int j = 0; j < rows; j++)
        {
            for (int i = 0; i < columns; i++)
            {
                int fi = i * factor, fj = j * factor;
                double centre = ground[fi, fj];
                if (double.IsNaN(centre))
                {
                    heights[(j * columns) + i] = double.NaN;
                    continue;
                }

                double sum = 0.0;
                int count = 0;
                // Keep the averaging window symmetric at the border so a plane's edge heights do not
                // move uphill/downhill when its neighbours exist on only one side.
                int reachX = Math.Min(reach, Math.Min(fi, ground.Columns - 1 - fi));
                int reachY = Math.Min(reach, Math.Min(fj, ground.Rows - 1 - fj));
                for (int dj = -reachY; dj <= reachY; dj++)
                {
                    int y = fj + dj;
                    if (y < 0 || y >= ground.Rows)
                        continue;
                    for (int di = -reachX; di <= reachX; di++)
                    {
                        int x = fi + di;
                        if (x < 0 || x >= ground.Columns)
                            continue;
                        double value = ground[x, y];
                        if (double.IsNaN(value))
                            continue;
                        sum += value;
                        count++;
                    }
                }

                heights[(j * columns) + i] = sum / count;
            }
        }

        int cellColumns = columns - 1, cellRows = rows - 1;
        var cells = new bool[cellColumns * cellRows];
        for (int j = 0; j < cellRows; j++)
        {
            for (int i = 0; i < cellColumns; i++)
            {
                int a = (j * columns) + i;
                cells[(j * cellColumns) + i] =
                    !double.IsNaN(heights[a]) && !double.IsNaN(heights[a + 1]) &&
                    !double.IsNaN(heights[a + columns]) && !double.IsNaN(heights[a + columns + 1]);
            }
        }

        cells = SimplyBordered(cells, cellColumns, cellRows);
        return Triangulate(heights, cells, columns, cellColumns, cellRows, ground.OriginX, ground.OriginY, ground.CellSize * factor);
    }

    /// <summary>The largest piece of the cell mask with no corner-only contacts.</summary>
    internal static bool[] SimplyBordered(bool[] cells, int cellColumns, int cellRows)
    {
        bool[] current = KeepLargest(cells, cellColumns, cellRows);
        while (true)
        {
            bool changed = false;
            for (int j = 0; j + 1 < cellRows; j++)
            {
                for (int i = 0; i + 1 < cellColumns; i++)
                {
                    int a = (j * cellColumns) + i, b = a + 1, c = a + cellColumns, d = c + 1;
                    if (current[a] && current[d] && !current[b] && !current[c])
                        changed |= DropWeaker(current, cellColumns, cellRows, a, d);
                    else if (current[b] && current[c] && !current[a] && !current[d])
                        changed |= DropWeaker(current, cellColumns, cellRows, b, c);
                }
            }

            if (!changed)
                return current;
            current = KeepLargest(current, cellColumns, cellRows);
        }
    }

    private static bool DropWeaker(bool[] cells, int cellColumns, int cellRows, int first, int second)
    {
        cells[Neighbours(cells, cellColumns, cellRows, first) <= Neighbours(cells, cellColumns, cellRows, second) ? first : second] = false;
        return true;
    }

    private static int Neighbours(bool[] cells, int cellColumns, int cellRows, int cell)
    {
        int i = cell % cellColumns, j = cell / cellColumns, count = 0;
        if (i > 0 && cells[cell - 1]) count++;
        if (i < cellColumns - 1 && cells[cell + 1]) count++;
        if (j > 0 && cells[cell - cellColumns]) count++;
        if (j < cellRows - 1 && cells[cell + cellColumns]) count++;
        return count;
    }

    private static bool[] KeepLargest(bool[] cells, int cellColumns, int cellRows)
    {
        int[] labels = GridMorphology.Label(cells, cellColumns, cellRows, out List<int> sizes, out _);
        var result = new bool[cells.Length];
        if (sizes.Count == 0)
            return result;
        int largest = 0;
        for (int i = 1; i < sizes.Count; i++)
        {
            if (sizes[i] > sizes[largest])
                largest = i;
        }

        for (int i = 0; i < cells.Length; i++)
            result[i] = labels[i] == largest;
        return result;
    }

    private static IndexedTriMesh Triangulate(
        double[] heights, bool[] cells, int columns, int cellColumns, int cellRows, double originX, double originY, double spacing)
    {
        var index = new int[heights.Length];
        Array.Fill(index, -1);
        var vertices = new List<double>();
        var faces = new List<int>();
        int Vertex(int node)
        {
            if (index[node] >= 0)
                return index[node];
            index[node] = vertices.Count / 3;
            vertices.Add(originX + ((node % columns) * spacing));
            vertices.Add(originY + ((node / columns) * spacing));
            vertices.Add(heights[node]);
            return index[node];
        }

        for (int j = 0; j < cellRows; j++)
        {
            for (int i = 0; i < cellColumns; i++)
            {
                if (!cells[(j * cellColumns) + i])
                    continue;
                int n00 = (j * columns) + i, n10 = n00 + 1, n01 = n00 + columns, n11 = n01 + 1;
                int a = Vertex(n00), b = Vertex(n10), c = Vertex(n11), d = Vertex(n01);

                // Split along the diagonal whose ends differ least in height.
                if (Math.Abs(heights[n00] - heights[n11]) <= Math.Abs(heights[n10] - heights[n01]))
                    faces.AddRange(new[] { a, b, c, a, c, d });
                else
                    faces.AddRange(new[] { a, b, d, b, c, d });
            }
        }

        return IndexedTriMesh.FromArrays(vertices.ToArray(), faces.ToArray());
    }
}
