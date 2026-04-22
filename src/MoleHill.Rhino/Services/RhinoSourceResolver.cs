using MoleHill.Rhino.Model;
using MoleHill.Shared;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

internal static class RhinoSourceResolver
{
    public static List<RhinoObject> ResolveObjects(RhinoDoc doc, SourceReferenceSet sourceSet)
    {
        var objectsById = new Dictionary<Guid, RhinoObject>();

        foreach (var objectId in sourceSet.ObjectIds)
        {
            var obj = doc.Objects.FindId(objectId);
            if (obj != null)
                objectsById[objectId] = obj;
        }

        foreach (var layerPath in sourceSet.LayerPaths)
        {
            int layerIndex = doc.Layers.FindByFullPath(layerPath, -1);
            if (layerIndex < 0)
                continue;

            var settings = new ObjectEnumeratorSettings
            {
                NormalObjects = true,
                LockedObjects = true,
                HiddenObjects = true,
                ActiveObjects = true,
                DeletedObjects = false,
                LayerIndexFilter = layerIndex
            };

            foreach (var obj in doc.Objects.GetObjectList(settings))
            {
                if (obj != null)
                    objectsById[obj.Id] = obj;
            }
        }

        return objectsById.Values.ToList();
    }

    public static List<Point3d> ResolvePoints(RhinoDoc doc, SourceReferenceSet sourceSet)
    {
        var points = new List<Point3d>();
        foreach (var obj in ResolveObjects(doc, sourceSet))
        {
            switch (obj.Geometry)
            {
                case Point point:
                    points.Add(point.Location);
                    break;
                case PointCloud pointCloud:
                    for (int i = 0; i < pointCloud.Count; i++)
                        points.Add(pointCloud[i].Location);
                    break;
            }
        }

        return points;
    }

    public static List<Curve> ResolveCurves(RhinoDoc doc, SourceReferenceSet sourceSet)
    {
        var curves = new List<Curve>();
        foreach (var obj in ResolveObjects(doc, sourceSet))
        {
            if (obj.Geometry is Curve curve)
                curves.Add(curve.DuplicateCurve());
        }

        return curves;
    }

    public static List<Mesh> ResolveMeshes(RhinoDoc doc, SourceReferenceSet sourceSet)
    {
        var meshes = new List<Mesh>();
        foreach (var obj in ResolveObjects(doc, sourceSet))
        {
            switch (obj.Geometry)
            {
                case Mesh mesh:
                    meshes.Add(mesh.DuplicateMesh());
                    break;
                case Brep brep:
                    var brepMeshes = Mesh.CreateFromBrep(brep, MeshingParameters.FastRenderMesh);
                    if (brepMeshes == null)
                        break;

                    foreach (var brepMesh in brepMeshes)
                        meshes.Add(brepMesh);
                    break;
                case Extrusion extrusion:
                    var extrusionBrep = extrusion.ToBrep();
                    if (extrusionBrep == null)
                        break;

                    var extrusionMeshes = Mesh.CreateFromBrep(extrusionBrep, MeshingParameters.FastRenderMesh);
                    if (extrusionMeshes == null)
                        break;

                    foreach (var extrusionMesh in extrusionMeshes)
                        meshes.Add(extrusionMesh);
                    break;
            }
        }

        return meshes;
    }

    public static List<Point3d> ResolveMarkerSamplePoints(RhinoDoc doc, SourceReferenceSet sourceSet)
    {
        var points = new List<Point3d>();
        foreach (var obj in ResolveObjects(doc, sourceSet))
        {
            switch (obj.Geometry)
            {
                case Point point:
                    points.Add(point.Location);
                    break;
                case PointCloud pointCloud:
                    for (int i = 0; i < pointCloud.Count; i++)
                        points.Add(pointCloud[i].Location);
                    break;
                case Curve curve:
                    points.Add(curve.PointAt(curve.Domain.ParameterAt(0.5)));
                    break;
            }
        }

        return points;
    }

    public static bool TryGetPolyline(Curve curve, double tolerance, bool requireClosed, out Polyline polyline)
    {
        return TryGetPolyline(curve, tolerance, requireClosed, requestedEdgeLength: 0.0, maxArea: 0.0, out polyline);
    }

    public static bool TryGetPolyline(
        Curve curve,
        double tolerance,
        bool requireClosed,
        double requestedEdgeLength,
        double maxArea,
        out Polyline polyline)
    {
        return AdaptivePolylineBuilder.TryGetPolyline(
            curve,
            tolerance,
            requireClosed,
            requestedEdgeLength,
            maxArea,
            out polyline);
    }
}
