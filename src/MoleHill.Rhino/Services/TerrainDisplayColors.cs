using System.Drawing;
using MoleHill.Rhino.Model;

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
        if (!string.IsNullOrWhiteSpace(layerPath))
        {
            int layerIndex = doc.Layers.FindByFullPath(layerPath, -1);
            if (layerIndex >= 0 && layerIndex < doc.Layers.Count)
            {
                color = doc.Layers[layerIndex].Color;
                return true;
            }
        }

        color = default;
        return false;
    }
}
