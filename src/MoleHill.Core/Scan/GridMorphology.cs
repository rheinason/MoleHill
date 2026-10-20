namespace MoleHill.Core.Scan;

/// <summary>
/// Square-window grey morphology, disc openings by exact distance and connected components on row-major lattices. Min and max filters are
/// separable (rows, then columns) and run in time independent of the window, with a monotonic queue per
/// line, so the 48 m windows of the ground filter cost what a 1 m one does. NaN is no data: it never wins
/// a min or a max, and a window with no data at all gives NaN.
/// </summary>
internal static class GridMorphology
{
    public static double[] Erode(double[] values, int columns, int rows, int halfWidth) =>
        Filter(values, columns, rows, halfWidth, takeMinimum: true);

    public static double[] Dilate(double[] values, int columns, int rows, int halfWidth) =>
        Filter(values, columns, rows, halfWidth, takeMinimum: false);

    /// <summary>Erosion then dilation: removes what stands up narrower than the window and keeps the rest.</summary>
    public static double[] Open(double[] values, int columns, int rows, int halfWidth) =>
        Dilate(Erode(values, columns, rows, halfWidth), columns, rows, halfWidth);

    /// <summary>True wherever a true node lies within <paramref name="halfWidth"/> nodes (square window).</summary>
    public static bool[] DilateMask(bool[] mask, int columns, int rows, int halfWidth)
    {
        var values = new double[mask.Length];
        for (int i = 0; i < mask.Length; i++)
            values[i] = mask[i] ? 1.0 : 0.0;
        double[] dilated = Dilate(values, columns, rows, halfWidth);
        var result = new bool[mask.Length];
        for (int i = 0; i < mask.Length; i++)
            result[i] = dilated[i] > 0.5;
        return result;
    }

    /// <summary>Erosion then dilation of a mask: drops true parts narrower than the window.</summary>
    public static bool[] OpenMask(bool[] mask, int columns, int rows, int halfWidth)
    {
        var inverse = new bool[mask.Length];
        for (int i = 0; i < mask.Length; i++)
            inverse[i] = !mask[i];
        bool[] grownGaps = DilateMask(inverse, columns, rows, halfWidth);
        for (int i = 0; i < mask.Length; i++)
            grownGaps[i] = !grownGaps[i];
        bool[] opened = DilateMask(grownGaps, columns, rows, halfWidth);
        for (int i = 0; i < mask.Length; i++)
            opened[i] &= mask[i];
        return opened;
    }

    private static double[] Filter(double[] values, int columns, int rows, int halfWidth, bool takeMinimum)
    {
        if (halfWidth <= 0)
            return (double[])values.Clone();

        var across = new double[values.Length];
        var line = new double[Math.Max(columns, rows)];
        var output = new double[line.Length];
        var queue = new int[line.Length];
        for (int row = 0; row < rows; row++)
        {
            Array.Copy(values, row * columns, line, 0, columns);
            FilterLine(line, columns, halfWidth, takeMinimum, output, queue);
            Array.Copy(output, 0, across, row * columns, columns);
        }

        var result = new double[values.Length];
        for (int column = 0; column < columns; column++)
        {
            for (int row = 0; row < rows; row++)
                line[row] = across[(row * columns) + column];
            FilterLine(line, rows, halfWidth, takeMinimum, output, queue);
            for (int row = 0; row < rows; row++)
                result[(row * columns) + column] = output[row];
        }

        return result;
    }

    /// <summary>Sliding min (or max) of a window of 2h+1 over one line, NaN skipped, by a monotonic queue.</summary>
    private static void FilterLine(double[] line, int length, int halfWidth, bool takeMinimum, double[] output, int[] queue)
    {
        int head = 0, tail = 0;
        int next = 0;
        for (int i = 0; i < length; i++)
        {
            int windowEnd = Math.Min(length - 1, i + halfWidth);
            for (; next <= windowEnd; next++)
            {
                double value = line[next];
                if (double.IsNaN(value))
                    continue;
                while (tail > head && (takeMinimum ? line[queue[tail - 1]] >= value : line[queue[tail - 1]] <= value))
                    tail--;
                queue[tail++] = next;
            }

            while (tail > head && queue[head] < i - halfWidth)
                head++;
            output[i] = tail > head ? line[queue[head]] : double.NaN;
        }
    }

    /// <summary>
    /// 4-connected components of the true nodes: a label per node (-1 where false), each component's size,
    /// and whether it touches the lattice's edge.
    /// </summary>
    public static int[] Label(bool[] mask, int columns, int rows, out List<int> sizes, out List<bool> touchesEdge)
    {
        var labels = new int[mask.Length];
        Array.Fill(labels, -1);
        sizes = new List<int>();
        touchesEdge = new List<bool>();
        var stack = new Stack<int>();
        for (int start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || labels[start] >= 0)
                continue;
            int label = sizes.Count;
            int size = 0;
            bool edge = false;
            labels[start] = label;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int node = stack.Pop();
                size++;
                int column = node % columns, row = node / columns;
                if (column == 0 || row == 0 || column == columns - 1 || row == rows - 1)
                    edge = true;
                Visit(column > 0 ? node - 1 : -1);
                Visit(column < columns - 1 ? node + 1 : -1);
                Visit(row > 0 ? node - columns : -1);
                Visit(row < rows - 1 ? node + columns : -1);
            }

            sizes.Add(size);
            touchesEdge.Add(edge);

            void Visit(int neighbour)
            {
                if (neighbour < 0 || !mask[neighbour] || labels[neighbour] >= 0)
                    return;
                labels[neighbour] = label;
                stack.Push(neighbour);
            }
        }

        return labels;
    }

    /// <summary>
    /// The largest 4-connected component of <paramref name="mask"/>, with every enclosed gap in it filled:
    /// one piece of ground with an outer edge and no islands of missing data.
    /// </summary>
    public static bool[] LargestComponentWithoutHoles(bool[] mask, int columns, int rows)
    {
        int[] labels = Label(mask, columns, rows, out List<int> sizes, out _);
        var result = new bool[mask.Length];
        if (sizes.Count == 0)
            return result;
        int largest = 0;
        for (int i = 1; i < sizes.Count; i++)
        {
            if (sizes[i] > sizes[largest])
                largest = i;
        }

        for (int i = 0; i < mask.Length; i++)
            result[i] = labels[i] == largest;

        var outside = new bool[mask.Length];
        for (int i = 0; i < mask.Length; i++)
            outside[i] = !result[i];
        int[] gaps = Label(outside, columns, rows, out _, out List<bool> gapTouchesEdge);
        for (int i = 0; i < mask.Length; i++)
        {
            if (gaps[i] >= 0 && !gapTouchesEdge[gaps[i]])
                result[i] = true;
        }

        return result;
    }

    /// <summary>
    /// Opening by a disc of <paramref name="radius"/> nodes: drops true parts narrower than its diameter in any
    /// direction (a square window passes a diagonal strip 1.4 times wider). The lattice edge does not erode.
    /// </summary>
    public static bool[] OpenMaskDisc(bool[] mask, int columns, int rows, double radius, CancellationToken token = default)
    {
        double squared = radius * radius;
        float[] gap = SquaredDistance(node => !mask[node], columns, rows, token);
        float[] core = SquaredDistance(node => gap[node] > squared, columns, rows, token);
        var opened = new bool[mask.Length];
        for (int node = 0; node < mask.Length; node++) opened[node] = mask[node] && core[node] <= squared;
        return opened;
    }

    /// <summary>Exact squared Euclidean distance in cells to the nearest feature node (Felzenszwalb-Huttenlocher).</summary>
    public static float[] SquaredDistance(Func<int, bool> feature, int columns, int rows, CancellationToken token)
    {
        const float Far = 1e20f;
        var distance = new float[columns * rows];
        for (int node = 0; node < distance.Length; node++) distance[node] = feature(node) ? 0 : Far;
        var options = new ParallelOptions { CancellationToken = token };
        Parallel.For(0, rows, options, () => new Scratch(Math.Max(columns, rows)), (row, _, scratch) =>
        {
            Pass(distance, row * columns, 1, columns, scratch);
            return scratch;
        }, _ => { });
        Parallel.For(0, columns, options, () => new Scratch(Math.Max(columns, rows)), (column, _, scratch) =>
        {
            Pass(distance, column, columns, rows, scratch);
            return scratch;
        }, _ => { });
        return distance;
    }

    private sealed class Scratch(int length)
    {
        public readonly double[] F = new double[length], Z = new double[length + 1];
        public readonly int[] V = new int[length];
    }

    /// <summary>One lower envelope of parabolas along a row or column, in place; nodes with no feature in reach add none.</summary>
    private static void Pass(float[] values, int start, int stride, int count, Scratch s)
    {
        int k = -1;
        for (int q = 0; q < count; q++)
        {
            s.F[q] = values[start + q * stride];
            if (s.F[q] >= 1e20) continue;
            double cut = double.NegativeInfinity;
            while (k >= 0)
            {
                int v = s.V[k];
                cut = (s.F[q] + (double)q * q - (s.F[v] + (double)v * v)) / (2.0 * (q - v));
                if (cut > s.Z[k]) break;
                k--;
            }
            if (k < 0) cut = double.NegativeInfinity;
            k++;
            s.V[k] = q; s.Z[k] = cut; s.Z[k + 1] = double.PositiveInfinity;
        }
        if (k < 0) return;
        k = 0;
        for (int q = 0; q < count; q++)
        {
            while (s.Z[k + 1] < q) k++;
            int v = s.V[k];
            values[start + q * stride] = (float)((q - v) * (double)(q - v) + s.F[v]);
        }
    }
}
