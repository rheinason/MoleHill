using TriangleNet.Geometry;
using TriangleNet.Meshing;
using TriangleNet.Topology;

namespace MoleHill.Core.Engine;

/// <summary>
/// Converts Triangle.NET output into stable indexed arrays and optionally retains validated native adjacency.
/// </summary>
public static class TriangleNetExtractor
{
    public sealed class Result
    {
        public double[] Xy { get; }

        public int[] SourceIds { get; }

        public int VertexCount { get; }

        public int[] Faces { get; }

        public int FaceCount { get; }

        internal NativeAdjacency? Adjacency { get; }

        internal Result(
            double[] xy,
            int[] sourceIds,
            int vertexCount,
            int[] faces,
            int faceCount,
            NativeAdjacency? adjacency)
        {
            Xy = xy;
            SourceIds = sourceIds;
            VertexCount = vertexCount;
            Faces = faces;
            FaceCount = faceCount;
            Adjacency = adjacency;
        }
    }

    internal sealed class NativeAdjacency
    {
        private readonly Triangle[] _triangles;
        private readonly int[]? _denseFaceByTriangleId;
        private readonly Dictionary<Triangle, int>? _sparseFaceByTriangle;

        public int FaceCount => _triangles.Length;

        public int BoundaryEdgeCount { get; }

        public bool IsValid { get; }

        private NativeAdjacency(
            Triangle[] triangles,
            int[]? denseFaceByTriangleId,
            Dictionary<Triangle, int>? sparseFaceByTriangle,
            int boundaryEdgeCount,
            bool isValid)
        {
            _triangles = triangles;
            _denseFaceByTriangleId = denseFaceByTriangleId;
            _sparseFaceByTriangle = sparseFaceByTriangle;
            BoundaryEdgeCount = boundaryEdgeCount;
            IsValid = isValid;
        }

        public int GetNeighborFace(int faceIndex, int localEdge)
        {
            Triangle triangle = _triangles[faceIndex];
            ITriangle? neighbor = triangle.GetNeighbor((localEdge + 2) % 3);
            return neighbor is Triangle neighborTriangle
                ? FindFace(neighborTriangle, _triangles, _denseFaceByTriangleId, _sparseFaceByTriangle)
                : -1;
        }

        internal static NativeAdjacency Create(Triangle[] triangles)
        {
            BuildFaceMap(
                triangles,
                out int[]? denseFaceByTriangleId,
                out Dictionary<Triangle, int>? sparseFaceByTriangle);

            int boundaryEdgeCount = 0;
            bool isValid = true;
            for (int face = 0; face < triangles.Length; face++)
            {
                Triangle triangle = triangles[face];
                if (ReferenceEquals(triangle.GetVertex(0), triangle.GetVertex(1)) ||
                    ReferenceEquals(triangle.GetVertex(1), triangle.GetVertex(2)) ||
                    ReferenceEquals(triangle.GetVertex(2), triangle.GetVertex(0)))
                {
                    isValid = false;
                    break;
                }

                for (int localEdge = 0; localEdge < 3; localEdge++)
                {
                    ITriangle? neighbor = triangle.GetNeighbor((localEdge + 2) % 3);
                    if (neighbor == null)
                    {
                        boundaryEdgeCount++;
                        continue;
                    }

                    if (neighbor is not Triangle neighborTriangle)
                    {
                        isValid = false;
                        break;
                    }

                    int neighborFace = FindFace(
                        neighborTriangle,
                        triangles,
                        denseFaceByTriangleId,
                        sparseFaceByTriangle);
                    if (neighborFace < 0 ||
                        neighborFace == face ||
                        !SharesEdge(triangle, localEdge, neighborTriangle) ||
                        !HasReciprocalReference(neighborTriangle, triangle))
                    {
                        isValid = false;
                        break;
                    }
                }

                if (!isValid)
                    break;
            }

            return new NativeAdjacency(
                triangles,
                denseFaceByTriangleId,
                sparseFaceByTriangle,
                boundaryEdgeCount,
                isValid);
        }

        private static void BuildFaceMap(
            Triangle[] triangles,
            out int[]? denseFaceByTriangleId,
            out Dictionary<Triangle, int>? sparseFaceByTriangle)
        {
            int maxTriangleId = -1;
            bool hasNegativeId = false;
            for (int face = 0; face < triangles.Length; face++)
            {
                int triangleId = triangles[face].ID;
                hasNegativeId |= triangleId < 0;
                maxTriangleId = Math.Max(maxTriangleId, triangleId);
            }

            denseFaceByTriangleId = null;
            sparseFaceByTriangle = null;
            if (!hasNegativeId &&
                maxTriangleId >= 0 &&
                (long)maxTriangleId <= Math.Max((long)triangles.Length * 4, 1024))
            {
                denseFaceByTriangleId = new int[maxTriangleId + 1];
                Array.Fill(denseFaceByTriangleId, -1);
                for (int face = 0; face < triangles.Length; face++)
                {
                    int triangleId = triangles[face].ID;
                    if (denseFaceByTriangleId[triangleId] >= 0)
                    {
                        denseFaceByTriangleId = null;
                        break;
                    }

                    denseFaceByTriangleId[triangleId] = face;
                }
            }

            if (denseFaceByTriangleId != null)
                return;

            sparseFaceByTriangle = new Dictionary<Triangle, int>(triangles.Length);
            for (int face = 0; face < triangles.Length; face++)
                sparseFaceByTriangle[triangles[face]] = face;
        }

        private static int FindFace(
            Triangle triangle,
            Triangle[] triangles,
            int[]? denseFaceByTriangleId,
            Dictionary<Triangle, int>? sparseFaceByTriangle)
        {
            if (denseFaceByTriangleId != null)
            {
                int triangleId = triangle.ID;
                if ((uint)triangleId >= (uint)denseFaceByTriangleId.Length)
                    return -1;

                int face = denseFaceByTriangleId[triangleId];
                return face >= 0 && ReferenceEquals(triangles[face], triangle)
                    ? face
                    : -1;
            }

            return sparseFaceByTriangle!.GetValueOrDefault(triangle, -1);
        }

        private static bool SharesEdge(Triangle triangle, int localEdge, Triangle neighbor)
        {
            Vertex a = triangle.GetVertex(localEdge);
            Vertex b = triangle.GetVertex((localEdge + 1) % 3);
            bool hasA = false;
            bool hasB = false;
            for (int corner = 0; corner < 3; corner++)
            {
                Vertex vertex = neighbor.GetVertex(corner);
                hasA |= ReferenceEquals(vertex, a);
                hasB |= ReferenceEquals(vertex, b);
            }

            return hasA && hasB;
        }

        private static bool HasReciprocalReference(Triangle neighbor, Triangle triangle)
        {
            for (int localVertex = 0; localVertex < 3; localVertex++)
            {
                if (ReferenceEquals(neighbor.GetNeighbor(localVertex), triangle))
                    return true;
            }

            return false;
        }
    }

    public static Result Extract(IMesh mesh) => Extract(mesh, includeNativeAdjacency: false);

    internal static Result Extract(IMesh mesh, bool includeNativeAdjacency)
    {
        Triangle[] meshTriangles = mesh.Triangles.ToArray();
        var vertRefToIdx = new Dictionary<Vertex, int>(ReferenceEqualityComparer.Instance);
        var allVerts = new List<Vertex>(mesh.Vertices.Count);

        foreach (var v in mesh.Vertices)
        {
            vertRefToIdx[v] = allVerts.Count;
            allVerts.Add(v);
        }

        foreach (var tri in meshTriangles)
        {
            for (int k = 0; k < 3; k++)
            {
                var v = tri.GetVertex(k);
                if (!vertRefToIdx.ContainsKey(v))
                {
                    vertRefToIdx[v] = allVerts.Count;
                    allVerts.Add(v);
                }
            }
        }

        int vertexCount = allVerts.Count;
        var xy = new double[vertexCount * 2];
        var sourceIds = new int[vertexCount];
        for (int i = 0; i < vertexCount; i++)
        {
            var vertex = allVerts[i];
            xy[i * 2] = vertex.X;
            xy[i * 2 + 1] = vertex.Y;
            sourceIds[i] = vertex.ID;
        }

        int faceCount = meshTriangles.Length;
        var faces = new int[faceCount * 3];
        for (int i = 0; i < faceCount; i++)
        {
            var tri = meshTriangles[i];
            faces[i * 3] = vertRefToIdx[tri.GetVertex(0)];
            faces[i * 3 + 1] = vertRefToIdx[tri.GetVertex(1)];
            faces[i * 3 + 2] = vertRefToIdx[tri.GetVertex(2)];
        }

        NativeAdjacency? adjacency = includeNativeAdjacency
            ? NativeAdjacency.Create(meshTriangles)
            : null;
        return new Result(xy, sourceIds, vertexCount, faces, faceCount, adjacency);
    }
}
