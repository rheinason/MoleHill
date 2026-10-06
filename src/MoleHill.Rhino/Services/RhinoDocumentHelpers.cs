using Rhino;
using Rhino.DocObjects;

namespace MoleHill.Rhino.Services;

/// <summary>Small RhinoDoc lookups and clean-ups shared by the services and commands.</summary>
internal static class RhinoDocumentHelpers
{
    /// <summary>Full path of the layer at <paramref name="layerIndex"/>, or null when the index is out of range.</summary>
    public static string? GetLayerPath(RhinoDoc doc, int layerIndex)
    {
        return layerIndex >= 0 && layerIndex < doc.Layers.Count
            ? doc.Layers[layerIndex].FullPath
            : null;
    }

    /// <summary>Deletes the listed objects (quietly, ignoring lock and hide modes); ids no longer in the document are skipped.</summary>
    public static void DeleteObjects(RhinoDoc doc, IEnumerable<Guid> objectIds)
    {
        foreach (Guid objectId in objectIds)
        {
            RhinoObject? obj = doc.Objects.FindId(objectId);
            if (obj != null)
                doc.Objects.Delete(obj, quiet: true, ignoreModes: true);
        }
    }
}
