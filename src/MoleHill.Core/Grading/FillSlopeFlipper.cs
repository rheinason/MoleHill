using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

/// <summary>
/// Improves a graded fill's triangulation once its heights are known, by flipping interior edges that make a
/// face needlessly steep.
/// </summary>
/// <remarks>
/// A hole fill is triangulated in plan (constrained Delaunay over its boundary, road edges and batter seeds)
/// before any height is assigned, so plan quality is all the triangulator can see. Where the daylight loop
/// turns concave, it closes the turn with an ear of three boundary points, each pinned to the terrain; across
/// a terrain crease those heights make a thin face read as a spike (71.6 degrees at a tight Grade Path bend,
/// with no edge steeper than 34). Choosing the other diagonal of such a quad is free: the fill is welded to
/// the terrain only along its boundary, and constrained edges never move. An edge is flipped only when its
/// quad is strictly convex in plan, so no face inverts, only when one of its faces is at least
/// <see cref="SteepDegrees"/> steep, and only when the steeper of the two gets at least
/// <see cref="MinimumGainDegrees"/> flatter.
/// </remarks>
internal static class FillSlopeFlipper
{
    internal const double MinimumGainDegrees = 1.0;

    /// <summary>
    /// Only edges of faces at least this steep are considered. Ordinary batter (33 degrees by default) keeps
    /// its plan triangulation; a crease spike is far steeper.
    /// </summary>
    internal const double SteepDegrees = 40.0;
    private const int MaxPasses = 8;

    /// <summary>Returns the number of flips. <paramref name="faces"/> is rewritten in place, keeping winding.</summary>
    public static int Run(double[] xyz, int[] faces, int faceCount, Func<int, int, bool> isConstrained)
    {
        int flips = 0;
        for (int pass = 0; pass < MaxPasses; pass++)
        {
            int flipsThisPass = 0;

            // Only an edge of a steep face can be worth flipping. Index just those edges, then find their
            // other face in one pass: a fill spans its whole convex hull, and mapping every edge of it on
            // every pass cost a Grade Pad more than its whole topology stage.
            HashSet<long> wanted = IndexedMeshTools.CreateEdgeKeySet(64);
            for (int f = 0; f < faceCount; f++)
            {
                int a0 = faces[f * 3], b0 = faces[(f * 3) + 1], c0 = faces[(f * 3) + 2];
                if (Slope(xyz, a0, b0, c0) < SteepDegrees)
                    continue;
                wanted.Add(IndexedMeshTools.GetEdgeKey(a0, b0));
                wanted.Add(IndexedMeshTools.GetEdgeKey(b0, c0));
                wanted.Add(IndexedMeshTools.GetEdgeKey(c0, a0));
            }

            if (wanted.Count == 0)
                break;

            var edgeFaces = IndexedMeshTools.CreateEdgeKeyMap<(int A, int B)>(wanted.Count);
            for (int f = 0; f < faceCount; f++)
            {
                for (int k = 0; k < 3; k++)
                {
                    long key = IndexedMeshTools.GetEdgeKey(faces[(f * 3) + k], faces[(f * 3) + ((k + 1) % 3)]);
                    if (!wanted.Contains(key))
                        continue;
                    edgeFaces[key] = edgeFaces.TryGetValue(key, out var pair) ? (pair.A, pair.B < 0 ? f : -2) : (f, -1);
                }
            }

            var touched = new bool[faceCount];
            foreach ((long key, (int f1, int f2)) in edgeFaces)
            {
                if (f2 < 0 || touched[f1] || touched[f2])
                    continue;

                int u = (int)(key >> 32), v = (int)(key & 0xffffffff);
                if (isConstrained(u, v))
                    continue;

                // f1 runs u -> v (or v -> u); orient so f1 holds (a -> b -> w1) and f2 holds (b -> a -> w2).
                if (!TryDirected(faces, f1, u, v, out int a, out int b, out int w1) ||
                    !TryThird(faces, f2, a, b, out int w2) || w1 == w2)
                {
                    continue;
                }

                if (!StrictlyConvex(xyz, a, b, w1, w2))
                    continue;

                double before = Math.Max(Slope(xyz, a, b, w1), Slope(xyz, b, a, w2));
                double after = Math.Max(Slope(xyz, a, w2, w1), Slope(xyz, w2, b, w1));
                if (after > before - MinimumGainDegrees)
                    continue;

                // Quad a -> w2 -> b -> w1 keeps the faces' winding as (a, w2, w1) and (w2, b, w1).
                SetFace(faces, f1, a, w2, w1);
                SetFace(faces, f2, w2, b, w1);
                touched[f1] = touched[f2] = true;
                flipsThisPass++;
            }

            flips += flipsThisPass;
            if (flipsThisPass == 0)
                break;
        }

        return flips;
    }

    private static bool TryDirected(int[] faces, int f, int u, int v, out int a, out int b, out int third)
    {
        for (int k = 0; k < 3; k++)
        {
            int x = faces[(f * 3) + k], y = faces[(f * 3) + ((k + 1) % 3)];
            if ((x == u && y == v) || (x == v && y == u))
            {
                a = x;
                b = y;
                third = faces[(f * 3) + ((k + 2) % 3)];
                return true;
            }
        }

        a = b = third = -1;
        return false;
    }

    private static bool TryThird(int[] faces, int f, int a, int b, out int third)
    {
        for (int k = 0; k < 3; k++)
        {
            if (faces[(f * 3) + k] == b && faces[(f * 3) + ((k + 1) % 3)] == a)
            {
                third = faces[(f * 3) + ((k + 2) % 3)];
                return true;
            }
        }

        third = -1;
        return false;
    }

    private static bool StrictlyConvex(double[] p, int a, int b, int w1, int w2)
    {
        double abW1 = Orient(p, a, b, w1), abW2 = Orient(p, a, b, w2);
        double wwA = Orient(p, w1, w2, a), wwB = Orient(p, w1, w2, b);
        return abW1 * abW2 < 0.0 && wwA * wwB < 0.0;
    }

    private static double Orient(double[] p, int a, int b, int c) =>
        ((p[b * 3] - p[a * 3]) * (p[(c * 3) + 1] - p[(a * 3) + 1])) -
        ((p[(b * 3) + 1] - p[(a * 3) + 1]) * (p[c * 3] - p[a * 3]));

    /// <summary>Face slope from horizontal in degrees; 90 for a face with no plan area.</summary>
    private static double Slope(double[] p, int a, int b, int c)
    {
        double ux = p[b * 3] - p[a * 3], uy = p[(b * 3) + 1] - p[(a * 3) + 1], uz = p[(b * 3) + 2] - p[(a * 3) + 2];
        double wx = p[c * 3] - p[a * 3], wy = p[(c * 3) + 1] - p[(a * 3) + 1], wz = p[(c * 3) + 2] - p[(a * 3) + 2];
        double nx = (uy * wz) - (uz * wy), ny = (uz * wx) - (ux * wz), nz = (ux * wy) - (uy * wx);
        double length = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
        return length <= 0.0 ? 90.0 : Math.Acos(Math.Min(1.0, Math.Abs(nz) / length)) * (180.0 / Math.PI);
    }

    private static void SetFace(int[] faces, int f, int a, int b, int c)
    {
        faces[f * 3] = a;
        faces[(f * 3) + 1] = b;
        faces[(f * 3) + 2] = c;
    }
}
