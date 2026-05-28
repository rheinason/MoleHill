namespace MoleHill.Core.Grading;

internal enum CoincidentVertexZPolicy
{
    KeepFirst,
    UseLatest
}

internal static class MeshTopologyOperations
{
    public static void MergeMeshes(
        double[] firstVertices,
        int firstVertexCount,
        int[] firstFaces,
        int firstFaceCount,
        double[] secondVertices,
        int secondVertexCount,
        int[] secondFaces,
        int secondFaceCount,
        double tolerance,
        CoincidentVertexZPolicy coincidentZPolicy,
        out double[] mergedVertices,
        out int mergedVertexCount,
        out int[] mergedFaces,
        out int mergedFaceCount)
    {
        var xyList = new List<double>(firstVertexCount * 2 + secondVertexCount * 2);
        var zList = new List<double>(firstVertexCount + secondVertexCount);
        var vertHash = new SpatialVertexHash(tolerance);
        var seenFaces = new HashSet<ulong>();

        int AddVertex(double x, double y, double z)
        {
            int near = vertHash.FindNearest(xyList, x, y, tolerance);
            if (near >= 0)
            {
                if (coincidentZPolicy == CoincidentVertexZPolicy.UseLatest)
                    zList[near] = z;

                return near;
            }

            int index = zList.Count;
            xyList.Add(x);
            xyList.Add(y);
            zList.Add(z);
            vertHash.Insert(index, x, y);
            return index;
        }

        bool TryAddFace(List<int> faceList, int a, int b, int c)
        {
            if (a == b || b == c || c == a)
                return false;

            double ax = xyList[a * 2];
            double ay = xyList[a * 2 + 1];
            double bx = xyList[b * 2];
            double by = xyList[b * 2 + 1];
            double cx = xyList[c * 2];
            double cy = xyList[c * 2 + 1];
            double area2 = ((bx - ax) * (cy - ay)) - ((by - ay) * (cx - ax));
            if (Math.Abs(area2) <= tolerance * tolerance)
                return false;

            ulong key = CreateFaceKey(a, b, c);
            if (!seenFaces.Add(key))
                return false;

            faceList.Add(a);
            faceList.Add(b);
            faceList.Add(c);
            return true;
        }

        var faceList = new List<int>((firstFaceCount + secondFaceCount) * 3);
        var firstRemap = new int[firstVertexCount];
        for (int i = 0; i < firstVertexCount; i++)
            firstRemap[i] = AddVertex(firstVertices[i * 3], firstVertices[i * 3 + 1], firstVertices[i * 3 + 2]);

        for (int i = 0; i < firstFaceCount; i++)
        {
            int a = firstRemap[firstFaces[i * 3]];
            int b = firstRemap[firstFaces[i * 3 + 1]];
            int c = firstRemap[firstFaces[i * 3 + 2]];
            TryAddFace(faceList, a, b, c);
        }

        var secondRemap = new int[secondVertexCount];
        for (int i = 0; i < secondVertexCount; i++)
            secondRemap[i] = AddVertex(secondVertices[i * 3], secondVertices[i * 3 + 1], secondVertices[i * 3 + 2]);

        for (int i = 0; i < secondFaceCount; i++)
        {
            int a = secondRemap[secondFaces[i * 3]];
            int b = secondRemap[secondFaces[i * 3 + 1]];
            int c = secondRemap[secondFaces[i * 3 + 2]];
            TryAddFace(faceList, a, b, c);
        }

        mergedVertexCount = zList.Count;
        mergedVertices = new double[mergedVertexCount * 3];
        for (int i = 0; i < mergedVertexCount; i++)
        {
            mergedVertices[i * 3] = xyList[i * 2];
            mergedVertices[i * 3 + 1] = xyList[i * 2 + 1];
            mergedVertices[i * 3 + 2] = zList[i];
        }

        mergedFaces = faceList.ToArray();
        mergedFaceCount = mergedFaces.Length / 3;
    }

    private static ulong CreateFaceKey(int a, int b, int c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return ((ulong)(uint)a << 42) | ((ulong)(uint)b << 21) | (uint)c;
    }
}
