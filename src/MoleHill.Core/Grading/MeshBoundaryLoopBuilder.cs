using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

// Ordered boundary-loop extraction backed by MeshTopologyValidator's flat topology analysis.
internal static class MeshBoundaryLoopBuilder
{
    public static bool TryBuildBoundaryLoop(
        double[] vertices,
        int[] faces,
        int faceCount,
        out double[] boundaryXy,
        out int boundaryVertexCount)
    {
        MeshTopologyValidator.FlatBoundaryTopology topology =
            MeshTopologyValidator.AnalyzeBoundaryTopology(faces, faceCount);
        return TryBuildBoundaryLoop(vertices, topology, out boundaryXy, out boundaryVertexCount);
    }

    internal static bool TryBuildBoundaryLoop(
        double[] vertices,
        MeshTopologyValidator.FlatBoundaryTopology topology,
        out double[] boundaryXy,
        out int boundaryVertexCount)
    {
        boundaryXy = Array.Empty<double>();
        boundaryVertexCount = 0;
        if (!topology.TryGetSingleBoundaryLoop(out int[] order))
            return false;

        boundaryVertexCount = order.Length;
        boundaryXy = new double[boundaryVertexCount * 2];
        for (int index = 0; index < boundaryVertexCount; index++)
        {
            int vertex = order[index];
            boundaryXy[index * 2] = vertices[vertex * 3];
            boundaryXy[index * 2 + 1] = vertices[vertex * 3 + 1];
        }

        return true;
    }

    public static bool TryBuildBoundaryLoop(
        double[] vertices,
        int[] faces,
        int faceCount,
        double tolerance,
        out double[] boundaryXy,
        out double[] boundaryZ)
    {
        boundaryXy = Array.Empty<double>();
        boundaryZ = Array.Empty<double>();

        MeshTopologyValidator.FlatBoundaryTopology topology =
            MeshTopologyValidator.AnalyzeBoundaryTopology(faces, faceCount);
        if (!topology.TryGetSingleBoundaryLoop(out int[] order))
            return false;

        boundaryXy = new double[order.Length * 2];
        boundaryZ = new double[order.Length];
        for (int index = 0; index < order.Length; index++)
        {
            int vertex = order[index];
            boundaryXy[index * 2] = vertices[vertex * 3];
            boundaryXy[index * 2 + 1] = vertices[vertex * 3 + 1];
            boundaryZ[index] = vertices[vertex * 3 + 2];
        }

        return boundaryXy.Length >= 6 &&
               Math.Abs(ClipperGeometry.SignedArea(boundaryXy)) > tolerance * tolerance;
    }

    /// <summary>
    /// Extracts every closed boundary loop of a face set as ordered original vertex indices. Unlike
    /// the single-loop overloads, this walks ALL naked edges and returns each separate component, so
    /// a face set with several disjoint holes (or an outer boundary plus holes) yields one loop each.
    /// Returns false if any naked edge has a non-manifold (degree != 2) junction.
    /// </summary>
    public static bool TryBuildBoundaryLoopsIndexed(int[] faces, int faceCount, out List<int[]> loops)
    {
        MeshTopologyValidator.FlatBoundaryTopology topology =
            MeshTopologyValidator.AnalyzeBoundaryTopology(faces, faceCount);
        return topology.TryGetBoundaryLoops(out loops);
    }
}
