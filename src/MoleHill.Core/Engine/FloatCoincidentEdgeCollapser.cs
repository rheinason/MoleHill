namespace MoleHill.Core.Engine;

/// <summary>
/// Collapses every edge whose two vertices share one single-precision position, wherever that is safe.
/// </summary>
/// <remarks>
/// Every stage mesh is normalized as Rhino normalizes it (<see cref="MeshArrayNormalizer"/>), and that merges
/// vertices whose <b>float</b> positions are equal. Far from the origin a float resolves only to about 1e-6 of
/// the coordinate, so a remesh that leaves two vertices a micron apart has them welded blindly at the hand-off:
/// on a 566k-face terrain 167 m from the origin, 39 such groups became 116 dropped faces and three edges shared
/// by four faces. Those then failed the normalizer's winding check, so every later stage fell back to Rhino's
/// normalization, about 0.45 s each. Collapsing the edge first, only when the link condition holds (the two ends
/// share no neighbour but the corners opposite the edge), is the same weld done without folding the surface.
/// A pair that is not joined by an edge, or whose collapse would fold, is separated instead: the later vertex
/// moves a few float steps further along its own offset from the first, so the normalizer keeps both and the
/// topology stays as the remesh made it. That moves it by micrometres, far inside any model tolerance.
/// </remarks>
public static class FloatCoincidentEdgeCollapser
{
    /// <summary>
    /// Returns the mesh with those edges collapsed, the vertices left coincident separated, and unused vertices
    /// dropped, keeping vertex and face order otherwise; returns the inputs themselves when no two vertices share
    /// a float position.
    /// </summary>
    public static (double[] Vertices, int[] Faces) Collapse(double[] vertices, int[] faces, out int collapsed) =>
        Collapse(vertices, faces, out collapsed, out _);

    /// <inheritdoc cref="Collapse(double[], int[], out int)"/>
    public static (double[] Vertices, int[] Faces) Collapse(double[] vertices, int[] faces, out int collapsed, out int separated)
    {
        collapsed = 0;
        separated = 0;
        int vertexCount = vertices.Length / 3;
        int faceCount = faces.Length / 3;
        if (!MeshArrayNormalizer.HasFloatDuplicates(vertices, vertexCount))
            return (vertices, faces);

        // Groups of float-coincident vertices; almost always there are none.
        var byPosition = new Dictionary<(float, float, float), int>(vertexCount);
        var coincident = new List<(int A, int B)>();
        for (int i = 0; i < vertexCount; i++)
        {
            var key = ((float)vertices[i * 3] + 0f, (float)vertices[i * 3 + 1] + 0f, (float)vertices[i * 3 + 2] + 0f);
            if (byPosition.TryGetValue(key, out int first))
                coincident.Add((first, i));
            else
                byPosition[key] = i;
        }

        if (coincident.Count == 0)
            return (vertices, faces);

        var f = (int[])faces.Clone();
        var alive = new bool[faceCount];
        Array.Fill(alive, true);
        var facesOf = new List<int>?[vertexCount];
        for (int t = 0; t < faceCount; t++)
        {
            for (int k = 0; k < 3; k++)
                (facesOf[f[t * 3 + k]] ??= new List<int>(6)).Add(t);
        }

        var root = new int[vertexCount];
        for (int i = 0; i < vertexCount; i++)
            root[i] = i;
        int Find(int v)
        {
            while (root[v] != v)
                v = root[v] = root[root[v]];
            return v;
        }

        foreach ((int first, int other) in coincident)
        {
            int keep = Find(first), drop = Find(other);
            if (keep == drop)
                continue;

            if (TryCollapse(keep, drop))
            {
                root[drop] = keep;
                collapsed++;
            }
        }

        var outFaces = new List<int>(faces.Length);
        var used = new bool[vertexCount];
        for (int t = 0; t < faceCount; t++)
        {
            if (!alive[t])
                continue;
            for (int k = 0; k < 3; k++)
            {
                outFaces.Add(f[t * 3 + k]);
                used[f[t * 3 + k]] = true;
            }
        }

        var map = new int[vertexCount];
        var outVertices = new List<double>(vertices.Length);
        for (int i = 0; i < vertexCount; i++)
        {
            map[i] = -1;
            if (!used[i])
                continue;
            map[i] = outVertices.Count / 3;
            outVertices.Add(vertices[i * 3]);
            outVertices.Add(vertices[i * 3 + 1]);
            outVertices.Add(vertices[i * 3 + 2]);
        }

        int[] remapped = outFaces.ToArray();
        for (int i = 0; i < remapped.Length; i++)
            remapped[i] = map[remapped[i]];
        double[] result = outVertices.ToArray();
        separated = Separate(result);
        return (result, remapped);

        // Collapses drop into keep when they share an edge and the collapse cannot fold the surface.
        bool TryCollapse(int keep, int drop)
        {
            List<int> keepFaces = facesOf[keep] ?? new List<int>();
            List<int> dropFaces = facesOf[drop] ?? new List<int>();
            var shared = new List<int>(2);
            foreach (int t in dropFaces)
            {
                if (alive[t] && FaceHas(t, keep))
                    shared.Add(t);
            }

            // Not joined by an edge, or a non-manifold edge: leave it.
            if (shared.Count is 0 or > 2)
                return false;

            // Link condition: the only neighbours common to both ends are the corners opposite the edge.
            var opposite = new HashSet<int>();
            foreach (int t in shared)
            {
                for (int k = 0; k < 3; k++)
                {
                    int v = f[t * 3 + k];
                    if (v != keep && v != drop)
                        opposite.Add(v);
                }
            }

            var keepNeighbours = Neighbours(keepFaces);
            foreach (int v in Neighbours(dropFaces))
            {
                if (v != keep && v != drop && keepNeighbours.Contains(v) && !opposite.Contains(v))
                    return false;
            }

            // An interior edge between two border vertices: collapsing it pinches the border into one vertex.
            if (shared.Count == 2 && OnBorder(keep, keepFaces) && OnBorder(drop, dropFaces))
                return false;

            foreach (int t in shared)
                alive[t] = false;
            foreach (int t in dropFaces)
            {
                if (!alive[t])
                    continue;
                for (int k = 0; k < 3; k++)
                {
                    if (f[t * 3 + k] == drop)
                        f[t * 3 + k] = keep;
                }

                keepFaces.Add(t);
            }

            facesOf[keep] = keepFaces;
            facesOf[drop] = null;
            return true;
        }

        bool OnBorder(int v, List<int> incident)
        {
            var uses = new Dictionary<int, int>();
            foreach (int t in incident)
            {
                if (!alive[t])
                    continue;
                for (int k = 0; k < 3; k++)
                {
                    int w = f[t * 3 + k];
                    if (w != v)
                        uses[w] = uses.GetValueOrDefault(w) + 1;
                }
            }

            foreach (int count in uses.Values)
            {
                if (count == 1)
                    return true;
            }

            return false;
        }

        bool FaceHas(int t, int v) => f[t * 3] == v || f[t * 3 + 1] == v || f[t * 3 + 2] == v;

        HashSet<int> Neighbours(List<int> incident)
        {
            var result = new HashSet<int>();
            foreach (int t in incident)
            {
                if (!alive[t])
                    continue;
                for (int k = 0; k < 3; k++)
                    result.Add(f[t * 3 + k]);
            }

            return result;
        }
    }

    /// <summary>
    /// Moves every vertex that still shares a float position with an earlier one along its offset from it,
    /// a few float steps at a time, until its float position is its own. Returns how many moved.
    /// </summary>
    private static int Separate(double[] v)
    {
        int count = v.Length / 3;
        var taken = new Dictionary<(float, float, float), int>(count);
        int moved = 0;
        for (int i = 0; i < count; i++)
        {
            if (taken.TryAdd(Key(v, i), i))
                continue;

            int first = taken[Key(v, i)];
            double dx = v[i * 3] - v[first * 3], dy = v[i * 3 + 1] - v[first * 3 + 1], dz = v[i * 3 + 2] - v[first * 3 + 2];
            double length = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
            if (length == 0.0)
                (dx, dy, dz, length) = (1.0, 0.0, 0.0, 1.0);

            // One float step at the largest coordinate here, then doubled until the position is free.
            double scale = Math.Max(Math.Abs(v[i * 3]), Math.Max(Math.Abs(v[i * 3 + 1]), Math.Abs(v[i * 3 + 2])));
            double step = Math.Max(MathF.BitIncrement((float)scale) - (float)scale, 1e-9) * 2.0;
            double x0 = v[first * 3], y0 = v[first * 3 + 1], z0 = v[first * 3 + 2];
            for (int attempt = 0; attempt < 32 && taken.ContainsKey(Key(v, i)); attempt++, step *= 2.0)
            {
                v[i * 3] = x0 + (dx / length * step);
                v[i * 3 + 1] = y0 + (dy / length * step);
                v[i * 3 + 2] = z0 + (dz / length * step);
            }

            taken.TryAdd(Key(v, i), i);
            moved++;
        }

        return moved;

        static (float, float, float) Key(double[] v, int i) =>
            ((float)v[i * 3] + 0f, (float)v[i * 3 + 1] + 0f, (float)v[i * 3 + 2] + 0f);
    }
}
