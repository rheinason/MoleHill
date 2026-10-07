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
    internal static bool TryBuildValidatedTinMesh(
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
    internal static (double[] Xy, double[] Z, int[] Segments) RepairTinInputTopology(
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

    internal static bool ShouldAttemptTriangulationCleanupRetry(
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
