using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

/// <summary>
/// After a modifier grades through breaklines, the parts of the earlier lines it regraded no longer describe
/// the terrain: kept as constraints, a later stage would pull the old ground back into the new grade. This
/// keeps each line only where it still lies on the graded surface, splitting it around the regraded stretch.
/// </summary>
internal static class RegradedConstraintTrimmer
{
    /// <summary>
    /// Each constraint cut down to its runs of vertices that sit on <paramref name="graded"/> within
    /// <paramref name="heightTolerance"/> (a vertex off the terrain in plan is kept: nothing regraded it). Runs
    /// shorter than two vertices are dropped. <paramref name="removedLength"/> is the plan length taken away.
    /// </summary>
    public static List<ConstraintPolyline> Trim(
        IReadOnlyList<ConstraintPolyline> constraints,
        TerrainFaceGrid graded,
        double heightTolerance,
        out double removedLength,
        out int trimmedLineCount)
    {
        removedLength = 0.0;
        trimmedLineCount = 0;
        var result = new List<ConstraintPolyline>(constraints.Count);
        foreach (ConstraintPolyline line in constraints)
        {
            int n = line.PointCount;
            var keep = new bool[n];
            bool all = true;
            for (int k = 0; k < n; k++)
            {
                keep[k] = !graded.TryInterpolateZ(line.Points[k * 3], line.Points[k * 3 + 1], out double z) ||
                          Math.Abs(z - line.Points[k * 3 + 2]) <= heightTolerance;
                all &= keep[k];
            }

            if (all)
            {
                result.Add(line);
                continue;
            }

            trimmedLineCount++;
            int segments = line.IsClosed && n > 2 ? n : n - 1;
            for (int s = 0; s < segments; s++)
            {
                int t = (s + 1) % n;
                if (!keep[s] || !keep[t])
                {
                    double dx = line.Points[t * 3] - line.Points[s * 3], dy = line.Points[t * 3 + 1] - line.Points[s * 3 + 1];
                    removedLength += Math.Sqrt((dx * dx) + (dy * dy));
                }
            }

            // Walk the kept runs. A closed line starts at a dropped vertex so no run wraps past its start.
            int start = 0;
            if (line.IsClosed)
            {
                while (start < n && keep[start])
                    start++;
            }

            var run = new List<double>();
            void Flush()
            {
                if (run.Count >= 6)
                    result.Add(new ConstraintPolyline(run.ToArray(), run.Count / 3, IsClosed: false, line.PreserveInputElevation));
                run.Clear();
            }

            int steps = line.IsClosed ? n + 1 : n;
            for (int step = 0; step < steps; step++)
            {
                int k = (start + step) % n;
                if (!keep[k])
                {
                    Flush();
                    continue;
                }

                run.Add(line.Points[k * 3]);
                run.Add(line.Points[k * 3 + 1]);
                run.Add(line.Points[k * 3 + 2]);
            }

            Flush();
        }

        return result;
    }
}
