using System.Diagnostics;
using System.Text.Json;
using MoleHill.Core.Analysis;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using MoleHill.Core.Processing;
using MoleHill.Rhino.Model;
using MoleHill.Shared;
using Rhino;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using TriangleNet.Meshing;
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
        Func<bool>? shouldCancel)
    {
        var totalTimer = Stopwatch.StartNew();
        var results = new List<TerrainAnalysisSummary>(terrain.Analyses.Count + terrain.Annotations.Count);
        ThrowIfCancellationRequested(shouldCancel);
        double[] currentVertices = Array.Empty<double>();
        int[] currentFaces = Array.Empty<int>();
        double elevMinZ = 0.0;
        double elevMaxZ = 0.0;
        double surfaceArea = 0.0;
        bool analysisContextPrepared = false;
        var referenceComparisonCache = new Dictionary<ReferenceComparisonCacheKey, ReferenceComparisonStats>();
        var referenceProjectionCache = new Dictionary<ReferenceProjectionCacheKey, ReferenceProjectionContext>();

        bool EnsureAnalysisContext()
        {
            if (analysisContextPrepared)
                return true;

            if (!RhinoGeometryConversions.TryExtractMeshData(currentMesh, out double[] vertices, out int[] faces, out _))
                return false;

            currentVertices = vertices;
            currentFaces = faces;
            GetElevationRange(currentVertices, currentMesh.Vertices.Count, out elevMinZ, out elevMaxZ);
            surfaceArea = AreaMassProperties.Compute(currentMesh)?.Area ?? 0.0;
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
            RunStage(current, "Analysis", "analysis", () => current switch
            {
                EarthworkAnalysisDefinition earthwork => BuildEarthworkSummary(
                    snapshot,
                    fallbackBaseMesh,
                    currentMesh,
                    currentVertices,
                    currentFaces,
                    earthwork,
                    surfaceArea,
                    elevMinZ,
                    elevMaxZ,
                    build,
                    referenceComparisonCache,
                    referenceProjectionCache,
                    shouldCancel),
                SlopeAnalysisDefinition slope => BuildSlopeSummary(
                    currentMesh,
                    currentVertices,
                    currentFaces,
                    slope,
                    surfaceArea),
                ElevationAnalysisDefinition => new TerrainAnalysisSummary
                {
                    AnalysisId = analysis.Id,
                    SurfaceArea = surfaceArea,
                    ElevationMinZ = elevMinZ,
                    ElevationMaxZ = elevMaxZ
                },
                WaterflowAnalysisDefinition waterflow => BuildWaterflowSummary(
                    snapshot,
                    currentMesh,
                    currentVertices,
                    currentFaces,
                    waterflow,
                    build,
                    shouldCancel),
                CutFillAnalysisDefinition cutFill => BuildCutFillSummary(
                    snapshot,
                    fallbackBaseMesh,
                    currentMesh,
                    currentVertices,
                    currentFaces,
                    cutFill,
                    surfaceArea,
                    elevMinZ,
                    elevMaxZ,
                    build,
                    referenceComparisonCache,
                    referenceProjectionCache,
                    shouldCancel),
                _ => null
            });
        }

        foreach (var annotation in terrain.Annotations)
        {
            ThrowIfCancellationRequested(shouldCancel);
            AnnotationDefinition current = annotation;
            RunStage(current, "Annotation", "annotation", () => current switch
            {
                CurveSlopeLabelAnnotationDefinition curveSlope => TerrainAnalysisAnnotationBuilder.BuildCurveSlopeSummary(
                    snapshot,
                    currentMesh,
                    curveSlope,
                    build,
                    shouldCancel,
                    snapshot.LayerRoles),
                CurveElevationLabelAnnotationDefinition curveElevation => TerrainAnalysisAnnotationBuilder.BuildCurveElevationSummary(
                    snapshot,
                    currentMesh,
                    curveElevation,
                    build,
                    shouldCancel,
                    snapshot.LayerRoles),
                ProjectedElevationLabelAnnotationDefinition projectedElevation => TerrainAnalysisAnnotationBuilder.BuildProjectedElevationSummary(
                    snapshot,
                    currentMesh,
                    projectedElevation,
                    build,
                    shouldCancel,
                    snapshot.LayerRoles),
                PointSlopeLabelAnnotationDefinition pointSlope => TerrainAnalysisAnnotationBuilder.BuildPointSlopeSummary(
                    snapshot,
                    currentMesh,
                    pointSlope,
                    build,
                    shouldCancel,
                    snapshot.LayerRoles),
                SlopeArrowAnnotationDefinition slopeArrows => TerrainAnalysisAnnotationBuilder.BuildSlopeArrowSummary(
                    snapshot,
                    currentMesh,
                    slopeArrows,
                    build,
                    shouldCancel,
                    snapshot.LayerRoles),
                GradeBetweenPointsAnnotationDefinition gradeCallout => TerrainAnalysisAnnotationBuilder.BuildGradeCalloutSummary(
                    snapshot,
                    currentMesh,
                    gradeCallout,
                    build,
                    shouldCancel,
                    snapshot.LayerRoles),
                TerrainSectionAnnotationDefinition terrainSection => TerrainAnalysisAnnotationBuilder.BuildTerrainSectionSummary(
                    snapshot,
                    currentMesh,
                    terrainSection,
                    build,
                    shouldCancel,
                    snapshot.LayerRoles,
                    fallbackBaseMesh),
                CrossSectionStationAnnotationDefinition crossSection => TerrainAnalysisAnnotationBuilder.BuildCrossSectionStationSummary(
                    snapshot,
                    currentMesh,
                    crossSection,
                    build,
                    shouldCancel,
                    snapshot.LayerRoles,
                    fallbackBaseMesh),
                LongitudinalSectionAnnotationDefinition longitudinal => TerrainAnalysisAnnotationBuilder.BuildLongitudinalSectionSummary(
                    snapshot,
                    currentMesh,
                    longitudinal,
                    build,
                    shouldCancel,
                    snapshot.LayerRoles,
                    fallbackBaseMesh),
                ContourAnnotationDefinition contour => BuildContourSummary(
                    terrain,
                    currentMesh,
                    contour,
                    elevMinZ,
                    elevMaxZ,
                    snapshot.ModelAbsoluteTolerance,
                    build,
                    snapshot.AnnotationStyle,
                    snapshot.LayerRoles),
                _ => null
            });
        }

        totalTimer.Stop();
        build.RecordTiming("Analysis", totalTimer.Elapsed, $"{results.Count:N0} enabled analyses and annotations");
        return results;
    }

    private static TerrainAnalysisSummary BuildSlopeSummary(
        RhinoMesh currentMesh,
        double[] currentVertices,
        int[] currentFaces,
        SlopeAnalysisDefinition analysis,
        double surfaceArea)
    {
        // Auto-fit must be honoured here exactly as the preview mesh honours it, or the "Mapped" row and
        // the legend describe a range the terrain was never coloured with.
        double lowPercent = ConvertSlopeUnitToPercent(analysis.RangeLow, analysis.Unit);
        double highPercent = ConvertSlopeUnitToPercent(analysis.RangeHigh, analysis.Unit);
        var slope = SlopeAnalyzer.Summarize(
            currentVertices,
            currentMesh.Vertices.Count,
            currentFaces,
            currentMesh.Faces.Count,
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

    private static TerrainAnalysisSummary BuildEarthworkSummary(
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

    private static TerrainAnalysisSummary BuildCutFillSummary(
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

        return new TerrainAnalysisSummary
        {
            AnalysisId = analysis.Id,
            SurfaceArea = surfaceArea,
            ElevationMinZ = elevMinZ,
            ElevationMaxZ = elevMaxZ,
            CutFillDisplayAbsMax = stats.CutFillDisplayAbsMax,
            CutVolume = stats.CutVolume,
            FillVolume = stats.FillVolume,
            NetVolume = stats.NetVolume,
            EarthworkIsEstimated = stats.IsEstimated
        };
    }

    private static TerrainAnalysisSummary BuildContourSummary(
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
        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out _, out _))
            return (new List<GeneratedRhinoObject>(), new TerrainAnalysisSummary { AnalysisId = analysis.Id });

        GetElevationRange(vertices, mesh.Vertices.Count, out double minZ, out double maxZ);
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

        if (RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out var faces, out _))
        {
            double effectiveTolerance = Math.Max(Math.Abs(tolerance), double.Epsilon);
            var levels = BuildContourLevels(elevMinZ, elevMaxZ, analysis.StartZ, Math.Max(analysis.Interval, effectiveTolerance));

            // Single pass over the faces (marching triangles) instead of one mesh-plane intersection
            // per level. Each triangle only contributes to the levels inside its own Z-span.
            var contourLevels = ContourGenerator.Generate(
                vertices, mesh.Vertices.Count, faces, mesh.Faces.Count, levels, effectiveTolerance);

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
        var projectionKey = new ReferenceProjectionCacheKey(
            cacheKey.ReferenceFingerprint,
            referenceTerrainFingerprint,
            cacheKey.UsesFallbackBaseMesh);
        if (!referenceProjectionCache.TryGetValue(projectionKey, out ReferenceProjectionContext? projection))
        {
            // Projection depends only on the reference geometry. The result statistics also depend on
            // the current mesh and clipping boundary, and therefore use the stricter cache key above.
            RhinoMesh baseMesh = ResolveReferenceMesh(snapshot, referenceSet) ?? referenceTerrain?.Mesh ?? fallbackBaseMesh;
            projection = CreateReferenceProjectionContext(baseMesh);
            referenceProjectionCache[projectionKey] = projection;
        }

        ReferenceComparisonStats stats = EstimateReferenceComparison(
            projection,
            currentVertices,
            currentFaces,
            currentFaces.Length / 3,
            boundaries,
            snapshot.ModelAbsoluteTolerance,
            isEstimated,
            shouldCancel);

        referenceComparisonCache[cacheKey] = stats;
        if (stats.FallbackProjectionCount > 0)
        {
            build.Diagnostics.Add(
                $"Reference comparison projection used Rhino fallback for {stats.FallbackProjectionCount:N0} samples " +
                $"after {stats.GridProjectionCount:N0} 2.5D grid hits.");
        }

        return stats;
    }

    private static ReferenceProjectionContext CreateReferenceProjectionContext(RhinoMesh baseMesh)
    {
        MeshHeightProjector? projector = null;
        if (RhinoGeometryConversions.TryExtractMeshData(baseMesh, out var baseVertices, out var baseFaces, out _))
            projector = new MeshHeightProjector(baseVertices, baseVertices.Length / 3, baseFaces, baseFaces.Length / 3);

        return new ReferenceProjectionContext
        {
            Mesh = baseMesh,
            Projector = projector
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

    private static List<double> BuildContourLevels(double minZ, double maxZ, double startZ, double interval)
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
        int gridProjectionCountBefore = projection.GridProjectionCount;
        int fallbackProjectionCountBefore = projection.FallbackProjectionCount;
        double cutVolume = 0.0;
        double fillVolume = 0.0;
        double cutFillAbsMax = 0.0;

        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            if ((faceIndex & 127) == 0)
                ThrowIfCancellationRequested(shouldCancel);

            int a = currentFaces[faceIndex * 3];
            int b = currentFaces[faceIndex * 3 + 1];
            int c = currentFaces[faceIndex * 3 + 2];

            var pa = new Point3d(currentVertices[a * 3], currentVertices[a * 3 + 1], currentVertices[a * 3 + 2]);
            var pb = new Point3d(currentVertices[b * 3], currentVertices[b * 3 + 1], currentVertices[b * 3 + 2]);
            var pc = new Point3d(currentVertices[c * 3], currentVertices[c * 3 + 1], currentVertices[c * 3 + 2]);

            var centroid = new Point3d(
                (pa.X + pb.X + pc.X) / 3.0,
                (pa.Y + pb.Y + pc.Y) / 3.0,
                (pa.Z + pb.Z + pc.Z) / 3.0);

            if (!IsInsideBoundaries(centroid, boundaries, tolerance))
                continue;

            if (!TryProjectReferencePoint(projection, centroid, tolerance, out Point3d basePoint))
                continue;

            double deltaZ = centroid.Z - basePoint.Z;
            cutFillAbsMax = Math.Max(cutFillAbsMax, Math.Abs(deltaZ));

            double projectedArea = Math.Abs(
                (pb.X - pa.X) * (pc.Y - pa.Y) -
                (pb.Y - pa.Y) * (pc.X - pa.X)) * 0.5;

            double volume = projectedArea * deltaZ;
            if (volume >= 0)
                fillVolume += volume;
            else
                cutVolume += -volume;
        }

        return new ReferenceComparisonStats(
            cutVolume,
            fillVolume,
            cutFillAbsMax,
            isEstimated,
            projection.GridProjectionCount - gridProjectionCountBefore,
            projection.FallbackProjectionCount - fallbackProjectionCountBefore);
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
                projection.GridProjectionCount++;
                projectedPoint = new Point3d(point.X, point.Y, z);
                return true;
            }
        }

        if (projection.Projector == null || status == MeshHeightProjector.ProjectionStatus.RequiresFallback)
        {
            projection.FallbackProjectionCount++;
            return TerrainMeshProjection.TryProjectPointAlongWorldZ(projection.Mesh, point, tolerance, out projectedPoint);
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

    private static void ThrowIfCancellationRequested(Func<bool>? shouldCancel)
    {
        if (shouldCancel?.Invoke() == true)
            throw new OperationCanceledException("Terrain rebuild cancelled.");
    }

    private static bool IsInsideBoundaries(Point3d point, IReadOnlyList<Curve> boundaries, double tolerance)
    {
        if (boundaries.Count == 0)
            return true;

        foreach (var curve in boundaries)
        {
            var containment = curve.Contains(new Point3d(point.X, point.Y, curve.PointAtStart.Z), Plane.WorldXY, tolerance);
            if (containment == PointContainment.Inside || containment == PointContainment.Coincident)
                return true;
        }

        return false;
    }

}
