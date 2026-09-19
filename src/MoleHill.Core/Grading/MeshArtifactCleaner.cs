using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

internal static class MeshArtifactCleaner
{
    internal sealed record Options(
        int MinComponentFaceCount = 12,
        double MinComponentAreaRatio = 0.002,
        double MinFaceAngleDegrees = 1.0,
        double MaxAspectRatio = 100.0,
        bool KeepLargestComponentOnly = false);

    internal sealed class Metrics
    {
        public int ComponentCount { get; init; }
        public int BoundaryEdgeCount { get; init; }
        public int SliverFaceCount { get; init; }
        public int LargestComponentFaceCount { get; init; }
        public double LargestComponentArea { get; init; }
    }

    internal sealed class CleanupResult
    {
        public required double[] Vertices { get; init; }
        public required int VertexCount { get; init; }
        public required int[] Faces { get; init; }
        public required int FaceCount { get; init; }
        public required Metrics Before { get; init; }
        public required Metrics After { get; init; }
        public int RemovedComponentCount { get; init; }
        public int RemovedFaceCount { get; init; }
    }

    private sealed class ComponentInfo
    {
        public required List<int> FaceIndices { get; init; }
        public int FaceCount => FaceIndices.Count;
        public double Area { get; set; }
    }

    private sealed class MeshAnalysis
    {
        public required List<ComponentInfo> Components { get; init; }
        public required Metrics Metrics { get; init; }
    }

    internal static CleanupResult Clean(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        Options? options = null)
    {
        options ??= new Options();
        MeshAnalysis beforeAnalysis = AnalyzeMesh(vertices, vertexCount, faces, faceCount, options);

        if (beforeAnalysis.Components.Count <= 1)
        {
            return new CleanupResult
            {
                Vertices = (double[])vertices.Clone(),
                VertexCount = vertexCount,
                Faces = (int[])faces.Clone(),
                FaceCount = faceCount,
                Before = beforeAnalysis.Metrics,
                After = beforeAnalysis.Metrics,
                RemovedComponentCount = 0,
                RemovedFaceCount = 0
            };
        }

        int largestComponentIndex = 0;
        int largestFaceCount = 0;
        double largestArea = 0.0;
        for (int i = 0; i < beforeAnalysis.Components.Count; i++)
        {
            ComponentInfo component = beforeAnalysis.Components[i];
            if (component.FaceCount > largestFaceCount ||
                (component.FaceCount == largestFaceCount && component.Area > largestArea))
            {
                largestComponentIndex = i;
                largestFaceCount = component.FaceCount;
                largestArea = component.Area;
            }
        }

        bool[] keepComponent = new bool[beforeAnalysis.Components.Count];
        int keptFaceCount = 0;
        for (int i = 0; i < beforeAnalysis.Components.Count; i++)
        {
            ComponentInfo component = beforeAnalysis.Components[i];
            bool keep = i == largestComponentIndex;
            if (!keep && !options.KeepLargestComponentOnly)
            {
                keep =
                    component.FaceCount >= options.MinComponentFaceCount &&
                    component.Area >= largestArea * options.MinComponentAreaRatio;
            }

            keepComponent[i] = keep;
            if (keep)
                keptFaceCount += component.FaceCount;
        }

        if (keptFaceCount == faceCount)
        {
            return new CleanupResult
            {
                Vertices = (double[])vertices.Clone(),
                VertexCount = vertexCount,
                Faces = (int[])faces.Clone(),
                FaceCount = faceCount,
                Before = beforeAnalysis.Metrics,
                After = beforeAnalysis.Metrics,
                RemovedComponentCount = 0,
                RemovedFaceCount = 0
            };
        }

        var usedVertex = new bool[vertexCount];
        var keptFaces = new List<int>(keptFaceCount * 3);
        int removedComponentCount = 0;
        for (int i = 0; i < beforeAnalysis.Components.Count; i++)
        {
            ComponentInfo component = beforeAnalysis.Components[i];
            if (!keepComponent[i])
            {
                removedComponentCount++;
                continue;
            }

            foreach (int faceIndex in component.FaceIndices)
            {
                int a = faces[faceIndex * 3];
                int b = faces[faceIndex * 3 + 1];
                int c = faces[faceIndex * 3 + 2];
                keptFaces.Add(a);
                keptFaces.Add(b);
                keptFaces.Add(c);
                usedVertex[a] = true;
                usedVertex[b] = true;
                usedVertex[c] = true;
            }
        }

        var remap = new int[vertexCount];
        Array.Fill(remap, -1);
        int newVertexCount = 0;
        for (int i = 0; i < vertexCount; i++)
        {
            if (!usedVertex[i])
                continue;

            remap[i] = newVertexCount++;
        }

        var cleanedVertices = new double[newVertexCount * 3];
        for (int i = 0; i < vertexCount; i++)
        {
            int target = remap[i];
            if (target < 0)
                continue;

            cleanedVertices[target * 3] = vertices[i * 3];
            cleanedVertices[target * 3 + 1] = vertices[i * 3 + 1];
            cleanedVertices[target * 3 + 2] = vertices[i * 3 + 2];
        }

        int[] cleanedFaces = keptFaces.ToArray();
        for (int i = 0; i < cleanedFaces.Length; i++)
            cleanedFaces[i] = remap[cleanedFaces[i]];

        int newFaceCount = cleanedFaces.Length / 3;
        MeshAnalysis afterAnalysis = AnalyzeMesh(cleanedVertices, newVertexCount, cleanedFaces, newFaceCount, options);

        return new CleanupResult
        {
            Vertices = cleanedVertices,
            VertexCount = newVertexCount,
            Faces = cleanedFaces,
            FaceCount = newFaceCount,
            Before = beforeAnalysis.Metrics,
            After = afterAnalysis.Metrics,
            RemovedComponentCount = removedComponentCount,
            RemovedFaceCount = faceCount - newFaceCount
        };
    }

    internal static Metrics Analyze(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        Options? options = null)
        => AnalyzeMesh(vertices, vertexCount, faces, faceCount, options ?? new Options()).Metrics;

    private static MeshAnalysis AnalyzeMesh(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        Options options)
    {
        if (vertexCount <= 0 || faceCount <= 0)
        {
            return new MeshAnalysis
            {
                Components = new List<ComponentInfo>(),
                Metrics = new Metrics()
            };
        }

        var edgeToFaces = new Dictionary<ulong, List<int>>(faceCount * 2, IndexedMeshTools.PackedKeyComparer.Instance);
        var faceNeighbors = new List<int>[faceCount];
        for (int i = 0; i < faceCount; i++)
            faceNeighbors[i] = new List<int>(3);

        int boundaryEdgeCount = 0;
        int sliverFaceCount = 0;
        double largestComponentArea = 0.0;
        int largestComponentFaceCount = 0;

        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            int a = faces[faceIndex * 3];
            int b = faces[faceIndex * 3 + 1];
            int c = faces[faceIndex * 3 + 2];

            AddEdge(edgeToFaces, a, b, faceIndex);
            AddEdge(edgeToFaces, b, c, faceIndex);
            AddEdge(edgeToFaces, c, a, faceIndex);

            if (IsSliverFace(vertices, a, b, c, options))
                sliverFaceCount++;
        }

        foreach ((ulong _, List<int> edgeFaces) in edgeToFaces)
        {
            if (edgeFaces.Count == 1)
            {
                boundaryEdgeCount++;
                continue;
            }

            for (int i = 0; i < edgeFaces.Count; i++)
            {
                for (int j = i + 1; j < edgeFaces.Count; j++)
                {
                    AddNeighbor(faceNeighbors[edgeFaces[i]], edgeFaces[j]);
                    AddNeighbor(faceNeighbors[edgeFaces[j]], edgeFaces[i]);
                }
            }
        }

        var visited = new bool[faceCount];
        var components = new List<ComponentInfo>();
        var queue = new Queue<int>();

        for (int start = 0; start < faceCount; start++)
        {
            if (visited[start])
                continue;

            visited[start] = true;
            queue.Enqueue(start);
            var componentFaces = new List<int>();
            double componentArea = 0.0;

            while (queue.Count > 0)
            {
                int faceIndex = queue.Dequeue();
                componentFaces.Add(faceIndex);

                int a = faces[faceIndex * 3];
                int b = faces[faceIndex * 3 + 1];
                int c = faces[faceIndex * 3 + 2];
                componentArea += ComputeTriangleArea(vertices, a, b, c);

                foreach (int neighbor in faceNeighbors[faceIndex])
                {
                    if (visited[neighbor])
                        continue;

                    visited[neighbor] = true;
                    queue.Enqueue(neighbor);
                }
            }

            var component = new ComponentInfo
            {
                FaceIndices = componentFaces,
                Area = componentArea
            };

            if (component.FaceCount > largestComponentFaceCount ||
                (component.FaceCount == largestComponentFaceCount && component.Area > largestComponentArea))
            {
                largestComponentFaceCount = component.FaceCount;
                largestComponentArea = component.Area;
            }

            components.Add(component);
        }

        return new MeshAnalysis
        {
            Components = components,
            Metrics = new Metrics
            {
                ComponentCount = components.Count,
                BoundaryEdgeCount = boundaryEdgeCount,
                SliverFaceCount = sliverFaceCount,
                LargestComponentFaceCount = largestComponentFaceCount,
                LargestComponentArea = largestComponentArea
            }
        };
    }

    private static void AddEdge(Dictionary<ulong, List<int>> edgeToFaces, int a, int b, int faceIndex)
    {
        ulong key = MakeEdgeKey(a, b);
        if (!edgeToFaces.TryGetValue(key, out List<int>? faceList))
        {
            faceList = new List<int>(2);
            edgeToFaces.Add(key, faceList);
        }

        faceList.Add(faceIndex);
    }

    private static void AddNeighbor(List<int> neighbors, int value)
    {
        if (!neighbors.Contains(value))
            neighbors.Add(value);
    }

    private static ulong MakeEdgeKey(int a, int b)
    {
        uint low = (uint)Math.Min(a, b);
        uint high = (uint)Math.Max(a, b);
        return ((ulong)low << 32) | high;
    }

    private static bool IsSliverFace(double[] vertices, int a, int b, int c, Options options)
    {
        double ab = Distance(vertices, a, b);
        double bc = Distance(vertices, b, c);
        double ca = Distance(vertices, c, a);
        if (ab <= 1e-12 || bc <= 1e-12 || ca <= 1e-12)
            return true;

        double longest = Math.Max(ab, Math.Max(bc, ca));
        double shortest = Math.Min(ab, Math.Min(bc, ca));
        double aspectRatio = longest / shortest;
        if (aspectRatio > options.MaxAspectRatio)
            return true;

        double angleA = ComputeAngleDegrees(ab, ca, bc);
        double angleB = ComputeAngleDegrees(ab, bc, ca);
        double angleC = ComputeAngleDegrees(bc, ca, ab);
        double minAngle = Math.Min(angleA, Math.Min(angleB, angleC));
        return minAngle < options.MinFaceAngleDegrees;
    }

    private static double ComputeTriangleArea(double[] vertices, int a, int b, int c)
    {
        double abx = vertices[b * 3] - vertices[a * 3];
        double aby = vertices[b * 3 + 1] - vertices[a * 3 + 1];
        double abz = vertices[b * 3 + 2] - vertices[a * 3 + 2];
        double acx = vertices[c * 3] - vertices[a * 3];
        double acy = vertices[c * 3 + 1] - vertices[a * 3 + 1];
        double acz = vertices[c * 3 + 2] - vertices[a * 3 + 2];

        double crossX = (aby * acz) - (abz * acy);
        double crossY = (abz * acx) - (abx * acz);
        double crossZ = (abx * acy) - (aby * acx);
        return 0.5 * Math.Sqrt((crossX * crossX) + (crossY * crossY) + (crossZ * crossZ));
    }

    private static double Distance(double[] vertices, int a, int b)
    {
        double dx = vertices[b * 3] - vertices[a * 3];
        double dy = vertices[b * 3 + 1] - vertices[a * 3 + 1];
        double dz = vertices[b * 3 + 2] - vertices[a * 3 + 2];
        return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    private static double ComputeAngleDegrees(double side1, double side2, double opposite)
    {
        double denominator = 2.0 * side1 * side2;
        if (denominator <= 1e-12)
            return 0.0;

        double cosine = ((side1 * side1) + (side2 * side2) - (opposite * opposite)) / denominator;
        cosine = Math.Clamp(cosine, -1.0, 1.0);
        return Math.Acos(cosine) * (180.0 / Math.PI);
    }
}
