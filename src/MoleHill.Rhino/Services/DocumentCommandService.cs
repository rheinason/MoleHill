using System.Globalization;
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
        getBasePoint.SetCommandPrompt("Select project base point");
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

    public static Result RunApplySavedGeoref(RhinoDoc doc, bool removeGeoref)
    {
        if (!ProjectBaseCPlaneService.TryGetTransform(removeGeoref, doc, out Transform transform))
        {
            RhinoApp.WriteLine("MoleHill: no project georef CPlane found.");
            return Result.Nothing;
        }

        var getObject = new GetObject();
        getObject.SetCommandPrompt(removeGeoref
            ? "Select geometry to convert to local project space"
            : "Select geometry to georeference");
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

    public static Result RunImportWithGeoref(RhinoDoc doc)
    {
        if (!ProjectBaseCPlaneService.TryGetTransform(removeGeoref: true, doc, out Transform transform))
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
        if (!ProjectBaseCPlaneService.TryGetTransform(removeGeoref: false, doc, out Transform transform))
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
        string tfwPath = Path.ChangeExtension(geotiffPath, ".tfw");
        if (!File.Exists(tfwPath))
        {
            var tfwDialog = new Eto.Forms.OpenFileDialog { Title = "Select TFW file", MultiSelect = false };
            tfwDialog.Filters.Add(new FileFilter("World File", ".tfw"));
            if (tfwDialog.ShowDialog(RhinoEtoApp.MainWindowForDocument(doc)) != DialogResult.Ok ||
                string.IsNullOrWhiteSpace(tfwDialog.FileName))
                return Result.Cancel;

            tfwPath = tfwDialog.FileName;
        }

        string[] lines = File.ReadAllLines(tfwPath);
        if (lines.Length != 6)
        {
            RhinoApp.WriteLine("Invalid TFW file.");
            return Result.Failure;
        }

        double xScale = double.Parse(lines[0], CultureInfo.InvariantCulture);
        double yScale = -double.Parse(lines[3], CultureInfo.InvariantCulture);
        double xOrigin = double.Parse(lines[4], CultureInfo.InvariantCulture);
        double yOrigin = double.Parse(lines[5], CultureInfo.InvariantCulture);

        using var image = System.Drawing.Image.FromFile(geotiffPath);
        double worldWidth = image.Width * xScale;
        double worldHeight = image.Height * yScale;

        Plane plane = Plane.WorldXY;
        plane.Origin = new Point3d(xOrigin, yOrigin - worldHeight, 0.0);
        Guid pictureId = doc.Objects.AddPictureFrame(plane, geotiffPath, false, worldWidth, Math.Abs(worldHeight), false, false);
        if (pictureId == Guid.Empty)
            return Result.Failure;

        doc.Views.Redraw();
        return Result.Success;
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
