// Picks the terrain the route commands search over: a mesh or surface, or the active MoleHill terrain.
using MoleHill.Rhino.Model;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace MoleHill.Rhino.Services;

/// <summary>
/// The terrain prompt mhSuggestRoute and mhRelaxRoute share. Enter takes the active MoleHill terrain's final
/// build; a picked mesh, surface, polysurface or extrusion is meshed and combined. The caller owns the mesh.
/// </summary>
internal static class RouteTerrainPicker
{
    public static Mesh? Pick(RhinoDoc doc, string verb, out Result result)
    {
        result = Result.Cancel;
        var getTarget = new GetObject();
        getTarget.SetCommandPrompt("Select the terrain mesh or surface. Press Enter for the active MoleHill terrain");
        getTarget.GeometryFilter = ObjectType.Mesh | ObjectType.Surface | ObjectType.Brep | ObjectType.Extrusion;
        getTarget.EnablePreSelect(true, true);
        getTarget.AcceptNothing(true);
        int activeTerrainOption = getTarget.AddOption("ActiveMoleHillTerrain");

        GetResult targetResult = getTarget.Get();
        if (targetResult == GetResult.Nothing ||
            (targetResult == GetResult.Option && getTarget.OptionIndex() == activeTerrainOption))
        {
            TerrainDefinition? definition = TerrainController.Instance.GetSelectedTerrain(doc);
            if (definition == null)
            {
                RhinoApp.WriteLine($"Select an active MoleHill terrain, or pick a mesh or surface, before {verb}.");
                result = Result.Nothing;
                return null;
            }

            Mesh? finalMesh = TerrainController.Instance.DuplicateFinalTerrainMesh(doc, definition.TerrainId);
            if (finalMesh == null)
            {
                RhinoApp.WriteLine($"Active terrain '{definition.Name}' has no current final build. Rebuild it before {verb}.");
                result = Result.Nothing;
            }

            return finalMesh;
        }

        if (targetResult != GetResult.Object)
        {
            result = getTarget.CommandResult();
            return null;
        }

        RhinoObject? targetObject = getTarget.Object(0).Object();
        if (targetObject == null)
        {
            result = Result.Failure;
            return null;
        }

        List<Mesh> parts = TerrainInputCommandService.CreateProjectionMeshes(targetObject.Geometry);
        if (parts.Count == 0)
        {
            RhinoApp.WriteLine("The selected object could not be converted into a usable mesh.");
            result = Result.Failure;
            return null;
        }

        var combined = new Mesh();
        foreach (Mesh part in parts)
        {
            combined.Append(part);
            part.Dispose();
        }

        doc.Objects.UnselectAll();
        return combined;
    }
}
