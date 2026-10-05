using System.Drawing;
using MoleHill.Rhino.Model;
using Rhino;
using Rhino.DocObjects.Tables;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Shared colour/transparency resolution for generated terrain output.
///
/// Both the viewport path (<see cref="TerrainDisplayConduit"/>, via <c>DisplayMaterial</c>) and the
/// render path (<see cref="TerrainRenderMeshProvider"/>, via <c>RenderMaterial</c>) must land on the
/// same colour for the same object, otherwise a rendered view will not match the preview — which is
/// the entire point of the render mesh provider. Keep the resolution in one place.
/// </summary>
internal static class TerrainDisplayColors
{
    // Layer colour by full path, per document. The conduit resolves a colour for every generated
    // object in every display pass, and FindByFullPath was ~2 µs of that each time: on a terrain with
    // 2,763 contour labels in a seven-pass Shaded view it alone cost ~40 ms a frame. A null value
    // records that the path has no layer. Any layer-table change can alter a colour or a path, so it
    // drops the document's entries.
    private static readonly object LayerColorGate = new();
    private static readonly Dictionary<(uint DocumentSerial, string LayerPath), Color?> LayerColors = new();

    // Subscribed on the first layer lookup, not in a static constructor: subscribing needs Rhino's native
    // runtime, and the explicit-colour path must keep working without one (managed tests use it).
    private static bool _subscribedToLayerEvents;

    /// <summary>
    /// Explicit object colour wins, then the output layer as it exists in the document, then the
    /// layer the geometry was read from, and finally the appearance the layer template declares.
    ///
    /// The last step is what makes a preview match its bake. Output layers are only created when
    /// something is baked, so before this an un-baked terrain drew every drawing line in a
    /// placeholder grey and then changed colour the moment it was baked.
    ///
    /// The output layer is consulted before the source layer for the same reason the template comes
    /// last: once a layer is in the document it belongs to the user. A zone's layer is seeded from
    /// its source layer when created, so the two agree to begin with; afterwards, restyling either
    /// one does the same thing to the preview as it does to the bake.
    /// </summary>
    public static Color Resolve(
        global::Rhino.RhinoDoc doc,
        string? layerPath,
        string? sourceLayerPath,
        int? colorArgb,
        LayerAppearance? appearance = null)
    {
        if (colorArgb.HasValue)
            return Color.FromArgb(colorArgb.Value);

        if (TryResolveLayerColor(doc, layerPath, out Color layerColor))
            return layerColor;

        if (TryResolveLayerColor(doc, sourceLayerPath, out Color sourceColor))
            return sourceColor;

        return Color.FromArgb(appearance?.ColorArgb ?? unchecked((int)0xFF000000));
    }

    /// <summary>
    /// An explicit object colour carries its own alpha; otherwise the terrain's output transparency
    /// applies. Returned as Rhino transparency (0 = opaque, 1 = fully transparent).
    /// </summary>
    public static double ResolveTransparency(TerrainDefinition terrain, int? colorArgb)
    {
        if (colorArgb.HasValue)
            return 1.0 - (Color.FromArgb(colorArgb.Value).A / 255.0);

        return Math.Clamp(terrain.OutputTransparencyPercent, 0, 100) / 100.0;
    }

    public static Color GetOpaqueColor(Color color) => Color.FromArgb(color.R, color.G, color.B);

    private static bool TryResolveLayerColor(global::Rhino.RhinoDoc doc, string? layerPath, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(layerPath))
            return false;

        var key = (doc.RuntimeSerialNumber, layerPath);
        Color? cached;
        lock (LayerColorGate)
        {
            if (!_subscribedToLayerEvents)
            {
                RhinoDoc.LayerTableEvent += OnLayerTableEvent;
                RhinoDoc.CloseDocument += OnCloseDocument;
                _subscribedToLayerEvents = true;
            }

            if (!LayerColors.TryGetValue(key, out cached))
            {
                int layerIndex = doc.Layers.FindByFullPath(layerPath, -1);
                cached = layerIndex >= 0 && layerIndex < doc.Layers.Count
                    ? doc.Layers[layerIndex].Color
                    : null;
                LayerColors[key] = cached;
            }
        }

        if (cached is not Color found)
            return false;

        color = found;
        return true;
    }

    private static void OnLayerTableEvent(object? sender, LayerTableEventArgs e) =>
        InvalidateDocument(e.Document.RuntimeSerialNumber);

    private static void OnCloseDocument(object? sender, DocumentEventArgs e) =>
        InvalidateDocument(e.Document.RuntimeSerialNumber);

    private static void InvalidateDocument(uint documentSerial)
    {
        lock (LayerColorGate)
        {
            var keys = LayerColors.Keys.Where(key => key.DocumentSerial == documentSerial).ToArray();
            foreach (var key in keys)
                LayerColors.Remove(key);
        }
    }
}
