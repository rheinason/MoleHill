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

        var selectedBefore = doc.Objects.GetSelectedObjects(false, false).Select(obj => obj.Id).ToArray();
        var lockedObjects = new List<Guid>();
        var transformedOriginalIds = new HashSet<Guid>();
        var transformedSelectionIds = new List<Guid>();
        var updatedIds = new List<Guid>(transformedIds.Length);
        try
        {
            foreach (Guid id in transformedIds)
            {
                var obj = doc.Objects.FindId(id);
                if (obj == null)
                    continue;

                transformedOriginalIds.Add(id);
                bool wasLocked = obj.IsLocked;
                if (wasLocked)
                    doc.Objects.Unlock(id, ignoreLayerMode: true);

                Guid newId = doc.Objects.Transform(id, transform, deleteOriginal: true);
                if (newId == Guid.Empty)
                {
                    error = $"Failed to transform object {id}.";
                    return false;
                }

                updatedIds.Add(newId);
                if (wasLocked)
                    lockedObjects.Add(newId);

                if (selectedBefore.Contains(id))
                    transformedSelectionIds.Add(newId);
            }

            transformedIds = updatedIds.ToArray();
            return true;
        }
        finally
        {
            doc.Objects.UnselectAll();
            foreach (Guid id in lockedObjects)
                doc.Objects.Lock(id, ignoreLayerMode: true);

            foreach (Guid id in selectedBefore.Where(id => !transformedOriginalIds.Contains(id)))
                doc.Objects.Select(id, true, true);

            foreach (Guid id in transformedSelectionIds)
                doc.Objects.Select(id, true, true);
        }
    }
}
