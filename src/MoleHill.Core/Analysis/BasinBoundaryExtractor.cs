using MoleHill.Core.Engine;

namespace MoleHill.Core.Analysis;

/// <summary>Turns a basin's faces into the polygon(s) that bound it.</summary>
public static class BasinBoundaryExtractor
{
    /// <summary>
    /// Boundary loops of one basin, as flat XYZ point arrays that follow the mesh exactly. A closed loop
    /// repeats its first point at the end, so a caller can hand it straight to a polyline without
    /// deciding whether it closes. An open chain — only possible where the mesh's winding is
    /// inconsistent — is returned open, with its first and last points distinct.
    /// </summary>
    /// <remarks>
    /// The loops come out of the mesh's own winding: every edge of a basin face whose neighbour is in a
    /// different basin (or is naked) is emitted in that face's direction. On a consistently wound mesh
    /// those directed edges balance at every vertex, so following them closes without any geometric
    /// decision about what is inside — which is what keeps a basin with an island in it, or one that
    /// pinches to a point, from needing a special case.
    /// </remarks>
    public static List<double[]> Extract(
        BasinGraph graph,
        IReadOnlyList<double> vertices,
        int vertexCount,
        IReadOnlyList<int> faces,
        int basinIndex)
    {
        if (graph == null)
            throw new ArgumentNullException(nameof(graph));
        if (vertices == null)
            throw new ArgumentNullException(nameof(vertices));
        if (faces == null)
            throw new ArgumentNullException(nameof(faces));

        var boundaryEdges = new List<int>();
        for (int face = 0; face < graph.FaceCount; face++)
        {
            if (graph.FaceBasin[face] == basinIndex)
                AddBoundaryEdges(graph, faces, face, basinIndex, boundaryEdges);
        }

        var localIndex = new int[Math.Max(0, vertexCount)];
        Array.Fill(localIndex, -1);
        return BuildLoops(boundaryEdges, vertices, vertexCount, localIndex, new List<int>());
    }

    /// <summary>
    /// Boundary loops of every basin in <see cref="BasinGraph.Basins"/>, indexed by basin index, with the
    /// same content and order <see cref="Extract"/> gives one basin at a time. One pass over the faces
    /// buckets them by basin, and each basin's chaining works on its own boundary vertices only, so the
    /// cost is linear in the mesh rather than basins × faces.
    /// </summary>
    public static List<double[]>[] ExtractAll(
        BasinGraph graph,
        IReadOnlyList<double> vertices,
        int vertexCount,
        IReadOnlyList<int> faces,
        Func<bool>? shouldCancel = null)
    {
        if (graph == null)
            throw new ArgumentNullException(nameof(graph));
        if (vertices == null)
            throw new ArgumentNullException(nameof(vertices));
        if (faces == null)
            throw new ArgumentNullException(nameof(faces));

        int basinCount = graph.Basins.Count;
        var result = new List<double[]>[basinCount];

        // Flat CSR of faces per basin, in face order, so each basin sees its faces in exactly the order
        // the per-basin scan would.
        var faceStart = new int[basinCount + 1];
        for (int face = 0; face < graph.FaceCount; face++)
        {
            int basin = graph.FaceBasin[face];
            if ((uint)basin < (uint)basinCount)
                faceStart[basin + 1]++;
        }

        for (int basin = 0; basin < basinCount; basin++)
            faceStart[basin + 1] += faceStart[basin];

        var cursor = (int[])faceStart.Clone();
        var basinFaces = new int[faceStart[basinCount]];
        for (int face = 0; face < graph.FaceCount; face++)
        {
            int basin = graph.FaceBasin[face];
            if ((uint)basin < (uint)basinCount)
                basinFaces[cursor[basin]++] = face;
        }

        var localIndex = new int[Math.Max(0, vertexCount)];
        Array.Fill(localIndex, -1);
        var localToGlobal = new List<int>();
        var boundaryEdges = new List<int>();
        for (int basin = 0; basin < basinCount; basin++)
        {
            if (shouldCancel?.Invoke() == true)
                throw new OperationCanceledException();

            boundaryEdges.Clear();
            for (int slot = faceStart[basin]; slot < faceStart[basin + 1]; slot++)
                AddBoundaryEdges(graph, faces, basinFaces[slot], basin, boundaryEdges);

            result[basin] = BuildLoops(boundaryEdges, vertices, vertexCount, localIndex, localToGlobal);
        }

        return result;
    }

    private static void AddBoundaryEdges(BasinGraph graph, IReadOnlyList<int> faces, int face, int basinIndex, List<int> boundaryEdges)
    {
        for (int edge = 0; edge < 3; edge++)
        {
            int neighbor = graph.Neighbors[(face * 3) + edge];
            if (neighbor >= 0 && graph.FaceBasin[neighbor] == basinIndex)
                continue;

            boundaryEdges.Add(faces[(face * 3) + edge]);
            boundaryEdges.Add(faces[(face * 3) + ((edge + 1) % 3)]);
        }
    }

    /// <summary>
    /// Chains one basin's directed boundary edges into point loops. The edges are renumbered onto the
    /// basin's own boundary vertices first (renumbering preserves both edge order and each vertex's
    /// outgoing order, so the chains are the same), which keeps the chainer's per-vertex tables sized to
    /// the boundary rather than the whole mesh. <paramref name="localIndex"/> is all -1 on entry and exit.
    /// </summary>
    private static List<double[]> BuildLoops(
        List<int> boundaryEdges,
        IReadOnlyList<double> vertices,
        int vertexCount,
        int[] localIndex,
        List<int> localToGlobal)
    {
        localToGlobal.Clear();
        var localEdges = new int[boundaryEdges.Count];
        for (int index = 0; index < boundaryEdges.Count; index++)
        {
            int vertex = boundaryEdges[index];
            if ((uint)vertex >= (uint)vertexCount)
            {
                localEdges[index] = -1;
                continue;
            }

            if (localIndex[vertex] < 0)
            {
                localIndex[vertex] = localToGlobal.Count;
                localToGlobal.Add(vertex);
            }

            localEdges[index] = localIndex[vertex];
        }

        foreach (int vertex in localToGlobal)
            localIndex[vertex] = -1;

        var closed = new List<bool>();
        List<int[]> chains = EdgeLoopChainer.ChainDirected(localEdges, localEdges.Length / 2, localToGlobal.Count, closed);
        var loops = new List<double[]>(chains.Count);
        for (int chainIndex = 0; chainIndex < chains.Count; chainIndex++)
        {
            int[] chain = chains[chainIndex];
            if (chain.Length < 3)
                continue;

            // Only a chain that really returned to its start is closed. An open chain (unbalanced edges
            // on an inconsistently wound mesh) stays open: closing it would draw a straight divide that
            // does not exist.
            bool isClosed = closed[chainIndex];
            var points = new double[(chain.Length + (isClosed ? 1 : 0)) * 3];
            for (int index = 0; index < chain.Length; index++)
            {
                int vertex = localToGlobal[chain[index]];
                points[index * 3] = vertices[vertex * 3];
                points[(index * 3) + 1] = vertices[(vertex * 3) + 1];
                points[(index * 3) + 2] = vertices[(vertex * 3) + 2];
            }

            if (isClosed)
            {
                points[chain.Length * 3] = points[0];
                points[(chain.Length * 3) + 1] = points[1];
                points[(chain.Length * 3) + 2] = points[2];
            }

            loops.Add(points);
        }

        return loops;
    }
}
