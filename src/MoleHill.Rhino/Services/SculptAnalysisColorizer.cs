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
    private readonly AnalysisRange _range;
    private readonly IReadOnlyList<AnalysisColorMapper.Band>? _bands;

    private readonly AnalysisColorMapper.Mode _colorMode;
    private readonly byte _alpha;

    private SculptAnalysisColorizer(
        Mode mode,
        SlopeAnalyzer.SlopeUnit unit,
        SlopeAnalyzer.ColorStop[] stops,
        AnalysisRange range,
        AnalysisColorMapper.Mode colorMode,
        double interval,
        byte alpha)
    {
        _mode = mode;
        _unit = unit;
        _stops = stops;
        _range = range;
        _colorMode = colorMode;
        _bands = AnalysisColorMapper.ResolveBandsFor(range, colorMode, interval, stops);
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
                var stops = slope.ResolveRamp().Stops.ToArray();
                AnalysisRange slopeRange = ResolveSlopeRange(vertices, faces, faceCount, slope);
                return new SculptAnalysisColorizer(
                    Mode.Slope, slope.Unit, stops, slopeRange, slope.ColorMode, slope.ColorInterval, alpha);
            }

            case ElevationAnalysisDefinition elevation:
            {
                var stops = elevation.ResolveRamp().Stops.ToArray();
                AnalysisRange elevationRange = ResolveElevationRange(vertices, faces, faceCount, elevation);
                return new SculptAnalysisColorizer(
                    Mode.Elevation, default, stops, elevationRange, elevation.ColorMode, elevation.ColorInterval, alpha);
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

        var mapped = AnalysisColorMapper.SampleResolved(value, _range, _colorMode, _bands, _stops);

        return System.Drawing.Color.FromArgb(_alpha, mapped.R, mapped.G, mapped.B);
    }

    /// <summary>
    /// The slope range to pin for this session, resolved exactly as
    /// <see cref="TerrainAnalysisPreviewBuilder"/> resolves it so the live colours match the baked
    /// preview the session replaces.
    /// </summary>
    private static AnalysisRange ResolveSlopeRange(
        double[] vertices,
        int[] faces,
        int faceCount,
        SlopeAnalysisDefinition slope)
    {
        if (!slope.AutoColorRange)
            return AnalysisRange.FromRequested(slope.RangeLow, slope.RangeHigh, RangeShape.FromZero);

        var summary = SlopeAnalyzer.Summarize(
            vertices, vertices.Length / 3, faces, faceCount, slope.Unit, autoRange: true);
        return summary.Range;
    }

    private static AnalysisRange ResolveElevationRange(
        double[] vertices,
        int[] faces,
        int faceCount,
        ElevationAnalysisDefinition elevation)
    {
        if (!elevation.AutoColorRange)
            return AnalysisRange.FromRequested(elevation.RangeLow, elevation.RangeHigh, RangeShape.MinMax);

        var values = new double[faceCount];
        var areas = new double[faceCount];
        for (int f = 0; f < faceCount; f++)
        {
            int i0 = faces[f * 3];
            int i1 = faces[f * 3 + 1];
            int i2 = faces[f * 3 + 2];
            values[f] = (vertices[i0 * 3 + 2] + vertices[i1 * 3 + 2] + vertices[i2 * 3 + 2]) / 3.0;

            double e1x = vertices[i1 * 3] - vertices[i0 * 3];
            double e1y = vertices[i1 * 3 + 1] - vertices[i0 * 3 + 1];
            double e1z = vertices[i1 * 3 + 2] - vertices[i0 * 3 + 2];
            double e2x = vertices[i2 * 3] - vertices[i0 * 3];
            double e2y = vertices[i2 * 3 + 1] - vertices[i0 * 3 + 1];
            double e2z = vertices[i2 * 3 + 2] - vertices[i0 * 3 + 2];
            double nx = (e1y * e2z) - (e1z * e2y);
            double ny = (e1z * e2x) - (e1x * e2z);
            double nz = (e1x * e2y) - (e1y * e2x);
            areas[f] = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz)) * 0.5;
        }

        return AnalysisRange.Resolve(
            values, areas, auto: true, elevation.RangeLow, elevation.RangeHigh, RangeShape.MinMax);
    }
}
