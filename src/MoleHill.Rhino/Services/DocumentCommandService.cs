using DialogResult = Eto.Forms.DialogResult;
using FileFilter = Eto.Forms.FileFilter;
using OpenFileDialog = Eto.Forms.OpenFileDialog;
using SaveFileDialog = Eto.Forms.SaveFileDialog;
using MoleHill.Rhino.UI;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;

namespace MoleHill.Rhino.Services;

internal static class DocumentCommandService
{
    public static Result RunOrientToOrigin(RhinoDoc doc)
    {
        var getBasePoint = new GetPoint();
        getBasePoint.SetCommandPrompt("Select project XY base point (elevation will be preserved)");
        if (getBasePoint.Get() != GetResult.Point)
            return getBasePoint.CommandResult();

        Point3d basePoint = getBasePoint.Point();
        Point3d? xAxisPoint = null;

        var getXAxisPoint = new GetPoint();
        getXAxisPoint.SetCommandPrompt("Select X-axis reference point or press Enter to skip");
        getXAxisPoint.AcceptNothing(true);
        switch (getXAxisPoint.Get())
        {
            case GetResult.Point:
                xAxisPoint = getXAxisPoint.Point();
                break;
            case GetResult.Nothing:
                break;
            default:
                return getXAxisPoint.CommandResult();
        }

        Result result = ProjectBaseCPlaneService.OrientDocument(doc, basePoint, xAxisPoint, out string message);
        if (!string.IsNullOrWhiteSpace(message))
            RhinoApp.WriteLine(message);
        return result;
    }

    public static Result RunApplySavedGeoref(RhinoDoc doc, bool toProjectCoordinates)
    {
        if (!ProjectBaseCPlaneService.TryGetTransform(toProjectCoordinates, doc, out Transform transform))
        {
            RhinoApp.WriteLine("MoleHill: no project georef CPlane found.");
            return Result.Nothing;
        }

        var getObject = new GetObject();
        getObject.SetCommandPrompt(toProjectCoordinates
            ? "Select geometry to convert to local project space"
            : "Select geometry to convert to real-world coordinates");
        getObject.EnablePreSelect(true, true);
        getObject.SubObjectSelect = false;
        getObject.GroupSelect = true;
        getObject.GetMultiple(1, 0);
        if (getObject.CommandResult() != Result.Success)
            return getObject.CommandResult();

        Guid[] objectIds = Enumerable.Range(0, getObject.ObjectCount)
            .Select(i => getObject.Object(i)?.ObjectId ?? Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToArray();
        if (objectIds.Length == 0)
            return Result.Nothing;

        bool ran = CommandScriptRunner.RunTransformScript(doc, objectIds, transform, out string? error);
        if (!ran && !string.IsNullOrWhiteSpace(error))
            RhinoApp.WriteLine(error);
        return ran ? Result.Success : Result.Failure;
    }

    public static Result RunClearProjectBase(RhinoDoc doc)
    {
        if (!ProjectBaseCPlaneService.ClearProjectBasePlane(doc))
        {
            RhinoApp.WriteLine("MoleHill: no saved project base was found.");
            return Result.Nothing;
        }

        RhinoApp.WriteLine("MoleHill: cleared the saved project base. Document geometry was not moved.");
        doc.Views.Redraw();
        return Result.Success;
    }

    public static Result RunImportWithGeoref(RhinoDoc doc)
    {
        if (!ProjectBaseCPlaneService.TryGetTransform(toProjectCoordinates: true, doc, out Transform transform))
        {
            RhinoApp.WriteLine("MoleHill: no project georef CPlane found.");
            return Result.Nothing;
        }

        var getOption = new GetOption();
        getOption.SetCommandPrompt("Import geometry with georef");
        int pasteOption = getOption.AddOption("Paste");
        int fileOption = getOption.AddOption("File");
        GetResult getResult = getOption.Get();
        if (getResult != GetResult.Option)
            return getOption.CommandResult();

        bool usePaste = getOption.OptionIndex() == pasteOption;
        bool useFile = getOption.OptionIndex() == fileOption;
        if (!usePaste && !useFile)
            return Result.Cancel;

        var beforeIds = new HashSet<Guid>(EnumerateActiveObjectIds(doc));
        var selectedBefore = new HashSet<Guid>(doc.Objects.GetSelectedObjects(false, false).Select(obj => obj.Id));
        bool ran;
        if (usePaste)
        {
            ran = RhinoApp.RunScript("Paste", false);
        }
        else
        {
            var dialog = new Eto.Forms.OpenFileDialog
            {
                Title = "Import Georeferenced Rhino File",
                MultiSelect = false
            };
            dialog.Filters.Add(new FileFilter("Rhino 3D", ".3dm"));
            dialog.Filters.Add(new FileFilter("All Files", ".*"));
            if (dialog.ShowDialog(RhinoEtoApp.MainWindowForDocument(doc)) != DialogResult.Ok ||
                string.IsNullOrWhiteSpace(dialog.FileName))
                return Result.Cancel;

            string escapedPath = dialog.FileName.Replace("\"", "\"\"");
            ran = RhinoApp.RunScript($"_-Import \"{escapedPath}\" _Enter", false);
        }

        if (!ran)
            return Result.Failure;

        Guid[] importedIds = doc.Objects
            .GetSelectedObjects(false, false)
            .Select(obj => obj.Id)
            .Where(id => id != Guid.Empty && !selectedBefore.Contains(id))
            .ToArray();

        if (importedIds.Length == 0)
        {
            importedIds = EnumerateActiveObjectIds(doc)
                .Where(id => !beforeIds.Contains(id))
                .ToArray();
        }

        if (importedIds.Length == 0)
        {
            var getImportedObjects = new GetObject();
            getImportedObjects.SetCommandPrompt("Select imported objects to remap");
            getImportedObjects.EnablePreSelect(true, true);
            getImportedObjects.SubObjectSelect = false;
            getImportedObjects.GroupSelect = true;
            getImportedObjects.GetMultiple(1, 0);
            if (getImportedObjects.CommandResult() != Result.Success)
                return getImportedObjects.CommandResult();

            importedIds = Enumerable.Range(0, getImportedObjects.ObjectCount)
                .Select(i => getImportedObjects.Object(i)?.ObjectId ?? Guid.Empty)
                .Where(id => id != Guid.Empty)
                .ToArray();
        }

        if (importedIds.Length == 0)
            return Result.Success;

        bool transformed = CommandScriptRunner.RunTransformScript(doc, importedIds, transform, out string? error);
        if (!transformed && !string.IsNullOrWhiteSpace(error))
            RhinoApp.WriteLine(error);

        return transformed ? Result.Success : Result.Failure;
    }

    public static Result RunExportWithGeoref(RhinoDoc doc)
    {
        if (!ProjectBaseCPlaneService.TryGetTransform(toProjectCoordinates: false, doc, out Transform transform))
        {
            RhinoApp.WriteLine("MoleHill: no project georef CPlane found.");
            return Result.Nothing;
        }

        var getObject = new GetObject();
        getObject.SetCommandPrompt("Select geometry to export with georef");
        getObject.EnablePreSelect(true, true);
        getObject.SubObjectSelect = false;
        getObject.GroupSelect = true;
        getObject.GetMultiple(1, 0);
        if (getObject.CommandResult() != Result.Success)
            return getObject.CommandResult();

        var dialog = new Eto.Forms.SaveFileDialog
        {
            Title = "Export Georeferenced Rhino File",
            FileName = $"{doc.Name ?? "Export"}.3dm"
        };
        dialog.Filters.Add(new FileFilter("Rhino 3D", ".3dm"));
        if (dialog.ShowDialog(RhinoEtoApp.MainWindowForDocument(doc)) != DialogResult.Ok ||
            string.IsNullOrWhiteSpace(dialog.FileName))
            return Result.Cancel;

        var selectedIds = Enumerable.Range(0, getObject.ObjectCount)
            .Select(i => getObject.Object(i)?.ObjectId ?? Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToHashSet();

        var selectedBefore = new HashSet<Guid>(doc.Objects.GetSelectedObjects(false, false).Select(obj => obj.Id));
        try
        {
            doc.Objects.UnselectAll();
            foreach (Guid id in selectedIds)
                doc.Objects.Select(id, true, true);

            var options = new FileWriteOptions
            {
                SuppressDialogBoxes = true,
                WriteSelectedObjectsOnly = true,
                Xform = transform
            };

            bool wrote = doc.WriteFile(dialog.FileName, options);
            return wrote ? Result.Success : Result.Failure;
        }
        finally
        {
            doc.Objects.UnselectAll();
            foreach (Guid id in selectedBefore)
                doc.Objects.Select(id, true, true);
        }
    }

    public static Result RunImportGeoTiff(RhinoDoc doc)
    {
        var imageDialog = new Eto.Forms.OpenFileDialog { Title = "Select GeoTIFF file", MultiSelect = false };
        imageDialog.Filters.Add(new FileFilter("GeoTIFF", ".tif", ".tiff"));
        if (imageDialog.ShowDialog(RhinoEtoApp.MainWindowForDocument(doc)) != DialogResult.Ok ||
            string.IsNullOrWhiteSpace(imageDialog.FileName))
            return Result.Cancel;

        string geotiffPath = imageDialog.FileName;
        using var image = System.Drawing.Image.FromFile(geotiffPath);
        RasterGeoreference georeference;
        string sourceDescription;
        if (!GeoTiffMetadataReader.TryRead(image, out georeference, out sourceDescription))
        {
            string? worldFilePath = FindWorldFile(geotiffPath);
            if (worldFilePath == null)
            {
                var worldFileDialog = new Eto.Forms.OpenFileDialog
                {
                    Title = "GeoTIFF has no embedded placement; select a world file",
                    MultiSelect = false
                };
                worldFileDialog.Filters.Add(new FileFilter("World File", ".tfw", ".tifw", ".wld"));
                if (worldFileDialog.ShowDialog(RhinoEtoApp.MainWindowForDocument(doc)) != DialogResult.Ok ||
                    string.IsNullOrWhiteSpace(worldFileDialog.FileName))
                    return Result.Cancel;

                worldFilePath = worldFileDialog.FileName;
            }

            if (!RasterGeoreference.TryReadWorldFile(worldFilePath, out georeference, out string? worldFileError))
            {
                RhinoApp.WriteLine($"MoleHill: {worldFileError ?? "Invalid world file."}");
                return Result.Failure;
            }

            sourceDescription = $"world file '{Path.GetFileName(worldFilePath)}'";
        }

        Plane plane = Plane.WorldXY;
        Guid pictureId = doc.Objects.AddPictureFrame(
            plane,
            geotiffPath,
            false,
            image.Width,
            image.Height,
            false,
            false);
        if (pictureId == Guid.Empty)
            return Result.Failure;

        Transform placement = georeference.CreatePictureFrameToWorldTransform(image.Height);
        bool placedInProjectCoordinates = ProjectBaseCPlaneService.TryGetTransform(
            toProjectCoordinates: true,
            doc,
            out Transform worldToProject);
        if (placedInProjectCoordinates)
            placement = worldToProject * placement;

        if (!CommandScriptRunner.RunTransformScript(
                doc,
                new[] { pictureId },
                placement,
                out string? placementError,
                out Guid[] placedIds))
        {
            doc.Objects.Delete(placedIds, quiet: true);
            RhinoApp.WriteLine(placementError ?? "MoleHill: could not place the GeoTIFF picture frame.");
            return Result.Failure;
        }

        RhinoApp.WriteLine(
            $"MoleHill: imported GeoTIFF using {sourceDescription} " +
            (placedInProjectCoordinates ? "in local project coordinates." : "in real-world coordinates.") +
            " Coordinate values were used directly; no CRS reprojection was applied.");
        doc.Views.Redraw();
        return Result.Success;
    }

    private static string? FindWorldFile(string geotiffPath)
    {
        string directory = Path.GetDirectoryName(geotiffPath) ?? string.Empty;
        string stem = Path.GetFileNameWithoutExtension(geotiffPath);
        foreach (string extension in new[] { ".tfw", ".tifw", ".wld" })
        {
            string candidate = Path.Combine(directory, stem + extension);
            if (File.Exists(candidate))
                return candidate;

            string upperCandidate = Path.Combine(directory, stem + extension.ToUpperInvariant());
            if (File.Exists(upperCandidate))
                return upperCandidate;
        }

        return null;
    }

    private static IEnumerable<Guid> EnumerateActiveObjectIds(RhinoDoc doc)
    {
        return doc.Objects
            .GetObjectList(new ObjectEnumeratorSettings
            {
                ActiveObjects = true,
                DeletedObjects = false,
                HiddenObjects = true,
                LockedObjects = true,
                NormalObjects = true,
                ReferenceObjects = false
            })
            .Select(obj => obj.Id)
            .Where(id => id != Guid.Empty);
    }
}
