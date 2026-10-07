using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Shared;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Per-modifier-type build-stage bodies, dispatched from the registry instead of a central switch.
/// Each <c>RunXStage</c> mirrors exactly one former <c>case</c> in <c>Build</c>'s modifier loop:
/// it reads the incoming mesh/fingerprint from the <see cref="ModifierBuildContext"/> and writes back the
/// outgoing mesh/fingerprint (and, for triangulate, the captured base mesh). The heavy lifting still
/// lives in the private <c>ApplyX</c>/<c>BuildX</c> helpers; these are thin shims so a modifier
/// descriptor can own its build step (Blender-style "register a type, it builds itself").
/// </summary>
internal sealed partial class TerrainBuildService
{
    internal static void RunTriangulateStage(ModifierBuildContext c)
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
            c.BaseMeshFingerprint = ComputeMeshFingerprint(c.BaseMesh);
        }
    }

    internal static void RunAddGeometryStage(ModifierBuildContext c)
    {
        var addGeometry = (AddGeometryModifierDefinition)c.Modifier;
        RhinoMesh? input = c.CurrentMesh;
        c.CurrentMesh = ExecuteCachedMeshStage(
            c.Build,
            c.RuntimeCache,
            c.StageKey,
            "Add Geometry",
            ComputeModifierStageFingerprint(c.Snapshot, c.Terrain, addGeometry, c.CurrentMeshFingerprint),
            () => input == null ? WarnMissingMesh(c.Build, addGeometry.Label) : ApplyAddGeometry(c.Snapshot, c.Terrain, input, addGeometry, c.Build, c.RuntimeCache, c.ShouldCancel),
            result => DescribeModifierMeshResult(addGeometry.Label, result),
            out ulong fingerprint,
            c.ShouldCancel);
        c.CurrentMeshFingerprint = fingerprint;
    }

    internal static void RunRemeshStage(ModifierBuildContext c)
    {
        var remesh = (RemeshModifierDefinition)c.Modifier;
        RhinoMesh? input = c.CurrentMesh;
        c.CurrentMesh = ExecuteCachedMeshStage(
            c.Build,
            c.RuntimeCache,
            c.StageKey,
            "Remesh",
            ComputeModifierStageFingerprint(c.Snapshot, c.Terrain, remesh, c.CurrentMeshFingerprint),
            () => input == null ? WarnMissingMesh(c.Build, remesh.Label) : ApplyRemesh(c.Snapshot, c.Terrain, input, remesh, c.Build, c.Mode, c.ShouldCancel, c.RuntimeCache, c.StageKey),
            result => DescribeModifierMeshResult(remesh.Label, result),
            out ulong fingerprint,
            c.ShouldCancel);
        c.CurrentMeshFingerprint = fingerprint;
    }

    internal static void RunRetopoStage(ModifierBuildContext c)
    {
        var retopo = (RetopoModifierDefinition)c.Modifier;
        RhinoMesh? input = c.CurrentMesh;
        if (input == null)
        {
            WarnMissingMesh(c.Build, retopo.Label);
            return;
        }

        // Quad extraction replaces the mesh (Stage 2) — go through the cached mesh stage.
        if (retopo.Quads)
        {
            c.CurrentMesh = ExecuteCachedMeshStage(
                c.Build,
                c.RuntimeCache,
                c.StageKey,
                "Retopo",
                ComputeModifierStageFingerprint(c.Snapshot, c.Terrain, retopo, c.CurrentMeshFingerprint),
                () => ApplyRetopoQuads(c.Snapshot, c.Terrain, input, retopo, c.Build, c.Mode),
                result => DescribeModifierMeshResult(retopo.Label, result),
                out ulong fingerprint,
                c.ShouldCancel);
            c.CurrentMeshFingerprint = fingerprint;
        }

        // The flow-cross overlay reads the field on the input triangle mesh; recompute each build (preview
        // only, independent of the mesh cache) so it survives cache hits.
        if (retopo.ShowField)
            BuildRetopoFieldOverlay(c.Snapshot, c.Terrain, input, retopo, c.Build);
    }

    internal static void RunSmoothStage(ModifierBuildContext c)
    {
        var smooth = (SmoothModifierDefinition)c.Modifier;
        c.UsedStageKeys.Add(TerrainStageKey.CreateSmoothPrepared(c.StageKey));
        RhinoMesh? input = c.CurrentMesh;
        c.CurrentMesh = ExecuteCachedMeshStage(
            c.Build,
            c.RuntimeCache,
            c.StageKey,
            "Smooth",
            ComputeSmoothStageFingerprint(c.Snapshot, c.Terrain, smooth, c.Index, c.CurrentMeshFingerprint),
            () => input == null ? WarnMissingMesh(c.Build, smooth.Label) : ApplySmooth(c.Snapshot, c.Terrain, input, smooth, c.Build, c.RuntimeCache, c.Index, c.StageKey, c.Mode),
            result => DescribeModifierMeshResult(smooth.Label, result),
            out ulong fingerprint,
            c.ShouldCancel);
        c.CurrentMeshFingerprint = fingerprint;
    }

    internal static void RunGradePadStage(ModifierBuildContext c)
    {
        var gradePad = (GradePadModifierDefinition)c.Modifier;
        c.UsedStageKeys.Add(TerrainStageKey.CreateGradingTopology(c.StageKey, "Pad"));
        c.CurrentMesh = BuildGradePadMesh(
            c.Snapshot,
            c.Terrain,
            gradePad,
            c.Build,
            c.RuntimeCache,
            c.Index,
            c.StageKey,
            c.CurrentMesh,
            c.CurrentMeshFingerprint,
            c.Mode,
            out ulong fingerprint,
            c.ShouldCancel);
        c.CurrentMeshFingerprint = fingerprint;
    }

    internal static void RunGradePathStage(ModifierBuildContext c)
    {
        var gradePath = (GradePathModifierDefinition)c.Modifier;
        c.UsedStageKeys.Add(TerrainStageKey.CreateGradingTopology(c.StageKey, "Path"));
        RhinoMesh? input = c.CurrentMesh;
        c.CurrentMesh = ExecuteCachedMeshStage(
            c.Build,
            c.RuntimeCache,
            c.StageKey,
            "Grade Path",
            ComputeModifierStageFingerprint(c.Snapshot, c.Terrain, gradePath, c.CurrentMeshFingerprint),
            () => input == null ? WarnMissingMesh(c.Build, gradePath.Label) : ApplyGradePath(c.Snapshot, c.Terrain, input, gradePath, c.Build, c.RuntimeCache, c.Index, c.StageKey, c.Mode),
            result => DescribeModifierMeshResult(gradePath.Label, result),
            out ulong fingerprint,
            c.ShouldCancel);
        c.CurrentMeshFingerprint = fingerprint;
    }

    internal static void RunInSituStairStage(ModifierBuildContext c)
    {
        var inSituStair = (InSituStairModifierDefinition)c.Modifier;
        RhinoMesh? input = c.CurrentMesh;
        c.CurrentMesh = ExecuteCachedMeshStage(
            c.Build,
            c.RuntimeCache,
            c.StageKey,
            "In-Situ Stair",
            ComputeModifierStageFingerprint(c.Snapshot, c.Terrain, inSituStair, c.CurrentMeshFingerprint),
            () => input == null
                ? WarnMissingMesh(c.Build, inSituStair.Label)
                : ApplyInSituStair(c.Snapshot, c.Terrain, input, inSituStair, c.Build, c.Mode),
            result => DescribeModifierMeshResult(inSituStair.Label, result),
            out ulong fingerprint,
            c.ShouldCancel);
        c.CurrentMeshFingerprint = fingerprint;
        if (c.RuntimeCache.StageEntries.TryGetValue(c.StageKey, out var stairStageEntry))
        {
            if (!string.IsNullOrWhiteSpace(inSituStair.ComputedTreadDepthSummary))
            {
                stairStageEntry.StairSurfaceCount = inSituStair.ComputedSurfaceCount;
                stairStageEntry.StairTreadDepthSummary = inSituStair.ComputedTreadDepthSummary;
                stairStageEntry.StairStepCountSummary = inSituStair.ComputedStepCountSummary;
            }
            else if (!string.IsNullOrWhiteSpace(stairStageEntry.StairTreadDepthSummary))
            {
                inSituStair.ComputedSurfaceCount = stairStageEntry.StairSurfaceCount;
                inSituStair.ComputedTreadDepthSummary = stairStageEntry.StairTreadDepthSummary;
                inSituStair.ComputedStepCountSummary = stairStageEntry.StairStepCountSummary;
            }
        }
    }
}
