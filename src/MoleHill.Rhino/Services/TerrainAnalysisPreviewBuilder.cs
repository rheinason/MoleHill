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

    /// <summary>Bars in the card's histogram strip. Enough to show a distribution's shape at the width of
    /// a docked panel, and few enough that each bar is still more than a hairline.</summary>
    internal const int HistogramBars = 44;

    /// <summary>Faces with no comparable reference (outside the boundary, or over a hole in the reference
    /// mesh) are drawn in this neutral grey.</summary>
    private static readonly SlopeAnalyzer.ColorStop UnmappedColor = new(0.0, 130, 130, 130);

    public static void UpdatePreviewMesh(
        RhinoDoc doc,
        TerrainDefinition terrain,
        TerrainDisplayState state,
        Func<Guid, RhinoMesh?>? resolveReferenceTerrainMesh = null)
    {
        state.ActiveAnalysisId = null;
        state.ActiveAnalysisLabel = null;
        state.ActiveAnalysisRange = null;
        state.ActiveAnalysisDistribution = null;

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

        byte alpha = GetAlpha(terrain.TerrainColorArgb);
        AnalysisRange? resolvedRange = null;
        double[]? distribution = null;
        RhinoMesh? previewMesh = activeAnalysis switch
        {
            SlopeAnalysisDefinition slope => BuildSlopePreviewMesh(state.TerrainMesh, slope, alpha, out resolvedRange, out distribution),
            AspectAnalysisDefinition aspect => BuildAspectPreviewMesh(doc, state.TerrainMesh, aspect, alpha, out resolvedRange, out distribution),
            ElevationAnalysisDefinition elevation => BuildElevationPreviewMesh(state.TerrainMesh, elevation, alpha, out resolvedRange, out distribution),
            CutFillAnalysisDefinition cutFill => BuildCutFillPreviewMesh(doc, terrain, state, cutFill, alpha, resolveReferenceTerrainMesh, out resolvedRange, out distribution),
            CatchmentAnalysisDefinition catchment => BuildCatchmentPreviewMesh(state.TerrainMesh, catchment, alpha),
            _ => state.TerrainMesh
        };

        state.PreviewTerrainMesh = previewMesh ?? state.TerrainMesh;
        state.ActiveAnalysisId = activeAnalysis.Id;
        state.ActiveAnalysisLabel = activeAnalysis.Label;

        // The legend reads this back, so it always describes the mesh currently on screen — colour edits
        // recolour without a rebuild, and the last build's summary would otherwise be stale.
        state.ActiveAnalysisRange = resolvedRange;
        state.ActiveAnalysisDistribution = distribution;
    }

    /// <summary>The range an analysis maps across its palette, resolved the same way the preview mesh
    /// resolves it. Used by the panel to label the legend without forcing a rebuild.</summary>
    public static RangeShape GetRangeShape(AnalysisDefinition analysis) => analysis switch
    {
        SlopeAnalysisDefinition => RangeShape.FromZero,
        AspectAnalysisDefinition => RangeShape.Cyclic,
        CutFillAnalysisDefinition => RangeShape.SymmetricAboutZero,
        _ => RangeShape.MinMax
    };

    private static RhinoMesh? BuildSlopePreviewMesh(
        RhinoMesh mesh,
        SlopeAnalysisDefinition analysis,
        byte alpha,
        out AnalysisRange? range,
        out double[]? distribution)
    {
        range = null;
        distribution = null;
        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out var faces, out _))
            return null;

        var palette = analysis.ResolveRamp();
        var slope = SlopeAnalyzer.Analyze(
            vertices,
            mesh.Vertices.Count,
            faces,
            mesh.Faces.Count,
            analysis.Unit,
            analysis.AutoColorRange,
            analysis.RangeLow,
            analysis.RangeHigh,
            palette.Stops,
            analysis.ColorMode,
            analysis.ColorInterval);

        range = slope.Range;
        distribution = BuildDistribution(slope.Slopes, ReadOnlySpan<double>.Empty);
        return BuildFaceColorMesh(vertices, faces, mesh.Faces.Count, slope.FaceColors, alpha);
    }

    /// <summary>
    /// Aspect colours the terrain by the compass direction each face drains towards.
    ///
    /// Unlike the slope and elevation builders beside it, this pairs the extracted arrays with the counts
    /// the SAME extraction returned. <c>TryExtractMeshData</c> normalizes a copy of the mesh, so its arrays
    /// routinely describe fewer vertices and faces than <c>mesh.Faces.Count</c> reports, and pairing the two
    /// reads off the end of the array on a worker thread. See CLAUDE.md — the neighbours here still pair the
    /// old way and are not a template.
    /// </summary>
    private static RhinoMesh? BuildAspectPreviewMesh(
        RhinoDoc doc,
        RhinoMesh mesh,
        AspectAnalysisDefinition analysis,
        byte alpha,
        out AnalysisRange? range,
        out double[]? distribution)
    {
        range = null;
        distribution = null;
        if (!RhinoGeometryConversions.TryExtractMeshData(
                mesh, out var vertices, out int vertexCount, out var faces, out int faceCount, out _))
            return null;

        var aspect = AspectAnalyzer.Analyze(
            vertices,
            vertexCount,
            faces,
            faceCount,
            DocumentNorth.AzimuthDegrees(doc),
            SlopeAnalyzer.ConvertUnitToRatio(analysis.FlatSlopeThresholdDegrees, SlopeAnalyzer.SlopeUnit.Degrees),
            analysis.ResolveRamp().Stops,
            analysis.ColorMode,
            analysis.ColorInterval,
            UnmappedColor);

        range = aspect.Range;

        // Flat faces carry NaN, which the histogram ignores outright, so the rose shows the directions that
        // exist rather than a spike at north for every level face.
        distribution = BuildDistribution(aspect.Bearings, aspect.PlanAreas);
        return BuildFaceColorMesh(vertices, faces, faceCount, aspect.FaceColors, alpha);
    }

    /// <summary>
    /// Catchments colour the terrain by which outlet each face drains to.
    /// </summary>
    /// <remarks>
    /// It writes no range and no distribution, and that is correct rather than unfinished: both describe
    /// where a value sits on a continuum, and a catchment index is a name, not a magnitude. The card
    /// draws no ramp and no histogram, because there is nothing for either to be about. Colours come from
    /// <see cref="CategoricalPalette"/>, whose job is that two adjacent catchments never look alike.
    /// </remarks>
    private static RhinoMesh? BuildCatchmentPreviewMesh(
        RhinoMesh mesh,
        CatchmentAnalysisDefinition analysis,
        byte alpha)
    {
        if (!RhinoGeometryConversions.TryExtractMeshData(
                mesh, out var vertices, out int vertexCount, out var faces, out int faceCount, out _))
            return null;

        BasinGraph graph = DrainageBasinAnalyzer.Analyze(
            vertices,
            vertexCount,
            faces,
            faceCount,
            new DrainageBasinAnalyzer.Options
            {
                FlatSlopeRatio = SlopeAnalyzer.ConvertUnitToRatio(
                    analysis.FlatSlopeThresholdDegrees, SlopeAnalyzer.SlopeUnit.Degrees),
                MinimumBasinAreaShare = Math.Clamp(analysis.MinimumBasinAreaPercent, 0.0, 100.0) / 100.0
            });

        return BuildFaceColorMesh(
            vertices, faces, faceCount, TerrainBuildService.BuildCatchmentFaceColors(graph), alpha);
    }

    internal static bool SupportsTerrainPreview(AnalysisDefinition analysis)
    {
        return analysis is SlopeAnalysisDefinition or AspectAnalysisDefinition or ElevationAnalysisDefinition
            or CutFillAnalysisDefinition or CatchmentAnalysisDefinition;
    }

    /// <summary>
    /// Whether this content emits geometry into the drawing (as opposed to only colouring the terrain
    /// mesh or reporting a number). Every annotation does, by definition; on the analysis side waterflow
    /// always does, and cut/fill does when either of its drawn outputs is switched on — which is what
    /// makes an edit to the layer template invalidate its cached curves.
    /// </summary>
    internal static bool ProducesGeneratedOutput(ITerrainContentItem item)
    {
        return item is AnnotationDefinition or WaterflowAnalysisDefinition
            or CutFillAnalysisDefinition { DrawsDeltaOutput: true }
            or CatchmentAnalysisDefinition { ShowBoundaries: true }
            or CatchmentAnalysisDefinition { ShowFlowPaths: true };
    }

    internal static bool ShouldDisplayGeneratedOutput(TerrainDefinition terrain, GeneratedRhinoObject generated)
    {
        if (generated.Kind == GeneratedObjectKind.SlopePreview)
            return terrain.ShowAnalysisOutputs && terrain.ShowSlopePreview;

        if (!generated.AnalysisId.HasValue)
            return true;

        // Each family answers for its own output. Before schema 31 both went through
        // ShowAnalysisOutputs, so turning off slope colours also silently hid every label and section.
        Guid ownerId = generated.AnalysisId.Value;

        AnalysisDefinition? analysis = terrain.Analyses.FirstOrDefault(item => item.Id == ownerId);
        if (analysis != null)
            return terrain.ShowAnalysisOutputs && analysis.IsEnabled;

        // Annotations carry no terrain-level visibility flag: they are the drawing, so the card's own
        // enabled state is the whole of it.
        AnnotationDefinition? annotation = terrain.Annotations.FirstOrDefault(item => item.Id == ownerId);
        if (annotation != null)
            return annotation.IsEnabled;

        return false;
    }

    private static RhinoMesh? BuildElevationPreviewMesh(
        RhinoMesh mesh,
        ElevationAnalysisDefinition analysis,
        byte alpha,
        out AnalysisRange? range,
        out double[]? distribution)
    {
        range = null;
        distribution = null;
        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out var faces, out _))
            return null;

        int faceCount = mesh.Faces.Count;
        var values = new double[faceCount];
        var areas = new double[faceCount];
        if (faceCount >= ParallelColorThreshold)
            Parallel.For(0, faceCount, faceIndex => MeasureFace(vertices, faces, faceIndex, values, areas));
        else
            for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
                MeasureFace(vertices, faces, faceIndex, values, areas);

        AnalysisRange resolved = AnalysisRange.Resolve(
            values, areas, analysis.AutoColorRange, analysis.RangeLow, analysis.RangeHigh, RangeShape.MinMax);
        range = resolved;

        distribution = BuildDistribution(values, areas);

        var palette = analysis.ResolveRamp().Stops;
        byte[] colors = BuildMappedColors(values, resolved, analysis.ColorMode, analysis.ColorInterval, palette);
        return BuildFaceColorMesh(vertices, faces, faceCount, colors, alpha);
    }

    /// <summary>
    /// Face-average elevation and the face's <em>plan</em> (XY-projected) area, the two inputs a weighted
    /// range fit needs. Plan area, not 3D area: these maps are read in plan, so a near-vertical face should
    /// carry the weight of the ground it covers, which is almost none.
    /// </summary>
    private static void MeasureFace(double[] vertices, int[] faces, int faceIndex, double[] values, double[] planAreas)
    {
        int a = faces[faceIndex * 3];
        int b = faces[faceIndex * 3 + 1];
        int c = faces[faceIndex * 3 + 2];

        values[faceIndex] = (vertices[a * 3 + 2] + vertices[b * 3 + 2] + vertices[c * 3 + 2]) / 3.0;

        double e1x = vertices[b * 3] - vertices[a * 3];
        double e1y = vertices[b * 3 + 1] - vertices[a * 3 + 1];
        double e1z = vertices[b * 3 + 2] - vertices[a * 3 + 2];
        double e2x = vertices[c * 3] - vertices[a * 3];
        double e2y = vertices[c * 3 + 1] - vertices[a * 3 + 1];
        double e2z = vertices[c * 3 + 2] - vertices[a * 3 + 2];
        double nx = (e1y * e2z) - (e1z * e2y);
        double ny = (e1z * e2x) - (e1x * e2z);
        double nz = (e1x * e2y) - (e1y * e2x);
        planAreas[faceIndex] = Math.Abs(nz) * 0.5;
    }

    private static RhinoMesh? BuildCutFillPreviewMesh(
        RhinoDoc doc,
        TerrainDefinition terrain,
        TerrainDisplayState state,
        CutFillAnalysisDefinition analysis,
        byte alpha,
        Func<Guid, RhinoMesh?>? resolveReferenceTerrainMesh,
        out AnalysisRange? range,
        out double[]? distribution)
    {
        range = null;
        distribution = null;
        RhinoMesh? terrainMesh = state.TerrainMesh;
        if (terrainMesh == null || !RhinoGeometryConversions.TryExtractMeshData(terrainMesh, out var vertices, out var faces, out _))
            return null;

        RhinoMesh? referenceTerrainMesh = analysis.ReferenceTerrainId is { } referenceTerrainId
            ? resolveReferenceTerrainMesh?.Invoke(referenceTerrainId)
            : null;
        RhinoMesh? referenceMesh = ResolveReferenceMesh(doc, analysis.Reference) ?? referenceTerrainMesh ?? state.BaseTerrainMesh;
        if (referenceMesh == null)
            return terrainMesh;

        MeshHeightProjector? referenceProjector = CreateReferenceProjector(referenceMesh);
        var boundaries = RhinoSourceResolver.ResolveCurves(doc, analysis.Boundary);
        int faceCount = terrainMesh.Faces.Count;
        var values = new double[faceCount];
        var areas = new double[faceCount];

        // An explicit flag rather than a sentinel colour: the old code re-detected "unmapped" by testing
        // for RGB 130,130,130, which a palette is free to produce.
        var mapped = new bool[faceCount];

        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            MeasureFace(vertices, faces, faceIndex, values, areas);
            double centroidZ = values[faceIndex];
            int a = faces[faceIndex * 3];
            int b = faces[faceIndex * 3 + 1];
            int c = faces[faceIndex * 3 + 2];
            var centroid = new Point3d(
                (vertices[a * 3] + vertices[b * 3] + vertices[c * 3]) / 3.0,
                (vertices[a * 3 + 1] + vertices[b * 3 + 1] + vertices[c * 3 + 1]) / 3.0,
                centroidZ);

            values[faceIndex] = 0.0;
            if (!IsInsideBoundaries(centroid, boundaries))
                continue;

            if (!TryProjectReferencePoint(
                    referenceMesh,
                    referenceProjector,
                    centroid,
                    doc.ModelAbsoluteTolerance,
                    out Point3d referencePoint))
                continue;

            values[faceIndex] = centroid.Z - referencePoint.Z;
            mapped[faceIndex] = true;
        }

        // Only comparable faces may influence the fitted range.
        AnalysisRange resolved = ResolveMaskedRange(
            values, areas, mapped, analysis.AutoColorRange, analysis.RangeLow, analysis.RangeHigh, RangeShape.SymmetricAboutZero);
        range = resolved;

        // Only the comparable faces belong in the histogram too — an unmapped face has no depth, and
        // counting it as zero would put a spike at "no change" that isn't in the data.
        distribution = BuildMaskedDistribution(values, areas, mapped);

        var palette = analysis.ResolveRamp().Stops;
        IReadOnlyList<AnalysisColorMapper.Band>? bands =
            AnalysisColorMapper.ResolveBandsFor(resolved, analysis.ColorMode, analysis.ColorInterval, palette);

        var colors = new byte[faceCount * 3];
        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            SlopeAnalyzer.ColorStop color = !mapped[faceIndex]
                ? UnmappedColor
                : AnalysisColorMapper.SampleResolved(
                    values[faceIndex], resolved, analysis.ColorMode, bands, palette);
            WriteColor(colors, faceIndex, color.R, color.G, color.B);
        }

        return BuildFaceColorMesh(vertices, faces, faceCount, colors, alpha);
    }

    /// <summary>
    /// Bars for the analysis card's histogram, area-weighted where areas are available so a thousand
    /// slivers cannot out-vote the ground they sit on — the same weighting the range fit uses, so the
    /// shape drawn behind the ramp is the shape auto-fit was reading.
    /// </summary>
    private static double[]? BuildDistribution(ReadOnlySpan<double> values, ReadOnlySpan<double> weights)
    {
        if (values.Length == 0)
            return null;

        double[] bars = AnalysisRange.Histogram.Build(values, weights).Resample(HistogramBars);
        return bars.Length == 0 ? null : bars;
    }

    private static double[]? BuildMaskedDistribution(double[] values, double[] areas, bool[] mask)
    {
        var histogram = new AnalysisRange.Histogram();
        for (int i = 0; i < values.Length; i++)
        {
            if (mask[i])
                histogram.Observe(values[i]);
        }

        histogram.FreezeBounds();
        for (int i = 0; i < values.Length; i++)
        {
            if (mask[i])
                histogram.Add(values[i], areas[i]);
        }

        double[] bars = histogram.Resample(HistogramBars);
        return bars.Length == 0 ? null : bars;
    }

    /// <summary>Fits a range over only the entries flagged in <paramref name="mask"/>.</summary>
    private static AnalysisRange ResolveMaskedRange(
        double[] values,
        double[] areas,
        bool[] mask,
        bool auto,
        double requestedLow,
        double requestedHigh,
        RangeShape shape)
    {
        if (!auto)
            return AnalysisRange.FromRequested(requestedLow, requestedHigh, shape);

        var histogram = new AnalysisRange.Histogram();
        for (int i = 0; i < values.Length; i++)
        {
            if (mask[i])
                histogram.Observe(values[i]);
        }

        histogram.FreezeBounds();
        for (int i = 0; i < values.Length; i++)
        {
            if (mask[i])
                histogram.Add(values[i], areas[i]);
        }

        return histogram.ResolveAuto(shape);
    }

    internal static MeshHeightProjector? CreateReferenceProjector(RhinoMesh referenceMesh)
    {
        return RhinoGeometryConversions.TryExtractMeshData(referenceMesh, out var vertices, out var faces, out _)
            ? new MeshHeightProjector(vertices, vertices.Length / 3, faces, faces.Length / 3)
            : null;
    }

    internal static bool TryProjectReferencePoint(
        RhinoMesh referenceMesh,
        MeshHeightProjector? projector,
        Point3d point,
        double tolerance,
        out Point3d projectedPoint)
    {
        MeshHeightProjector.ProjectionStatus status = MeshHeightProjector.ProjectionStatus.OutsideMesh;
        if (projector != null &&
            projector.TryProjectZ(point.X, point.Y, point.Z, tolerance, out double z, out status))
        {
            projectedPoint = new Point3d(point.X, point.Y, z);
            return true;
        }

        if (projector == null || status == MeshHeightProjector.ProjectionStatus.RequiresFallback)
            return TerrainMeshProjection.TryProjectPointAlongWorldZ(referenceMesh, point, tolerance, out projectedPoint);

        projectedPoint = Point3d.Unset;
        return false;
    }

    internal static RhinoMesh? ResolveReferenceMesh(RhinoDoc doc, SourceReferenceSet referenceSet)
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

    internal static bool IsInsideBoundaries(Point3d point, IReadOnlyList<Curve> boundaries)
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

    private static byte[] BuildMappedColors(
        double[] values,
        AnalysisRange range,
        AnalysisColorMapper.Mode mode,
        double interval,
        IReadOnlyList<SlopeAnalyzer.ColorStop> palette)
    {
        var colors = new byte[values.Length * 3];
        IReadOnlyList<AnalysisColorMapper.Band>? bands =
            AnalysisColorMapper.ResolveBandsFor(range, mode, interval, palette);

        for (int index = 0; index < values.Length; index++)
        {
            SlopeAnalyzer.ColorStop color =
                AnalysisColorMapper.SampleResolved(values[index], range, mode, bands, palette);
            WriteColor(colors, index, color.R, color.G, color.B);
        }

        return colors;
    }

    private static void WriteColor(byte[] colors, int faceIndex, byte r, byte g, byte b)
    {
        colors[faceIndex * 3] = r;
        colors[faceIndex * 3 + 1] = g;
        colors[faceIndex * 3 + 2] = b;
    }
}
