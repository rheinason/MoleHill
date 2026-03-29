using Rhino.Geometry;

namespace MoleHill.Shared;

internal static class RetainingWallBrepBuilder
{
    private const double LoftKinkAngleDegrees = 20.0;
    private readonly record struct VertexKey(long X, long Y, long Z);

    private readonly record struct SectionProfile(
        Point3d ToeLow,
        Point3d ToeHigh,
        Point3d TopHigh,
        Point3d TopLow,
        double Height);

    public static Brep? Build(Point3d[] toePts, Point3d[] topPts, double tolerance)
    {
        if (toePts.Length != topPts.Length || toePts.Length < 2)
            return null;

        double minHeight = Math.Max(tolerance * 0.01, 1e-6);
        double vertexTol = Math.Max(tolerance * 1e-6, 1e-9);
        var mesh = new Mesh();
        var vertices = new Dictionary<VertexKey, int>();
        for (int i = 0; i < toePts.Length - 1; i++)
        {
            SectionProfile current = CreateSection(toePts[i], topPts[i]);
            SectionProfile next = CreateSection(toePts[i + 1], topPts[i + 1]);
            if (current.Height < minHeight && next.Height < minHeight)
                continue;

            AddFace(mesh, vertices, current.ToeLow, next.ToeLow, next.ToeHigh, current.ToeHigh, vertexTol);
            AddFace(mesh, vertices, current.ToeHigh, next.ToeHigh, next.TopHigh, current.TopHigh, vertexTol);
            AddFace(mesh, vertices, current.TopHigh, next.TopHigh, next.TopLow, current.TopLow, vertexTol);
            AddFace(mesh, vertices, current.TopLow, next.TopLow, next.ToeLow, current.ToeLow, vertexTol);
        }

        if (mesh.Faces.Count == 0)
            return null;

        SectionProfile startSection = CreateSection(toePts[0], topPts[0]);
        if (startSection.Height >= minHeight)
            AddFace(mesh, vertices, startSection.ToeLow, startSection.TopLow, startSection.TopHigh, startSection.ToeHigh, vertexTol);

        SectionProfile endSection = CreateSection(toePts[^1], topPts[^1]);
        if (endSection.Height >= minHeight)
            AddFace(mesh, vertices, endSection.ToeLow, endSection.ToeHigh, endSection.TopHigh, endSection.TopLow, vertexTol);

        mesh.Vertices.CombineIdentical(true, true);
        mesh.Vertices.CullUnused();
        mesh.Faces.CullDegenerateFaces();
        mesh.UnifyNormals();
        mesh.Normals.ComputeNormals();
        mesh.Compact();

        return Brep.CreateFromMesh(mesh, true);
    }

    private static SectionProfile CreateSection(Point3d toe, Point3d top)
    {
        double low = Math.Min(toe.Z, top.Z);
        double high = Math.Max(toe.Z, top.Z);
        return new SectionProfile(
            new Point3d(toe.X, toe.Y, low),
            new Point3d(toe.X, toe.Y, high),
            new Point3d(top.X, top.Y, high),
            new Point3d(top.X, top.Y, low),
            high - low);
    }

    private static void AddFace(
        Mesh mesh,
        Dictionary<VertexKey, int> vertices,
        Point3d a,
        Point3d b,
        Point3d c,
        Point3d d,
        double vertexTol)
    {
        var loop = new List<Point3d>(4);
        foreach (Point3d point in new[] { a, b, c, d })
        {
            if (loop.Count == 0 || !SamePoint(loop[^1], point, vertexTol))
                loop.Add(point);
        }

        if (loop.Count > 1 && SamePoint(loop[0], loop[^1], vertexTol))
            loop.RemoveAt(loop.Count - 1);

        if (loop.Count < 3)
            return;

        var indices = new List<int>(loop.Count);
        foreach (Point3d point in loop)
        {
            int index = GetOrAddVertex(mesh, vertices, point, vertexTol);
            if (indices.Count == 0 || indices[^1] != index)
                indices.Add(index);
        }

        if (indices.Count > 1 && indices[0] == indices[^1])
            indices.RemoveAt(indices.Count - 1);

        if (indices.Count == 3)
        {
            AddTriangle(mesh, indices[0], indices[1], indices[2]);
            return;
        }

        if (indices.Count == 4)
        {
            AddTriangle(mesh, indices[0], indices[1], indices[2]);
            AddTriangle(mesh, indices[0], indices[2], indices[3]);
        }
    }

    private static int GetOrAddVertex(Mesh mesh, Dictionary<VertexKey, int> vertices, Point3d point, double vertexTol)
    {
        VertexKey key = ToKey(point, vertexTol);
        if (vertices.TryGetValue(key, out int index))
            return index;

        index = mesh.Vertices.Add((float)point.X, (float)point.Y, (float)point.Z);
        vertices.Add(key, index);
        return index;
    }

    private static VertexKey ToKey(Point3d point, double snap) =>
        new(
            (long)Math.Round(point.X / snap),
            (long)Math.Round(point.Y / snap),
            (long)Math.Round(point.Z / snap));

    private static void AddTriangle(Mesh mesh, int a, int b, int c)
    {
        if (a == b || b == c || c == a)
            return;

        mesh.Faces.AddFace(a, b, c);
    }

    private static bool SamePoint(Point3d a, Point3d b, double tolerance) =>
        a.DistanceToSquared(b) <= tolerance * tolerance;
}
