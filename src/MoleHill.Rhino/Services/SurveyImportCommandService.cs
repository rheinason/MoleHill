using Eto.Forms;
using MoleHill.Core.Interop;
using MoleHill.Rhino.UI;
using MoleHill.Shared;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.UI;
using OpenFileDialog = Eto.Forms.OpenFileDialog;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Command entry points for survey point import and its field code table.
///
/// The import creates ordinary Rhino curves and points on named layers and **never touches a
/// <c>TerrainDefinition</c>** — the user assigns those layers through the normal source editor, exactly
/// as with <c>mhDrapeCurve</c>. That is what makes a revised survey a re-import rather than a
/// reassignment.
/// </summary>
internal static class SurveyImportCommandService
{
    public static Result RunEditFieldCodes(RhinoDoc doc)
    {
        bool saved = FieldCodeTableEditorDialog.ShowDialog(doc, MoleHillRhinoPlugin.Instance.FieldCodeTableStore);
        return saved ? Result.Success : Result.Cancel;
    }

    public static Result RunImportSurveyPoints(RhinoDoc doc)
    {
        if (!ModelUnitGuard.TryGet(doc, out ModelUnitContext units))
            return Result.Failure;

        if (!TryChooseFile(doc, out string path, out string content))
            return Result.Cancel;

        if (!SurveyImportDialog.ShowDialog(doc, path, content, units, out SurveyReadOptions options))
            return Result.Cancel;

        SurveyPointFile file = SurveyPointFileReader.Read(content, options);
        if (!file.HasPoints)
        {
            RhinoApp.WriteLine("MoleHill: no points could be read from that file.");
            return Result.Nothing;
        }

        Result placement = SurveyPlacement.Resolve(
            doc,
            file.Points,
            units,
            out Transform toDocument,
            out SurveyPlacementChoice choice);
        if (placement != Result.Success)
            return placement;

        FieldCodeTable table = MoleHillRhinoPlugin.Instance.FieldCodeTableStore.Load(out string? tableWarning);
        if (tableWarning != null)
            RhinoApp.WriteLine(tableWarning);
        SurveyImportResult parsed = SurveyFigureBuilder.Build(file.Points, table);

        return Create(doc, file, parsed, table, toDocument, choice, units);
    }

    private static bool TryChooseFile(RhinoDoc doc, out string path, out string content)
    {
        path = string.Empty;
        content = string.Empty;

        var dialog = new OpenFileDialog { Title = "Import Survey Points" };
        dialog.Filters.Add(new FileFilter("Survey Points", ".csv", ".txt", ".pnt", ".asc", ".nez"));
        dialog.Filters.Add(new FileFilter("All Files", ".*"));
        if (dialog.ShowDialog(RhinoEtoApp.MainWindowForDocument(doc)) != DialogResult.Ok ||
            string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return false;
        }

        path = dialog.FileName;
        try
        {
            content = File.ReadAllText(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RhinoApp.WriteLine($"MoleHill: could not read that file: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Creates the geometry under one undo record, rolling the whole import back if any part of it
    /// fails. A half-imported survey is worse than none: the user cannot tell which half is missing.
    ///
    /// The rollback lives in the finally block alone, next to the one <c>EndUndoRecord</c>, so every
    /// exit path — a failed add, an exception, success — ends the record exactly once (the GeoTIFF
    /// importer's pattern).
    /// </summary>
    private static Result Create(
        RhinoDoc doc,
        SurveyPointFile file,
        SurveyImportResult parsed,
        FieldCodeTable table,
        Transform toDocument,
        SurveyPlacementChoice choice,
        ModelUnitContext units)
    {
        List<Point3d> points = file.Points
            .Select(point =>
            {
                var placed = new Point3d(point.X, point.Y, point.Z);
                placed.Transform(toDocument);
                return placed;
            })
            .ToList();

        uint undoRecord = doc.BeginUndoRecord("Import survey points");
        var created = new List<Guid>();
        var reportedLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool success = false;
        try
        {
            int figuresDrawn = 0;
            foreach (SurveyFigure figure in parsed.Figures)
            {
                Curve? curve = SurveyGeometryBuilder.BuildFigure(figure, points);
                if (curve == null)
                    continue;

                var attributes = new ObjectAttributes
                {
                    LayerIndex = EnsureLayer(doc, figure.Layer, FieldCodeTable.DefaultLayerFor(figure.Role), reportedLayers)
                };

                Guid id = doc.Objects.AddCurve(curve, attributes);
                if (id == Guid.Empty)
                {
                    RhinoApp.WriteLine("MoleHill: could not create the survey linework; nothing was imported.");
                    return Result.Failure;
                }

                created.Add(id);
                figuresDrawn++;
            }

            int spotsDrawn = AddSpots(doc, points, parsed, created, reportedLayers);
            int unmatchedDrawn = AddPoints(
                doc,
                points,
                parsed.UnmatchedPointIndices,
                EnsureLayer(doc, table.UnmatchedLayer, FieldCodeTable.DefaultUnmatchedLayer, reportedLayers),
                created);

            doc.Views.Redraw();
            Report(file, parsed, choice, units, figuresDrawn, spotsDrawn, unmatchedDrawn);
            if (choice == SurveyPlacementChoice.ProjectBaseCreated)
                SurveyPlacement.RegisterCreatedProjectBaseUndo(doc);
            success = true;
            return Result.Success;
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine($"MoleHill: the survey import failed and was rolled back: {ex.Message}");
            return Result.Failure;
        }
        finally
        {
            if (!success)
            {
                RollBack(doc, created);
                if (choice == SurveyPlacementChoice.ProjectBaseCreated)
                {
                    SurveyPlacement.RevertCreatedProjectBase(doc);
                    RhinoApp.WriteLine("MoleHill: the project base created for this survey was removed again.");
                }
            }
            doc.EndUndoRecord(undoRecord);
        }
    }

    private static int AddPoints(
        RhinoDoc doc,
        IReadOnlyList<Point3d> points,
        IReadOnlyList<int> indices,
        int layerIndex,
        List<Guid> created)
    {
        if (indices.Count == 0)
            return 0;

        var attributes = new ObjectAttributes { LayerIndex = layerIndex };
        int drawn = 0;
        foreach (int index in indices)
        {
            if (index < 0 || index >= points.Count)
                continue;

            Guid id = doc.Objects.AddPoint(points[index], attributes);
            if (id == Guid.Empty)
                continue;

            created.Add(id);
            drawn++;
        }

        return drawn;
    }

    /// <summary>
    /// Adds each spot on the layer its own rule resolved to, grouped so each layer is ensured once.
    /// Grouping keeps file order within a layer, which is the only order a user could notice.
    /// </summary>
    private static int AddSpots(
        RhinoDoc doc,
        IReadOnlyList<Point3d> points,
        SurveyImportResult parsed,
        List<Guid> created,
        HashSet<string> reportedLayers)
    {
        int drawn = 0;
        foreach (IGrouping<string, int> group in Enumerable.Range(0, parsed.SpotPointIndices.Count)
                     .GroupBy(i => parsed.SpotLayers[i], StringComparer.OrdinalIgnoreCase))
        {
            List<int> indices = group.Select(i => parsed.SpotPointIndices[i]).ToList();
            int layerIndex = EnsureLayer(doc, group.Key, FieldCodeTable.DefaultLayerFor(FieldCodeRole.Spot), reportedLayers);
            drawn += AddPoints(doc, points, indices, layerIndex, created);
        }

        return drawn;
    }

    /// <summary>
    /// The layer for a typed path, or for <paramref name="fallbackPath"/> when the typed one is unusable.
    ///
    /// <see cref="LayerCreationService.EnsureLayerPath"/> answers a blank or rejected path with the
    /// <i>current</i> layer, so an unvalidated path would scatter survey linework onto whatever the user
    /// last clicked. The fallback is always one of this command's own role layers, and the substitution
    /// is reported once per path so the user can correct the rule.
    /// </summary>
    private static int EnsureLayer(RhinoDoc doc, string layerPath, string fallbackPath, HashSet<string> reportedLayers)
    {
        LayerRoleTable roles = LayerRoleService.GetTable(doc);
        if (IsUsableLayerPath(layerPath))
        {
            int index = LayerCreationService.EnsureLayerPath(doc, layerPath, roles);
            if (index >= 0 && index < doc.Layers.Count &&
                string.Equals(doc.Layers[index].FullPath, layerPath, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        if (reportedLayers.Add(layerPath ?? string.Empty))
        {
            RhinoApp.WriteLine(
                $"MoleHill: \"{layerPath}\" is not a usable layer path; that output went to {fallbackPath} instead. " +
                "Correct it with mhEditFieldCodes.");
        }

        return LayerCreationService.EnsureLayerPath(doc, fallbackPath, roles);
    }

    /// <summary>
    /// Whether a typed layer path can be created as written: the structural rules in Core plus Rhino's
    /// own naming rules for each segment. Shared with the field code editor so it can refuse to save
    /// a path the import would have to replace.
    /// </summary>
    internal static bool IsUsableLayerPath(string? layerPath) =>
        FieldCodeTable.IsValidLayerPath(layerPath) &&
        layerPath!.Split("::").All(ModelComponent.IsValidComponentName);

    private static void RollBack(RhinoDoc doc, List<Guid> created)
    {
        foreach (Guid id in created)
            doc.Objects.Delete(id, quiet: true);
        created.Clear();
        doc.Views.Redraw();
    }

    /// <summary>
    /// Says what was imported and, more importantly, what was not.
    ///
    /// Unmatched codes are listed by name because that is the list the user acts on — a code with no
    /// rule is the normal first-run state, and naming it turns "some linework is missing" into "add TREE
    /// to the table and re-import".
    /// </summary>
    private static void Report(
        SurveyPointFile file,
        SurveyImportResult parsed,
        SurveyPlacementChoice choice,
        ModelUnitContext units,
        int figuresDrawn,
        int spotsDrawn,
        int unmatchedDrawn)
    {
        string placement = choice switch
        {
            SurveyPlacementChoice.ProjectBase => "in local project coordinates",
            SurveyPlacementChoice.ProjectBaseCreated => "in local project coordinates, against a new project base",
            SurveyPlacementChoice.LocalGrid =>
                "on the file's own local grid (the survey is near the origin, so the project base was not applied)",
            _ => "in real-world coordinates"
        };

        RhinoApp.WriteLine(
            $"MoleHill: read {file.Points.Count:N0} of {file.DataLineCount:N0} rows and drew " +
            $"{figuresDrawn:N0} figures and {spotsDrawn:N0} spot levels {placement} " +
            $"({units.Abbreviation}). No CRS reprojection was applied.");

        if (parsed.IgnoredPointCount > 0)
            RhinoApp.WriteLine($"MoleHill: {parsed.IgnoredPointCount:N0} points matched an Ignore rule and were not drawn.");

        if (unmatchedDrawn > 0)
        {
            string codes = string.Join(
                ", ",
                parsed.UnmatchedCodes.OrderByDescending(pair => pair.Value).Take(10).Select(pair => $"{pair.Key} ({pair.Value})"));
            RhinoApp.WriteLine(
                $"MoleHill: {unmatchedDrawn:N0} points carried codes the table does not know and were drawn as points: {codes}. " +
                "Add them with mhEditFieldCodes and import again.");
        }

        foreach (SurveyReadDiagnostic diagnostic in file.Diagnostics.Take(10))
            RhinoApp.WriteLine($"MoleHill: line {diagnostic.LineNumber} — {diagnostic.Message}");

        if (file.Diagnostics.Count > 10)
            RhinoApp.WriteLine($"MoleHill: {file.Diagnostics.Count - 10:N0} further rows could not be read.");

        foreach (SurveyReadDiagnostic diagnostic in parsed.Diagnostics.Take(10))
            RhinoApp.WriteLine($"MoleHill: line {diagnostic.LineNumber} — {diagnostic.Message}");
    }
}
