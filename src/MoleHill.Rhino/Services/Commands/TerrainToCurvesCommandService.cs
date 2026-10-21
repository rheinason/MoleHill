// mhTerrainToCurves: recovers a border, spot points and as few contours as rebuild a terrain within tolerance.
using System.Globalization;
using MoleHill.Core.Engine;
using MoleHill.Core.Processing;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.UI;
using Rhino.UI;
using MoleHill.Shared;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Turns a dense terrain — a DEM, a point-cloud mesh, or the active MoleHill terrain — into editable input:
/// border curves, spot points on peaks and pits, and the fewest contours that rebuild it within a vertical
/// tolerance (<see cref="TerrainCurveRecovery"/>). The output is ordinary Rhino geometry, ready to assign as
/// a new terrain's sources and edit from there. The shared panel previews before Keep; Esc discards it.
/// </summary>
internal static class TerrainToCurvesCommandService
{
    /// <summary>
    /// From here a preview takes a minute or more (122k vertices: about 40 s; 275k with walls: about five
    /// minutes), so a changed setting waits for Recompute instead of restarting a run.
    /// </summary>
    internal const int LargeTerrainVertices = 100_000;

    public static Result Run(RhinoDoc doc)
    {
        if (!ModelUnitGuard.TryGet(doc, out ModelUnitContext unitContext))
            return Result.Failure;

        using var getMesh = new GetObject();
        getMesh.SetCommandPrompt("Select a terrain mesh to turn into curves");
        getMesh.GeometryFilter = ObjectType.Mesh;
        getMesh.EnablePreSelect(true, true);
        int activeTerrainOption = getMesh.AddOption("ActiveMoleHillTerrain");
        GetResult picked = getMesh.Get();
        Mesh? mesh;
        string sourceName;
        Guid? sourceId = null;
        if (picked == GetResult.Option && getMesh.OptionIndex() == activeTerrainOption)
        {
            TerrainDefinition? terrain = TerrainController.Instance.GetSelectedTerrain(doc);
            mesh = terrain == null ? null : TerrainController.Instance.DuplicateFinalTerrainMesh(doc, terrain.TerrainId);
            if (mesh == null)
            {
                RhinoApp.WriteLine("Select and build an active MoleHill terrain first.");
                return Result.Nothing;
            }

            sourceName = terrain!.Name;
        }
        else if (picked == GetResult.Object && getMesh.Object(0).Mesh() is { } selected)
        {
            mesh = selected.DuplicateMesh();
            sourceId = getMesh.Object(0).ObjectId;
            getMesh.Object(0).Object()?.Select(false);
            string? objectName = getMesh.Object(0).Object()?.Attributes.Name;
            sourceName = string.IsNullOrWhiteSpace(objectName) ? "mesh" : objectName;
        }
        else
        {
            return getMesh.CommandResult();
        }

        using (mesh)
        {
            if (!RhinoGeometryConversions.TryExtractMesh(mesh, out IndexedTriMesh terrainMesh, out string? meshError))
            { RhinoApp.WriteLine(meshError ?? "The mesh could not be read."); return Result.Failure; }
            const string prefix = "MoleHill.TerrainToCurves.";
            var settings = TerrainCurveExtractionSettings.Load(unitContext, prefix);
            using var panel = new TerrainCurveExtractionDialog(doc, unitContext, "Terrain to Curves", sourceName,
                FormattableString.Invariant($"{terrainMesh.VertexCount:N0} {(terrainMesh.VertexCount == 1 ? "vertex" : "vertices")} from the final terrain mesh. Accuracy is measured against this surface."), settings,
                async (current, token, progress, preview) =>
                {
                    var reference = new TerrainCurveExtractionPrepared(terrainMesh);
                    preview(reference);
                    return await TerrainCurveExtractionService.RecoverAsync(reference, current, unitContext, token, progress);
                }, sourceId, manualRecompute: terrainMesh.VertexCount >= LargeTerrainVertices);
            RhinoApp.SetCommandPrompt("Terrain to curves — adjust the floating panel; Enter keeps a ready preview, Esc cancels");
            if (!panel.ShowSemiModal(doc, RhinoEtoApp.MainWindowForDocument(doc)) || panel.Prepared?.Recovery is not { } recovery)
                return Result.Cancel;
            int added = TerrainCurveExtractionOutput.Add(doc, recovery, $"Terrain to Curves {sourceName}", "Terrain to Curves", null, out string root);
            if (added > 0) panel.Settings.Save(unitContext, prefix);
            RhinoApp.WriteLine($"Terrain to curves: {recovery.Contours.Count} contours, {recovery.SpotPointCount} spots; " + recovery.Diagnostic);
            if (added > 0) RhinoApp.WriteLine($"Added to the layers under '{root}'.");
            doc.Views.Redraw();
            return added > 0 ? Result.Success : Result.Failure;
        }
    }
}
