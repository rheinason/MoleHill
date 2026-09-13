using MoleHill.Core.Engine;

namespace MoleHill.Core.Analysis;

/// <summary>Turns a basin's faces into the closed polygon(s) that bound it.</summary>
public static class BasinBoundaryExtractor
{
    /// <summary>
    /// Boundary loops of one basin, as flat XYZ point arrays that follow the mesh exactly. Each loop
    /// repeats its first point at the end, so a caller can hand it straight to a polyline without
    /// deciding whether it closes.
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
            if (graph.FaceBasin[face] != basinIndex)
                continue;

            for (int edge = 0; edge < 3; edge++)
            {
                int neighbor = graph.Neighbors[(face * 3) + edge];
                if (neighbor >= 0 && graph.FaceBasin[neighbor] == basinIndex)
                    continue;

                boundaryEdges.Add(faces[(face * 3) + edge]);
                boundaryEdges.Add(faces[(face * 3) + ((edge + 1) % 3)]);
            }
        }

        List<int[]> chains = EdgeLoopChainer.ChainDirected(boundaryEdges, boundaryEdges.Count / 2, vertexCount);
        var loops = new List<double[]>(chains.Count);
        foreach (int[] chain in chains)
        {
            if (chain.Length < 3)
                continue;

            var points = new double[(chain.Length + 1) * 3];
            for (int index = 0; index < chain.Length; index++)
            {
                int vertex = chain[index];
                points[index * 3] = vertices[vertex * 3];
                points[(index * 3) + 1] = vertices[(vertex * 3) + 1];
                points[(index * 3) + 2] = vertices[(vertex * 3) + 2];
            }

            points[chain.Length * 3] = points[0];
            points[(chain.Length * 3) + 1] = points[1];
            points[(chain.Length * 3) + 2] = points[2];
            loops.Add(points);
        }

        return loops;
    }
}
