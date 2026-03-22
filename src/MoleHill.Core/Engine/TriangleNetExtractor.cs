using TriangleNet.Geometry;
using TriangleNet.Meshing;

namespace MoleHill.Core.Engine;

/// <summary>
/// Converts Triangle.NET mesh output into stable indexed arrays without trusting Vertex.ID.
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

        internal Result(double[] xy, int[] sourceIds, int vertexCount, int[] faces, int faceCount)
        {
            Xy = xy;
            SourceIds = sourceIds;
            VertexCount = vertexCount;
            Faces = faces;
            FaceCount = faceCount;
        }
    }

    public static Result Extract(IMesh mesh)
    {
        var meshTriangles = mesh.Triangles.ToList();
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

        int faceCount = meshTriangles.Count;
        var normalizedFaces = new (int A, int B, int C)[faceCount];
        for (int i = 0; i < faceCount; i++)
        {
            var tri = meshTriangles[i];
            int a = vertRefToIdx[tri.GetVertex(0)];
            int b = vertRefToIdx[tri.GetVertex(1)];
            int c = vertRefToIdx[tri.GetVertex(2)];
            RotateFaceToMinimumFirst(ref a, ref b, ref c);
            normalizedFaces[i] = (a, b, c);
        }

        Array.Sort(normalizedFaces, CompareFaces);

        var faces = new int[faceCount * 3];
        for (int i = 0; i < faceCount; i++)
        {
            faces[i * 3] = normalizedFaces[i].A;
            faces[i * 3 + 1] = normalizedFaces[i].B;
            faces[i * 3 + 2] = normalizedFaces[i].C;
        }

        return new Result(xy, sourceIds, vertexCount, faces, faceCount);
    }

    private static void RotateFaceToMinimumFirst(ref int a, ref int b, ref int c)
    {
        if (b < a && b < c)
        {
            (a, b, c) = (b, c, a);
        }
        else if (c < a && c < b)
        {
            (a, b, c) = (c, a, b);
        }
    }

    private static int CompareFaces((int A, int B, int C) left, (int A, int B, int C) right)
    {
        int compare = left.A.CompareTo(right.A);
        if (compare != 0)
            return compare;

        compare = left.B.CompareTo(right.B);
        if (compare != 0)
            return compare;

        return left.C.CompareTo(right.C);
    }
}
