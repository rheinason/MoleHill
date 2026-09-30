using MoleHill.Rhino.Model;
using Rhino;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Resolves the layer role table a terrain routes and styles by, and caches it.
///
/// The document's own embedded copy is authoritative. That is what makes a .3dm look the same on
/// another machine: templates are otherwise a per-user setting, and resolving against whatever
/// templates the opener happens to have would restyle their drawing. The per-user store is the
/// standard new documents are seeded from, and the two are reconciled only by an explicit action.
///
/// Building a table walks every role and every entry, and the conduit asks for one on every redraw,
/// so it is cached per document, template and terrain name — the template's {terrain} token
/// resolves to the name — and dropped when any of them changes.
/// </summary>
internal static class LayerRoleService
{
    private static readonly object Gate = new();
    private static readonly Dictionary<(uint Document, string Template, string? Terrain), LayerRoleTable> Cache = new();
    private static readonly HashSet<(uint Document, string Template, string? Terrain)> LayersEnsured = new();

    /// <summary>
    /// Where the machine-local templates come from. The plugin points this at its
    /// <see cref="LayerTemplateStore"/> on load; left unset, documents with no embedded copy resolve
    /// through the built-in role defaults. Injected rather than reached through the plugin singleton
    /// so role resolution can be exercised without a running Rhino.
    /// </summary>
    public static Func<IReadOnlyList<LayerTemplateDefinition>>? TemplateProvider { get; set; }

    /// <summary>Writes the machine-local templates back. Set alongside <see cref="TemplateProvider"/>.</summary>
    public static Action<IReadOnlyList<LayerTemplateDefinition>>? TemplateWriter { get; set; }

    /// <summary>
    /// The table for a terrain. Never null: a document with no template at all resolves through the
    /// built-in role defaults, so output always has somewhere to go.
    /// </summary>
    public static LayerRoleTable GetTable(RhinoDoc? doc, TerrainDefinition? terrain = null)
    {
        if (doc == null)
            return LayerRoleTable.Default;

        return GetTable(doc, terrain?.LayerTemplateName ?? string.Empty, terrain?.Name);
    }

    /// <summary>
    /// The table a template gives a terrain of the given name, without needing the terrain itself.
    /// A rename asks for both the old name's table and the new one's to work out which layers move.
    /// </summary>
    public static LayerRoleTable GetTable(RhinoDoc doc, string templateName, string? terrainName)
    {
        lock (Gate)
        {
            var key = (doc.RuntimeSerialNumber, templateName, terrainName);
            if (Cache.TryGetValue(key, out LayerRoleTable? cached))
                return cached;

            LayerRoleTable table = Build(doc, templateName, terrainName);
            Cache[key] = table;
            return table;
        }
    }

    /// <summary>Drops cached tables for a document, or for all of them when none is given.</summary>
    public static void Invalidate(RhinoDoc? doc = null)
    {
        lock (Gate)
        {
            if (doc == null)
            {
                Cache.Clear();
                LayersEnsured.Clear();
                return;
            }

            foreach (var key in Cache.Keys.Where(key => key.Document == doc.RuntimeSerialNumber).ToList())
                Cache.Remove(key);

            LayersEnsured.RemoveWhere(key => key.Document == doc.RuntimeSerialNumber);
        }
    }

    /// <summary>
    /// Gives the document its own copy of the template it is using, if it has none, and creates the
    /// layers that template declares.
    ///
    /// Runs on the document thread at the start of a build — the only place layers may be created.
    /// Creating them up front is what lets preview read real layer colours, so nothing shifts
    /// appearance the first time it is baked.
    /// </summary>
    public static void EnsureTemplateLayers(RhinoDoc doc, TerrainDefinition? terrain = null)
    {
        EmbedLocalTemplateIfMissing(doc);

        lock (Gate)
        {
            // Once per document and template per session. The work is idempotent, but a build should
            // not walk the whole layer table every time it runs. Keyed by template too, so a second
            // terrain naming a different template still gets its layers before it previews.
            if (!LayersEnsured.Add((doc.RuntimeSerialNumber, terrain?.LayerTemplateName ?? string.Empty, terrain?.Name)))
                return;
        }

        LayerCreationService.ApplyTemplate(doc, GetTable(doc, terrain));
    }

    /// <summary>Creates the layer for one role and returns its index.</summary>
    public static int EnsureRoleLayer(
        RhinoDoc doc,
        LayerRole role,
        string? relativeSuffix = null,
        TerrainDefinition? terrain = null)
    {
        LayerRoleTable table = GetTable(doc, terrain);
        return LayerCreationService.EnsureLayerPath(doc, table.Path(role, relativeSuffix), table);
    }

    /// <summary>
    /// True when the document's copy of a template no longer matches the machine-local one of the
    /// same name — the two have been edited apart, and only the user can say which should win.
    /// </summary>
    public static bool DivergesFromLocal(RhinoDoc doc, string? templateName = null)
    {
        EmbeddedLayerTemplateState? state = LayerTemplateDocumentStore.Load(doc);
        LayerTemplateDefinition? embedded = LayerTemplateDocumentStore.Find(state, templateName);
        if (embedded == null)
            return false;

        LayerTemplateDefinition? local = FindLocalTemplate(embedded.Name);
        if (local == null)
            return false;

        return LayerRoleTable.Build(local).Fingerprint != LayerRoleTable.Build(embedded).Fingerprint;
    }

    /// <summary>Replaces the document's copy with the machine-local template of the same name.</summary>
    public static bool PullFromLocal(RhinoDoc doc, string templateName)
    {
        LayerTemplateDefinition? local = FindLocalTemplate(templateName);
        if (local == null)
            return false;

        LayerTemplateDocumentStore.Embed(doc, local, LayerRoleTable.Build(local).Fingerprint, makeActive: true);
        Invalidate(doc);
        return true;
    }

    /// <summary>
    /// Carries a template-editor save into the document it was opened from: every embedded copy
    /// whose machine-local template of the same name was just saved differently is replaced by it.
    ///
    /// Without this an edit never reaches a document that already carries a copy, because the copy
    /// wins. Only the document the editor was opened on is updated — the edit is an explicit action
    /// there — so any other document still renders as saved wherever it is opened. Which template is
    /// active is left alone, and a template the editor no longer has keeps its embedded copy.
    /// </summary>
    /// <returns>How many embedded copies were replaced.</returns>
    public static int PullEditedFromLocal(RhinoDoc doc, IReadOnlyList<LayerTemplateDefinition> saved)
    {
        EmbeddedLayerTemplateState? state = LayerTemplateDocumentStore.Load(doc);
        if (state == null)
            return 0;

        int replaced = 0;
        foreach (EmbeddedLayerTemplate embedded in state.Templates.ToList())
        {
            LayerTemplateDefinition? local = saved.FirstOrDefault(item =>
                string.Equals(item.Name, embedded.Template.Name, StringComparison.OrdinalIgnoreCase));
            if (local == null)
                continue;

            ulong fingerprint = LayerRoleTable.Build(local).Fingerprint;
            if (fingerprint == LayerRoleTable.Build(embedded.Template).Fingerprint)
                continue;

            LayerTemplateDocumentStore.Embed(doc, local.Copy(), fingerprint, makeActive: false);
            replaced++;
        }

        if (replaced > 0)
            Invalidate(doc);

        return replaced;
    }

    /// <summary>Writes the document's copy back over the machine-local template of the same name.</summary>
    public static bool PushToLocal(RhinoDoc doc, string templateName)
    {
        EmbeddedLayerTemplateState? state = LayerTemplateDocumentStore.Load(doc);
        LayerTemplateDefinition? embedded = LayerTemplateDocumentStore.Find(state, templateName);
        if (embedded == null || TemplateProvider == null || TemplateWriter == null)
            return false;

        var templates = TemplateProvider().ToList();
        templates.RemoveAll(item => string.Equals(item.Name, embedded.Name, StringComparison.OrdinalIgnoreCase));
        templates.Add(embedded);

        TemplateWriter(templates);
        LayerTemplateDocumentStore.Embed(doc, embedded, LayerRoleTable.Build(embedded).Fingerprint, makeActive: true);
        Invalidate(doc);
        return true;
    }

    private static void EmbedLocalTemplateIfMissing(RhinoDoc doc)
    {
        if (LayerTemplateDocumentStore.Load(doc) != null)
            return;

        LayerTemplateDefinition? local = FindLocalTemplate(null);
        if (local == null)
            return;

        LayerTemplateDocumentStore.Embed(doc, local, LayerRoleTable.Build(local).Fingerprint, makeActive: true);
        Invalidate(doc);
    }

    private static LayerTemplateDefinition? FindLocalTemplate(string? name)
    {
        try
        {
            var templates = TemplateProvider?.Invoke();
            if (templates is not { Count: > 0 })
                return null;

            if (string.IsNullOrWhiteSpace(name))
                return templates[0];

            return templates.FirstOrDefault(item =>
                string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            // A broken or unreadable settings file must not stop a terrain from building.
            return null;
        }
    }

    private static LayerRoleTable Build(RhinoDoc doc, string templateName, string? terrainName)
    {
        // The document's own copy wins, so a drawing renders the same wherever it is opened.
        EmbeddedLayerTemplateState? state = LayerTemplateDocumentStore.Load(doc);
        LayerTemplateDefinition? template = LayerTemplateDocumentStore.Find(state, templateName);

        template ??= FindLocalTemplate(string.IsNullOrWhiteSpace(templateName) ? null : templateName);

        return LayerRoleTable.Build(template, terrainName);
    }
}
