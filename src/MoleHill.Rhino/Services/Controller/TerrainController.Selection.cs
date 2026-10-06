using Rhino;
using Rhino.DocObjects;
using Rhino.Input.Custom;

namespace MoleHill.Rhino.Services;

// Document selection helpers for the panel: the selected points, curves, zones, meshes and layers, and
// the interactive source-object editor.
internal sealed partial class TerrainController
{
    public IReadOnlyList<Guid> GetSelectedPointObjectIds(RhinoDoc doc)
    {
        return GetSelectedObjectIds(doc, ObjectType.Point | ObjectType.PointSet);
    }

    public IReadOnlyList<Guid> GetSelectedCurveObjectIds(RhinoDoc doc)
    {
        return GetSelectedObjectIds(doc, ObjectType.Curve);
    }

    public IReadOnlyList<Guid> GetSelectedZoneObjectIds(RhinoDoc doc)
    {
        return GetSelectedObjectIds(doc, ObjectType.Curve | ObjectType.Brep | ObjectType.Extrusion);
    }

    public IReadOnlyList<Guid> GetSelectedMeshObjectIds(RhinoDoc doc)
    {
        return GetSelectedObjectIds(doc, ObjectType.Mesh | ObjectType.Brep | ObjectType.Extrusion);
    }

    public IReadOnlyList<Guid>? EditSourceObjectIds(RhinoDoc doc, IEnumerable<Guid> seedIds, ObjectType objectFilter, string prompt)
    {
        var selectedIds = seedIds
            .Where(id => IsLiveSourceObject(doc, id, objectFilter))
            .Distinct()
            .ToList();
        if (selectedIds.Count == 0)
        {
            selectedIds = GetSelectedObjectIds(doc, objectFilter)
                .Where(id => IsLiveSourceObject(doc, id, objectFilter))
                .Distinct()
                .ToList();
        }

        var activeIds = new HashSet<Guid>(selectedIds);

        SelectSourceObjects(doc, activeIds);

        using var picker = new GetObject
        {
            GroupSelect = true,
            SubObjectSelect = false,
            DeselectAllBeforePostSelect = false
        };
        if (objectFilter != 0)
            picker.GeometryFilter = objectFilter;
        picker.SetCommandPrompt(prompt);
        picker.AcceptNothing(true);
        picker.EnablePostSelect(true);
        picker.EnableUnselectObjectsOnExit(false);
        picker.AlreadySelectedObjectSelect = true;
        // Force Rhino into post-select mode while preserving the seeded highlight.
        picker.EnablePreSelect(false, true);

        RhinoApp.SetFocusToMainWindow(doc);
        while (true)
        {
            var result = picker.GetMultiple(1, -1);
            if (result == global::Rhino.Input.GetResult.Cancel)
            {
                SelectSourceObjects(doc, selectedIds);
                return null;
            }

            if (result == global::Rhino.Input.GetResult.Nothing)
                return activeIds.ToList();

            if (result != global::Rhino.Input.GetResult.Object)
                return null;

            var pickedIds = Enumerable.Range(0, picker.ObjectCount)
                .Select(index => picker.Object(index)?.ObjectId ?? Guid.Empty)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();

            if (pickedIds.Count == 0)
                continue;

            using var _ = new EventSuppression(this);
            foreach (var objectId in pickedIds)
            {
                if (!IsLiveSourceObject(doc, objectId, objectFilter))
                    continue;

                bool shouldSelect = !activeIds.Contains(objectId);
                doc.Objects.Select(objectId, shouldSelect, syncHighlight: true);
                if (shouldSelect)
                    activeIds.Add(objectId);
                else
                    activeIds.Remove(objectId);
            }

            doc.Views.Redraw();
        }
    }

    public IReadOnlyList<string> GetSelectedLayerPaths(RhinoDoc doc)
    {
        if (doc.Layers.GetSelected(out var selectedLayerIndices) && selectedLayerIndices.Count > 0)
        {
            return selectedLayerIndices
                .Where(index => index >= 0 && index < doc.Layers.Count)
                .Select(index => doc.Layers[index].FullPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return doc.Objects
            .GetSelectedObjects(false, false)
            .Select(obj => RhinoDocumentHelpers.GetLayerPath(doc, obj.Attributes.LayerIndex))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
