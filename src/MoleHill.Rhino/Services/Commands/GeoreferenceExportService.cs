using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

/// <summary>Selected-geometry export and clipboard transfer in real-world coordinates.</summary>
internal static class GeoreferenceExportService
{
    public static bool WriteSelected(
        RhinoDoc doc, IEnumerable<Guid> ids, Transform transform, string path, RunMode mode)
    {
        Guid[] selectedBefore = SelectedIds(doc);
        try
        {
            doc.Objects.UnselectAll();
            Guid[] exportIds = ids.Distinct().ToArray();
            if (exportIds.Length == 0 || exportIds.Any(id => !doc.Objects.Select(id, true, true)))
                return false;

            using var options = new FileWriteOptions
            {
                UpdateDocumentPath = false,
                WriteSelectedObjectsOnly = true,
                // GeometryOnly also removes names, layers and units. Keep object attributes and
                // prune document content from the selected 3dm below instead.
                WriteGeometryOnly = false,
                WriteUserData = false,
                IncludeBitmapTable = false,
                IncludeHistory = false,
                IncludePreviewImage = false,
                IncludeRenderMeshes = false,
                SuppressDialogBoxes = mode == RunMode.Scripted,
                SuppressAllInput = mode == RunMode.Scripted,
                Xform = transform
            };
            if (!string.Equals(Path.GetExtension(path), ".3dm", StringComparison.OrdinalIgnoreCase))
                return doc.WriteFile(path, options);

            // Rhino filters block/layer definitions to the selection. Remove document content too,
            // preserving the selected objects' attributes and the model's measurement settings.
            string scratch = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!,
                $".molehill-{Guid.NewGuid():N}.3dm");
            string ready = scratch + ".ready";
            try
            {
                if (!doc.WriteFile(scratch, options))
                    return false;
                using File3dm? model = File3dm.Read(scratch);
                if (model == null)
                    return false;
                model.AllNamedConstructionPlanes.Clear();
                model.AllNamedViews.Clear();
                model.AllViews.Clear();
                model.Strings.Delete(null, null);
                model.PlugInData?.Clear();
                model.Notes.Notes = string.Empty;
                model.Notes.IsVisible = false;
                model.Settings.ModelBasepoint = Point3d.Origin;
                model.Settings.ModelUrl = string.Empty;
                var fileOptions = new File3dmWriteOptions
                {
                    Version = model.ArchiveVersion / 10,
                    SaveUserData = false
                };
                if (!model.Write(ready, fileOptions))
                    return false;
                File.Move(ready, path, overwrite: true);
                return true;
            }
            finally
            {
                File.Delete(scratch);
                File.Delete(ready);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RhinoApp.WriteLine($"MoleHill: export failed: {ex.Message}");
            return false;
        }
        finally
        {
            RestoreSelection(doc, selectedBefore);
        }
    }

    public static bool CopySelected(RhinoDoc doc, IEnumerable<Guid> ids, Transform transform, out string? error)
    {
        error = null;
        Guid[] selectedBefore = SelectedIds(doc);
        bool wasModified = doc.Modified;
        var copies = new List<Guid>();
        try
        {
            doc.Objects.UnselectAll();
            foreach (Guid id in ids.Distinct())
            {
                RhinoObject? source = doc.Objects.FindId(id);
                using GeometryBase? geometry = source?.Geometry.Duplicate();
                if (source == null || geometry == null || !geometry.Transform(transform))
                {
                    error = $"MoleHill: could not copy object {id} at real-world coordinates.";
                    return false;
                }

                using ObjectAttributes attributes = source.Attributes.Duplicate();
                attributes.ObjectId = Guid.Empty;
                // Copies are temporary; their original layer and appearance still belong on the clipboard.
                attributes.Mode = ObjectMode.Normal;
                Guid copy = doc.Objects.Add(geometry, attributes);
                if (copy == Guid.Empty)
                    return false;
                copies.Add(copy);
                if (!doc.Objects.Select(copy, true, true))
                    return false;
            }

            // Use Rhino's clipboard protocol so ordinary Paste preserves coordinates, units, blocks
            // and attributes. ScriptRunner on the calling command makes this complete before cleanup.
            return copies.Count > 0 && RhinoApp.RunScript(doc.RuntimeSerialNumber, "_CopyToClipboard", false);
        }
        finally
        {
            foreach (Guid id in copies)
            {
                RhinoObject? copy = doc.Objects.FindId(id);
                if (copy != null)
                    doc.Objects.Purge(copy);
            }
            RestoreSelection(doc, selectedBefore);
            doc.Modified = wasModified;
        }
    }

    private static Guid[] SelectedIds(RhinoDoc doc) =>
        doc.Objects.GetSelectedObjects(false, false).Select(obj => obj.Id).ToArray();

    private static void RestoreSelection(RhinoDoc doc, IEnumerable<Guid> ids)
    {
        doc.Objects.UnselectAll();
        foreach (Guid id in ids)
            doc.Objects.Select(id, true, true);
    }
}
