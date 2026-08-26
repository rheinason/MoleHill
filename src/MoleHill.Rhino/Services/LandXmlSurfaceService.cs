using MoleHill.Core.Interop;
using MoleHill.Rhino.Model;
using MoleHill.Shared;
using Rhino;
using Rhino.Commands;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

internal static class LandXmlSurfaceService
{
    public static Result ImportAsTerrains(
        RhinoDoc doc,
        string path,
        out IReadOnlyList<TerrainDefinition> importedTerrains)
    {
        importedTerrains = Array.Empty<TerrainDefinition>();
        if (!ModelUnitGuard.TryGet(doc, out ModelUnitContext documentUnits))
            return Result.Failure;

        IReadOnlyList<TinSurfaceData> surfaces;
        try
        {
            using var reader = File.OpenText(path);
            surfaces = LandXmlCodec.ReadAll(reader);
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"MoleHill: could not read LandXML: {ex.Message}");
            return Result.Failure;
        }

        Transform toProject = Transform.Identity;
        if (ProjectBaseCPlaneService.TryGetTransform(true, doc, out Transform projectTransform, out _))
            toProject = projectTransform;

        uint undoRecord = doc.BeginUndoRecord("Import LandXML terrains");
        var createdObjectIds = new List<Guid>();
        var createdTerrains = new List<TerrainDefinition>();
        bool success = false;
        try
        {
            foreach (TinSurfaceData surface in surfaces)
            {
                double coordinateScale = LandXmlLinearUnits.MetersPerUnit(surface.LinearUnit) /
                    documentUnits.MetersPerModelUnit;
                TerrainDefinition? terrain = surface.Triangles.Count > 0
                    ? ImportExactTin(doc, surface, coordinateScale, toProject, createdObjectIds)
                    : ImportPointSurface(doc, surface, coordinateScale, toProject, createdObjectIds);
                if (terrain == null)
                    return Result.Failure;
                createdTerrains.Add(terrain);
            }

            importedTerrains = createdTerrains;
            success = true;
            int pointCount = surfaces.Sum(surface => surface.Points.Count);
            int faceCount = surfaces.Sum(surface => surface.Triangles.Count);
            RhinoApp.WriteLine(
                $"MoleHill: imported {surfaces.Count:N0} LandXML surface(s) " +
                $"({pointCount:N0} points, {faceCount:N0} preserved faces).");
            return Result.Success;
        }
        finally
        {
            if (!success)
            {
                foreach (TerrainDefinition terrain in createdTerrains)
                    TerrainController.Instance.DeleteTerrain(doc, terrain.TerrainId);
                DeleteObjects(doc, createdObjectIds);
            }
            doc.EndUndoRecord(undoRecord);
        }
    }

    public static bool TryExport(TinSurfaceData surface, string path, out string? error)
    {
        error = null;
        try
        {
            File.WriteAllText(path, LandXmlCodec.Write(surface));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static TerrainDefinition? ImportExactTin(
        RhinoDoc doc,
        TinSurfaceData surface,
        double coordinateScale,
        Transform toProject,
        ICollection<Guid> createdObjectIds)
    {
        var pointIndexes = new Dictionary<int, int>(surface.Points.Count);
        using var mesh = new Mesh();
        foreach (TinSurfacePoint point in surface.Points)
        {
            Point3d location = TransformPoint(point, coordinateScale, toProject);
            pointIndexes.Add(point.Id, mesh.Vertices.Count);
            mesh.Vertices.Add(location);
        }

        foreach (TinSurfaceTriangle face in surface.Triangles)
            mesh.Faces.AddFace(pointIndexes[face.A], pointIndexes[face.B], pointIndexes[face.C]);
        mesh.Normals.ComputeNormals();
        mesh.UnifyNormals();
        mesh.Compact();
        if (!mesh.IsValid)
        {
            RhinoApp.WriteLine($"MoleHill: LandXML surface '{surface.Name}' produced an invalid TIN mesh.");
            return null;
        }

        Guid meshId = doc.Objects.AddMesh(mesh);
        if (meshId == Guid.Empty)
            return null;
        createdObjectIds.Add(meshId);
        doc.Objects.Hide(meshId, ignoreLayerMode: true);
        return TerrainController.Instance.CreateTerrainFromTinMeshId(doc, meshId, surface.Name);
    }

    private static TerrainDefinition? ImportPointSurface(
        RhinoDoc doc,
        TinSurfaceData surface,
        double coordinateScale,
        Transform toProject,
        ICollection<Guid> createdObjectIds)
    {
        var pointIds = new List<Guid>(surface.Points.Count);
        foreach (TinSurfacePoint point in surface.Points)
        {
            Guid id = doc.Objects.AddPoint(TransformPoint(point, coordinateScale, toProject));
            if (id == Guid.Empty)
                return null;
            pointIds.Add(id);
            createdObjectIds.Add(id);
            doc.Objects.Hide(id, ignoreLayerMode: true);
        }

        return TerrainController.Instance.CreateTerrainFromPointIds(doc, pointIds, surface.Name);
    }

    private static Point3d TransformPoint(TinSurfacePoint point, double scale, Transform transform)
    {
        var result = new Point3d(point.X * scale, point.Y * scale, point.Z * scale);
        result.Transform(transform);
        return result;
    }

    private static void DeleteObjects(RhinoDoc doc, IEnumerable<Guid> objectIds)
    {
        foreach (Guid id in objectIds)
        {
            global::Rhino.DocObjects.RhinoObject? obj = doc.Objects.FindId(id);
            if (obj != null)
                doc.Objects.Delete(obj, quiet: true, ignoreModes: true);
        }
    }
}
