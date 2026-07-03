using System.Diagnostics;
using System.Text.Json;
using MoleHill.Core.Analysis;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using MoleHill.Core.Processing;
using MoleHill.Core.Retopo;
using MoleHill.Rhino.Model;
using MoleHill.Shared;
using Rhino;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using TriangleNet.Meshing;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

// TIN construction stages: base triangulation, Add-Geometry, validated build + cleanup retry, Remesh, and Smooth.
internal sealed partial class TerrainBuildService
{
    private static RhinoMesh? BuildTinMesh(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        TriangulateModifierDefinition modifier,
        TerrainBuildResult build,
        TerrainRuntimeCache runtimeCache,
        string stageKey,
        out ulong outputFingerprint,
        Func<bool>? shouldCancel = null)
    {
        const string stageName = "Triangulate";
        var timer = Stopwatch.StartNew();
        ulong preResolutionFingerprint = ComputeTriangulatePreResolutionFingerprint(snapshot, terrain, modifier);
        if (runtimeCache.StageEntries.TryGetValue(stageKey, out var cachedEntry) &&
            cachedEntry.PreResolutionFingerprint == preResolutionFingerprint)
        {
            RhinoMesh? cachedMesh = RestoreCachedMeshStage(build, cachedEntry, out outputFingerprint);
            timer.Stop();
            build.RecordTiming(stageName, timer.Elapsed, AppendCacheHitDetail(DescribeModifierMeshResult(modifier.Label, cachedMesh)));
            return cachedMesh;
        }

        int diagnosticsStart = build.Diagnostics.Count;
        int structuredDiagnosticsStart = build.StructuredDiagnostics.Count;
        ThrowIfCancellationRequested(shouldCancel);
        var points = TerrainBuildSnapshotResolver.ResolvePoints(snapshot, modifier.Points);
        var breaklineCurves = TerrainBuildSnapshotResolver.ResolveCurves(snapshot, modifier.Breaklines);
        var contourCurves = TerrainBuildSnapshotResolver.ResolveCurves(snapshot, modifier.Contours);
        var boundaryCurves = TerrainBuildSnapshotResolver.ResolveCurves(snapshot, modifier.Boundary);

        if (points.Count == 0 && breaklineCurves.Count == 0 && contourCurves.Count == 0)
        {
            build.Diagnostics.Add("Triangulate has no point, contour, or breakline sources.");
            return StoreMeshStageCache(
                build,
                runtimeCache,
                stageKey,
                stageName,
                preResolutionFingerprint,
                0,
                null,
                build.PersistentHardConstraints,
                Array.Empty<GeneratedRhinoObject>(),
                build.Diagnostics.Skip(diagnosticsStart),
                DescribeModifierMeshResult(modifier.Label, null),
                timer,
                out outputFingerprint,
                structuredDiagnostics: build.StructuredDiagnostics.Skip(structuredDiagnosticsStart));
        }

        TerrainTolerancePolicy.Profile toleranceProfile = GetToleranceProfile(snapshot, terrain);
        double inputTolerance = modifier.Tolerance > 0
            ? modifier.Tolerance
            : toleranceProfile.InputMergeTolerance;
        double curveTolerance = modifier.Tolerance > 0
            ? modifier.Tolerance
            : toleranceProfile.CurveChordTolerance;

        // Work-region crop: a Triangulate boundary limits which inputs are triangulated so the user can
        // work fast on a sub-area; clearing/growing the boundary restores the full terrain.
        double workBoundaryMargin = Math.Max(toleranceProfile.DetailSize * 3.0, curveTolerance);
        (points, breaklineCurves, contourCurves) = FilterInputsToWorkBoundary(
            points, breaklineCurves, contourCurves, boundaryCurves, curveTolerance, workBoundaryMargin, build);

        var spotXyz = new double[points.Count * 3];
        for (int i = 0; i < points.Count; i++)
        {
            spotXyz[i * 3] = points[i].X;
            spotXyz[i * 3 + 1] = points[i].Y;
            spotXyz[i * 3 + 2] = points[i].Z;
        }

        var persistentHardConstraints = CreateConstraintPolylines(
            breaklineCurves,
            curveTolerance,
            preserveInputElevation: true);
        var persistentElevationConstraints = CreateConstraintPolylines(
            contourCurves,
            curveTolerance,
            preserveInputElevation: true);

        ThrowIfCancellationRequested(shouldCancel);
        var polylines = TerrainTriangulationInputBuilder.CreateTriangulationPolylines(
            breaklineCurves,
            contourCurves,
            curveTolerance);
        var boundaryPolylines = CreateBoundaryPolylines(boundaryCurves, curveTolerance);

        var breaklineData = BreaklineDiscretizer.Process(polylines, shouldCancel);
        var merged = PointCloudProcessor.Merge(spotXyz, points.Count, breaklineData, inputTolerance, shouldCancel);
        ThrowIfCancellationRequested(shouldCancel);
        ulong resolvedInputFingerprint = ComputeTriangulateResolvedInputFingerprint(
            terrain,
            modifier,
            inputTolerance,
            merged.XyCoords,
            merged.ZValues,
            merged.Segments,
            persistentHardConstraints,
            boundaryPolylines);

        if (cachedEntry != null &&
            cachedEntry.ResolvedInputFingerprint == resolvedInputFingerprint)
        {
            StageCacheEntry refreshedEntry = CloneStageCacheEntry(
                cachedEntry,
                preResolutionFingerprint,
                resolvedInputFingerprint);
            runtimeCache.StageEntries[stageKey] = refreshedEntry;

            RhinoMesh? resolvedCachedMesh = RestoreCachedMeshStage(build, refreshedEntry, out outputFingerprint);
            timer.Stop();
            build.RecordTiming(stageName, timer.Elapsed, AppendCacheHitDetail(DescribeModifierMeshResult(modifier.Label, resolvedCachedMesh)));
            return resolvedCachedMesh;
        }

        if (merged.VertexCount < 3)
        {
            build.Diagnostics.Add("Triangulate needs at least three unique points after deduplication.");
            return StoreMeshStageCache(
                build,
                runtimeCache,
                stageKey,
                stageName,
                preResolutionFingerprint,
                resolvedInputFingerprint,
                null,
                build.PersistentHardConstraints,
                Array.Empty<GeneratedRhinoObject>(),
                build.Diagnostics.Skip(diagnosticsStart),
                DescribeModifierMeshResult(modifier.Label, null),
                timer,
                out outputFingerprint,
                structuredDiagnostics: build.StructuredDiagnostics.Skip(structuredDiagnosticsStart));
        }

        if (merged.InvalidsSkipped > 0)
            build.Diagnostics.Add($"{merged.DescribeInvalidPoints()} during triangulation.");
        if (merged.DuplicatesRemoved > 0)
            build.Diagnostics.Add($"{merged.DuplicatesRemoved} duplicate points merged during triangulation.");

        if (TryBuildValidatedTinMesh(
            merged.XyCoords,
            merged.ZValues,
            merged.Segments,
            boundaryPolylines,
            inputTolerance,
            modifier.CreateBoundaryPeelSettings(),
            runtimeCache.TinEngine,
            runtimeCache.CoreCaseRecorder,
            stageName,
            out var exactMesh,
            out var exactMessage,
            shouldCancel))
        {
            build.PersistentHardConstraints.Clear();
            build.PersistentHardConstraints.AddRange(persistentHardConstraints);
            build.PersistentElevationConstraints.Clear();
            build.PersistentElevationConstraints.AddRange(persistentElevationConstraints);
            if (!string.IsNullOrWhiteSpace(exactMessage))
                build.Diagnostics.Add(exactMessage);
            return StoreMeshStageCache(
                build,
                runtimeCache,
                stageKey,
                stageName,
                preResolutionFingerprint,
                resolvedInputFingerprint,
                exactMesh,
                build.PersistentHardConstraints,
                Array.Empty<GeneratedRhinoObject>(),
                build.Diagnostics.Skip(diagnosticsStart),
                DescribeModifierMeshResult(modifier.Label, exactMesh),
                timer,
                out outputFingerprint);
        }

        if (!ShouldAttemptTriangulationCleanupRetry(merged.VertexCount, merged.SegmentCount, shouldCancel, out var cleanupSkipMessage))
        {
            build.Diagnostics.Add(exactMessage ?? "Triangulation failed.");
            build.Diagnostics.Add(cleanupSkipMessage);
            return StoreMeshStageCache(
                build,
                runtimeCache,
                stageKey,
                stageName,
                preResolutionFingerprint,
                resolvedInputFingerprint,
                null,
                build.PersistentHardConstraints,
                Array.Empty<GeneratedRhinoObject>(),
                build.Diagnostics.Skip(diagnosticsStart),
                DescribeModifierMeshResult(modifier.Label, null),
                timer,
                out outputFingerprint,
                shouldCancel);
        }

        var cleanup = TinInputCleaner.Clean(merged, inputTolerance);
        ThrowIfCancellationRequested(shouldCancel);
        if (!cleanup.HasChanges)
        {
            build.Diagnostics.Add(exactMessage ?? "Triangulation failed.");
            build.Diagnostics.Add($"Automatic input cleanup made no safe changes: {cleanup.ToDiagnosticSummary()}.");
            return StoreMeshStageCache(
                build,
                runtimeCache,
                stageKey,
                stageName,
                preResolutionFingerprint,
                resolvedInputFingerprint,
                null,
                build.PersistentHardConstraints,
                Array.Empty<GeneratedRhinoObject>(),
                build.Diagnostics.Skip(diagnosticsStart),
                DescribeModifierMeshResult(modifier.Label, null),
                timer,
                out outputFingerprint);
        }

        if (cleanup.VertexCount < 3)
        {
            build.Diagnostics.Add(exactMessage ?? "Triangulation failed.");
            build.Diagnostics.Add($"Automatic input cleanup reduced the dataset below three usable vertices: {cleanup.ToDiagnosticSummary()}.");
            return StoreMeshStageCache(
                build,
                runtimeCache,
                stageKey,
                stageName,
                preResolutionFingerprint,
                resolvedInputFingerprint,
                null,
                build.PersistentHardConstraints,
                Array.Empty<GeneratedRhinoObject>(),
                build.Diagnostics.Skip(diagnosticsStart),
                DescribeModifierMeshResult(modifier.Label, null),
                timer,
                out outputFingerprint);
        }

        ThrowIfCancellationRequested(shouldCancel);
        if (!TryBuildValidatedTinMesh(
            cleanup.XyCoords,
            cleanup.ZValues,
            cleanup.Segments,
            boundaryPolylines,
            inputTolerance,
            modifier.CreateBoundaryPeelSettings(),
            runtimeCache.TinEngine,
            runtimeCache.CoreCaseRecorder,
            $"{stageName} Cleanup Retry",
            out var cleanedMesh,
            out var cleanupMessage,
            shouldCancel))
        {
            build.Diagnostics.Add(exactMessage ?? "Triangulation failed.");
            build.Diagnostics.Add($"Automatic input cleanup retry failed: {cleanup.ToDiagnosticSummary()}.");
            if (!string.IsNullOrWhiteSpace(cleanupMessage))
                build.Diagnostics.Add(cleanupMessage);
            return StoreMeshStageCache(
                build,
                runtimeCache,
                stageKey,
                stageName,
                preResolutionFingerprint,
                resolvedInputFingerprint,
                null,
                build.PersistentHardConstraints,
                Array.Empty<GeneratedRhinoObject>(),
                build.Diagnostics.Skip(diagnosticsStart),
                DescribeModifierMeshResult(modifier.Label, null),
                timer,
                out outputFingerprint);
        }

        build.PersistentHardConstraints.Clear();
        build.PersistentHardConstraints.AddRange(persistentHardConstraints);
        build.PersistentElevationConstraints.Clear();
        build.PersistentElevationConstraints.AddRange(persistentElevationConstraints);
        build.Diagnostics.Add($"Automatic input cleanup retry succeeded: {cleanup.ToDiagnosticSummary()}.");
        if (!string.IsNullOrWhiteSpace(cleanupMessage))
            build.Diagnostics.Add(cleanupMessage);
        return StoreMeshStageCache(
            build,
            runtimeCache,
            stageKey,
            stageName,
            preResolutionFingerprint,
            resolvedInputFingerprint,
            cleanedMesh,
            build.PersistentHardConstraints,
            Array.Empty<GeneratedRhinoObject>(),
            build.Diagnostics.Skip(diagnosticsStart),
            DescribeModifierMeshResult(modifier.Label, cleanedMesh),
            timer,
            out outputFingerprint);
    }

    private static RhinoMesh ApplyAddGeometry(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        AddGeometryModifierDefinition modifier,
        TerrainBuildResult build,
        TerrainRuntimeCache runtimeCache,
        Func<bool>? shouldCancel = null)
    {
        ThrowIfCancellationRequested(shouldCancel);
        var points = TerrainBuildSnapshotResolver.ResolvePoints(snapshot, modifier.Points);
        var breaklineCurves = TerrainBuildSnapshotResolver.ResolveCurves(snapshot, modifier.Breaklines);
        var contourCurves = TerrainBuildSnapshotResolver.ResolveCurves(snapshot, modifier.Contours);
        var boundaryCurves = TerrainBuildSnapshotResolver.ResolveCurves(snapshot, modifier.Boundary);

        if (points.Count == 0 && breaklineCurves.Count == 0 && contourCurves.Count == 0 && boundaryCurves.Count == 0)
        {
            build.Diagnostics.Add("Add Geometry has no sources.");
            return mesh.DuplicateMesh();
        }

        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var meshVertices, out _, out var errorMessage))
        {
            build.Diagnostics.Add(errorMessage ?? "Could not extract mesh data for add geometry.");
            return mesh;
        }

        TerrainTolerancePolicy.Profile toleranceProfile = GetToleranceProfile(snapshot, terrain);
        double inputTolerance = modifier.Tolerance > 0
            ? modifier.Tolerance
            : toleranceProfile.InputMergeTolerance;
        double curveTolerance = modifier.Tolerance > 0
            ? modifier.Tolerance
            : toleranceProfile.CurveChordTolerance;

        int existingPointCount = meshVertices.Length / 3;
        var spotXyz = new double[(existingPointCount + points.Count) * 3];
        Array.Copy(meshVertices, spotXyz, meshVertices.Length);
        for (int i = 0; i < points.Count; i++)
        {
            int targetIndex = (existingPointCount + i) * 3;
            spotXyz[targetIndex] = points[i].X;
            spotXyz[targetIndex + 1] = points[i].Y;
            spotXyz[targetIndex + 2] = points[i].Z;
        }

        var newHardConstraints = CreateConstraintPolylines(
            breaklineCurves,
            curveTolerance,
            preserveInputElevation: true);
        var persistentHardConstraints = CombineConstraints(build.PersistentHardConstraints, newHardConstraints);
        var newElevationConstraints = CreateConstraintPolylines(
            contourCurves,
            curveTolerance,
            preserveInputElevation: true);
        var persistentElevationConstraints = CombineConstraints(build.PersistentElevationConstraints, newElevationConstraints);

        ThrowIfCancellationRequested(shouldCancel);
        var polylines = CreateFlatPolylines(CombineConstraints(build.PersistentHardConstraints, build.PersistentElevationConstraints));
        polylines.AddRange(TerrainTriangulationInputBuilder.CreateTriangulationPolylines(
            breaklineCurves,
            contourCurves,
            curveTolerance));

        var boundaryPolylines = CombineBoundaryPolylines(
            CreateBoundaryPolylines(mesh, curveTolerance),
            CreateBoundaryPolylines(boundaryCurves, curveTolerance));

        var breaklineData = BreaklineDiscretizer.Process(polylines, shouldCancel);
        var merged = PointCloudProcessor.Merge(spotXyz, existingPointCount + points.Count, breaklineData, inputTolerance, shouldCancel);
        if (merged.VertexCount < 3)
        {
            build.Diagnostics.Add("Add Geometry needs at least three unique points after deduplication.");
            return mesh;
        }

        if (merged.InvalidsSkipped > 0)
            build.Diagnostics.Add($"{merged.DescribeInvalidPoints()} during add geometry.");
        if (merged.DuplicatesRemoved > 0)
            build.Diagnostics.Add($"{merged.DuplicatesRemoved} duplicate points merged during add geometry.");

        if (TryBuildValidatedTinMesh(
            merged.XyCoords,
            merged.ZValues,
            merged.Segments,
            boundaryPolylines,
            inputTolerance,
            modifier.CreateBoundaryPeelSettings(),
            runtimeCache.TinEngine,
            runtimeCache.CoreCaseRecorder,
            modifier.Label,
            out var exactMesh,
            out var exactMessage,
            shouldCancel))
        {
            build.PersistentHardConstraints.Clear();
            build.PersistentHardConstraints.AddRange(persistentHardConstraints);
            build.PersistentElevationConstraints.Clear();
            build.PersistentElevationConstraints.AddRange(persistentElevationConstraints);
            if (!string.IsNullOrWhiteSpace(exactMessage))
                build.Diagnostics.Add(exactMessage);
            return exactMesh!;
        }

        if (!ShouldAttemptTriangulationCleanupRetry(merged.VertexCount, merged.SegmentCount, shouldCancel, out var cleanupSkipMessage))
        {
            build.Diagnostics.Add(exactMessage ?? "Add Geometry failed.");
            build.Diagnostics.Add(cleanupSkipMessage);
            return mesh;
        }

        var cleanup = TinInputCleaner.Clean(merged, inputTolerance);
        if (!cleanup.HasChanges)
        {
            build.Diagnostics.Add(exactMessage ?? "Add Geometry failed.");
            build.Diagnostics.Add($"Automatic input cleanup made no safe changes: {cleanup.ToDiagnosticSummary()}.");
            return mesh;
        }

        if (cleanup.VertexCount < 3)
        {
            build.Diagnostics.Add(exactMessage ?? "Add Geometry failed.");
            build.Diagnostics.Add($"Automatic input cleanup reduced the dataset below three usable vertices: {cleanup.ToDiagnosticSummary()}.");
            return mesh;
        }

        if (!TryBuildValidatedTinMesh(
            cleanup.XyCoords,
            cleanup.ZValues,
            cleanup.Segments,
            boundaryPolylines,
            inputTolerance,
            modifier.CreateBoundaryPeelSettings(),
            runtimeCache.TinEngine,
            runtimeCache.CoreCaseRecorder,
            $"{modifier.Label} Cleanup Retry",
            out var cleanedMesh,
            out var cleanupMessage,
            shouldCancel))
        {
            build.Diagnostics.Add(exactMessage ?? "Add Geometry failed.");
            build.Diagnostics.Add($"Automatic input cleanup retry failed: {cleanup.ToDiagnosticSummary()}.");
            if (!string.IsNullOrWhiteSpace(cleanupMessage))
                build.Diagnostics.Add(cleanupMessage);
            return mesh;
        }

        build.PersistentHardConstraints.Clear();
        build.PersistentHardConstraints.AddRange(persistentHardConstraints);
        build.PersistentElevationConstraints.Clear();
        build.PersistentElevationConstraints.AddRange(persistentElevationConstraints);
        build.Diagnostics.Add($"Automatic input cleanup retry succeeded: {cleanup.ToDiagnosticSummary()}.");
        if (!string.IsNullOrWhiteSpace(cleanupMessage))
            build.Diagnostics.Add(cleanupMessage);
        return cleanedMesh!;
    }

    private static bool TryBuildValidatedTinMesh(
        double[] xyCoords,
        double[] zValues,
        int[] segments,
        TinBoundaryPreparer.BoundaryPolyline[] boundaryPolylines,
        double tolerance,
        BoundaryTrianglePeelSettings boundaryPeelSettings,
        TinEngine engine,
        TerrainCoreCaseRecorder? coreCaseRecorder,
        string stageName,
        out RhinoMesh? mesh,
        out string? message,
        Func<bool>? shouldCancel = null)
    {
        mesh = null;
        ThrowIfCancellationRequested(shouldCancel);

        var prepared = TinBoundaryPreparer.Prepare(
            xyCoords,
            zValues,
            segments,
            boundaryPolylines,
            tolerance);
        ThrowIfCancellationRequested(shouldCancel);

        var result = engine.Build(
            prepared.XyCoords,
            prepared.ZValues,
            prepared.Segments,
            QualitySettings.None,
            out message,
            useConvexHull: prepared.UseConvexHull,
            boundaryPeelSettings: boundaryPeelSettings,
            shouldCancel: shouldCancel);
        ThrowIfCancellationRequested(shouldCancel);

        if (!string.IsNullOrWhiteSpace(prepared.WarningMessage))
            message = AppendBuildMessage(message, prepared.WarningMessage);
        if (!string.IsNullOrWhiteSpace(prepared.InfoMessage))
            message = AppendBuildMessage(message, prepared.InfoMessage);

        coreCaseRecorder?.RecordTin(
            stageName,
            prepared.XyCoords,
            prepared.ZValues,
            prepared.Segments,
            prepared.UseConvexHull,
            boundaryPeelSettings,
            result != null,
            result?.VertexCount,
            result?.FaceCount,
            message);

        if (result == null)
            return false;

        try
        {
            mesh = RhinoGeometryConversions.ToRhinoMesh(result);
        }
        catch (Exception ex)
        {
            message = string.IsNullOrWhiteSpace(message)
                ? $"Triangulation produced an invalid mesh: {ex.Message}"
                : $"{message} Triangulation produced an invalid mesh: {ex.Message}";
            mesh = null;
            return false;
        }

        if (mesh.Faces.Count == 0 || mesh.Vertices.Count == 0 || !mesh.IsValid)
        {
            message = string.IsNullOrWhiteSpace(message)
                ? "Triangulation produced an invalid mesh."
                : $"{message} Triangulation produced an invalid mesh.";
            mesh = null;
            return false;
        }

        return true;
    }

    private static bool ShouldAttemptTriangulationCleanupRetry(
        int vertexCount,
        int segmentCount,
        Func<bool>? shouldCancel,
        out string message)
    {
        ThrowIfCancellationRequested(shouldCancel);

        if (vertexCount > 120_000 || segmentCount > 180_000)
        {
            message = $"Automatic triangulation cleanup retry skipped for large input ({vertexCount:N0} verts, {segmentCount:N0} segments) to keep rebuilds responsive.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    /// <summary>Faces steeper than this are frozen retaining walls in the Remesh modifier (never touched).</summary>
    private const double RemeshWallFaceMinSlopeDeg = 70.0;

    /// <summary>
    /// Isotropic remesh (split / collapse / flip / relax / back-project): regularizes the whole terrain
    /// toward even triangles of the target edge length while every vertex stays exactly on the input
    /// surface. Features (boundary ∪ creases at Crease Angle ∪ the whole constraint stack incl.
    /// grade-path road edges) are pinned polylines; steep retaining-wall faces pass through verbatim.
    /// </summary>
    private static RhinoMesh ApplyRemesh(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        RemeshModifierDefinition modifier,
        TerrainBuildResult build,
        TerrainBuildMode mode)
    {
        TerrainTolerancePolicy.Profile toleranceProfile = GetToleranceProfile(snapshot, terrain);
        double tolerance = toleranceProfile.CurveChordTolerance;
        double edgeLength = modifier.EdgeLength;
        if (mode == TerrainBuildMode.Preview && edgeLength > 0)
            edgeLength *= 2.0;

        var localConstraints = CreateConstraintPolylines(
            TerrainBuildSnapshotResolver.ResolveCurves(snapshot, modifier.Constraints),
            tolerance,
            preserveInputElevation: false,
            requestedEdgeLength: edgeLength,
            maxArea: 0);

        var constraints = CombineConstraints(build.PersistentHardConstraints, localConstraints);

        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out var faces, out var errorMessage))
        {
            build.Diagnostics.Add(errorMessage ?? "Could not extract mesh data for remesh.");
            return mesh.DuplicateMesh();
        }

        // EdgeLength 0 = regularize at the mesh's own density (median input edge length).
        double target = edgeLength > 0 ? edgeLength : MedianEdgeLength(vertices, faces);
        if (mode == TerrainBuildMode.Preview && modifier.EdgeLength <= 0)
            target *= 2.0;
        if (target <= 0)
        {
            build.Diagnostics.Add("Remesh skipped: could not derive a target edge length.");
            return mesh.DuplicateMesh();
        }

        IsotropicRemesher.Result result = IsotropicRemesher.Remesh(
            vertices,
            faces,
            constraints,
            new IsotropicRemesher.Options
            {
                TargetEdgeLength = target,
                CreaseAngleDeg = modifier.CreaseAngle,
                Tolerance = toleranceProfile.RemeshConstraintTolerance,
                WallFaceMinSlopeDeg = RemeshWallFaceMinSlopeDeg,
                Iterations = mode == TerrainBuildMode.Preview ? 3 : 5
            });

        if (!result.Success)
        {
            build.Diagnostics.Add(result.Warning ?? "Remesh kept the upstream mesh unchanged.");
            return mesh.DuplicateMesh();
        }

        build.Diagnostics.Add(
            $"Remesh isotropic: {result.Splits:N0} splits, {result.Collapses:N0} collapses, " +
            $"{result.Flips:N0} flips at target {target:0.###} " +
            $"({result.Faces.Length / 3:N0} faces; features and walls pinned).");

        return BuildMeshFromArrays(result.Vertices, result.Faces);
    }

    /// <summary>Median 3-D edge length over the mesh's unique edges (the "keep this density" target).</summary>
    private static double MedianEdgeLength(double[] vertices, int[] faces)
    {
        var seen = new HashSet<long>();
        var lengths = new List<double>(faces.Length);
        for (int t = 0; t < faces.Length / 3; t++)
        {
            for (int corner = 0; corner < 3; corner++)
            {
                int a = faces[t * 3 + corner];
                int b = faces[t * 3 + ((corner + 1) % 3)];
                if (!seen.Add(IndexedMeshTools.GetEdgeKey(a, b)))
                    continue;
                double dx = vertices[a * 3] - vertices[b * 3];
                double dy = vertices[a * 3 + 1] - vertices[b * 3 + 1];
                double dz = vertices[a * 3 + 2] - vertices[b * 3 + 2];
                lengths.Add(Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz)));
            }
        }

        if (lengths.Count == 0)
            return 0.0;
        lengths.Sort();
        return lengths[lengths.Count / 2];
    }

    /// <summary>
    /// Field-guided quad retopology, Stage 2: replace the terrain with the extracted quad-dominant mesh whose
    /// edges flow along the features. Runs <see cref="QuadRemesher"/> on the input mesh + the whole
    /// constraint stack (incl. grade-path road edges via <c>PersistentHardConstraints</c>), then builds a
    /// quad-preserving Rhino mesh. Falls back to the input mesh if extraction yields nothing.
    /// </summary>
    private static RhinoMesh ApplyRetopoQuads(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        RetopoModifierDefinition modifier,
        TerrainBuildResult build,
        TerrainBuildMode mode)
    {
        TerrainTolerancePolicy.Profile toleranceProfile = GetToleranceProfile(snapshot, terrain);

        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out var faces, out var errorMessage))
        {
            build.Diagnostics.Add(errorMessage ?? "Could not extract mesh data for retopo.");
            return mesh.DuplicateMesh();
        }

        double edgeLength = modifier.TargetEdgeLength;
        if (mode == TerrainBuildMode.Preview && edgeLength > 0)
            edgeLength *= 2.0; // coarser quads for the fast preview

        var localConstraints = CreateConstraintPolylines(
            TerrainBuildSnapshotResolver.ResolveCurves(snapshot, modifier.Constraints),
            toleranceProfile.CurveChordTolerance,
            preserveInputElevation: false,
            requestedEdgeLength: 0,
            maxArea: 0);
        var constraints = CombineConstraints(build.PersistentHardConstraints, localConstraints);

        double effectiveEdge = edgeLength > 0 ? edgeLength : EstimateQuadSpacing(vertices);

        QuadRemesher.Result result = QuadRemesher.Remesh(
            vertices,
            faces,
            constraints,
            new QuadRemesher.Options
            {
                EdgeLength = effectiveEdge,
                CreaseAngleDeg = modifier.CreaseAngle,
                Tolerance = toleranceProfile.RemeshConstraintTolerance,
                // Exclude near-vertical retaining-wall faces from the heightfield field; they are rebuilt
                // as dedicated quad strips below.
                WallFaceMinSlopeDeg = RetopoWallFaceMinSlopeDeg,
                EnableCleanup = true
            });

        List<WallQuadStripBuilder.Strip> wallStrips = GatherWallQuadStrips(snapshot, terrain, effectiveEdge, build);

        int terrainQuadCount = result.Quads.Length / 4;
        int cleanupTriCount = result.Tris.Length / 3;
        int wallQuadCount = 0;
        foreach (WallQuadStripBuilder.Strip strip in wallStrips)
            wallQuadCount += strip.QuadCount;

        if (terrainQuadCount + wallQuadCount == 0)
        {
            build.Diagnostics.Add(result.Warning ?? "Retopo produced no quads; kept the input mesh.");
            return mesh.DuplicateMesh();
        }

        (double[] mergedVertices, int[] mergedQuads, int[] mergedTris, QuadRetopoCleanup.WeldResult weld) =
            MergeQuadSets(result.Vertices, result.Quads, result.Tris, wallStrips, toleranceProfile.RemeshConstraintTolerance);

        if (!ValidateQuadDominantTopology(mergedQuads, mergedTris, out string? topologyWarning))
        {
            build.Diagnostics.Add(topologyWarning ?? "Retopo cleanup produced invalid topology; kept the input mesh.");
            return mesh.DuplicateMesh();
        }

        build.Diagnostics.Add(
            $"Retopo quads: {terrainQuadCount:N0} terrain + {wallQuadCount:N0} wall = {terrainQuadCount + wallQuadCount:N0} quads" +
            (cleanupTriCount > 0 ? $" + {cleanupTriCount:N0} cleanup tris" : "") +
            $" over {mergedVertices.Length / 3:N0} vertices" +
            (result.ClosedGapLoopCount > 0 ? $" ({result.ClosedGapLoopCount:N0} gap loop(s) closed)" : "") +
            (result.SkippedLargeGapLoopCount > 0 ? $" ({result.SkippedLargeGapLoopCount:N0} large gap loop(s) left open)" : "") +
            (weld.RemovedVertexCount > 0 ? $", welded {weld.RemovedVertexCount:N0} duplicate vertices" : "") +
            (weld.DroppedFaceCount > 0 ? $", dropped {weld.DroppedFaceCount:N0} degenerate faces" : "") +
            "." +
            (string.IsNullOrWhiteSpace(result.Warning) ? "" : $" {result.Warning}"));

        return RhinoGeometryConversions.BuildQuadDominantMesh(mergedVertices, mergedQuads, mergedTris);
    }

    // Near-vertical faces (retaining walls) are excluded from the heightfield quad field at this slope.
    private const double RetopoWallFaceMinSlopeDeg = 70.0;

    /// <summary>
    /// Rebuilds each enabled retaining wall as a quad strip between its top &amp; toe rails. Re-derives the rails
    /// with the same planner the retaining-wall stage uses; runs inside the retopo stage cache, so it only
    /// recomputes when the upstream mesh/params change.
    /// </summary>
    private static List<WallQuadStripBuilder.Strip> GatherWallQuadStrips(
        TerrainBuildSnapshot snapshot, TerrainDefinition terrain, double edgeLength, TerrainBuildResult build)
    {
        var strips = new List<WallQuadStripBuilder.Strip>();
        double rowSpacing = edgeLength > 0 ? edgeLength : 1.0;

        foreach (ModifierDefinition definition in terrain.Modifiers)
        {
            if (definition is not RetainingWallModifierDefinition wallModifier || !wallModifier.IsEnabled)
                continue;

            TerrainTolerancePolicy.Profile toleranceProfile = GetToleranceProfile(snapshot, terrain);
            double wallTolerance = toleranceProfile.RetainingWallTolerance(wallModifier.MaxWallWidth);
            double maxWallWidth = Math.Max(wallTolerance, wallModifier.MaxWallWidth);

            var wallCurves = TerrainBuildSnapshotResolver.ResolveCurves(snapshot, wallModifier.WallCurves);
            if (wallCurves.Count == 0)
                continue;

            var plan = RetainingWallPlannerCore.Plan(wallCurves, maxWallWidth, curveParsingTolerance: wallTolerance);
            foreach (var wall in plan.Walls)
            {
                if (!IsWallStripUsable(wall.Rails, wallTolerance, out _))
                    continue;

                WallQuadStripBuilder.Strip strip = WallQuadStripBuilder.Build(
                    ToFlatXyz(wall.Rails.TopPoints),
                    ToFlatXyz(wall.Rails.ToePoints),
                    wall.Rails.IsClosed,
                    rowSpacing);
                if (strip.QuadCount > 0)
                    strips.Add(strip);
            }
        }

        return strips;
    }

    private static (double[] vertices, int[] quads, int[] tris, QuadRetopoCleanup.WeldResult weld) MergeQuadSets(
        double[] terrainVertices,
        int[] terrainQuads,
        int[] terrainTris,
        List<WallQuadStripBuilder.Strip> wallStrips,
        double tolerance)
    {
        var vertices = new List<double>(terrainVertices);
        var quads = new List<int>(terrainQuads);
        var tris = new List<int>(terrainTris);
        int offset = terrainVertices.Length / 3;

        foreach (WallQuadStripBuilder.Strip strip in wallStrips)
        {
            vertices.AddRange(strip.Vertices);
            for (int i = 0; i < strip.Quads.Length; i++)
                quads.Add(strip.Quads[i] + offset);
            offset += strip.VertexCount;
        }

        QuadRetopoCleanup.WeldResult weld = QuadRetopoCleanup.WeldByTolerance(
            vertices.ToArray(),
            quads.ToArray(),
            tris.ToArray(),
            tolerance);

        return (weld.Vertices, weld.Quads, weld.Tris, weld);
    }

    private static bool ValidateQuadDominantTopology(int[] quads, int[] tris, out string? warning)
    {
        int[] triangleFaces = BuildTriangleFacesForValidation(quads, tris);
        if (triangleFaces.Length == 0)
        {
            warning = "Retopo produced no valid faces; kept the input mesh.";
            return false;
        }

        MeshTopologyValidator.BoundaryGraphAnalysis topology =
            MeshTopologyValidator.AnalyzeBoundaryGraph(triangleFaces, triangleFaces.Length / 3);
        if (topology.NonManifoldEdgeCount != 0)
        {
            warning = $"Retopo cleanup produced {topology.NonManifoldEdgeCount:N0} non-manifold edge(s); kept the input mesh.";
            return false;
        }

        if (topology.HasOpenBoundaryChains)
        {
            warning = "Retopo cleanup produced open naked-edge chains; kept the input mesh.";
            return false;
        }

        warning = null;
        return true;
    }

    private static int[] BuildTriangleFacesForValidation(int[] quads, int[] tris)
    {
        var faces = new int[(quads.Length / 4 * 6) + tris.Length];
        int t = 0;
        for (int i = 0; i < quads.Length / 4; i++)
        {
            int a = quads[i * 4];
            int b = quads[i * 4 + 1];
            int c = quads[i * 4 + 2];
            int d = quads[i * 4 + 3];
            faces[t++] = a;
            faces[t++] = b;
            faces[t++] = c;
            faces[t++] = a;
            faces[t++] = c;
            faces[t++] = d;
        }

        Array.Copy(tris, 0, faces, t, tris.Length);
        return faces;
    }

    private static double[] ToFlatXyz(Point3d[] points)
    {
        var flat = new double[points.Length * 3];
        for (int i = 0; i < points.Length; i++)
        {
            flat[i * 3] = points[i].X;
            flat[i * 3 + 1] = points[i].Y;
            flat[i * 3 + 2] = points[i].Z;
        }

        return flat;
    }

    private static double EstimateQuadSpacing(double[] vertices)
    {
        int vertexCount = vertices.Length / 3;
        if (vertexCount == 0)
            return 1.0;

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (int i = 0; i < vertexCount; i++)
        {
            double x = vertices[i * 3], y = vertices[i * 3 + 1];
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        double diagonal = Math.Sqrt(((maxX - minX) * (maxX - minX)) + ((maxY - minY) * (maxY - minY)));
        double spacing = diagonal / Math.Max(1.0, Math.Sqrt(vertexCount));
        return spacing > 1e-9 ? spacing : 1.0;
    }

    /// <summary>
    /// Field-guided quad retopology, Stage 1 (cross-field preview). Computes a 2-D cross-field aligned to
    /// features via <see cref="CrossFieldSolver"/> and emits it as a decimated <b>flow-cross overlay</b> —
    /// short perpendicular segment pairs along the two quad directions, colored by θ — so the flow can be
    /// read directly (hue alone was unreadable) and validated before quad extraction (Stages 2–3) is built.
    /// Preview only; the mesh is unchanged. Features come from the whole stack: the boundary, detected
    /// creases, the modifier's own constraint curves, AND <c>PersistentHardConstraints</c> — grade-path road
    /// edges are generated from a centerline, so they only reach us through the stack, not as drawn curves.
    /// </summary>
    private static void BuildRetopoFieldOverlay(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        RetopoModifierDefinition modifier,
        TerrainBuildResult build)
    {
        TerrainTolerancePolicy.Profile toleranceProfile = GetToleranceProfile(snapshot, terrain);

        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out var faces, out var errorMessage))
        {
            build.Diagnostics.Add(errorMessage ?? "Could not extract mesh data for retopo field preview.");
            return;
        }

        var localConstraints = CreateConstraintPolylines(
            TerrainBuildSnapshotResolver.ResolveCurves(snapshot, modifier.Constraints),
            toleranceProfile.CurveChordTolerance,
            preserveInputElevation: false,
            requestedEdgeLength: 0,
            maxArea: 0);
        var constraints = CombineConstraints(build.PersistentHardConstraints, localConstraints);

        CrossFieldSolver.Result field = CrossFieldSolver.Solve(
            vertices,
            faces,
            constraints,
            new CrossFieldSolver.Options
            {
                CreaseAngleDeg = modifier.CreaseAngle,
                Tolerance = toleranceProfile.RemeshConstraintTolerance
            });

        if (!field.Success)
        {
            build.Diagnostics.Add(field.Warning ?? "Retopo cross-field could not be computed.");
            return;
        }

        int vertexCount = vertices.Length / 3;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (int i = 0; i < vertexCount; i++)
        {
            double x = vertices[i * 3], y = vertices[i * 3 + 1];
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        double diagonal = Math.Sqrt(((maxX - minX) * (maxX - minX)) + ((maxY - minY) * (maxY - minY)));
        double spacing = diagonal / Math.Max(1.0, Math.Sqrt(vertexCount));
        double crossHalf = 0.5 * (modifier.TargetEdgeLength > 0 ? modifier.TargetEdgeLength : spacing);
        if (crossHalf <= 1e-9)
            crossHalf = Math.Max(spacing, 1e-6) * 0.5;

        // Decimate onto a grid ~3 crosses apart so the comb reads instead of matting into a solid patch.
        double cellSize = crossHalf * 3.0;
        double invCell = 1.0 / cellSize;
        var used = new HashSet<long>();

        int pinnedCount = 0;
        for (int i = 0; i < vertexCount; i++)
        {
            if (field.Pinned[i])
                pinnedCount++;

            double x = vertices[i * 3], y = vertices[i * 3 + 1], z = vertices[i * 3 + 2];
            long cx = (long)Math.Floor(x * invCell);
            long cy = (long)Math.Floor(y * invCell);
            if (!used.Add((cx * 73856093L) ^ (cy * 19349663L)))
                continue;

            double theta = field.Theta[i];
            var point = new Point3d(x, y, z);
            var along = new Vector3d(Math.Cos(theta), Math.Sin(theta), 0.0) * crossHalf;
            var across = new Vector3d(-Math.Sin(theta), Math.Cos(theta), 0.0) * crossHalf;
            int argb = FieldColor(theta, field.Pinned[i]).ToArgb();
            build.FieldOverlayLines.Add(new FieldOverlayLine(new Line(point - along, point + along), argb));
            build.FieldOverlayLines.Add(new FieldOverlayLine(new Line(point - across, point + across), argb));
        }

        build.Diagnostics.Add(
            $"Retopo Stage 1 field preview: {vertexCount:N0} vertices, {pinnedCount:N0} feature-pinned, " +
            $"{build.FieldOverlayLines.Count:N0} overlay segments." +
            (string.IsNullOrWhiteSpace(field.Warning) ? "" : $" {field.Warning}"));
    }

    /// <summary>Maps a cross-field angle θ∈[0,π/2) to a hue so the quad-flow direction reads as color; pins pop brighter.</summary>
    private static System.Drawing.Color FieldColor(double theta, bool pinned)
    {
        double hue = Math.Clamp(theta / (Math.PI / 2.0), 0.0, 1.0) * 360.0;
        return HsvToColor(hue, pinned ? 1.0 : 0.7, pinned ? 1.0 : 0.9);
    }

    private static System.Drawing.Color HsvToColor(double hueDegrees, double saturation, double value)
    {
        double h = (hueDegrees % 360.0) / 60.0;
        int sector = (int)Math.Floor(h);
        double f = h - sector;
        double p = value * (1.0 - saturation);
        double q = value * (1.0 - (saturation * f));
        double t = value * (1.0 - (saturation * (1.0 - f)));
        double r, g, b;
        switch (sector)
        {
            case 0: r = value; g = t; b = p; break;
            case 1: r = q; g = value; b = p; break;
            case 2: r = p; g = value; b = t; break;
            case 3: r = p; g = q; b = value; break;
            case 4: r = t; g = p; b = value; break;
            default: r = value; g = p; b = q; break;
        }

        return System.Drawing.Color.FromArgb(
            (int)Math.Round(r * 255.0),
            (int)Math.Round(g * 255.0),
            (int)Math.Round(b * 255.0));
    }

    private static RhinoMesh ApplySmooth(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        SmoothModifierDefinition modifier,
        TerrainBuildResult build,
        TerrainRuntimeCache runtimeCache,
        string stageKey,
        TerrainBuildMode mode)
    {
        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out var faces, out var errorMessage))
        {
            build.Diagnostics.Add(errorMessage ?? "Could not extract mesh data for smoothing.");
            return mesh;
        }

        double tolerance = GetToleranceProfile(snapshot, terrain).CurveChordTolerance;
        double effectiveStrength = Math.Clamp(modifier.Strength, 0.0, 1.0);
        var boundaries = new List<(double[] xyVerts, int vertCount)>();
        foreach (var curve in TerrainBuildSnapshotResolver.ResolveCurves(snapshot, modifier.Boundaries))
        {
            if (!RhinoSourceResolver.TryGetPolyline(curve, tolerance, requireClosed: true, out var polyline))
                continue;

            int count = polyline.Count;
            if (polyline[0].DistanceTo(polyline[^1]) < tolerance)
                count--;

            var xyVerts = new double[count * 2];
            for (int i = 0; i < count; i++)
            {
                xyVerts[i * 2] = polyline[i].X;
                xyVerts[i * 2 + 1] = polyline[i].Y;
            }

            boundaries.Add((xyVerts, count));
        }

        var breaklines = new List<MeshSmoother.BreaklinePolyline>();
        foreach (var curve in TerrainBuildSnapshotResolver.ResolveCurves(snapshot, modifier.Breaklines))
        {
            if (!RhinoSourceResolver.TryGetPolyline(curve, tolerance, requireClosed: false, out var polyline))
                continue;

            int count = polyline.Count;
            bool isClosed = curve.IsClosed || (count > 2 && polyline[0].DistanceTo(polyline[^1]) <= tolerance);
            if (isClosed && polyline[0].DistanceTo(polyline[^1]) < tolerance)
                count--;

            var xyPts = new double[count * 2];
            for (int i = 0; i < count; i++)
            {
                xyPts[i * 2] = polyline[i].X;
                xyPts[i * 2 + 1] = polyline[i].Y;
            }

            breaklines.Add(new MeshSmoother.BreaklinePolyline(xyPts, count, isClosed));
        }

        string preparedStageKey = TerrainStageKey.CreateSmoothPrepared(stageKey);
        ulong preparedFingerprint = ComputeSmoothPreparedFingerprint(
            snapshot,
            terrain,
            modifier,
            ComputeMeshFingerprint(mesh));
        MeshSmoother.PreparedSmoothingData prepared;
        if (runtimeCache.SmoothEntries.TryGetValue(preparedStageKey, out var cachedPrepared) &&
            cachedPrepared.Fingerprint == preparedFingerprint)
        {
            prepared = TerrainRuntimeCacheCloner.CloneSmoothStageCacheEntry(cachedPrepared).Prepared;
        }
        else
        {
            prepared = MeshSmoother.Prepare(
                vertices,
                mesh.Vertices.Count,
                faces,
                mesh.Faces.Count,
                boundaries.ToArray(),
                breaklines.ToArray(),
                tolerance);
            runtimeCache.SmoothEntries[preparedStageKey] = new SmoothStageCacheEntry
            {
                Fingerprint = preparedFingerprint,
                Prepared = prepared
            };
        }

        var smoothed = MeshSmoother.SmoothPrepared(
            vertices,
            prepared,
            effectiveStrength,
            Math.Clamp(modifier.BreaklineFixity, 0.0, 1.0),
            mode == TerrainBuildMode.Preview
                ? Math.Min(2, Math.Max(1, modifier.Iterations))
                : Math.Max(1, modifier.Iterations));

        for (int i = 0; i < mesh.Vertices.Count; i++)
        {
            smoothed[i * 3] = vertices[i * 3];
            smoothed[i * 3 + 1] = vertices[i * 3 + 1];
        }

        if (smoothed.Any(value => double.IsNaN(value) || double.IsInfinity(value)))
        {
            build.Diagnostics.Add("Smooth produced invalid mesh data. Original mesh kept.");
            return mesh;
        }

        var smoothedMesh = RhinoGeometryConversions.BuildMesh(smoothed, mesh.Vertices.Count, faces, mesh.Faces.Count);
        if (smoothedMesh.Faces.Count == 0 || smoothedMesh.Vertices.Count == 0 || !smoothedMesh.IsValid)
        {
            build.Diagnostics.Add("Smooth produced an invalid mesh. Original mesh kept.");
            return mesh;
        }

        return smoothedMesh;
    }

}
