using MoleHill.Core.Engine;

namespace MoleHill.Core.Scan;

/// <summary>Coalesces a closed triangulated building shell into connected planar faces with hole loops.</summary>
public static class ScanBuildingFaces
{
    public sealed record Face(IReadOnlyList<double[]> Loops);

    public static IReadOnlyList<Face> Coalesce(IndexedTriMesh mesh, double tolerance, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(tolerance) || tolerance <= 0)
            throw new ArgumentOutOfRangeException(nameof(tolerance));
        var adjacent = new int[mesh.FaceCount * 3]; Array.Fill(adjacent, -1);
        var edges = IndexedMeshTools.CreateEdgeKeyMap<int>(mesh.FaceCount * 2);
        var normals = new double[mesh.FaceCount * 3];
        for (int face = 0; face < mesh.FaceCount; face++)
        {
            if ((face & 1023) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            int a = mesh.Faces[face * 3] * 3, b = mesh.Faces[face * 3 + 1] * 3, c = mesh.Faces[face * 3 + 2] * 3;
            double ux = mesh.Vertices[b] - mesh.Vertices[a], uy = mesh.Vertices[b + 1] - mesh.Vertices[a + 1], uz = mesh.Vertices[b + 2] - mesh.Vertices[a + 2];
            double vx = mesh.Vertices[c] - mesh.Vertices[a], vy = mesh.Vertices[c + 1] - mesh.Vertices[a + 1], vz = mesh.Vertices[c + 2] - mesh.Vertices[a + 2];
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (length > 0)
            {
                normals[face * 3] = nx / length; normals[face * 3 + 1] = ny / length; normals[face * 3 + 2] = nz / length;
            }
            for (int k = 0; k < 3; k++)
            {
                int p = mesh.Faces[face * 3 + k], q = mesh.Faces[face * 3 + (k + 1) % 3];
                long key = ((long)Math.Min(p, q) << 32) | (uint)Math.Max(p, q);
                if (edges.TryGetValue(key, out int owner))
                {
                    adjacent[face * 3 + k] = owner / 3;
                    adjacent[owner] = face;
                }
                else
                    edges.Add(key, face * 3 + k);
            }
        }
        var group = new int[mesh.FaceCount]; Array.Fill(group, -1);
        var groups = new List<List<int>>();
        var queue = new Queue<int>();
        for (int seed = 0; seed < mesh.FaceCount; seed++)
        {
            if (group[seed] >= 0)
                continue;
            cancellationToken.ThrowIfCancellationRequested();
            int id = groups.Count;
            var members = new List<int>(); groups.Add(members);
            group[seed] = id; queue.Enqueue(seed);
            while (queue.TryDequeue(out int face))
            {
                members.Add(face);
                for (int k = 0; k < 3; k++)
                {
                    int next = adjacent[face * 3 + k];
                    if (next < 0 || group[next] >= 0 || !Coplanar(seed, next))
                        continue;
                    group[next] = id; queue.Enqueue(next);
                }
            }
        }
        var vertexGroups = new HashSet<int>[mesh.VertexCount];
        for (int i = 0; i < mesh.VertexCount; i++)
            vertexGroups[i] = new();
        for (int face = 0; face < mesh.FaceCount; face++)
            for (int k = 0; k < 3; k++)
                vertexGroups[mesh.Faces[face * 3 + k]].Add(group[face]);
        var output = new List<Face>();
        for (int id = 0; id < groups.Count; id++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outgoing = new Dictionary<int, List<int>>();
            foreach (int face in groups[id])
                for (int k = 0; k < 3; k++)
                {
                    int neighbour = adjacent[face * 3 + k];
                    if (neighbour >= 0 && group[neighbour] == id)
                        continue;
                    int a = mesh.Faces[face * 3 + k], b = mesh.Faces[face * 3 + (k + 1) % 3];
                    if (!outgoing.TryGetValue(a, out List<int>? next))
                        outgoing.Add(a, next = new List<int>());
                    next.Add(b);
                }
            // A pinched planar region is safer as separate triangles than a self-touching trim loop.
            // The shell is still closed; normal regions, including courtyards, coalesce to light faces.
            if (outgoing.Count == 0 || outgoing.Values.Any(next => next.Count != 1))
            {
                AddTriangles(groups[id]);
                continue;
            }
            var pending = outgoing.Keys.ToHashSet();
            var loops = new List<List<int>>();
            bool valid = true;
            while (pending.Count > 0 && valid)
            {
                int start = pending.First(), current = start;
                var loop = new List<int>();
                do
                {
                    if (!pending.Remove(current) || !outgoing.TryGetValue(current, out List<int>? next))
                    {
                        valid = false;
                        break;
                    }
                    loop.Add(current); current = next[0];
                }
                while (current != start);
                if (loop.Count < 3)
                    valid = false;
                loops.Add(loop);
            }
            if (!valid)
            {
                AddTriangles(groups[id]);
                continue;
            }
            foreach (List<int> loop in loops)
            {
                for (int i = loop.Count - 1; i >= 0 && loop.Count > 3; i--)
                {
                    int a = loop[(i + loop.Count - 1) % loop.Count], b = loop[i], c = loop[(i + 1) % loop.Count];
                    if (vertexGroups[b].Count <= 2 && Collinear(a, b, c))
                        loop.RemoveAt(i);
                }
            }
            output.Add(new Face(loops.Select(loop => loop.SelectMany(vertex => mesh.Vertices.Skip(vertex * 3).Take(3)).ToArray()).ToArray()));
        }
        return output;

        bool Coplanar(int a, int b)
        {
            double dot = normals[a * 3] * normals[b * 3] + normals[a * 3 + 1] * normals[b * 3 + 1] + normals[a * 3 + 2] * normals[b * 3 + 2];
            if (dot < 1 - 1e-7)
                return false;
            int origin = mesh.Faces[a * 3] * 3;
            for (int k = 0; k < 3; k++)
            {
                int vertex = mesh.Faces[b * 3 + k] * 3;
                double distance = normals[a * 3] * (mesh.Vertices[vertex] - mesh.Vertices[origin]) +
                    normals[a * 3 + 1] * (mesh.Vertices[vertex + 1] - mesh.Vertices[origin + 1]) +
                    normals[a * 3 + 2] * (mesh.Vertices[vertex + 2] - mesh.Vertices[origin + 2]);
                if (Math.Abs(distance) > tolerance)
                    return false;
            }
            return true;
        }
        bool Collinear(int a, int b, int c)
        {
            double ux = mesh.Vertices[b * 3] - mesh.Vertices[a * 3], uy = mesh.Vertices[b * 3 + 1] - mesh.Vertices[a * 3 + 1], uz = mesh.Vertices[b * 3 + 2] - mesh.Vertices[a * 3 + 2];
            double vx = mesh.Vertices[c * 3] - mesh.Vertices[a * 3], vy = mesh.Vertices[c * 3 + 1] - mesh.Vertices[a * 3 + 1], vz = mesh.Vertices[c * 3 + 2] - mesh.Vertices[a * 3 + 2];
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            double length = Math.Sqrt(vx * vx + vy * vy + vz * vz);
            return length > 0 && Math.Sqrt(nx * nx + ny * ny + nz * nz) <= tolerance * length &&
                ux * vx + uy * vy + uz * vz >= 0 && ux * vx + uy * vy + uz * vz <= length * length;
        }
        void AddTriangles(List<int> members)
        {
            foreach (int face in members)
            {
                var xyz = new double[9];
                for (int k = 0; k < 3; k++)
                    Array.Copy(mesh.Vertices, mesh.Faces[face * 3 + k] * 3, xyz, k * 3, 3);
                output.Add(new Face(new[] { xyz }));
            }
        }
    }
}
