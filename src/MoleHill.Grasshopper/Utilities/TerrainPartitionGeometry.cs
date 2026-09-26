using System.Runtime.InteropServices;
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

        return BuildSubMesh(result, CollectionsMarshal.AsSpan(faceIndexes));
    }

    public static Mesh BuildRemainderMesh(MeshAreaSplitter.SplitResult result)
    {
        var faceIndexes = new List<int>();
        for (int faceIndex = 0; faceIndex < result.FaceCount; faceIndex++)
        {
            if (result.FaceAreaIndex[faceIndex] < 0)
                faceIndexes.Add(faceIndex);
        }

        return BuildSubMesh(result, CollectionsMarshal.AsSpan(faceIndexes));
    }

    public static int[] ClassifyFaceOwners(
        MeshAreaSplitter.SplitResult result,
        IReadOnlyList<IReadOnlyList<MeshAreaSplitter.AreaBoundary>> regions)
    {
        var owners = new int[result.FaceCount];
        Array.Fill(owners, -1);
        for (int faceIndex = 0; faceIndex < result.FaceCount; faceIndex++)
        {
            int a = result.Faces[faceIndex * 3];
            int b = result.Faces[faceIndex * 3 + 1];
            int c = result.Faces[faceIndex * 3 + 2];
            double x = (result.Vertices[a * 3] + result.Vertices[b * 3] + result.Vertices[c * 3]) / 3.0;
            double y = (result.Vertices[a * 3 + 1] + result.Vertices[b * 3 + 1] + result.Vertices[c * 3 + 1]) / 3.0;

            for (int regionIndex = 0; regionIndex < regions.Count; regionIndex++)
            {
                int containmentCount = 0;
                foreach (MeshAreaSplitter.AreaBoundary boundary in regions[regionIndex])
                {
                    if (PointInPolygon(x, y, boundary.XyVertices, boundary.VertexCount))
                        containmentCount++;
                }

                // Odd/even containment allows a branch to describe outer loops and holes.
                // Later branches retain the existing overlap priority.
                if ((containmentCount & 1) == 1)
                    owners[faceIndex] = regionIndex;
            }
        }

        return owners;
    }

    /// <summary>
    /// Builds one owner's mesh from a grouping the caller made once, so extracting every region costs
    /// one pass over the split result rather than one per region.
    /// </summary>
    public static Mesh BuildOwnedMesh(
        MeshAreaSplitter.SplitResult result,
        FaceOwnerGroups groups,
        int ownerIndex)
    {
        ArgumentNullException.ThrowIfNull(groups);
        return BuildSubMesh(result, groups.Faces(ownerIndex));
    }

    public static IReadOnlyList<Curve> ClipBreaklinesToMesh(
        IEnumerable<Curve> breaklines,
        Mesh mesh,
        double tolerance)
    {
        if (mesh.Faces.Count == 0)
            return Array.Empty<Curve>();

        BoundingBox bounds = mesh.GetBoundingBox(true);
        double lift = Math.Max(bounds.Diagonal.Length, tolerance * 10.0) + tolerance;
        var clipped = new List<Curve>();
        foreach (Curve source in breaklines)
        {
            Curve raised = source.DuplicateCurve();
            raised.Translate(0.0, 0.0, bounds.Max.Z + lift - raised.GetBoundingBox(true).Min.Z);
            Curve[] projected = Curve.ProjectToMesh(raised, mesh, -Vector3d.ZAxis, tolerance);
            raised.Dispose();
            foreach (Curve curve in projected)
            {
                if (curve.GetLength() > tolerance)
                    clipped.Add(curve);
                else
                    curve.Dispose();
            }
        }

        return clipped;
    }

    private static bool PointInPolygon(double x, double y, double[] polygon, int vertexCount)
    {
        bool inside = false;
        for (int i = 0, j = vertexCount - 1; i < vertexCount; j = i++)
        {
            double xi = polygon[i * 2];
            double yi = polygon[i * 2 + 1];
            double xj = polygon[j * 2];
            double yj = polygon[j * 2 + 1];
            bool crosses = (yi > y) != (yj > y) &&
                x < ((xj - xi) * (y - yi) / (yj - yi)) + xi;
            if (crosses)
                inside = !inside;
        }

        return inside;
    }

    private static Mesh BuildSubMesh(MeshAreaSplitter.SplitResult result, ReadOnlySpan<int> faceIndexes)
    {
        // Vertices are emitted in ascending source order here (not first-touch order): this partition
        // output's vertex ordering is part of its contract.
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
        mesh.Faces.Capacity = faceIndexes.Length;
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
