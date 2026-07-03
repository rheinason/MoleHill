using MoleHill.Core.Engine;
using static MoleHill.Core.Engine.MeshFlipGeometry;

namespace MoleHill.Core.Retopo;

/// <summary>
/// Turns a (field-aligned) triangle mesh into a quad-dominant mesh by merging adjacent triangle pairs:
/// every interior non-feature diagonal is scored by the quad it would leave behind (corner angles near
/// 90°, edges aligned to the cross-field, near-planar across the removed diagonal), and pairs are
/// accepted greedily best-first while both triangles are unmatched. Geometry never changes — the same
/// vertex set, every input triangle appears exactly once as a standalone triangle or half a quad — so
/// the output is watertight by construction and can't have holes. Feature edges are never removed, so
/// no quad straddles a crease/breakline/boundary; frozen (retaining-wall) triangles pair only with each
/// other, scored in their own best-fit plane since walls are slivers in XY.
/// </summary>
public static class TriQuadPairer
{
    public sealed class Options
    {
        /// <summary>Samples the 4-RoSy field angle at an XY point; null disables the alignment term.</summary>
        public Func<double, double, double>? ThetaSampler { get; init; }

        /// <summary>
        /// Pairs scoring above this stay triangles (quad-dominant, never quad-at-any-cost). The default
        /// admits the 60/120° rhombi an isotropic triangle mesh naturally produces — the score still
        /// RANKS candidates, so the better-shaped, better-aligned merge wins wherever there is a choice
        /// — while genuinely skewed or folded pairs stay triangles.
        /// </summary>
        public double AcceptThreshold { get; init; } = 0.8;
    }

    public sealed class Result
    {
        /// <summary>Flat quad corner indices (4 per quad), ring-ordered.</summary>
        public int[] Quads { get; init; } = Array.Empty<int>();

        /// <summary>Leftover triangles (3 indices each).</summary>
        public int[] Tris { get; init; } = Array.Empty<int>();

        public int QuadCount => Quads.Length / 4;
    }

    private const double AngleWeight = 1.0;
    private const double AlignmentWeight = 0.6;
    private const double PlanarityWeight = 0.4;

    public static Result Pair(
        double[] vertices,
        int[] faces,
        HashSet<long> featureEdges,
        bool[]? frozenFaces,
        Options options)
    {
        int faceCount = faces.Length / 3;
        var adjacency = new Dictionary<long, (int t0, int o0, int t1, int o1, int count)>(faceCount * 2, IndexedMeshTools.EdgeKeyComparer.Instance);
        for (int t = 0; t < faceCount; t++)
        {
            int a = faces[t * 3], b = faces[t * 3 + 1], c = faces[t * 3 + 2];
            AddIncidence(adjacency, a, b, t, c);
            AddIncidence(adjacency, b, c, t, a);
            AddIncidence(adjacency, c, a, t, b);
        }

        var candidates = new List<(double score, long key, int t0, int t1, int p, int q, int c, int d)>();
        foreach (KeyValuePair<long, (int t0, int o0, int t1, int o1, int count)> entry in adjacency)
        {
            (int t0, int o0, int t1, int o1, int count) e = entry.Value;
            if (e.count != 2)
                continue;
            if (featureEdges.Contains(entry.Key))
                continue;

            bool frozen0 = frozenFaces != null && frozenFaces[e.t0];
            bool frozen1 = frozenFaces != null && frozenFaces[e.t1];
            if (frozen0 != frozen1)
                continue; // never merge a wall triangle with a terrain triangle

            int p = (int)(entry.Key >> 32);
            int q = (int)(entry.Key & 0xFFFFFFFFL);
            double score = ScoreQuad(vertices, p, e.o0, q, e.o1, frozen0, options.ThetaSampler);
            if (double.IsNaN(score) || score > options.AcceptThreshold)
                continue;

            candidates.Add((score, entry.Key, e.t0, e.t1, p, q, e.o0, e.o1));
        }

        candidates.Sort((x, y) => x.score != y.score ? x.score.CompareTo(y.score) : x.key.CompareTo(y.key));

        var used = new bool[faceCount];
        var quads = new List<int>();
        foreach ((double _, long _, int t0, int t1, int p, int q, int c, int d) in candidates)
        {
            if (used[t0] || used[t1])
                continue;
            used[t0] = true;
            used[t1] = true;
            EmitQuad(vertices, quads, p, c, q, d);
        }

        var tris = new List<int>();
        for (int t = 0; t < faceCount; t++)
        {
            if (used[t])
                continue;
            tris.Add(faces[t * 3]);
            tris.Add(faces[t * 3 + 1]);
            tris.Add(faces[t * 3 + 2]);
        }

        return new Result { Quads = quads.ToArray(), Tris = tris.ToArray() };
    }

    /// <summary>
    /// Scores the quad ring p→c→q→d left after removing diagonal (p,q). 0 = perfect aligned square;
    /// NaN = invalid (non-convex / degenerate) — never merged.
    /// </summary>
    private static double ScoreQuad(double[] vertices, int p, int c, int q, int d, bool wall, Func<double, double, double>? thetaSampler)
    {
        Span<double> x = stackalloc double[4];
        Span<double> y = stackalloc double[4];
        if (!ProjectRing(vertices, p, c, q, d, wall, x, y))
            return double.NaN;

        // Convex + consistently wound: consecutive edge cross products all strictly one sign.
        double signRef = 0;
        double angleDeviation = 0;
        for (int i = 0; i < 4; i++)
        {
            int prev = (i + 3) & 3;
            int next = (i + 1) & 3;
            double ax = x[i] - x[prev], ay = y[i] - y[prev];
            double bx = x[next] - x[i], by = y[next] - y[i];
            double cross = (ax * by) - (ay * bx);
            if (Math.Abs(cross) < 1e-16)
                return double.NaN;
            if (signRef == 0)
                signRef = Math.Sign(cross);
            else if (Math.Sign(cross) != signRef)
                return double.NaN;

            double la = Math.Sqrt((ax * ax) + (ay * ay));
            double lb = Math.Sqrt((bx * bx) + (by * by));
            if (la < 1e-12 || lb < 1e-12)
                return double.NaN;
            double cos = Math.Clamp(((-ax * bx) + (-ay * by)) / (la * lb), -1.0, 1.0);
            angleDeviation += Math.Abs(Math.Acos(cos) - (Math.PI / 2.0));
        }

        double score = AngleWeight * (angleDeviation / (2.0 * Math.PI));

        if (!wall && thetaSampler != null)
        {
            double cx = (x[0] + x[1] + x[2] + x[3]) * 0.25;
            double cy = (y[0] + y[1] + y[2] + y[3]) * 0.25;
            double theta = thetaSampler(cx, cy);
            if (!double.IsNaN(theta))
            {
                double misalignment = 0;
                for (int i = 0; i < 4; i++)
                {
                    int next = (i + 1) & 3;
                    double phi = Math.Atan2(y[next] - y[i], x[next] - x[i]);
                    double s = Math.Sin(2.0 * (phi - theta));
                    misalignment += s * s;
                }

                score += AlignmentWeight * (misalignment / 4.0);
            }
        }

        // Planarity across the removed diagonal (1 - |normal agreement|, into [0, 1]). Absolute value:
        // TriangleNormal flips normals upward, which reports a flat VERTICAL pair as opposed.
        double agreement = Math.Abs(NormalAgreement(vertices, p, q, c, p, q, d));
        score += PlanarityWeight * (1.0 - agreement);
        return score;
    }

    /// <summary>
    /// 2D coordinates for the ring p,c,q,d: plain XY for terrain quads; for wall quads (XY slivers) the
    /// ring is projected onto its own best-fit plane so angles/convexity are measured meaningfully.
    /// </summary>
    private static bool ProjectRing(double[] vertices, int p, int c, int q, int d, bool wall, Span<double> x, Span<double> y)
    {
        Span<int> ring = stackalloc int[4];
        ring[0] = p;
        ring[1] = c;
        ring[2] = q;
        ring[3] = d;

        if (!wall)
        {
            for (int i = 0; i < 4; i++)
            {
                x[i] = vertices[ring[i] * 3];
                y[i] = vertices[ring[i] * 3 + 1];
            }

            return true;
        }

        // Plane normal from the two triangles (they share the diagonal, so their mean is stable).
        // TriangleNormal flips normals upward; on a perfectly vertical wall (nz = 0) the two can come
        // out opposed and cancel, so align the second with the first before summing.
        TriangleNormal(vertices, p, q, c, out double n0x, out double n0y, out double n0z);
        TriangleNormal(vertices, p, q, d, out double n1x, out double n1y, out double n1z);
        if ((n0x * n1x) + (n0y * n1y) + (n0z * n1z) < 0)
        {
            n1x = -n1x;
            n1y = -n1y;
            n1z = -n1z;
        }

        double nx = n0x + n1x, ny = n0y + n1y, nz = n0z + n1z;
        double nl = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
        if (nl < 1e-15)
            return false;
        nx /= nl;
        ny /= nl;
        nz /= nl;

        // In-plane basis: u = normalized projection of world Z-cross-normal (any stable perpendicular).
        double ux = -ny, uy = nx, uz = 0;
        double ul = Math.Sqrt((ux * ux) + (uy * uy));
        if (ul < 1e-12)
        {
            ux = 1; uy = 0; uz = 0; // normal is vertical (shouldn't happen for a wall) — fall back
        }
        else
        {
            ux /= ul;
            uy /= ul;
        }

        double vx = (ny * uz) - (nz * uy);
        double vy = (nz * ux) - (nx * uz);
        double vz = (nx * uy) - (ny * ux);

        double ox = vertices[p * 3], oy = vertices[p * 3 + 1], oz = vertices[p * 3 + 2];
        for (int i = 0; i < 4; i++)
        {
            double wxr = vertices[ring[i] * 3] - ox;
            double wyr = vertices[ring[i] * 3 + 1] - oy;
            double wzr = vertices[ring[i] * 3 + 2] - oz;
            x[i] = (wxr * ux) + (wyr * uy) + (wzr * uz);
            y[i] = (wxr * vx) + (wyr * vy) + (wzr * vz);
        }

        return true;
    }

    /// <summary>Emits the ring CCW in XY (upward normal) so quad orientation matches the triangles.</summary>
    private static void EmitQuad(double[] vertices, List<int> quads, int p, int c, int q, int d)
    {
        double area2 =
            ((vertices[c * 3] - vertices[p * 3]) * (vertices[q * 3 + 1] - vertices[p * 3 + 1])) -
            ((vertices[c * 3 + 1] - vertices[p * 3 + 1]) * (vertices[q * 3] - vertices[p * 3]));
        if (area2 < 0)
        {
            quads.Add(p);
            quads.Add(d);
            quads.Add(q);
            quads.Add(c);
        }
        else
        {
            quads.Add(p);
            quads.Add(c);
            quads.Add(q);
            quads.Add(d);
        }
    }
}
