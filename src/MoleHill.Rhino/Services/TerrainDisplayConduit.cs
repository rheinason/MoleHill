using System.Drawing;
using MoleHill.Rhino.Model;
using Rhino.Display;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

internal sealed class TerrainDisplayConduit : DisplayConduit
{
    protected override void PostDrawObjects(DrawEventArgs e)
    {
        if (e.RhinoDoc == null)
            return;

        foreach (var view in TerrainController.Instance.GetPreviewViews(e.RhinoDoc))
            DrawTerrain(e, e.RhinoDoc, view.Terrain, view.DisplayState);
    }

    private static void DrawTerrain(DrawEventArgs e, global::Rhino.RhinoDoc doc, TerrainDefinition terrain, TerrainDisplayState displayState)
    {
        if (!terrain.IsVisible)
            return;

        if (terrain.ShowTerrainMesh && displayState.PreviewTerrainMesh != null)
            DrawGeneratedMesh(e, doc, terrain, displayState.PreviewTerrainMesh, TerrainDefinition.ResolveTerrainLayerPath(terrain.TerrainLayerPath), null, terrain.TerrainColorArgb);

        if (terrain.ShowZoneMeshes)
        {
            foreach (var zone in displayState.ZoneObjects)
                DrawGeneratedObject(e, doc, terrain, zone);
        }

        foreach (var auxiliary in displayState.AuxiliaryObjects)
        {
            if (TerrainAnalysisPreviewBuilder.ShouldDisplayGeneratedOutput(terrain, auxiliary))
                DrawGeneratedObject(e, doc, terrain, auxiliary);
        }

        foreach (var marker in displayState.MarkerObjects)
            DrawGeneratedObject(e, doc, terrain, marker);
    }

    private static void DrawGeneratedObject(DrawEventArgs e, global::Rhino.RhinoDoc doc, TerrainDefinition terrain, GeneratedRhinoObject generated)
    {
        if (generated.Geometry is Mesh mesh)
        {
            DrawGeneratedMesh(e, doc, terrain, mesh, generated.LayerPath, generated.SourceLayerPath, generated.ColorArgb);
            return;
        }

        if (generated.Geometry is Brep brep)
        {
            var material = CreateDisplayMaterial(doc, terrain, generated.LayerPath, generated.SourceLayerPath, generated.ColorArgb);
            e.Display.DrawBrepShaded(brep, material);
            return;
        }

        if (generated.Geometry is TextDot textDot)
        {
            var color = ResolveColor(doc, generated.LayerPath, generated.SourceLayerPath, generated.ColorArgb);
            e.Display.DrawDot(textDot.Point, textDot.Text, color, Color.White);
            return;
        }

        if (generated.Geometry is Curve curve)
        {
            var color = ResolveColor(doc, generated.LayerPath, generated.SourceLayerPath, generated.ColorArgb);
            e.Display.DrawCurve(curve, color, 2);
            return;
        }

        if (!string.IsNullOrWhiteSpace(generated.InstanceDefinitionName))
        {
            bool drawOnTop = generated.AnalysisId.HasValue;
            if (drawOnTop)
            {
                e.Display.PushDepthTesting(false);
                e.Display.PushDepthWriting(false);
            }

            try
            {
                DrawMarkerTemplate(e, doc, generated);
            }
            finally
            {
                if (drawOnTop)
                {
                    e.Display.PopDepthWriting();
                    e.Display.PopDepthTesting();
                }
            }
        }
    }

    private static void DrawGeneratedMesh(DrawEventArgs e, global::Rhino.RhinoDoc doc, TerrainDefinition terrain, Mesh mesh, string? layerPath, string? sourceLayerPath, int? colorArgb)
    {
        if (mesh.VertexColors.Count == mesh.Vertices.Count && mesh.VertexColors.Count > 0)
        {
            e.Display.DrawMeshFalseColors(mesh);
            if (terrain.ShowMeshWires)
                e.Display.DrawMeshWires(mesh, ResolveWireColor(doc, layerPath, sourceLayerPath, colorArgb));
            return;
        }

        var material = CreateDisplayMaterial(doc, terrain, layerPath, sourceLayerPath, colorArgb);
        e.Display.DrawMeshShaded(mesh, material);
        if (terrain.ShowMeshWires)
            e.Display.DrawMeshWires(mesh, ResolveWireColor(doc, layerPath, sourceLayerPath, colorArgb));
    }

    private static DisplayMaterial CreateDisplayMaterial(global::Rhino.RhinoDoc doc, TerrainDefinition terrain, string? layerPath, string? sourceLayerPath, int? colorArgb)
    {
        var color = GetOpaqueColor(ResolveColor(doc, layerPath, sourceLayerPath, colorArgb));
        var material = new DisplayMaterial(color)
        {
            Transparency = ResolveTransparency(terrain, colorArgb)
        };
        return material;
    }

    private static Color ResolveColor(global::Rhino.RhinoDoc doc, string? layerPath, string? sourceLayerPath, int? colorArgb)
    {
        if (colorArgb.HasValue)
            return Color.FromArgb(colorArgb.Value);

        if (TryResolveLayerColor(doc, sourceLayerPath, out var sourceColor))
            return sourceColor;

        if (TryResolveLayerColor(doc, layerPath, out var layerColor))
            return layerColor;

        return Color.FromArgb(180, 180, 180);
    }

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

    private static double ResolveTransparency(TerrainDefinition terrain, int? colorArgb)
    {
        if (colorArgb.HasValue)
            return 1.0 - (Color.FromArgb(colorArgb.Value).A / 255.0);

        return Math.Clamp(terrain.OutputTransparencyPercent, 0, 100) / 100.0;
    }

    private static Color GetOpaqueColor(Color color)
    {
        return Color.FromArgb(color.R, color.G, color.B);
    }

    private static Color ResolveWireColor(global::Rhino.RhinoDoc doc, string? layerPath, string? sourceLayerPath, int? colorArgb)
    {
        var baseColor = GetOpaqueColor(ResolveColor(doc, layerPath, sourceLayerPath, colorArgb));
        return Color.FromArgb(
            Math.Max(0, (int)Math.Round(baseColor.R * 0.45)),
            Math.Max(0, (int)Math.Round(baseColor.G * 0.45)),
            Math.Max(0, (int)Math.Round(baseColor.B * 0.45)));
    }

    private static void DrawMarkerTemplate(DrawEventArgs e, global::Rhino.RhinoDoc doc, GeneratedRhinoObject generated)
    {
        var color = ResolveColor(doc, generated.LayerPath, generated.SourceLayerPath, generated.ColorArgb);

        if (TryDrawBlockDefinitionGeometry(e, doc, generated, color))
            return;

        foreach (var geometry in CreateMarkerBlockGeometry(generated.MarkerBlockTemplate))
            DrawMarkerGeometry(e, generated, geometry, color, substituteDisplayText: true);
    }

    private static bool TryDrawBlockDefinitionGeometry(DrawEventArgs e, global::Rhino.RhinoDoc doc, GeneratedRhinoObject generated, Color color)
    {
        if (string.IsNullOrWhiteSpace(generated.InstanceDefinitionName))
            return false;

        var definition = doc.InstanceDefinitions.Find(generated.InstanceDefinitionName!);
        if (definition == null)
            return false;

        bool drewGeometry = false;
        foreach (var instanceObject in definition.GetObjects())
        {
            if (instanceObject?.Geometry == null)
                continue;

            if (DrawMarkerGeometry(e, generated, instanceObject.Geometry, color, substituteDisplayText: true))
                drewGeometry = true;
        }

        return drewGeometry;
    }

    private static bool DrawMarkerGeometry(
        DrawEventArgs e,
        GeneratedRhinoObject generated,
        GeometryBase geometry,
        Color color,
        bool substituteDisplayText)
    {
        switch (geometry)
        {
            case Curve curve:
            {
                var transformed = curve.DuplicateCurve();
                transformed.Transform(generated.InstanceTransform);
                e.Display.DrawCurve(transformed, color, 2);
                return true;
            }
            case TextEntity text:
            {
                if (text.Duplicate() is not TextEntity transformedText)
                    return false;

                if (substituteDisplayText &&
                    TryBuildPreviewDisplayText(generated.InstanceUserStrings, out var displayText))
                {
                    transformedText.RichText = displayText;
                }

                transformedText.Transform(generated.InstanceTransform);
                e.Display.DrawText(transformedText, color);
                return true;
            }
            default:
                return false;
        }
    }

    private static bool TryBuildPreviewDisplayText(
        IReadOnlyDictionary<string, string>? userStrings,
        out string displayText)
    {
        displayText = string.Empty;
        if (userStrings == null || userStrings.Count == 0)
            return false;

        string prefix = GetUserString(userStrings, GeneratedBlockCatalog.PrefixToken);
        string value = GetUserString(userStrings, GeneratedBlockCatalog.ValueToken);
        string suffix = GetUserString(userStrings, GeneratedBlockCatalog.SuffixToken);
        displayText = string.Concat(prefix, value, suffix);
        if (!string.IsNullOrWhiteSpace(displayText))
            return true;

        displayText = GetUserString(userStrings, GeneratedBlockCatalog.DisplayToken);
        return !string.IsNullOrWhiteSpace(displayText);
    }

    private static string GetUserString(IReadOnlyDictionary<string, string> userStrings, string key)
    {
        if (!userStrings.TryGetValue(key, out var value) || value == null)
            return string.Empty;

        return value;
    }

    private static IEnumerable<GeometryBase> CreateMarkerBlockGeometry(MarkerBlockTemplate template)
    {
        foreach (var geometry in GeneratedBlockCatalog.CreateBlockGeometry(template))
            yield return geometry;
    }
}
