using MoleHill.Core.Engine;
using MoleHill.Core.Geometry;

namespace MoleHill.Core.Grading;

/// <summary>
/// Incremental-Delaunay edge flips around newly inserted vertices, in plan.
///
/// For each seed vertex p, the edges opposite p are tested: if the vertex across such an edge lies inside
/// the circumcircle of p's triangle, the edge is flipped so p connects to it, and the two new outer edges
/// are tested in turn. This is exactly the restructuring an incremental Delaunay insertion of p performs,
/// and it touches nothing whose circumcircle does not contain a seed — so the rest of an upstream surface,
/// deliberately non-Delaunay or not, keeps its edges.
///
/// Only strictly convex quads are flipped, so no face ever inverts, and winding is preserved.
/// </summary>
internal static class LawsonFlipper
{
    /// <summary>Returns the number of flips made. <paramref name="faces"/> is rewritten in place.</summary>
    internal static int Run(
        double[] vertices,
        int[] faces,
        int faceCount,
        IReadOnlyList<int> seeds,
        Func<int, int, bool> isConstrainedEdge,
        Func<int, bool> isLockedFace)
    {
        if (seeds.Count == 0 || faceCount == 0)
            return 0;

        var edgeFaces = new Dictionary<long, (int A, int B)>(faceCount * 2, IndexedMeshTools.EdgeKeyComparer.Instance);
        for (int f = 0; f < faceCount; f++)
        {
            for (int e = 0; e < 3; e++)
                AddEdgeFace(edgeFaces, faces[(f * 3) + e], faces[(f * 3) + ((e + 1) % 3)], f);
        }

        var seedSet = new HashSet<int>(seeds);
        var incident = new Dictionary<int, List<int>>();
        for (int f = 0; f < faceCount; f++)
        {
            for (int k = 0; k < 3; k++)
            {
                int v = faces[(f * 3) + k];
                if (!seedSet.Contains(v))
                    continue;
                if (!incident.TryGetValue(v, out var list))
                    incident[v] = list = new List<int>(6);
                list.Add(f);
            }
        }

        // A safety cap only: Lawson insertion flips terminate, but a malformed input must not spin.
        int flipBudget = Math.Max(1024, faceCount * 8);
        int flips = 0;
        var stack = new Stack<(int A, int B)>();
        foreach (int p in seeds)
        {
            if (!incident.TryGetValue(p, out var star))
                continue;

            stack.Clear();
            foreach (int f in star)
            {
                if (TryOpposite(faces, f, p, out int a, out int b))
                    stack.Push((a, b));
            }

            while (stack.Count > 0 && flips < flipBudget)
            {
                (int a, int b) = stack.Pop();
                if (!edgeFaces.TryGetValue(EdgeKey(a, b), out var pair) || pair.B < 0)
                    continue; // boundary or non-manifold

                int f1 = FaceHolding(faces, pair.A, p) ? pair.A : FaceHolding(faces, pair.B, p) ? pair.B : -1;
                if (f1 < 0)
                    continue;
                int f2 = f1 == pair.A ? pair.B : pair.A;
                int q = ThirdVertex(faces, f2, a, b);
                if (q < 0 || q == p || isLockedFace(f1) || isLockedFace(f2) || isConstrainedEdge(a, b))
                    continue;
                if (!ShouldFlip(vertices, p, a, b, q))
                    continue;

                // Keep f1's winding: orient (p, a, b) as stored, then the two new faces are (p, a, q)
                // and (p, q, b) in that same sense.
                (int u, int w) = StoredOrder(faces, f1, p, a, b);
                faces[f1 * 3] = p; faces[(f1 * 3) + 1] = u; faces[(f1 * 3) + 2] = q;
                faces[f2 * 3] = p; faces[(f2 * 3) + 1] = q; faces[(f2 * 3) + 2] = w;

                edgeFaces.Remove(EdgeKey(a, b));
                edgeFaces[EdgeKey(p, q)] = (f1, f2);
                ReplaceFace(edgeFaces, u, q, f2, f1);
                ReplaceFace(edgeFaces, w, p, f1, f2);
                flips++;

                stack.Push((u, q));
                stack.Push((q, w));
            }
        }

        return flips;
    }

    private static bool ShouldFlip(double[] v, int p, int a, int b, int q)
    {
        double px = v[p * 3], py = v[(p * 3) + 1];
        double ax = v[a * 3], ay = v[(a * 3) + 1];
        double bx = v[b * 3], by = v[(b * 3) + 1];
        double qx = v[q * 3], qy = v[(q * 3) + 1];

        // Strictly convex quad: p and q on opposite sides of ab, a and b on opposite sides of pq.
        double abp = Geometry2D.Orient(ax, ay, bx, by, px, py), abq = Geometry2D.Orient(ax, ay, bx, by, qx, qy);
        double pqa = Geometry2D.Orient(px, py, qx, qy, ax, ay), pqb = Geometry2D.Orient(px, py, qx, qy, bx, by);
        if (abp * abq >= 0.0 || pqa * pqb >= 0.0)
            return false;

        // In-circle, with (p, a, b) taken counter-clockwise.
        if (abp < 0.0)
            (ax, ay, bx, by) = (bx, by, ax, ay);
        double adx = px - qx, ady = py - qy, bdx = ax - qx, bdy = ay - qy, cdx = bx - qx, cdy = by - qy;
        double det = (((adx * adx) + (ady * ady)) * ((bdx * cdy) - (cdx * bdy))) -
                     (((bdx * bdx) + (bdy * bdy)) * ((adx * cdy) - (cdx * ady))) +
                     (((cdx * cdx) + (cdy * cdy)) * ((adx * bdy) - (bdx * ady)));
        double scale = Math.Max(Math.Max(Math.Abs(adx), Math.Abs(ady)), Math.Max(Math.Max(Math.Abs(bdx), Math.Abs(bdy)), Math.Max(Math.Abs(cdx), Math.Abs(cdy))));
        return det > 1e-12 * scale * scale * scale * scale;
    }

    private static long EdgeKey(int a, int b) => IndexedMeshTools.GetEdgeKey(a, b);

    private static void AddEdgeFace(Dictionary<long, (int A, int B)> edgeFaces, int a, int b, int face)
    {
        long key = EdgeKey(a, b);
        if (!edgeFaces.TryGetValue(key, out var pair))
            edgeFaces[key] = (face, -1);
        else if (pair.A >= 0)
            edgeFaces[key] = pair.B < 0 ? (pair.A, face) : (-1, -1); // non-manifold: never flipped
        // else already non-manifold: a fourth face must not revive it as (-1, face)
    }

    private static void ReplaceFace(Dictionary<long, (int A, int B)> edgeFaces, int a, int b, int from, int to)
    {
        long key = EdgeKey(a, b);
        if (!edgeFaces.TryGetValue(key, out var pair))
            return;
        edgeFaces[key] = pair.A == from ? (to, pair.B) : pair.B == from ? (pair.A, to) : pair;
    }

    private static bool FaceHolding(int[] faces, int f, int v) =>
        f >= 0 && (faces[f * 3] == v || faces[(f * 3) + 1] == v || faces[(f * 3) + 2] == v);

    private static bool TryOpposite(int[] faces, int f, int p, out int a, out int b)
    {
        for (int k = 0; k < 3; k++)
        {
            if (faces[(f * 3) + k] != p)
                continue;
            a = faces[(f * 3) + ((k + 1) % 3)];
            b = faces[(f * 3) + ((k + 2) % 3)];
            return true;
        }

        a = b = -1;
        return false;
    }

    private static int ThirdVertex(int[] faces, int f, int a, int b)
    {
        for (int k = 0; k < 3; k++)
        {
            int v = faces[(f * 3) + k];
            if (v != a && v != b)
                return v;
        }

        return -1;
    }

    /// <summary>The face's other two vertices in the order they follow <paramref name="p"/>.</summary>
    private static (int U, int W) StoredOrder(int[] faces, int f, int p, int a, int b)
    {
        for (int k = 0; k < 3; k++)
        {
            if (faces[(f * 3) + k] == p)
                return (faces[(f * 3) + ((k + 1) % 3)], faces[(f * 3) + ((k + 2) % 3)]);
        }

        return (a, b);
    }
}
