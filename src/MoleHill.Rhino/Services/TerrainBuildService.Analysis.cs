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
        TerrainBuildResult build,
        Func<bool>? shouldCancel)
    {
        var results = new List<TerrainAnalysisSummary>(terrain.Analyses.Count);
        ThrowIfCancellationRequested(shouldCancel);
        if (!RhinoGeometryConversions.TryExtractMeshData(currentMesh, out var currentVertices, out var currentFaces, out _))
            return results;

        GetElevationRange(currentVertices, currentMesh.Vertices.Count, out double elevMinZ, out double elevMaxZ);
        double surfaceArea = AreaMassProperties.Compute(currentMesh)?.Area ?? 0.0;

        foreach (var analysis in terrain.Analyses)
        {
            ThrowIfCancellationRequested(shouldCancel);

            // A disabled analysis produces no output, so skip its (sometimes expensive) computation
            // entirely instead of computing it and discarding the result — this was a per-solve cost,
            // most painfully for contours, which scanned the whole mesh per level even when disabled.
            if (!analysis.IsEnabled)
                continue;

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
        }

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
        var palette = SlopePreviewPaletteCatalog.Resolve(analysis.PalettePreset);
        var slope = SlopeAnalyzer.Analyze(
            currentVertices,
            currentMesh.Vertices.Count,
            currentFaces,
            currentMesh.Faces.Count,
            SlopeAnalyzer.SlopeUnit.Percent,
            Math.Max(0.0, lowPercent),
            Math.Max(0.0, highPercent),
            palette.Stops);

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

            foreach (var contourLevel in contourLevels)
            {
                int levelCurveIndex = 0;
                bool levelHasCurves = false;
                foreach (var polyline in contourLevel.Polylines)
                {
                    if (polyline.PointCount < 2)
                        continue;

                    levelHasCurves = true;
                    contourCurveCount++;
                    if (!analysis.IsEnabled)
                        continue;

                    levelCurveIndex++;
                    objects.Add(new GeneratedRhinoObject
                    {
                        Geometry = new PolylineCurve(ToRhinoPolyline(polyline)),
                        Name = levelCurveIndex == 1
                            ? $"{analysis.Label} {contourLevel.Z:G4}"
                            : $"{analysis.Label} {contourLevel.Z:G4} ({levelCurveIndex})",
                        AnalysisId = analysis.Id,
                        ColorArgb = analysis.ColorArgb,
                        LayerPath = analysis.OutputLayerPath ?? fallbackLayerPath
                    });
                }

                if (!levelHasCurves)
                    continue;

                contourLevelCount++;
                if (contourLevelCount == 1)
                    firstLevel = contourLevel.Z;
                lastLevel = contourLevel.Z;
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
        Func<bool>? shouldCancel)
    {
        RhinoMesh baseMesh = ResolveReferenceMesh(snapshot, referenceSet) ?? fallbackBaseMesh;
        var boundaries = TerrainBuildSnapshotResolver.ResolveCurves(snapshot, boundarySet);
        EstimateEarthworks(baseMesh, currentMesh, boundaries, snapshot.ModelAbsoluteTolerance, out double cutVolume, out double fillVolume, shouldCancel);

        return new ReferenceComparisonStats(
            cutVolume,
            fillVolume,
            ComputeCutFillDisplayAbsMax(baseMesh, currentVertices, currentFaces, currentMesh.Faces.Count, boundaries, snapshot.ModelAbsoluteTolerance),
            !referenceSet.HasReferences);
    }

    private static double ComputeCutFillDisplayAbsMax(
        RhinoMesh baseMesh,
        double[] currentVertices,
        int[] currentFaces,
        int faceCount,
        IReadOnlyList<Curve> boundaries,
        double tolerance)
    {
        double cutFillAbsMax = 0.0;
        if (!RhinoGeometryConversions.TryExtractMeshData(baseMesh, out _, out _, out _))
            return cutFillAbsMax;

        for (int fi = 0; fi < faceCount; fi++)
        {
            int a = currentFaces[fi * 3];
            int b = currentFaces[fi * 3 + 1];
            int c = currentFaces[fi * 3 + 2];
            var centroid = new Point3d(
                (currentVertices[a * 3] + currentVertices[b * 3] + currentVertices[c * 3]) / 3.0,
                (currentVertices[a * 3 + 1] + currentVertices[b * 3 + 1] + currentVertices[c * 3 + 1]) / 3.0,
                (currentVertices[a * 3 + 2] + currentVertices[b * 3 + 2] + currentVertices[c * 3 + 2]) / 3.0);
            if (!IsInsideBoundaries(centroid, boundaries, tolerance))
                continue;

            if (!TerrainMeshProjection.TryProjectPointAlongWorldZ(baseMesh, centroid, tolerance, out Point3d basePoint))
                continue;

            double delta = Math.Abs(centroid.Z - basePoint.Z);
            if (delta > cutFillAbsMax)
                cutFillAbsMax = delta;
        }

        return cutFillAbsMax;
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

    private static void EstimateEarthworks(RhinoMesh baseMesh, RhinoMesh currentMesh, IReadOnlyList<Curve> boundaries, double tolerance, out double cutVolume, out double fillVolume, Func<bool>? shouldCancel)
    {
        cutVolume = 0.0;
        fillVolume = 0.0;

        if (!RhinoGeometryConversions.TryExtractMeshData(currentMesh, out var currentVertices, out var currentFaces, out _))
            return;

        for (int faceIndex = 0; faceIndex < currentMesh.Faces.Count; faceIndex++)
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

            if (!TerrainMeshProjection.TryProjectPointAlongWorldZ(baseMesh, centroid, tolerance, out Point3d basePoint))
                continue;

            double deltaZ = centroid.Z - basePoint.Z;
            double projectedArea = Math.Abs(
                (pb.X - pa.X) * (pc.Y - pa.Y) -
                (pb.Y - pa.Y) * (pc.X - pa.X)) * 0.5;

            double volume = projectedArea * deltaZ;
            if (volume >= 0)
                fillVolume += volume;
            else
                cutVolume += -volume;
        }
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
