using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

internal static class TerrainMeshProjection
{
    public static bool TryProjectPointAlongWorldZ(
        RhinoMesh mesh,
        Point3d point,
        double tolerance,
        out Point3d projectedPoint)
    {
        return TryProjectPointAlongWorldZ(mesh, point, tolerance, out projectedPoint, out int _);
    }

    public static bool TryProjectPointAlongWorldZ(
        RhinoMesh mesh,
        Point3d point,
        double tolerance,
        out Point3d projectedPoint,
        out MeshPoint? meshPoint)
    {
        meshPoint = null;
        if (!TryProjectPointAlongWorldZ(mesh, point, tolerance, out projectedPoint))
            return false;

        double meshPointTolerance = Math.Max(Math.Abs(tolerance) * 4.0, double.Epsilon);
        meshPoint = mesh.ClosestMeshPoint(projectedPoint, meshPointTolerance);
        return true;
    }

    /// <summary>
    /// <see cref="TryProjectPointAlongWorldZ(RhinoMesh, Point3d, double)"/> against the few triangles that can
    /// meet the vertical line (<paramref name="candidateTriangles"/>, flat XYZ corner triples, from
    /// <c>MeshHeightProjector.CandidateTrianglesAt</c>), with the line spanning <paramref name="meshBounds"/> as
    /// before. Rhino intersects a line with every face of a mesh, so over a million-face reference each call
    /// cost tens of milliseconds, and the few hundred points over walls were most of a Cut / Fill analysis.
    /// </summary>
    public static bool TryProjectPointAlongWorldZ(
        double[] candidateTriangles,
        BoundingBox meshBounds,
        Point3d point,
        double tolerance,
        out Point3d projectedPoint)
    {
        var local = new RhinoMesh();
        int triangles = candidateTriangles.Length / 9;
        for (int t = 0; t < triangles; t++)
        {
            for (int c = 0; c < 3; c++)
                local.Vertices.Add(candidateTriangles[t * 9 + c * 3], candidateTriangles[t * 9 + c * 3 + 1], candidateTriangles[t * 9 + c * 3 + 2]);
            local.Faces.AddFace(t * 3, t * 3 + 1, t * 3 + 2);
        }

        return TryProjectPointAlongWorldZ(local, meshBounds, point, tolerance, out projectedPoint, out _);
    }

    public static bool TryProjectPointAlongWorldZ(
        RhinoMesh mesh,
        Point3d point,
        double tolerance,
        out Point3d projectedPoint,
        out int faceIndex)
    {
        return TryProjectPointAlongWorldZ(mesh, mesh.GetBoundingBox(true), point, tolerance, out projectedPoint, out faceIndex);
    }

    private static bool TryProjectPointAlongWorldZ(
        RhinoMesh mesh,
        BoundingBox bounds,
        Point3d point,
        double tolerance,
        out Point3d projectedPoint,
        out int faceIndex)
    {
        projectedPoint = Point3d.Unset;
        faceIndex = -1;

        if (!bounds.IsValid)
            return false;

        double height = Math.Max(bounds.Max.Z - bounds.Min.Z, tolerance);
        double padding = Math.Max(Math.Max(Math.Abs(tolerance), height) * 2.0, double.Epsilon);
        var line = new Line(
            new Point3d(point.X, point.Y, bounds.Min.Z - padding),
            new Point3d(point.X, point.Y, bounds.Max.Z + padding));

        var hits = Intersection.MeshLineSorted(mesh, line, out int[] faceIds);
        if (hits == null || hits.Length == 0)
            return false;

        int hitIndex = 0;
        double bestDistance = Math.Abs(hits[0].Z - point.Z);
        for (int i = 1; i < hits.Length; i++)
        {
            double distance = Math.Abs(hits[i].Z - point.Z);
            if (distance < bestDistance)
            {
                hitIndex = i;
                bestDistance = distance;
            }
        }

        var hit = hits[hitIndex];
        if (!hit.IsValid)
            return false;

        projectedPoint = new Point3d(point.X, point.Y, hit.Z);
        if (faceIds != null && hitIndex < faceIds.Length)
            faceIndex = faceIds[hitIndex];
        return true;
    }
}
