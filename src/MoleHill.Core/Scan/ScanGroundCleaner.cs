using MoleHill.Core.Processing;

namespace MoleHill.Core.Scan;

/// <summary>Bounded removal of isolated raster residuals and small standing objects, with accepted feature bands held unchanged.</summary>
internal static class ScanGroundCleaner
{
    public sealed record Result(ScanGrid Grid, int ChangedNodes, double MaximumDisplacement);

    /// <summary>
    /// Lowers ground standing more than <paramref name="height"/> above an opening of the surface
    /// <paramref name="width"/> across onto that opened floor. The ground filter keeps objects smaller than its
    /// minimum object (cars, bushes, a hedge, a bin) as ground, so the reference terrain rose over every parked
    /// car: paving missed its accuracy there and took a spot height on the car's roof, and lawns took contour
    /// rings round bushes. A flat opening keeps anything wider than its window exactly, so a terrace edge or the
    /// crest of a bank stays where it is. Measured lines (walls, kerbs, paved outlines) are held. An object
    /// taller than <paramref name="tallest"/>, or touching a taller object the filter removed, is no car: the filter's
    /// bands beside buildings and narrow wall tops stood up to 4 m and covered two-thirds of the first try.
    /// </summary>
    public static Result Lower(ScanGround.Result ground, IReadOnlyList<double[]> held, double width, double height,
        double tallest, CancellationToken token)
    {
        var grid = ground.Ground;
        if (width <= 0 || height <= 0) return new(grid, 0, 0);
        bool[] protectedNodes = Protect(grid, held, 1);
        double[] floor = GridMorphology.Open(grid.Values, grid.Columns, grid.Rows, Math.Max(1, (int)Math.Round(width * .5 / grid.CellSize)));
        token.ThrowIfCancellationRequested();
        // The opening finds the objects; it is no estimate of the ground under them, rising towards an object's
        // uphill side on a slope. They are filled harmonically from the ground round them instead: exact on a plane.
        var raised = new bool[grid.NodeCount];
        for (int i = 0; i < grid.NodeCount; i++)
            raised[i] = !protectedNodes[i] && ground.IsGround[i] && ground.Domain[i] && grid.Values[i] - floor[i] > height;
        int[] parts = GridMorphology.Label(raised, grid.Columns, grid.Rows, out List<int> sizes, out _);
        var rejected = new bool[sizes.Count];
        for (int i = 0; i < grid.NodeCount; i++)
        {
            if (parts[i] < 0) continue;
            int x = i % grid.Columns, y = i / grid.Columns;
            bool removedBeside = x > 0 && Removed(i - 1) || x + 1 < grid.Columns && Removed(i + 1) ||
                y > 0 && Removed(i - grid.Columns) || y + 1 < grid.Rows && Removed(i + grid.Columns);
            if (removedBeside || grid.Values[i] - floor[i] > tallest) rejected[parts[i]] = true;
        }
        for (int i = 0; i < grid.NodeCount; i++)
            if (parts[i] >= 0 && rejected[parts[i]]) raised[i] = false;
        // An object's sloping rim is part of it: half a metre more, so the fill starts from ground.
        bool[] isStanding = GridMorphology.DilateMask(raised, grid.Columns, grid.Rows, Math.Max(1, (int)Math.Round(width / 6 / grid.CellSize)));
        var standing = new List<int>();
        int objects = 0;
        for (int i = 0; i < grid.NodeCount; i++)
        {
            // The rim never reaches into a raised part that was kept (a wall top, a building band).
            isStanding[i] &= !protectedNodes[i] && ground.IsGround[i] && ground.Domain[i] && (raised[i] || !(grid.Values[i] - floor[i] > height));
            if (isStanding[i]) standing.Add(i);
            if (raised[i]) objects++;
        }
        var lowered = (double[])grid.Values.Clone();
        foreach (int i in standing) lowered[i] = floor[i];
        for (int sweep = 0; sweep < 400 && standing.Count > 0; sweep++)
        {
            if ((sweep & 31) == 0) token.ThrowIfCancellationRequested();
            double change = 0;
            foreach (int i in standing)
            {
                int x = i % grid.Columns, y = i / grid.Columns, n = 0; double sum = 0;
                if (x > 0) Take(i - 1);
                if (x + 1 < grid.Columns) Take(i + 1);
                if (y > 0) Take(i - grid.Columns);
                if (y + 1 < grid.Rows) Take(i + grid.Columns);
                if (n == 0) continue;
                // Over-relaxed Gauss-Seidel: an object a few metres across settles in a few dozen sweeps.
                double next = lowered[i] + 1.8 * (sum / n - lowered[i]);
                change = Math.Max(change, Math.Abs(next - lowered[i]));
                lowered[i] = next;

                void Take(int j)
                {
                    if (!double.IsFinite(lowered[j]) || !isStanding[j] && !ground.Domain[j]) return;
                    sum += lowered[j]; n++;
                }
            }
            if (change < height * 1e-6) break;
        }
        double maximum = 0;
        foreach (int i in standing) if (raised[i]) maximum = Math.Max(maximum, grid.Values[i] - lowered[i]);
        // Changed nodes count the objects themselves; their rims move by no more than the scan's own noise.
        return new(grid.WithValues(lowered), objects, maximum);

        // Removed ground standing taller than an object (a building, a tree), not a neighbouring car the filter took.
        bool Removed(int j) => ground.Domain[j] && !ground.IsGround[j] && !(ground.Surface.Values[j] - ground.Ground.Values[j] <= tallest);
    }
    public static Result Clean(ScanGround.Result ground, IReadOnlyList<double[]> held, double featureSize,
        double allowance, CancellationToken token, ScanGrid? surface = null)
    {
        var grid = surface ?? ground.Ground;
        if (featureSize <= 0 || allowance <= 0) return new(grid, 0, 0);
        bool[] protectedNodes = Protect(grid, held, Math.Max(1, (int)Math.Ceiling(featureSize * .5 / grid.CellSize)));
        var residual = new double[grid.NodeCount]; var positive = new bool[grid.NodeCount]; var negative = new bool[grid.NodeCount];
        int[] offsets = { 1, grid.Columns, grid.Columns + 1, grid.Columns - 1 };
        var predictions = new double[4];
        for (int y = 1; y < grid.Rows - 1; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 1; x < grid.Columns - 1; x++)
            {
                int i = y * grid.Columns + x;
                if (protectedNodes[i] || !ground.IsGround[i] || !ground.Domain[i]) continue;
                bool known = true;
                for (int k = 0; k < offsets.Length; k++)
                {
                    int a = i - offsets[k], b = i + offsets[k];
                    known &= ground.IsGround[a] && ground.IsGround[b];
                    predictions[k] = (grid.Values[a] + grid.Values[b]) * .5;
                }
                if (!known || predictions.Any(v => !double.IsFinite(v))) continue;
                Array.Sort(predictions);
                double d = grid.Values[i] - (predictions[1] + predictions[2]) * .5;
                // Large breaks and coherent curvature are not a licence for unlimited smoothing.
                if (Math.Abs(d) > allowance || Math.Abs(d) <= allowance * 1e-6) continue;
                residual[i] = d; positive[i] = d > 0; negative[i] = d < 0;
            }
        }
        var values = (double[])grid.Values.Clone(); int changed = 0; double maximum = 0;
        foreach (var mask in new[] { positive, negative })
        {
            int[] labels = GridMorphology.Label(mask, grid.Columns, grid.Rows, out var sizes, out _);
            for (int i = 0; i < values.Length; i++)
                if (labels[i] >= 0 && sizes[labels[i]] * grid.NodeArea < featureSize * featureSize)
                { values[i] -= residual[i]; changed++; maximum = Math.Max(maximum, Math.Abs(residual[i])); }
        }
        return new(grid.WithValues(values), changed, maximum);
    }

    private static bool[] Protect(ScanGrid grid, IReadOnlyList<double[]> held, int radius)
    {
        var protectedNodes = new bool[grid.NodeCount];
        foreach (var line in held)
            for (int i = 0; i + 5 < line.Length; i += 3)
            {
                double dx = line[i + 3] - line[i], dy = line[i + 4] - line[i + 1];
                int steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(dx * dx + dy * dy) / (grid.CellSize * .5)));
                for (int j = 0; j <= steps; j++)
                {
                    int cx = (int)Math.Round((line[i] + dx * j / steps - grid.OriginX) / grid.CellSize);
                    int cy = (int)Math.Round((line[i + 1] + dy * j / steps - grid.OriginY) / grid.CellSize);
                    for (int y = Math.Max(0, cy - radius); y <= Math.Min(grid.Rows - 1, cy + radius); y++)
                        for (int x = Math.Max(0, cx - radius); x <= Math.Min(grid.Columns - 1, cx + radius); x++)
                            protectedNodes[y * grid.Columns + x] = true;
                }
            }
        return protectedNodes;
    }
}
