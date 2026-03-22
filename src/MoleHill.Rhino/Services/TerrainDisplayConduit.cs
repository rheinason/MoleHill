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
            DrawGeneratedMesh(e, doc, terrain, displayState.PreviewTerrainMesh, terrain.TerrainLayerPath, null, terrain.TerrainColorArgb);

        if (terrain.ShowZoneMeshes)
        {
            foreach (var zone in displayState.ZoneObjects)
                DrawGeneratedObject(e, doc, terrain, zone);
        }

        foreach (var auxiliary in displayState.AuxiliaryObjects)
            DrawGeneratedObject(e, doc, terrain, auxiliary);

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

        if (!string.IsNullOrWhiteSpace(generated.InstanceDefinitionName))
            DrawMarkerTemplate(e, generated);
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

    private static void DrawMarkerTemplate(DrawEventArgs e, GeneratedRhinoObject generated)
    {
        var color = generated.ColorArgb.HasValue
            ? Color.FromArgb(generated.ColorArgb.Value)
            : Color.FromArgb(30, 30, 30);

        foreach (var geometry in CreateMarkerBlockGeometry(generated.MarkerBlockTemplate))
        {
            if (geometry is Curve curve)
            {
                var transformed = curve.DuplicateCurve();
                transformed.Transform(generated.InstanceTransform);
                e.Display.DrawCurve(transformed, color, 2);
            }
        }
    }

    private static IEnumerable<GeometryBase> CreateMarkerBlockGeometry(MarkerBlockTemplate template)
    {
        switch (template)
        {
            case MarkerBlockTemplate.Elevation:
                yield return new Circle(Plane.WorldXY, 0.8).ToNurbsCurve();
                yield return new LineCurve(new Point3d(-0.8, 0.0, 0.0), new Point3d(0.8, 0.0, 0.0));
                yield return new LineCurve(new Point3d(0.0, -0.8, 0.0), new Point3d(0.0, 0.8, 0.0));
                yield break;
            case MarkerBlockTemplate.Slope:
                yield return new PolylineCurve(new[]
                {
                    new Point3d(-0.8, -0.2, 0.0),
                    new Point3d(0.4, -0.2, 0.0),
                    new Point3d(0.4, -0.6, 0.0),
                    new Point3d(0.9, 0.0, 0.0),
                    new Point3d(0.4, 0.6, 0.0),
                    new Point3d(0.4, 0.2, 0.0),
                    new Point3d(-0.8, 0.2, 0.0)
                });
                yield return new LineCurve(new Point3d(-0.8, 0.0, 0.0), new Point3d(0.9, 0.0, 0.0));
                yield break;
        }
    }
}
