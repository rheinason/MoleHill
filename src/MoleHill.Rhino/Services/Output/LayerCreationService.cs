using Rhino;
using Rhino.DocObjects;

namespace MoleHill.Rhino.Services;

/// <summary>
/// The one place layers are created in the document.
///
/// Everything that writes MoleHill output goes through here — the build pipeline, applying a layer
/// template, and interactive commands like the curve inspector's labelling — so a layer looks the
/// same however it came to exist. There used to be two implementations with opposite rules about
/// existing layers, and which one you got depended on whether you had run a command.
///
/// The rule is <b>seed once</b>: a newly created layer takes its appearance from the template, and
/// after that the layer owns it. Edits in Rhino's Layers panel and per-detail print overrides
/// survive every rebuild. Re-stamping appearance is a deliberate, separate act
/// (<c>restyleExisting</c>), never something a build or a routine template apply does.
/// </summary>
internal static class LayerCreationService
{
    /// <summary>
    /// Creates a <c>::</c>-delimited layer path, seeding each layer it creates from that layer's own
    /// entry in <paramref name="table"/>.
    ///
    /// Appearance is matched per segment rather than applied wholesale down the path. Applying the
    /// leaf's appearance to its ancestors meant creating <c>Annotation::Sections::CutFill::Fill</c>
    /// restyled <c>Annotation</c> and <c>Annotation::Sections</c> to look like the fill, so a
    /// document ended up styled by whichever child happened to be written last.
    /// </summary>
    /// <param name="sourceLayer">Optional layer whose colour the leaf should copy — zone output
    /// takes its colour from the layer its boundary was read from, which identifies the zone.</param>
    /// <param name="restyleExisting">Re-applies appearance to layers that already exist, discarding
    /// the user's Layers-panel edits. Only ever set from an explicit, confirmed reset.</param>
    public static int EnsureLayerPath(
        RhinoDoc doc,
        string fullPath,
        LayerRoleTable table,
        Layer? sourceLayer = null,
        bool restyleExisting = false)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            return doc.Layers.CurrentLayerIndex;

        int parentIndex = -1;
        string currentPath = string.Empty;

        foreach (var segment in fullPath.Split(new[] { "::" }, StringSplitOptions.None))
        {
            currentPath = string.IsNullOrEmpty(currentPath) ? segment : $"{currentPath}::{segment}";
            bool isLeaf = string.Equals(currentPath, fullPath, StringComparison.OrdinalIgnoreCase);
            int index = doc.Layers.FindByFullPath(currentPath, -1);

            if (index >= 0)
            {
                if (restyleExisting)
                    RestyleLayer(doc, index, table.TryGetLayerAppearance(currentPath), isLeaf ? sourceLayer : null);

                parentIndex = index;
                continue;
            }

            var layer = new Layer { Name = segment };
            if (parentIndex >= 0)
                layer.ParentLayerId = doc.Layers[parentIndex].Id;

            ApplyAppearance(doc, layer, table.TryGetLayerAppearance(currentPath), isLeaf ? sourceLayer : null);
            parentIndex = doc.Layers.Add(layer);
        }

        return parentIndex >= 0 ? parentIndex : doc.Layers.CurrentLayerIndex;
    }

    /// <summary>
    /// Creates every layer the template declares. Used by <c>mhApplyLayerTemplate</c> and when a
    /// document is first built, so the Layers panel shows where output is going before anything is
    /// baked — and so preview can resolve real layer colours instead of falling back to grey.
    /// </summary>
    public static (int Created, int Existing) ApplyTemplate(
        RhinoDoc doc,
        LayerRoleTable table,
        bool restyleExisting = false)
    {
        int created = 0;
        int existing = 0;

        foreach (var (path, _) in table.AllLayers)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;

            if (doc.Layers.FindByFullPath(path, -1) >= 0)
                existing++;
            else
                created++;

            EnsureLayerPath(doc, path, table, sourceLayer: null, restyleExisting);
        }

        return (created, existing);
    }

    private static void ApplyAppearance(
        RhinoDoc doc,
        Layer layer,
        LayerAppearance? appearance,
        Layer? sourceLayer)
    {
        if (appearance != null)
        {
            layer.Color = System.Drawing.Color.FromArgb(appearance.ColorArgb);
            layer.PlotColor = System.Drawing.Color.FromArgb(appearance.PrintColorArgb);

            // A null print width leaves Rhino's own default, which is what text layers want.
            if (appearance.PlotWeight.HasValue)
                layer.PlotWeight = appearance.PlotWeight.Value;

            // Only a linetype the document already has. Inventing one from a name would put a
            // definition in the user's document that they never asked for, and a template naming a
            // linetype this document has never heard of is better left continuous than guessed at.
            if (!string.IsNullOrWhiteSpace(appearance.LinetypeName))
            {
                int linetypeIndex = doc.Linetypes.Find(appearance.LinetypeName);
                if (linetypeIndex >= 0)
                    layer.LinetypeIndex = linetypeIndex;
            }
        }

        // An explicit source layer wins: it is carrying information (which zone this is) that the
        // template cannot know.
        if (sourceLayer != null)
        {
            layer.Color = sourceLayer.Color;
            layer.PlotColor = sourceLayer.PlotColor;
        }
    }

    private static void RestyleLayer(
        RhinoDoc doc,
        int layerIndex,
        LayerAppearance? appearance,
        Layer? sourceLayer)
    {
        if (layerIndex < 0 || layerIndex >= doc.Layers.Count)
            return;

        Layer existing = doc.Layers[layerIndex];
        if (existing == null)
            return;

        ApplyAppearance(doc, existing, appearance, sourceLayer);
        doc.Layers.Modify(existing, layerIndex, quiet: true);
    }
}
