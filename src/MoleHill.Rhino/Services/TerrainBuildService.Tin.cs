using System.Diagnostics;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using MoleHill.Core.Processing;
using MoleHill.Core.Retopo;
using MoleHill.Rhino.Model;
using Rhino.Geometry;
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
        Func<bool>? shouldCancel = null,
        Action<TerrainBuildProgress>? reportProgress = null)
    {
        const string stageName = "Triangulate";
        var timer = Stopwatch.StartNew();
        var progress = new TerrainBuildProgressReporter(reportProgress);
        progress.Start("Source resolution");
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
        var exactTinMeshes = TerrainBuildSnapshotResolver.ResolveObjects(snapshot, modifier.TinMesh)
            .Select(source => source.Geometry)
            .OfType<RhinoMesh>()
            .ToList();
        if (modifier.TinMesh.HasReferences)
        {
            if (exactTinMeshes.Count != 1)
            {
                build.Diagnostics.Add(exactTinMeshes.Count == 0
                    ? "Triangulate exact TIN source did not resolve to a mesh."
                    : "Triangulate exact TIN source must resolve to exactly one mesh.");
                return StoreMeshStageCache(
                    build, runtimeCache, stageKey, stageName, preResolutionFingerprint,
                    ComputeSourceSetFingerprint(snapshot, modifier.TinMesh), null,
                    build.PersistentHardConstraints, Array.Empty<GeneratedRhinoObject>(),
                    build.Diagnostics.Skip(diagnosticsStart), DescribeModifierMeshResult(modifier.Label, null),
                    timer, out outputFingerprint, shouldCancel,
                    build.StructuredDiagnostics.Skip(structuredDiagnosticsStart), progress);
            }

            RhinoMesh exactTin = exactTinMeshes[0].DuplicateMesh(); // plain: it is mutated below, so nothing cached may ride along
            if (exactTin.Faces.QuadCount > 0)
                exactTin.Faces.ConvertQuadsToTriangles();
            if (!RhinoGeometryConversions.TryExtractMeshData(exactTin, out _, out _, out string? exactTinError))
            {
                exactTin.Dispose();
                build.Diagnostics.Add(exactTinError ?? "The exact TIN mesh is invalid.");
                return StoreMeshStageCache(
                    build, runtimeCache, stageKey, stageName, preResolutionFingerprint,
                    ComputeSourceSetFingerprint(snapshot, modifier.TinMesh), null,
                    build.PersistentHardConstraints, Array.Empty<GeneratedRhinoObject>(),
                    build.Diagnostics.Skip(diagnosticsStart), DescribeModifierMeshResult(modifier.Label, null),
                    timer, out outputFingerprint, shouldCancel,
                    build.StructuredDiagnostics.Skip(structuredDiagnosticsStart), progress);
            }

            if (modifier.Points.HasReferences || modifier.DemSurface.HasReferences || modifier.Breaklines.HasReferences ||
                modifier.Contours.HasReferences)
            {
                build.Diagnostics.Add("Triangulate is using the exact TIN mesh; DEM surface, point, breakline, and contour sources are ignored.");
            }
            if (modifier.DataClipBoundaries.HasReferences)
                build.Diagnostics.Add("Data Clip does not alter an Exact TIN mesh; Outer, Hide, and Show still apply to the finished terrain.");

            progress.Complete("Source resolution", $"exact TIN mesh: {exactTin.Vertices.Count:N0} vertices, {exactTin.Faces.Count:N0} faces");
            return StoreMeshStageCache(
                build, runtimeCache, stageKey, stageName, preResolutionFingerprint,
                ComputeSourceSetFingerprint(snapshot, modifier.TinMesh), exactTin,
                build.PersistentHardConstraints, Array.Empty<GeneratedRhinoObject>(),
                build.Diagnostics.Skip(diagnosticsStart), DescribeModifierMeshResult(modifier.Label, exactTin),
                timer, out outputFingerprint, shouldCancel,
                build.StructuredDiagnostics.Skip(structuredDiagnosticsStart), progress);
        }

        var points = TerrainBuildSnapshotResolver.ResolvePoints(snapshot, modifier.Points);
        if (snapshot.DemPoints.TryGetValue(modifier.Id, out List<Point3d>? demPoints))
            points.AddRange(demPoints);
        if (snapshot.DemDiagnostics.TryGetValue(modifier.Id, out string? demDiagnostic))
            build.Diagnostics.Add(demDiagnostic);
        var breaklineCurves = TerrainBuildSnapshotResolver.ResolveCurves(snapshot, modifier.Breaklines);
        var contourCurves = TerrainBuildSnapshotResolver.ResolveCurves(snapshot, modifier.Contours);
        string contourSourceDetail = snapshot.SourceDiagnostics.TryGetValue(modifier.Contours, out SourceResolutionDiagnostics? contourDiagnostics)
            ? $"; model-space layer validation v8; contour objects: {contourDiagnostics.Describe()}"
            : "; model-space layer validation v8";
        progress.Complete(
            "Source resolution",
            $"{points.Count:N0} points, {breaklineCurves.Count:N0} breaklines, {contourCurves.Count:N0} contours{contourSourceDetail}");

        if (points.Count == 0 && breaklineCurves.Count == 0 && contourCurves.Count == 0)
        {
            build.Diagnostics.Add("Triangulate has no DEM surface, point, contour, or breakline sources.");
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

        (points, breaklineCurves, contourCurves) = FilterInputsToDataClip(
            snapshot, terrain, points, breaklineCurves, contourCurves, curveTolerance, build, shouldCancel);

        progress.Start("Input packing");
        List<TerrainTriangulationInputBuilder.FlattenedPolyline> flattenedBreaklines =
            TerrainTriangulationInputBuilder.CreateFlattenedPolylines(breaklineCurves, curveTolerance);
        List<TerrainTriangulationInputBuilder.FlattenedPolyline> flattenedContours =
            TerrainTriangulationInputBuilder.CreateFlattenedPolylines(contourCurves, curveTolerance);
        int breaklineSourceVertexCount = flattenedBreaklines.Sum(static polyline => polyline.Points.Length / 3);
        int contourSourceVertexCount = flattenedContours.Sum(static polyline => polyline.Points.Length / 3);
        bool constrainContours = modifier.ShouldConstrainContours(contourSourceVertexCount);
        int sampledContourVertexCount = constrainContours ? 0 : contourSourceVertexCount;

        var spotXyz = new double[(points.Count + sampledContourVertexCount) * 3];
        for (int i = 0; i < points.Count; i++)
        {
            spotXyz[i * 3] = points[i].X;
            spotXyz[i * 3 + 1] = points[i].Y;
            spotXyz[i * 3 + 2] = points[i].Z;
        }

        if (!constrainContours && contourSourceVertexCount > 0)
        {
            int targetOffset = points.Count * 3;
            foreach (TerrainTriangulationInputBuilder.FlattenedPolyline contour in flattenedContours)
            {
                Array.Copy(contour.Points, 0, spotXyz, targetOffset, contour.Points.Length);
                targetOffset += contour.Points.Length;
            }

            string reason = string.Equals(modifier.ContourMode, TriangulateModifierDefinition.VerticesOnlyContourMode, StringComparison.OrdinalIgnoreCase)
                ? "Contour Mode is Vertices only"
                : $"Auto switches at {TriangulateModifierDefinition.AutoUnconstrainedContourVertexThreshold:N0} vertices";
            build.Diagnostics.Add(
                $"Triangulate treated {contourSourceVertexCount:N0} contour vertices as unconstrained TIN samples ({reason}). " +
                "Breaklines and the terrain boundary remain constrained.");
        }

        ThrowIfCancellationRequested(shouldCancel);
        TerrainConstraintPreprocessor.Result processed = TerrainConstraintPreprocessor.ProcessSeparately(
            flattenedBreaklines.Select(static polyline => polyline.Points).ToList(),
            constrainContours
                ? flattenedContours.Select(static polyline => polyline.Points).ToList()
                : Array.Empty<double[]>(),
            curveTolerance,
            spotXyz);
        List<double[]> polylines = processed.Breaklines.Concat(processed.Contours)
            .Where(static polyline => polyline != null)
            .Select(static polyline => polyline!)
            .ToList();

        // Persist the lines as TRIANGULATED, not as drawn. The preprocessor stations long straight runs;
        // a later constrained rebuild (Retaining Wall, Grade) seeds every mesh vertex, so inserting the raw
        // line passes each long segment within rounding of those stations and leaves a zero-area cap at
        // each one — 784 -> ~11,000 thin faces on the Glyvra terrain.
        var persistentHardConstraints = CreateConstraintPolylines(
            flattenedBreaklines,
            processed.Breaklines,
            preserveInputElevation: true);
        var persistentElevationConstraints = constrainContours
            ? CreateConstraintPolylines(flattenedContours, processed.Contours, preserveInputElevation: true)
            : new List<SurfaceRemesher.ConstraintPolyline>();
        var boundaryPolylines = Array.Empty<TinBoundaryPreparer.BoundaryPolyline>();
        int constraintVertexCount = polylines.Sum(static polyline => polyline.Length / 3);
        int boundaryVertexCount = boundaryPolylines.Sum(static polyline => polyline.PointCount);
        progress.Complete(
            "Input packing",
            $"{points.Count + sampledContourVertexCount:N0} sample points, {constraintVertexCount:N0} constraint vertices, {boundaryVertexCount:N0} boundary vertices; " +
            $"curve tolerance {curveTolerance:G6}; {breaklineSourceVertexCount:N0} breakline vertices, {contourSourceVertexCount:N0} contour vertices; " +
            $"contours {(constrainContours ? "constrained" : "vertices only")}");

        progress.Start("Point deduplication");
        var breaklineData = BreaklineDiscretizer.Process(polylines, shouldCancel);
        var merged = PointCloudProcessor.Merge(spotXyz, points.Count + sampledContourVertexCount, breaklineData, inputTolerance, shouldCancel);
        progress.Complete("Point deduplication", $"{merged.VertexCount:N0} unique vertices, {merged.SegmentCount:N0} segments");
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
            shouldCancel,
            progress))
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
                out outputFingerprint,
                reportProgress: progress);
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
        (points, breaklineCurves, contourCurves) = FilterInputsToDataClip(
            snapshot, terrain, points, breaklineCurves, contourCurves,
            modifier.Tolerance > 0 ? modifier.Tolerance : GetToleranceProfile(snapshot, terrain).CurveChordTolerance,
            build, shouldCancel);

        if (points.Count == 0 && breaklineCurves.Count == 0 && contourCurves.Count == 0)
        {
            build.Diagnostics.Add("Add Geometry has no sources.");
            return RhinoGeometryConversions.DuplicateWithCachedData(mesh);
        }

        if (!RhinoGeometryConversions.TryExtractMeshData(
                mesh, out var meshVertices, out int meshVertexCount, out var meshFaces, out int meshFaceCount, out var errorMessage))
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

        // Add Geometry only ever adds detail inside the terrain it is given — the rebuild below is bounded
        // by that terrain's own outline — so insert the new data locally. A rebuild re-Delaunays the whole
        // surface from its vertices: it discards every edge an upstream stage chose and merges vertices by
        // XY alone, collapsing any steep face narrower than the merge tolerance. The rebuild stays only as
        // the fallback for a local insertion that cannot complete.
        if (TryAddGeometryLocally(
                meshVertices, meshVertexCount, meshFaces, meshFaceCount, points, breaklineCurves, contourCurves,
                toleranceProfile, inputTolerance, curveTolerance, build, modifier.Label, out RhinoMesh? localMesh))
        {
            return localMesh!;
        }

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

        // Persist the new lines as triangulated (stationed), never as drawn — see the Triangulate stage.
        List<TerrainTriangulationInputBuilder.FlattenedPolyline> flattenedBreaklines =
            TerrainTriangulationInputBuilder.CreateFlattenedPolylines(breaklineCurves, curveTolerance);
        List<TerrainTriangulationInputBuilder.FlattenedPolyline> flattenedContours =
            TerrainTriangulationInputBuilder.CreateFlattenedPolylines(contourCurves, curveTolerance);
        TerrainConstraintPreprocessor.Result processed = TerrainConstraintPreprocessor.ProcessSeparately(
            flattenedBreaklines.Select(static polyline => polyline.Points).ToList(),
            flattenedContours.Select(static polyline => polyline.Points).ToList(),
            curveTolerance,
            spotXyz);
        var newHardConstraints = CreateConstraintPolylines(flattenedBreaklines, processed.Breaklines, preserveInputElevation: true);
        var persistentHardConstraints = CombineConstraints(build.PersistentHardConstraints, newHardConstraints);
        var newElevationConstraints = CreateConstraintPolylines(flattenedContours, processed.Contours, preserveInputElevation: true);
        var persistentElevationConstraints = CombineConstraints(build.PersistentElevationConstraints, newElevationConstraints);

        ThrowIfCancellationRequested(shouldCancel);
        var polylines = CreateFlatPolylines(CombineConstraints(build.PersistentHardConstraints, build.PersistentElevationConstraints));
        polylines.AddRange(processed.Breaklines.Concat(processed.Contours)
            .Where(static polyline => polyline != null)
            .Select(static polyline => polyline!));

        var boundaryPolylines = CombineBoundaryPolylines(
            CreateBoundaryPolylines(mesh, curveTolerance),
            Array.Empty<TinBoundaryPreparer.BoundaryPolyline>());

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

    private static bool TryAddGeometryLocally(
        double[] meshVertices,
        int meshVertexCount,
        int[] meshFaces,
        int meshFaceCount,
        IReadOnlyList<Point3d> points,
        IReadOnlyList<Curve> breaklineCurves,
        IReadOnlyList<Curve> contourCurves,
        TerrainTolerancePolicy.Profile toleranceProfile,
        double inputTolerance,
        double curveTolerance,
        TerrainBuildResult build,
        string label,
        out RhinoMesh? result)
    {
        result = null;
        var pointXyz = new double[points.Count * 3];
        for (int i = 0; i < points.Count; i++)
        {
            pointXyz[i * 3] = points[i].X;
            pointXyz[(i * 3) + 1] = points[i].Y;
            pointXyz[(i * 3) + 2] = points[i].Z;
        }

        var newHardConstraints = CreateConstraintPolylines(breaklineCurves, curveTolerance, preserveInputElevation: true);
        var newElevationConstraints = CreateConstraintPolylines(contourCurves, curveTolerance, preserveInputElevation: true);
        if (!TerrainDetailInserter.TryInsert(
                meshVertices, meshVertexCount, meshFaces, meshFaceCount,
                pointXyz,
                CombineConstraints(newHardConstraints, newElevationConstraints),
                build.PersistentHardConstraints,
                build.PersistentElevationConstraints,
                toleranceProfile.RemeshConstraintTolerance,
                inputTolerance,
                RemeshWallFaceMinSlopeDeg,
                out TerrainDetailInserter.Result? inserted,
                out string? error))
        {
            build.Diagnostics.Add($"{label}: local insertion declined ({error ?? "unknown reason"}); rebuilding the terrain instead.");
            return false;
        }

        result = RhinoGeometryConversions.BuildMesh(inserted!.Vertices, inserted.VertexCount, inserted.Faces, inserted.FaceCount);

        // Persist the lines as inserted, not as drawn: insertion split them at every edge they crossed and
        // snapped them through nearby vertices. Traced on the inserter's double-precision output, before
        // the Rhino mesh rounds it. A line that does not trace keeps its drawn form, as before.
        double traceTolerance = toleranceProfile.RemeshConstraintTolerance;
        newHardConstraints = InsertedConstraintTracer.TraceAll(
            newHardConstraints, inserted.Vertices, inserted.VertexCount, inserted.Faces, inserted.FaceCount, traceTolerance, out int tracedHard);
        newElevationConstraints = InsertedConstraintTracer.TraceAll(
            newElevationConstraints, inserted.Vertices, inserted.VertexCount, inserted.Faces, inserted.FaceCount, traceTolerance, out int tracedElevation);
        int untraced = (newHardConstraints.Count + newElevationConstraints.Count) - (tracedHard + tracedElevation);
        if (untraced > 0)
            build.Diagnostics.Add($"{label}: {untraced} inserted line(s) could not be traced through the terrain and were kept as drawn.");

        var persistentHard = CombineConstraints(build.PersistentHardConstraints, newHardConstraints);
        var persistentElevation = CombineConstraints(build.PersistentElevationConstraints, newElevationConstraints);
        build.PersistentHardConstraints.Clear();
        build.PersistentHardConstraints.AddRange(persistentHard);
        build.PersistentElevationConstraints.Clear();
        build.PersistentElevationConstraints.AddRange(persistentElevation);

        if (inserted.PointsOnExistingVertices > 0)
            build.Diagnostics.Add($"{inserted.PointsOnExistingVertices} added point(s) coincide with existing terrain vertices and were merged.");
        if (inserted.PointsOutsideTerrain > 0)
            build.Diagnostics.Add($"{inserted.PointsOutsideTerrain} added point(s) lie outside the terrain and were ignored.");
        if (inserted.VerticesHeldByWalls > 0)
            build.Diagnostics.Add($"{inserted.VerticesHeldByWalls} vertex/vertices on walls kept their elevation where added data crosses them.");
        return true;
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
        Func<bool>? shouldCancel = null,
        TerrainBuildProgressReporter? progress = null)
    {
        mesh = null;
        ThrowIfCancellationRequested(shouldCancel);

        progress?.Start(
            "Boundary preparation",
            $"{xyCoords.Length / 2:N0} vertices, {segments.Length / 2:N0} segments, {boundaryPolylines.Length:N0} boundary polylines");
        var prepared = TinBoundaryPreparer.Prepare(
            xyCoords,
            zValues,
            segments,
            boundaryPolylines,
            tolerance);
        progress?.Complete("Boundary preparation", $"{prepared.XyCoords.Length / 2:N0} vertices, {prepared.Segments.Length / 2:N0} segments");
        ThrowIfCancellationRequested(shouldCancel);

        progress?.Start("TIN engine");
        var result = engine.Build(
            prepared.XyCoords,
            prepared.ZValues,
            prepared.Segments,
            QualitySettings.None,
            out message,
            useConvexHull: prepared.UseConvexHull,
            boundaryPeelSettings: boundaryPeelSettings,
            shouldCancel: shouldCancel,
            reportProgress: update =>
            {
                switch (update.Phase)
                {
                    case TinEngineBuildPhase.WaitingForGate:
                        progress?.Report("TIN engine", "waiting for shared engine gate");
                        break;
                    case TinEngineBuildPhase.GateAcquired:
                        progress?.Report(
                            "TIN engine",
                            "shared engine gate acquired",
                            $"gate wait {update.GateWait.TotalSeconds:0.###} s");
                        break;
                    case TinEngineBuildPhase.FullRebuildStarting:
                        progress?.Report("TIN engine", "full triangulation running");
                        break;
                }
            },
            includeEdgeTopology: false);
        progress?.Complete("TIN engine", result == null ? "failed" : $"{result.VertexCount:N0} vertices, {result.FaceCount:N0} faces");
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
            progress?.Start("Rhino mesh conversion");
            mesh = RhinoGeometryConversions.ToRhinoMesh(result);
            progress?.Complete("Rhino mesh conversion", $"{mesh.Vertices.Count:N0} vertices, {mesh.Faces.Count:N0} faces");
        }
        catch (Exception ex)
        {
            progress?.Report("Rhino mesh conversion", "failed", ex.Message);
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
    /// Dispatches to one of three remesh algorithms selected by <see cref="RemeshModifierDefinition.Mode"/>:
    /// isotropic (default, best quality), rebuild (classic constrained-Delaunay, wall-safe), or local refine
    /// (topology-preserving). All three see the same constraint stack (persistent hard constraints incl.
    /// walls/breaklines/grade-path edges, plus this modifier's own Constraints input).
    /// </summary>
    private static RhinoMesh ApplyRemesh(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        RemeshModifierDefinition modifier,
        TerrainBuildResult build,
        TerrainBuildMode mode,
        Func<bool>? shouldCancel)
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

        return modifier.Mode switch
        {
            "rebuild" => ApplyRemeshRebuild(snapshot, terrain, mesh, constraints, edgeLength, modifier, build, toleranceProfile),
            "local" => ApplyRemeshLocalRefine(mesh, constraints, edgeLength, modifier.CreaseAngle, toleranceProfile.RemeshConstraintTolerance, build),
            // Prototype, not offered on the card: TiledIsotropicRemesher, the locality-exact remesh the
            // incremental-rebuild design needs (docs/incremental-rebuild-design-2026-09-29.md, D2).
            "tiled" => ApplyRemeshIsotropic(mesh, constraints, localConstraints, edgeLength, modifier, build, mode, toleranceProfile, shouldCancel, tiled: true),
            _ => ApplyRemeshIsotropic(mesh, constraints, localConstraints, edgeLength, modifier, build, mode, toleranceProfile, shouldCancel),
        };
    }

    /// <summary>
    /// Isotropic remesh (split / collapse / flip / relax / back-project): regularizes the whole terrain
    /// toward even triangles of the target edge length while every vertex stays exactly on the input
    /// surface. Features (boundary ∪ creases at Crease Angle ∪ the whole constraint stack incl.
    /// grade-path road edges) are pinned polylines; steep retaining-wall faces pass through verbatim.
    /// </summary>
    private static RhinoMesh ApplyRemeshIsotropic(
        RhinoMesh mesh,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> constraints,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> localConstraints,
        double edgeLength,
        RemeshModifierDefinition modifier,
        TerrainBuildResult build,
        TerrainBuildMode mode,
        TerrainTolerancePolicy.Profile toleranceProfile,
        Func<bool>? shouldCancel,
        bool tiled = false)
    {
        if (!RhinoGeometryConversions.TryExtractMeshData(
                mesh, out var vertices, out int vertexCount, out var faces, out int faceCount, out var errorMessage))
        {
            build.Diagnostics.Add(errorMessage ?? "Could not extract mesh data for remesh.");
            return RhinoGeometryConversions.DuplicateWithCachedData(mesh);
        }

        // The isotropic remesher only PINS constraints that already run along mesh edges; it never inserts
        // one. Upstream constraints are embedded by the stage that made them, but this card's own
        // Constraints input is not, so without this step they were silently ignored. Insert them into the
        // existing faces (draped: Z comes from the terrain) so the remesher sees them as edges to keep.
        if (localConstraints.Count > 0)
        {
            if (MeshConstraintTopologyInserter.TryInsert(
                    vertices, vertexCount, faces, faceCount, localConstraints, toleranceProfile.RemeshConstraintTolerance,
                    out var insertedVertices, out int insertedVertexCount, out var insertedFaces, out int insertedFaceCount,
                    out string? insertError))
            {
                vertices = insertedVertices.Length == insertedVertexCount * 3
                    ? insertedVertices
                    : insertedVertices[..(insertedVertexCount * 3)];
                faces = insertedFaces.Length == insertedFaceCount * 3
                    ? insertedFaces
                    : insertedFaces[..(insertedFaceCount * 3)];
            }
            else
            {
                build.Diagnostics.Add($"Remesh skipped because its breaklines could not be inserted: {insertError}");
                return RhinoGeometryConversions.DuplicateWithCachedData(mesh);
            }
        }

        // EdgeLength 0 = preserve the mesh's approximate global plan density. A median edge badly
        // over-refines terrains that mix dense feature sampling with large sparse outer faces.
        double target = edgeLength > 0
            ? edgeLength
            : IsotropicRemesher.EstimateFaceCountPreservingTarget(vertices, faces);
        if (mode == TerrainBuildMode.Preview && modifier.EdgeLength <= 0)
            target *= 2.0;
        if (target <= 0)
        {
            build.Diagnostics.Add("Remesh skipped: could not derive a target edge length.");
            return RhinoGeometryConversions.DuplicateWithCachedData(mesh);
        }

        var remeshOptions = new IsotropicRemesher.Options
            {
                TargetEdgeLength = target,
                CreaseAngleDeg = modifier.CreaseAngle,
                Tolerance = toleranceProfile.RemeshConstraintTolerance,
                WallFaceMinSlopeDeg = RemeshWallFaceMinSlopeDeg,
                Iterations = mode == TerrainBuildMode.Preview || modifier.EdgeLength <= 0 ? 3 : 5,

                // A rebuild the user has already superseded should stop inside the remesh, not after
                // it: this is the longest-running Core stage in the pipeline.
                ShouldCancel = shouldCancel
            };
        IsotropicRemesher.Result result = tiled
            ? TiledIsotropicRemesher.Remesh(vertices, faces, constraints, remeshOptions)
            : IsotropicRemesher.Remesh(vertices, faces, constraints, remeshOptions);

        if (!result.Success)
        {
            build.Diagnostics.Add((result.Warning ?? "Remesh kept the incoming mesh unchanged.") + $" [{result.Timing}]");
            return RhinoGeometryConversions.DuplicateWithCachedData(mesh);
        }

        build.Diagnostics.Add(
            $"Remesh {(tiled ? "tiled" : "isotropic")}: {result.Splits:N0} splits, {result.Collapses:N0} collapses, " +
            $"{result.Flips:N0} flips at target {target:0.###} " +
            $"({result.Faces.Length / 3:N0} faces; features and walls pinned) [{result.Timing}].");

        return BuildMeshFromArrays(result.Vertices, result.Faces);
    }

    /// <summary>
    /// Classic full re-triangulation from scratch (<see cref="SurfaceRemesher"/> via the shared
    /// <see cref="RebuildMeshWithConstraints"/> helper, also used by Retaining Wall rebuilds). Every
    /// constraint — including wall rails — becomes a hard edge of the constrained-Delaunay triangulation, so
    /// it structurally cannot connect across a wall. Coarser triangle shapes than Isotropic.
    /// </summary>
    private static RhinoMesh ApplyRemeshRebuild(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> constraints,
        double edgeLength,
        RemeshModifierDefinition modifier,
        TerrainBuildResult build,
        TerrainTolerancePolicy.Profile toleranceProfile)
    {
        RhinoMesh remeshed = RebuildMeshWithConstraints(
            snapshot,
            terrain,
            mesh,
            constraints,
            edgeLength,
            maxArea: modifier.MaxArea,
            minAngle: modifier.MinAngle,
            "Remesh",
            build,
            out bool keptInputMesh,
            toleranceOverride: toleranceProfile.RemeshConstraintTolerance,
            vertexMergeTolerance: 0.0,
            preserveCreaseAngleDeg: modifier.CreaseAngle);

        if (!keptInputMesh)
        {
            build.Diagnostics.Add(
                $"Remesh full rebuild: {remeshed.Faces.Count:N0} faces at target {edgeLength:0.###} " +
                "(constraints and walls pinned).");
        }

        return remeshed;
    }

    /// <summary>
    /// Connectivity-preserving "Local refine" remesh: keeps the input topology (flow lines), splits only
    /// coarse triangles in place on the surface, and flips toward a regular mesh — respecting creases. Unlike
    /// <see cref="ApplyRemeshRebuild"/>, it never re-triangulates from scratch, so graded corridor structure
    /// survives and no off-surface Steiner points are introduced. Fastest and safest on very large terrains
    /// and delicate wall/pad topology, but coarser and can't change overall mesh flow.
    /// </summary>
    private static RhinoMesh ApplyRemeshLocalRefine(
        RhinoMesh mesh,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> constraints,
        double requestedEdgeLength,
        double creaseAngleDeg,
        double tolerance,
        TerrainBuildResult build)
    {
        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out var faces, out var errorMessage))
        {
            build.Diagnostics.Add(errorMessage ?? "Could not extract mesh data for remesh.");
            return RhinoGeometryConversions.DuplicateWithCachedData(mesh);
        }

        LocalMeshRefiner.Result result = LocalMeshRefiner.Refine(
            vertices,
            faces,
            constraints,
            new LocalMeshRefiner.Options
            {
                TargetEdgeLength = requestedEdgeLength,
                MaxArea = 0,
                CreaseAngleDeg = creaseAngleDeg,
                Tolerance = tolerance,
                DoFlips = false
            });

        if (!result.Success)
        {
            build.Diagnostics.Add(result.Warning ?? "Remesh local refine kept the incoming mesh unchanged.");
            return RhinoGeometryConversions.DuplicateWithCachedData(mesh);
        }

        build.Diagnostics.Add(
            $"Remesh local refine: +{result.AddedVertices:N0} vertices, preserved input flow " +
            $"({result.Faces.Length / 3:N0} faces).");

        return BuildMeshFromArrays(result.Vertices, result.Faces);
    }

    /// <summary>
    /// Field-guided quad retopology: replace the terrain with a quad-dominant mesh whose edges flow
    /// along the features. Runs <see cref="QuadRemesher"/> (cross-field → field-aligned isotropic
    /// remesh → tri-to-quad pairing) on the input mesh + the whole constraint stack (incl. grade-path
    /// road edges via <c>PersistentHardConstraints</c>). One connected mesh, hole-free by construction;
    /// steep retaining-wall faces pass through frozen. Falls back to the input mesh on failure.
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
            return RhinoGeometryConversions.DuplicateWithCachedData(mesh);
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
                // Steep retaining-wall faces are frozen through the retopo (never cut out or rebuilt),
                // so walls pass through exactly and the output cannot acquire holes at wall joins.
                WallFaceMinSlopeDeg = RetopoWallFaceMinSlopeDeg,
                Iterations = mode == TerrainBuildMode.Preview ? 3 : 5
            });

        if (!result.Success || result.QuadCount == 0)
        {
            build.Diagnostics.Add(result.Warning ?? "Retopo produced no quads; kept the input mesh.");
            return RhinoGeometryConversions.DuplicateWithCachedData(mesh);
        }

        if (!ValidateQuadDominantTopology(faces, result.Quads, result.Tris, out string? topologyWarning))
        {
            build.Diagnostics.Add(topologyWarning ?? "Retopo produced invalid topology; kept the input mesh.");
            return RhinoGeometryConversions.DuplicateWithCachedData(mesh);
        }

        build.Diagnostics.Add(
            $"Retopo quads: {result.QuadCount:N0} quads + {result.TriangleCount:N0} triangles over " +
            $"{result.Vertices.Length / 3:N0} vertices (field-aligned remesh + pairing; features and walls pinned)." +
            (string.IsNullOrWhiteSpace(result.Warning) ? "" : $" {result.Warning}"));

        return RhinoGeometryConversions.BuildQuadDominantMesh(result.Vertices, result.Quads, result.Tris);
    }

    // Near-vertical faces (retaining walls) are frozen through the retopo at this slope.
    private const double RetopoWallFaceMinSlopeDeg = 70.0;

    /// <summary>
    /// Accepts the quad-dominant output only when its topology is no worse than the input's — imperfect
    /// upstream grading may already be non-manifold, and the retopo carries those quarantined zones
    /// through unchanged rather than repairing or worsening them.
    /// </summary>
    private static bool ValidateQuadDominantTopology(int[] inputFaces, int[] quads, int[] tris, out string? warning)
    {
        int[] triangleFaces = BuildTriangleFacesForValidation(quads, tris);
        if (triangleFaces.Length == 0)
        {
            warning = "Retopo produced no valid faces; kept the input mesh.";
            return false;
        }

        MeshTopologyValidator.BoundaryGraphAnalysis input =
            MeshTopologyValidator.AnalyzeBoundaryGraph(inputFaces, inputFaces.Length / 3);
        MeshTopologyValidator.BoundaryGraphAnalysis topology =
            MeshTopologyValidator.AnalyzeBoundaryGraph(triangleFaces, triangleFaces.Length / 3);
        if (topology.NonManifoldEdgeCount > input.NonManifoldEdgeCount)
        {
            warning = $"Retopo produced {topology.NonManifoldEdgeCount:N0} non-manifold edge(s) " +
                $"(input had {input.NonManifoldEdgeCount:N0}); kept the input mesh.";
            return false;
        }

        if (topology.HasOpenBoundaryChains && !input.HasOpenBoundaryChains)
        {
            warning = "Retopo produced open naked-edge chains; kept the input mesh.";
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

    private static double EstimateQuadSpacing(double[] vertices)
    {
        int vertexCount = vertices.Length / 3;
        if (vertexCount == 0)
            return double.Epsilon;

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        double coordinateScale = 0.0;
        for (int i = 0; i < vertexCount; i++)
        {
            double x = vertices[i * 3], y = vertices[i * 3 + 1];
            coordinateScale = Math.Max(coordinateScale, Math.Max(Math.Abs(x), Math.Abs(y)));
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        double diagonal = Math.Sqrt(((maxX - minX) * (maxX - minX)) + ((maxY - minY) * (maxY - minY)));
        double spacing = diagonal / Math.Max(1.0, Math.Sqrt(vertexCount));
        return Math.Max(spacing, Math.Max(coordinateScale * 1e-12, double.Epsilon));
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
        if (!(crossHalf > 0.0) || !double.IsFinite(crossHalf))
            crossHalf = Math.Max(spacing, toleranceProfile.RemeshConstraintTolerance) * 0.5;

        // Decimate onto a grid ~3 crosses apart so the comb reads instead of matting into a solid patch.
        double cellSize = crossHalf * 3.0;
        double invCell = 1.0 / cellSize;
        var used = new HashSet<long>();
        var guidePrimitives = new List<RuntimeOverlayPrimitive>();

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
            guidePrimitives.Add(RuntimeOverlayPrimitive.Polyline(new[] { point - along, point + along }, colorArgb: argb));
            guidePrimitives.Add(RuntimeOverlayPrimitive.Polyline(new[] { point - across, point + across }, colorArgb: argb));
        }

        build.RuntimeOverlays.Add(new RuntimeOverlayItem
        {
            StableId = $"retopo-field:{modifier.Id:N}",
            Owner = new RuntimeOverlayOwner(RuntimeOverlayOwnerKind.Modifier, modifier.Id),
            Channel = RuntimeOverlayChannel.Guide,
            Code = "retopo.field",
            Message = "Retopo Stage 1 cross-field guide.",
            ShortLabel = "Field",
            Primitives = guidePrimitives
        });

        build.Diagnostics.Add(
            $"Retopo Stage 1 field preview: {vertexCount:N0} vertices, {pinnedCount:N0} feature-pinned, " +
            $"{guidePrimitives.Count:N0} overlay segments." +
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
        int modifierIndex,
        string stageKey,
        TerrainBuildMode mode)
    {
        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out int vertexCount, out var faces, out int faceCount, out var errorMessage))
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

        AddSelectedGradePathRoadBreaklines(
            snapshot,
            terrain,
            modifier,
            modifierIndex,
            tolerance,
            breaklines);

        string preparedStageKey = TerrainStageKey.CreateSmoothPrepared(stageKey);
        ulong preparedFingerprint = ComputeSmoothPreparedFingerprint(
            snapshot,
            terrain,
            modifier,
            modifierIndex,
            ComputeMeshFingerprint(mesh));
        MeshSmoother.PreparedSmoothingData prepared;
        if (runtimeCache.SmoothEntries.TryGetValue(preparedStageKey, out var cachedPrepared) &&
            cachedPrepared.Fingerprint == preparedFingerprint)
        {
            // Prepared smoothing data is immutable after construction. Share it between the UI cache
            // and worker builds instead of copying the full adjacency/mask arrays on every scrub.
            prepared = cachedPrepared.Prepared;
        }
        else
        {
            prepared = MeshSmoother.Prepare(
                vertices,
                vertexCount,
                faces,
                faceCount,
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

        for (int i = 0; i < vertexCount; i++)
        {
            smoothed[i * 3] = vertices[i * 3];
            smoothed[i * 3 + 1] = vertices[i * 3 + 1];
        }

        if (smoothed.Any(value => double.IsNaN(value) || double.IsInfinity(value)))
        {
            build.Diagnostics.Add("Smooth produced invalid mesh data. Incoming mesh kept.");
            return mesh;
        }

        var smoothedMesh = RhinoGeometryConversions.BuildMesh(smoothed, vertexCount, faces, faceCount);
        if (smoothedMesh.Faces.Count == 0 || smoothedMesh.Vertices.Count == 0 || !smoothedMesh.IsValid)
        {
            build.Diagnostics.Add("Smooth produced an invalid mesh. Incoming mesh kept.");
            return mesh;
        }

        return smoothedMesh;
    }

    private static void AddSelectedGradePathRoadBreaklines(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        SmoothModifierDefinition smooth,
        int smoothModifierIndex,
        double curveTolerance,
        List<MeshSmoother.BreaklinePolyline> breaklines)
    {
        HashSet<Guid> selectedSourceIds = ResolveSourceObjectIds(snapshot, smooth.Breaklines);
        if (selectedSourceIds.Count == 0)
            return;

        var selectedPaths = new List<PathGrader.PathDefinition>();
        foreach (GradePathModifierDefinition gradePath in EnumeratePriorEnabledGradePathModifiers(terrain, smoothModifierIndex))
        {
            (ResolvedGradePathDefinition[] resolvedPaths, _) = ResolveGradePathDefinitions(
                snapshot,
                gradePath,
                curveTolerance,
                curveTolerance);
            foreach (ResolvedGradePathDefinition resolvedPath in resolvedPaths)
            {
                if (resolvedPath.SourceObjectId == Guid.Empty ||
                    !selectedSourceIds.Contains(resolvedPath.SourceObjectId))
                    continue;
                selectedPaths.Add(resolvedPath.Definition);
            }
        }

        if (selectedPaths.Count == 0)
            return;

        foreach (PathGrader.PathDefinition selectedPath in selectedPaths)
            AppendGradePathRoadBreaklines(selectedPath, breaklines);
    }

    private static ulong ComputeSelectedGradePathRoadBreaklinesFingerprint(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        SmoothModifierDefinition smooth,
        int smoothModifierIndex)
    {
        HashSet<Guid> selectedSourceIds = ResolveSourceObjectIds(snapshot, smooth.Breaklines);
        if (selectedSourceIds.Count == 0)
            return 0UL;

        var builder = new FingerprintBuilder();
        builder.Add("SmoothSelectedGradePathRoadBreaklinesV1");
        int matchedModifierCount = 0;
        foreach ((int _, GradePathModifierDefinition gradePath) in EnumeratePriorEnabledGradePathModifiersWithIndex(terrain, smoothModifierIndex))
        {
            var matchingIds = TerrainBuildSnapshotResolver
                .ResolveObjects(snapshot, gradePath.Paths)
                .Select(static sourceObject => sourceObject.ObjectId)
                .Where(objectId => objectId != Guid.Empty && selectedSourceIds.Contains(objectId))
                .Distinct()
                .OrderBy(static objectId => objectId)
                .ToArray();
            if (matchingIds.Length == 0)
                continue;

            matchedModifierCount++;
            builder.Add(gradePath.Id);
            builder.Add(gradePath.Width);
            builder.Add(gradePath.SlopeAngle);
            builder.Add(gradePath.CutSlopeAngle);
            builder.Add(gradePath.MaxDistance);
            builder.Add(gradePath.MaxEdgeDistance);
            builder.Add(ComputeSourceSetFingerprint(snapshot, gradePath.Paths));
            builder.Add(ComputeSourceSetFingerprint(snapshot, gradePath.WidthEdges));
            builder.Add(matchingIds.Length);
            foreach (Guid objectId in matchingIds)
                builder.Add(objectId);
        }

        return matchedModifierCount == 0 ? 0UL : builder.ToUInt64();
    }

    private static IEnumerable<GradePathModifierDefinition> EnumeratePriorEnabledGradePathModifiers(
        TerrainDefinition terrain,
        int modifierIndex)
    {
        return EnumeratePriorEnabledGradePathModifiersWithIndex(terrain, modifierIndex)
            .Select(static item => item.GradePath);
    }

    private static IEnumerable<(int Index, GradePathModifierDefinition GradePath)> EnumeratePriorEnabledGradePathModifiersWithIndex(
        TerrainDefinition terrain,
        int modifierIndex)
    {
        int endIndex = Math.Min(Math.Max(modifierIndex, 0), terrain.Modifiers.Count);
        for (int index = 0; index < endIndex; index++)
        {
            if (terrain.Modifiers[index] is GradePathModifierDefinition gradePath && gradePath.IsEnabled)
                yield return (index, gradePath);
        }
    }

    private static HashSet<Guid> ResolveSourceObjectIds(TerrainBuildSnapshot snapshot, SourceReferenceSet sourceSet)
    {
        return TerrainBuildSnapshotResolver
            .ResolveObjects(snapshot, sourceSet)
            .Select(static sourceObject => sourceObject.ObjectId)
            .Where(static objectId => objectId != Guid.Empty)
            .ToHashSet();
    }

    private static void AppendGradePathRoadBreaklines(
        PathGrader.PathDefinition path,
        List<MeshSmoother.BreaklinePolyline> breaklines)
    {
        if (path.VertexCount < 2 || path.Width <= 0.0)
            return;

        int vertexCount = path.VertexCount;
        var center = new double[vertexCount * 2];
        var left = new double[vertexCount * 2];
        var right = new double[vertexCount * 2];
        double halfWidth = path.Width * 0.5;
        for (int i = 0; i < vertexCount; i++)
        {
            double x = path.XyVertices[i * 2];
            double y = path.XyVertices[(i * 2) + 1];
            ComputePathPointTangent(path.XyVertices, vertexCount, i, out double tangentX, out double tangentY);
            double normalX = -tangentY;
            double normalY = tangentX;

            center[i * 2] = x;
            center[(i * 2) + 1] = y;
            left[i * 2] = path.LeftEdgeXy?[i * 2] ?? x + (normalX * halfWidth);
            left[(i * 2) + 1] = path.LeftEdgeXy?[(i * 2) + 1] ?? y + (normalY * halfWidth);
            right[i * 2] = path.RightEdgeXy?[i * 2] ?? x - (normalX * halfWidth);
            right[(i * 2) + 1] = path.RightEdgeXy?[(i * 2) + 1] ?? y - (normalY * halfWidth);
        }

        breaklines.Add(new MeshSmoother.BreaklinePolyline(center, vertexCount, IsClosed: false));
        breaklines.Add(new MeshSmoother.BreaklinePolyline(left, vertexCount, IsClosed: false));
        breaklines.Add(new MeshSmoother.BreaklinePolyline(right, vertexCount, IsClosed: false));
    }

    private static void ComputePathPointTangent(
        double[] xyVertices,
        int vertexCount,
        int index,
        out double tangentX,
        out double tangentY)
    {
        int startIndex = index == 0 ? 0 : index - 1;
        int endIndex = index >= vertexCount - 1 ? vertexCount - 1 : index + 1;
        tangentX = xyVertices[endIndex * 2] - xyVertices[startIndex * 2];
        tangentY = xyVertices[(endIndex * 2) + 1] - xyVertices[(startIndex * 2) + 1];
        double length = Math.Sqrt((tangentX * tangentX) + (tangentY * tangentY));
        if (length > 1e-12)
        {
            tangentX /= length;
            tangentY /= length;
            return;
        }

        tangentX = 1.0;
        tangentY = 0.0;
    }
}
