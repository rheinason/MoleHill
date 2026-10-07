using MoleHill.Core.Geometry;

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
    /// Scratch kept between calls, strongly held. Every buffer here is a large-object-heap array at terrain
    /// scale, and a large-object allocation made while a background collection runs waits for it: measured
    /// as a 6 ms Grade Pad Output Mesh taking 35 ms on half the cold builds, with a GC pause of 0.5 ms. A
    /// weakly held pool emptied at every full collection, so it allocated afresh exactly when that cost.
    /// </summary>
    /// <remarks>
    /// A few slots per element type (at most three buffers are out at once), each the smallest free buffer
    /// that fits; a miss allocates with an eighth of headroom so an edit that adds a few vertices reuses it.
    /// Buffers over <see cref="MaxRetainedLength"/> elements are not kept: below that, the most this holds
    /// is a few times the largest terrain normalized so far, tens of MB at 1.6M faces.
    /// </remarks>
    private static class Scratch<T>
    {
        private const int Slots = 4;
        private const int MaxRetainedLength = 1 << 23;
        private static readonly T[]?[] Free = new T[]?[Slots];

        public static T[] Rent(int length)
        {
            if (length == 0)
                return Array.Empty<T>();
            lock (Free)
            {
                int best = -1;
                for (int i = 0; i < Slots; i++)
                {
                    if (Free[i] is { } candidate && candidate.Length >= length &&
                        (best < 0 || candidate.Length < Free[best]!.Length))
                    {
                        best = i;
                    }
                }

                if (best >= 0)
                {
                    T[] array = Free[best]!;
                    Free[best] = null;
                    return array;
                }
            }

            return new T[Math.Min(Array.MaxLength, (long)length + (length >> 3))];
        }

        public static void Return(T[] array)
        {
            if (array.Length == 0 || array.Length > MaxRetainedLength)
                return;
            lock (Free)
            {
                // An empty slot, else the smallest buffer shorter than this one.
                int target = -1;
                for (int i = 0; i < Slots; i++)
                {
                    if (Free[i] == null)
                    {
                        target = i;
                        break;
                    }

                    if (Free[i]!.Length < array.Length && (target < 0 || Free[i]!.Length < Free[target]!.Length))
                        target = i;
                }

                if (target >= 0)
                    Free[target] = array;
            }
        }
    }

    /// <summary>
    /// Normalizes a triangle mesh. Returns false when face windings are inconsistent (Rhino's
    /// <c>UnifyNormals</c> would reorient faces) or an index is out of range; the outputs are then undefined.
    /// Inputs are never modified. Unchanged vertex and face arrays may be shared with the outputs;
    /// callers that mutate either afterwards must make their own copy.
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
        int[] f = faces;
        bool ownsFaces = false;
        if (HasFloatDuplicates(vertices, vertexCount))
        {
            EnsureOwnedFaces(ref f, faceCount, ref ownsFaces);
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

            for (int i = 0; i < faceCount * 3; i++)
                f[i] = map[f[i]];
            v = merged.ToArray();
            vc = groups;
        }

        // 2. Cull unused.
        (v, vc) = CullUnused(v, vc, ref f, faceCount, ref ownsFaces);

        // 3. Cull degenerate faces.
        // Copy only when normalization changes faces, then compact that owned copy in place.
        // An unchanged topology can share its input array, as unchanged vertices already do.
        int kept = 0;
        for (int t = 0; t < faceCount; t++)
        {
            int a = f[t * 3], b = f[t * 3 + 1], c = f[t * 3 + 2];
            if (a == b || b == c || c == a)
            {
                EnsureOwnedFaces(ref f, faceCount, ref ownsFaces);
                continue;
            }
            double ux = v[b * 3] - v[a * 3], uy = v[b * 3 + 1] - v[a * 3 + 1], uz = v[b * 3 + 2] - v[a * 3 + 2];
            double wx = v[c * 3] - v[a * 3], wy = v[c * 3 + 1] - v[a * 3 + 1], wz = v[c * 3 + 2] - v[a * 3 + 2];
            double nx = (uy * wz) - (uz * wy), ny = (uz * wx) - (ux * wz), nz = (ux * wy) - (uy * wx);
            if (nx == 0.0 && ny == 0.0 && nz == 0.0)
            {
                EnsureOwnedFaces(ref f, faceCount, ref ownsFaces);
                continue;
            }
            if (ownsFaces)
            {
                f[kept] = a;
                f[kept + 1] = b;
                f[kept + 2] = c;
            }
            kept += 3;
        }

        if (ownsFaces && kept != f.Length)
            Array.Resize(ref f, kept);
        int fc = kept / 3;

        // 4. Consistent winding, or Rhino decides.
        if (!HasConsistentWinding(f, fc))
            return false;

        // 5. Compact.
        (v, vc) = CullUnused(v, vc, ref f, fc, ref ownsFaces);
        if (f.Length != fc * 3)
            EnsureOwnedFaces(ref f, fc, ref ownsFaces);
        outputVertices = v;
        outputVertexCount = vc;
        outputFaces = f;
        outputFaceCount = fc;
        return true;
    }

    /// <summary>
    /// Removes each face <see cref="TryNormalize"/> would cull as three exactly collinear but distinct corners
    /// (a cap: one corner lying on the edge between the other two) by splitting the face across that long
    /// edge at the middle corner. Culling a cap leaves its two short edges and the long edge across from it
    /// each used once - a zero-area slit in the terrain. The split covers exactly the same surface. A cap on
    /// the border, with nothing across its long edge, is simply dropped: the border then runs through the
    /// middle corner instead, with no slit. Returns <paramref name="faces"/> itself when there is nothing to do.
    /// With <paramref name="maxApexDistance"/> above zero, a face whose middle corner lies within that distance
    /// of its long edge counts as a cap too: float-rounded terrain carries thousands of caps a few micrometres
    /// thick, and a face-by-face splitter cannot re-triangulate one, so it would leave its neighbours' splits
    /// on single-use edges.
    /// </summary>
    public static int[] SplitCollinearCaps(
        double[] vertices, int[] faces, int faceCount, out int outputFaceCount, out int capsResolved, double maxApexDistance = 0.0)
    {
        outputFaceCount = faceCount;
        capsResolved = 0;
        int vertexCount = vertices.Length / 3;
        bool[]? capVertex = null;
        for (int t = 0; t < faceCount; t++)
        {
            int a0 = faces[t * 3], b0 = faces[t * 3 + 1], c0 = faces[t * 3 + 2];
            if (!IsCap(vertices, a0, b0, c0, maxApexDistance))
                continue;
            capVertex ??= new bool[vertexCount];
            capVertex[a0] = capVertex[b0] = capVertex[c0] = true;
        }

        if (capVertex == null)
            return faces;

        // Only faces sharing a vertex with a cap can take part: a split rewrites the face across a cap's long
        // edge (both ends are cap vertices) and every face it creates holds the cap's middle corner, so any
        // later cap has a long edge with an indexed end. Indexing just those keeps a pass local on a terrain
        // of hundreds of thousands of faces.
        var work = new List<int>(faces.AsSpan(0, faceCount * 3).ToArray());
        var removed = new List<bool>(new bool[faceCount]);
        var candidates = new List<int>();
        var edgeFaces = IndexedMeshTools.CreateEdgeKeyMap<List<int>>(64);
        for (int t = 0; t < faceCount; t++)
        {
            if (!capVertex[work[t * 3]] && !capVertex[work[t * 3 + 1]] && !capVertex[work[t * 3 + 2]])
                continue;
            candidates.Add(t);
            for (int k = 0; k < 3; k++)
                AddEdgeFace(edgeFaces, work[t * 3 + k], work[t * 3 + ((k + 1) % 3)], t);
        }

        // Two caps can face each other across one long edge. Splitting one at the other's middle corner
        // leaves shorter caps whose long edges face real faces, so repeat until a pass changes nothing.
        const int MaxPasses = 16;
        for (int pass = 0; pass < MaxPasses; pass++)
        {
            int resolvedThisPass = 0;
            int candidateCount = candidates.Count;
            for (int ci = 0; ci < candidateCount; ci++)
            {
                int cap = candidates[ci];
                if (removed[cap])
                    continue;

                int a = work[cap * 3], b = work[cap * 3 + 1], c = work[cap * 3 + 2];
                if (!IsCap(vertices, a, b, c, maxApexDistance) || !TryOrderCap(vertices, a, b, c, out int p, out int q, out int r))
                    continue;

                long longEdge = IndexedMeshTools.GetEdgeKey(p, q);
                int across = -1, acrossCount = 0;
                if (!edgeFaces.TryGetValue(longEdge, out List<int>? onLongEdge))
                    continue;
                foreach (int f in onLongEdge)
                {
                    if (f != cap && !removed[f])
                    {
                        across = f;
                        acrossCount++;
                    }
                }

                if (acrossCount > 1)
                    continue; // non-manifold: leave it

                removed[cap] = true;
                for (int k = 0; k < 3; k++)
                    edgeFaces[IndexedMeshTools.GetEdgeKey(work[cap * 3 + k], work[cap * 3 + ((k + 1) % 3)])].Remove(cap);
                resolvedThisPass++;
                if (acrossCount == 0)
                    continue;

                // Split the neighbour at r, keeping its winding: (u, v, s) becomes (u, r, s) and (r, v, s).
                int n = across;
                int u = -1, v = -1, s = -1;
                for (int k = 0; k < 3; k++)
                {
                    int x = work[n * 3 + k], y = work[n * 3 + ((k + 1) % 3)];
                    if ((x == p && y == q) || (x == q && y == p))
                    {
                        u = x;
                        v = y;
                        s = work[n * 3 + ((k + 2) % 3)];
                    }
                }

                if (s == r)
                {
                    // The cap's mirror: the same three corners wound the other way. Together they cover
                    // nothing and their short edges pair up with each other's, so both simply go.
                    removed[n] = true;
                    for (int k = 0; k < 3; k++)
                        edgeFaces[IndexedMeshTools.GetEdgeKey(work[n * 3 + k], work[n * 3 + ((k + 1) % 3)])].Remove(n);
                    continue;
                }

                int added = work.Count / 3;
                work[n * 3] = u;
                work[n * 3 + 1] = r;
                work[n * 3 + 2] = s;
                work.Add(r);
                work.Add(v);
                work.Add(s);
                removed.Add(false);
                candidates.Add(added);
                edgeFaces[longEdge].Remove(n);
                edgeFaces[IndexedMeshTools.GetEdgeKey(v, s)].Remove(n);
                AddEdgeFace(edgeFaces, u, r, n);
                AddEdgeFace(edgeFaces, r, s, n);
                AddEdgeFace(edgeFaces, r, v, added);
                AddEdgeFace(edgeFaces, r, s, added);
                AddEdgeFace(edgeFaces, v, s, added);
            }

            capsResolved += resolvedThisPass;
            if (resolvedThisPass == 0)
                break;
        }

        if (capsResolved == 0)
            return faces;

        // Where many vertices lie on one line (a flat graded edge), splitting caps can produce a face and its
        // mirror - the same corners wound the other way. The pair covers nothing and doubles every edge it
        // touches, so both go.
        var byCorners = new Dictionary<(int, int, int), int>();
        foreach (int t in candidates)
        {
            if (removed[t])
                continue;

            int a = work[t * 3], b = work[t * 3 + 1], c = work[t * 3 + 2];
            int lo = Math.Min(a, Math.Min(b, c)), hi = Math.Max(a, Math.Max(b, c)), mid = a + b + c - lo - hi;
            if (byCorners.Remove((lo, mid, hi), out int other) && !SameWinding(work, t, other))
            {
                removed[t] = true;
                removed[other] = true;
                continue;
            }

            byCorners[(lo, mid, hi)] = t;
        }

        var output = new List<int>(work.Count);
        for (int t = 0; t < work.Count / 3; t++)
        {
            if (removed[t])
                continue;
            output.Add(work[t * 3]);
            output.Add(work[t * 3 + 1]);
            output.Add(work[t * 3 + 2]);
        }

        outputFaceCount = output.Count / 3;
        return output.ToArray();

        static void AddEdgeFace(Dictionary<long, List<int>> map, int x, int y, int face)
        {
            long key = IndexedMeshTools.GetEdgeKey(x, y);
            if (!map.TryGetValue(key, out var list))
                map[key] = list = new List<int>(2);
            if (!list.Contains(face))
                list.Add(face);
        }
    }

    private static bool SameWinding(List<int> faces, int t, int other)
    {
        int a = faces[t * 3], b = faces[t * 3 + 1];
        for (int k = 0; k < 3; k++)
        {
            if (faces[other * 3 + k] == a)
                return faces[other * 3 + ((k + 1) % 3)] == b;
        }

        return false;
    }

    private static bool IsCap(double[] v, int a, int b, int c, double maxApexDistance) =>
        a != b && b != c && c != a &&
        (ExactlyCollinear(v, a, b, c) || (maxApexDistance > 0.0 && IsThinCap(v, a, b, c, maxApexDistance)));

    private static bool IsThinCap(double[] v, int a, int b, int c, double maxApexDistance)
    {
        if (!TryOrderCap(v, a, b, c, out int p, out int q, out int r))
            return false;

        double dx = v[q * 3] - v[p * 3], dy = v[q * 3 + 1] - v[p * 3 + 1], dz = v[q * 3 + 2] - v[p * 3 + 2];
        double wx = v[r * 3] - v[p * 3], wy = v[r * 3 + 1] - v[p * 3 + 1], wz = v[r * 3 + 2] - v[p * 3 + 2];
        double lengthSquared = (dx * dx) + (dy * dy) + (dz * dz);
        double t = ((wx * dx) + (wy * dy) + (wz * dz)) / lengthSquared;
        if (t <= 0.0 || t >= 1.0)
            return false;

        double cx = (wy * dz) - (wz * dy), cy = (wz * dx) - (wx * dz), cz = (wx * dy) - (wy * dx);
        return ((cx * cx) + (cy * cy) + (cz * cz)) / lengthSquared <= maxApexDistance * maxApexDistance;
    }

    private static bool ExactlyCollinear(double[] v, int a, int b, int c)
    {
        double ux = v[b * 3] - v[a * 3], uy = v[b * 3 + 1] - v[a * 3 + 1], uz = v[b * 3 + 2] - v[a * 3 + 2];
        double wx = v[c * 3] - v[a * 3], wy = v[c * 3 + 1] - v[a * 3 + 1], wz = v[c * 3 + 2] - v[a * 3 + 2];
        return (uy * wz) - (uz * wy) == 0.0 && (uz * wx) - (ux * wz) == 0.0 && (ux * wy) - (uy * wx) == 0.0;
    }

    /// <summary>The cap's two outer corners (its long edge) and the corner between them.</summary>
    private static bool TryOrderCap(double[] v, int a, int b, int c, out int p, out int q, out int r)
    {
        double ab = Geometry2D.DistanceSquared3(v, a, b), bc = Geometry2D.DistanceSquared3(v, b, c), ca = Geometry2D.DistanceSquared3(v, c, a);
        (p, q, r) = ab >= bc && ab >= ca ? (a, b, c) : bc >= ca ? (b, c, a) : (c, a, b);
        return Geometry2D.DistanceSquared3(v, p, r) > 0.0 && Geometry2D.DistanceSquared3(v, r, q) > 0.0;
    }

    /// <summary>
    /// True when <see cref="TryNormalize"/> would hand back these arrays unchanged, given that they came from a
    /// normalized mesh by moving vertices only in height. Height cannot change a face's plan winding or which
    /// vertices are used, so only two things can: two vertices meeting at one float position (a merge, which
    /// re-sorts every vertex) and a face whose corners become exactly collinear (a cull). A wall's vertical
    /// edge, two vertices at one plan position, is where either can happen.
    /// </summary>
    public static bool HeightsKeepNormalForm(double[] vertices, int vertexCount, int[] faces, int faceCount)
    {
        if (HasFloatDuplicates(vertices, vertexCount))
            return false;

        for (int t = 0; t < faceCount; t++)
        {
            int a = faces[t * 3], b = faces[t * 3 + 1], c = faces[t * 3 + 2];
            double ux = vertices[b * 3] - vertices[a * 3], uy = vertices[b * 3 + 1] - vertices[a * 3 + 1], uz = vertices[b * 3 + 2] - vertices[a * 3 + 2];
            double wx = vertices[c * 3] - vertices[a * 3], wy = vertices[c * 3 + 1] - vertices[a * 3 + 1], wz = vertices[c * 3 + 2] - vertices[a * 3 + 2];
            if ((uy * wz) - (uz * wy) == 0.0 && (uz * wx) - (ux * wz) == 0.0 && (ux * wy) - (uy * wx) == 0.0)
                return false;
        }

        return true;
    }

    private static bool SameFloat(double[] v, int a, int b) =>
        (float)v[a * 3] == (float)v[b * 3] && (float)v[a * 3 + 1] == (float)v[b * 3 + 1] && (float)v[a * 3 + 2] == (float)v[b * 3 + 2];

    /// <summary>
    /// True when two vertices share a float-rounded position. Vertices are split by a hash of that position into
    /// partitions checked in parallel; equal positions always land in one partition.
    /// </summary>
    internal static bool HasFloatDuplicates(double[] v, int vertexCount)
    {
        const int Partitions = 64;
        ulong[] hashes = Scratch<ulong>.Rent(vertexCount);
        int[] members = Scratch<int>.Rent(vertexCount);
        int[]? tables = null;
        try
        {
            Parallel.For(0, (vertexCount + 65535) / 65536, block =>
            {
                int end = Math.Min(vertexCount, (block + 1) * 65536);
                for (int i = block * 65536; i < end; i++)
                    hashes[i] = PositionHash(v, i);
            });

            var start = new int[Partitions + 1];
            for (int i = 0; i < vertexCount; i++)
                start[(int)(hashes[i] >> 58) + 1]++;
            for (int p = 0; p < Partitions; p++)
                start[p + 1] += start[p];
            var fill = (int[])start.Clone();
            for (int i = 0; i < vertexCount; i++)
                members[fill[(int)(hashes[i] >> 58)]++] = i;

            // One open-addressing table per partition, all carved from one buffer: a power of two at most
            // half full, so linear probing stays short. Tables hold vertex indices, not positions.
            var tableStart = new int[Partitions + 1];
            for (int p = 0; p < Partitions; p++)
            {
                int count = start[p + 1] - start[p];
                int capacity = 0;
                if (count >= 2)
                {
                    capacity = 4;
                    while (capacity < (long)count * 2)
                        capacity = checked(capacity * 2);
                }

                tableStart[p + 1] = checked(tableStart[p] + capacity);
            }

            int[] slab = tables = Scratch<int>.Rent(tableStart[Partitions]);
            int found = 0;
            Parallel.For(0, Partitions, (p, state) =>
            {
                int offset = tableStart[p];
                int capacity = tableStart[p + 1] - offset;
                if (capacity == 0)
                    return;
                Span<int> seen = slab.AsSpan(offset, capacity);
                seen.Clear();
                for (int k = start[p]; k < start[p + 1]; k++)
                {
                    int i = members[k];
                    int slot = (int)(hashes[i] & (uint)(capacity - 1));
                    while (seen[slot] != 0)
                    {
                        int other = seen[slot] - 1;
                        if (((float)v[i * 3]).Equals((float)v[other * 3]) &&
                            ((float)v[i * 3 + 1]).Equals((float)v[other * 3 + 1]) &&
                            ((float)v[i * 3 + 2]).Equals((float)v[other * 3 + 2]))
                        {
                            Interlocked.Exchange(ref found, 1);
                            state.Stop();
                            return;
                        }
                        slot = (slot + 1) & (capacity - 1);
                    }
                    seen[slot] = i + 1;
                }
            });
            return found != 0;
        }
        finally
        {
            if (tables != null)
                Scratch<int>.Return(tables);
            Scratch<ulong>.Return(hashes);
            Scratch<int>.Return(members);
        }
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
    private static (double[] V, int Count) CullUnused(double[] v, int vc, ref int[] f, int fc, ref bool ownsFaces)
    {
        bool[] used = Scratch<bool>.Rent(vc);
        Array.Clear(used, 0, vc);
        try
        {
            for (int i = 0; i < fc * 3; i++)
                used[f[i]] = true;
            int count = 0;
            for (int i = 0; i < vc; i++)
            {
                if (used[i])
                    count++;
            }
            if (count == vc)
            {
                if (v.Length == vc * 3)
                    return (v, vc);
                var trimmed = new double[vc * 3];
                Array.Copy(v, trimmed, vc * 3);
                return (trimmed, vc);
            }

            int[] map = Scratch<int>.Rent(vc);
            try
            {
                int next = 0;
                for (int i = 0; i < vc; i++)
                    map[i] = used[i] ? next++ : -1;
                var result = new double[count * 3];
                for (int i = 0; i < vc; i++)
                {
                    if (map[i] < 0)
                        continue;
                    result[map[i] * 3] = v[i * 3];
                    result[map[i] * 3 + 1] = v[i * 3 + 1];
                    result[map[i] * 3 + 2] = v[i * 3 + 2];
                }

                EnsureOwnedFaces(ref f, fc, ref ownsFaces);
                for (int i = 0; i < fc * 3; i++)
                    f[i] = map[f[i]];
                return (result, count);
            }
            finally
            {
                Scratch<int>.Return(map);
            }
        }
        finally
        {
            Scratch<bool>.Return(used);
        }
    }

    private static void EnsureOwnedFaces(ref int[] faces, int faceCount, ref bool ownsFaces)
    {
        if (ownsFaces)
            return;
        var copy = new int[faceCount * 3];
        Array.Copy(faces, copy, copy.Length);
        faces = copy;
        ownsFaces = true;
    }

    /// <summary>True when no directed edge occurs twice (the condition under which UnifyNormals flips nothing).</summary>
    public static bool HasConsistentWinding(int[] f, int fc)
    {
        // Directed edges bucketed by their start vertex (a counting sort), then each vertex's few outgoing
        // edges checked for a repeat: linear, where sorting every directed edge was 390 ms at 6.4M faces.
        int vertexCount = 0;
        for (int i = 0; i < fc * 3; i++)
            vertexCount = Math.Max(vertexCount, f[i] + 1);
        int[] start = Scratch<int>.Rent(vertexCount + 1);
        int[] fill = Scratch<int>.Rent(vertexCount + 1);
        int[] targets = Scratch<int>.Rent(fc * 3);
        Array.Clear(start, 0, vertexCount + 1);
        try
        {
            for (int i = 0; i < fc * 3; i++)
                start[f[i] + 1]++;
            for (int i = 0; i < vertexCount; i++)
                start[i + 1] += start[i];
            Array.Copy(start, fill, vertexCount + 1);
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
        finally
        {
            Scratch<int>.Return(start);
            Scratch<int>.Return(fill);
            Scratch<int>.Return(targets);
        }
    }
}
