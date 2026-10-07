using System.Diagnostics;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using MoleHill.Core.Processing;
using MoleHill.Core.Retopo;
using MoleHill.Rhino.Model;
using MoleHill.Shared;
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
            if (!RhinoGeometryConversions.TryExtractMeshData(exactTin, out _, out _, out _, out _, out string? exactTinError))
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
            : new List<ConstraintPolyline>();
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

        (double[] tinXy, double[] tinZ, int[] tinSegments) = RepairTinInputTopology(merged, inputTolerance, build, modifier.Label, shouldCancel);
        if (TryBuildValidatedTinMesh(
            tinXy,
            tinZ,
            tinSegments,
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

        (double[] tinXy, double[] tinZ, int[] tinSegments) = RepairTinInputTopology(merged, inputTolerance, build, modifier.Label, shouldCancel);
        if (TryBuildValidatedTinMesh(
            tinXy,
            tinZ,
            tinSegments,
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

    private const int CleanupMaxVertexCount = 120_000;
    private const int CleanupMaxSegmentCount = 180_000;

    /// <summary>
    /// The repairs <see cref="TinInputCleaner"/> makes without removing any data — degenerate and duplicate
    /// segments, tiny spikes, and crossings split at one shared vertex — run before every triangulation, not
    /// only after a failure. Left to the triangulator, a crossing becomes a Steiner point that can land a
    /// fraction of a millimetre from an existing vertex, and every such pair is a sliver that later local
    /// insertions (Add Geometry, walls, grading) must cut through. Collinear collapse is deliberately not
    /// run here: breaklines are stationed to their neighbours on purpose, and the persisted constraints
    /// record those stations, so removing them would bring back fans and constraint/mesh mismatches.
    /// </summary>
    private static (double[] Xy, double[] Z, int[] Segments) RepairTinInputTopology(
        PointCloudProcessor.MergedData merged,
        double inputTolerance,
        TerrainBuildResult build,
        string label,
        Func<bool>? shouldCancel)
    {
        ThrowIfCancellationRequested(shouldCancel);
        if (merged.SegmentCount == 0 || merged.VertexCount > CleanupMaxVertexCount || merged.SegmentCount > CleanupMaxSegmentCount)
            return (merged.XyCoords, merged.ZValues, merged.Segments);

        TinInputCleaner.CleanupResult repaired = TinInputCleaner.Clean(merged, inputTolerance, collapseCollinearVertices: false);
        ThrowIfCancellationRequested(shouldCancel);
        if (repaired.IntersectionConflictsDetected > 0)
        {
            build.Diagnostics.Add(
                $"{label}: {repaired.IntersectionConflictsDetected:N0} place(s) where breaklines or contours cross or overlap at " +
                $"different elevations; the terrain can only take one height at each. Overlapping sources — two contour sets " +
                "of the same ground, or a breakline drawn across contours at another height — are the usual cause.");
        }

        if (!repaired.HasChanges || repaired.VertexCount < 3)
            return (merged.XyCoords, merged.ZValues, merged.Segments);

        build.Diagnostics.Add($"{label}: input repaired before triangulation: {repaired.ToDiagnosticSummary()}.");
        return (repaired.XyCoords, repaired.ZValues, repaired.Segments);
    }

    private static bool ShouldAttemptTriangulationCleanupRetry(
        int vertexCount,
        int segmentCount,
        Func<bool>? shouldCancel,
        out string message)
    {
        ThrowIfCancellationRequested(shouldCancel);

        if (vertexCount > CleanupMaxVertexCount || segmentCount > CleanupMaxSegmentCount)
        {
            message = $"Automatic triangulation cleanup retry skipped for large input ({vertexCount:N0} verts, {segmentCount:N0} segments) to keep rebuilds responsive.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    /// <summary>Faces steeper than this are frozen retaining walls in the Remesh modifier (never touched).</summary>
    internal const double RemeshWallFaceMinSlopeDeg = 70.0;

    internal static IEnumerable<GradePathModifierDefinition> EnumeratePriorEnabledGradePathModifiers(
        TerrainDefinition terrain,
        int modifierIndex)
    {
        return EnumeratePriorEnabledGradePathModifiersWithIndex(terrain, modifierIndex)
            .Select(static item => item.GradePath);
    }

    internal static IEnumerable<(int Index, GradePathModifierDefinition GradePath)> EnumeratePriorEnabledGradePathModifiersWithIndex(
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

    internal static HashSet<Guid> ResolveSourceObjectIds(TerrainBuildSnapshot snapshot, SourceReferenceSet sourceSet)
    {
        return TerrainBuildSnapshotResolver
            .ResolveObjects(snapshot, sourceSet)
            .Select(static sourceObject => sourceObject.ObjectId)
            .Where(static objectId => objectId != Guid.Empty)
            .ToHashSet();
    }
}
