using MoleHill.Core.Interop;
using MoleHill.Rhino.Model;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

internal static class LandXmlSurfaceService
{
    public static Result ImportAsTerrain(RhinoDoc doc, string path, out TerrainDefinition? terrain)
    {
        terrain = null;
        TinSurfaceData surface;
        try
        {
            surface = LandXmlCodec.Read(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"MoleHill: could not read LandXML: {ex.Message}");
            return Result.Failure;
        }

        var ids = new List<Guid>(surface.Points.Count);
        foreach (TinSurfacePoint point in surface.Points)
        {
            Guid id = doc.Objects.AddPoint(new Point3d(point.X, point.Y, point.Z));
            if (id == Guid.Empty)
                return Result.Failure;
            ids.Add(id);
        }

        terrain = TerrainController.Instance.CreateTerrainFromPointIds(doc, ids, surface.Name);
        if (terrain == null)
            return Result.Failure;

        RhinoApp.WriteLine($"MoleHill: imported LandXML surface '{surface.Name}' ({surface.Points.Count:N0} points, {surface.Triangles.Count:N0} faces).");
        return Result.Success;
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
}
