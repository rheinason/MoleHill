namespace MoleHill.Core.Processing;

/// <summary>Merge targets for ordered XYZ points, including transitive clusters in full 3D.</summary>
public static class PointMerge
{
    /// <summary>Returns XYZ targets in input order. Pairwise clustering is intended for selected sets.</summary>
    public static double[] CreateTargets(IReadOnlyList<double> coordinates, PointMergeMode mode, double distance = 0)
    {
        ArgumentNullException.ThrowIfNull(coordinates);
        if (coordinates.Count < 6 || coordinates.Count % 3 != 0 || coordinates.Any(value => !double.IsFinite(value)))
            throw new ArgumentException("Select at least two valid points.", nameof(coordinates));
        if (!Enum.IsDefined(typeof(PointMergeMode), mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (mode == PointMergeMode.ByDistance && (!double.IsFinite(distance) || distance < 0))
            throw new ArgumentOutOfRangeException(nameof(distance), "Distance must be finite and nonnegative.");

        int count = coordinates.Count / 3;
        var targets = coordinates.ToArray();
        if (mode is PointMergeMode.First or PointMergeMode.Last)
        {
            int anchor = mode == PointMergeMode.First ? 0 : (count - 1) * 3;
            for (int index = 0; index < count; index++)
                for (int axis = 0; axis < 3; axis++) targets[index * 3 + axis] = coordinates[anchor + axis];
            return targets;
        }

        int[] parents = Enumerable.Range(0, count).ToArray();
        int Root(int index)
        {
            while (parents[index] != index)
            {
                parents[index] = parents[parents[index]];
                index = parents[index];
            }
            return index;
        }
        if (mode == PointMergeMode.ByDistance)
        {
            for (int first = 0; first < count; first++)
                for (int second = 0; second < first; second++)
                    if (WithinDistance(coordinates, first, second, distance)) parents[Root(first)] = Root(second);
        }
        else
            Array.Fill(parents, 0);

        var groups = new Dictionary<int, List<int>>();
        for (int index = 0; index < count; index++)
        {
            int root = Root(index);
            if (!groups.TryGetValue(root, out List<int>? group)) groups.Add(root, group = new List<int>());
            group.Add(index);
        }
        foreach (List<int> group in groups.Values)
        {
            if (group.Count == 1) continue;
            for (int axis = 0; axis < 3; axis++)
            {
                // Divide before summation to avoid overflow; compensate cancellation so a
                // large positive/negative pair does not erase smaller coordinates in between.
                double sum = 0, correction = 0;
                foreach (int index in group)
                {
                    double value = coordinates[index * 3 + axis] / group.Count;
                    double next = sum + value;
                    correction += Math.Abs(sum) >= Math.Abs(value) ? (sum - next) + value : (value - next) + sum;
                    sum = next;
                }
                double center = sum + correction;
                if (!double.IsFinite(center)) throw new ArgumentException("Merge position is outside the supported range.");
                foreach (int index in group) targets[index * 3 + axis] = center;
            }
        }
        return targets;
    }

    private static bool WithinDistance(IReadOnlyList<double> coordinates, int first, int second, double distance)
    {
        double squared = 0;
        for (int axis = 0; axis < 3; axis++)
        {
            double delta = Math.Abs(coordinates[first * 3 + axis] - coordinates[second * 3 + axis]);
            if (delta > distance) return false;
            if (distance > 0) squared += Math.Pow(delta / distance, 2);
        }
        return squared <= 1;
    }
}
