using MoleHill.Core.Analysis;

namespace MoleHill.Core.Scan;

/// <summary>
/// Partitions observed ground into paved, soft and unknown on a colour lattice finer than the height grid.
/// Each node scores how paved and how vegetated it looks; the scores are averaged over a physical radius
/// before the larger one wins. Hard per-node thresholds on single texel samples gave outlines that followed
/// every speck of colour noise, and a curve then had to trace each one.
/// </summary>
internal static class ScanSurfaceClassifier
{
    // Logistic widths around the configured thresholds: soft enough that one stray texel cannot flip a node.
    private const double GreenWidth = 0.025, SaturationWidth = 0.04, Decisive = 0.35;

    public static byte[] Classify(ScanGround.Result ground, ScanColourLattice lattice, ScanSurfaceAreas.Options options,
        MeshHeightProjector? projector, double smoothingRadius)
    {
        ScanGrid grid = ground.Ground;
        (bool[] eligible, bool[] smooth, bool[] rough) = CoarseEvidence(ground, options, projector, smoothingRadius);
        int columns = lattice.Columns, rows = lattice.Rows, factor = lattice.Factor, count = columns * rows;
        var weight = new float[count];
        var paved = new float[count];
        var soft = new float[count];
        Parallel.For(0, rows, new ParallelOptions { CancellationToken = options.CancellationToken }, row =>
        {
            int coarseRow = Math.Min(grid.Rows - 1, (row + factor / 2) / factor);
            for (int column = 0; column < columns; column++)
            {
                int node = row * columns + column;
                int coarse = coarseRow * grid.Columns + Math.Min(grid.Columns - 1, (column + factor / 2) / factor);
                int rgb = lattice.Rgb[node];
                if (!eligible[coarse] || rgb < 0)
                    continue;
                int r = rgb >> 16 & 255, g = rgb >> 8 & 255, b = rgb & 255;
                if (r + g + b < 6)
                    continue;
                double exg = (2.0 * g - r - b) / (r + g + b);
                double maximum = Math.Max(r, Math.Max(g, b)), minimum = Math.Min(r, Math.Min(g, b));
                double saturation = maximum > 0 ? (maximum - minimum) / maximum : 0;
                double green = Logistic((exg - options.ExcessGreen) / GreenWidth);
                double grey = Logistic((options.MaximumPavedSaturation - saturation) / SaturationWidth);
                double p = smooth[coarse] ? (1 - green) * grey : 0;
                double s = rough[coarse] ? Math.Max(green, 1 - p) : green;
                weight[node] = 1; paved[node] = (float)p; soft[node] = (float)s;
            }
        });
        double sigma = smoothingRadius / lattice.CellSize;
        // A footpath is narrower than the evidence radius: averaged with the lawn on both sides it lost to it.
        // The same evidence a third as wide still sees it; Thin keeps only the strips the wide average erased.
        float[]? fine = sigma > 1.5 ? Fine(weight, paved, soft, columns, rows, sigma / 3, options.CancellationToken) : null;
        if (sigma > 0.5)
        {
            // Three box passes approximate a Gaussian of this deviation at constant cost per node.
            int radius = Math.Max(1, (int)Math.Round((Math.Sqrt(4 * sigma * sigma + 1) - 1) * 0.5));
            foreach (float[] field in new[] { weight, paved, soft })
                for (int pass = 0; pass < 3; pass++)
                    BoxBlur(field, columns, rows, radius, options.CancellationToken);
        }
        var labels = new byte[count];
        Parallel.For(0, rows, new ParallelOptions { CancellationToken = options.CancellationToken }, row =>
        {
            int coarseRow = Math.Min(grid.Rows - 1, (row + factor / 2) / factor);
            for (int column = 0; column < columns; column++)
            {
                int node = row * columns + column;
                int coarse = coarseRow * grid.Columns + Math.Min(grid.Columns - 1, (column + factor / 2) / factor);
                float w = weight[node];
                if (!eligible[coarse] || w <= 1e-6f)
                    continue;
                double p = paved[node] / w, s = soft[node] / w;
                labels[node] = p >= s && p > Decisive ? (byte)ScanSurfaceKind.Paved
                    : s > p && s > Decisive ? (byte)ScanSurfaceKind.Soft : (byte)ScanSurfaceKind.Unknown;
            }
        });
        bool[]? strips = fine != null ? Thin(labels, fine, columns, rows, smoothingRadius / lattice.CellSize, options.CancellationToken) : null;
        double minimumNodes = options.MinimumArea / (lattice.CellSize * lattice.CellSize);
        // A stretch of path the lawn has cut off from the drive is still a path, though smaller than the minimum.
        CullSmall(labels, columns, rows, ScanSurfaceKind.Paved, minimumNodes, strips);
        CullSmall(labels, columns, rows, ScanSurfaceKind.Soft, minimumNodes);
        // A car on a drive or a bush on a lawn is enclosed by classified ground; the drawing passes over it. A car with
        // the ground filter's margin, or two parked side by side, exceeded three minimum areas and left
        // breakline loops around vehicles; six still keeps most buildings out.
        // Height splits a car from the house it is parked against. Low raised objects are opened at about half
        // a metre, which cuts the ground filter's thin margin band around a building from a compact car beside it;
        // a pocket may hold unknown ground and those low blobs, never a tall object or a margin band.
        bool[]? excluded = null;
        if (options.MaximumFilledObjectHeight is double tallest)
        {
            var low = new bool[grid.NodeCount];
            for (int i = 0; i < low.Length; i++)
                low[i] = ground.Domain[i] && !ground.IsGround[i] && ground.Surface.Values[i] - grid.Values[i] <= tallest;
            // The opening half-width scales with the evidence radius: half a metre at its 0.3 m default.
            int cut = Math.Max(1, (int)Math.Round(smoothingRadius * 5 / 3 / grid.CellSize));
            bool[] blobs = GridMorphology.OpenMask(low, grid.Columns, grid.Rows, cut);
            excluded = new bool[count];
            for (int row = 0; row < rows; row++)
            {
                int coarseRow = Math.Min(grid.Rows - 1, (row + factor / 2) / factor);
                for (int column = 0; column < columns; column++)
                {
                    int coarse = coarseRow * grid.Columns + Math.Min(grid.Columns - 1, (column + factor / 2) / factor);
                    excluded[row * columns + column] = ground.Domain[coarse] && !ground.IsGround[coarse] && !blobs[coarse];
                }
            }
        }
        FillEnclosed(labels, columns, rows, minimumNodes * 6, excluded, options.CancellationToken);
        return labels;
    }

    /// <summary>
    /// The paved/soft gate from height-grid geometry: a kerb beside a node is a jump, not crossfall. Ground standing
    /// more than four paved roughnesses above an opening of the surface about 3 m across (ten evidence radii) is an
    /// object the ground filter kept for being small: a car, a bush, a bin. It gives no class evidence. Rough
    /// ground otherwise reads as soft, which drew a soft outline round every parked car on the reference scan.
    /// </summary>
    private static (bool[] Eligible, bool[] Smooth, bool[] Rough) CoarseEvidence(ScanGround.Result ground,
        ScanSurfaceAreas.Options options, MeshHeightProjector? projector, double smoothingRadius)
    {
        ScanGrid grid = ground.Ground;
        double[] floor = GridMorphology.Open(grid.Values, grid.Columns, grid.Rows, Math.Max(1, (int)Math.Round(smoothingRadius * 5 / grid.CellSize)));
        double standing = options.MaximumPavedRoughness * 4;
        var eligible = new bool[grid.NodeCount];
        var smooth = new bool[grid.NodeCount];
        var rough = new bool[grid.NodeCount];
        for (int row = 0; row < grid.Rows; row++)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            for (int column = 0; column < grid.Columns; column++)
            {
                int node = row * grid.Columns + column;
                if (!ground.IsGround[node] || !ground.Domain[node] || !double.IsFinite(grid.Values[node]))
                    continue;
                if (projector != null && !FitsFootprint(column, row) || grid.Values[node] - floor[node] > standing)
                    continue;
                eligible[node] = true;
                double z = grid.Values[node], slope = 0, roughness = 0;
                Check(column - 1, row, column + 1, row);
                Check(column, row - 1, column, row + 1);
                smooth[node] = slope <= options.MaximumPavedSlope && roughness <= options.MaximumPavedRoughness;
                rough[node] = roughness > options.MaximumPavedRoughness;

                void Check(int x0, int y0, int x1, int y1)
                {
                    if (x0 < 0 || y0 < 0 || x1 >= grid.Columns || y1 >= grid.Rows)
                        return;
                    double before = grid[x0, y0], after = grid[x1, y1];
                    if (!double.IsFinite(before + after))
                        return;
                    slope = Math.Max(slope, Math.Min(Math.Abs(z - before), Math.Abs(after - z)) / grid.CellSize);
                    roughness = Math.Max(roughness, Math.Abs(z - (before + after) * 0.5));
                }
            }
        }
        return (eligible, smooth, rough);

        bool FitsFootprint(int column, int row)
        {
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = -1; sy <= 1; sy += 2)
                {
                    double x = grid.OriginX + Math.Clamp(column + sx * 0.5, 0, grid.Columns - 1) * grid.CellSize;
                    double y = grid.OriginY + Math.Clamp(row + sy * 0.5, 0, grid.Rows - 1) * grid.CellSize;
                    if (!projector!.TryGetHeightExtremes(x, y, grid.CellSize * 1e-7, out _, out _))
                        return false;
                }
            return true;
        }
    }

    /// <summary>Marks nodes the evidence averaged over <paramref name="sigma"/> nodes calls paved outright.</summary>
    private static float[] Fine(float[] weight, float[] paved, float[] soft, int columns, int rows, double sigma, CancellationToken token)
    {
        int radius = Math.Max(1, (int)Math.Round((Math.Sqrt(4 * sigma * sigma + 1) - 1) * 0.5));
        float[] w = (float[])weight.Clone(), p = (float[])paved.Clone(), s = (float[])soft.Clone();
        foreach (float[] field in new[] { w, p, s })
            for (int pass = 0; pass < 3; pass++)
                BoxBlur(field, columns, rows, radius, token);
        var margin = new float[w.Length];
        for (int node = 0; node < w.Length; node++)
            margin[node] = w[node] > 1e-6f && p[node] > s[node] && p[node] > w[node] * .5f ? 1 : 0;
        return margin;
    }

    /// <summary>
    /// Paves finely paved strips narrower than four evidence radii (a path, a narrow drive). The averaged
    /// decision broke a path wherever it narrowed or greened over, and the pieces between fell under the minimum
    /// area. Wider paving keeps its smoother averaged edge. A strip, counted whole, must cover twenty squared radii
    /// (about 1.8 m² at 0.3 m), so stepping stones and grey specks in a lawn stay lawn. Returns the strips at least
    /// five times longer than wide, which the minimum area spares: on the reference scan the strips it would also
    /// have spared were shadows and dark fringes beside buildings, blobs no longer than three widths.
    /// </summary>
    private static bool[] Thin(byte[] labels, float[] fine, int columns, int rows, double radius, CancellationToken token)
    {
        var mask = new bool[labels.Length];
        for (int node = 0; node < mask.Length; node++) mask[node] = fine[node] > 0;
        bool[] wide = GridMorphology.OpenMaskDisc(mask, columns, rows, radius * 2, token);
        var thin = new bool[mask.Length];
        for (int node = 0; node < mask.Length; node++)
            thin[node] = mask[node] && !wide[node];
        int[] parts = GridMorphology.Label(thin, columns, rows, out List<int> sizes, out _);
        double minimum = 20 * radius * radius;
        // Second moments per strip: the principal axes of its nodes give its length over its width.
        var moments = new double[sizes.Count * 5];
        for (int node = 0; node < labels.Length; node++)
        {
            int part = parts[node];
            if (part < 0 || sizes[part] < minimum) continue;
            labels[node] = (byte)ScanSurfaceKind.Paved;
            double x = node % columns, y = node / columns;
            moments[part * 5] += x; moments[part * 5 + 1] += y;
            moments[part * 5 + 2] += x * x; moments[part * 5 + 3] += y * y; moments[part * 5 + 4] += x * y;
        }
        var elongated = new bool[sizes.Count];
        for (int part = 0; part < sizes.Count; part++)
        {
            double n = sizes[part], mx = moments[part * 5] / n, my = moments[part * 5 + 1] / n;
            double xx = moments[part * 5 + 2] / n - mx * mx, yy = moments[part * 5 + 3] / n - my * my, xy = moments[part * 5 + 4] / n - mx * my;
            double half = (xx + yy) * 0.5, spread = Math.Sqrt(Math.Max(0, (xx - yy) * (xx - yy) * 0.25 + xy * xy));
            // Axis lengths go as the square roots of the eigenvalues: five to one is twenty-five to one.
            elongated[part] = n >= minimum && half + spread >= 25 * Math.Max(half - spread, 1e-9);
        }
        for (int node = 0; node < labels.Length; node++)
            thin[node] = parts[node] >= 0 && elongated[parts[node]];
        return thin;
    }

    private static double Logistic(double x) => 1 / (1 + Math.Exp(-x));

    /// <summary>Running-sum box filter; nodes beyond the lattice contribute zero, matched by the weight field.</summary>
    private static void BoxBlur(float[] field, int columns, int rows, int radius, CancellationToken token)
    {
        Parallel.For(0, rows, new ParallelOptions { CancellationToken = token }, () => new float[columns], (row, _, line) =>
        {
            int offset = row * columns;
            Array.Copy(field, offset, line, 0, columns);
            double sum = 0;
            for (int i = 0; i < Math.Min(radius, columns); i++) sum += line[i];
            for (int i = 0; i < columns; i++)
            {
                if (i + radius < columns) sum += line[i + radius];
                if (i - radius - 1 >= 0) sum -= line[i - radius - 1];
                field[offset + i] = (float)sum;
            }
            return line;
        }, _ => { });
        const int block = 256;
        Parallel.For(0, (columns + block - 1) / block, new ParallelOptions { CancellationToken = token }, chunk =>
        {
            int first = chunk * block, width = Math.Min(block, columns - first);
            var sum = new double[width];
            var original = new float[(radius + 1) * width];
            for (int r = 0; r < Math.Min(radius, rows); r++)
                for (int k = 0; k < width; k++) sum[k] += field[r * columns + first + k];
            // A ring of the last radius+1 original rows: each row is overwritten after it has been read ahead.
            for (int row = 0; row < rows; row++)
            {
                int ahead = row + radius, behind = row - radius - 1;
                for (int k = 0; k < width; k++)
                {
                    if (ahead < rows) sum[k] += field[ahead * columns + first + k];
                    if (behind >= 0) sum[k] -= original[(behind % (radius + 1)) * width + k];
                    original[(row % (radius + 1)) * width + k] = field[row * columns + first + k];
                    field[row * columns + first + k] = (float)sum[k];
                }
            }
        });
    }

    /// <summary>Unlabels components under the minimum, except those holding a <paramref name="keep"/> node.</summary>
    private static void CullSmall(byte[] labels, int columns, int rows, ScanSurfaceKind kind, double minimumNodes, bool[]? keep = null)
    {
        bool[] mask = labels.Select(value => value == (byte)kind).ToArray();
        int[] components = GridMorphology.Label(mask, columns, rows, out List<int> sizes, out _);
        if (keep != null)
            for (int node = 0; node < labels.Length; node++)
                if (keep[node] && components[node] >= 0) sizes[components[node]] = int.MaxValue;
        for (int node = 0; node < labels.Length; node++)
            if (components[node] >= 0 && sizes[components[node]] < minimumNodes)
                labels[node] = (byte)ScanSurfaceKind.Unknown;
    }

    private static void FillEnclosed(byte[] labels, int columns, int rows, double maximumNodes, bool[]? excluded, CancellationToken token)
    {
        bool[] unknown = labels.Select((value, i) => value == (byte)ScanSurfaceKind.Unknown && excluded?[i] != true).ToArray();
        int[] components = GridMorphology.Label(unknown, columns, rows, out List<int> sizes, out List<bool> touchesEdge);
        // Border length shared with each class: a car on the edge of a drive also touches the verge.
        var paved = new int[sizes.Count]; var soft = new int[sizes.Count];
        for (int row = 0; row < rows; row++)
        {
            token.ThrowIfCancellationRequested();
            for (int column = 0; column < columns; column++)
            {
                int node = row * columns + column, component = components[node];
                if (component < 0) continue;
                if (column > 0) Mark(node - 1);
                if (column + 1 < columns) Mark(node + 1);
                if (row > 0) Mark(node - columns);
                if (row + 1 < rows) Mark(node + columns);
                void Mark(int other)
                {
                    if (labels[other] == (byte)ScanSurfaceKind.Paved) paved[component]++;
                    else if (labels[other] == (byte)ScanSurfaceKind.Soft) soft[component]++;
                }
            }
        }
        // Each pocket node takes the class it is nearest to through the pocket, so a car between a lawn and a drive
        // carries both edges on through it. Requiring a three-quarter majority left such cars, and their shadows,
        // outlined as holes.
        var queue = new Queue<int>();
        var fill = new byte[labels.Length];
        for (int node = 0; node < labels.Length; node++)
        {
            int component = components[node];
            if (component < 0 || touchesEdge[component] || sizes[component] >= maximumNodes || paved[component] + soft[component] == 0) continue;
            int column = node % columns, row = node / columns;
            byte near = 0;
            if (column > 0) Seed(node - 1);
            if (column + 1 < columns) Seed(node + 1);
            if (row > 0) Seed(node - columns);
            if (row + 1 < rows) Seed(node + columns);
            if (near != 0) { fill[node] = near; queue.Enqueue(node); }
            void Seed(int other)
            {
                if (near == 0 && labels[other] is (byte)ScanSurfaceKind.Paved or (byte)ScanSurfaceKind.Soft) near = labels[other];
            }
        }
        while (queue.Count > 0)
        {
            int node = queue.Dequeue(), column = node % columns, row = node / columns;
            if (column > 0) Spread(node - 1);
            if (column + 1 < columns) Spread(node + 1);
            if (row > 0) Spread(node - columns);
            if (row + 1 < rows) Spread(node + columns);
            void Spread(int other)
            {
                if (components[other] != components[node] || fill[other] != 0) return;
                fill[other] = fill[node];
                queue.Enqueue(other);
            }
        }
        for (int node = 0; node < labels.Length; node++)
            if (fill[node] != 0) labels[node] = fill[node];
    }
}
