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
        return ResolveObjects(doc, sourceSet, out _);
    }

    public static List<RhinoObject> ResolveObjects(
        RhinoDoc doc,
        SourceReferenceSet sourceSet,
        out SourceResolutionDiagnostics diagnostics)
    {
        var objectsById = new Dictionary<Guid, RhinoObject>();
        var candidateIds = new HashSet<Guid>();
        var resultDiagnostics = new SourceResolutionDiagnostics();

        void Consider(RhinoObject? obj)
        {
            if (obj == null)
            {
                resultDiagnostics.MissingReferences++;
                return;
            }

            if (!candidateIds.Add(obj.Id))
                return;

            if (obj.IsDeleted)
            {
                resultDiagnostics.RejectedDeletedObjects++;
                return;
            }

            if (obj.IsInstanceDefinitionGeometry)
            {
                resultDiagnostics.RejectedInstanceDefinitionObjects++;
                return;
            }

            if (obj.IsReference)
            {
                resultDiagnostics.RejectedReferenceObjects++;
                return;
            }

            if (!IsSupportedSourceSpace(obj.Attributes.Space))
            {
                resultDiagnostics.RejectedPageSpaceObjects++;
                return;
            }

            if (obj.IsHidden)
                resultDiagnostics.AcceptedObjectHiddenObjects++;
            else
                resultDiagnostics.AcceptedNormalOrLayerHiddenObjects++;

            if (obj.IsLocked)
                resultDiagnostics.AcceptedLockedObjects++;
            if (obj.IsSelectable())
                resultDiagnostics.AcceptedSelectableObjects++;

            BoundingBox objectBounds = obj.Geometry?.GetBoundingBox(true) ?? BoundingBox.Empty;
            if (objectBounds.IsValid)
            {
                if (resultDiagnostics.AcceptedBounds.IsValid)
                {
                    BoundingBox acceptedBounds = resultDiagnostics.AcceptedBounds;
                    acceptedBounds.Union(objectBounds);
                    resultDiagnostics.AcceptedBounds = acceptedBounds;
                }
                else
                    resultDiagnostics.AcceptedBounds = objectBounds;
            }

            objectsById[obj.Id] = obj;
        }

        foreach (var objectId in sourceSet.ObjectIds)
            Consider(doc.Objects.FindId(objectId));

        foreach (var layerPath in sourceSet.LayerPaths)
        {
            int layerIndex = doc.Layers.FindByFullPath(layerPath, -1);
            if (layerIndex < 0)
            {
                resultDiagnostics.MissingLayers++;
                continue;
            }

            resultDiagnostics.ResolvedLayers++;
            Layer layer = doc.Layers[layerIndex];

            foreach (var layerObject in doc.Objects.FindByLayer(layer))
            {
                // FindByLayer can expose stale object-table wrappers retained by Rhino. Resolve the ID
                // back through the active object table; deleted objects cannot be found by ID.
                RhinoObject? activeObject = layerObject == null ? null : doc.Objects.FindId(layerObject.Id);
                if (activeObject == null)
                {
                    resultDiagnostics.RejectedStaleLayerObjects++;
                    continue;
                }

                if (activeObject.Attributes.LayerIndex != layerIndex)
                {
                    resultDiagnostics.RejectedWrongLayerObjects++;
                    continue;
                }

                Consider(activeObject);
            }
        }

        resultDiagnostics.AcceptedObjects = objectsById.Count;
        diagnostics = resultDiagnostics;
        return objectsById.Values.ToList();
    }

    internal static bool IsSupportedSourceSpace(ActiveSpace space)
    {
        return space == ActiveSpace.ModelSpace;
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

    /// <summary>
    /// Closed curves as flat, non-repeating XY loops. Open or degenerate curves are skipped: a boundary
    /// that does not close has no inside. Shared by modifiers (Project To) and analyses (gradient
    /// compliance), which is why it lives here: a modifier must never depend on analysis code.
    /// </summary>
    public static List<double[]> ToXyLoops(IReadOnlyList<Curve> curves, double tolerance)
    {
        var loops = new List<double[]>(curves.Count);
        foreach (Curve curve in curves)
        {
            if (!RhinoSourceResolver.TryGetPolyline(curve, tolerance, requireClosed: true, out Polyline polyline))
                continue;

            int count = polyline.Count;
            if (count > 1 && polyline[0].DistanceTo(polyline[^1]) <= tolerance)
                count--;
            if (count < 3)
                continue;

            var xy = new double[count * 2];
            for (int i = 0; i < count; i++)
            {
                xy[i * 2] = polyline[i].X;
                xy[(i * 2) + 1] = polyline[i].Y;
            }

            loops.Add(xy);
        }

        return loops;
    }

    /// <summary>Curves as flat XY polylines, open or closed. Degenerate curves are skipped.</summary>
    public static List<double[]> ToXyPolylines(IReadOnlyList<Curve> curves, double tolerance)
    {
        var polylines = new List<double[]>(curves.Count);
        foreach (Curve curve in curves)
        {
            if (!RhinoSourceResolver.TryGetPolyline(curve, tolerance, requireClosed: false, out Polyline polyline) ||
                polyline.Count < 2)
                continue;

            var xy = new double[polyline.Count * 2];
            for (int i = 0; i < polyline.Count; i++)
            {
                xy[i * 2] = polyline[i].X;
                xy[(i * 2) + 1] = polyline[i].Y;
            }

            polylines.Add(xy);
        }

        return polylines;
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
