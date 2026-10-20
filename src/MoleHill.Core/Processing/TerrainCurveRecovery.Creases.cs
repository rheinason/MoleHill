using MoleHill.Core.Engine;

namespace MoleHill.Core.Processing;

/// <summary>
/// The terrain's own folds and walls for interval recovery. Between interval contours, a designed terrain's batter
/// crests, toes and pad edges were described by strings of spot heights, dozens along each; one crease line describes
/// it. A 2.5D rebuild cannot stand a wall up, so a vertex on one misses by the wall's height whatever is added:
/// the wall is carried by its top and toe creases and its own vertices are not measured.
/// </summary>
public static partial class TerrainCurveRecovery
{
    /// <summary>
    /// Chains of edges folding by at least <paramref name="degrees"/>, broken at junctions, simplified within a fifth
    /// of the tolerances; chains shorter than <paramref name="minimumLength"/> in plan are left to contours and spots.
    /// </summary>
    internal static List<double[]> CreaseLines(IndexedTriMesh terrain, double degrees, double minimumLength,
        double planTolerance, double verticalTolerance)
    {
        var edges = MeshFeatureDetection.DetectCreaseEdges(terrain.Vertices, terrain.Faces, terrain.FaceCount, Math.Cos(degrees * Math.PI / 180));
        var neighbours = new Dictionary<int, List<int>>();
        foreach (var (a, b) in edges)
        {
            (neighbours.TryGetValue(a, out var la) ? la : neighbours[a] = new List<int>()).Add(b);
            (neighbours.TryGetValue(b, out var lb) ? lb : neighbours[b] = new List<int>()).Add(a);
        }
        var used = IndexedMeshTools.CreateEdgeKeySet(edges.Count);
        static long Key(int a, int b) => ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
        var lines = new List<double[]>();
        // Chains start at junctions and ends, then whatever is left is a closed loop.
        foreach (bool loops in new[] { false, true })
            foreach (var (start, list) in neighbours.OrderBy(entry => entry.Key))
            {
                if (!loops && list.Count == 2) continue;
                foreach (int first in list)
                    if (!used.Contains(Key(start, first)))
                        Add(Walk(start, first));
            }
        return lines;

        List<int> Walk(int start, int next)
        {
            var chain = new List<int> { start };
            int previous = start, current = next;
            while (used.Add(Key(previous, current)))
            {
                chain.Add(current);
                List<int> onward = neighbours[current];
                if (onward.Count != 2) break;
                (previous, current) = (current, onward[0] == previous ? onward[1] : onward[0]);
            }
            return chain;
        }

        void Add(List<int> chain)
        {
            var points = new double[chain.Count * 3];
            double plan = 0;
            for (int i = 0; i < chain.Count; i++)
            {
                Array.Copy(terrain.Vertices, chain[i] * 3, points, i * 3, 3);
                if (i > 0) plan += Math.Sqrt(Sq(points[i * 3] - points[i * 3 - 3]) + Sq(points[i * 3 + 1] - points[i * 3 - 2]));
            }
            if (plan < minimumLength) return;
            double[] simple = SimplifyOpen(points, planTolerance * .2, verticalTolerance * .2);
            if (simple.Length >= 6) lines.Add(simple);
        }
    }

    /// <summary>Vertices of faces steeper than <paramref name="degrees"/>: on a wall.</summary>
    internal static bool[] WallVertices(IndexedTriMesh terrain, double degrees)
    {
        var wall = new bool[terrain.VertexCount];
        double cosine = Math.Cos(degrees * Math.PI / 180);
        double[] v = terrain.Vertices;
        for (int f = 0; f < terrain.FaceCount; f++)
        {
            int a = terrain.Faces[f * 3] * 3, b = terrain.Faces[f * 3 + 1] * 3, c = terrain.Faces[f * 3 + 2] * 3;
            double ux = v[b] - v[a], uy = v[b + 1] - v[a + 1], uz = v[b + 2] - v[a + 2];
            double wx = v[c] - v[a], wy = v[c + 1] - v[a + 1], wz = v[c + 2] - v[a + 2];
            double nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx;
            if (Math.Abs(nz) >= cosine * Math.Sqrt(nx * nx + ny * ny + nz * nz)) continue;
            wall[a / 3] = wall[b / 3] = wall[c / 3] = true;
        }
        return wall;
    }
}
