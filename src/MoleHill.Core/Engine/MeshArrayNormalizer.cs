namespace MoleHill.Core.Engine;

/// <summary>
/// Rhino's mesh normalization (the Rhino host's <c>NormalizeMeshInPlace</c>) reproduced on flat arrays, so a
/// stage can hand on the exact arrays Rhino would have produced without building and reading back a Rhino mesh.
/// </summary>
/// <remarks>
/// The rules, measured against RhinoCommon 8.35 (docs/architecture.md, "Stage meshes without Rhino"):
/// <list type="number">
/// <item><c>CombineIdentical(true, true)</c> merges vertices whose <b>float</b>-rounded positions are equal.
/// When it merges anything it re-sorts every vertex, used or not, descending by float x, then y, then z;
/// each merged group keeps the double position of its last-added member. With nothing to merge it
/// changes nothing.</item>
/// <item><c>CullUnused</c> drops unreferenced vertices, keeping order.</item>
/// <item><c>CullDegenerateFaces</c> drops a face with a repeated vertex or three exactly collinear corners
/// (in doubles: a 1e-12 sliver stays), keeping order.</item>
/// <item><c>UnifyNormals</c> flips nothing when no directed edge repeats; this returns false otherwise, and the
/// caller lets Rhino decide.</item>
/// <item><c>Compact</c> drops vertices left unused by the face cull, keeping order.</item>
/// </list>
/// </remarks>
public static class MeshArrayNormalizer
{
    /// <summary>
    /// Normalizes a triangle mesh. Returns false when face windings are inconsistent (Rhino's
    /// <c>UnifyNormals</c> would reorient faces) or an index is out of range; the outputs are then undefined.
    /// </summary>
    public static bool TryNormalize(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        out double[] outputVertices,
        out int outputVertexCount,
        out int[] outputFaces,
        out int outputFaceCount)
    {
        outputVertices = Array.Empty<double>();
        outputFaces = Array.Empty<int>();
        outputVertexCount = outputFaceCount = 0;
        for (int i = 0; i < faceCount * 3; i++)
        {
            if ((uint)faces[i] >= (uint)vertexCount)
                return false;
        }

        // 1. Combine identical (float) positions.
        double[] v = vertices;
        int vc = vertexCount;
        int[] f = new int[faceCount * 3];
        Array.Copy(faces, f, faceCount * 3);
        if (HasFloatDuplicates(vertices, vertexCount))
        {
            var order = new int[vertexCount];
            for (int i = 0; i < vertexCount; i++)
                order[i] = i;
            Array.Sort(order, (a, b) =>
            {
                int c = ((float)vertices[b * 3]).CompareTo((float)vertices[a * 3]);
                if (c != 0) return c;
                c = ((float)vertices[b * 3 + 1]).CompareTo((float)vertices[a * 3 + 1]);
                if (c != 0) return c;
                c = ((float)vertices[b * 3 + 2]).CompareTo((float)vertices[a * 3 + 2]);
                return c != 0 ? c : a.CompareTo(b);
            });

            var map = new int[vertexCount];
            var merged = new List<double>(vertexCount * 3);
            int groups = 0;
            for (int s = 0; s < vertexCount;)
            {
                int e = s + 1;
                while (e < vertexCount && SameFloat(vertices, order[s], order[e]))
                    e++;
                int keep = order[s];
                for (int k = s; k < e; k++)
                {
                    keep = Math.Max(keep, order[k]);
                    map[order[k]] = groups;
                }

                merged.Add(vertices[keep * 3]);
                merged.Add(vertices[keep * 3 + 1]);
                merged.Add(vertices[keep * 3 + 2]);
                groups++;
                s = e;
            }

            for (int i = 0; i < f.Length; i++)
                f[i] = map[f[i]];
            v = merged.ToArray();
            vc = groups;
        }

        // 2. Cull unused.
        (v, vc) = CullUnused(v, vc, f, f.Length / 3);

        // 3. Cull degenerate faces.
        var kept = new List<int>(f.Length);
        for (int t = 0; t < f.Length / 3; t++)
        {
            int a = f[t * 3], b = f[t * 3 + 1], c = f[t * 3 + 2];
            if (a == b || b == c || c == a)
                continue;
            double ux = v[b * 3] - v[a * 3], uy = v[b * 3 + 1] - v[a * 3 + 1], uz = v[b * 3 + 2] - v[a * 3 + 2];
            double wx = v[c * 3] - v[a * 3], wy = v[c * 3 + 1] - v[a * 3 + 1], wz = v[c * 3 + 2] - v[a * 3 + 2];
            double nx = (uy * wz) - (uz * wy), ny = (uz * wx) - (ux * wz), nz = (ux * wy) - (uy * wx);
            if (nx == 0.0 && ny == 0.0 && nz == 0.0)
                continue;
            kept.Add(a);
            kept.Add(b);
            kept.Add(c);
        }

        f = kept.ToArray();
        int fc = f.Length / 3;

        // 4. Consistent winding, or Rhino decides.
        if (!HasConsistentWinding(f, fc))
            return false;

        // 5. Compact.
        (v, vc) = CullUnused(v, vc, f, fc);
        outputVertices = v;
        outputVertexCount = vc;
        outputFaces = f;
        outputFaceCount = fc;
        return true;
    }

    private static bool SameFloat(double[] v, int a, int b) =>
        (float)v[a * 3] == (float)v[b * 3] && (float)v[a * 3 + 1] == (float)v[b * 3 + 1] && (float)v[a * 3 + 2] == (float)v[b * 3 + 2];

    /// <summary>
    /// True when two vertices share a float-rounded position. Vertices are split by a hash of that position into
    /// partitions checked in parallel; equal positions always land in one partition.
    /// </summary>
    private static bool HasFloatDuplicates(double[] v, int vertexCount)
    {
        const int Partitions = 64;
        var hashes = new ulong[vertexCount];
        Parallel.For(0, (vertexCount + 65535) / 65536, block =>
        {
            int end = Math.Min(vertexCount, (block + 1) * 65536);
            for (int i = block * 65536; i < end; i++)
                hashes[i] = PositionHash(v, i);
        });

        var start = new int[Partitions + 1];
        foreach (ulong h in hashes)
            start[(int)(h >> 58) + 1]++;
        for (int p = 0; p < Partitions; p++)
            start[p + 1] += start[p];
        var fill = (int[])start.Clone();
        var members = new int[vertexCount];
        for (int i = 0; i < vertexCount; i++)
            members[fill[(int)(hashes[i] >> 58)]++] = i;

        int found = 0;
        Parallel.For(0, Partitions, (p, state) =>
        {
            var seen = new HashSet<(float, float, float)>(start[p + 1] - start[p]);
            for (int k = start[p]; k < start[p + 1]; k++)
            {
                int i = members[k];
                if (!seen.Add(((float)v[i * 3], (float)v[i * 3 + 1], (float)v[i * 3 + 2])))
                {
                    Interlocked.Exchange(ref found, 1);
                    state.Stop();
                    return;
                }
            }
        });
        return found != 0;
    }

    private static ulong PositionHash(double[] v, int i)
    {
        ulong x = BitConverter.SingleToUInt32Bits((float)v[i * 3] + 0f);
        ulong y = BitConverter.SingleToUInt32Bits((float)v[i * 3 + 1] + 0f);
        ulong z = BitConverter.SingleToUInt32Bits((float)v[i * 3 + 2] + 0f);
        ulong h = (x * 0x9E3779B97F4A7C15UL) ^ (y * 0xC2B2AE3D27D4EB4FUL) ^ (z * 0x165667B19E3779F9UL);
        h ^= h >> 31;
        h *= 0xBF58476D1CE4E5B9UL;
        return h ^ (h >> 29);
    }

    /// <summary>Drops unreferenced vertices, keeping order, and renumbers <paramref name="f"/> in place.</summary>
    private static (double[] V, int Count) CullUnused(double[] v, int vc, int[] f, int fc)
    {
        var used = new bool[vc];
        for (int i = 0; i < fc * 3; i++)
            used[f[i]] = true;
        int count = 0;
        var map = new int[vc];
        for (int i = 0; i < vc; i++)
            map[i] = used[i] ? count++ : -1;
        if (count == vc)
        {
            if (v.Length == vc * 3)
                return (v, vc);
            var trimmed = new double[vc * 3];
            Array.Copy(v, trimmed, vc * 3);
            return (trimmed, vc);
        }

        var result = new double[count * 3];
        for (int i = 0; i < vc; i++)
        {
            if (map[i] < 0)
                continue;
            result[map[i] * 3] = v[i * 3];
            result[map[i] * 3 + 1] = v[i * 3 + 1];
            result[map[i] * 3 + 2] = v[i * 3 + 2];
        }

        for (int i = 0; i < fc * 3; i++)
            f[i] = map[f[i]];
        return (result, count);
    }

    /// <summary>True when no directed edge occurs twice (the condition under which UnifyNormals flips nothing).</summary>
    public static bool HasConsistentWinding(int[] f, int fc)
    {
        // Directed edges bucketed by their start vertex (a counting sort), then each vertex's few outgoing
        // edges checked for a repeat: linear, where sorting every directed edge was 390 ms at 6.4M faces.
        int vertexCount = 0;
        for (int i = 0; i < fc * 3; i++)
            vertexCount = Math.Max(vertexCount, f[i] + 1);
        var start = new int[vertexCount + 1];
        for (int i = 0; i < fc * 3; i++)
            start[f[i] + 1]++;
        for (int i = 0; i < vertexCount; i++)
            start[i + 1] += start[i];
        var fill = (int[])start.Clone();
        var targets = new int[fc * 3];
        for (int t = 0; t < fc; t++)
        {
            for (int k = 0; k < 3; k++)
                targets[fill[f[t * 3 + k]]++] = f[t * 3 + ((k + 1) % 3)];
        }

        int repeated = 0;
        Parallel.For(0, (vertexCount + 16383) / 16384, (block, state) =>
        {
            int end = Math.Min(vertexCount, (block + 1) * 16384);
            for (int a = block * 16384; a < end; a++)
            {
                for (int p = start[a]; p < start[a + 1]; p++)
                {
                    for (int q = p + 1; q < start[a + 1]; q++)
                    {
                        if (targets[p] == targets[q])
                        {
                            Interlocked.Exchange(ref repeated, 1);
                            state.Stop();
                            return;
                        }
                    }
                }
            }
        });
        return repeated == 0;
    }
}
