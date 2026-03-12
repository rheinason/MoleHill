namespace MoleHill.Rhino.Model;

public sealed class SourceReferenceSet
{
    public List<Guid> ObjectIds { get; set; } = new();

    public List<string> LayerPaths { get; set; } = new();

    public bool HasReferences => ObjectIds.Count > 0 || LayerPaths.Count > 0;

    public void ReplaceObjects(IEnumerable<Guid> ids)
    {
        ObjectIds = ids
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
    }

    public void ReplaceLayers(IEnumerable<string> layerPaths)
    {
        LayerPaths = layerPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void RemoveObjects(IEnumerable<Guid> ids)
    {
        var toRemove = new HashSet<Guid>(ids);
        ObjectIds.RemoveAll(id => toRemove.Contains(id));
    }

    public void RemoveLayer(string layerPath)
    {
        LayerPaths.RemoveAll(p => string.Equals(p, layerPath, StringComparison.OrdinalIgnoreCase));
    }

    public void AddObjects(IEnumerable<Guid> ids)
    {
        var existing = new HashSet<Guid>(ObjectIds);
        foreach (var id in ids)
            if (id != Guid.Empty && existing.Add(id))
                ObjectIds.Add(id);
    }

    public void AddLayer(string layerPath)
    {
        if (string.IsNullOrWhiteSpace(layerPath))
            return;
        layerPath = layerPath.Trim();
        if (!LayerPaths.Any(p => string.Equals(p, layerPath, StringComparison.OrdinalIgnoreCase)))
            LayerPaths.Add(layerPath);
    }

    public void AddLayers(IEnumerable<string> layerPaths)
    {
        foreach (var path in layerPaths)
            AddLayer(path);
    }

    public bool RetainObjects(Func<Guid, bool> keepObject)
    {
        var retained = ObjectIds
            .Where(id => id != Guid.Empty && keepObject(id))
            .Distinct()
            .ToList();

        bool changed = retained.Count != ObjectIds.Count;
        if (!changed)
        {
            for (int index = 0; index < retained.Count; index++)
            {
                if (retained[index] == ObjectIds[index])
                    continue;

                changed = true;
                break;
            }
        }

        if (changed)
            ObjectIds = retained;

        return changed;
    }

    public bool ReplaceObject(Guid oldId, Guid newId)
    {
        if (oldId == Guid.Empty || newId == Guid.Empty)
            return false;

        bool changed = false;
        var replaced = new List<Guid>(ObjectIds.Count);
        var seen = new HashSet<Guid>();

        foreach (var objectId in ObjectIds)
        {
            var targetId = objectId == oldId ? newId : objectId;
            if (targetId != objectId)
                changed = true;

            if (targetId == Guid.Empty || !seen.Add(targetId))
            {
                if (targetId != Guid.Empty)
                    changed = true;
                continue;
            }

            replaced.Add(targetId);
        }

        if (changed)
            ObjectIds = replaced;

        return changed;
    }
}
