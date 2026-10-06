using System.Drawing;
using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Contour lines that follow the brush during a sculpt session. Built contours are build output and only
/// caught up in the rebuild after mouse-up; these are traced from the session's own vertex arrays, so
/// they are exact on every dab. Each enabled contour annotation gets an
/// <see cref="IncrementalContourTracer"/> at session start (one full pass), and each dab re-traces only
/// the faces whose normals it recomputed. While a session runs, the conduit draws these in place of the
/// annotation's built curves; its labels stay the built ones and move at the post-stroke rebuild.
/// </summary>
internal sealed class SculptLiveContours
{
    /// <summary>Extra levels traced beyond the terrain's range at session start, in intervals, so
    /// sculpting above the highest or below the lowest contour still draws new ones.</summary>
    private const int HeadroomIntervals = 200;

    private sealed class AnnotationTracer
    {
        public required IncrementalContourTracer Tracer { get; init; }
        public required int[] StyleOfLevel { get; init; }
        public int DrawnVersion { get; set; } = -1;
        public List<Line>[] LinesByStyle { get; set; } = Array.Empty<List<Line>>();
    }

    private readonly List<AnnotationTracer> _tracers = new();
    private readonly List<(Color Color, int Width)> _styles = new();
    private readonly HashSet<Guid> _annotationIds = new();

    /// <summary>Contour annotations whose built curves these lines replace while the session runs.</summary>
    public bool Replaces(Guid? analysisId) => analysisId.HasValue && _annotationIds.Contains(analysisId.Value);

    public void Bind(RhinoDoc doc, TerrainDefinition terrain, double[] vertices, int vertexCount, int[] faces, int faceCount)
    {
        Clear();
        double minZ = double.MaxValue, maxZ = double.MinValue;
        for (int i = 0; i < vertexCount; i++)
        {
            minZ = Math.Min(minZ, vertices[i * 3 + 2]);
            maxZ = Math.Max(maxZ, vertices[i * 3 + 2]);
        }

        if (vertexCount == 0)
            return;

        double tolerance = Math.Max(doc.ModelAbsoluteTolerance, double.Epsilon);
        LayerRoleTable roles = LayerRoleService.GetTable(doc, terrain);
        var styleIndex = new Dictionary<(int, int), int>();
        foreach (ContourAnnotationDefinition annotation in terrain.Annotations.OfType<ContourAnnotationDefinition>())
        {
            if (!annotation.IsEnabled)
                continue;

            double interval = Math.Max(annotation.Interval, tolerance);
            List<double> levels = TerrainBuildService.BuildContourLevels(
                minZ - HeadroomIntervals * interval, maxZ + HeadroomIntervals * interval, annotation.StartZ, interval);
            if (levels.Count == 0)
                continue;

            var tracer = new IncrementalContourTracer(vertices, faces, faceCount, levels);
            var styleOfLevel = new int[tracer.Levels.Count];
            for (int li = 0; li < styleOfLevel.Length; li++)
            {
                bool isMajor = TerrainBuildService.IsMajorContourLevel(tracer.Levels[li], annotation, tolerance);
                LayerRole role = TerrainBuildService.ResolveContourLevelRole(annotation, isMajor);
                LayerAppearance appearance = roles.Appearance(role);
                Color color = TerrainDisplayColors.Resolve(doc, roles.Path(role), null, annotation.ColorArgb, appearance);
                int width = appearance.ScalePreviewWidth(terrain.PreviewLineWeight);
                if (!styleIndex.TryGetValue((color.ToArgb(), width), out int style))
                {
                    style = _styles.Count;
                    _styles.Add((color, width));
                    styleIndex[(color.ToArgb(), width)] = style;
                }

                styleOfLevel[li] = style;
            }

            _tracers.Add(new AnnotationTracer { Tracer = tracer, StyleOfLevel = styleOfLevel });
            _annotationIds.Add(annotation.Id);
        }
    }

    /// <summary>Re-traces the faces a dab (or an undo) moved.</summary>
    public void Update(double[] vertices, IReadOnlyList<int> faces)
    {
        if (faces.Count == 0)
            return;

        foreach (AnnotationTracer tracer in _tracers)
            tracer.Tracer.Update(vertices, faces);
    }

    /// <summary>Draws the live lines; the caller sets depth state. Line buffers are rebuilt only after
    /// a change, not every frame.</summary>
    public void Draw(DisplayPipeline display)
    {
        foreach (AnnotationTracer tracer in _tracers)
        {
            if (tracer.DrawnVersion != tracer.Tracer.Version)
            {
                var lines = new List<Line>[_styles.Count];
                for (int s = 0; s < lines.Length; s++)
                    lines[s] = new List<Line>();
                foreach (ContourSegment segment in tracer.Tracer.Segments())
                {
                    lines[tracer.StyleOfLevel[segment.Level]].Add(new Line(
                        segment.Ax, segment.Ay, segment.Az, segment.Bx, segment.By, segment.Bz));
                }

                tracer.LinesByStyle = lines;
                tracer.DrawnVersion = tracer.Tracer.Version;
            }

            for (int s = 0; s < tracer.LinesByStyle.Length; s++)
            {
                if (tracer.LinesByStyle[s].Count > 0)
                    display.DrawLines(tracer.LinesByStyle[s], _styles[s].Color, _styles[s].Width);
            }
        }
    }

    public void Clear()
    {
        _tracers.Clear();
        _styles.Clear();
        _annotationIds.Clear();
    }
}
