using System.Globalization;
using MoleHill.Core.Engine;
using MoleHill.Core.Processing;
using MoleHill.Core.Scan;
using MoleHill.Rhino.UI;
using MoleHill.Shared;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;

namespace MoleHill.Rhino.Services;

/// <summary>Captures a mesh or cloud, previews pure scan stages in one floating panel, then validates
/// native buildings and writes the accepted output in the command's single Undo record.</summary>
internal static partial class ScanToTerrainCommandService
{

    public static Result Run(RhinoDoc doc)
    {
        if (!ModelUnitGuard.TryGet(doc, out ModelUnitContext units)) return Result.Failure;
        using var get = new GetObject();
        get.SetCommandPrompt("Select a scan mesh or point cloud (Z up)");
        get.GeometryFilter = ObjectType.Mesh | ObjectType.PointSet;
        get.EnablePreSelect(true, true);
        if (get.Get() != GetResult.Object) return get.CommandResult();
        RhinoObject? source = get.Object(0).Object();
        string sourceName = string.IsNullOrWhiteSpace(source?.Attributes.Name) ? "scan" : source!.Attributes.Name;
        IndexedTriMesh mesh = IndexedTriMesh.Empty; ScanColourSource? colours = null; ScanPointCloud? cloud = null;
        string? diagnostic;
        try
        {
            if (source?.Geometry is PointCloud points)
            {
                cloud = ScanPointCloudReader.Read(points);
                diagnostic = $"{cloud.Count:N0} cloud observations; " + (cloud.Colours == null ? "no colour" : "point colours") +
                    (cloud.Classes == null ? "." : "; retained LAS classes.");
            }
            else if (get.Object(0).Mesh() is Mesh picked)
            {
                using Mesh copy = picked.DuplicateMesh();
                if (!ScanMeshReader.TryRead(doc, source, copy, out mesh, out colours, out diagnostic))
                { RhinoApp.WriteLine(diagnostic); return Result.Failure; }
            }
            else return Result.Failure;
        }
        catch (ArgumentException error) { RhinoApp.WriteLine(error.Message); return Result.Failure; }
        RhinoApp.WriteLine(diagnostic);
        source?.Select(false);
        using var panel = new TerrainCurveExtractionDialog(doc, units, "Scan to Terrain", sourceName, diagnostic ?? string.Empty,
            ScanToTerrainSettings.Load(units), (settings, token, progress, preview) =>
                ComputeAsync(mesh, colours, cloud, (ScanToTerrainSettings)settings, units, token, progress, preview), source?.Id);
        RhinoApp.SetCommandPrompt("Scan to terrain — adjust the floating panel; Enter keeps a ready preview, Esc cancels");
        if (!panel.ShowSemiModal(doc, RhinoEtoApp.MainWindowForDocument(doc)) || panel.Prepared?.Recovery == null)
            return Result.Cancel;
        TerrainCurveExtractionPrepared prepared = panel.Prepared;
        ScanTerrainPipeline.Result pipeline = prepared.Scan!;
        TerrainCurveRecovery.Result recovery = prepared.Recovery!;
        ScanToTerrainSettings accepted = (ScanToTerrainSettings)panel.Settings;
        List<Brep> solids;
        try { solids = BuildBuildingSolids(doc, pipeline.Buildings); }
        catch (OperationCanceledException) { return Result.Cancel; }
        catch (InvalidOperationException error)
        { RhinoApp.WriteLine($"Scan to terrain: {error.Message} No output was added."); return Result.Failure; }
        int added; string root;
        try
        {
            added = TerrainCurveExtractionOutput.Add(doc, recovery, $"Scan {sourceName}", "Scan to Terrain",
                onLayer => AddExtras(doc, pipeline, solids, accepted.AddGroundMesh, onLayer), out root);
        }
        finally { foreach (Brep solid in solids) solid.Dispose(); }
        if (added > 0) accepted.Save(units);
        RhinoApp.WriteLine($"Scan to terrain: {pipeline.Buildings?.Buildings.Count ?? 0} buildings, " +
            $"{pipeline.Walls.Count} walls, {pipeline.Roads.Count} road/kerb stretches; " +
            $"{recovery.Contours.Count} contours and {recovery.SpotPointCount} spots. " + recovery.Diagnostic);
        if (pipeline.Ground.ClassifiedGroundNodes > 0)
            RhinoApp.WriteLine($"Used {pipeline.Ground.ClassifiedGroundNodes:N0} supplied ground cells; " +
                $"{pipeline.Ground.ClassifiedGroundDisagreements:N0} differ from the independent filter by more than its initial allowance.");
        if (added > 0) RhinoApp.WriteLine($"Added to the layers under '{root}'.");
        doc.Views.Redraw(); return added > 0 ? Result.Success : Result.Failure;
    }

    internal static async Task<TerrainCurveExtractionPrepared> ComputeAsync(IndexedTriMesh mesh, ScanColourSource? colours, ScanPointCloud? cloud,
        ScanToTerrainSettings settings, ModelUnitContext units, CancellationToken token, Action<string> progress,
        Action<TerrainCurveExtractionPrepared> preview)
    {
        var options = settings.Pipeline(units, token, progress, stage => preview(TerrainCurveExtractionPrepared.FromScan(stage)));
        ScanTerrainPipeline.Result pipeline = await Task.Run(() => cloud == null
            ? ScanTerrainPipeline.Build(mesh, colours, options) : ScanTerrainPipeline.Build(cloud, options), token);
        token.ThrowIfCancellationRequested();
        if (pipeline.Reference.IsEmpty) throw new InvalidOperationException("No connected ground was found. Try a larger raster cell for a sparse cloud.");
        return await TerrainCurveExtractionService.RecoverAsync(TerrainCurveExtractionPrepared.FromScan(pipeline),
            settings, units, token, progress);
    }

    private static Guid AddGroundMesh(
        RhinoDoc doc, IndexedTriMesh groundMesh, Func<string, System.Drawing.Color, string, ObjectAttributes> onLayer)
    {
        using var mesh = new Mesh();
        double[] vertices = groundMesh.Vertices;
        for (int v = 0; v < groundMesh.VertexCount; v++)
            mesh.Vertices.Add(vertices[v * 3], vertices[(v * 3) + 1], vertices[(v * 3) + 2]);
        int[] faces = groundMesh.Faces;
        for (int f = 0; f < groundMesh.FaceCount; f++)
            mesh.Faces.AddFace(faces[f * 3], faces[(f * 3) + 1], faces[(f * 3) + 2]);
        MeshNormalOrientation.UnifyAndComputeNormalsWelded(mesh);
        return doc.Objects.AddMesh(mesh, onLayer("Ground (check)", System.Drawing.Color.FromArgb(160, 160, 160), "Scan ground"));
    }

    private static IEnumerable<Guid> AddExtras(RhinoDoc doc, ScanTerrainPipeline.Result pipeline,
        IReadOnlyList<Brep> buildingSolids, bool showGround, Func<string, System.Drawing.Color, string, ObjectAttributes> onLayer)
    {
        foreach (ScanWalls.Wall wall in pipeline.Walls)
        {
            using Curve top = RouteCurveBuilder.FromFitted(wall.TopCurve);
            using Curve toe = RouteCurveBuilder.FromFitted(wall.ToeCurve);
            ObjectAttributes Attributes(string side)
            {
                ObjectAttributes attributes = onLayer("Walls and edges", System.Drawing.Color.FromArgb(110, 85, 65), $"Wall {side}");
                attributes.SetUserString("MoleHill.Scan.MeanStep", Format(wall.MeanHeight));
                attributes.SetUserString("MoleHill.Scan.MeasuredLength", Format(wall.Length));
                return attributes;
            }
            yield return doc.Objects.AddCurve(top, Attributes("top"));
            yield return doc.Objects.AddCurve(toe, Attributes("toe"));
        }
        foreach (Guid id in AddSurfaceOutput(doc, pipeline, onLayer))
            yield return id;
        foreach (Guid id in AddBuildingOutput(doc, pipeline.Buildings, buildingSolids, onLayer))
            yield return id;
        if (showGround)
            yield return AddGroundMesh(doc, pipeline.Reference, onLayer);
    }

    private static string Format(double value) => value.ToString("G4", CultureInfo.InvariantCulture);
}
