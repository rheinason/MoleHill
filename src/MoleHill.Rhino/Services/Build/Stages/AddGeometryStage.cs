using System.Diagnostics;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using MoleHill.Core.Processing;
using MoleHill.Core.Retopo;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Shared;
using Rhino.Geometry;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Add Geometry modifier build stage: inserts point/breakline geometry into the incoming mesh, locally when possible, by re-triangulating otherwise.
/// </summary>
internal static class AddGeometryStage
{
    internal static void Run(ModifierBuildContext c)
    {
        var addGeometry = (AddGeometryModifierDefinition)c.Modifier;
        RhinoMesh? input = c.CurrentMesh;
        c.CurrentMesh = TerrainBuildService.ExecuteCachedMeshStage(
            c.Build,
            c.RuntimeCache,
            c.StageKey,
            "Add Geometry",
            TerrainBuildService.ComputeModifierStageFingerprint(c.Snapshot, c.Terrain, addGeometry, c.CurrentMeshFingerprint),
            () => input == null ? TerrainBuildService.WarnMissingMesh(c.Build, addGeometry.Label) : ApplyAddGeometry(c.Snapshot, c.Terrain, input, addGeometry, c.Build, c.RuntimeCache, c.ShouldCancel),
            result => TerrainBuildService.DescribeModifierMeshResult(addGeometry.Label, result),
            out ulong fingerprint,
            c.ShouldCancel);
        c.CurrentMeshFingerprint = fingerprint;
    }

    private static TinBoundaryPreparer.BoundaryPolyline[] CombineBoundaryPolylines(
        IReadOnlyList<TinBoundaryPreparer.BoundaryPolyline> primary,
        IReadOnlyList<TinBoundaryPreparer.BoundaryPolyline> additional)
    {
        if (primary.Count == 0 && additional.Count == 0)
            return Array.Empty<TinBoundaryPreparer.BoundaryPolyline>();

        var result = new TinBoundaryPreparer.BoundaryPolyline[primary.Count + additional.Count];
        for (int i = 0; i < primary.Count; i++)
            result[i] = primary[i];
        for (int i = 0; i < additional.Count; i++)
            result[primary.Count + i] = additional[i];
        return result;
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
        TerrainBuildService.ThrowIfCancellationRequested(shouldCancel);
        var points = TerrainBuildSnapshotResolver.ResolvePoints(snapshot, modifier.Points);
        var breaklineCurves = TerrainBuildSnapshotResolver.ResolveCurves(snapshot, modifier.Breaklines);
        var contourCurves = TerrainBuildSnapshotResolver.ResolveCurves(snapshot, modifier.Contours);
        (points, breaklineCurves, contourCurves) = TerrainBuildService.FilterInputsToDataClip(
            snapshot, terrain, points, breaklineCurves, contourCurves,
            modifier.Tolerance > 0 ? modifier.Tolerance : TerrainBuildService.GetToleranceProfile(snapshot, terrain).CurveChordTolerance,
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

        TerrainTolerancePolicy.Profile toleranceProfile = TerrainBuildService.GetToleranceProfile(snapshot, terrain);
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
        var newHardConstraints = TerrainBuildService.CreateConstraintPolylines(flattenedBreaklines, processed.Breaklines, preserveInputElevation: true);
        var persistentHardConstraints = TerrainBuildService.CombineConstraints(build.PersistentHardConstraints, newHardConstraints);
        var newElevationConstraints = TerrainBuildService.CreateConstraintPolylines(flattenedContours, processed.Contours, preserveInputElevation: true);
        var persistentElevationConstraints = TerrainBuildService.CombineConstraints(build.PersistentElevationConstraints, newElevationConstraints);

        TerrainBuildService.ThrowIfCancellationRequested(shouldCancel);
        var polylines = TerrainBuildService.CreateFlatPolylines(TerrainBuildService.CombineConstraints(build.PersistentHardConstraints, build.PersistentElevationConstraints));
        polylines.AddRange(processed.Breaklines.Concat(processed.Contours)
            .Where(static polyline => polyline != null)
            .Select(static polyline => polyline!));

        var boundaryPolylines = CombineBoundaryPolylines(
            TerrainBuildService.CreateBoundaryPolylines(mesh, curveTolerance),
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

        if (!TerrainBuildService.ShouldAttemptTriangulationCleanupRetry(merged.VertexCount, merged.SegmentCount, shouldCancel, out var cleanupSkipMessage))
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

        if (!TerrainBuildService.TryBuildValidatedTinMesh(
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

        var newHardConstraints = TerrainBuildService.CreateConstraintPolylines(breaklineCurves, curveTolerance, preserveInputElevation: true);
        var newElevationConstraints = TerrainBuildService.CreateConstraintPolylines(contourCurves, curveTolerance, preserveInputElevation: true);
        if (!TerrainDetailInserter.TryInsert(
                meshVertices, meshVertexCount, meshFaces, meshFaceCount,
                pointXyz,
                TerrainBuildService.CombineConstraints(newHardConstraints, newElevationConstraints),
                build.PersistentHardConstraints,
                build.PersistentElevationConstraints,
                toleranceProfile.RemeshConstraintTolerance,
                inputTolerance,
                TerrainBuildService.RemeshWallFaceMinSlopeDeg,
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
        newHardConstraints = InsertedConstraintTracer.TraceAll(newHardConstraints, new IndexedTriMesh(inserted.Vertices, inserted.VertexCount, inserted.Faces, inserted.FaceCount), traceTolerance, out int tracedHard);
        newElevationConstraints = InsertedConstraintTracer.TraceAll(newElevationConstraints, new IndexedTriMesh(inserted.Vertices, inserted.VertexCount, inserted.Faces, inserted.FaceCount), traceTolerance, out int tracedElevation);
        int untraced = (newHardConstraints.Count + newElevationConstraints.Count) - (tracedHard + tracedElevation);
        if (untraced > 0)
            build.Diagnostics.Add($"{label}: {untraced} inserted line(s) could not be traced through the terrain and were kept as drawn.");

        var persistentHard = TerrainBuildService.CombineConstraints(build.PersistentHardConstraints, newHardConstraints);
        var persistentElevation = TerrainBuildService.CombineConstraints(build.PersistentElevationConstraints, newElevationConstraints);
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
}
