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
}
