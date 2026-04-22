using System.Threading.Tasks;
using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using Rhino;
using Rhino.Geometry;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

internal static class TerrainAnalysisPreviewBuilder
{
    private const int ParallelColorThreshold = 20_000;

    public static void UpdatePreviewMesh(RhinoDoc doc, TerrainDefinition terrain, TerrainDisplayState state)
    {
        state.ActiveAnalysisId = null;
        state.ActiveAnalysisLabel = null;

        if (state.TerrainMesh == null)
        {
            state.PreviewTerrainMesh = null;
            return;
        }

        if (!terrain.ShowAnalysisOutputs)
        {
            state.PreviewTerrainMesh = state.TerrainMesh;
            return;
        }

        AnalysisDefinition? activeAnalysis = terrain.Analyses.FirstOrDefault(analysis => analysis.IsEnabled && SupportsTerrainPreview(analysis));
        if (activeAnalysis == null)
        {
            state.PreviewTerrainMesh = state.TerrainMesh;
            return;
        }

        RhinoMesh? previewMesh = activeAnalysis switch
        {
            SlopeAnalysisDefinition slope => BuildSlopePreviewMesh(state.TerrainMesh, slope, GetAlpha(terrain.TerrainColorArgb)),
            ElevationAnalysisDefinition elevation => BuildElevationPreviewMesh(state.TerrainMesh, elevation, GetAlpha(terrain.TerrainColorArgb)),
            CutFillAnalysisDefinition cutFill => BuildCutFillPreviewMesh(doc, terrain, state, cutFill, GetAlpha(terrain.TerrainColorArgb)),
            _ => state.TerrainMesh
        };

        state.PreviewTerrainMesh = previewMesh ?? state.TerrainMesh;
        state.ActiveAnalysisId = activeAnalysis.Id;
        state.ActiveAnalysisLabel = activeAnalysis.Label;
    }

    private static RhinoMesh? BuildSlopePreviewMesh(RhinoMesh mesh, SlopeAnalysisDefinition analysis, byte alpha)
    {
        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out var faces, out _))
            return null;

        var palette = SlopePreviewPaletteCatalog.Resolve(analysis.PalettePreset);
        var slope = SlopeAnalyzer.Analyze(
            vertices,
            mesh.Vertices.Count,
            faces,
            mesh.Faces.Count,
            analysis.Unit,
            Math.Max(0.0, analysis.RangeLow),
            Math.Max(0.0, analysis.RangeHigh),
            palette.Stops);
        return BuildFaceColorMesh(vertices, faces, mesh.Faces.Count, slope.FaceColors, alpha);
    }

    internal static bool SupportsTerrainPreview(AnalysisDefinition analysis)
    {
        return analysis is SlopeAnalysisDefinition or ElevationAnalysisDefinition or CutFillAnalysisDefinition;
    }

    internal static bool ProducesGeneratedOutput(AnalysisDefinition analysis)
    {
        return analysis is ContourAnalysisDefinition
            or CurveElevationLabelAnalysisDefinition
            or CurveSlopeLabelAnalysisDefinition
            or ProjectedElevationLabelAnalysisDefinition
            or PointSlopeLabelAnalysisDefinition;
    }

    internal static bool ShouldDisplayGeneratedOutput(TerrainDefinition terrain, GeneratedRhinoObject generated)
    {
        if (generated.Kind == GeneratedObjectKind.SlopePreview)
            return terrain.ShowAnalysisOutputs && terrain.ShowSlopePreview;

        if (!generated.AnalysisId.HasValue)
            return true;

        if (!terrain.ShowAnalysisOutputs)
            return false;

        return terrain.Analyses.Any(analysis => analysis.Id == generated.AnalysisId.Value && analysis.IsEnabled);
    }

    private static RhinoMesh? BuildElevationPreviewMesh(RhinoMesh mesh, ElevationAnalysisDefinition analysis, byte alpha)
    {
        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out var faces, out _))
            return null;

        int faceCount = mesh.Faces.Count;
        var values = new double[faceCount];
        double min = double.MaxValue;
        double max = double.MinValue;
        if (faceCount >= ParallelColorThreshold)
        {
            object gate = new();
            Parallel.For<(double LocalMin, double LocalMax)>(0, faceCount,
                () => (double.MaxValue, double.MinValue),
                (faceIndex, _, local) =>
                {
                    int a = faces[faceIndex * 3];
                    int b = faces[faceIndex * 3 + 1];
                    int c = faces[faceIndex * 3 + 2];
                    double value =
                        (vertices[a * 3 + 2] +
                         vertices[b * 3 + 2] +
                         vertices[c * 3 + 2]) / 3.0;
                    values[faceIndex] = value;
                    local.LocalMin = Math.Min(local.LocalMin, value);
                    local.LocalMax = Math.Max(local.LocalMax, value);
                    return local;
                },
                local =>
                {
                    lock (gate)
                    {
                        min = Math.Min(min, local.LocalMin);
                        max = Math.Max(max, local.LocalMax);
                    }
                });
        }
        else
        {
            for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
            {
                int a = faces[faceIndex * 3];
                int b = faces[faceIndex * 3 + 1];
                int c = faces[faceIndex * 3 + 2];
                double value =
                    (vertices[a * 3 + 2] +
                     vertices[b * 3 + 2] +
                     vertices[c * 3 + 2]) / 3.0;
                values[faceIndex] = value;
                min = Math.Min(min, value);
                max = Math.Max(max, value);
            }
        }

        if (min == double.MaxValue)
            min = 0.0;
        if (max == double.MinValue)
            max = 0.0;

        double low = analysis.RangeLow;
        double high = analysis.RangeHigh > low ? analysis.RangeHigh : max;
        if (high <= low)
        {
            low = min;
            high = max > min ? max : min + 1.0;
        }

        byte[] colors = BuildFaceColors(values, low, high, SlopePreviewPaletteCatalog.Resolve(analysis.PalettePreset).Stops);
        return BuildFaceColorMesh(vertices, faces, faceCount, colors, alpha);
    }

    private static RhinoMesh? BuildCutFillPreviewMesh(RhinoDoc doc, TerrainDefinition terrain, TerrainDisplayState state, CutFillAnalysisDefinition analysis, byte alpha)
    {
        RhinoMesh? terrainMesh = state.TerrainMesh;
        if (terrainMesh == null || !RhinoGeometryConversions.TryExtractMeshData(terrainMesh, out var vertices, out var faces, out _))
            return null;

        RhinoMesh? referenceMesh = ResolveReferenceMesh(doc, analysis.Reference) ?? state.BaseTerrainMesh;
        if (referenceMesh == null)
            return terrainMesh;

        var boundaries = RhinoSourceResolver.ResolveCurves(doc, analysis.Boundary);
        int faceCount = terrainMesh.Faces.Count;
        var values = new double[faceCount];
        var colors = new byte[faceCount * 3];
        double maxAbs = 0.0;

        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            int a = faces[faceIndex * 3];
            int b = faces[faceIndex * 3 + 1];
            int c = faces[faceIndex * 3 + 2];

            var pa = new Point3d(vertices[a * 3], vertices[a * 3 + 1], vertices[a * 3 + 2]);
            var pb = new Point3d(vertices[b * 3], vertices[b * 3 + 1], vertices[b * 3 + 2]);
            var pc = new Point3d(vertices[c * 3], vertices[c * 3 + 1], vertices[c * 3 + 2]);
            var centroid = new Point3d(
                (pa.X + pb.X + pc.X) / 3.0,
                (pa.Y + pb.Y + pc.Y) / 3.0,
                (pa.Z + pb.Z + pc.Z) / 3.0);

            if (!IsInsideBoundaries(centroid, boundaries))
            {
                WriteColor(colors, faceIndex, 130, 130, 130);
                continue;
            }

            var referencePoint = referenceMesh.ClosestMeshPoint(centroid, 0.0);
            if (referencePoint == null)
            {
                WriteColor(colors, faceIndex, 130, 130, 130);
                continue;
            }

            double delta = centroid.Z - referenceMesh.PointAt(referencePoint).Z;
            values[faceIndex] = delta;
            maxAbs = Math.Max(maxAbs, Math.Abs(delta));
        }

        double low = analysis.RangeLow;
        double high = analysis.RangeHigh;
        if (high <= low)
        {
            double effective = maxAbs > 0.0 ? maxAbs : 1.0;
            low = -effective;
            high = effective;
        }

        var palette = SlopePreviewPaletteCatalog.Resolve(analysis.PalettePreset).Stops;
        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            if (colors[faceIndex * 3] == 130 &&
                colors[faceIndex * 3 + 1] == 130 &&
                colors[faceIndex * 3 + 2] == 130)
                continue;

            SamplePaletteColor(values[faceIndex], low, high, palette, out byte r, out byte g, out byte b);
            WriteColor(colors, faceIndex, r, g, b);
        }

        return BuildFaceColorMesh(vertices, faces, faceCount, colors, alpha);
    }

    private static RhinoMesh? ResolveReferenceMesh(RhinoDoc doc, SourceReferenceSet referenceSet)
    {
        var meshes = RhinoSourceResolver.ResolveMeshes(doc, referenceSet);
        if (meshes.Count == 0)
            return null;
        if (meshes.Count == 1)
            return meshes[0];

        var combined = new RhinoMesh();
        foreach (var mesh in meshes)
            combined.Append(mesh);
        RhinoGeometryConversions.NormalizeMeshInPlace(combined);
        return combined;
    }

    private static bool IsInsideBoundaries(Point3d point, IReadOnlyList<Curve> boundaries)
    {
        if (boundaries.Count == 0)
            return true;

        double tolerance = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 1e-6;
        foreach (var curve in boundaries)
        {
            var containment = curve.Contains(new Point3d(point.X, point.Y, curve.PointAtStart.Z), Plane.WorldXY, tolerance);
            if (containment == PointContainment.Inside || containment == PointContainment.Coincident)
                return true;
        }

        return false;
    }

    private static RhinoMesh BuildFaceColorMesh(double[] vertices, int[] faces, int faceCount, byte[] colors, byte alpha)
    {
        var coloredMesh = new RhinoMesh();
        coloredMesh.Vertices.Capacity = faceCount * 3;
        coloredMesh.Faces.Capacity = faceCount;
        coloredMesh.VertexColors.Capacity = faceCount * 3;

        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            int i0 = faces[faceIndex * 3];
            int i1 = faces[faceIndex * 3 + 1];
            int i2 = faces[faceIndex * 3 + 2];
            int vertexIndex = coloredMesh.Vertices.Count;

            coloredMesh.Vertices.Add(vertices[i0 * 3], vertices[i0 * 3 + 1], vertices[i0 * 3 + 2]);
            coloredMesh.Vertices.Add(vertices[i1 * 3], vertices[i1 * 3 + 1], vertices[i1 * 3 + 2]);
            coloredMesh.Vertices.Add(vertices[i2 * 3], vertices[i2 * 3 + 1], vertices[i2 * 3 + 2]);
            coloredMesh.Faces.AddFace(vertexIndex, vertexIndex + 1, vertexIndex + 2);

            var color = System.Drawing.Color.FromArgb(
                alpha,
                colors[faceIndex * 3],
                colors[faceIndex * 3 + 1],
                colors[faceIndex * 3 + 2]);
            coloredMesh.VertexColors.Add(color);
            coloredMesh.VertexColors.Add(color);
            coloredMesh.VertexColors.Add(color);
        }

        coloredMesh.Normals.ComputeNormals();
        coloredMesh.UnifyNormals();
        coloredMesh.Compact();
        return coloredMesh;
    }

    private static byte GetAlpha(int argb)
    {
        return System.Drawing.Color.FromArgb(argb).A;
    }

    private static byte[] BuildFaceColors(double[] values, double low, double high, IReadOnlyList<SlopeAnalyzer.ColorStop> palette)
    {
        var colors = new byte[values.Length * 3];
        if (values.Length >= ParallelColorThreshold)
        {
            Parallel.For(0, values.Length, index =>
            {
                SamplePaletteColor(values[index], low, high, palette, out byte r, out byte g, out byte b);
                WriteColor(colors, index, r, g, b);
            });
            return colors;
        }

        for (int index = 0; index < values.Length; index++)
        {
            SamplePaletteColor(values[index], low, high, palette, out byte r, out byte g, out byte b);
            WriteColor(colors, index, r, g, b);
        }

        return colors;
    }

    private static void SamplePaletteColor(
        double value,
        double low,
        double high,
        IReadOnlyList<SlopeAnalyzer.ColorStop> palette,
        out byte r,
        out byte g,
        out byte b)
    {
        if (palette.Count == 0)
        {
            r = 180;
            g = 180;
            b = 180;
            return;
        }

        if (high <= low)
            high = low + 1.0;

        if (double.IsNaN(value) || double.IsInfinity(value) || value <= low)
        {
            var first = palette[0];
            r = first.R;
            g = first.G;
            b = first.B;
            return;
        }

        if (value >= high)
        {
            var last = palette[^1];
            r = last.R;
            g = last.G;
            b = last.B;
            return;
        }

        double t = (value - low) / (high - low);
        var previous = palette[0];
        for (int index = 1; index < palette.Count; index++)
        {
            var current = palette[index];
            if (t > current.Position)
            {
                previous = current;
                continue;
            }

            double segment = current.Position - previous.Position;
            if (segment <= 1e-9)
            {
                r = current.R;
                g = current.G;
                b = current.B;
                return;
            }

            double localT = (t - previous.Position) / segment;
            r = Interpolate(previous.R, current.R, localT);
            g = Interpolate(previous.G, current.G, localT);
            b = Interpolate(previous.B, current.B, localT);
            return;
        }

        var fallback = palette[^1];
        r = fallback.R;
        g = fallback.G;
        b = fallback.B;
    }

    private static byte Interpolate(byte a, byte b, double t)
    {
        double clamped = Math.Clamp(t, 0.0, 1.0);
        return (byte)Math.Round(a + ((b - a) * clamped));
    }

    private static void WriteColor(byte[] colors, int faceIndex, byte r, byte g, byte b)
    {
        colors[faceIndex * 3] = r;
        colors[faceIndex * 3 + 1] = g;
        colors[faceIndex * 3 + 2] = b;
    }
}
