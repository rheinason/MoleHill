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
/// Remesh modifier build stage: dispatches to the isotropic, rebuild or local-refine remesh by the card mode.
/// </summary>
internal static class RemeshStage
{
    internal static void Run(ModifierBuildContext c)
    {
        var remesh = (RemeshModifierDefinition)c.Modifier;
        RhinoMesh? input = c.CurrentMesh;
        c.CurrentMesh = TerrainBuildService.ExecuteCachedMeshStage(
            c.Build,
            c.RuntimeCache,
            c.StageKey,
            "Remesh",
            TerrainBuildService.ComputeModifierStageFingerprint(c.Snapshot, c.Terrain, remesh, c.CurrentMeshFingerprint),
            () => input == null ? TerrainBuildService.WarnMissingMesh(c.Build, remesh.Label) : ApplyRemesh(c.Snapshot, c.Terrain, input, remesh, c.Build, c.Mode, c.ShouldCancel, c.RuntimeCache, c.StageKey),
            result => TerrainBuildService.DescribeModifierMeshResult(remesh.Label, result),
            out ulong fingerprint,
            c.ShouldCancel);
        c.CurrentMeshFingerprint = fingerprint;
    }

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
        Func<bool>? shouldCancel,
        TerrainRuntimeCache? runtimeCache = null,
        string? stageKey = null)
    {
        TerrainTolerancePolicy.Profile toleranceProfile = TerrainBuildService.GetToleranceProfile(snapshot, terrain);
        double tolerance = toleranceProfile.CurveChordTolerance;
        double edgeLength = modifier.EdgeLength;
        if (mode == TerrainBuildMode.Preview && edgeLength > 0)
            edgeLength *= 2.0;

        var localConstraints = TerrainBuildService.CreateConstraintPolylines(
            TerrainBuildSnapshotResolver.ResolveCurves(snapshot, modifier.Constraints),
            tolerance,
            preserveInputElevation: false,
            requestedEdgeLength: edgeLength,
            maxArea: 0);

        var constraints = TerrainBuildService.CombineConstraints(build.PersistentHardConstraints, localConstraints);

        return modifier.Mode switch
        {
            "rebuild" => ApplyRemeshRebuild(snapshot, terrain, mesh, constraints, edgeLength, modifier, build, toleranceProfile),
            "local" => ApplyRemeshLocalRefine(mesh, constraints, edgeLength, modifier.CreaseAngle, toleranceProfile.RemeshConstraintTolerance, build),
            // "isotropic" runs TiledIsotropicRemesher: the same operators over world-anchored tiles, so the
            // output depends only on local input and an incremental rebuild can match a cold one exactly
            // (docs/incremental-rebuild-design-2026-09-29.md, D2). Not offered on the card, "global" keeps the
            // whole-mesh remesher for comparison.
            "global" => ApplyRemeshIsotropic(mesh, constraints, localConstraints, edgeLength, modifier, build, mode, toleranceProfile, shouldCancel, tiled: false),
            _ => ApplyRemeshIsotropic(mesh, constraints, localConstraints, edgeLength, modifier, build, mode, toleranceProfile, shouldCancel, tiled: true, runtimeCache, stageKey),
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
        IReadOnlyList<ConstraintPolyline> constraints,
        IReadOnlyList<ConstraintPolyline> localConstraints,
        double edgeLength,
        RemeshModifierDefinition modifier,
        TerrainBuildResult build,
        TerrainBuildMode mode,
        TerrainTolerancePolicy.Profile toleranceProfile,
        Func<bool>? shouldCancel,
        bool tiled = false,
        TerrainRuntimeCache? runtimeCache = null,
        string? stageKey = null)
    {
        if (!RhinoGeometryConversions.TryExtractMeshData(
                mesh, out var vertices, out int vertexCount, out var faces, out int faceCount, out var errorMessage))
        {
            build.Diagnostics.Add(errorMessage ?? "Could not extract mesh data for remesh.");
            return RhinoGeometryConversions.DuplicateWithCachedData(mesh);
        }

        // The isotropic remesher only PINS constraints that already run along mesh edges; it never inserts
        // one. Upstream constraints are embedded by the stage that made them, but this card's own
        // Constraints input is not, so the pipeline inserts them first (draped: Z comes from the terrain).
        // Tiles whose input is unchanged since the last run come from the memo, exactly as a cold run would
        // make them; only the tiles an edit touched are remeshed.
        TiledIsotropicRemesher.TiledRemeshMemo? previousMemo = null;
        if (tiled && runtimeCache != null && stageKey != null)
            runtimeCache.RemeshMemos.TryGetValue(stageKey, out previousMemo);
        IsotropicRemeshPipeline.Outcome outcome = IsotropicRemeshPipeline.Run(new IsotropicRemeshPipeline.Request
        {
            Terrain = new IndexedTriMesh(vertices, vertexCount, faces, faceCount),
            InsertedConstraints = localConstraints,
            PinnedConstraints = constraints,
            EdgeLength = edgeLength,
            AutoTargetScale = mode == TerrainBuildMode.Preview && modifier.EdgeLength <= 0 ? 2.0 : 1.0,
            CreaseAngleDeg = modifier.CreaseAngle,
            Tolerance = toleranceProfile.RemeshConstraintTolerance,
            WallFaceMinSlopeDeg = TerrainBuildService.RemeshWallFaceMinSlopeDeg,
            Iterations = mode == TerrainBuildMode.Preview || modifier.EdgeLength <= 0 ? 3 : 5,
            Tiled = tiled,
            PreviousMemo = previousMemo,

            // A rebuild the user has already superseded should stop inside the remesh, not after
            // it: this is the longest-running Core stage in the pipeline.
            ShouldCancel = shouldCancel
        });
        if (tiled && outcome.Memo != null && runtimeCache != null && stageKey != null)
            runtimeCache.RemeshMemos[stageKey] = outcome.Memo;

        if (outcome.InsertError != null)
        {
            build.Diagnostics.Add($"Remesh skipped because its breaklines could not be inserted: {outcome.InsertError}");
            return RhinoGeometryConversions.DuplicateWithCachedData(mesh);
        }

        if (outcome.Remesh == null)
        {
            build.Diagnostics.Add("Remesh skipped: could not derive a target edge length.");
            return RhinoGeometryConversions.DuplicateWithCachedData(mesh);
        }

        IsotropicRemesher.Result result = outcome.Remesh;
        if (!outcome.Success)
        {
            build.Diagnostics.Add((result.Warning ?? "Remesh kept the incoming mesh unchanged.") + $" [{result.Timing}]");
            return RhinoGeometryConversions.DuplicateWithCachedData(mesh);
        }

        build.Diagnostics.Add(
            $"Remesh {(tiled ? "tiled" : "isotropic")}: {result.Splits:N0} splits, {result.Collapses:N0} collapses, " +
            $"{result.Flips:N0} flips at target {outcome.Target:0.###} " +
            $"({result.Faces.Length / 3:N0} faces; features and walls pinned) [{result.Timing}].");

        if (outcome.CollapsedEdges + outcome.SeparatedVertices > 0)
        {
            build.Diagnostics.Add(
                $"Remesh collapsed {outcome.CollapsedEdges:N0} edge(s) and separated {outcome.SeparatedVertices:N0} vertex(es) shorter than float precision, " +
                "which the mesh hand-off would otherwise have welded into folds.");
        }

        (double[] outVertices, int[] outFaces) = (outcome.Vertices, outcome.Faces);
        return TerrainBuildService.BuildMeshFromArrays(outVertices, outFaces);
    }

    /// <summary>
    /// Classic full re-triangulation from scratch (<see cref="SurfaceRemesher"/> via the shared
    /// <see cref="TerrainBuildService.RebuildMeshWithConstraints"/> helper, also used by Retaining Wall rebuilds). Every
    /// constraint — including wall rails — becomes a hard edge of the constrained-Delaunay triangulation, so
    /// it structurally cannot connect across a wall. Coarser triangle shapes than Isotropic.
    /// </summary>
    private static RhinoMesh ApplyRemeshRebuild(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        IReadOnlyList<ConstraintPolyline> constraints,
        double edgeLength,
        RemeshModifierDefinition modifier,
        TerrainBuildResult build,
        TerrainTolerancePolicy.Profile toleranceProfile)
    {
        RhinoMesh remeshed = TerrainBuildService.RebuildMeshWithConstraints(
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
        IReadOnlyList<ConstraintPolyline> constraints,
        double requestedEdgeLength,
        double creaseAngleDeg,
        double tolerance,
        TerrainBuildResult build)
    {
        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out _, out var faces, out _, out var errorMessage))
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

        return TerrainBuildService.BuildMeshFromArrays(result.Vertices, result.Faces);
    }
}
