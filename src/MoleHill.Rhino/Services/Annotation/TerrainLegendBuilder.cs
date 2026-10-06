using System.Drawing;
using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Shared;
using Rhino;
using Rhino.Geometry;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

// The Legend annotation: what the terrain is coloured by, laid out as swatches, outlines and text. Drawn
// with the preview colouring (TerrainController.RefreshLegends), never by the build.

/// <summary>What a Legend annotation keys: the content and its heading, or why there is nothing to key.</summary>
internal sealed record TerrainLegendContent(AnalysisLegend Legend, string Title, Guid AnalysisId);

/// <summary>
/// Draws the Legend annotation: a key to the analysis colouring the terrain.
///
/// <para>It runs with the preview colouring rather than in the build (see
/// <see cref="LegendAnnotationDefinition"/>), and reads the range the mesh was <em>just</em> coloured with
/// from the display state, so the key always describes the colours on screen — the same promise the
/// panel's ramp card makes.</para>
///
/// <para>Swatches are meshes carrying the analysis's colours per vertex: the same mechanism that colours
/// the terrain, so a swatch prints exactly as the ground it keys. Outlines and text follow the Legend
/// layer. Everything is sized in multiples of the text height and drawn at exactly that height, for the
/// reason <see cref="TerrainReportTableBuilder"/> gives: baking binds text to the annotation style, and a
/// larger heading would preview large and bake at 1×.</para>
/// </summary>
internal static class TerrainLegendBuilder
{
    /// <summary>Glyph advance as a share of text height. The same deliberate overestimate the report
    /// table uses — see <see cref="TerrainReportTableBuilder"/>.</summary>
    private const double CharacterWidthRatio = 0.72;

    /// <summary>
    /// What the terrain is coloured by, as a key. Null with a reason when there is nothing honest to draw:
    /// nothing colours the terrain, its colours are hidden, the colouring has not landed yet, or the
    /// colouring has no scale (catchments paint each basin apart).
    /// </summary>
    public static TerrainLegendContent? ResolveContent(
        TerrainDefinition terrain,
        TerrainDisplayState displayState,
        UnitSystem unitSystem,
        LegendAnnotationDefinition legend,
        out string? reason)
    {
        reason = DescribeUnavailable(terrain);
        if (reason != null)
            return null;

        AnalysisDefinition coloring = TerrainAnalysisPreviewBuilder.FindColoringAnalysis(terrain)!;
        if (displayState.ActiveAnalysisId != coloring.Id)
        {
            reason = "The terrain has not been coloured yet — the key appears once it is.";
            return null;
        }

        AnalysisLegend? content = coloring switch
        {
            GradientComplianceAnalysisDefinition compliance => ComplianceKey(compliance.Rules),
            _ when displayState.ActiveAnalysisRange is { } range => AnalysisLegendBuilder.ForRamp(
                range,
                coloring.ColorMode,
                coloring.ColorInterval,
                coloring.ResolveRamp().Stops,
                AnalysisFormatting.GetValueFormatter(coloring)),
            _ => null
        };

        if (content == null)
        {
            reason = "The terrain has not been coloured yet — the key appears once it is.";
            return null;
        }

        string title = string.IsNullOrWhiteSpace(legend.Title)
            ? DefaultTitle(coloring, unitSystem)
            : legend.Title.Trim();
        return new TerrainLegendContent(content, title, coloring.Id);
    }

    /// <summary>
    /// Why a legend on this terrain would draw nothing, judged from the definition alone — the card's
    /// blocker. Null when there is a colouring to key.
    /// </summary>
    public static string? DescribeUnavailable(TerrainDefinition terrain)
    {
        AnalysisDefinition? coloring = TerrainAnalysisPreviewBuilder.FindColoringAnalysis(terrain);
        if (coloring == null)
            return "Nothing colours the terrain — enable a Slope, Elevation, Cut/Fill, Aspect, Ponding or " +
                "Gradient Compliance analysis for the legend to key.";

        if (!terrain.ShowAnalysisOutputs)
            return "Analysis colours are hidden (the Analysis tab's eye), so there is nothing on the terrain to key.";

        if (coloring is CatchmentAnalysisDefinition)
            return $"“{coloring.Label}” colours each basin apart, so there is no scale to key. Move a " +
                "ramp-coloured analysis above it to key that instead.";

        return null;
    }

    private static string DefaultTitle(AnalysisDefinition coloring, UnitSystem unitSystem)
    {
        string label = string.IsNullOrWhiteSpace(coloring.Label) ? "Analysis" : coloring.Label.Trim();
        return coloring switch
        {
            ElevationAnalysisDefinition or CutFillAnalysisDefinition or PondingAnalysisDefinition =>
                $"{label} ({ModelUnits.Abbreviation(unitSystem)})",
            AspectAnalysisDefinition => $"{label} (°)",
            // Slope values carry their unit in every label, so the heading does not repeat it.
            _ => label
        };
    }

    /// <summary>
    /// The compliance colours actually in use under these rules: an off rule paints nothing, so keying it
    /// would list a colour the terrain cannot show.
    /// </summary>
    private static AnalysisLegend ComplianceKey(GradientRuleSet rules)
    {
        static SlopeAnalyzer.ColorStop Stop((byte R, byte G, byte B) c) => new(0.0, c.R, c.G, c.B);

        bool warns = rules.LevelAreaMode == GradientRuleMode.Warn || rules.RouteMode == GradientRuleMode.Warn;
        bool reports = rules.LevelAreaMode == GradientRuleMode.Report || rules.RouteMode == GradientRuleMode.Report;
        bool routes = rules.RouteMode != GradientRuleMode.Off;

        var entries = new List<AnalysisLegendEntry>
        {
            new(Stop(GradientComplianceEvaluator.PassColor), "Within limit")
        };
        if (routes)
        {
            entries.Add(new(Stop(GradientComplianceEvaluator.RampColor), "Ramp"));
            entries.Add(new(Stop(GradientComplianceEvaluator.LandingColor), "Landing"));
        }

        if (reports)
            entries.Add(new(Stop(GradientComplianceEvaluator.ReportColor), warns ? "Over limit (reported)" : "Over limit"));
        if (warns)
            entries.Add(new(Stop(GradientComplianceEvaluator.WarnColor), "Over limit"));

        entries.Add(new(Stop(GradientComplianceEvaluator.UncheckedColor), "Not checked"));
        return AnalysisLegendBuilder.ForCategories(entries);
    }

    /// <summary>
    /// Lays the key out and returns its geometry. Built in a local frame whose origin is the key's top-left
    /// corner, then placed: at the picked origin, or beside the terrain with its foot level with the
    /// terrain's — the report table auto-places at the terrain's top, so the two do not collide.
    /// </summary>
    public static List<GeneratedRhinoObject> Build(
        LegendAnnotationDefinition legend,
        TerrainLegendContent content,
        BoundingBox terrainBounds,
        double textHeight,
        LayerRoleTable layerRoles)
    {
        ArgumentNullException.ThrowIfNull(legend);
        ArgumentNullException.ThrowIfNull(content);

        double h = textHeight > 0.0 && double.IsFinite(textHeight) ? textHeight : 1.0;
        var layout = new Layout(h, Math.Max(0.5, legend.SwatchSize) * h);

        double cursorY = 0.0;
        if (!string.IsNullOrWhiteSpace(content.Title))
        {
            layout.Text(0.0, cursorY - h, content.Title, TextJustification.BottomLeft);
            cursorY -= h * 2.0;
        }

        if (content.Legend.Style == AnalysisLegendStyle.Gradient)
        {
            double length = Math.Max(2.0, legend.GradientLength) * h;
            if (legend.Horizontal)
                LayoutGradientHorizontal(layout, content.Legend, cursorY, length);
            else
                LayoutGradientVertical(layout, content.Legend, cursorY, length);
        }
        else if (legend.Horizontal)
        {
            LayoutSwatchesHorizontal(layout, content.Legend.Entries, cursorY);
        }
        else
        {
            LayoutSwatchesVertical(layout, content.Legend.Entries, cursorY);
        }

        Plane plane = ResolvePlane(legend, terrainBounds, layout.MinY, h);
        string? layerPath = layerRoles.Path(LayerRole.Legend);
        return layout.Emit(legend, plane, layerPath);
    }

    private static void LayoutSwatchesVertical(Layout layout, IReadOnlyList<AnalysisLegendEntry> entries, double top)
    {
        double s = layout.SwatchSize;
        double pitch = s + (layout.TextHeight * 0.5);

        // Highest value at the top, as a key to a terrain reads: the same way up as an elevation scale.
        for (int i = 0; i < entries.Count; i++)
        {
            AnalysisLegendEntry entry = entries[entries.Count - 1 - i];
            double y = top - (i * pitch);
            layout.AddSwatch(0.0, y - s, s, y, entry.Color);
            layout.Text(s + (layout.TextHeight * 0.75), y - (s * 0.5), entry.Label, TextJustification.MiddleLeft);
        }
    }

    private static void LayoutSwatchesHorizontal(Layout layout, IReadOnlyList<AnalysisLegendEntry> entries, double top)
    {
        double s = layout.SwatchSize;
        double gap = layout.TextHeight;
        double x = 0.0;
        foreach (AnalysisLegendEntry entry in entries)
        {
            double width = Math.Max(s, EstimateWidth(entry.Label, layout.TextHeight));
            double centre = x + (width * 0.5);
            layout.AddSwatch(centre - (s * 0.5), top - s, centre + (s * 0.5), top, entry.Color);
            layout.Text(centre, top - s - (layout.TextHeight * 0.5), entry.Label, TextJustification.TopCenter);
            x += width + gap;
        }
    }

    private static void LayoutGradientVertical(Layout layout, AnalysisLegend legend, double top, double length)
    {
        double s = layout.SwatchSize;
        double bottom = top - length;
        layout.Strip(legend.Gradient, t => new Point2d(0.0, bottom + (t * length)), t => new Point2d(s, bottom + (t * length)));
        layout.Outline(0.0, bottom, s, top);

        double tick = layout.TextHeight * 0.4;
        foreach (AnalysisLegendTick mark in legend.Ticks)
        {
            double y = bottom + (Math.Clamp(mark.Position, 0.0, 1.0) * length);
            layout.Rule(s, y, s + tick, y);
            layout.Text(s + tick + (layout.TextHeight * 0.4), y, mark.Label, TextJustification.MiddleLeft);
        }
    }

    private static void LayoutGradientHorizontal(Layout layout, AnalysisLegend legend, double top, double length)
    {
        double s = layout.SwatchSize;
        layout.Strip(legend.Gradient, t => new Point2d(t * length, top - s), t => new Point2d(t * length, top));
        layout.Outline(0.0, top - s, length, top);

        double tick = layout.TextHeight * 0.4;
        foreach (AnalysisLegendTick mark in legend.Ticks)
        {
            double x = Math.Clamp(mark.Position, 0.0, 1.0) * length;
            layout.Rule(x, top - s, x, top - s - tick);
            layout.Text(x, top - s - tick - (layout.TextHeight * 0.3), mark.Label, TextJustification.TopCenter);
        }
    }

    private static double EstimateWidth(string? text, double textHeight) =>
        (text?.Length ?? 0) * textHeight * CharacterWidthRatio;

    private static Plane ResolvePlane(LegendAnnotationDefinition legend, BoundingBox terrainBounds, double minY, double textHeight)
    {
        if (legend.HasInsertionPlane)
        {
            return new Plane(
                new Point3d(legend.InsertionOriginX, legend.InsertionOriginY, legend.InsertionOriginZ),
                Vector3d.XAxis,
                Vector3d.YAxis);
        }

        if (!terrainBounds.IsValid)
            return Plane.WorldXY;

        double gap = Math.Max((terrainBounds.Max.X - terrainBounds.Min.X) * 0.05, textHeight * 4.0);
        return new Plane(
            new Point3d(terrainBounds.Max.X + gap, terrainBounds.Min.Y - minY, terrainBounds.Min.Z),
            Vector3d.XAxis,
            Vector3d.YAxis);
    }

    /// <summary>The key in its local frame, collected before placement so the auto position can align
    /// the key's measured foot with the terrain's.</summary>
    private sealed class Layout
    {
        private readonly List<Action<Plane, List<(GeometryBase, bool)>>> _emitters = new();

        public Layout(double textHeight, double swatch)
        {
            TextHeight = textHeight;
            SwatchSize = swatch;
        }

        public double TextHeight { get; }

        public double SwatchSize { get; }

        /// <summary>Lowest local Y reached, text included (estimated as one text height below its anchor
        /// line for text hanging from the top).</summary>
        public double MinY { get; private set; }

        private void Reach(double y) => MinY = Math.Min(MinY, y);

        public void Text(double x, double y, string text, TextJustification justification)
        {
            Reach(justification is TextJustification.TopCenter or TextJustification.TopLeft
                ? y - TextHeight
                : justification is TextJustification.MiddleLeft ? y - (TextHeight * 0.5) : y);
            _emitters.Add((plane, output) =>
            {
                var entity = new TextEntity
                {
                    Plane = new Plane(plane.PointAt(x, y), plane.XAxis, plane.YAxis),
                    PlainText = text,
                    TextHeight = TextHeight,
                    Justification = justification
                };
                output.Add((entity, false));
            });
        }

        public void AddSwatch(double x0, double y0, double x1, double y1, SlopeAnalyzer.ColorStop colour)
        {
            Reach(y0);
            _emitters.Add((plane, output) =>
            {
                var mesh = new RhinoMesh();
                mesh.Vertices.Add(plane.PointAt(x0, y0));
                mesh.Vertices.Add(plane.PointAt(x1, y0));
                mesh.Vertices.Add(plane.PointAt(x1, y1));
                mesh.Vertices.Add(plane.PointAt(x0, y1));
                mesh.Faces.AddFace(0, 1, 2, 3);
                Color c = Color.FromArgb(colour.R, colour.G, colour.B);
                for (int i = 0; i < 4; i++)
                    mesh.VertexColors.Add(c);
                MeshNormalOrientation.UnifyAndComputeNormals(mesh);
                output.Add((mesh, true));
            });
            Outline(x0, y0, x1, y1);
        }

        /// <summary>A gradient strip: one quad per pair of samples, coloured per vertex.</summary>
        public void Strip(
            IReadOnlyList<SlopeAnalyzer.ColorStop> samples,
            Func<double, Point2d> sideA,
            Func<double, Point2d> sideB)
        {
            if (samples.Count < 2)
                return;

            Reach(Math.Min(sideA(0.0).Y, sideB(0.0).Y));
            _emitters.Add((plane, output) =>
            {
                var mesh = new RhinoMesh();
                foreach (SlopeAnalyzer.ColorStop sample in samples)
                {
                    Point2d a = sideA(sample.Position);
                    Point2d b = sideB(sample.Position);
                    mesh.Vertices.Add(plane.PointAt(a.X, a.Y));
                    mesh.Vertices.Add(plane.PointAt(b.X, b.Y));
                    Color c = Color.FromArgb(sample.R, sample.G, sample.B);
                    mesh.VertexColors.Add(c);
                    mesh.VertexColors.Add(c);
                }

                for (int i = 0; i < samples.Count - 1; i++)
                {
                    int a = i * 2;
                    mesh.Faces.AddFace(a, a + 1, a + 3, a + 2);
                }

                MeshNormalOrientation.UnifyAndComputeNormals(mesh);
                output.Add((mesh, true));
            });
        }

        public void Outline(double x0, double y0, double x1, double y1)
        {
            Reach(Math.Min(y0, y1));
            _emitters.Add((plane, output) =>
            {
                var corners = new[]
                {
                    plane.PointAt(x0, y0), plane.PointAt(x1, y0), plane.PointAt(x1, y1),
                    plane.PointAt(x0, y1), plane.PointAt(x0, y0)
                };
                output.Add((new PolylineCurve(corners), false));
            });
        }

        public void Rule(double x0, double y0, double x1, double y1)
        {
            Reach(Math.Min(y0, y1));
            _emitters.Add((plane, output) =>
                output.Add((new LineCurve(plane.PointAt(x0, y0), plane.PointAt(x1, y1)), false)));
        }

        public List<GeneratedRhinoObject> Emit(LegendAnnotationDefinition legend, Plane plane, string? layerPath)
        {
            var geometry = new List<(GeometryBase Geometry, bool IsSwatch)>();
            foreach (var emit in _emitters)
                emit(plane, geometry);

            var objects = new List<GeneratedRhinoObject>(geometry.Count);
            foreach ((GeometryBase item, bool isSwatch) in geometry)
            {
                bool overridden = !isSwatch && legend.ColorArgb.HasValue;
                objects.Add(new GeneratedRhinoObject
                {
                    Role = LayerRole.Legend,
                    Geometry = item,
                    Name = $"{legend.Label} {(isSwatch ? "swatch" : item is TextEntity ? "text" : "outline")}",
                    AnalysisId = legend.Id,
                    ColorArgb = overridden ? legend.ColorArgb : null,
                    AppearanceSource = overridden ? GeneratedAppearanceSource.Object : GeneratedAppearanceSource.Layer,
                    LayerPath = layerPath,
                    // Swatches underneath, so outlines and labels sit on top in preview and bake alike.
                    DisplayOrder = isSwatch ? 0 : 1
                });
            }

            return objects;
        }
    }
}
