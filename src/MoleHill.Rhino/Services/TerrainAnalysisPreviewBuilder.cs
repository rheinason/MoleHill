using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using MoleHill.Shared;
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
            CatchmentAnalysisDefinition catchment => BuildCatchmentPreviewMesh(state.TerrainMesh, state.DrainagePreview, catchment, alpha),
            PondingAnalysisDefinition ponding => BuildPondingPreviewMesh(state.TerrainMesh, state.DrainagePreview, ponding, alpha, out resolvedRange, out distribution),
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
        PondingAnalysisDefinition => RangeShape.FromZero,
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
        if (!RhinoGeometryConversions.TryExtractMeshData(
                mesh, out var vertices, out int vertexCount, out var faces, out int faceCount, out _))
            return null;

        var palette = analysis.ResolveRamp();
        var slope = SlopeAnalyzer.Analyze(
            vertices,
            vertexCount,
            faces,
            faceCount,
            analysis.Unit,
            analysis.AutoColorRange,
            analysis.RangeLow,
            analysis.RangeHigh,
            palette.Stops,
            analysis.ColorMode,
            analysis.ColorInterval);

        range = slope.Range;
        distribution = BuildDistribution(slope.Slopes, ReadOnlySpan<double>.Empty, slope.Range);
        return BuildFaceColorMesh(vertices, faces, faceCount, slope.FaceColors, alpha);
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
        distribution = BuildDistribution(aspect.Bearings, aspect.PlanAreas, aspect.Range);
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
        DrainagePreviewCache cache,
        CatchmentAnalysisDefinition analysis,
        byte alpha)
    {
        double flatSlopeRatio = SlopeAnalyzer.ConvertUnitToRatio(
            analysis.FlatSlopeThresholdDegrees, SlopeAnalyzer.SlopeUnit.Degrees);
        double mergeShare = Math.Clamp(analysis.MinimumBasinAreaPercent, 0.0, 100.0) / 100.0;

        // Catchment colours are categorical and carry no palette setting, so the whole coloured result is
        // what the solve determines; only the alpha is applied per refresh.
        CatchmentPreviewSolve? solved = cache.GetOrCompute(
            mesh,
            new DrainagePreviewKey("catchments", flatSlopeRatio, mergeShare, 0.0),
            () => SolveCatchmentPreview(mesh, flatSlopeRatio, mergeShare));
        if (!solved.IsValid)
            return null;

        return BuildFaceColorMesh(solved.Vertices, solved.Faces, solved.FaceCount, solved.Colors, alpha);
    }

    private static CatchmentPreviewSolve SolveCatchmentPreview(RhinoMesh mesh, double flatSlopeRatio, double mergeShare)
    {
        if (!RhinoGeometryConversions.TryExtractMeshData(
                mesh, out var vertices, out int vertexCount, out var faces, out int faceCount, out _))
            return CatchmentPreviewSolve.Invalid;

        BasinGraph graph = DrainageBasinAnalyzer.Analyze(
            vertices,
            vertexCount,
            faces,
            faceCount,
            new DrainageBasinAnalyzer.Options
            {
                FlatSlopeRatio = flatSlopeRatio,
                MinimumBasinAreaShare = mergeShare
            });

        return new CatchmentPreviewSolve(vertices, faces, faceCount, TerrainBuildService.BuildCatchmentFaceColors(graph));
    }

    private sealed record CatchmentPreviewSolve(double[] Vertices, int[] Faces, int FaceCount, byte[] Colors)
    {
        public static readonly CatchmentPreviewSolve Invalid = new(Array.Empty<double>(), Array.Empty<int>(), 0, Array.Empty<byte>());

        public bool IsValid => FaceCount > 0;
    }

    /// <summary>
    /// Ponding colours the terrain by how deep the water stands.
    /// </summary>
    /// <remarks>
    /// Ramped, where the catchment map beside it is categorical — and the difference is the point. Ponded
    /// depth is a measurement on a continuum, so near values ought to read as near colours and a legend
    /// naming the ends means something.
    ///
    /// Dry ground is *masked*, not mapped to zero. Mapping it would paint every draining face at the
    /// ramp's low end, which is a colour, and the reader would have to know that this particular colour
    /// means "no water" rather than "a little water" — on the one analysis whose whole job is to make a
    /// problem obvious. Masked ground draws neutral grey and is excluded from the range fit and the
    /// histogram, so the ramp is fitted to the water rather than to the site.
    /// </remarks>
    private static RhinoMesh? BuildPondingPreviewMesh(
        RhinoMesh mesh,
        DrainagePreviewCache cache,
        PondingAnalysisDefinition analysis,
        byte alpha,
        out AnalysisRange? range,
        out double[]? distribution)
    {
        range = null;
        distribution = null;
        double flatSlopeRatio = SlopeAnalyzer.ConvertUnitToRatio(
            analysis.FlatSlopeThresholdDegrees, SlopeAnalyzer.SlopeUnit.Degrees);
        double minimumDepth = Math.Max(0.0, analysis.MinimumDepth);

        // Depths are a property of the water, not of how it is drawn, so they are what gets cached. The
        // range fit, histogram and colouring below read the palette settings and run on every refresh.
        PondingPreviewSolve? solved = cache.GetOrCompute(
            mesh,
            new DrainagePreviewKey("ponding", flatSlopeRatio, 0.0, minimumDepth),
            () => SolvePondingPreview(mesh, flatSlopeRatio, minimumDepth));
        if (!solved.IsValid)
            return null;

        double[] vertices = solved.Vertices;
        int[] faces = solved.Faces;
        int faceCount = solved.FaceCount;
        double[] depths = solved.Depths;
        double[] areas = solved.Areas;
        bool[] wet = solved.Wet;

        AnalysisRange resolved = ResolveMaskedRange(
            depths, areas, wet, analysis.AutoColorRange, analysis.RangeLow, analysis.RangeHigh,
            RangeShape.FromZero);
        range = resolved;
        distribution = BuildMaskedDistribution(depths, areas, wet, resolved);

        var palette = analysis.ResolveRamp().Stops;
        var bands = AnalysisColorMapper.ResolveBandsFor(resolved, analysis.ColorMode, analysis.ColorInterval, palette);
        var colors = new byte[faceCount * 3];
        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            if (!wet[faceIndex])
            {
                WriteColor(colors, faceIndex, UnmappedColor.R, UnmappedColor.G, UnmappedColor.B);
                continue;
            }

            var color = AnalysisColorMapper.SampleResolved(
                depths[faceIndex], resolved, analysis.ColorMode, bands, palette);
            WriteColor(colors, faceIndex, color.R, color.G, color.B);
        }

        return BuildFaceColorMesh(vertices, faces, faceCount, colors, alpha);
    }

    private sealed record PondingPreviewSolve(double[] Vertices, int[] Faces, int FaceCount, double[] Depths, double[] Areas, bool[] Wet)
    {
        public static readonly PondingPreviewSolve Invalid = new(
            Array.Empty<double>(), Array.Empty<int>(), 0, Array.Empty<double>(), Array.Empty<double>(), Array.Empty<bool>());

        public bool IsValid => FaceCount > 0;
    }

    private static PondingPreviewSolve SolvePondingPreview(RhinoMesh mesh, double flatSlopeRatio, double minimumDepth)
    {
        if (!RhinoGeometryConversions.TryExtractMeshData(
                mesh, out var vertices, out int vertexCount, out var faces, out int faceCount, out _))
            return PondingPreviewSolve.Invalid;

        BasinGraph graph = DrainageBasinAnalyzer.Analyze(
            vertices,
            vertexCount,
            faces,
            faceCount,
            new DrainageBasinAnalyzer.Options { FlatSlopeRatio = flatSlopeRatio });
        IReadOnlyList<PondingSolver.Pond> ponds = PondingSolver.Solve(
            graph, vertices, vertexCount, faces,
            new PondingSolver.Options { MinimumDepth = minimumDepth });

        var depths = new double[faceCount];
        var areas = new double[faceCount];
        var wet = new bool[faceCount];
        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
            MeasureFace(vertices, faces, faceIndex, depths, areas);

        // depths currently holds face-average elevation; turn it into depth below each pond's surface.
        var surfaceOfBasin = new Dictionary<int, double>(ponds.Count);
        foreach (PondingSolver.Pond pond in ponds)
            surfaceOfBasin[pond.BasinIndex] = pond.SpillZ;

        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            int basin = graph.FaceBasin[faceIndex];
            if (basin < 0 || !surfaceOfBasin.TryGetValue(basin, out double surfaceZ))
            {
                depths[faceIndex] = double.NaN;
                continue;
            }

            double depth = surfaceZ - depths[faceIndex];
            if (depth <= 0.0)
            {
                depths[faceIndex] = double.NaN;
                continue;
            }

            depths[faceIndex] = depth;
            wet[faceIndex] = true;
        }

        return new PondingPreviewSolve(vertices, faces, faceCount, depths, areas, wet);
    }

    internal static bool SupportsTerrainPreview(AnalysisDefinition analysis)
    {
        return analysis is SlopeAnalysisDefinition or AspectAnalysisDefinition or ElevationAnalysisDefinition
            or CutFillAnalysisDefinition or CatchmentAnalysisDefinition or PondingAnalysisDefinition;
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
            or CatchmentAnalysisDefinition { ShowFlowPaths: true }
            or PondingAnalysisDefinition { ShowOutlines: true }
            or PondingAnalysisDefinition { ShowSpillPoints: true };
    }

    internal static bool ShouldDisplayGeneratedOutput(TerrainDefinition terrain, GeneratedRhinoObject generated)
    {
        if (generated.Kind == GeneratedObjectKind.SlopePreview)
            return terrain.ShowAnalysisOutputs && terrain.ShowSlopePreview;

        return !generated.AnalysisId.HasValue ||
            TerrainContentVisibility.IsOwnerVisible(terrain, generated.AnalysisId.Value);
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
        if (!RhinoGeometryConversions.TryExtractMeshData(
                mesh, out var vertices, out _, out var faces, out int faceCount, out _))
            return null;

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

        distribution = BuildDistribution(values, areas, resolved);

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
        if (terrainMesh == null || !RhinoGeometryConversions.TryExtractMeshData(
                terrainMesh, out var vertices, out _, out var faces, out int faceCount, out _))
            return null;

        RhinoMesh? referenceTerrainMesh = analysis.ReferenceTerrainId is { } referenceTerrainId
            ? resolveReferenceTerrainMesh?.Invoke(referenceTerrainId)
            : null;
        RhinoMesh? referenceMesh = ResolveReferenceMesh(doc, analysis.Reference) ?? referenceTerrainMesh ?? state.BaseTerrainMesh;
        if (referenceMesh == null)
            return terrainMesh;

        MeshHeightProjector? referenceProjector = CreateReferenceProjector(referenceMesh);
        var boundaries = RhinoSourceResolver.ResolveCurves(doc, analysis.Boundary);
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
        distribution = BuildMaskedDistribution(values, areas, mapped, resolved);

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
    /// slivers cannot out-vote the ground they sit on — the same weighting the range fit uses.
    ///
    /// Binned over the mapped <paramref name="range"/>, not the data's own extent: the card draws the bars
    /// across the ramp, which spans the mapped range, so a bar must sit under the colour its faces are
    /// painted. Values outside the range fall in the end bars, as they take the end colours.
    /// </summary>
    private static double[]? BuildDistribution(ReadOnlySpan<double> values, ReadOnlySpan<double> weights, AnalysisRange? range)
        => BuildRangeDistribution(values, weights, mask: null, range, HistogramBars);

    private static double[]? BuildMaskedDistribution(double[] values, double[] areas, bool[] mask, AnalysisRange range)
        => BuildRangeDistribution(values, areas, mask, range, HistogramBars);

    /// <summary>
    /// Weighted bar heights over <paramref name="range"/>, scaled so the tallest is 1.0 — the shape the
    /// card draws behind its ramp. Non-finite values (vertical or flat faces) and masked-out entries are
    /// ignored; an empty weight span weights every value equally. Null when there is nothing to draw.
    /// </summary>
    internal static double[]? BuildRangeDistribution(
        ReadOnlySpan<double> values,
        ReadOnlySpan<double> weights,
        bool[]? mask,
        AnalysisRange? range,
        int barCount)
    {
        if (values.Length == 0 || barCount <= 0 || range is not { } mapped)
            return null;

        double low = mapped.Low;
        double span = mapped.High - mapped.Low;
        if (!double.IsFinite(low) || !double.IsFinite(span) || span <= 0.0)
            return null;

        var bars = new double[barCount];
        bool any = false;
        for (int i = 0; i < values.Length; i++)
        {
            if (mask != null && (i >= mask.Length || !mask[i]))
                continue;

            double value = values[i];
            if (!double.IsFinite(value))
                continue;

            double weight = weights.Length == values.Length ? weights[i] : 1.0;
            if (!double.IsFinite(weight) || weight <= 0.0)
                continue;

            int bar = (int)Math.Floor((value - low) / span * barCount);
            bars[Math.Clamp(bar, 0, barCount - 1)] += weight;
            any = true;
        }

        if (!any)
            return null;

        double tallest = bars.Max();
        if (tallest <= 0.0)
            return null;

        for (int i = 0; i < barCount; i++)
            bars[i] /= tallest;
        return bars;
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

        MeshNormalOrientation.UnifyAndComputeNormals(coloredMesh);
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
