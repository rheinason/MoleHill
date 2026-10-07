using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization.Metadata;
using System.Text.Json;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using MoleHill.Core.Processing;
using MoleHill.Core.Retopo;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Shared;
using Rhino.Geometry;
using Rhino;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Triangulate modifier build stage: builds the base TIN from points, breaklines, contours and DEM sources.
/// </summary>
internal static class TriangulateStage
{
    internal static void Run(ModifierBuildContext c)
    {
        var triangulate = (TriangulateModifierDefinition)c.Modifier;
        c.CurrentMesh = BuildTinMesh(c.Snapshot, c.Terrain, triangulate, c.Build, c.RuntimeCache, c.StageKey, out ulong fingerprint, c.ShouldCancel, c.ReportProgress);
        c.CurrentMeshFingerprint = fingerprint;
        if (c.CurrentMesh != null && c.BaseMesh == null)
        {
            var progress = new TerrainBuildProgressReporter(c.ReportProgress);
            progress.Start("Base-mesh duplicate");
            c.BaseMesh = RhinoGeometryConversions.DuplicateWithCachedData(c.CurrentMesh);
            progress.Complete("Base-mesh duplicate", $"{c.BaseMesh.Vertices.Count:N0} vertices, {c.BaseMesh.Faces.Count:N0} faces");
            c.BaseMeshFingerprint = TerrainBuildService.ComputeMeshFingerprint(c.BaseMesh);
        }
    }

    private const int TriangulateCacheVersion = 5;

    private static ulong ComputeTriangulatePreResolutionFingerprint(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        TriangulateModifierDefinition modifier)
    {
        var builder = new FingerprintBuilder();
        builder.Add("Triangulate");
        builder.Add(TriangulateCacheVersion);
        builder.Add(snapshot.ModelAbsoluteTolerance);
        builder.Add(terrain.GlobalTolerance);
        AddTriangulationSettingsFingerprint(ref builder, modifier);
        builder.Add(TerrainBuildService.ComputeSourceSetFingerprint(snapshot, modifier.TinMesh));
        builder.Add(TerrainBuildService.ComputeSourceSetFingerprint(snapshot, modifier.DemSurface));
        builder.Add(snapshot.DemFingerprints.GetValueOrDefault(modifier.Id));
        builder.Add(TerrainBuildService.ComputeSourceSetFingerprint(snapshot, modifier.Points));
        builder.Add(TerrainBuildService.ComputeSourceSetFingerprint(snapshot, modifier.Breaklines));
        builder.Add(TerrainBuildService.ComputeSourceSetFingerprint(snapshot, modifier.Contours));
        builder.Add(TerrainBuildService.ComputeSourceSetFingerprint(snapshot, modifier.DataClipBoundaries));
        return builder.ToUInt64();
    }

    private static ulong ComputeTriangulateResolvedInputFingerprint(
        TerrainDefinition terrain,
        TriangulateModifierDefinition modifier,
        double tolerance,
        double[] xyCoords,
        double[] zValues,
        int[] segments,
        IReadOnlyList<ConstraintPolyline> persistentHardConstraints,
        IReadOnlyList<TinBoundaryPreparer.BoundaryPolyline> boundaryPolylines)
    {
        var builder = new FingerprintBuilder();
        builder.Add("TriangulateResolved");
        builder.Add(TriangulateCacheVersion);
        builder.Add(terrain.GlobalTolerance);
        builder.Add(tolerance);
        AddTriangulationSettingsFingerprint(ref builder, modifier);
        TerrainBuildService.AddDoubleArrayFingerprint(ref builder, xyCoords);
        TerrainBuildService.AddDoubleArrayFingerprint(ref builder, zValues);
        TerrainBuildService.AddIntArrayFingerprint(ref builder, segments);
        builder.Add(TerrainBuildService.ComputeConstraintsFingerprint(persistentHardConstraints));
        builder.Add(ComputeBoundaryPolylinesFingerprint(boundaryPolylines));
        return builder.ToUInt64();
    }

    private static void AddTriangulationSettingsFingerprint(ref FingerprintBuilder builder, TriangulateModifierDefinition modifier)
    {
        builder.Add(modifier.Id);
        builder.Add(modifier.IsEnabled);
        builder.Add(modifier.Tolerance);
        builder.Add(modifier.PeelBoundaryTriangles);
        builder.Add(modifier.MaxBoundaryEdgeLength);
        builder.Add(modifier.MaxBoundaryAngleDegrees);
        builder.Add(modifier.MaxBoundarySlopeDegrees);
        builder.Add(modifier.ContourMode);
        builder.Add(modifier.DemElevationScale);
        builder.Add(modifier.DemSourceFileName);
    }

    private static ulong ComputeBoundaryPolylinesFingerprint(IReadOnlyList<TinBoundaryPreparer.BoundaryPolyline> boundaries)
    {
        var builder = new FingerprintBuilder();
        builder.Add(boundaries.Count);
        foreach (var boundary in boundaries)
        {
            builder.Add(boundary.PointCount);
            builder.Add(boundary.IsClosed);
            TerrainBuildService.AddDoubleArrayFingerprint(ref builder, boundary.Points);
        }

        return builder.ToUInt64();
    }

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
            RhinoMesh? cachedMesh = TerrainBuildService.RestoreCachedMeshStage(build, cachedEntry, out outputFingerprint);
            timer.Stop();
            build.RecordTiming(stageName, timer.Elapsed, TerrainBuildService.AppendCacheHitDetail(TerrainBuildService.DescribeModifierMeshResult(modifier.Label, cachedMesh)));
            return cachedMesh;
        }

        int diagnosticsStart = build.Diagnostics.Count;
        int structuredDiagnosticsStart = build.StructuredDiagnostics.Count;
        StageSupport.ThrowIfCancellationRequested(shouldCancel);
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
                return TerrainBuildService.StoreMeshStageCache(
                    build, runtimeCache, stageKey, stageName, preResolutionFingerprint,
                    TerrainBuildService.ComputeSourceSetFingerprint(snapshot, modifier.TinMesh), null,
                    build.PersistentHardConstraints, Array.Empty<GeneratedRhinoObject>(),
                    build.Diagnostics.Skip(diagnosticsStart), TerrainBuildService.DescribeModifierMeshResult(modifier.Label, null),
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
                return TerrainBuildService.StoreMeshStageCache(
                    build, runtimeCache, stageKey, stageName, preResolutionFingerprint,
                    TerrainBuildService.ComputeSourceSetFingerprint(snapshot, modifier.TinMesh), null,
                    build.PersistentHardConstraints, Array.Empty<GeneratedRhinoObject>(),
                    build.Diagnostics.Skip(diagnosticsStart), TerrainBuildService.DescribeModifierMeshResult(modifier.Label, null),
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
            return TerrainBuildService.StoreMeshStageCache(
                build, runtimeCache, stageKey, stageName, preResolutionFingerprint,
                TerrainBuildService.ComputeSourceSetFingerprint(snapshot, modifier.TinMesh), exactTin,
                build.PersistentHardConstraints, Array.Empty<GeneratedRhinoObject>(),
                build.Diagnostics.Skip(diagnosticsStart), TerrainBuildService.DescribeModifierMeshResult(modifier.Label, exactTin),
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
            return TerrainBuildService.StoreMeshStageCache(
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
                TerrainBuildService.DescribeModifierMeshResult(modifier.Label, null),
                timer,
                out outputFingerprint,
                structuredDiagnostics: build.StructuredDiagnostics.Skip(structuredDiagnosticsStart));
        }

        TerrainTolerancePolicy.Profile toleranceProfile = TerrainBuildService.GetToleranceProfile(snapshot, terrain);
        double inputTolerance = modifier.Tolerance > 0
            ? modifier.Tolerance
            : toleranceProfile.InputMergeTolerance;
        double curveTolerance = modifier.Tolerance > 0
            ? modifier.Tolerance
            : toleranceProfile.CurveChordTolerance;

        (points, breaklineCurves, contourCurves) = TerrainBuildService.FilterInputsToDataClip(
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

        StageSupport.ThrowIfCancellationRequested(shouldCancel);
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
        var persistentHardConstraints = TerrainBuildService.CreateConstraintPolylines(
            flattenedBreaklines,
            processed.Breaklines,
            preserveInputElevation: true);
        var persistentElevationConstraints = constrainContours
            ? TerrainBuildService.CreateConstraintPolylines(flattenedContours, processed.Contours, preserveInputElevation: true)
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
        StageSupport.ThrowIfCancellationRequested(shouldCancel);
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
            StageCacheEntry refreshedEntry = TerrainBuildService.CloneStageCacheEntry(
                cachedEntry,
                preResolutionFingerprint,
                resolvedInputFingerprint);
            runtimeCache.StageEntries[stageKey] = refreshedEntry;

            RhinoMesh? resolvedCachedMesh = TerrainBuildService.RestoreCachedMeshStage(build, refreshedEntry, out outputFingerprint);
            timer.Stop();
            build.RecordTiming(stageName, timer.Elapsed, TerrainBuildService.AppendCacheHitDetail(TerrainBuildService.DescribeModifierMeshResult(modifier.Label, resolvedCachedMesh)));
            return resolvedCachedMesh;
        }

        if (merged.VertexCount < 3)
        {
            build.Diagnostics.Add("Triangulate needs at least three unique points after deduplication.");
            return TerrainBuildService.StoreMeshStageCache(
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
                TerrainBuildService.DescribeModifierMeshResult(modifier.Label, null),
                timer,
                out outputFingerprint,
                structuredDiagnostics: build.StructuredDiagnostics.Skip(structuredDiagnosticsStart));
        }

        if (merged.InvalidsSkipped > 0)
            build.Diagnostics.Add($"{merged.DescribeInvalidPoints()} during triangulation.");
        if (merged.DuplicatesRemoved > 0)
            build.Diagnostics.Add($"{merged.DuplicatesRemoved} duplicate points merged during triangulation.");

        (double[] tinXy, double[] tinZ, int[] tinSegments) = TerrainBuildService.RepairTinInputTopology(merged, inputTolerance, build, modifier.Label, shouldCancel);
        if (TerrainBuildService.TryBuildValidatedTinMesh(
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
            return TerrainBuildService.StoreMeshStageCache(
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
                TerrainBuildService.DescribeModifierMeshResult(modifier.Label, exactMesh),
                timer,
                out outputFingerprint,
                reportProgress: progress);
        }

        if (!TerrainBuildService.ShouldAttemptTriangulationCleanupRetry(merged.VertexCount, merged.SegmentCount, shouldCancel, out var cleanupSkipMessage))
        {
            build.Diagnostics.Add(exactMessage ?? "Triangulation failed.");
            build.Diagnostics.Add(cleanupSkipMessage);
            return TerrainBuildService.StoreMeshStageCache(
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
                TerrainBuildService.DescribeModifierMeshResult(modifier.Label, null),
                timer,
                out outputFingerprint,
                shouldCancel);
        }

        var cleanup = TinInputCleaner.Clean(merged, inputTolerance);
        StageSupport.ThrowIfCancellationRequested(shouldCancel);
        if (!cleanup.HasChanges)
        {
            build.Diagnostics.Add(exactMessage ?? "Triangulation failed.");
            build.Diagnostics.Add($"Automatic input cleanup made no safe changes: {cleanup.ToDiagnosticSummary()}.");
            return TerrainBuildService.StoreMeshStageCache(
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
                TerrainBuildService.DescribeModifierMeshResult(modifier.Label, null),
                timer,
                out outputFingerprint);
        }

        if (cleanup.VertexCount < 3)
        {
            build.Diagnostics.Add(exactMessage ?? "Triangulation failed.");
            build.Diagnostics.Add($"Automatic input cleanup reduced the dataset below three usable vertices: {cleanup.ToDiagnosticSummary()}.");
            return TerrainBuildService.StoreMeshStageCache(
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
                TerrainBuildService.DescribeModifierMeshResult(modifier.Label, null),
                timer,
                out outputFingerprint);
        }

        StageSupport.ThrowIfCancellationRequested(shouldCancel);
        if (!TerrainBuildService.TryBuildValidatedTinMesh(
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
            return TerrainBuildService.StoreMeshStageCache(
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
                TerrainBuildService.DescribeModifierMeshResult(modifier.Label, null),
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
        return TerrainBuildService.StoreMeshStageCache(
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
            TerrainBuildService.DescribeModifierMeshResult(modifier.Label, cleanedMesh),
            timer,
            out outputFingerprint);
    }
}
