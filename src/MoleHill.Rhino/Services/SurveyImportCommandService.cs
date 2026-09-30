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
/// The import creates ordinary Rhino curves and points on a layer tree named after the file, outside
/// every terrain's own layers, and **never touches a <c>TerrainDefinition</c>** — the user assigns those
/// layers through the normal source editor, exactly as with <c>mhDrapeCurve</c>. Importing a file whose
/// tree already exists either replaces what the earlier import made or starts a new tree beside it, so
/// a revised survey is a re-import rather than a reassignment.
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

        if (!TryChooseDestination(doc, path, out string rootName, out List<Guid> replaceIds))
            return Result.Cancel;

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

        return Create(doc, file, parsed, table, toDocument, choice, units, rootName, replaceIds);
    }

    /// <summary>
    /// Picks the layer tree this import writes to. A file imported before leaves tagged objects behind;
    /// the user then chooses between replacing exactly those and starting a new tree beside them.
    /// Anything the user added to the tree by hand is never tagged, so replacing never touches it.
    /// Replacing keeps the layers, so sources wired by layer keep working; sources wired by object
    /// ID go stale, which the prompt says.
    /// </summary>
    private static bool TryChooseDestination(RhinoDoc doc, string filePath, out string rootName, out List<Guid> replaceIds)
    {
        rootName = SurveyLayerNaming.RootName(filePath);
        replaceIds = new List<Guid>();

        List<Guid> previous = FindImportedObjects(doc, rootName);
        if (previous.Count == 0)
            return true;

        var getOption = new global::Rhino.Input.Custom.GetOption();
        getOption.SetCommandPrompt(
            $"\"{rootName}\" was imported before ({previous.Count:N0} objects). Replace them (terrains wired to those objects by ID lose them), or add a new survey beside them");
        getOption.AcceptNothing(false);
        int replace = getOption.AddOption("Replace");
        int addNew = getOption.AddOption("AddAsNew");
        if (getOption.Get() != global::Rhino.Input.GetResult.Option)
            return false;

        int chosen = getOption.Option()!.Index;
        if (chosen == replace)
        {
            replaceIds = previous;
            return true;
        }

        if (chosen == addNew)
        {
            rootName = SurveyLayerNaming.NextFreeRootName(
                rootName,
                candidate => doc.Layers.FindByFullPath(candidate, -1) >= 0 || FindImportedObjects(doc, candidate).Count > 0);
            return true;
        }

        return false;
    }

    private static List<Guid> FindImportedObjects(RhinoDoc doc, string rootName)
    {
        RhinoObject[]? tagged = doc.Objects.FindByUserString(
            SurveyLayerNaming.ImportUserStringKey, rootName, caseSensitive: false);
        if (tagged == null)
            return new List<Guid>();

        return tagged
            .Where(static obj => obj != null && !obj.IsDeleted)
            .Select(static obj => obj.Id)
            .ToList();
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
        ModelUnitContext units,
        string rootName,
        IReadOnlyList<Guid> replaceIds)
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
                    LayerIndex = EnsureLayer(
                        doc, rootName, figure.Layer, FieldCodeTable.DefaultLayerFor(figure.Role), figure.Role, reportedLayers)
                };
                attributes.SetUserString(SurveyLayerNaming.ImportUserStringKey, rootName);

                Guid id = doc.Objects.AddCurve(curve, attributes);
                if (id == Guid.Empty)
                {
                    RhinoApp.WriteLine("MoleHill: could not create the survey linework; nothing was imported.");
                    return Result.Failure;
                }

                created.Add(id);
                figuresDrawn++;
            }

            int spotsDrawn = AddSpots(doc, rootName, points, parsed, created, reportedLayers);
            int unmatchedDrawn = parsed.UnmatchedPointIndices.Count == 0
                ? 0
                : AddPoints(
                    doc,
                    points,
                    parsed.UnmatchedPointIndices,
                    EnsureLayer(
                        doc, rootName, table.UnmatchedLayer, FieldCodeTable.DefaultUnmatchedLayer, FieldCodeRole.Ignore, reportedLayers),
                    rootName,
                    created);

            // The earlier import goes only once everything new exists, so a failure part-way leaves the
            // survey the user already had rather than half of each.
            int replaced = 0;
            foreach (Guid id in replaceIds)
            {
                if (doc.Objects.Delete(id, quiet: true))
                    replaced++;
            }

            doc.Views.Redraw();
            RhinoApp.WriteLine(replaced > 0
                ? $"MoleHill: survey layers are under \"{rootName}\"; {replaced:N0} objects from the earlier import were replaced."
                : $"MoleHill: survey layers are under \"{rootName}\".");
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
        string rootName,
        List<Guid> created)
    {
        if (indices.Count == 0)
            return 0;

        var attributes = new ObjectAttributes { LayerIndex = layerIndex };
        attributes.SetUserString(SurveyLayerNaming.ImportUserStringKey, rootName);
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
        string rootName,
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
            int layerIndex = EnsureLayer(
                doc, rootName, group.Key, FieldCodeTable.DefaultLayerFor(FieldCodeRole.Spot), FieldCodeRole.Spot, reportedLayers);
            drawn += AddPoints(doc, points, indices, layerIndex, rootName, created);
        }

        return drawn;
    }

    /// <summary>
    /// The layer under the survey root for a typed relative path, or for <paramref name="fallbackPath"/>
    /// when the typed one is unusable.
    ///
    /// An unusable path must never reach layer creation: a blank or rejected name would fall back to
    /// the <i>current</i> layer, scattering survey linework onto whatever the user last clicked. The
    /// fallback is always one of this command's own sublayers, and the substitution is reported once
    /// per path so the user can correct the rule.
    /// </summary>
    private static int EnsureLayer(
        RhinoDoc doc,
        string rootName,
        string relativePath,
        string fallbackPath,
        FieldCodeRole role,
        HashSet<string> reportedLayers)
    {
        if (IsUsableLayerPath(relativePath))
        {
            string fullPath = SurveyLayerNaming.FullPath(rootName, relativePath);
            int index = EnsureSurveyLayerPath(doc, fullPath, role);
            if (index >= 0 && index < doc.Layers.Count &&
                string.Equals(doc.Layers[index].FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        if (reportedLayers.Add(relativePath ?? string.Empty))
        {
            RhinoApp.WriteLine(
                $"MoleHill: \"{relativePath}\" is not a usable layer path; that output went to {fallbackPath} instead. " +
                "Correct it with mhEditFieldCodes.");
        }

        return EnsureSurveyLayerPath(doc, SurveyLayerNaming.FullPath(rootName, fallbackPath), role);
    }

    /// <summary>
    /// Creates a survey layer path segment by segment. Only a layer created here takes the role's
    /// starting colour, and only the leaf does; a layer that already exists stays as the user has it.
    /// </summary>
    private static int EnsureSurveyLayerPath(RhinoDoc doc, string fullPath, FieldCodeRole role)
    {
        int parentIndex = -1;
        string currentPath = string.Empty;

        foreach (string segment in fullPath.Split("::"))
        {
            currentPath = currentPath.Length == 0 ? segment : $"{currentPath}::{segment}";
            int index = doc.Layers.FindByFullPath(currentPath, -1);
            if (index >= 0)
            {
                parentIndex = index;
                continue;
            }

            var layer = new Layer { Name = segment };
            if (parentIndex >= 0)
                layer.ParentLayerId = doc.Layers[parentIndex].Id;

            bool isLeaf = string.Equals(currentPath, fullPath, StringComparison.OrdinalIgnoreCase);
            int? color = isLeaf ? SurveyLayerNaming.DefaultColorArgb(role) : null;
            if (color.HasValue)
            {
                layer.Color = System.Drawing.Color.FromArgb(color.Value);
                layer.PlotColor = layer.Color;
            }

            parentIndex = doc.Layers.Add(layer);
        }

        return parentIndex;
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
