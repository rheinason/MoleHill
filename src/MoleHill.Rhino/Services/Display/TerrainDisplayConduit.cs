using System.Drawing;
using MoleHill.Rhino.Model;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

internal sealed class TerrainDisplayConduit : DisplayConduit
{
    private const int ScatterShapePointBudget = 32;
    private const int OverlaySegmentBudget = 50_000;
    private const int OverlayFaceBudget = 50_000;
    private const int OverlayAnnotationBudget = 500;
    private static readonly object DisplayMaterialCacheGate = new();
    private static readonly Dictionary<(int Argb, double Transparency), DisplayMaterial> DisplayMaterialCache = new();
    private static readonly object MarkerBlockGeometryCacheGate = new();
    private static readonly Dictionary<MarkerBlockTemplate, GeometryBase[]> MarkerBlockGeometryCache = new();

    protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
    {
        if (e.RhinoDoc == null)
            return;

        foreach (var view in TerrainController.Instance.GetPreviewViews(e.RhinoDoc))
        {
            if (!view.Terrain.IsVisible)
                continue;

            BoundingBox bounds = view.DisplayState.GetPreviewBounds(e.RhinoDoc);
            if (bounds.IsValid)
                e.IncludeBoundingBox(bounds);
        }
    }

    protected override void PostDrawObjects(DrawEventArgs e)
    {
        if (e.RhinoDoc == null)
            return;

        bool shadowPass = IsShadowMapPass(e);
        foreach (var view in TerrainController.Instance.GetPreviewViews(e.RhinoDoc))
        {
            if (shadowPass)
                DrawShadowCasters(e, e.RhinoDoc, view.Terrain, view.DisplayState);
            else
                DrawTerrain(e, e.RhinoDoc, view.Terrain, view.DisplayState);
            view.DisplayState.MarkDrawn();
        }
    }

    /// <summary>
    /// Lines, labels and dots, drawn once per frame. PostDrawObjects runs once per pass, and a Shaded
    /// mode with skylight shadows makes about ten passes, seven of them indistinguishable from the
    /// view's own. A mesh redraw is nearly free from its GPU cache, but every curve and text entity is
    /// re-submitted: measured on a terrain with 2,763 contour labels and 733 contour lines, they cost
    /// ~160 ms of every frame — the 3 fps a sculpt session ran at. DrawForeground runs once per frame
    /// with the scene's depth buffer intact.
    ///
    /// Curves are depth-tested (testing is turned back on here), so a contour behind a hill or under a
    /// wall stays hidden, as its bake would be. Labels and dots are drawn on top of everything, after
    /// the curves: a label exists to be read, and one half-buried in a slope is no use.
    /// </summary>
    protected override void DrawForeground(DrawEventArgs e)
    {
        if (e.RhinoDoc == null)
            return;

        var views = TerrainController.Instance.GetPreviewViews(e.RhinoDoc);

        e.Display.PushDepthTesting(true);
        try
        {
            foreach (var view in views)
                DrawTerrainLinework(e, e.RhinoDoc, view.Terrain, view.DisplayState, labels: false);
        }
        finally
        {
            e.Display.PopDepthTesting();
        }

        e.Display.PushDepthTesting(false);
        e.Display.PushDepthWriting(false);
        try
        {
            foreach (var view in views)
                DrawTerrainLinework(e, e.RhinoDoc, view.Terrain, view.DisplayState, labels: true);
        }
        finally
        {
            e.Display.PopDepthWriting();
            e.Display.PopDepthTesting();
        }
    }

    /// <summary>Geometry drawn by <see cref="DrawForeground"/> rather than once per pass.</summary>
    private static bool IsLinework(GeneratedRhinoObject generated) =>
        generated.Geometry is Curve || IsLabel(generated);

    private static bool IsLabel(GeneratedRhinoObject generated) =>
        generated.Geometry is TextEntity or TextDot;

    /// <summary>Draws the terrain's curves (<paramref name="labels"/> false) or its labels and dots.</summary>
    private static void DrawTerrainLinework(
        DrawEventArgs e,
        global::Rhino.RhinoDoc doc,
        TerrainDefinition terrain,
        TerrainDisplayState displayState,
        bool labels)
    {
        if (!terrain.IsVisible)
            return;

        bool Wanted(GeneratedRhinoObject generated) => IsLinework(generated) && IsLabel(generated) == labels;

        if (terrain.ShowZoneMeshes)
        {
            foreach (var zone in displayState.ZoneObjects)
            {
                if (Wanted(zone))
                    DrawGeneratedObject(e, doc, terrain, zone);
            }
        }

        foreach (var auxiliary in displayState.AuxiliaryObjects)
        {
            if (!Wanted(auxiliary) || !TerrainAnalysisPreviewBuilder.ShouldDisplayGeneratedOutput(terrain, auxiliary))
                continue;

            // A sculpt session traces this annotation's contours live; its built curves are stale.
            if (!labels && TerrainController.Instance.IsReplacedBySculptLiveContours(terrain.TerrainId, auxiliary.AnalysisId))
                continue;

            DrawGeneratedObject(e, doc, terrain, auxiliary);
        }

        foreach (var marker in displayState.MarkerObjects)
        {
            if (Wanted(marker))
                DrawGeneratedObject(e, doc, terrain, marker);
        }

        if (!labels)
            TerrainController.Instance.DrawSculptLiveContours(e.Display, terrain.TerrainId);
    }

    /// <summary>
    /// True while the pipeline renders a shadow map. A display mode that casts shadows (Shaded, Arctic,
    /// Rendered) calls PostDrawObjects several times per frame, and during a shadow-map pass it projects
    /// from the light while <c>e.Viewport</c> still reports the view's camera, so the only tell is the
    /// pipeline's world-to-clip matrix disagreeing with the viewport's. Anything but a shaded surface drawn
    /// there leaks onto the screen from the light's projection: scatter points scattered across the
    /// terrain and floating in the air, found live.
    /// </summary>
    private static bool IsShadowMapPass(DrawEventArgs e)
    {
        float[]? pipeline = e.Display.GetOpenGLWorldToClip(true);
        if (pipeline == null || pipeline.Length < 16)
            return false;

        Transform viewport = e.Viewport.GetTransform(CoordinateSystem.World, CoordinateSystem.Clip);
        for (int row = 0; row < 4; row++)
        {
            // The depth row is negated on the view's own passes (OpenGL's depth convention), so compare
            // only x, y and w: a shadow-map projection disagrees in all of them.
            if (row == 2)
                continue;

            for (int column = 0; column < 4; column++)
            {
                // OpenGL matrices are column-major.
                double expected = viewport[row, column];
                double actual = pipeline[(column * 4) + row];
                if (Math.Abs(expected - actual) > 1e-4 * Math.Max(1.0, Math.Abs(expected)))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The shadow-map pass draws only what should cast a shadow: the terrain, zone and wall surfaces, and
    /// scatter drawn as real geometry. Never points, curves, text or overlays - see
    /// <see cref="IsShadowMapPass"/>.
    /// </summary>
    private static void DrawShadowCasters(DrawEventArgs e, global::Rhino.RhinoDoc doc, TerrainDefinition terrain, TerrainDisplayState displayState)
    {
        if (!terrain.IsVisible)
            return;

        DisplayMaterial material = GetOverlayMaterial(Color.Gray, 0.0);
        if (terrain.ShowTerrainMesh && displayState.PreviewTerrainMesh != null)
            e.Display.DrawMeshShaded(displayState.PreviewTerrainMesh, material);

        if (terrain.ShowZoneMeshes)
        {
            foreach (var zone in displayState.ZoneObjects)
                DrawShadowCaster(e, doc, zone, material);
        }

        foreach (var auxiliary in displayState.AuxiliaryObjects)
        {
            if (TerrainAnalysisPreviewBuilder.ShouldDisplayGeneratedOutput(terrain, auxiliary))
                DrawShadowCaster(e, doc, auxiliary, material);
        }

        if (displayState.ScatterObjects.Count > 0)
            DrawScatterObjects(e, doc, terrain, displayState, shadowPass: true);
    }

    private static void DrawShadowCaster(DrawEventArgs e, global::Rhino.RhinoDoc doc, GeneratedRhinoObject generated, DisplayMaterial material)
    {
        switch (generated.Geometry)
        {
            case Mesh mesh:
                e.Display.DrawMeshShaded(TerrainController.Instance.ResolveSculptZoneMesh(mesh), material);
                break;
            case Brep brep:
                MeshingParameters meshingParameters = doc.GetMeshingParameters(doc.MeshingParameterStyle);
                foreach (Mesh previewMesh in generated.GetPreviewBrepMeshes(brep, meshingParameters))
                    e.Display.DrawMeshShaded(previewMesh, material);
                break;
        }
    }

    private static void DrawTerrain(DrawEventArgs e, global::Rhino.RhinoDoc doc, TerrainDefinition terrain, TerrainDisplayState displayState)
    {
        if (!terrain.IsVisible)
            return;

        if (terrain.ShowTerrainMesh && displayState.PreviewTerrainMesh != null)
        {
            // Shade an unwelded copy: terrain and wall share their rail vertices, and one averaged normal
            // per vertex smears the wall's near-vertical normal across the flat ground beside it.
            // See TerrainPresentationMesh. The display state keeps the welded mesh, which analysis
            // preview, sculpt and the case exporter all pair with arrays extracted from it.
            Mesh? shaded = TerrainPresentationMesh.CreateForDisplay(displayState.PreviewTerrainMesh);
            DrawGeneratedMesh(e, doc, terrain, shaded ?? displayState.PreviewTerrainMesh, LayerRoleService.GetTable(doc, terrain).Path(LayerRole.Terrain), null, terrain.TerrainColorArgb);
        }

        DrawRuntimeOverlays(e, displayState);

        // Linework (curves, labels, dots) is drawn once per frame by DrawForeground, not per pass.
        if (terrain.ShowZoneMeshes)
        {
            foreach (var zone in displayState.ZoneObjects)
            {
                if (!IsLinework(zone))
                    DrawGeneratedObject(e, doc, terrain, zone);
            }
        }

        foreach (var auxiliary in displayState.AuxiliaryObjects)
        {
            if (!IsLinework(auxiliary) && TerrainAnalysisPreviewBuilder.ShouldDisplayGeneratedOutput(terrain, auxiliary))
                DrawGeneratedObject(e, doc, terrain, auxiliary);
        }

        foreach (var marker in displayState.MarkerObjects)
        {
            if (!IsLinework(marker))
                DrawGeneratedObject(e, doc, terrain, marker);
        }

        if (displayState.ScatterObjects.Count > 0)
            DrawScatterObjects(e, doc, terrain, displayState);
    }

    private static void DrawRuntimeOverlays(DrawEventArgs e, TerrainDisplayState displayState)
    {
        int segments = 0;
        int faces = 0;
        int annotations = 0;
        IEnumerable<RuntimeOverlayItem> orderedItems = displayState.RuntimeOverlays
            .Select((item, index) => (item, index))
            .Where(pair => pair.item.Channel == RuntimeOverlayChannel.Guide ||
                           displayState.VisibleDiagnosticOwners.Contains(pair.item.Owner))
            .OrderByDescending(pair => pair.item.Channel == RuntimeOverlayChannel.Diagnostic)
            .ThenByDescending(pair => pair.item.Severity)
            .ThenBy(pair => pair.index)
            .Select(pair => pair.item);

        foreach (RuntimeOverlayItem item in orderedItems)
        {
            foreach (RuntimeOverlayPrimitive primitive in item.Primitives)
            {
                if (primitive.SegmentCost > 0 && segments + primitive.SegmentCost > OverlaySegmentBudget)
                    continue;
                if (primitive.FaceCost > 0 && faces + primitive.FaceCost > OverlayFaceBudget)
                    continue;
                if (primitive.AnnotationCost > 0 && annotations + primitive.AnnotationCost > OverlayAnnotationBudget)
                    continue;

                Color color = RuntimeOverlayPalette.Resolve(item, primitive);
                switch (primitive.Kind)
                {
                    case RuntimeOverlayPrimitiveKind.Marker when primitive.Point.IsValid:
                        e.Display.DrawPoint(primitive.Point, PointStyle.RoundSimple, primitive.Size, color);
                        break;
                    case RuntimeOverlayPrimitiveKind.Dot when primitive.Point.IsValid:
                        DrawOverlayAnnotation(e, () => e.Display.DrawDot(
                            primitive.Point,
                            string.IsNullOrWhiteSpace(primitive.Text) ? item.ShortLabel : primitive.Text,
                            Color.White,
                            color));
                        break;
                    case RuntimeOverlayPrimitiveKind.Text when primitive.Point.IsValid && !string.IsNullOrWhiteSpace(primitive.Text):
                        DrawOverlayAnnotation(e, () => e.Display.Draw2dText(primitive.Text, color, primitive.Point, true, primitive.Size));
                        break;
                    case RuntimeOverlayPrimitiveKind.Polyline:
                        DrawOverlayPolyline(e, primitive, color);
                        break;
                    case RuntimeOverlayPrimitiveKind.Mesh when primitive.RegionMesh != null:
                        if (primitive.ShadeMesh)
                            e.Display.DrawMeshShaded(primitive.RegionMesh, GetOverlayMaterial(color, primitive.MeshTransparency));
                        if (primitive.DrawMeshWires)
                            e.Display.DrawMeshWires(primitive.RegionMesh, color);
                        break;
                }

                segments += primitive.SegmentCost;
                faces += primitive.FaceCost;
                annotations += primitive.AnnotationCost;
            }
        }
    }

    private static void DrawOverlayAnnotation(DrawEventArgs e, Action draw)
    {
        e.Display.PushDepthTesting(false);
        e.Display.PushDepthWriting(false);
        try
        {
            draw();
        }
        finally
        {
            e.Display.PopDepthWriting();
            e.Display.PopDepthTesting();
        }
    }

    private static void DrawOverlayPolyline(DrawEventArgs e, RuntimeOverlayPrimitive primitive, Color color)
    {
        Point3d[] points = primitive.Points;
        for (int i = 1; i < points.Length; i++)
        {
            if (points[i - 1].IsValid && points[i].IsValid)
                e.Display.DrawLine(points[i - 1], points[i], color, primitive.Thickness);
        }

        if (primitive.IsClosed && points.Length > 2 && points[^1].IsValid && points[0].IsValid)
            e.Display.DrawLine(points[^1], points[0], color, primitive.Thickness);
    }

    private static DisplayMaterial GetOverlayMaterial(Color color, double transparency)
    {
        Color opaque = TerrainDisplayColors.GetOpaqueColor(color);
        var key = (opaque.ToArgb(), transparency);
        lock (DisplayMaterialCacheGate)
        {
            if (DisplayMaterialCache.TryGetValue(key, out DisplayMaterial? material))
                return material;

            material = new DisplayMaterial(opaque) { Transparency = transparency };
            DisplayMaterialCache[key] = material;
            return material;
        }
    }

    private static void DrawScatterObjects(
        DrawEventArgs e,
        global::Rhino.RhinoDoc doc,
        TerrainDefinition terrain,
        TerrainDisplayState displayState,
        bool shadowPass = false)
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
            if (shadowPass && mode != ScatterPreviewMode.Instances)
                continue;
            ScatterObjectRange range = rangeEntry.Value;
            int drawCount = definition is { PreviewCap: > 0 }
                ? Math.Min(range.Count, definition.PreviewCap)
                : range.Count;
            if (drawCount <= 0)
                continue;

            int end = Math.Min(range.StartIndex + drawCount, displayState.ScatterObjects.Count);
            for (int index = range.StartIndex; index < end; index++)
            {
                if (shadowPass)
                    DrawScatterInstanceGeometry(e, frame, displayState.ScatterObjects[index], meshesOnly: true);
                else
                    DrawScatterObject(e, frame, displayState.ScatterObjects[index], mode);
            }
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
        private readonly Dictionary<string, ScatterBlockPreview?> _blockPreviews = new(StringComparer.Ordinal);
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

            color = TerrainDisplayColors.Resolve(_doc, layerPath, sourceLayerPath, colorArgb);
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

        public ScatterBlockPreview? FindBlockPreview(string? definitionName)
        {
            if (string.IsNullOrWhiteSpace(definitionName))
                return null;

            if (_blockPreviews.TryGetValue(definitionName!, out var cached))
                return cached;

            var definition = FindDefinition(definitionName);
            var preview = definition == null ? null : ScatterBlockPreview.Get(_doc, definition);
            _blockPreviews[definitionName!] = preview;
            return preview;
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

    // Not DrawInstanceDefinition: per instance it is slow and leaks display memory until the GPU driver
    // kills Rhino. See ScatterBlockPreview.
    private static bool DrawScatterInstanceGeometry(
        DrawEventArgs e,
        ScatterPreviewDrawFrame frame,
        GeneratedRhinoObject scatter,
        bool meshesOnly = false)
    {
        ScatterBlockPreview? preview = frame.FindBlockPreview(scatter.InstanceDefinitionName);
        if (preview == null)
            return false;

        Color parentColor = frame.ResolveColor(scatter.LayerPath, scatter.SourceLayerPath, scatter.ColorArgb);
        e.Display.PushModelTransform(scatter.InstanceTransform);
        try
        {
            foreach (var (mesh, material) in preview.Meshes)
                e.Display.DrawMeshShaded(mesh, material);
            if (meshesOnly)
                return true;

            foreach (var (curve, color) in preview.Curves)
                e.Display.DrawCurve(curve, color ?? parentColor);
            foreach (var (hatch, color) in preview.Hatches)
                e.Display.DrawHatch(hatch, color ?? parentColor, color ?? parentColor);
            foreach (var (text, color) in preview.Texts)
                e.Display.DrawText(text, color ?? parentColor);
        }
        finally
        {
            e.Display.PopModelTransform();
        }

        return true;
    }

    private static void DrawGeneratedObject(DrawEventArgs e, global::Rhino.RhinoDoc doc, TerrainDefinition terrain, GeneratedRhinoObject generated)
    {
        if (generated.Geometry is Mesh mesh)
        {
            DrawGeneratedMesh(
                e,
                doc,
                terrain,
                TerrainController.Instance.ResolveSculptZoneMesh(mesh),
                generated.LayerPath,
                generated.SourceLayerPath,
                generated.ColorArgb,
                drawWires: generated.Kind != GeneratedObjectKind.RetainingWall);
            return;
        }

        if (generated.Geometry is Brep brep)
        {
            MeshingParameters meshingParameters = doc.GetMeshingParameters(doc.MeshingParameterStyle);
            IReadOnlyList<Mesh> previewMeshes = generated.GetPreviewBrepMeshes(brep, meshingParameters);
            if (previewMeshes.Count == 0)
            {
                var material = CreateDisplayMaterial(doc, terrain, generated.LayerPath, generated.SourceLayerPath, generated.ColorArgb);
                e.Display.DrawBrepShaded(brep, material);
                return;
            }

            foreach (Mesh previewMesh in previewMeshes)
            {
                DrawGeneratedMesh(
                    e,
                    doc,
                    terrain,
                    previewMesh,
                    generated.LayerPath,
                    generated.SourceLayerPath,
                    generated.ColorArgb,
                    drawWires: false);
            }
            return;
        }

        if (generated.Geometry is Hatch hatch)
        {
            LayerAppearance hatchAppearance = LayerRoleService.GetTable(doc, terrain).Appearance(generated.Role);
            var color = TerrainDisplayColors.Resolve(
                doc, generated.LayerPath, generated.SourceLayerPath, generated.ColorArgb, hatchAppearance);

            // A solid fill is what DrawHatch already does well, and is the default for cut and fill.
            // Anything patterned is drawn from its own pattern lines, so the preview shows the
            // hatch the bake will produce rather than a flat tint of it.
            IReadOnlyList<Curve> patternCurves = generated.GetPreviewHatchCurves(hatch);
            if (patternCurves.Count == 0)
            {
                e.Display.DrawHatch(hatch, color, color);
                return;
            }

            int hatchWidth = hatchAppearance.ScalePreviewWidth(terrain.PreviewLineWeight);
            foreach (Curve patternCurve in patternCurves)
                e.Display.DrawCurve(patternCurve, color, hatchWidth);
            return;
        }

        if (generated.Geometry is TextDot textDot)
        {
            var color = TerrainDisplayColors.Resolve(doc, generated.LayerPath, generated.SourceLayerPath, generated.ColorArgb);
            e.Display.DrawDot(textDot.Point, textDot.Text, color, Color.White);
            return;
        }

        if (generated.Geometry is TextEntity textEntity)
        {
            var color = TerrainDisplayColors.Resolve(doc, generated.LayerPath, generated.SourceLayerPath, generated.ColorArgb);
            e.Display.DrawText(textEntity, color);
            return;
        }

        if (generated.Geometry is Curve curve)
        {
            // Colour and thickness both come from the object's role, which is the same record the
            // bake stamps — so the viewport shows the drawing's hierarchy, and baking changes nothing.
            LayerAppearance appearance = LayerRoleService.GetTable(doc, terrain).Appearance(generated.Role);
            var color = TerrainDisplayColors.Resolve(
                doc, generated.LayerPath, generated.SourceLayerPath, generated.ColorArgb, appearance);
            int width = appearance.ScalePreviewWidth(terrain.PreviewLineWeight);
            e.Display.DrawCurve(curve, color, width);
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
                DrawMarkerTemplate(e, doc, terrain, generated, terrain.PreviewLineWeight);
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

    private static void DrawGeneratedMesh(
        DrawEventArgs e,
        global::Rhino.RhinoDoc doc,
        TerrainDefinition terrain,
        Mesh mesh,
        string? layerPath,
        string? sourceLayerPath,
        int? colorArgb,
        bool drawWires = true)
    {
        if (mesh.VertexColors.Count == mesh.Vertices.Count && mesh.VertexColors.Count > 0)
        {
            e.Display.DrawMeshFalseColors(mesh);
            if (terrain.ShowMeshWires && drawWires)
                e.Display.DrawMeshWires(mesh, ResolveWireColor(doc, layerPath, sourceLayerPath, colorArgb));
            return;
        }

        var material = CreateDisplayMaterial(doc, terrain, layerPath, sourceLayerPath, colorArgb);
        e.Display.DrawMeshShaded(mesh, material);
        if (terrain.ShowMeshWires && drawWires)
            e.Display.DrawMeshWires(mesh, ResolveWireColor(doc, layerPath, sourceLayerPath, colorArgb));
    }

    private static DisplayMaterial CreateDisplayMaterial(global::Rhino.RhinoDoc doc, TerrainDefinition terrain, string? layerPath, string? sourceLayerPath, int? colorArgb)
    {
        var color = TerrainDisplayColors.GetOpaqueColor(TerrainDisplayColors.Resolve(doc, layerPath, sourceLayerPath, colorArgb));
        double transparency = TerrainDisplayColors.ResolveTransparency(terrain, colorArgb);
        var key = (color.ToArgb(), transparency);
        lock (DisplayMaterialCacheGate)
        {
            if (DisplayMaterialCache.TryGetValue(key, out DisplayMaterial? material))
                return material;

            material = new DisplayMaterial(color)
            {
                Transparency = transparency
            };
            DisplayMaterialCache[key] = material;
            return material;
        }
    }

    private static Color ResolveWireColor(global::Rhino.RhinoDoc doc, string? layerPath, string? sourceLayerPath, int? colorArgb)
    {
        var baseColor = TerrainDisplayColors.GetOpaqueColor(TerrainDisplayColors.Resolve(doc, layerPath, sourceLayerPath, colorArgb));
        return Color.FromArgb(
            Math.Max(0, (int)Math.Round(baseColor.R * 0.45)),
            Math.Max(0, (int)Math.Round(baseColor.G * 0.45)),
            Math.Max(0, (int)Math.Round(baseColor.B * 0.45)));
    }

    private static void DrawMarkerTemplate(
        DrawEventArgs e,
        global::Rhino.RhinoDoc doc,
        TerrainDefinition terrain,
        GeneratedRhinoObject generated,
        double previewLineWeight)
    {
        LayerAppearance appearance = LayerRoleService.GetTable(doc, terrain).Appearance(generated.Role);
        var color = TerrainDisplayColors.Resolve(
            doc, generated.LayerPath, generated.SourceLayerPath, generated.ColorArgb, appearance);
        int width = appearance.ScalePreviewWidth(previewLineWeight);

        if (TryDrawBlockDefinitionGeometry(e, doc, generated, color, width))
            return;

        foreach (var geometry in GetMarkerBlockGeometry(generated.MarkerBlockTemplate))
            DrawMarkerGeometry(e, generated, geometry, color, substituteDisplayText: true, width);
    }

    private static bool TryDrawBlockDefinitionGeometry(
        DrawEventArgs e,
        global::Rhino.RhinoDoc doc,
        GeneratedRhinoObject generated,
        Color color,
        int width)
    {
        if (string.IsNullOrWhiteSpace(generated.InstanceDefinitionName))
            return false;

        var definition = doc.InstanceDefinitions.Find(generated.InstanceDefinitionName!);
        if (definition == null)
            return false;

        // The same flattened, cached preview Scatter uses, so a user's own block previews as it bakes:
        // shaded solids, hatches and nested blocks, and members keeping their own colour. Members that
        // take their colour from the parent draw in the label's colour.
        ScatterBlockPreview? preview = ScatterBlockPreview.Get(doc, definition);
        if (preview == null)
            return false;

        e.Display.PushModelTransform(generated.InstanceTransform);
        try
        {
            foreach (var (mesh, material) in preview.Meshes)
                e.Display.DrawMeshShaded(mesh, material);
            foreach (var (curve, memberColor) in preview.Curves)
                e.Display.DrawCurve(curve, memberColor ?? color, width);
            foreach (var (hatch, memberColor) in preview.Hatches)
                e.Display.DrawHatch(hatch, memberColor ?? color, memberColor ?? color);
            foreach (var (text, memberColor) in preview.Texts)
                e.Display.DrawText(generated.GetResolvedPreviewText(text), memberColor ?? color);
        }
        finally
        {
            e.Display.PopModelTransform();
        }

        return true;
    }

    private static bool DrawMarkerGeometry(
        DrawEventArgs e,
        GeneratedRhinoObject generated,
        GeometryBase geometry,
        Color color,
        bool substituteDisplayText,
        int width)
    {
        switch (geometry)
        {
            case Curve curve:
            {
                e.Display.PushModelTransform(generated.InstanceTransform);
                try
                {
                    e.Display.DrawCurve(curve, color, width);
                }
                finally
                {
                    e.Display.PopModelTransform();
                }

                return true;
            }
            case TextEntity text:
            {
                TextEntity textToDraw = substituteDisplayText
                    ? generated.GetResolvedPreviewText(text)
                    : text;

                e.Display.PushModelTransform(generated.InstanceTransform);
                try
                {
                    e.Display.DrawText(textToDraw, color);
                }
                finally
                {
                    e.Display.PopModelTransform();
                }

                return true;
            }
            default:
                return false;
        }
    }

    private static IReadOnlyList<GeometryBase> GetMarkerBlockGeometry(MarkerBlockTemplate template)
    {
        lock (MarkerBlockGeometryCacheGate)
        {
            if (MarkerBlockGeometryCache.TryGetValue(template, out GeometryBase[]? geometry))
                return geometry;

            geometry = GeneratedBlockCatalog.CreateBlockGeometry(template).ToArray();
            MarkerBlockGeometryCache[template] = geometry;
            return geometry;
        }
    }
}
