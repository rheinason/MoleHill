namespace MoleHill.Core.Engine;

/// <summary>
/// Shared edge-flip / triangle-adjacency primitives for the mesh passes (<see cref="LocalMeshRefiner"/>
/// and the remeshing engines). All operate on flat XYZ vertex arrays and flat triangle-index face
/// arrays; none move vertices or change Z.
/// </summary>
internal static class MeshFlipGeometry
{
    /// <summary>Canonical undirected edge key (min index in the high word, max in the low word).</summary>
    internal static long EdgeKey(int a, int b) => IndexedMeshTools.GetEdgeKey(a, b);

    /// <summary>
    /// Accumulates edge → incident-triangle adjacency: for each edge, up to two triangles with the vertex
    /// opposite the edge in each. A third incidence marks the edge non-manifold (count &gt; 2) so callers
    /// can skip it.
    /// </summary>
    internal static void AddIncidence(
        Dictionary<long, (int t0, int o0, int t1, int o1, int count)> adjacency, int u, int v, int triangle, int opposite)
    {
        long key = EdgeKey(u, v);
        if (!adjacency.TryGetValue(key, out (int t0, int o0, int t1, int o1, int count) e))
            adjacency[key] = (triangle, opposite, -1, -1, 1);
        else if (e.count == 1)
            adjacency[key] = (e.t0, e.o0, triangle, opposite, 2);
        else
            adjacency[key] = (e.t0, e.o0, e.t1, e.o1, e.count + 1); // >2: non-manifold, never flipped
    }

    /// <summary>Crease (1 - normal agreement) between two triangles given by explicit vertex triples.</summary>
    internal static double Crease(double[] v, int a0, int b0, int c0, int a1, int b1, int c1) =>
        1.0 - NormalAgreement(v, a0, b0, c0, a1, b1, c1);

    /// <summary>Cosine of the angle between the upward normals of two triangles (1 = coplanar/flat).</summary>
    internal static double NormalAgreement(double[] v, int a0, int b0, int c0, int a1, int b1, int c1)
    {
        TriangleNormal(v, a0, b0, c0, out double nx0, out double ny0, out double nz0);
        TriangleNormal(v, a1, b1, c1, out double nx1, out double ny1, out double nz1);
        double l0 = Math.Sqrt((nx0 * nx0) + (ny0 * ny0) + (nz0 * nz0));
        double l1 = Math.Sqrt((nx1 * nx1) + (ny1 * ny1) + (nz1 * nz1));
        if (l0 <= 1e-18 || l1 <= 1e-18)
            return -1.0;
        return ((nx0 * nx1) + (ny0 * ny1) + (nz0 * nz1)) / (l0 * l1);
    }

    internal static void TriangleNormal(double[] v, int a, int b, int c, out double nx, out double ny, out double nz)
    {
        double ux = v[b * 3] - v[a * 3], uy = v[b * 3 + 1] - v[a * 3 + 1], uz = v[b * 3 + 2] - v[a * 3 + 2];
        double wx = v[c * 3] - v[a * 3], wy = v[c * 3 + 1] - v[a * 3 + 1], wz = v[c * 3 + 2] - v[a * 3 + 2];
        nx = (uy * wz) - (uz * wy);
        ny = (uz * wx) - (ux * wz);
        nz = (ux * wy) - (uy * wx);
        if (nz < 0.0)
        {
            nx = -nx;
            ny = -ny;
            nz = -nz;
        }
    }

    /// <summary>True when quad a-c-b-d is convex, so swapping diagonal a–b for c–d stays valid.</summary>
    internal static bool QuadIsConvexForFlip(double[] v, int a, int b, int c, int d)
    {
        // c and d must straddle a–b (they do for two triangles sharing a–b, unless degenerate), and
        // a and b must straddle c–d (the new diagonal) for the quad to be convex.
        double abc = Cross2D(v, a, b, c);
        double abd = Cross2D(v, a, b, d);
        double cda = Cross2D(v, c, d, a);
        double cdb = Cross2D(v, c, d, b);
        return abc * abd < 0.0 && cda * cdb < 0.0;
    }

    internal static double Cross2D(double[] v, int a, int b, int p)
    {
        double abx = v[b * 3] - v[a * 3], aby = v[b * 3 + 1] - v[a * 3 + 1];
        double apx = v[p * 3] - v[a * 3], apy = v[p * 3 + 1] - v[a * 3 + 1];
        return (abx * apy) - (aby * apx);
    }

    internal static double MinTriangleAngle(double[] v, int a, int b, int c)
    {
        double angA = CornerAngle(v, a, b, c);
        double angB = CornerAngle(v, b, c, a);
        double angC = CornerAngle(v, c, a, b);
        return Math.Min(angA, Math.Min(angB, angC));
    }

    internal static double CornerAngle(double[] v, int p, int q, int r)
    {
        double e1x = v[q * 3] - v[p * 3], e1y = v[q * 3 + 1] - v[p * 3 + 1];
        double e2x = v[r * 3] - v[p * 3], e2y = v[r * 3 + 1] - v[p * 3 + 1];
        double l1 = Math.Sqrt((e1x * e1x) + (e1y * e1y));
        double l2 = Math.Sqrt((e2x * e2x) + (e2y * e2y));
        if (l1 <= 1e-15 || l2 <= 1e-15)
            return 0.0;
        double cos = Math.Clamp(((e1x * e2x) + (e1y * e2y)) / (l1 * l2), -1.0, 1.0);
        return Math.Acos(cos);
    }

    /// <summary>Writes a triangle into the face array oriented CCW in XY (upward normal).</summary>
    internal static void WriteOrientedFace(double[] v, int[] faces, int triangle, int p, int q, int r)
    {
        if (Cross2D(v, p, q, r) < 0.0)
            (q, r) = (r, q);
        faces[triangle * 3] = p;
        faces[triangle * 3 + 1] = q;
        faces[triangle * 3 + 2] = r;
    }
}
