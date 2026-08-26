using DialogResult = Eto.Forms.DialogResult;
using FileFilter = Eto.Forms.FileFilter;
using OpenFileDialog = Eto.Forms.OpenFileDialog;
using SaveFileDialog = Eto.Forms.SaveFileDialog;
using MoleHill.Rhino.UI;
using MoleHill.Shared;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;
using MoleHill.Core.Interop;
using MoleHill.Rhino.Model;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

internal static class DocumentCommandService
{
    public static Result RunOrientToOrigin(RhinoDoc doc)
    {
        if (!ModelUnitGuard.TryGet(doc, out _)) return Result.Failure;
        Result resolution = ResolveProjectBase(doc, out _);
        if (resolution != Result.Success)
            return resolution;

        var getBasePoint = new GetPoint();
        getBasePoint.SetCommandPrompt("Select project XY base point (elevation will be preserved)");
        if (getBasePoint.Get() != GetResult.Point)
            return getBasePoint.CommandResult();

        Point3d basePoint = getBasePoint.Point();
        Point3d? xAxisPoint = null;

        var getXAxisPoint = new GetPoint();
        getXAxisPoint.SetCommandPrompt("Select project X-axis reference point in XY or press Enter to keep World X");
        getXAxisPoint.SetBasePoint(basePoint, showDistanceInStatusBar: true);
        getXAxisPoint.DrawLineFromPoint(basePoint, showDistanceInStatusBar: true);
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
        if (!ModelUnitGuard.TryGet(doc, out _)) return Result.Failure;
        Result resolution = ResolveProjectBaseTransform(doc, toProjectCoordinates, out Transform transform);
        if (resolution != Result.Success)
            return resolution;

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

        RhinoApp.WriteLine(
            "MoleHill: cleared the saved project base. Document geometry and legacy CPlanes were not moved or deleted.");
        doc.Views.Redraw();
        return Result.Success;
    }

    public static Result RunImportWithGeoref(RhinoDoc doc)
    {
        if (!ModelUnitGuard.TryGet(doc, out _)) return Result.Failure;
        Result resolution = ResolveProjectBaseTransform(doc, toProjectCoordinates: true, out Transform transform);
        if (resolution != Result.Success)
            return resolution;

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

        GeoreferenceObjectState[] beforeObjects = EnumerateActiveObjects(doc).ToArray();
        var selectedBefore = new HashSet<Guid>(doc.Objects.GetSelectedObjects(false, false).Select(obj => obj.Id));
        bool ran;
        if (usePaste)
        {
            ran = RhinoApp.RunScript(doc.RuntimeSerialNumber, "_Paste", false);
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
            ran = RhinoApp.RunScript(doc.RuntimeSerialNumber, $"_-Import \"{escapedPath}\" _Enter", false);
        }

        if (!ran)
        {
            int removedCount = DeleteObjectsAddedSince(doc, beforeObjects);
            RestoreSelection(doc, selectedBefore);
            if (removedCount > 0)
                RhinoApp.WriteLine($"MoleHill: import did not complete; removed {removedCount} newly added object(s).");
            return Result.Failure;
        }

        GeoreferenceObjectState[] afterObjects = EnumerateActiveObjects(doc).ToArray();
        Guid[] allImportedIds = GeoreferenceImportPlanner.FindNewObjectIds(beforeObjects, afterObjects);
        Guid[] importedIds = GeoreferenceImportPlanner.FindNewModelObjectIds(beforeObjects, afterObjects);

        if (allImportedIds.Length == 0)
        {
            importedIds = doc.Objects
                .GetSelectedObjects(false, false)
                .Where(obj => obj.Attributes.Space == ActiveSpace.ModelSpace && !selectedBefore.Contains(obj.Id))
                .Select(obj => obj.Id)
                .ToArray();
        }

        if (importedIds.Length == 0 && allImportedIds.Length == 0)
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
                .Select(i => getImportedObjects.Object(i)?.Object())
                .Where(obj => obj?.Attributes.Space == ActiveSpace.ModelSpace)
                .Select(obj => obj?.Id ?? Guid.Empty)
                .Where(id => id != Guid.Empty)
                .ToArray();
        }

        if (importedIds.Length == 0)
        {
            if (allImportedIds.Length > 0)
                RhinoApp.WriteLine("MoleHill: imported objects contained no ModelSpace geometry to remap.");
            return Result.Success;
        }

        bool transformed = CommandScriptRunner.RunTransformScript(doc, importedIds, transform, out string? error);
        if (!transformed)
        {
            int removedCount = DeleteObjectsAddedSince(doc, beforeObjects);
            RestoreSelection(doc, selectedBefore);
            RhinoApp.WriteLine(error ?? "MoleHill: imported geometry could not be remapped.");
            RhinoApp.WriteLine(
                removedCount > 0
                    ? $"MoleHill: removed {removedCount} object(s) added by the failed import."
                    : "MoleHill: no newly added objects remained after the failed import.");
            doc.Views.Redraw();
            return Result.Failure;
        }

        doc.Views.Redraw();
        return Result.Success;
    }

    public static Result RunExportWithGeoref(RhinoDoc doc)
    {
        if (!ModelUnitGuard.TryGet(doc, out _)) return Result.Failure;
        Result resolution = ResolveProjectBaseTransform(doc, toProjectCoordinates: false, out Transform transform);
        if (resolution != Result.Success)
            return resolution;

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
            FileName = GetDefaultGeoreferenceExportFileName(doc.Name)
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

            using var options = new FileWriteOptions
            {
                UpdateDocumentPath = false,
                SuppressDialogBoxes = true,
                SuppressAllInput = true,
                WriteSelectedObjectsOnly = true,
                Xform = transform
            };

            bool wrote = doc.WriteFile(dialog.FileName, options);
            if (!wrote)
                RhinoApp.WriteLine($"MoleHill: could not export georeferenced geometry to '{dialog.FileName}'.");
            return wrote ? Result.Success : Result.Failure;
        }
        finally
        {
            doc.Objects.UnselectAll();
            foreach (Guid id in selectedBefore)
                doc.Objects.Select(id, true, true);
        }
    }

    public static Result RunImportLandXml(RhinoDoc doc)
    {
        var dialog = new OpenFileDialog { Title = "Import LandXML Surface" };
        dialog.Filters.Add(new FileFilter("LandXML", ".xml", ".landxml"));
        if (dialog.ShowDialog(RhinoEtoApp.MainWindowForDocument(doc)) != DialogResult.Ok || string.IsNullOrWhiteSpace(dialog.FileName))
            return Result.Cancel;
        return LandXmlSurfaceService.ImportAsTerrain(doc, dialog.FileName, out _);
    }

    public static Result RunExportLandXml(RhinoDoc doc)
    {
        TerrainDefinition? terrain = TerrainController.Instance.GetSelectedTerrain(doc);
        if (terrain == null)
            return Result.Nothing;
        RhinoMesh? mesh = TerrainController.Instance.DuplicateFinalTerrainMesh(doc, terrain.TerrainId);
        if (mesh == null)
        {
            RhinoApp.WriteLine("MoleHill: export requires a completed final terrain build.");
            return Result.Nothing;
        }

        var surface = new TinSurfaceData { Name = terrain.Name };
        Transform exportTransform = Transform.Identity;
        if (ProjectBaseCPlaneService.TryGetTransform(false, doc, out Transform georef, out _))
            exportTransform = georef;
        for (int i = 0; i < mesh.Vertices.Count; i++)
        {
            Point3d point = mesh.Vertices[i];
            point.Transform(exportTransform);
            surface.Points.Add(new TinSurfacePoint(i + 1, point.X, point.Y, point.Z));
        }
        for (int i = 0; i < mesh.Faces.Count; i++)
        {
            MeshFace face = mesh.Faces[i];
            if (face.IsTriangle)
                surface.Triangles.Add(new TinSurfaceTriangle(face.A + 1, face.B + 1, face.C + 1));
            else
            {
                surface.Triangles.Add(new TinSurfaceTriangle(face.A + 1, face.B + 1, face.C + 1));
                surface.Triangles.Add(new TinSurfaceTriangle(face.A + 1, face.C + 1, face.D + 1));
            }
        }

        var dialog = new SaveFileDialog { Title = "Export LandXML Surface", FileName = $"{terrain.Name}.xml" };
        dialog.Filters.Add(new FileFilter("LandXML", ".xml"));
        if (dialog.ShowDialog(RhinoEtoApp.MainWindowForDocument(doc)) != DialogResult.Ok || string.IsNullOrWhiteSpace(dialog.FileName))
            return Result.Cancel;
        if (!LandXmlSurfaceService.TryExport(surface, dialog.FileName, out string? error))
        {
            RhinoApp.WriteLine($"MoleHill: could not export LandXML: {error}");
            return Result.Failure;
        }
        RhinoApp.WriteLine($"MoleHill: exported LandXML surface '{terrain.Name}'.");
        return Result.Success;
    }

    public static Result RunImportGeoTiff(RhinoDoc doc) => RunImportGeoTiff(doc, createTerrain: false);

    public static Result RunImportGeoTiffTerrain(RhinoDoc doc) => RunImportGeoTiff(doc, createTerrain: true);

    private static Result RunImportGeoTiff(RhinoDoc doc, bool createTerrain)
    {
        if (!ModelUnitGuard.TryGet(doc, out ModelUnitContext documentUnits))
            return Result.Failure;

        var imageDialog = new Eto.Forms.OpenFileDialog { Title = "Select GeoTIFF file", MultiSelect = false };
        imageDialog.Filters.Add(new FileFilter("GeoTIFF", ".tif", ".tiff"));
        if (imageDialog.ShowDialog(RhinoEtoApp.MainWindowForDocument(doc)) != DialogResult.Ok ||
            string.IsNullOrWhiteSpace(imageDialog.FileName))
            return Result.Cancel;

        string geotiffPath = imageDialog.FileName;
        using var image = System.Drawing.Image.FromFile(geotiffPath);
        RasterGeoreference georeference;
        string sourceDescription;
        GeoTiffLinearUnit? sourceUnits;
        if (!GeoTiffMetadataReader.TryRead(image, out georeference, out sourceDescription, out sourceUnits))
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
            sourceUnits = null;
        }

        if (!TryResolveRasterUnits(sourceUnits, documentUnits, out double sourceMetersPerUnit, out string unitDescription))
            return Result.Cancel;

        georeference = georeference.ScaleCoordinates(sourceMetersPerUnit / documentUnits.MetersPerModelUnit);

        Result projectBaseResolution = ResolveProjectBase(doc, out bool hasProjectBase);
        if (projectBaseResolution != Result.Success)
            return projectBaseResolution;

        Transform placement = georeference.CreatePictureFrameToWorldTransform(image.Height);
        bool placedInProjectCoordinates = false;
        if (hasProjectBase)
        {
            if (!ProjectBaseCPlaneService.TryGetTransform(
                    toProjectCoordinates: true,
                    doc,
                    out Transform worldToProject,
                    out string? transformError))
            {
                RhinoApp.WriteLine(transformError ?? "MoleHill: the saved project-base transform is invalid.");
                return Result.Failure;
            }

            placement = worldToProject * placement;
            placedInProjectCoordinates = true;
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

        if (!CommandScriptRunner.RunTransformScript(
                doc,
                new[] { pictureId },
                placement,
                out string? placementError,
                out Guid[] placedIds))
        {
            foreach (Guid placedId in placedIds)
            {
                RhinoObject? placedObject = doc.Objects.FindId(placedId);
                if (placedObject != null)
                    doc.Objects.Delete(placedObject, quiet: true, ignoreModes: true);
            }
            RhinoApp.WriteLine(placementError ?? "MoleHill: could not place the GeoTIFF picture frame.");
            return Result.Failure;
        }

        RhinoApp.WriteLine(
            $"MoleHill: imported GeoTIFF using {sourceDescription} " +
            (placedInProjectCoordinates ? "in local project coordinates." : "in real-world coordinates.") +
            $" Source coordinates were interpreted as {unitDescription} and converted to {documentUnits.Abbreviation}." +
            " No CRS reprojection was applied.");
        if (createTerrain)
        {
            List<Guid> pointIds = SampleRasterAsTerrainPoints(doc, image, georeference, hasProjectBase,
                sourceMetersPerUnit / documentUnits.MetersPerModelUnit);
            if (pointIds.Count < 3 || TerrainController.Instance.CreateTerrainFromPointIds(doc, pointIds, Path.GetFileNameWithoutExtension(geotiffPath)) == null)
            {
                RhinoApp.WriteLine("MoleHill: the raster did not produce enough valid elevation samples for a terrain.");
                return Result.Failure;
            }
            RhinoApp.WriteLine($"MoleHill: created a managed DEM terrain from {pointIds.Count:N0} raster samples.");
        }
        doc.Views.Redraw();
        return Result.Success;
    }

    private static List<Guid> SampleRasterAsTerrainPoints(
        RhinoDoc doc,
        System.Drawing.Image image,
        RasterGeoreference georeference,
        bool hasProjectBase,
        double elevationScale)
    {
        using var bitmap = new System.Drawing.Bitmap(image);
        int stride = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((bitmap.Width * (double)bitmap.Height) / 20_000.0)));
        Transform toProject = Transform.Identity;
        if (hasProjectBase && !ProjectBaseCPlaneService.TryGetTransform(true, doc, out toProject, out _))
            return new List<Guid>();

        var ids = new List<Guid>();
        for (int y = 0; y < bitmap.Height; y += stride)
        {
            for (int x = 0; x < bitmap.Width; x += stride)
            {
                System.Drawing.Color color = bitmap.GetPixel(x, y);
                double elevation = color.R * elevationScale;
                if (!double.IsFinite(elevation))
                    continue;
                var mapped = georeference.MapRasterPoint(x + 0.5, y + 0.5);
                var point = new Point3d(mapped.X, mapped.Y, elevation);
                point.Transform(toProject);
                Guid id = doc.Objects.AddPoint(point);
                if (id != Guid.Empty)
                    ids.Add(id);
            }
        }
        return ids;
    }

    private static bool TryResolveRasterUnits(
        GeoTiffLinearUnit? embeddedUnits,
        ModelUnitContext documentUnits,
        out double metersPerUnit,
        out string description)
    {
        if (embeddedUnits is { } detected)
        {
            metersPerUnit = detected.MetersPerUnit;
            description = detected.Name;
            return true;
        }

        var getOption = new GetOption();
        getOption.SetCommandPrompt(
            $"Raster coordinate units are not embedded. Press Enter for document units ({documentUnits.Abbreviation}) or choose source units");
        getOption.AcceptNothing(true);
        int documentOption = getOption.AddOption("DocumentUnits");
        int metresOption = getOption.AddOption("Meters");
        int feetOption = getOption.AddOption("Feet");
        int surveyFeetOption = getOption.AddOption("USSurveyFeet");
        int millimetresOption = getOption.AddOption("Millimeters");

        GetResult result = getOption.Get();
        if (result == GetResult.Nothing)
        {
            metersPerUnit = documentUnits.MetersPerModelUnit;
            description = $"document units ({documentUnits.Abbreviation})";
            return true;
        }

        if (result != GetResult.Option)
        {
            metersPerUnit = 0.0;
            description = string.Empty;
            return false;
        }

        int selected = getOption.OptionIndex();
        if (selected == metresOption)
        {
            metersPerUnit = 1.0;
            description = "metres";
        }
        else if (selected == feetOption)
        {
            metersPerUnit = 0.3048;
            description = "international feet";
        }
        else if (selected == surveyFeetOption)
        {
            metersPerUnit = 1200.0 / 3937.0;
            description = "US survey feet";
        }
        else if (selected == millimetresOption)
        {
            metersPerUnit = 0.001;
            description = "millimetres";
        }
        else if (selected == documentOption)
        {
            metersPerUnit = documentUnits.MetersPerModelUnit;
            description = $"document units ({documentUnits.Abbreviation})";
        }
        else
        {
            metersPerUnit = 0.0;
            description = string.Empty;
            return false;
        }

        return true;
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

    internal static string GetDefaultGeoreferenceExportFileName(string? documentName)
    {
        string? baseName = Path.GetFileNameWithoutExtension(documentName);
        return $"{(string.IsNullOrWhiteSpace(baseName) ? "Export" : baseName)}.3dm";
    }

    private static Result ResolveProjectBaseTransform(
        RhinoDoc doc,
        bool toProjectCoordinates,
        out Transform transform)
    {
        transform = Transform.Identity;
        Result resolution = ResolveProjectBase(doc, out bool hasProjectBase);
        if (resolution != Result.Success)
            return resolution;
        if (!hasProjectBase)
        {
            RhinoApp.WriteLine("MoleHill: no project georef CPlane found.");
            return Result.Nothing;
        }

        if (ProjectBaseCPlaneService.TryGetTransform(
                toProjectCoordinates,
                doc,
                out transform,
                out string? error))
        {
            return Result.Success;
        }

        RhinoApp.WriteLine(error ?? "MoleHill: the saved project-base transform is invalid.");
        return Result.Failure;
    }

    private static Result ResolveProjectBase(RhinoDoc doc, out bool hasProjectBase)
    {
        hasProjectBase = false;
        if (ProjectBaseCPlaneService.HasProjectBasePlane(doc))
        {
            if (ProjectBaseCPlaneService.TryGetProjectBasePlane(doc, out _, out string? error))
            {
                hasProjectBase = true;
                return Result.Success;
            }

            RhinoApp.WriteLine(error ?? "MoleHill: the saved project base is invalid.");
            return Result.Failure;
        }

        if (!ProjectBaseCPlaneService.TryGetLegacyCandidate(doc, out LegacyProjectBaseCandidate legacy))
            return Result.Success;

        string convention = legacy.Convention == LegacyProjectBaseConvention.WorldToLocal
            ? "Python real-world-to-local"
            : "legacy C# local-to-real-world";
        var getOption = new GetOption();
        getOption.SetCommandPrompt(
            $"Found legacy named CPlane '{legacy.Name}' ({convention}). Press Enter to migrate it");
        getOption.AcceptNothing(true);
        int migrateOption = getOption.AddOption("Migrate");
        int ignoreOption = getOption.AddOption("IgnoreOnce");
        int disableOption = getOption.AddOption("DisableLegacy");
        GetResult result = getOption.Get();
        bool migrate = result == GetResult.Nothing ||
                       result == GetResult.Option && getOption.OptionIndex() == migrateOption;
        if (!migrate)
        {
            if (result == GetResult.Option && getOption.OptionIndex() == ignoreOption)
                return Result.Success;
            if (result == GetResult.Option && getOption.OptionIndex() == disableOption)
            {
                ProjectBaseCPlaneService.SuppressLegacyFallback(doc);
                RhinoApp.WriteLine(
                    $"MoleHill: legacy fallback was disabled; named CPlane '{legacy.Name}' was left unchanged.");
                return Result.Success;
            }
            return getOption.CommandResult();
        }

        if (!ProjectBaseCPlaneService.TryMigrateLegacyProjectBase(doc, legacy, out string message))
        {
            RhinoApp.WriteLine($"MoleHill: {message}");
            return Result.Failure;
        }

        RhinoApp.WriteLine($"MoleHill: {message}");
        hasProjectBase = true;
        return Result.Success;
    }

    private static int DeleteObjectsAddedSince(
        RhinoDoc doc,
        IReadOnlyCollection<GeoreferenceObjectState> beforeObjects)
    {
        GeoreferenceObjectState[] currentObjects = EnumerateActiveObjects(doc).ToArray();
        Guid[] newIds = GeoreferenceImportPlanner.FindNewObjectIds(beforeObjects, currentObjects);
        int deleted = 0;
        foreach (Guid id in newIds)
        {
            RhinoObject? obj = doc.Objects.FindId(id);
            if (obj != null && doc.Objects.Delete(obj, quiet: true, ignoreModes: true))
                deleted++;
        }

        return deleted;
    }

    private static void RestoreSelection(RhinoDoc doc, IReadOnlyCollection<Guid> selectedIds)
    {
        doc.Objects.UnselectAll();
        foreach (Guid id in selectedIds)
        {
            if (doc.Objects.FindId(id) != null)
                doc.Objects.Select(id, true, true);
        }
    }

    private static IEnumerable<GeoreferenceObjectState> EnumerateActiveObjects(RhinoDoc doc)
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
            .Where(obj => obj.Id != Guid.Empty)
            .Select(obj => new GeoreferenceObjectState(obj.Id, obj.Attributes.Space));
    }
}
