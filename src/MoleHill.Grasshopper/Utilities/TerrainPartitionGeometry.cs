// Rhino mesh/curve conversions for Core's topology-preserving terrain partitioner.
using MoleHill.Core.Grading;
using Rhino.Geometry;

namespace MoleHill.Grasshopper.Utilities;

internal static class TerrainPartitionGeometry
{
    public static bool TryExtractTriangleMesh(
        Mesh source,
        out double[] vertices,
        out int[] faces,
        out string? warning)
    {
        warning = null;
        vertices = Array.Empty<double>();
        faces = Array.Empty<int>();

        if (source == null || source.Vertices.Count < 3 || source.Faces.Count == 0)
        {
            warning = "Terrain mesh is empty.";
            return false;
        }

        Mesh mesh = source.DuplicateMesh();
        if (mesh.Faces.QuadCount > 0)
        {
            mesh.Faces.ConvertQuadsToTriangles();
            warning = "Terrain quads were converted to triangles before partitioning.";
        }

        vertices = new double[mesh.Vertices.Count * 3];
        for (int i = 0; i < mesh.Vertices.Count; i++)
        {
            Point3f point = mesh.Vertices[i];
            vertices[i * 3] = point.X;
            vertices[i * 3 + 1] = point.Y;
            vertices[i * 3 + 2] = point.Z;
        }

        faces = new int[mesh.Faces.Count * 3];
        for (int i = 0; i < mesh.Faces.Count; i++)
        {
            MeshFace face = mesh.Faces[i];
            if (!face.IsTriangle)
            {
                warning = "Terrain contains a non-triangular face that could not be converted.";
                vertices = Array.Empty<double>();
                faces = Array.Empty<int>();
                return false;
            }

            faces[i * 3] = face.A;
            faces[i * 3 + 1] = face.B;
            faces[i * 3 + 2] = face.C;
        }

        return true;
    }

    public static bool TryCreateBoundary(
        Curve curve,
        double tolerance,
        out MeshAreaSplitter.AreaBoundary boundary,
        out Curve displayCurve)
    {
        boundary = null!;
        displayCurve = null!;
        if (curve == null || !curve.IsClosed)
            return false;

        Polyline polyline;
        if (!curve.TryGetPolyline(out polyline))
        {
            PolylineCurve? approximation = curve.ToPolyline(
                tolerance,
                Math.PI / 90.0,
                tolerance,
                0.0);
            if (approximation == null || !approximation.TryGetPolyline(out polyline))
                return false;
        }

        int count = polyline.Count;
        if (count > 1 && polyline[0].DistanceTo(polyline[count - 1]) <= tolerance)
            count--;
        if (count < 3)
            return false;

        var xyVertices = new double[count * 2];
        var displayPoints = new Point3d[count + 1];
        for (int i = 0; i < count; i++)
        {
            xyVertices[i * 2] = polyline[i].X;
            xyVertices[i * 2 + 1] = polyline[i].Y;
            displayPoints[i] = polyline[i];
        }

        displayPoints[count] = displayPoints[0];
        boundary = new MeshAreaSplitter.AreaBoundary(xyVertices, count);
        displayCurve = new PolylineCurve(displayPoints);
        return true;
    }

    public static Mesh BuildSubMesh(MeshAreaSplitter.SplitResult result, IReadOnlySet<int> areaIndexes)
    {
        var faceIndexes = new List<int>();
        for (int faceIndex = 0; faceIndex < result.FaceCount; faceIndex++)
        {
            if (areaIndexes.Contains(result.FaceAreaIndex[faceIndex]))
                faceIndexes.Add(faceIndex);
        }

        return BuildSubMesh(result, faceIndexes);
    }

    public static Mesh BuildRemainderMesh(MeshAreaSplitter.SplitResult result)
    {
        var faceIndexes = new List<int>();
        for (int faceIndex = 0; faceIndex < result.FaceCount; faceIndex++)
        {
            if (result.FaceAreaIndex[faceIndex] < 0)
                faceIndexes.Add(faceIndex);
        }

        return BuildSubMesh(result, faceIndexes);
    }

    private static Mesh BuildSubMesh(MeshAreaSplitter.SplitResult result, IReadOnlyList<int> faceIndexes)
    {
        var usedVertices = new SortedSet<int>();
        foreach (int faceIndex in faceIndexes)
        {
            usedVertices.Add(result.Faces[faceIndex * 3]);
            usedVertices.Add(result.Faces[faceIndex * 3 + 1]);
            usedVertices.Add(result.Faces[faceIndex * 3 + 2]);
        }

        var remap = new Dictionary<int, int>(usedVertices.Count);
        var mesh = new Mesh();
        mesh.Vertices.Capacity = usedVertices.Count;
        mesh.Faces.Capacity = faceIndexes.Count;
        foreach (int vertexIndex in usedVertices)
        {
            remap[vertexIndex] = mesh.Vertices.Count;
            mesh.Vertices.Add(
                result.Vertices[vertexIndex * 3],
                result.Vertices[vertexIndex * 3 + 1],
                result.Vertices[vertexIndex * 3 + 2]);
        }

        foreach (int faceIndex in faceIndexes)
        {
            mesh.Faces.AddFace(
                remap[result.Faces[faceIndex * 3]],
                remap[result.Faces[faceIndex * 3 + 1]],
                remap[result.Faces[faceIndex * 3 + 2]]);
        }

        if (mesh.Faces.Count > 0)
        {
            mesh.Normals.ComputeNormals();
            mesh.UnifyNormals();
            mesh.Compact();
        }

        return mesh;
    }
}
