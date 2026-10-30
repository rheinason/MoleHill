using MoleHill.Core.Scattering;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

internal static class ObjectPlacementGeometry
{
    internal static ulong ReferenceFingerprint(GeometryBase geometry)
    {
        var hash = new FingerprintBuilder();
        var reference = ObjectPlacementReference.Find(geometry);
        hash.Add(reference != null);
        if (reference == null) return hash.ToUInt64();
        hash.Add(reference.IsCustomBase);
        foreach (var point in reference.BoxCorners.Concat(reference.BaseCorners))
        {
            hash.Add(point.X); hash.Add(point.Y); hash.Add(point.Z);
        }
        hash.Add(reference.Up.X); hash.Add(reference.Up.Y); hash.Add(reference.Up.Z);
        for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++) hash.Add(reference.LastPlacement[row, column]);
        return hash.ToUInt64();
    }

    internal static bool TryGetReference(ResolvedSourceObject obj, GeometryBase baseline, Transform inversePrevious,
        out ObjectPlacementReference reference)
    {
        var saved = obj.PlacementReference ?? ObjectPlacementReference.Find(baseline);
        if (saved != null)
        {
            reference = saved.CopyReference();
            if (obj.PlacementReference != null) reference.TransformReference(inversePrevious);
            return reference.IsValid;
        }
        reference = new ObjectPlacementReference();
        Point3d[] corners;
        if (obj.HasSourceTransform && obj.LocalBoundingBox.IsValid)
        {
            corners = obj.LocalBoundingBox.GetCorners();
            Transform pose = inversePrevious * obj.SourceTransform;
            for (int i = 0; i < corners.Length; i++) corners[i].Transform(pose);
        }
        else
        {
            var points = SampleGeometry(baseline);
            if (!PlacementBox.TryFit(points.Select(ToCore).ToArray(), out var box) || box == null) return false;
            corners = box.Corners.Select(ToRhino).ToArray();
        }
        reference.BoxCorners = corners;
        reference.BaseCorners = corners.Take(4).ToArray();
        reference.Up = corners[4] - corners[0];
        if (!reference.Up.Unitize()) reference.Up = Vector3d.CrossProduct(corners[1] - corners[0], corners[3] - corners[0]);
        return reference.IsValid;
    }

    internal static IReadOnlyList<Point3d> SampleGeometry(GeometryBase geometry)
    {
        switch (geometry)
        {
            case Mesh mesh: return mesh.Vertices.ToPoint3dArray();
            case Brep brep:
                var meshes = Mesh.CreateFromBrep(brep, MeshingParameters.FastRenderMesh) ?? Array.Empty<Mesh>();
                try { return meshes.SelectMany(m => m.Vertices.ToPoint3dArray()).ToArray(); }
                finally { foreach (var part in meshes) part.Dispose(); }
            case Extrusion extrusion:
                using (var brep = extrusion.ToBrep()) return brep == null ? Array.Empty<Point3d>() : SampleGeometry(brep);
            case PointCloud cloud: return cloud.GetPoints();
            case Curve curve:
                var parameters = curve.DivideByCount(128, true);
                return parameters == null ? Array.Empty<Point3d>() : parameters.Select(curve.PointAt).ToArray();
            default: return Array.Empty<Point3d>();
        }
    }

    internal static bool TryFitTerrainFrame(IReadOnlyList<Point3d> hits, Vector3d heading, out Plane plane)
    {
        plane = Plane.Unset;
        if (!PlacementFrame.TryFit(hits.Select(ToCore).ToArray(), new(heading.X, heading.Y, heading.Z), out var fit)) return false;
        plane = new Plane(ToRhino(fit.Origin), new Vector3d(fit.XAxis.X, fit.XAxis.Y, fit.XAxis.Z),
            new Vector3d(fit.YAxis.X, fit.YAxis.Y, fit.YAxis.Z));
        return plane.IsValid;
    }

    internal static void SetBase(ObjectPlacementReference reference, Plane plane)
    {
        var bounds = new BoundingBox(reference.BoxCorners.Select(p =>
        {
            plane.ClosestParameter(p, out double u, out double v);
            return new Point3d(u, v, 0);
        }));
        reference.BaseCorners = new[] { plane.PointAt(bounds.Min.X, bounds.Min.Y), plane.PointAt(bounds.Max.X, bounds.Min.Y),
            plane.PointAt(bounds.Max.X, bounds.Max.Y), plane.PointAt(bounds.Min.X, bounds.Max.Y) };
        reference.Up = plane.Normal;
        reference.IsCustomBase = true;
    }

    private static PlacementVector ToCore(Point3d point) => new(point.X, point.Y, point.Z);
    private static Point3d ToRhino(PlacementVector point) => new(point.X, point.Y, point.Z);
}
