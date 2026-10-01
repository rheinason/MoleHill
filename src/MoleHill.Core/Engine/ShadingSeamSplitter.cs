namespace MoleHill.Core.Engine;

/// <summary>
/// Splits a terrain's vertices along wall seams so each side shades with its own normal (the Rhino host's
/// <c>TerrainPresentationMesh</c> explains why), working on flat arrays.
/// </summary>
/// <remarks>
/// The Rhino-side version built Rhino's edge topology to find the seams: 33 of the 44 ms it took on a
/// 100,911-face terrain, on the UI thread at the first draw after every edit, and most terrains have no
/// seam at all, so that cost found nothing. Here the seams are found from a vertex-to-face table, the faces
/// are classified in parallel, and a terrain without a seam is reported as such without building anything.
/// </remarks>
public static class ShadingSeamSplitter
{
    public sealed class Result
    {
        /// <summary>The input vertices, then one copy per extra shading group of a seam vertex.</summary>
        public required int[] SourceVertexOfCopy { get; init; }

        /// <summary>Faces renumbered onto the split vertices (same face order as the input).</summary>
        public required int[] Faces { get; init; }

        /// <summary>Per vertex (input vertices first, then the copies), x, y, z of its unit normal.</summary>
        public required float[] Normals { get; init; }

        public required int SeamEdgeCount { get; init; }
    }

    /// <summary>
    /// The split mesh, or null when nothing needs splitting. A face steeper than
    /// <paramref name="wallMinSlopeDegrees"/> is a wall; a seam is an edge between a wall and a non-wall face,
    /// or between two walls creased beyond <paramref name="mitreCreaseDegrees"/>. Normals of vertices not on a
    /// seam are taken from <paramref name="vertexNormals"/>; each shading group of a seam vertex gets the
    /// mean of its own faces' unit normals.
    /// </summary>
    /// <remarks>
    /// Unweighted, as Rhino computes a vertex normal. Weighting by area was measured live to tilt the flat
    /// ground beside a wall by 30–37°: one large face just under the wall slope shares the ground's group
    /// and outweighed the small flat faces around it.
    /// </remarks>
    public static Result? Split(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        float[] vertexNormals,
        double wallMinSlopeDegrees,
        double mitreCreaseDegrees)
    {
        if (faceCount == 0)
            return null;

        // Face normals (unnormalized, so their length is twice the area) and the wall flag.
        var areaNormals = new double[faceCount * 3];
        var isWall = new bool[faceCount];
        double wallLimit = Math.Cos(wallMinSlopeDegrees * Math.PI / 180.0);
        Parallel.For(0, (faceCount + 16383) / 16384, block =>
        {
            int end = Math.Min(faceCount, (block + 1) * 16384);
            for (int t = block * 16384; t < end; t++)
            {
                int a = faces[t * 3], b = faces[t * 3 + 1], c = faces[t * 3 + 2];
                double ux = vertices[b * 3] - vertices[a * 3], uy = vertices[b * 3 + 1] - vertices[a * 3 + 1], uz = vertices[b * 3 + 2] - vertices[a * 3 + 2];
                double wx = vertices[c * 3] - vertices[a * 3], wy = vertices[c * 3 + 1] - vertices[a * 3 + 1], wz = vertices[c * 3 + 2] - vertices[a * 3 + 2];
                double nx = (uy * wz) - (uz * wy), ny = (uz * wx) - (ux * wz), nz = (ux * wy) - (uy * wx);
                areaNormals[t * 3] = nx;
                areaNormals[t * 3 + 1] = ny;
                areaNormals[t * 3 + 2] = nz;
                double length = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
                isWall[t] = length > 0 && Math.Abs(nz) / length <= wallLimit;
            }
        });

        // A terrain with no wall face has no seam: the common case, settled without any adjacency.
        bool anyWall = false;
        for (int t = 0; t < faceCount && !anyWall; t++)
            anyWall = isWall[t];
        if (!anyWall)
            return null;

        // Vertex -> faces.
        var start = new int[vertexCount + 1];
        for (int i = 0; i < faceCount * 3; i++)
            start[faces[i] + 1]++;
        for (int i = 0; i < vertexCount; i++)
            start[i + 1] += start[i];
        var fill = (int[])start.Clone();
        var incident = new int[faceCount * 3];
        for (int t = 0; t < faceCount; t++)
        {
            for (int k = 0; k < 3; k++)
                incident[fill[faces[t * 3 + k]]++] = t;
        }

        // Seam edges: only an edge with a wall face on at least one side can be one.
        double mitreLimit = Math.Cos(mitreCreaseDegrees * Math.PI / 180.0);
        HashSet<long> seams = IndexedMeshTools.CreateEdgeKeySet();
        for (int t = 0; t < faceCount; t++)
        {
            if (!isWall[t])
                continue;
            for (int k = 0; k < 3; k++)
            {
                int a = faces[t * 3 + k], b = faces[t * 3 + ((k + 1) % 3)];
                int other = OtherFace(t, a, b, faces, start, incident, out bool manifold);
                if (other < 0 || !manifold)
                    continue;
                if (!isWall[other] || UnitDot(areaNormals, t, other) < mitreLimit)
                    seams.Add(IndexedMeshTools.GetEdgeKey(a, b));
            }
        }

        if (seams.Count == 0)
            return null;

        // Each seam vertex's faces fall into groups joined across non-seam edges; the first group keeps the
        // vertex, every other group gets a copy.
        var seamVertices = new SortedSet<int>();
        foreach (long key in seams)
        {
            seamVertices.Add((int)(key >> 32));
            seamVertices.Add((int)(key & 0xFFFFFFFFL));
        }

        var outFaces = (int[])faces.Clone();
        var copies = new List<int>();
        var normals = new List<float>(vertexNormals.Length + (seamVertices.Count * 6));
        normals.AddRange(vertexNormals.AsSpan(0, vertexCount * 3).ToArray());
        var groupOf = new Dictionary<int, int>();
        foreach (int v in seamVertices)
        {
            groupOf.Clear();
            int groupCount = 0;
            for (int s = start[v]; s < start[v + 1]; s++)
            {
                int seed = incident[s];
                if (groupOf.ContainsKey(seed))
                    continue;

                // Flood through faces around v that share a non-seam edge with v.
                var stack = new Stack<int>();
                stack.Push(seed);
                groupOf[seed] = groupCount;
                while (stack.Count > 0)
                {
                    int t = stack.Pop();
                    for (int k = 0; k < 3; k++)
                    {
                        int a = faces[t * 3 + k], b = faces[t * 3 + ((k + 1) % 3)];
                        if (a != v && b != v)
                            continue;
                        if (seams.Contains(IndexedMeshTools.GetEdgeKey(a, b)))
                            continue;
                        int other = OtherFace(t, a, b, faces, start, incident, out bool manifold);
                        if (other < 0 || !manifold || groupOf.ContainsKey(other))
                            continue;
                        groupOf[other] = groupCount;
                        stack.Push(other);
                    }
                }

                groupCount++;
            }

            // Normals per group; group 0 keeps index v.
            var sums = new double[groupCount * 3];
            foreach ((int t, int g) in groupOf)
            {
                double nx = areaNormals[t * 3], ny = areaNormals[t * 3 + 1], nz = areaNormals[t * 3 + 2];
                double length = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
                if (length <= 0)
                    continue;
                sums[g * 3] += nx / length;
                sums[g * 3 + 1] += ny / length;
                sums[g * 3 + 2] += nz / length;
            }

            var indexOfGroup = new int[groupCount];
            indexOfGroup[0] = v;
            for (int g = 1; g < groupCount; g++)
            {
                indexOfGroup[g] = vertexCount + copies.Count;
                copies.Add(v);
                normals.AddRange(new float[] { 0, 0, 0 });
            }

            for (int g = 0; g < groupCount; g++)
            {
                double x = sums[g * 3], y = sums[g * 3 + 1], z = sums[g * 3 + 2];
                double length = Math.Sqrt((x * x) + (y * y) + (z * z));
                if (length <= 0)
                    continue;
                int at = indexOfGroup[g] * 3;
                normals[at] = (float)(x / length);
                normals[at + 1] = (float)(y / length);
                normals[at + 2] = (float)(z / length);
            }

            foreach ((int t, int g) in groupOf)
            {
                if (g == 0)
                    continue;
                for (int k = 0; k < 3; k++)
                {
                    if (faces[t * 3 + k] == v)
                        outFaces[t * 3 + k] = indexOfGroup[g];
                }
            }
        }

        return new Result
        {
            SourceVertexOfCopy = copies.ToArray(),
            Faces = outFaces,
            Normals = normals.ToArray(),
            SeamEdgeCount = seams.Count
        };
    }

    /// <summary>The face other than <paramref name="face"/> on edge (a, b); -1 on a border.</summary>
    private static int OtherFace(int face, int a, int b, int[] faces, int[] start, int[] incident, out bool manifold)
    {
        int found = -1;
        manifold = true;
        for (int s = start[a]; s < start[a + 1]; s++)
        {
            int g = incident[s];
            if (g == face)
                continue;
            if (faces[g * 3] != b && faces[g * 3 + 1] != b && faces[g * 3 + 2] != b)
                continue;
            if (found >= 0)
            {
                manifold = false;
                return found;
            }

            found = g;
        }

        return found;
    }

    private static double UnitDot(double[] n, int a, int b)
    {
        double la = Math.Sqrt((n[a * 3] * n[a * 3]) + (n[a * 3 + 1] * n[a * 3 + 1]) + (n[a * 3 + 2] * n[a * 3 + 2]));
        double lb = Math.Sqrt((n[b * 3] * n[b * 3]) + (n[b * 3 + 1] * n[b * 3 + 1]) + (n[b * 3 + 2] * n[b * 3 + 2]));
        if (la <= 0 || lb <= 0)
            return 1.0;
        return ((n[a * 3] * n[b * 3]) + (n[a * 3 + 1] * n[b * 3 + 1]) + (n[a * 3 + 2] * n[b * 3 + 2])) / (la * lb);
    }
}
