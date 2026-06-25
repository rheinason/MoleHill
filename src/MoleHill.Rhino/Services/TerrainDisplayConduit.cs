using System.Drawing;
using System.Linq;
using MoleHill.Rhino.Model;
using Rhino.DocObjects;
using Rhino.Display;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

internal sealed class TerrainDisplayConduit : DisplayConduit
{
    private const int ScatterShapePointBudget = 32;

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

        if (displayState.ScatterObjects.Count > 0)
            DrawScatterObjects(e, doc, terrain, displayState);
    }

    private static void DrawScatterObjects(DrawEventArgs e, global::Rhino.RhinoDoc doc, TerrainDefinition terrain, TerrainDisplayState displayState)
    {
        if (displayState.ScatterObjectRanges.Count == 0)
            displayState.RebuildScatterObjectRanges();

        var definitions = terrain.Objects
            .OfType<ScatterObjectDefinition>()
            .ToDictionary(definition => definition.Id);
        var frame = new ScatterPreviewDrawFrame(doc);

        foreach (var rangeEntry in displayState.ScatterObjectRanges)
        {
            Guid key = rangeEntry.Key;
            ScatterObjectDefinition? definition = null;
            if (key != Guid.Empty)
                definitions.TryGetValue(key, out definition);

            ScatterPreviewMode mode = definition?.PreviewMode ?? ScatterPreviewMode.Instances;
            ScatterObjectRange range = rangeEntry.Value;
            int drawCount = definition is { PreviewCap: > 0 }
                ? Math.Min(range.Count, definition.PreviewCap)
                : range.Count;
            if (drawCount <= 0)
                continue;

            int end = Math.Min(range.StartIndex + drawCount, displayState.ScatterObjects.Count);
            for (int index = range.StartIndex; index < end; index++)
                DrawScatterObject(e, frame, displayState.ScatterObjects[index], mode);
        }
    }

    private static void DrawScatterObject(
        DrawEventArgs e,
        ScatterPreviewDrawFrame frame,
        GeneratedRhinoObject scatter,
        ScatterPreviewMode mode)
    {
        switch (mode)
        {
            case ScatterPreviewMode.Points:
            {
                var color = frame.ResolveColor(scatter.LayerPath, scatter.SourceLayerPath, scatter.ColorArgb);
                Point3d origin = Point3d.Origin;
                origin.Transform(scatter.InstanceTransform);
                e.Display.DrawPoint(origin, color);
                break;
            }
            case ScatterPreviewMode.ShapePoints:
            {
                var color = frame.ResolveColor(scatter.LayerPath, scatter.SourceLayerPath, scatter.ColorArgb);
                if (frame.TryGetScatterShapePoints(scatter, out var points))
                {
                    foreach (Point3d sourcePoint in points)
                    {
                        Point3d point = sourcePoint;
                        point.Transform(scatter.InstanceTransform);
                        e.Display.DrawPoint(point, color);
                    }
                }
                else
                {
                    Point3d origin = Point3d.Origin;
                    origin.Transform(scatter.InstanceTransform);
                    e.Display.DrawPoint(origin, color);
                }

                break;
            }
            case ScatterPreviewMode.BoundingBox:
            {
                var color = frame.ResolveColor(scatter.LayerPath, scatter.SourceLayerPath, scatter.ColorArgb);
                if (frame.TryGetScatterBox(scatter, out Box box))
                    e.Display.DrawBox(box, color, 1);
                break;
            }
            default:
                DrawScatterInstanceGeometry(e, frame, scatter);
                break;
        }
    }

    private sealed class ScatterPreviewDrawFrame
    {
        private readonly global::Rhino.RhinoDoc _doc;
        private readonly Dictionary<string, InstanceDefinition?> _definitions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Box?> _definitionBoxes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Point3d[]> _definitionShapePoints = new(StringComparer.Ordinal);
        private readonly Dictionary<(string? LayerPath, string? SourceLayerPath, int? ColorArgb), Color> _colors = new();

        public ScatterPreviewDrawFrame(global::Rhino.RhinoDoc doc)
        {
            _doc = doc;
        }

        public Color ResolveColor(string? layerPath, string? sourceLayerPath, int? colorArgb)
        {
            var key = (layerPath, sourceLayerPath, colorArgb);
            if (_colors.TryGetValue(key, out var color))
                return color;

            color = TerrainDisplayConduit.ResolveColor(_doc, layerPath, sourceLayerPath, colorArgb);
            _colors[key] = color;
            return color;
        }

        public InstanceDefinition? FindDefinition(string? definitionName)
        {
            if (string.IsNullOrWhiteSpace(definitionName))
                return null;

            if (_definitions.TryGetValue(definitionName!, out var cached))
                return cached;

            var definition = _doc.InstanceDefinitions.Find(definitionName!);
            _definitions[definitionName!] = definition;
            return definition;
        }

        public bool TryGetScatterBox(GeneratedRhinoObject scatter, out Box box)
        {
            box = Box.Unset;
            if (string.IsNullOrWhiteSpace(scatter.InstanceDefinitionName))
                return false;

            if (!_definitionBoxes.TryGetValue(scatter.InstanceDefinitionName!, out Box? cachedBox))
            {
                cachedBox = TryBuildDefinitionBox(scatter.InstanceDefinitionName!, out Box definitionBox)
                    ? definitionBox
                    : null;
                _definitionBoxes[scatter.InstanceDefinitionName!] = cachedBox;
            }

            if (!cachedBox.HasValue)
                return false;

            box = cachedBox.Value;
            return box.Transform(scatter.InstanceTransform) && box.IsValid;
        }

        public bool TryGetScatterShapePoints(GeneratedRhinoObject scatter, out Point3d[] points)
        {
            points = Array.Empty<Point3d>();
            if (string.IsNullOrWhiteSpace(scatter.InstanceDefinitionName))
                return false;

            if (!_definitionShapePoints.TryGetValue(scatter.InstanceDefinitionName!, out points!))
            {
                points = BuildDefinitionShapePoints(scatter.InstanceDefinitionName!);
                _definitionShapePoints[scatter.InstanceDefinitionName!] = points;
            }

            return points.Length > 0;
        }

        private bool TryBuildDefinitionBox(string definitionName, out Box box)
        {
            box = Box.Unset;
            var definition = FindDefinition(definitionName);
            if (definition == null)
                return false;

            BoundingBox bounds = BoundingBox.Empty;
            foreach (var instanceObject in definition.GetObjects())
            {
                if (instanceObject?.Geometry == null)
                    continue;

                BoundingBox geometryBounds = instanceObject.Geometry.GetBoundingBox(true);
                if (geometryBounds.IsValid)
                    bounds.Union(geometryBounds);
            }

            if (!bounds.IsValid)
                return false;

            box = new Box(bounds);
            return box.IsValid;
        }

        private Point3d[] BuildDefinitionShapePoints(string definitionName)
        {
            var definition = FindDefinition(definitionName);
            if (definition == null)
                return Array.Empty<Point3d>();

            var points = new List<Point3d>(ScatterShapePointBudget);
            BoundingBox definitionBounds = BoundingBox.Empty;
            foreach (var instanceObject in definition.GetObjects())
            {
                GeometryBase? geometry = instanceObject?.Geometry;
                if (geometry == null)
                    continue;

                BoundingBox bounds = geometry.GetBoundingBox(true);
                if (bounds.IsValid)
                    definitionBounds.Union(bounds);
            }

            if (definitionBounds.IsValid)
                AddBoundingBoxShapePoints(definitionBounds, points, includeCorners: true);

            foreach (var instanceObject in definition.GetObjects())
            {
                if (points.Count >= ScatterShapePointBudget)
                    break;

                GeometryBase? geometry = instanceObject?.Geometry;
                if (geometry == null)
                    continue;

                AddGeometryShapePoints(geometry, points);
            }

            return points.ToArray();
        }

        private static void AddGeometryShapePoints(GeometryBase geometry, List<Point3d> points)
        {
            switch (geometry)
            {
                case Mesh mesh:
                    AddMeshShapePoints(mesh, points);
                    return;
                case Brep brep:
                    AddBrepShapePoints(brep, points);
                    return;
                case Curve curve:
                    AddCurveShapePoints(curve, points);
                    return;
                case global::Rhino.Geometry.Point point:
                    AddShapePoint(point.Location, points);
                    return;
                case PointCloud pointCloud:
                    AddPointCloudShapePoints(pointCloud, points);
                    return;
            }

            BoundingBox bounds = geometry.GetBoundingBox(true);
            if (bounds.IsValid)
                AddBoundingBoxShapePoints(bounds, points, includeCorners: false);
        }

        private static void AddMeshShapePoints(Mesh mesh, List<Point3d> points)
        {
            int count = mesh.Vertices.Count;
            if (count == 0)
                return;

            int remaining = ScatterShapePointBudget - points.Count;
            int samples = Math.Min(remaining, Math.Min(12, count));
            if (samples <= 0)
                return;

            if (samples == 1)
            {
                AddShapePoint(mesh.Vertices.Point3dAt(0), points);
                return;
            }

            for (int i = 0; i < samples && points.Count < ScatterShapePointBudget; i++)
            {
                int index = (int)Math.Round(i * (count - 1) / (double)(samples - 1));
                AddShapePoint(mesh.Vertices.Point3dAt(index), points);
            }
        }

        private static void AddBrepShapePoints(Brep brep, List<Point3d> points)
        {
            int remaining = ScatterShapePointBudget - points.Count;
            int samples = Math.Min(remaining, Math.Min(8, brep.Vertices.Count));
            for (int i = 0; i < samples && points.Count < ScatterShapePointBudget; i++)
                AddShapePoint(brep.Vertices[i].Location, points);
        }

        private static void AddCurveShapePoints(Curve curve, List<Point3d> points)
        {
            int samples = Math.Min(6, ScatterShapePointBudget - points.Count);
            if (samples <= 0)
                return;

            if (samples == 1)
            {
                AddShapePoint(curve.PointAtStart, points);
                return;
            }

            double[]? parameters = curve.DivideByCount(samples - 1, includeEnds: true);
            if (parameters == null || parameters.Length == 0)
            {
                AddShapePoint(curve.PointAtStart, points);
                AddShapePoint(curve.PointAtEnd, points);
                return;
            }

            foreach (double parameter in parameters)
            {
                if (points.Count >= ScatterShapePointBudget)
                    break;

                AddShapePoint(curve.PointAt(parameter), points);
            }
        }

        private static void AddPointCloudShapePoints(PointCloud pointCloud, List<Point3d> points)
        {
            int count = pointCloud.Count;
            if (count == 0)
                return;

            int samples = Math.Min(ScatterShapePointBudget - points.Count, Math.Min(12, count));
            for (int i = 0; i < samples && points.Count < ScatterShapePointBudget; i++)
            {
                int index = samples == 1 ? 0 : (int)Math.Round(i * (count - 1) / (double)(samples - 1));
                AddShapePoint(pointCloud[index].Location, points);
            }
        }

        private static void AddBoundingBoxShapePoints(BoundingBox bounds, List<Point3d> points, bool includeCorners)
        {
            AddShapePoint(bounds.Center, points);
            if (!includeCorners)
                return;

            foreach (Point3d corner in bounds.GetCorners())
            {
                if (points.Count >= ScatterShapePointBudget)
                    return;

                AddShapePoint(corner, points);
            }
        }

        private static void AddShapePoint(Point3d point, List<Point3d> points)
        {
            if (!point.IsValid || points.Count >= ScatterShapePointBudget)
                return;

            foreach (Point3d existing in points)
            {
                if (existing.DistanceTo(point) <= 1e-9)
                    return;
            }

            points.Add(point);
        }
    }

    private static bool DrawScatterInstanceGeometry(DrawEventArgs e, ScatterPreviewDrawFrame frame, GeneratedRhinoObject scatter)
    {
        var definition = frame.FindDefinition(scatter.InstanceDefinitionName);
        if (definition == null)
            return false;

        e.Display.DrawInstanceDefinition(definition, scatter.InstanceTransform);
        return true;
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
