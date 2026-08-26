using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Live analysis coloring for the sculpt session's working mesh: when the terrain shows a slope or
/// elevation preview, the working mesh carries vertex colors and every dab recolors just the
/// vertices it touched, so the analysis reads in real time while sculpting. Values come from data
/// the session already keeps fresh per dab (vertex normals via <see cref="SculptNormalPatcher"/>,
/// vertex Z), and the color range is pinned at session start so colors don't rescale mid-stroke.
/// Per-vertex coloring shades smoothly rather than the baked preview's crisp per-face look; the
/// canonical rebuild at session end restores the baked mesh. Cut/fill previews are not colored
/// live — they need reference-mesh projection per sample.
/// </summary>
internal sealed class SculptAnalysisColorizer
{
    private enum Mode { Slope, Elevation }

    private readonly Mode _mode;
    private readonly SlopeAnalyzer.SlopeUnit _unit;
    private readonly SlopeAnalyzer.ColorStop[] _stops;
    private readonly double _low;
    private readonly double _high;
    private readonly AnalysisColorMapper.Mode _colorMode;
    private readonly double _interval;
    private readonly byte _alpha;

    private SculptAnalysisColorizer(
        Mode mode,
        SlopeAnalyzer.SlopeUnit unit,
        SlopeAnalyzer.ColorStop[] stops,
        double low,
        double high,
        AnalysisColorMapper.Mode colorMode,
        double interval,
        byte alpha)
    {
        _mode = mode;
        _unit = unit;
        _stops = stops;
        _low = low;
        _high = high;
        _colorMode = colorMode;
        _interval = interval;
        _alpha = alpha;
    }

    /// <summary>Null when the terrain has no live-colorable analysis preview (hidden outputs, no
    /// enabled slope/elevation analysis, or a cut/fill preview) — the session then shows the plain
    /// working mesh exactly as before.</summary>
    public static SculptAnalysisColorizer? TryCreate(
        TerrainDefinition terrain,
        double[] vertices,
        int[] faces,
        int faceCount)
    {
        if (!terrain.ShowAnalysisOutputs)
            return null;

        AnalysisDefinition? active = terrain.Analyses.FirstOrDefault(
            analysis => analysis.IsEnabled && TerrainAnalysisPreviewBuilder.SupportsTerrainPreview(analysis));
        byte alpha = System.Drawing.Color.FromArgb(terrain.TerrainColorArgb).A;

        switch (active)
        {
            case SlopeAnalysisDefinition slope:
            {
                var stops = SlopePreviewPaletteCatalog.Resolve(slope.PalettePreset).Stops;
                double low = slope.AutoColorRange ? 0.0 : Math.Max(0.0, slope.RangeLow);
                double high = !slope.AutoColorRange && slope.RangeHigh > low
                    ? slope.RangeHigh
                    : MaxFiniteFaceSlope(vertices, faces, faceCount, slope.Unit);
                double interval = AnalysisColorMapper.ResolveInterval(low, high, slope.ColorInterval);
                return new SculptAnalysisColorizer(Mode.Slope, slope.Unit, stops, low, high, slope.ColorMode, interval, alpha);
            }

            case ElevationAnalysisDefinition elevation:
            {
                var stops = SlopePreviewPaletteCatalog.Resolve(elevation.PalettePreset).Stops;
                FaceAverageZRange(vertices, faces, faceCount, out double min, out double max);
                double low = elevation.AutoColorRange ? min : elevation.RangeLow;
                double high = elevation.AutoColorRange ? max : elevation.RangeHigh;
                if (high <= low)
                {
                    low = min;
                    high = max > min ? max : min + 1.0;
                }

                double interval = AnalysisColorMapper.ResolveInterval(low, high, elevation.ColorInterval);
                return new SculptAnalysisColorizer(Mode.Elevation, default, stops, low, high, elevation.ColorMode, interval, alpha);
            }

            default:
                return null;
        }
    }

    /// <summary>Colors every vertex — call once at bind and again whenever DynTopo rebuilds the
    /// working mesh (the colorizer holds no per-topology state, only the pinned range).</summary>
    public void ColorAll(RhinoMesh mesh)
    {
        int count = mesh.Vertices.Count;
        var colors = new System.Drawing.Color[count];
        for (int i = 0; i < count; i++)
            colors[i] = ColorAt(mesh, i);
        mesh.VertexColors.SetColors(colors);
    }

    /// <summary>Recolors a subset — the vertices whose normals the last dab refreshed.</summary>
    public void Recolor(RhinoMesh mesh, IReadOnlyList<int> vertices)
    {
        foreach (int i in vertices)
            mesh.VertexColors.SetColor(i, ColorAt(mesh, i));
    }

    private System.Drawing.Color ColorAt(RhinoMesh mesh, int vertexIndex)
    {
        double value;
        if (_mode == Mode.Slope)
        {
            var normal = mesh.Normals[vertexIndex];
            double horizontal = Math.Sqrt((double)normal.X * normal.X + (double)normal.Y * normal.Y);
            double absNz = Math.Abs((double)normal.Z);
            double ratio = absNz < 1e-12 ? double.PositiveInfinity : horizontal / absNz;
            value = SlopeAnalyzer.ConvertRatioToUnit(ratio, _unit);
        }
        else
        {
            value = mesh.Vertices[vertexIndex].Z;
        }

        var mapped = AnalysisColorMapper.Sample(value, _low, _high, _colorMode, _interval, _stops);
        byte r = mapped.R;
        byte g = mapped.G;
        byte b = mapped.B;

        return System.Drawing.Color.FromArgb(_alpha, r, g, b);
    }

    private static double MaxFiniteFaceSlope(double[] vertices, int[] faces, int faceCount, SlopeAnalyzer.SlopeUnit unit)
    {
        double maxRatio = 0.0;
        bool any = false;
        for (int f = 0; f < faceCount; f++)
        {
            int i0 = faces[f * 3];
            int i1 = faces[f * 3 + 1];
            int i2 = faces[f * 3 + 2];

            double e1x = vertices[i1 * 3] - vertices[i0 * 3];
            double e1y = vertices[i1 * 3 + 1] - vertices[i0 * 3 + 1];
            double e1z = vertices[i1 * 3 + 2] - vertices[i0 * 3 + 2];
            double e2x = vertices[i2 * 3] - vertices[i0 * 3];
            double e2y = vertices[i2 * 3 + 1] - vertices[i0 * 3 + 1];
            double e2z = vertices[i2 * 3 + 2] - vertices[i0 * 3 + 2];

            double nx = e1y * e2z - e1z * e2y;
            double ny = e1z * e2x - e1x * e2z;
            double nz = e1x * e2y - e1y * e2x;

            double absNz = Math.Abs(nz);
            if (absNz < 1e-12)
                continue;

            double ratio = Math.Sqrt(nx * nx + ny * ny) / absNz;
            maxRatio = Math.Max(maxRatio, ratio);
            any = true;
        }

        return any ? SlopeAnalyzer.ConvertRatioToUnit(maxRatio, unit) : 0.0;
    }

    private static void FaceAverageZRange(double[] vertices, int[] faces, int faceCount, out double min, out double max)
    {
        min = double.MaxValue;
        max = double.MinValue;
        for (int f = 0; f < faceCount; f++)
        {
            double average =
                (vertices[faces[f * 3] * 3 + 2] +
                 vertices[faces[f * 3 + 1] * 3 + 2] +
                 vertices[faces[f * 3 + 2] * 3 + 2]) / 3.0;
            min = Math.Min(min, average);
            max = Math.Max(max, average);
        }

        if (min == double.MaxValue)
            min = 0.0;
        if (max == double.MinValue)
            max = 0.0;
    }
}
