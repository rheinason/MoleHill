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

// Analysis stage: slope, earthwork/cut-fill, and contour summaries plus their preview/earthwork helpers.
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
        var results = new List<TerrainAnalysisSummary>(terrain.Analyses.Count);
        ThrowIfCancellationRequested(shouldCancel);
        double[] currentVertices = Array.Empty<double>();
        int[] currentFaces = Array.Empty<int>();
        double elevMinZ = 0.0;
        double elevMaxZ = 0.0;
        double surfaceArea = 0.0;
        bool analysisContextPrepared = false;
        var referenceComparisonCache = new Dictionary<ReferenceComparisonCacheKey, ReferenceComparisonStats>();

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

        foreach (var analysis in terrain.Analyses)
        {
            ThrowIfCancellationRequested(shouldCancel);

            // A disabled analysis produces no output, so skip its (sometimes expensive) computation
            // entirely instead of computing it and discarding the result — this was a per-solve cost,
            // most painfully for contours, which scanned the whole mesh per level even when disabled.
            if (!analysis.IsEnabled)
                continue;

            string stageKey = TerrainStageKey.ForMode(TerrainBuildMode.Final, $"analysis:{analysis.Id:N}");
            usedStageKeys.Add(stageKey);
            ulong fingerprint = ComputeAnalysisFingerprint(
                snapshot,
                terrain,
                analysis,
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
                    $"Analysis {analysis.Label}",
                    analysisTimer.Elapsed,
                    AppendCacheHitDetail($"{cachedResults.Count:N0} summaries, {cachedEntry.AuxiliaryObjects.Count:N0} outputs"),
                    isCacheHit: true);
                continue;
            }

            if (!EnsureAnalysisContext())
                break;

            int diagnosticsStart = build.Diagnostics.Count;
            int structuredDiagnosticsStart = build.StructuredDiagnostics.Count;
            int auxiliaryStart = build.AuxiliaryObjects.Count;

            TerrainAnalysisSummary? summary = analysis switch
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
                CurveSlopeLabelAnalysisDefinition curveSlope => TerrainAnalysisAnnotationBuilder.BuildCurveSlopeSummary(
                    snapshot,
                    currentMesh,
                    curveSlope,
                    build,
                    shouldCancel,
                    TerrainDefinition.ResolveAnnotationLayerPath(terrain.AnnotationLayerPath)),
                CurveElevationLabelAnalysisDefinition curveElevation => TerrainAnalysisAnnotationBuilder.BuildCurveElevationSummary(
                    snapshot,
                    currentMesh,
                    curveElevation,
                    build,
                    shouldCancel,
                    TerrainDefinition.ResolveAnnotationLayerPath(terrain.AnnotationLayerPath)),
                ProjectedElevationLabelAnalysisDefinition projectedElevation => TerrainAnalysisAnnotationBuilder.BuildProjectedElevationSummary(
                    snapshot,
                    currentMesh,
                    projectedElevation,
                    build,
                    shouldCancel,
                    TerrainDefinition.ResolveAnnotationLayerPath(terrain.AnnotationLayerPath)),
                PointSlopeLabelAnalysisDefinition pointSlope => TerrainAnalysisAnnotationBuilder.BuildPointSlopeSummary(
                    snapshot,
                    currentMesh,
                    pointSlope,
                    build,
                    shouldCancel,
                    TerrainDefinition.ResolveAnnotationLayerPath(terrain.AnnotationLayerPath)),
                SlopeArrowAnalysisDefinition slopeArrows => TerrainAnalysisAnnotationBuilder.BuildSlopeArrowSummary(
                    snapshot,
                    currentMesh,
                    slopeArrows,
                    build,
                    shouldCancel,
                    TerrainDefinition.ResolveAnnotationLayerPath(terrain.AnnotationLayerPath)),
                GradeBetweenPointsAnalysisDefinition gradeCallout => TerrainAnalysisAnnotationBuilder.BuildGradeCalloutSummary(
                    snapshot,
                    currentMesh,
                    gradeCallout,
                    build,
                    shouldCancel,
                    TerrainDefinition.ResolveAnnotationLayerPath(terrain.AnnotationLayerPath)),
                TerrainSectionAnalysisDefinition terrainSection => TerrainAnalysisAnnotationBuilder.BuildTerrainSectionSummary(
                    snapshot,
                    currentMesh,
                    terrainSection,
                    build,
                    shouldCancel,
                    TerrainDefinition.ResolveAnnotationLayerPath(terrain.AnnotationLayerPath)),
                CrossSectionStationAnalysisDefinition crossSection => TerrainAnalysisAnnotationBuilder.BuildCrossSectionStationSummary(
                    snapshot,
                    currentMesh,
                    crossSection,
                    build,
                    shouldCancel,
                    TerrainDefinition.ResolveAnnotationLayerPath(terrain.AnnotationLayerPath)),
                LongitudinalSectionAnalysisDefinition longitudinal => TerrainAnalysisAnnotationBuilder.BuildLongitudinalSectionSummary(
                    snapshot,
                    currentMesh,
                    longitudinal,
                    build,
                    shouldCancel,
                    TerrainDefinition.ResolveAnnotationLayerPath(terrain.AnnotationLayerPath)),
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
                    shouldCancel),
                ContourAnalysisDefinition contour => BuildContourSummary(
                    terrain,
                    currentMesh,
                    contour,
                    elevMinZ,
                    elevMaxZ,
                    snapshot.ModelAbsoluteTolerance,
                    build),
                _ => null
            };

            if (summary != null)
                results.Add(summary);

            analysisTimer.Stop();
            runtimeCache.StageEntries[stageKey] = new StageCacheEntry
            {
                StageName = $"Analysis {analysis.Label}",
                PreResolutionFingerprint = fingerprint,
                ResolvedInputFingerprint = fingerprint,
                OutputFingerprint = fingerprint,
                AnalysisOutput = summary == null
                    ? new List<TerrainAnalysisSummary>()
                    : TerrainRuntimeCacheCloner.CloneAnalyses(new[] { summary }),
                AuxiliaryObjects = TerrainRuntimeCacheCloner.CloneGeneratedObjects(build.AuxiliaryObjects.Skip(auxiliaryStart)),
                Diagnostics = build.Diagnostics.Skip(diagnosticsStart).ToList(),
                StructuredDiagnostics = build.StructuredDiagnostics.Skip(structuredDiagnosticsStart).ToList()
            };
            build.RecordTiming(
                $"Analysis {analysis.Label}",
                analysisTimer.Elapsed,
                $"{(summary == null ? 0 : 1):N0} summaries, {build.AuxiliaryObjects.Count - auxiliaryStart:N0} outputs");
        }

        totalTimer.Stop();
        build.RecordTiming("Analysis", totalTimer.Elapsed, $"{results.Count:N0} enabled analyses");
        return results;
    }

    private static TerrainAnalysisSummary BuildSlopeSummary(
        RhinoMesh currentMesh,
        double[] currentVertices,
        int[] currentFaces,
        SlopeAnalysisDefinition analysis,
        double surfaceArea)
    {
        double lowPercent = ConvertSlopeUnitToPercent(analysis.RangeLow, analysis.Unit);
        double highPercent = ConvertSlopeUnitToPercent(analysis.RangeHigh, analysis.Unit);
        var slope = SlopeAnalyzer.Summarize(
            currentVertices,
            currentMesh.Vertices.Count,
            currentFaces,
            currentMesh.Faces.Count,
            SlopeAnalyzer.SlopeUnit.Percent,
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
        Func<bool>? shouldCancel)
    {
        var stats = ComputeReferenceComparisonStats(
            snapshot,
            fallbackBaseMesh,
            currentMesh,
            currentVertices,
            currentFaces,
            analysis.Reference,
            analysis.Boundary,
            build,
            referenceComparisonCache,
            shouldCancel);

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
        Func<bool>? shouldCancel)
    {
        var stats = ComputeReferenceComparisonStats(
            snapshot,
            fallbackBaseMesh,
            currentMesh,
            currentVertices,
            currentFaces,
            analysis.Reference,
            analysis.Boundary,
            build,
            referenceComparisonCache,
            shouldCancel);

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
        ContourAnalysisDefinition analysis,
        double elevMinZ,
        double elevMaxZ,
        double tolerance,
        TerrainBuildResult build)
    {
        var (objects, summary) = BuildContourCore(currentMesh, analysis, elevMinZ, elevMaxZ, tolerance,
            TerrainDefinition.ResolveAnnotationLayerPath(terrain.AnnotationLayerPath));
        build.AuxiliaryObjects.AddRange(objects);
        return summary;
    }

    internal static (List<GeneratedRhinoObject> Objects, TerrainAnalysisSummary Summary) BuildContourObjects(
        RhinoMesh mesh,
        ContourAnalysisDefinition analysis,
        double tolerance = 1e-4)
    {
        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out _, out _))
            return (new List<GeneratedRhinoObject>(), new TerrainAnalysisSummary { AnalysisId = analysis.Id });

        GetElevationRange(vertices, mesh.Vertices.Count, out double minZ, out double maxZ);
        return BuildContourCore(mesh, analysis, minZ, maxZ, tolerance);
    }

    private static (List<GeneratedRhinoObject> Objects, TerrainAnalysisSummary Summary) BuildContourCore(
        RhinoMesh mesh,
        ContourAnalysisDefinition analysis,
        double elevMinZ,
        double elevMaxZ,
        double tolerance,
        string? fallbackLayerPath = null)
    {
        var objects = new List<GeneratedRhinoObject>();
        int contourCurveCount = 0;
        int contourLevelCount = 0;
        double firstLevel = 0.0;
        double lastLevel = 0.0;

        if (RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out var faces, out _))
        {
            var levels = BuildContourLevels(elevMinZ, elevMaxZ, analysis.StartZ, Math.Max(analysis.Interval, 0.01));

            // Single pass over the faces (marching triangles) instead of one mesh-plane intersection
            // per level. Each triangle only contributes to the levels inside its own Z-span.
            var contourLevels = ContourGenerator.Generate(
                vertices, mesh.Vertices.Count, faces, mesh.Faces.Count, levels, Math.Max(tolerance, 1e-6));

            int everyNth = Math.Max(1, analysis.LabelEveryNth);
            bool wantLabels = analysis.ShowLabels && analysis.IsEnabled;
            string? outputLayerPath = ResolveContourOutputLayerPath(analysis.OutputLayerPath, fallbackLayerPath);

            foreach (var contourLevel in contourLevels)
            {
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
                        LayerPath = outputLayerPath
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
                        EmitContourLabels(objects, rhinoPolyline, contourLevel.Z, analysis, outputLayerPath);
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

    internal static string? ResolveContourOutputLayerPath(string? outputLayerPath, string? fallbackLayerPath)
    {
        if (!string.IsNullOrWhiteSpace(outputLayerPath))
            return outputLayerPath;

        return string.IsNullOrWhiteSpace(fallbackLayerPath) ? null : fallbackLayerPath;
    }

    private static void EmitContourLabels(
        List<GeneratedRhinoObject> objects,
        Polyline polyline,
        double levelZ,
        ContourAnalysisDefinition analysis,
        string? layerPath)
    {
        if (polyline.Count < 2)
            return;

        var curve = new PolylineCurve(polyline);
        double length = curve.GetLength();
        if (length <= 1e-9)
            return;

        double textHeight = Math.Max(analysis.LabelTextHeight, 1e-3);
        string text = FormatContourLabel(levelZ, analysis.LabelFormat);

        // Repeat along the contour when an interval is set; otherwise a single label at the midpoint.
        var stations = new List<double>();
        if (analysis.LabelInterval > 1e-9)
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

    private static ReferenceComparisonStats ComputeReferenceComparisonStats(
        TerrainBuildSnapshot snapshot,
        RhinoMesh fallbackBaseMesh,
        RhinoMesh currentMesh,
        double[] currentVertices,
        int[] currentFaces,
        SourceReferenceSet referenceSet,
        SourceReferenceSet boundarySet,
        TerrainBuildResult build,
        Dictionary<ReferenceComparisonCacheKey, ReferenceComparisonStats> referenceComparisonCache,
        Func<bool>? shouldCancel)
    {
        var cacheKey = new ReferenceComparisonCacheKey(
            ComputeSourceSetFingerprint(snapshot, referenceSet),
            ComputeSourceSetFingerprint(snapshot, boundarySet),
            !referenceSet.HasReferences);
        if (referenceComparisonCache.TryGetValue(cacheKey, out ReferenceComparisonStats cachedStats))
            return cachedStats;

        RhinoMesh baseMesh = ResolveReferenceMesh(snapshot, referenceSet) ?? fallbackBaseMesh;
        var boundaries = TerrainBuildSnapshotResolver.ResolveCurves(snapshot, boundarySet);
        var projection = CreateReferenceProjectionContext(baseMesh);

        ReferenceComparisonStats stats = EstimateReferenceComparison(
            projection,
            currentVertices,
            currentFaces,
            currentFaces.Length / 3,
            boundaries,
            snapshot.ModelAbsoluteTolerance,
            !referenceSet.HasReferences,
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
        if (interval <= 1e-9 || maxZ < minZ)
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

    private static RhinoMesh BuildSlopePreviewMesh(
        double[] vertices,
        int[] faces,
        int faceCount,
        SlopeAnalyzer.SlopeResult slope)
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
                slope.FaceColors[faceIndex * 3],
                slope.FaceColors[faceIndex * 3 + 1],
                slope.FaceColors[faceIndex * 3 + 2]);
            coloredMesh.VertexColors.Add(color);
            coloredMesh.VertexColors.Add(color);
            coloredMesh.VertexColors.Add(color);
        }

        coloredMesh.Normals.ComputeNormals();
        coloredMesh.UnifyNormals();
        coloredMesh.Compact();
        return coloredMesh;
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
            projection.GridProjectionCount,
            projection.FallbackProjectionCount);
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
