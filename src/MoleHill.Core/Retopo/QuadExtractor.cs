namespace MoleHill.Core.Retopo;

/// <summary>
/// Stage 2 quad extraction: turns a field-guided parametrization (u,v) into a quad mesh by reading its
/// integer lattice. Within each triangle u and v are affine, so every point where <c>u=i ∧ v=j</c> (both
/// integer) that lands inside the triangle is a quad-grid vertex; a lattice vertex is identified by its
/// integer pair (i,j), which makes cross-face dedup exact and trivial. A quad is emitted for cell (i,j) when
/// all four corners exist; missing corners (near singularities / the boundary) simply leave a gap, so the
/// result is quad-<b>dominant</b>. Z is taken from the source triangle (exact on the 2.5D surface).
/// </summary>
public static class QuadExtractor
{
    public sealed class QuadMesh
    {
        public double[] Vertices { get; init; } = Array.Empty<double>();

        public int[] Quads { get; init; } = Array.Empty<int>();

        public int[] Tris { get; init; } = Array.Empty<int>();

        public int VertexCount => Vertices.Length / 3;

        public int QuadCount => Quads.Length / 4;
    }

    // Guard against a blown-up parametrization spraying a face with a huge lattice.
    private const int MaxLatticePointsPerFace = 20000;

    public static QuadMesh Extract(double[] vertices, int[] faces, double[] u, double[] v, double tolerance, bool[]? active = null)
    {
        int faceCount = faces.Length / 3;
        double inside = -Math.Max(tolerance, 1e-9);

        var latticeVertices = new Dictionary<long, int>();
        var outVertices = new List<double>();

        for (int f = 0; f < faceCount; f++)
        {
            if (active != null && !active[f])
                continue; // masked-out face (e.g. a wall) — no quads emitted here

            int a = faces[f * 3], b = faces[f * 3 + 1], c = faces[f * 3 + 2];
            double u0 = u[a], u1 = u[b], u2 = u[c];
            double v0 = v[a], v1 = v[b], v2 = v[c];

            double du1 = u1 - u0, du2 = u2 - u0;
            double dv1 = v1 - v0, dv2 = v2 - v0;
            double det = (du1 * dv2) - (du2 * dv1);
            if (Math.Abs(det) < 1e-12)
                continue; // parametrization degenerate on this face

            double invDet = 1.0 / det;

            int iMin = (int)Math.Ceiling(Math.Min(u0, Math.Min(u1, u2)) - 1e-9);
            int iMax = (int)Math.Floor(Math.Max(u0, Math.Max(u1, u2)) + 1e-9);
            int jMin = (int)Math.Ceiling(Math.Min(v0, Math.Min(v1, v2)) - 1e-9);
            int jMax = (int)Math.Floor(Math.Max(v0, Math.Max(v1, v2)) + 1e-9);
            if (iMax < iMin || jMax < jMin)
                continue;
            if ((long)(iMax - iMin + 1) * (jMax - jMin + 1) > MaxLatticePointsPerFace)
                continue;

            double ax = vertices[a * 3], ay = vertices[a * 3 + 1], az = vertices[a * 3 + 2];
            double bx = vertices[b * 3], by = vertices[b * 3 + 1], bz = vertices[b * 3 + 2];
            double cx = vertices[c * 3], cy = vertices[c * 3 + 1], cz = vertices[c * 3 + 2];

            for (int i = iMin; i <= iMax; i++)
            {
                for (int j = jMin; j <= jMax; j++)
                {
                    double ru = i - u0, rv = j - v0;
                    double lambda1 = ((ru * dv2) - (du2 * rv)) * invDet;
                    double lambda2 = ((du1 * rv) - (ru * dv1)) * invDet;
                    double lambda0 = 1.0 - lambda1 - lambda2;
                    if (lambda0 < inside || lambda1 < inside || lambda2 < inside)
                        continue;

                    long key = ((long)i << 32) | (uint)j;
                    if (latticeVertices.ContainsKey(key))
                        continue;

                    double x = ax + ((bx - ax) * lambda1) + ((cx - ax) * lambda2);
                    double y = ay + ((by - ay) * lambda1) + ((cy - ay) * lambda2);
                    double z = az + ((bz - az) * lambda1) + ((cz - az) * lambda2);

                    latticeVertices[key] = outVertices.Count / 3;
                    outVertices.Add(x);
                    outVertices.Add(y);
                    outVertices.Add(z);
                }
            }
        }

        var quads = new List<int>();
        foreach (KeyValuePair<long, int> entry in latticeVertices)
        {
            int i = (int)(entry.Key >> 32);
            int j = (int)(entry.Key & 0xFFFFFFFFL);
            if (latticeVertices.TryGetValue(((long)(i + 1) << 32) | (uint)j, out int right) &&
                latticeVertices.TryGetValue(((long)(i + 1) << 32) | (uint)(j + 1), out int diag) &&
                latticeVertices.TryGetValue(((long)i << 32) | (uint)(j + 1), out int up))
            {
                quads.Add(entry.Value);
                quads.Add(right);
                quads.Add(diag);
                quads.Add(up);
            }
        }

        return new QuadMesh
        {
            Vertices = outVertices.ToArray(),
            Quads = quads.ToArray(),
            Tris = Array.Empty<int>()
        };
    }
}
