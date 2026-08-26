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
    private static readonly Color FallbackColor = Color.FromArgb(180, 180, 180);

    /// <summary>Explicit object colour wins, then the source layer, then the output layer.</summary>
    public static Color Resolve(global::Rhino.RhinoDoc doc, string? layerPath, string? sourceLayerPath, int? colorArgb)
    {
        if (colorArgb.HasValue)
            return Color.FromArgb(colorArgb.Value);

        if (TryResolveLayerColor(doc, sourceLayerPath, out Color sourceColor))
            return sourceColor;

        if (TryResolveLayerColor(doc, layerPath, out Color layerColor))
            return layerColor;

        return FallbackColor;
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
