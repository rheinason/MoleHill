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
        // Only what touches the document happens here. Copying a million triangles and decoding an 8k texture
        // froze Rhino for seconds before the panel appeared, so that is read in the background while it opens.
        Task<(IndexedTriMesh Mesh, ScanColourSource? Colours, ScanPointCloud? Cloud, string Diagnostic)> reading;
        try
        {
            if (source?.Geometry is PointCloud points)
            {
                ScanPointCloud cloud = ScanPointCloudReader.Read(points);
                string note = FormattableString.Invariant($"{cloud.Count:N0} cloud observations; ") + (cloud.Colours == null ? "no colour" : "point colours") +
                    (cloud.Classes == null ? "." : "; retained LAS classes.");
                reading = Task.FromResult((IndexedTriMesh.Empty, (ScanColourSource?)null, (ScanPointCloud?)cloud, note));
            }
            else if (get.Object(0).Mesh() is Mesh picked)
            {
                Mesh copy = picked.DuplicateMesh();
                ScanMeshReader.DiffuseTexture? texture = ScanMeshReader.FindDiffuse(doc, source, copy, out string? textureNote);
                reading = Task.Run(() =>
                {
                    using (copy)
                    {
                        if (!ScanMeshReader.TryRead(copy, texture, textureNote, out IndexedTriMesh mesh, out ScanColourSource? colours, out string? note))
                            throw new InvalidOperationException(note ?? "The scan could not be read.");
                        return (mesh, colours, (ScanPointCloud?)null, note ?? string.Empty);
                    }
                });
            }
            else return Result.Failure;
        }
        catch (ArgumentException error) { RhinoApp.WriteLine(error.Message); return Result.Failure; }
        source?.Select(false);
        using var panel = new TerrainCurveExtractionPanel(doc, units, "Scan to Terrain", sourceName, "Reading the scan…",
            ScanToTerrainSettings.Load(units), async (settings, token, progress, preview) =>
            {
                var input = await reading;
                return await ComputeAsync(input.Mesh, input.Colours, input.Cloud, (ScanToTerrainSettings)settings, units, token, progress, preview);
            }, source?.Id, manualRecompute: true, loading: ReadSummary(reading));
        if (!panel.ShowAndWait() || panel.Prepared?.Recovery == null)
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
        // Zone fills go beneath the whole source, whose skirts and fringes reach below the ground found in it.
        double? sourceBottom = source?.Geometry?.GetBoundingBox(true) is { IsValid: true } box ? box.Min.Z : null;
        try
        {
            added = TerrainCurveExtractionOutput.Add(doc, recovery, sourceName, "Scan to Terrain",
                onLayer => AddExtras(doc, pipeline, solids, accepted.AddGroundMesh, sourceBottom, onLayer), out root);
        }
        finally { foreach (Brep solid in solids) solid.Dispose(); }
        if (added > 0) accepted.Save(units);
        RhinoApp.WriteLine($"Scan to terrain: {pipeline.Buildings?.Buildings.Count ?? 0} buildings, " +
            $"{pipeline.Walls.Count} walls, {pipeline.Roads.Count} road/kerb stretches; " +
            $"{recovery.Contours.Count} contours and {recovery.SpotPointCount} spots. " + recovery.Diagnostic);
        if (pipeline.Ground.ClassifiedGroundNodes > 0)
            RhinoApp.WriteLine(FormattableString.Invariant($"Used {pipeline.Ground.ClassifiedGroundNodes:N0} supplied ground cells; ") +
                FormattableString.Invariant($"{pipeline.Ground.ClassifiedGroundDisagreements:N0} differ from the independent filter by more than its initial allowance."));
        if (added > 0) RhinoApp.WriteLine($"Added to the layers under '{root}'.");
        doc.Views.Redraw(); return added > 0 ? Result.Success : Result.Failure;
    }

    private static async Task<string> ReadSummary(Task<(IndexedTriMesh Mesh, ScanColourSource? Colours, ScanPointCloud? Cloud, string Diagnostic)> reading)
    {
        string diagnostic = (await reading).Diagnostic;
        RhinoApp.WriteLine(diagnostic);
        return diagnostic;
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
        RhinoDoc doc, IndexedTriMesh groundMesh, Func<TerrainCurveExtractionStyle.Style, string, ObjectAttributes> onLayer)
    {
        using var mesh = new Mesh();
        double[] vertices = groundMesh.Vertices;
        for (int v = 0; v < groundMesh.VertexCount; v++)
            mesh.Vertices.Add(vertices[v * 3], vertices[(v * 3) + 1], vertices[(v * 3) + 2]);
        int[] faces = groundMesh.Faces;
        for (int f = 0; f < groundMesh.FaceCount; f++)
            mesh.Faces.AddFace(faces[f * 3], faces[(f * 3) + 1], faces[(f * 3) + 2]);
        MeshNormalOrientation.UnifyAndComputeNormalsWelded(mesh);
        return doc.Objects.AddMesh(mesh, onLayer(TerrainCurveExtractionStyle.Ground, TerrainCurveExtractionStyle.Ground.Name));
    }

    private static IEnumerable<Guid> AddExtras(RhinoDoc doc, ScanTerrainPipeline.Result pipeline,
        IReadOnlyList<Brep> buildingSolids, bool showGround, double? sourceBottom, Func<TerrainCurveExtractionStyle.Style, string, ObjectAttributes> onLayer)
    {
        foreach (ScanWalls.Wall wall in pipeline.Walls)
        {
            using Curve top = ScanCurveBuilder.Build(wall.TopCurve);
            using Curve toe = ScanCurveBuilder.Build(wall.ToeCurve);
            ObjectAttributes Attributes(string side)
            {
                ObjectAttributes attributes = onLayer(TerrainCurveExtractionStyle.Walls, $"Wall {side}");
                attributes.SetUserString("MoleHill.Scan.MeanStep", Format(wall.MeanHeight));
                attributes.SetUserString("MoleHill.Scan.MeasuredLength", Format(wall.Length));
                ScanCurveBuilder.StampEvidence(attributes, side == "top" ? wall.TopCurve : wall.ToeCurve);
                return attributes;
            }
            yield return doc.Objects.AddCurve(top, Attributes("top"));
            yield return doc.Objects.AddCurve(toe, Attributes("toe"));
        }
        foreach (Guid id in AddSurfaceOutput(doc, pipeline, sourceBottom, onLayer))
            yield return id;
        foreach (Guid id in AddBuildingOutput(doc, pipeline.Buildings, buildingSolids, onLayer))
            yield return id;
        if (showGround)
            yield return AddGroundMesh(doc, pipeline.Reference, onLayer);
    }

    private static string Format(double value) => value.ToString("G4", CultureInfo.InvariantCulture);
}
