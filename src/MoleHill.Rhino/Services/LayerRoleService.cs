using MoleHill.Rhino.Model;
using Rhino;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Resolves the layer role table for a document, and caches it.
///
/// Building a table walks every role and every template entry, and the conduit asks for it on every
/// redraw, so it is cached per document and invalidated when the templates change. The cache key is
/// the document's runtime serial number, which is stable for the life of an open document and never
/// reused, so a closed document's entry cannot be handed to its replacement.
/// </summary>
internal static class LayerRoleService
{
    private static readonly object Gate = new();
    private static readonly Dictionary<uint, LayerRoleTable> Cache = new();

    /// <summary>
    /// Where templates come from. The plugin points this at its <see cref="LayerTemplateStore"/> on
    /// load; left unset, every document resolves through the built-in role defaults. Injected rather
    /// than reached through the plugin singleton so role resolution can be exercised without a
    /// running Rhino.
    /// </summary>
    public static Func<IReadOnlyList<LayerTemplateDefinition>>? TemplateProvider { get; set; }

    /// <summary>
    /// The active table for this document. Never null: a document with no template at all resolves
    /// through the built-in role defaults, so output always has somewhere to go.
    /// </summary>
    public static LayerRoleTable GetTable(RhinoDoc? doc)
    {
        if (doc == null)
            return LayerRoleTable.Default;

        lock (Gate)
        {
            if (Cache.TryGetValue(doc.RuntimeSerialNumber, out LayerRoleTable? cached))
                return cached;

            LayerRoleTable table = Build(doc);
            Cache[doc.RuntimeSerialNumber] = table;
            return table;
        }
    }

    /// <summary>Drops the cached table for a document, or for all of them when none is given.</summary>
    public static void Invalidate(RhinoDoc? doc = null)
    {
        lock (Gate)
        {
            if (doc == null)
                Cache.Clear();
            else
                Cache.Remove(doc.RuntimeSerialNumber);
        }
    }

    /// <summary>
    /// Creates the layers a role table declares, without touching any that already exist. Called
    /// before the first build of a document so the Layers panel shows where output is going, and so
    /// preview can read real layer colours rather than falling back to a placeholder grey and then
    /// changing appearance the moment something is baked.
    /// </summary>
    public static void EnsureTemplateLayers(RhinoDoc doc)
    {
        LayerCreationService.ApplyTemplate(doc, GetTable(doc));
    }

    /// <summary>Creates the layer for one role and returns its index.</summary>
    public static int EnsureRoleLayer(RhinoDoc doc, LayerRole role, string? relativeSuffix = null)
    {
        LayerRoleTable table = GetTable(doc);
        return LayerCreationService.EnsureLayerPath(doc, table.Path(role, relativeSuffix), table);
    }

    private static LayerRoleTable Build(RhinoDoc doc)
    {
        try
        {
            var templates = TemplateProvider?.Invoke();
            if (templates is { Count: > 0 })
                return LayerRoleTable.Build(templates[0]);
        }
        catch
        {
            // A broken or unreadable settings file must not stop a terrain from building; the
            // built-in defaults are a complete, working table on their own.
        }

        return LayerRoleTable.Default;
    }
}
