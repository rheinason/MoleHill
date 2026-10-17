using System.Diagnostics;
using MoleHill.Core.Analysis;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Shared;
using Rhino.Geometry;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

// Analysis stage: slope, earthwork/cut-fill, contour, and waterflow summaries plus their preview helpers.
internal sealed partial class TerrainBuildService
{
    private static List<TerrainAnalysisSummary> BuildAnalyses(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh fallbackBaseMesh,
        RhinoMesh currentMesh,
        ulong baseMeshFingerprint,
        ulong currentMeshFingerprint,
        TerrainBuildResult build,
        TerrainRuntimeCache runtimeCache,
        ISet<string> usedStageKeys,
        Dictionary<ReferenceProjectionCacheKey, ReferenceProjectionContext> referenceProjectionCache,
        Func<bool>? shouldCancel)
    {
        var totalTimer = Stopwatch.StartNew();
        var results = new List<TerrainAnalysisSummary>(terrain.Analyses.Count + terrain.Annotations.Count);
        ThrowIfCancellationRequested(shouldCancel);
        bool analysisContextPrepared = false;
        // Statistics are keyed on the current geometry too, so the comparison cache is scoped to this pass;
        // the projection cache is build-wide and supplied by the caller. The basin-graph cache has the same
        // scoping and reason: the drainage cards rest on one routing of this geometry, so two of them
        // agreeing on their settings route it once.
        var context = new AnalysisBuildContext
        {
            Snapshot = snapshot,
            Terrain = terrain,
            FallbackBaseMesh = fallbackBaseMesh,
            CurrentMesh = currentMesh,
            Build = build,
            ReferenceComparisonCache = new Dictionary<ReferenceComparisonCacheKey, ReferenceComparisonStats>(),
            ReferenceProjectionCache = referenceProjectionCache,
            BasinGraphCache = new Dictionary<BasinGraphCacheKey, BasinGraph>(),
            ShouldCancel = shouldCancel
        };

        bool EnsureAnalysisContext()
        {
            if (analysisContextPrepared)
                return true;

            // The counts must come from the same extraction as the arrays: TryExtractMeshData normalizes
            // a copy, so currentMesh.Vertices.Count/Faces.Count can describe a different mesh.
            if (!RhinoGeometryConversions.TryExtractMesh(currentMesh, out IndexedTriMesh extracted, out _))
                return false;

            context.Mesh = extracted;
            GetElevationRange(extracted.Vertices, extracted.VertexCount, out double elevMinZ, out double elevMaxZ);
            context.ElevationMinZ = elevMinZ;
            context.ElevationMaxZ = elevMaxZ;
            context.SurfaceArea = AreaMassProperties.Compute(currentMesh)?.Area ?? 0.0;
            analysisContextPrepared = true;
            return true;
        }

        // One stage runner, two families. The scaffolding here — enabled check, fingerprint, stage
        // cache, timing — genuinely does not care which kind of content it is running, so it takes an
        // ITerrainContentItem; deciding what to actually compute stays with the caller, per family.
        void RunStage(ITerrainContentItem item, string family, string stageKind, Func<TerrainAnalysisSummary?> compute)
        {
            ThrowIfCancellationRequested(shouldCancel);

            // Disabled content produces no output, so skip its (sometimes expensive) computation
            // entirely instead of computing it and discarding the result — this was a per-solve cost,
            // most painfully for contours, which scanned the whole mesh per level even when disabled.
            if (!item.IsEnabled)
                return;

            string stageKey = TerrainStageKey.ForMode(TerrainBuildMode.Final, $"{stageKind}:{item.Id:N}");
            usedStageKeys.Add(stageKey);
            ulong fingerprint = ComputeAnalysisFingerprint(
                snapshot,
                terrain,
                item,
                fallbackBaseMesh,
                currentMesh,
                baseMeshFingerprint,
                currentMeshFingerprint);
            var analysisTimer = Stopwatch.StartNew();
            if (runtimeCache.StageEntries.TryGetValue(stageKey, out StageCacheEntry? cachedEntry) &&
                cachedEntry.PreResolutionFingerprint == fingerprint)
            {
                RestoreCachedDiagnostics(build, cachedEntry);
                List<TerrainAnalysisSummary> cachedResults = TerrainRuntimeCacheCloner.CloneAnalyses(cachedEntry.AnalysisOutput);
                build.AuxiliaryObjects.AddRange(TerrainRuntimeCacheCloner.CloneGeneratedObjects(cachedEntry.AuxiliaryObjects));
                results.AddRange(cachedResults);
                analysisTimer.Stop();
                build.RecordTiming(
                    $"{family} {item.Label}",
                    analysisTimer.Elapsed,
                    AppendCacheHitDetail($"{cachedResults.Count:N0} summaries, {cachedEntry.AuxiliaryObjects.Count:N0} outputs"),
                    isCacheHit: true);
                return;
            }

            if (!EnsureAnalysisContext())
                return;

            int diagnosticsStart = build.Diagnostics.Count;
            int structuredDiagnosticsStart = build.StructuredDiagnostics.Count;
            int auxiliaryStart = build.AuxiliaryObjects.Count;
            int runtimeOverlayStart = build.RuntimeOverlays.Count;
            TerrainAnalysisSummary? summary = compute();

            if (summary != null)
                results.Add(summary);

            analysisTimer.Stop();
            runtimeCache.StageEntries[stageKey] = new StageCacheEntry
            {
                StageName = $"{family} {item.Label}",
                PreResolutionFingerprint = fingerprint,
                ResolvedInputFingerprint = fingerprint,
                OutputFingerprint = fingerprint,
                AnalysisOutput = summary == null
                    ? new List<TerrainAnalysisSummary>()
                    : TerrainRuntimeCacheCloner.CloneAnalyses(new[] { summary }),
                AuxiliaryObjects = TerrainRuntimeCacheCloner.CloneGeneratedObjects(build.AuxiliaryObjects.Skip(auxiliaryStart)),
                Diagnostics = build.Diagnostics.Skip(diagnosticsStart).ToList(),
                StructuredDiagnostics = build.StructuredDiagnostics.Skip(structuredDiagnosticsStart).ToList(),
                RuntimeOverlays = TerrainRuntimeCacheCloner.CloneRuntimeOverlays(build.RuntimeOverlays.Skip(runtimeOverlayStart))
            };
            build.RecordTiming(
                $"{family} {item.Label}",
                analysisTimer.Elapsed,
                $"{(summary == null ? 0 : 1):N0} summaries, {build.AuxiliaryObjects.Count - auxiliaryStart:N0} outputs");
        }

        foreach (var analysis in terrain.Analyses)
        {
            ThrowIfCancellationRequested(shouldCancel);
            AnalysisDefinition current = analysis;
            RunStage(current, "Analysis", "analysis",
                () => AnalysisTypeRegistry.ForType(current.GetType())?.Build(context, current));
        }

        foreach (var annotation in terrain.Annotations)
        {
            ThrowIfCancellationRequested(shouldCancel);

            // A legend keys the preview colouring, which recolours without a rebuild; it is drawn with
            // that colouring (TerrainController.RefreshLegends), never here, so it gets no stage either.
            if (annotation is LegendAnnotationDefinition)
                continue;

            AnnotationDefinition current = annotation;
            RunStage(current, "Annotation", "annotation",
                () => AnnotationTypeRegistry.ForType(current.GetType())?.Build(context, current));
        }

        totalTimer.Stop();
        build.RecordTiming("Analysis", totalTimer.Elapsed, $"{results.Count:N0} enabled analyses and annotations");
        return results;
    }

    internal static TerrainAnalysisSummary BuildSlopeSummary(
        IndexedTriMesh currentMesh,
        SlopeAnalysisDefinition analysis,
        double surfaceArea)
    {
        // Auto-fit must be honoured here exactly as the preview mesh honours it, or the "Mapped" row and
        // the legend describe a range the terrain was never coloured with.
        double lowPercent = ConvertSlopeUnitToPercent(analysis.RangeLow, analysis.Unit);
        double highPercent = ConvertSlopeUnitToPercent(analysis.RangeHigh, analysis.Unit);
        var slope = SlopeAnalyzer.Summarize(
            currentMesh,
            SlopeAnalyzer.SlopeUnit.Percent,
            analysis.AutoColorRange,
            Math.Max(0.0, lowPercent),
            Math.Max(0.0, highPercent));

        return new TerrainAnalysisSummary
        {
            AnalysisId = analysis.Id,
            SurfaceArea = surfaceArea,
            SlopeMinPercent = slope.Min,
            SlopeMaxPercent = slope.Max,
            SlopeAveragePercent = slope.Average,
            SlopeDisplayLowPercent = slope.ColorLow,
            SlopeDisplayHighPercent = slope.ColorHigh
        };
    }

    /// <summary>
    /// Counts come from the arrays themselves, never from the Rhino mesh they were extracted out of:
    /// <c>TryExtractMeshData</c> normalizes a copy, so the mesh's own counts routinely describe
    /// different geometry and pairing the two reads off the end of the array. See CLAUDE.md.
    /// </summary>
    internal static TerrainAnalysisSummary BuildAspectSummary(
        TerrainBuildSnapshot snapshot,
        IndexedTriMesh currentMesh,
        AspectAnalysisDefinition analysis,
        double surfaceArea)
    {
        var aspect = AspectAnalyzer.Summarize(
            currentMesh,
            snapshot.NorthAzimuthDegrees,
            SlopeAnalyzer.ConvertUnitToRatio(analysis.FlatSlopeThresholdDegrees, SlopeAnalyzer.SlopeUnit.Degrees));

        return new TerrainAnalysisSummary
        {
            AnalysisId = analysis.Id,
            SurfaceArea = surfaceArea,
            AspectFaceCount = aspect.FaceCount,
            AspectFlatFaceCount = aspect.FlatFaceCount,
            AspectDominantBearing = aspect.DominantBearing
        };
    }

    internal static TerrainAnalysisSummary BuildEarthworkSummary(
        TerrainBuildSnapshot snapshot,
        RhinoMesh fallbackBaseMesh,
        RhinoMesh currentMesh,
        double[] currentVertices,
        int[] currentFaces,
        EarthworkAnalysisDefinition analysis,
        double surfaceArea,
        double elevMinZ,
        double elevMaxZ,
        TerrainBuildResult build,
        Dictionary<ReferenceComparisonCacheKey, ReferenceComparisonStats> referenceComparisonCache,
        Dictionary<ReferenceProjectionCacheKey, ReferenceProjectionContext> referenceProjectionCache,
        Func<bool>? shouldCancel)
    {
        var stats = ComputeReferenceComparisonStats(
            snapshot,
            fallbackBaseMesh,
            currentVertices,
            currentFaces,
            analysis.Reference,
            analysis.Boundary,
            build,
            referenceComparisonCache,
            referenceProjectionCache,
            shouldCancel,
            analysis.ReferenceTerrainId);

        return new TerrainAnalysisSummary
        {
            AnalysisId = analysis.Id,
            SurfaceArea = surfaceArea,
            ElevationMinZ = elevMinZ,
            ElevationMaxZ = elevMaxZ,
            CutVolume = stats.CutVolume,
            FillVolume = stats.FillVolume,
            NetVolume = stats.NetVolume,
            EarthworkIsEstimated = stats.IsEstimated
        };
    }

    internal static TerrainAnalysisSummary BuildCutFillSummary(
        TerrainBuildSnapshot snapshot,
        RhinoMesh fallbackBaseMesh,
        RhinoMesh currentMesh,
        double[] currentVertices,
        int[] currentFaces,
        CutFillAnalysisDefinition analysis,
        double surfaceArea,
        double elevMinZ,
        double elevMaxZ,
        TerrainBuildResult build,
        Dictionary<ReferenceComparisonCacheKey, ReferenceComparisonStats> referenceComparisonCache,
        Dictionary<ReferenceProjectionCacheKey, ReferenceProjectionContext> referenceProjectionCache,
        Func<bool>? shouldCancel)
    {
        var stats = ComputeReferenceComparisonStats(
            snapshot,
            fallbackBaseMesh,
            currentVertices,
            currentFaces,
            analysis.Reference,
            analysis.Boundary,
            build,
            referenceComparisonCache,
            referenceProjectionCache,
            shouldCancel,
            analysis.ReferenceTerrainId);

        int deltaContourCount = 0;
        int balanceCurveCount = 0;
        if (analysis.DrawsDeltaOutput)
        {
            EmitCutFillDeltaOutputs(
                snapshot,
                fallbackBaseMesh,
                currentVertices,
                currentFaces,
                analysis,
                build,
                referenceProjectionCache,
                shouldCancel,
                out deltaContourCount,
                out balanceCurveCount);
        }

        return new TerrainAnalysisSummary
        {
            AnalysisId = analysis.Id,
            SurfaceArea = surfaceArea,
            ElevationMinZ = elevMinZ,
            ElevationMaxZ = elevMaxZ,
            CutFillDisplayAbsMax = stats.CutFillDisplayAbsMax,
            CutFillDeltaContourCount = deltaContourCount,
            CutFillBalanceCurveCount = balanceCurveCount,
            CutVolume = stats.CutVolume,
            FillVolume = stats.FillVolume,
            NetVolume = stats.NetVolume,
            EarthworkIsEstimated = stats.IsEstimated
        };
    }

    /// <summary>
    /// Draws the cut/fill delta: contours of the depth, and the balance line where the depth crosses zero.
    ///
    /// The delta is contoured as a per-VERTEX field, not the per-face field the colour map and the volumes
    /// use: contouring interpolates along an edge, so it needs the value at both of its ends. Vertices with
    /// nothing beneath them — outside the comparison boundary, or over a hole in the reference — carry NaN,
    /// and <see cref="ContourGenerator"/> skips every face touching one, so unmapped ground draws nothing
    /// rather than a line derived from a depth that was never measured.
    /// </summary>
    private static void EmitCutFillDeltaOutputs(
        TerrainBuildSnapshot snapshot,
        RhinoMesh fallbackBaseMesh,
        double[] currentVertices,
        int[] currentFaces,
        CutFillAnalysisDefinition analysis,
        TerrainBuildResult build,
        Dictionary<ReferenceProjectionCacheKey, ReferenceProjectionContext> referenceProjectionCache,
        Func<bool>? shouldCancel,
        out int deltaContourCount,
        out int balanceCurveCount)
    {
        deltaContourCount = 0;
        balanceCurveCount = 0;

        // Counts come from the arrays themselves, never from the Rhino mesh they were extracted out of:
        // the extraction normalizes a copy, so the mesh's own counts can describe different geometry.
        int vertexCount = currentVertices.Length / 3;
        int faceCount = currentFaces.Length / 3;
        if (vertexCount < 3 || faceCount == 0)
            return;

        ReferenceProjectionContext projection = ResolveReferenceProjection(
            snapshot, fallbackBaseMesh, analysis.Reference, analysis.ReferenceTerrainId, referenceProjectionCache);
        var boundaries = TerrainBuildSnapshotResolver.ResolveCurves(snapshot, analysis.Boundary);
        double tolerance = snapshot.ModelAbsoluteTolerance;

        double[] field = ComputeReferenceDeltaField(
            projection, currentVertices, vertexCount, boundaries, tolerance, shouldCancel);

        GetFiniteRange(field, out double minDelta, out double maxDelta);
        if (!(maxDelta > minDelta))
            return;

        double effectiveTolerance = Math.Max(Math.Abs(tolerance), double.Epsilon);
        LayerRoleTable roles = snapshot.LayerRoles;

        if (analysis.ShowDeltaContours && analysis.DeltaContourInterval > effectiveTolerance)
        {
            // Levels step out from zero rather than from the data's low end, so the drawn depths are round
            // numbers either side of no-change — the way a cut/fill drawing is read. Zero itself belongs to
            // the balance line, which means something different from a depth and draws on its own layer.
            var levels = BuildContourLevels(minDelta, maxDelta, 0.0, analysis.DeltaContourInterval)
                .Where(level => Math.Abs(level) > effectiveTolerance)
                .ToList();

            deltaContourCount = EmitDeltaFieldCurves(
                currentVertices,
                vertexCount,
                currentFaces,
                faceCount,
                field,
                levels,
                effectiveTolerance,
                analysis,
                LayerRole.CutFillContours,
                roles,
                analysis.DeltaContourColorArgb,
                level => $"{analysis.Label} delta {level:G4}",
                build);
        }

        // No balance line unless the delta actually changes sign: a site that is all fill has no line where
        // cut meets fill, and drawing one at the shallowest edge would invent a boundary.
        if (analysis.ShowBalanceLine && minDelta < 0.0 && maxDelta > 0.0)
        {
            balanceCurveCount = EmitDeltaFieldCurves(
                currentVertices,
                vertexCount,
                currentFaces,
                faceCount,
                field,
                new[] { 0.0 },
                effectiveTolerance,
                analysis,
                LayerRole.BalanceLine,
                roles,
                analysis.BalanceLineColorArgb,
                _ => $"{analysis.Label} balance line",
                build);
        }
    }

    /// <summary>Contours one field at the given levels and adds the result to the build's output.</summary>
    private static int EmitDeltaFieldCurves(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        double[] field,
        IReadOnlyList<double> levels,
        double tolerance,
        CutFillAnalysisDefinition analysis,
        LayerRole role,
        LayerRoleTable roles,
        int? colorArgb,
        Func<double, string> nameFor,
        TerrainBuildResult build)
    {
        if (levels.Count == 0)
            return 0;

        var contourLevels = ContourGenerator.Generate(
            new IndexedTriMesh(vertices, vertexCount, faces, faceCount), levels, tolerance, field);

        string layerPath = roles.Path(role);
        int curveCount = 0;
        foreach (var contourLevel in contourLevels)
        {
            int indexAtLevel = 0;
            foreach (var polyline in contourLevel.Polylines)
            {
                if (polyline.PointCount < 2)
                    continue;

                indexAtLevel++;
                curveCount++;
                string name = nameFor(contourLevel.Z);
                build.AuxiliaryObjects.Add(new GeneratedRhinoObject
                {
                    Geometry = new PolylineCurve(ToRhinoPolyline(polyline)),
                    Name = indexAtLevel == 1 ? name : $"{name} ({indexAtLevel})",
                    AnalysisId = analysis.Id,
                    ColorArgb = colorArgb,
                    AppearanceSource = colorArgb.HasValue
                        ? GeneratedAppearanceSource.Object
                        : GeneratedAppearanceSource.Layer,
                    Role = role,
                    LayerPath = layerPath
                });
            }
        }

        return curveCount;
    }

    /// <summary>
    /// Per-vertex delta between this terrain and the reference, NaN where there is nothing to compare to.
    /// </summary>
    private static double[] ComputeReferenceDeltaField(
        ReferenceProjectionContext projection,
        double[] currentVertices,
        int vertexCount,
        IReadOnlyList<Curve> boundaries,
        double tolerance,
        Func<bool>? shouldCancel)
    {
        var field = new double[vertexCount];
        for (int i = 0; i < vertexCount; i++)
        {
            if ((i & 127) == 0)
                ThrowIfCancellationRequested(shouldCancel);

            var point = new Point3d(
                currentVertices[i * 3],
                currentVertices[(i * 3) + 1],
                currentVertices[(i * 3) + 2]);

            if (!TerrainAnalysisPreviewBuilder.IsInsideBoundaries(point, boundaries, tolerance) ||
                !TryProjectReferencePoint(projection, point, tolerance, out Point3d basePoint))
            {
                field[i] = double.NaN;
                continue;
            }

            field[i] = point.Z - basePoint.Z;
        }

        return field;
    }

    /// <summary>The extent of a field's comparable values, ignoring the NaNs that mark unmapped ground.</summary>
    private static void GetFiniteRange(double[] values, out double min, out double max)
    {
        min = double.MaxValue;
        max = double.MinValue;
        foreach (double value in values)
        {
            if (!double.IsFinite(value))
                continue;
            if (value < min)
                min = value;
            if (value > max)
                max = value;
        }

        if (min > max)
        {
            min = 0.0;
            max = 0.0;
        }
    }

    internal static TerrainAnalysisSummary BuildContourSummary(
        TerrainDefinition terrain,
        RhinoMesh currentMesh,
        ContourAnnotationDefinition analysis,
        double elevMinZ,
        double elevMaxZ,
        double tolerance,
        TerrainBuildResult build,
        AnnotationStyleSnapshot? annotationStyle,
        LayerRoleTable layerRoles)
    {
        var (objects, summary) = BuildContourCore(currentMesh, analysis, elevMinZ, elevMaxZ, tolerance,
            layerRoles,
            annotationStyle);
        build.AuxiliaryObjects.AddRange(objects);
        return summary;
    }

    internal static (List<GeneratedRhinoObject> Objects, TerrainAnalysisSummary Summary) BuildContourObjects(
        RhinoMesh mesh,
        ContourAnnotationDefinition analysis,
        double tolerance = 1e-4)
    {
        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out int vertexCount, out _, out _, out _))
            return (new List<GeneratedRhinoObject>(), new TerrainAnalysisSummary { AnalysisId = analysis.Id });

        GetElevationRange(vertices, vertexCount, out double minZ, out double maxZ);
        return BuildContourCore(mesh, analysis, minZ, maxZ, tolerance);
    }

    private static (List<GeneratedRhinoObject> Objects, TerrainAnalysisSummary Summary) BuildContourCore(
        RhinoMesh mesh,
        ContourAnnotationDefinition analysis,
        double elevMinZ,
        double elevMaxZ,
        double tolerance,
        LayerRoleTable? layerRoles = null,
        AnnotationStyleSnapshot? annotationStyle = null)
    {
        var objects = new List<GeneratedRhinoObject>();
        int contourCurveCount = 0;
        int contourLevelCount = 0;
        double firstLevel = 0.0;
        double lastLevel = 0.0;

        if (RhinoGeometryConversions.TryExtractMesh(mesh, out IndexedTriMesh extracted, out _))
        {
            (double[] vertices, int vertexCount, int[] faces, int faceCount) = extracted;
            double effectiveTolerance = Math.Max(Math.Abs(tolerance), double.Epsilon);
            var levels = BuildContourLevels(elevMinZ, elevMaxZ, analysis.StartZ, Math.Max(analysis.Interval, effectiveTolerance));

            // Single pass over the faces (marching triangles) instead of one mesh-plane intersection
            // per level. Each triangle only contributes to the levels inside its own Z-span.
            var contourLevels = ContourGenerator.Generate(extracted, levels, effectiveTolerance);

            int everyNth = Math.Max(1, analysis.LabelEveryNth);
            bool wantLabels = analysis.ShowLabels && analysis.IsEnabled;
            LayerRoleTable roles = layerRoles ?? LayerRoleTable.Default;

            foreach (var contourLevel in contourLevels)
            {
                bool isMajor = IsMajorContourLevel(contourLevel.Z, analysis, effectiveTolerance);
                LayerRole levelRole = ResolveContourLevelRole(analysis, isMajor);
                string levelLayerPath = roles.Path(levelRole);
                int levelCurveIndex = 0;
                bool levelHasCurves = false;
                List<Polyline>? levelPolylines = wantLabels ? new List<Polyline>() : null;
                foreach (var polyline in contourLevel.Polylines)
                {
                    if (polyline.PointCount < 2)
                        continue;

                    levelHasCurves = true;
                    contourCurveCount++;
                    if (!analysis.IsEnabled)
                        continue;

                    levelCurveIndex++;
                    var rhinoPolyline = ToRhinoPolyline(polyline);
                    objects.Add(new GeneratedRhinoObject
                    {
                        Geometry = new PolylineCurve(rhinoPolyline),
                        Name = levelCurveIndex == 1
                            ? $"{analysis.Label} {contourLevel.Z:G4}"
                            : $"{analysis.Label} {contourLevel.Z:G4} ({levelCurveIndex})",
                        AnalysisId = analysis.Id,
                        ColorArgb = analysis.ColorArgb,
                        AppearanceSource = analysis.ColorArgb.HasValue
                            ? GeneratedAppearanceSource.Object
                            : GeneratedAppearanceSource.Layer,
                        Role = levelRole,
                        LayerPath = levelLayerPath
                    });
                    levelPolylines?.Add(rhinoPolyline);
                }

                if (!levelHasCurves)
                    continue;

                contourLevelCount++;
                if (contourLevelCount == 1)
                    firstLevel = contourLevel.Z;
                lastLevel = contourLevel.Z;

                // Index-contour labelling: only every Nth drawn level carries elevation text.
                if (levelPolylines != null && ((contourLevelCount - 1) % everyNth == 0))
                {
                    foreach (var rhinoPolyline in levelPolylines)
                        EmitContourLabels(objects, rhinoPolyline, contourLevel.Z, analysis, roles.Path(LayerRole.Labels), effectiveTolerance, annotationStyle);
                }
            }
        }

        var summary = new TerrainAnalysisSummary
        {
            AnalysisId = analysis.Id,
            ContourCurveCount = contourCurveCount,
            ContourLevelCount = contourLevelCount,
            ContourFirstLevel = contourLevelCount > 0 ? firstLevel : 0.0,
            ContourLastLevel = contourLevelCount > 0 ? lastLevel : 0.0
        };
        return (objects, summary);
    }

    /// <summary>
    /// A level is a major (index) contour when its step from <see cref="ContourAnnotationDefinition.StartZ"/>
    /// is a multiple of <see cref="ContourAnnotationDefinition.MajorEveryNth"/>. Deliberately keyed on
    /// elevation rather than on the ordinal of levels that happened to produce curves, so a level that is
    /// empty on one build does not shift the whole major/minor pattern on the next.
    /// </summary>
    internal static bool IsMajorContourLevel(double levelZ, ContourAnnotationDefinition analysis, double tolerance)
    {
        int everyNth = Math.Max(1, analysis.MajorEveryNth);
        if (everyNth == 1)
            return true;

        double interval = Math.Max(analysis.Interval, Math.Max(Math.Abs(tolerance), double.Epsilon));
        double steps = (levelZ - analysis.StartZ) / interval;
        long rounded = (long)Math.Round(steps);

        // Guard against a level that is not on the interval grid at all (a caller-supplied level, or
        // accumulated floating-point drift beyond half an interval).
        if (Math.Abs(steps - rounded) > 1e-6)
            return false;

        return ((rounded % everyNth) + everyNth) % everyNth == 0;
    }

    /// <summary>
    /// Major and minor contours are separated by layer, not by per-object colour or width, so the drawing
    /// hierarchy is controlled from Rhino's Layers panel and honours per-detail overrides.
    /// </summary>
    internal static LayerRole ResolveContourLevelRole(ContourAnnotationDefinition analysis, bool isMajor)
    {
        if (!analysis.SeparateMajorMinorLayers)
            return LayerRole.Contours;

        return isMajor ? LayerRole.ContoursMajor : LayerRole.ContoursMinor;
    }

    private static void EmitContourLabels(
        List<GeneratedRhinoObject> objects,
        Polyline polyline,
        double levelZ,
        ContourAnnotationDefinition analysis,
        string? layerPath,
        double tolerance,
        AnnotationStyleSnapshot? annotationStyle)
    {
        if (polyline.Count < 2)
            return;

        var curve = new PolylineCurve(polyline);
        double length = curve.GetLength();
        if (length <= tolerance)
            return;

        double textHeight = Math.Max(
            analysis.FollowsAnnotationStyle && annotationStyle != null
                ? annotationStyle.TextHeight
                : analysis.LabelTextHeight,
            tolerance);
        string text = FormatContourLabel(levelZ, analysis.LabelFormat);

        // Repeat along the contour when an interval is set; otherwise a single label at the midpoint.
        var stations = new List<double>();
        if (analysis.LabelInterval > tolerance)
        {
            for (double s = analysis.LabelInterval * 0.5; s < length; s += analysis.LabelInterval)
                stations.Add(s);
            if (stations.Count == 0)
                stations.Add(length * 0.5);
        }
        else
        {
            stations.Add(length * 0.5);
        }

        foreach (double station in stations)
        {
            if (!curve.LengthParameter(station, out double t))
                continue;

            Point3d point = curve.PointAt(t);
            Vector3d tangent = curve.TangentAt(t);
            Plane plane = SectionLayoutHelper.FrameFromCurveTangent(point, tangent);
            var label = new TextEntity
            {
                Plane = plane,
                PlainText = text,
                TextHeight = textHeight,
                Justification = TextJustification.MiddleCenter
            };

            objects.Add(new GeneratedRhinoObject
            {
                Role = LayerRole.Labels,
                Geometry = label,
                Name = $"{analysis.Label} {levelZ:G4} label",
                AnalysisId = analysis.Id,
                ColorArgb = analysis.ColorArgb,
                LayerPath = layerPath
            });
        }
    }

    private static string FormatContourLabel(double value, string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return value.ToString("F2");

        try
        {
            return value.ToString(format);
        }
        catch (FormatException)
        {
            return value.ToString("F2");
        }
    }

    private static Polyline ToRhinoPolyline(MoleHill.Core.Analysis.ContourPolyline polyline)
    {
        int count = polyline.PointCount;
        var result = new Polyline(count + (polyline.IsClosed ? 1 : 0));
        for (int i = 0; i < count; i++)
            result.Add(new Point3d(polyline.PointsXyz[i * 3], polyline.PointsXyz[i * 3 + 1], polyline.PointsXyz[i * 3 + 2]));

        if (polyline.IsClosed && count > 0)
            result.Add(result[0]);

        return result;
    }

    internal static ReferenceComparisonStats ComputeReferenceComparisonStats(
        TerrainBuildSnapshot snapshot,
        RhinoMesh fallbackBaseMesh,
        double[] currentVertices,
        int[] currentFaces,
        SourceReferenceSet referenceSet,
        SourceReferenceSet boundarySet,
        TerrainBuildResult build,
        Dictionary<ReferenceComparisonCacheKey, ReferenceComparisonStats> referenceComparisonCache,
        Dictionary<ReferenceProjectionCacheKey, ReferenceProjectionContext> referenceProjectionCache,
        Func<bool>? shouldCancel,
        Guid? referenceTerrainId = null)
    {
        ulong referenceTerrainFingerprint = 0;
        TerrainSectionReferenceSnapshot? referenceTerrain = null;
        if (referenceTerrainId.HasValue &&
            snapshot.SectionTerrains.TryGetValue(referenceTerrainId.Value, out referenceTerrain))
            referenceTerrainFingerprint = referenceTerrain.MeshFingerprint;

        var cacheKey = new ReferenceComparisonCacheKey(
            currentVertices,
            currentFaces,
            ComputeSourceSetFingerprint(snapshot, referenceSet),
            ComputeSourceSetFingerprint(snapshot, boundarySet),
            referenceTerrainFingerprint,
            !referenceSet.HasReferences && referenceTerrain == null);
        if (referenceComparisonCache.TryGetValue(cacheKey, out ReferenceComparisonStats cachedStats))
            return cachedStats;

        bool isEstimated = !referenceSet.HasReferences && referenceTerrain == null;
        var boundaries = TerrainBuildSnapshotResolver.ResolveCurves(snapshot, boundarySet);
        var setupTimer = Stopwatch.StartNew();
        ReferenceProjectionContext projection = ResolveReferenceProjection(
            snapshot, fallbackBaseMesh, referenceSet, referenceTerrainId, referenceProjectionCache);
        build.RecordTiming("Reference Projection Setup", setupTimer.Elapsed, $"{currentFaces.Length / 3:N0} faces to compare", StageTimingDiagnosticThresholdMs);

        var volumeTimer = Stopwatch.StartNew();
        ReferenceComparisonStats stats = EstimateReferenceComparison(
            projection,
            currentVertices,
            currentFaces,
            currentFaces.Length / 3,
            boundaries,
            snapshot.ModelAbsoluteTolerance,
            isEstimated,
            shouldCancel);

        build.RecordTiming("Reference Comparison Volumes", volumeTimer.Elapsed, $"{currentFaces.Length / 3:N0} faces", StageTimingDiagnosticThresholdMs);
        referenceComparisonCache[cacheKey] = stats;
        if (stats.OverhangFaceCount > 0)
        {
            build.Diagnostics.Add(
                $"Reference comparison: {stats.OverhangFaceCount:N0} face(s) lie over overlapping reference triangles " +
                "(an overhang) and were measured against the nearest reference sheet.");
        }

        return stats;
    }

    /// <summary>
    /// The cached height lookup for a comparison's reference surface.
    ///
    /// Projection depends only on the reference geometry, so it is keyed and cached more loosely than the
    /// statistics that use it (those also depend on the current mesh and the clipping boundary). Shared by
    /// the volume statistics and by the delta field the drawn outputs contour, so a cut/fill card cannot
    /// colour against one reference and draw against another.
    /// </summary>
    private static ReferenceProjectionContext ResolveReferenceProjection(
        TerrainBuildSnapshot snapshot,
        RhinoMesh fallbackBaseMesh,
        SourceReferenceSet referenceSet,
        Guid? referenceTerrainId,
        Dictionary<ReferenceProjectionCacheKey, ReferenceProjectionContext> referenceProjectionCache)
    {
        TerrainSectionReferenceSnapshot? referenceTerrain = null;
        ulong referenceTerrainFingerprint = 0;
        if (referenceTerrainId.HasValue &&
            snapshot.SectionTerrains.TryGetValue(referenceTerrainId.Value, out referenceTerrain))
            referenceTerrainFingerprint = referenceTerrain.MeshFingerprint;

        var key = new ReferenceProjectionCacheKey(
            ComputeSourceSetFingerprint(snapshot, referenceSet),
            referenceTerrainFingerprint,
            !referenceSet.HasReferences && referenceTerrain == null);

        if (!referenceProjectionCache.TryGetValue(key, out ReferenceProjectionContext? projection))
        {
            RhinoMesh baseMesh = ResolveReferenceMesh(snapshot, referenceSet) ?? referenceTerrain?.Mesh ?? fallbackBaseMesh;
            projection = CreateReferenceProjectionContext(baseMesh);
            referenceProjectionCache[key] = projection;
        }

        return projection;
    }

    private static ReferenceProjectionContext CreateReferenceProjectionContext(RhinoMesh baseMesh)
    {
        MeshHeightProjector? projector = null;
        ReferenceSurface? surface = null;
        if (RhinoGeometryConversions.TryExtractMeshData(baseMesh, out var baseVertices, out _, out var baseFaces, out _, out _))
        {
            projector = new MeshHeightProjector(baseVertices, baseVertices.Length / 3, baseFaces, baseFaces.Length / 3);
            surface = new ReferenceSurface(baseVertices, baseVertices.Length / 3, baseFaces, baseFaces.Length / 3);
        }

        return new ReferenceProjectionContext
        {
            Mesh = baseMesh,
            Projector = projector,
            Surface = surface
        };
    }

    private static void GetElevationRange(double[] vertices, int vertexCount, out double minZ, out double maxZ)
    {
        minZ = double.MaxValue;
        maxZ = double.MinValue;
        for (int i = 0; i < vertexCount; i++)
        {
            double z = vertices[i * 3 + 2];
            if (z < minZ)
                minZ = z;
            if (z > maxZ)
                maxZ = z;
        }

        if (minZ == double.MaxValue)
            minZ = 0.0;
        if (maxZ == double.MinValue)
            maxZ = 0.0;
    }

    internal static List<double> BuildContourLevels(double minZ, double maxZ, double startZ, double interval)
    {
        var levels = new List<double>();
        if (!(interval > 0.0) || !double.IsFinite(interval) || maxZ < minZ)
            return levels;

        long firstIndex = (long)Math.Ceiling(((minZ - startZ) / interval) - 1e-9);
        long lastIndex = (long)Math.Floor(((maxZ - startZ) / interval) + 1e-9);
        if (lastIndex < firstIndex)
            return levels;

        for (long index = firstIndex; index <= lastIndex; index++)
            levels.Add(startZ + (index * interval));

        return levels;
    }

    private static double ConvertSlopeUnitToPercent(double slopeValue, SlopeAnalyzer.SlopeUnit unit)
    {
        return SlopeAnalyzer.ConvertUnitToRatio(slopeValue, unit) * 100.0;
    }

    /// <summary>
    /// Cut and fill of the current mesh against the reference, exact: the two triangulations are overlaid in
    /// plan and the height difference integrated piece by piece (<see cref="SurfaceDifferenceVolume"/>).
    /// This used to read the reference once per face, at its centroid, which is exact only where the
    /// reference is flat across the face; over a coarse terrain a large graded face read hundreds of cubic
    /// metres off. A clipping boundary still selects whole faces by their centroid, as the colour map does.
    /// </summary>
    private static ReferenceComparisonStats EstimateReferenceComparison(
        ReferenceProjectionContext projection,
        double[] currentVertices,
        int[] currentFaces,
        int faceCount,
        IReadOnlyList<Curve> boundaries,
        double tolerance,
        bool isEstimated,
        Func<bool>? shouldCancel)
    {
        if (projection.Surface == null)
            return new ReferenceComparisonStats(0.0, 0.0, 0.0, isEstimated, 0);

        bool[]? include = null;
        if (boundaries.Count > 0)
        {
            // Boundary containment is a Rhino curve call, so the selection is made here, on this thread.
            include = new bool[faceCount];
            for (int face = 0; face < faceCount; face++)
            {
                if ((face & 127) == 0)
                    ThrowIfCancellationRequested(shouldCancel);
                int a = currentFaces[face * 3], b = currentFaces[face * 3 + 1], c = currentFaces[face * 3 + 2];
                var centroid = new Point3d(
                    (currentVertices[a * 3] + currentVertices[b * 3] + currentVertices[c * 3]) / 3.0,
                    (currentVertices[a * 3 + 1] + currentVertices[b * 3 + 1] + currentVertices[c * 3 + 1]) / 3.0,
                    (currentVertices[a * 3 + 2] + currentVertices[b * 3 + 2] + currentVertices[c * 3 + 2]) / 3.0);
                include[face] = TerrainAnalysisPreviewBuilder.IsInsideBoundaries(centroid, boundaries, tolerance);
            }
        }

        SurfaceDifference difference;
        try
        {
            difference = SurfaceDifferenceVolume.Measure(
                currentVertices, currentFaces, faceCount, projection.Surface, tolerance, include, shouldCancel: shouldCancel);
        }
        catch (OperationCanceledException)
        {
            ThrowIfCancellationRequested(() => true);
            throw;
        }

        return new ReferenceComparisonStats(
            difference.Cut, difference.Fill, difference.AbsMax, isEstimated, difference.FoldFaceCount);
    }

    private static bool TryProjectReferencePoint(
        ReferenceProjectionContext projection,
        Point3d point,
        double tolerance,
        out Point3d projectedPoint)
    {
        MeshHeightProjector.ProjectionStatus status = MeshHeightProjector.ProjectionStatus.OutsideMesh;
        if (projection.Projector != null)
        {
            if (projection.Projector.TryProjectZ(
                point.X,
                point.Y,
                point.Z,
                tolerance,
                out double z,
                out status))
            {
                projectedPoint = new Point3d(point.X, point.Y, z);
                return true;
            }
        }

        if (projection.Projector == null || status == MeshHeightProjector.ProjectionStatus.RequiresFallback)
        {
            return projection.TryFallbackProject(point, tolerance, out projectedPoint);
        }

        projectedPoint = Point3d.Unset;
        return false;
    }

    private static RhinoMesh? ResolveReferenceMesh(TerrainBuildSnapshot snapshot, SourceReferenceSet referenceSet)
    {
        var meshes = TerrainBuildSnapshotResolver.ResolveMeshes(snapshot, referenceSet);
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

    internal static void ThrowIfCancellationRequested(Func<bool>? shouldCancel)
    {
        if (shouldCancel?.Invoke() == true)
            throw new OperationCanceledException("Terrain rebuild cancelled.");
    }
}
