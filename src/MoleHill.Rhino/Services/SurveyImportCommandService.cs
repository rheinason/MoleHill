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

        FieldCodeTable table = MoleHillRhinoPlugin.Instance.FieldCodeTableStore.Load();
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
                    LayerIndex = EnsureLayer(doc, figure.Layer)
                };

                Guid id = doc.Objects.AddCurve(curve, attributes);
                if (id == Guid.Empty)
                {
                    RollBack(doc, created, undoRecord);
                    RhinoApp.WriteLine("MoleHill: could not create the survey linework; nothing was imported.");
                    return Result.Failure;
                }

                created.Add(id);
                figuresDrawn++;
            }

            int spotsDrawn = AddPoints(doc, points, parsed.SpotPointIndices, SpotLayer(table), created);
            int unmatchedDrawn = AddPoints(doc, points, parsed.UnmatchedPointIndices, table.UnmatchedLayer, created);

            doc.Views.Redraw();
            Report(file, parsed, choice, units, figuresDrawn, spotsDrawn, unmatchedDrawn);
            return Result.Success;
        }
        catch (Exception ex)
        {
            RollBack(doc, created, undoRecord);
            RhinoApp.WriteLine($"MoleHill: the survey import failed and was rolled back: {ex.Message}");
            return Result.Failure;
        }
        finally
        {
            doc.EndUndoRecord(undoRecord);
        }
    }

    private static int AddPoints(
        RhinoDoc doc,
        IReadOnlyList<Point3d> points,
        IReadOnlyList<int> indices,
        string layer,
        List<Guid> created)
    {
        if (indices.Count == 0)
            return 0;

        var attributes = new ObjectAttributes { LayerIndex = EnsureLayer(doc, layer) };
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

    private static string SpotLayer(FieldCodeTable table)
    {
        FieldCodeRule? spotRule = table.Rules.FirstOrDefault(rule => rule.Role == FieldCodeRole.Spot);
        return spotRule != null
            ? FieldCodeTable.ResolveLayer(spotRule)
            : FieldCodeTable.DefaultLayerFor(FieldCodeRole.Spot);
    }

    private static int EnsureLayer(RhinoDoc doc, string layerPath) =>
        LayerCreationService.EnsureLayerPath(doc, layerPath, LayerRoleService.GetTable(doc));

    private static void RollBack(RhinoDoc doc, List<Guid> created, uint undoRecord)
    {
        foreach (Guid id in created)
            doc.Objects.Delete(id, quiet: true);
        created.Clear();
        doc.EndUndoRecord(undoRecord);
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
