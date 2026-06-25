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
        return TryProjectPointAlongWorldZ(mesh, point, tolerance, out projectedPoint, out _);
    }

    public static bool TryProjectPointAlongWorldZ(
        RhinoMesh mesh,
        Point3d point,
        double tolerance,
        out Point3d projectedPoint,
        out MeshPoint? meshPoint)
    {
        projectedPoint = Point3d.Unset;
        meshPoint = null;

        BoundingBox bounds = mesh.GetBoundingBox(true);
        if (!bounds.IsValid)
            return false;

        double height = Math.Max(bounds.Max.Z - bounds.Min.Z, tolerance);
        double padding = Math.Max(Math.Max(tolerance, height) * 2.0, 1e-6);
        var line = new Line(
            new Point3d(point.X, point.Y, bounds.Min.Z - padding),
            new Point3d(point.X, point.Y, bounds.Max.Z + padding));

        var hits = Intersection.MeshLineSorted(mesh, line, out _);
        if (hits == null || hits.Length == 0)
            return false;

        projectedPoint = hits
            .OrderBy(hit => Math.Abs(hit.Z - point.Z))
            .First();
        if (!projectedPoint.IsValid)
            return false;

        double meshPointTolerance = Math.Max(tolerance * 4.0, 1e-6);
        meshPoint = mesh.ClosestMeshPoint(projectedPoint, meshPointTolerance);
        return true;
    }
}
