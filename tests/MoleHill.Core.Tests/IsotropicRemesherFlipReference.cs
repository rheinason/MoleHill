using MoleHill.Core.Engine;
using static MoleHill.Core.Engine.MeshFlipGeometry;

namespace MoleHill.Core.Tests;

/// <summary>
/// The dictionary-driven flip phase exactly as <see cref="IsotropicRemesher"/> ran it before
/// <see cref="FlipEdgeIndex"/> (2026-09-29), kept as the oracle the indexed version must match flip for
/// flip. The faster version is only acceptable because it is not a different algorithm.
/// </summary>
internal static class IsotropicRemesherFlipReference
{
    private const double FlipAngleImproveEps = 1e-3;
    private const double FlipMinAngleFloorRad = 20.0 * Math.PI / 180.0;
    private const double FlipFieldImproveEps = 0.05;
    private const int MaxFlipSweeps = 16;

    public static int FlipForQuality(IsotropicRemesher.MeshState state)
    {
        int totalFlips = 0;
        double[] vertices = state.Verts.ToArray();
        int sweepFaceCount = state.FaceCount;
        var adjacency = new Dictionary<long, (int t0, int o0, int t1, int o1, int count)>(sweepFaceCount * 2, IndexedMeshTools.EdgeKeyComparer.Instance);
        var touched = new bool[sweepFaceCount];
        var createdEdges = new HashSet<long>(IndexedMeshTools.EdgeKeyComparer.Instance);

        for (int sweep = 0; sweep < MaxFlipSweeps; sweep++)
        {
            int faceCount = state.FaceCount;
            adjacency.Clear();
            createdEdges.Clear();
            if (touched.Length < faceCount)
                touched = new bool[faceCount];
            else
                Array.Clear(touched, 0, faceCount);

            for (int t = 0; t < faceCount; t++)
            {
                if (!state.IsLive(t))
                    continue;
                int a = state.Tris[t * 3], b = state.Tris[t * 3 + 1], c = state.Tris[t * 3 + 2];
                AddIncidence(adjacency, a, b, t, c);
                AddIncidence(adjacency, b, c, t, a);
                AddIncidence(adjacency, c, a, t, b);
            }

            int flips = 0;
            foreach (KeyValuePair<long, (int t0, int o0, int t1, int o1, int count)> entry in adjacency)
            {
                (int t0, int o0, int t1, int o1, int count) e = entry.Value;
                if (e.count != 2)
                    continue;
                if (state.FeatureEdges.ContainsKey(entry.Key))
                    continue;
                if (state.FaceFrozen[e.t0] || state.FaceFrozen[e.t1])
                    continue;
                if (touched[e.t0] || touched[e.t1])
                    continue;

                int p = (int)(entry.Key >> 32);
                int q = (int)(entry.Key & 0xFFFFFFFFL);
                int c = e.o0;
                int d = e.o1;

                long newKey = EdgeKey(c, d);
                if (adjacency.ContainsKey(newKey) || createdEdges.Contains(newKey))
                    continue;
                if (!QuadIsConvexForFlip(vertices, p, q, c, d))
                    continue;

                double minAfter = Math.Min(MinTriangleAngle(vertices, p, c, d), MinTriangleAngle(vertices, c, q, d));
                if (state.Field == null)
                {
                    double minBefore = Math.Min(MinTriangleAngle(vertices, p, q, c), MinTriangleAngle(vertices, p, q, d));
                    if (minAfter <= minBefore + FlipAngleImproveEps)
                        continue;
                }
                else
                {
                    if (minAfter < FlipMinAngleFloorRad)
                        continue;

                    double cx = 0.25 * (vertices[p * 3] + vertices[c * 3] + vertices[q * 3] + vertices[d * 3]);
                    double cy = 0.25 * (vertices[p * 3 + 1] + vertices[c * 3 + 1] + vertices[q * 3 + 1] + vertices[d * 3 + 1]);
                    double theta = state.Field.SampleTheta(cx, cy, double.NaN);
                    if (double.IsNaN(theta))
                    {
                        double minBefore = Math.Min(MinTriangleAngle(vertices, p, q, c), MinTriangleAngle(vertices, p, q, d));
                        if (minAfter <= minBefore + FlipAngleImproveEps)
                            continue;
                    }
                    else
                    {
                        double sPQ = DiagonalFieldScore(vertices[q * 3] - vertices[p * 3], vertices[q * 3 + 1] - vertices[p * 3 + 1], theta);
                        double sCD = DiagonalFieldScore(vertices[d * 3] - vertices[c * 3], vertices[d * 3 + 1] - vertices[c * 3 + 1], theta);
                        if (sCD <= sPQ + FlipFieldImproveEps)
                            continue;
                    }
                }

                WriteOrientedFaceToList(vertices, state.Tris, e.t0, p, c, d);
                WriteOrientedFaceToList(vertices, state.Tris, e.t1, c, q, d);
                createdEdges.Add(newKey);
                touched[e.t0] = true;
                touched[e.t1] = true;
                flips++;
            }

            totalFlips += flips;
            if (flips == 0)
                break;
        }

        return totalFlips;
    }

    private static void WriteOrientedFaceToList(double[] vertices, List<int> tris, int triangle, int p, int q, int r)
    {
        if (Cross2D(vertices, p, q, r) < 0.0)
            (q, r) = (r, q);
        tris[triangle * 3] = p;
        tris[triangle * 3 + 1] = q;
        tris[triangle * 3 + 2] = r;
    }

    private static double DiagonalFieldScore(double dx, double dy, double theta)
    {
        double s = Math.Sin(2.0 * (Math.Atan2(dy, dx) - theta));
        return s * s;
    }
}
