using MoleHill.Core.Engine;
using MoleHill.Core.Grading;

namespace MoleHill.Core.Tests;

/// <summary>
/// Whether a grade kept its hard constraints: the surface before and after grading, sampled along each
/// constraint every 0.25 m. A grade may stop at a breakline, a wall or a pad edge; it may not move one.
/// Compared with the terrain rather than the constraint's own heights, because a captured terrain need not
/// carry every constraint it was handed.
/// </summary>
internal static class ConstraintHeightCheck
{
    /// <summary>The largest height change along any constraint, and where it is.</summary>
    public static (double Worst, double X, double Y) WorstChange(
        IReadOnlyList<ConstraintPolyline> constraints, IndexedTriMesh before, GradingResult after)
    {
        (double Worst, double X, double Y) worst = (0.0, 0.0, 0.0);
        foreach (ConstraintPolyline constraint in constraints)
        {
            int count = constraint.PointCount;
            int spans = constraint.IsClosed ? count : count - 1;
            for (int s = 0; s < spans; s++)
            {
                int t = (s + 1) % count;
                double ax = constraint.Points[s * 3], ay = constraint.Points[(s * 3) + 1];
                double bx = constraint.Points[t * 3], by = constraint.Points[(t * 3) + 1];
                int steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(((bx - ax) * (bx - ax)) + ((by - ay) * (by - ay))) / 0.25));
                for (int k = 0; k <= steps; k++)
                {
                    double u = k / (double)steps;
                    double x = ax + ((bx - ax) * u), y = ay + ((by - ay) * u);
                    double change = Math.Abs(
                        SampleZ(after.Vertices, after.Faces, after.FaceCount, x, y) -
                        SampleZ(before.Vertices, before.Faces, before.FaceCount, x, y));
                    if (change > worst.Worst)
                        worst = (change, x, y);
                }
            }
        }

        return worst;
    }

    /// <summary>The surface height at (x, y), or NaN outside the mesh (which never counts as a change).</summary>
    public static double SampleZ(double[] v, int[] faces, int faceCount, double x, double y)
    {
        for (int f = 0; f < faceCount; f++)
        {
            int a = faces[f * 3] * 3, b = faces[(f * 3) + 1] * 3, c = faces[(f * 3) + 2] * 3;
            double d = ((v[b + 1] - v[c + 1]) * (v[a] - v[c])) + ((v[c] - v[b]) * (v[a + 1] - v[c + 1]));
            if (Math.Abs(d) < 1e-14)
                continue;
            double l1 = (((v[b + 1] - v[c + 1]) * (x - v[c])) + ((v[c] - v[b]) * (y - v[c + 1]))) / d;
            double l2 = (((v[c + 1] - v[a + 1]) * (x - v[c])) + ((v[a] - v[c]) * (y - v[c + 1]))) / d;
            double l3 = 1.0 - l1 - l2;
            if (l1 >= -1e-9 && l2 >= -1e-9 && l3 >= -1e-9)
                return (l1 * v[a + 2]) + (l2 * v[b + 2]) + (l3 * v[c + 2]);
        }

        return double.NaN;
    }
}
