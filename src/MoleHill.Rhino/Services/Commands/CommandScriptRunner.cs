using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

internal static class CommandScriptRunner
{
    public static bool RunTransformScript(RhinoDoc doc, IReadOnlyList<Guid> objectIds, Transform transform)
    {
        return RunTransformScript(doc, objectIds, transform, out _, out _);
    }

    public static bool RunTransformScript(
        RhinoDoc doc,
        IReadOnlyList<Guid> objectIds,
        Transform transform,
        out string? error)
    {
        return RunTransformScript(doc, objectIds, transform, out error, out _);
    }

    public static bool RunTransformScript(
        RhinoDoc doc,
        IReadOnlyList<Guid> objectIds,
        Transform transform,
        out string? error,
        out Guid[] transformedIds)
    {
        error = null;
        transformedIds = objectIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (objectIds.Count == 0 || !transform.IsValid || transform.IsIdentity)
            return true;

        if (!transform.TryGetInverse(out Transform inverse))
        {
            error = "The requested transform is not invertible.";
            return false;
        }

        var objects = transformedIds
            .Select(id => doc.Objects.FindId(id))
            .OfType<RhinoObject>()
            .ToList();
        RhinoObject? lockedLayerObject = objects.FirstOrDefault(obj => IsLayerOrParentLocked(doc, obj.Attributes.LayerIndex));
        if (lockedLayerObject != null)
        {
            string layerName = GetLayerPath(doc, lockedLayerObject.Attributes.LayerIndex) ?? "(unknown layer)";
            error = $"Cannot transform object {lockedLayerObject.Id} because layer '{layerName}' is locked. Unlock the layer and retry.";
            return false;
        }

        var selectedBefore = doc.Objects.GetSelectedObjects(false, false).Select(obj => obj.Id).ToHashSet();
        var updated = new List<TransformedObject>(objects.Count);
        try
        {
            foreach (RhinoObject obj in objects)
            {
                bool wasLocked = obj.IsLocked;
                if (wasLocked)
                    doc.Objects.Unlock(obj.Id, ignoreLayerMode: true);

                Guid newId = doc.Objects.Transform(obj.Id, transform, deleteOriginal: true);
                if (newId == Guid.Empty)
                {
                    if (wasLocked)
                        doc.Objects.Lock(obj.Id, ignoreLayerMode: true);

                    error = $"Failed to transform object {obj.Id}.";
                    RollBackTransforms(doc, updated, inverse, ref error);
                    transformedIds = updated.Select(item => item.CurrentId).ToArray();
                    return false;
                }

                if (wasLocked)
                    doc.Objects.Lock(newId, ignoreLayerMode: true);

                updated.Add(new TransformedObject(obj.Id, newId, wasLocked));
            }

            transformedIds = updated.Select(item => item.CurrentId).ToArray();
            return true;
        }
        finally
        {
            doc.Objects.UnselectAll();
            var currentByOriginal = updated.ToDictionary(item => item.OriginalId, item => item.CurrentId);
            foreach (Guid selectedId in selectedBefore)
            {
                Guid currentId = currentByOriginal.TryGetValue(selectedId, out Guid replacementId)
                    ? replacementId
                    : selectedId;
                if (doc.Objects.FindId(currentId) != null)
                    doc.Objects.Select(currentId, true, true);
            }
        }
    }

    private static void RollBackTransforms(
        RhinoDoc doc,
        List<TransformedObject> updated,
        Transform inverse,
        ref string? error)
    {
        bool rollbackFailed = false;
        for (int index = updated.Count - 1; index >= 0; index--)
        {
            TransformedObject item = updated[index];
            if (item.WasLocked)
                doc.Objects.Unlock(item.CurrentId, ignoreLayerMode: true);

            Guid restoredId = doc.Objects.Transform(item.CurrentId, inverse, deleteOriginal: true);
            if (restoredId == Guid.Empty)
            {
                rollbackFailed = true;
                if (item.WasLocked)
                    doc.Objects.Lock(item.CurrentId, ignoreLayerMode: true);
                continue;
            }

            if (item.WasLocked)
                doc.Objects.Lock(restoredId, ignoreLayerMode: true);
            updated[index] = item with { CurrentId = restoredId };
        }

        if (rollbackFailed)
            error = $"{error} Automatic rollback was incomplete; use Undo before continuing.";
    }

    private static bool IsLayerOrParentLocked(RhinoDoc doc, int layerIndex)
    {
        var visited = new HashSet<Guid>();
        while (layerIndex >= 0 && layerIndex < doc.Layers.Count)
        {
            Layer layer = doc.Layers[layerIndex];
            if (!visited.Add(layer.Id))
                break;
            if (layer.IsLocked)
                return true;
            if (layer.ParentLayerId == Guid.Empty)
                break;

            layerIndex = FindLayerIndex(doc, layer.ParentLayerId);
        }

        return false;
    }

    private static int FindLayerIndex(RhinoDoc doc, Guid layerId)
    {
        for (int index = 0; index < doc.Layers.Count; index++)
        {
            if (doc.Layers[index].Id == layerId)
                return index;
        }

        return -1;
    }

    private static string? GetLayerPath(RhinoDoc doc, int layerIndex)
    {
        return layerIndex >= 0 && layerIndex < doc.Layers.Count
            ? doc.Layers[layerIndex].FullPath
            : null;
    }

    private readonly record struct TransformedObject(Guid OriginalId, Guid CurrentId, bool WasLocked);
}
