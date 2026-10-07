namespace MoleHill.Core.Engine;

/// <summary>
/// Feature lines detected from a mesh's own geometry. Shared by the remeshers (Surface, Local, Isotropic via
/// <see cref="FeaturePolylineGraph"/>) and Retopo's cross-field, which all keep creases as edges.
/// </summary>
internal static class MeshFeatureDetection
{
    /// <summary>
    /// Interior edges whose two faces meet at a dihedral angle ≥ the threshold (cos ≤
    /// <paramref name="cosThreshold"/>) — the mesh's crease/feature lines (batter toes, slope breaks).
    /// Returned as original-vertex index pairs. Boundary edges (one face) are excluded; they are already
    /// constrained as the mesh outline.
    /// </summary>
    public static List<(int a, int b)> DetectCreaseEdges(double[] vertices, int[] faces, int faceCount, double cosThreshold)
    {
        // Edge keys are (min << 32) | max, so the default long hash (lo ^ hi) collapses adjacent
        // mesh indices into a handful of buckets and degrades this to a quadratic scan. Every
        // edge-keyed table must use EdgeKeyComparer.
        var edgeFaces = new Dictionary<long, (int f0, int f1, int count)>(faceCount * 2, IndexedMeshTools.EdgeKeyComparer.Instance);
        for (int f = 0; f < faceCount; f++)
        {
            int v0 = faces[f * 3], v1 = faces[f * 3 + 1], v2 = faces[f * 3 + 2];
            Accumulate(v0, v1, f);
            Accumulate(v1, v2, f);
            Accumulate(v2, v0, f);
        }

        var creases = new List<(int, int)>();
        foreach (KeyValuePair<long, (int f0, int f1, int count)> entry in edgeFaces)
        {
            if (entry.Value.count != 2)
                continue;

            FaceNormal(vertices, faces, entry.Value.f0, out double n0x, out double n0y, out double n0z);
            FaceNormal(vertices, faces, entry.Value.f1, out double n1x, out double n1y, out double n1z);
            double l0 = Math.Sqrt((n0x * n0x) + (n0y * n0y) + (n0z * n0z));
            double l1 = Math.Sqrt((n1x * n1x) + (n1y * n1y) + (n1z * n1z));
            if (l0 <= 1e-18 || l1 <= 1e-18)
                continue;

            double cos = ((n0x * n1x) + (n0y * n1y) + (n0z * n1z)) / (l0 * l1);
            if (cos <= cosThreshold)
                creases.Add(((int)(entry.Key >> 32), (int)(entry.Key & 0xFFFFFFFFL)));
        }

        return creases;

        void Accumulate(int a, int b, int face)
        {
            long key = ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
            if (!edgeFaces.TryGetValue(key, out (int f0, int f1, int count) e))
                edgeFaces[key] = (face, -1, 1);
            else if (e.count == 1)
                edgeFaces[key] = (e.f0, face, 2);
            else
                edgeFaces[key] = (e.f0, e.f1, e.count + 1);
        }
    }

    private static void FaceNormal(double[] v, int[] faces, int face, out double nx, out double ny, out double nz)
    {
        int a = faces[face * 3], b = faces[face * 3 + 1], c = faces[face * 3 + 2];
        double ux = v[b * 3] - v[a * 3], uy = v[b * 3 + 1] - v[a * 3 + 1], uz = v[b * 3 + 2] - v[a * 3 + 2];
        double wx = v[c * 3] - v[a * 3], wy = v[c * 3 + 1] - v[a * 3 + 1], wz = v[c * 3 + 2] - v[a * 3 + 2];
        nx = (uy * wz) - (uz * wy);
        ny = (uz * wx) - (ux * wz);
        nz = (ux * wy) - (uy * wx);
    }
}
