using System.Diagnostics;
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
/// Smooth modifier build stage: Laplacian smoothing within boundaries, or (Round Creases mode) rounding the
/// terrain's creases with <see cref="CreaseRounder"/>. Selected Grade Path roads are held as protect curves in both.
/// </summary>
internal static class SmoothStage
{
    internal static void Run(ModifierBuildContext c)
    {
        var smooth = (SmoothModifierDefinition)c.Modifier;
        c.UsedStageKeys.Add(TerrainStageKey.CreateSmoothPrepared(c.StageKey));
        RhinoMesh? input = c.CurrentMesh;
        c.CurrentMesh = TerrainBuildService.ExecuteCachedMeshStage(
            c.Build,
            c.RuntimeCache,
            c.StageKey,
            "Smooth",
            ComputeSmoothStageFingerprint(c.Snapshot, c.Terrain, smooth, c.Index, c.CurrentMeshFingerprint),
            () => input == null ? TerrainBuildService.WarnMissingMesh(c.Build, smooth.Label) : ApplySmooth(c.Snapshot, c.Terrain, input, smooth, c.Build, c.RuntimeCache, c.Index, c.StageKey, c.Mode),
            result => TerrainBuildService.DescribeModifierMeshResult(smooth.Label, result),
            out ulong fingerprint,
            c.ShouldCancel);
        c.CurrentMeshFingerprint = fingerprint;
    }

    private static ulong ComputeSmoothStageFingerprint(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        SmoothModifierDefinition modifier,
        int modifierIndex,
        ulong upstreamFingerprint)
    {
        var builder = new FingerprintBuilder();
        builder.Add(TerrainBuildService.ComputeModifierStageFingerprint(snapshot, terrain, modifier, upstreamFingerprint));
        builder.Add(ComputeSelectedGradePathRoadBreaklinesFingerprint(snapshot, terrain, modifier, modifierIndex));
        return builder.ToUInt64();
    }

    private static ulong ComputeSmoothPreparedFingerprint(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        SmoothModifierDefinition modifier,
        int modifierIndex,
        ulong upstreamFingerprint)
    {
        var builder = new FingerprintBuilder();
        builder.Add("SmoothPrepared");
        builder.Add(upstreamFingerprint);
        // The two modes prepare different data under one key; the crease angle is a Round input only.
        bool round = SmoothModes.IsRound(modifier);
        builder.Add(round ? SmoothModes.Round : SmoothModes.Smooth);
        if (round)
            builder.Add(modifier.CreaseAngle);
        builder.Add(snapshot.ModelAbsoluteTolerance);
        builder.Add(terrain.GlobalTolerance);
        builder.Add(TerrainBuildService.ComputeSourceSetFingerprint(snapshot, modifier.Boundaries));
        builder.Add(TerrainBuildService.ComputeSourceSetFingerprint(snapshot, modifier.Breaklines));
        builder.Add(ComputeSelectedGradePathRoadBreaklinesFingerprint(snapshot, terrain, modifier, modifierIndex));
        return builder.ToUInt64();
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
        var phaseTimer = Stopwatch.StartNew();
        var phases = new System.Text.StringBuilder();
        void Phase(string name)
        {
            phases.Append(phases.Length > 0 ? ", " : string.Empty).Append(name).Append(' ').Append(phaseTimer.ElapsedMilliseconds).Append(" ms");
            phaseTimer.Restart();
        }

        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out int vertexCount, out var faces, out int faceCount, out var errorMessage))
        {
            build.Diagnostics.Add(errorMessage ?? "Could not extract mesh data for smoothing.");
            return mesh;
        }

        Phase("extract");

        double tolerance = TerrainBuildService.GetToleranceProfile(snapshot, terrain).CurveChordTolerance;
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

        Phase("inputs");
        string preparedStageKey = TerrainStageKey.CreateSmoothPrepared(stageKey);
        ulong preparedFingerprint = ComputeSmoothPreparedFingerprint(
            snapshot,
            terrain,
            modifier,
            modifierIndex,
            TerrainBuildService.ComputeMeshFingerprint(mesh));
        Phase("fingerprint");
        runtimeCache.SmoothEntries.TryGetValue(preparedStageKey, out var cachedPrepared);
        if (cachedPrepared != null && cachedPrepared.Fingerprint != preparedFingerprint)
            cachedPrepared = null;

        if (SmoothModes.IsRound(modifier))
        {
            return ApplyRound(
                mesh,
                new IndexedTriMesh(vertices, vertexCount, faces, faceCount),
                boundaries,
                breaklines,
                modifier,
                tolerance,
                build,
                runtimeCache,
                preparedStageKey,
                preparedFingerprint,
                cachedPrepared?.Rounding,
                mode,
                Phase,
                phases);
        }

        MeshSmoother.PreparedSmoothingData prepared;
        if (cachedPrepared?.Prepared != null)
        {
            // Prepared smoothing data is immutable after construction. Share it between the UI cache
            // and worker builds instead of copying the full adjacency/mask arrays on every scrub.
            prepared = cachedPrepared.Prepared;
        }
        else
        {
            prepared = MeshSmoother.Prepare(
                new IndexedTriMesh(vertices, vertexCount, faces, faceCount),
                boundaries.ToArray(),
                breaklines.ToArray(),
                tolerance);
            runtimeCache.SmoothEntries[preparedStageKey] = new SmoothStageCacheEntry
            {
                Fingerprint = preparedFingerprint,
                Prepared = prepared
            };
        }

        Phase("prepare");
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

        Phase("smooth");
        if (smoothed.Any(value => double.IsNaN(value) || double.IsInfinity(value)))
        {
            build.Diagnostics.Add("Smooth produced invalid mesh data. Incoming mesh kept.");
            return mesh;
        }

        Phase("finite check");
        // Smoothing moves heights only, so the incoming topology is reused rather than normalized again.
        var smoothedMesh = RhinoGeometryConversions.BuildMeshWithNewHeights(mesh, smoothed, vertexCount);
        Phase("mesh build");
        if (smoothedMesh.Faces.Count == 0 || smoothedMesh.Vertices.Count == 0 || !smoothedMesh.IsValid)
        {
            build.Diagnostics.Add("Smooth produced an invalid mesh. Incoming mesh kept.");
            return mesh;
        }

        Phase("validity");
        build.Diagnostics.Add($"Smooth phases: {phases}.");
        return smoothedMesh;
    }

    /// <summary>Densifying may add this many vertices (or twice the incoming count, if more) before it is refused.</summary>
    private const int MaxDensifyVertices = 500_000;

    private static RhinoMesh ApplyRound(
        RhinoMesh mesh,
        IndexedTriMesh indexed,
        List<(double[] xyVerts, int vertCount)> boundaries,
        List<MeshSmoother.BreaklinePolyline> protect,
        SmoothModifierDefinition modifier,
        double tolerance,
        TerrainBuildResult build,
        TerrainRuntimeCache runtimeCache,
        string preparedStageKey,
        ulong preparedFingerprint,
        CreaseRounder.PreparedCreaseRounding? cached,
        TerrainBuildMode mode,
        Action<string> phase,
        System.Text.StringBuilder phases)
    {
        CreaseRounder.PreparedCreaseRounding prepared = cached ?? CreaseRounder.Prepare(
            indexed,
            boundaries.ToArray(),
            protect.ToArray(),
            modifier.CreaseAngle,
            tolerance);
        if (cached == null)
        {
            // Immutable once built, like Smooth's prepared data. It holds the incoming mesh's creases, which
            // do not depend on the length; densifying the band does, and runs per build.
            runtimeCache.SmoothEntries[preparedStageKey] = new SmoothStageCacheEntry
            {
                Fingerprint = preparedFingerprint,
                Rounding = prepared
            };
        }

        phase("prepare");

        // A crease with nothing beside it to round into would only move its own vertices, and a wide face
        // (a pad top meshed from its outline alone) would tilt whole. Split the band first; the split keeps
        // the surface, so the rounding still starts from the incoming terrain. Splitting costs a whole-mesh
        // refine and a fresh prepare, too slow to repeat per scrub tick, so a preview rounds at the mesh's
        // own resolution and the final build that follows densifies.
        int addedVertices = 0;
        bool overBudget = false;
        if (mode != TerrainBuildMode.Preview &&
            CreaseRounder.DensifyBand(
                indexed,
                prepared,
                modifier.RoundingLength,
                tolerance,
                Math.Max(MaxDensifyVertices, indexed.VertexCount * 2),
                out overBudget) is { } dense)
        {
            addedVertices = dense.AddedVertices;
            indexed = dense.Mesh;
            prepared = CreaseRounder.Prepare(indexed, boundaries.ToArray(), protect.ToArray(), modifier.CreaseAngle, tolerance);
            phase("densify");
        }

        CreaseRounder.CreaseRoundingResult rounded = CreaseRounder.Round(indexed.Vertices, prepared, modifier.RoundingLength);
        phase("round");
        if (overBudget)
        {
            build.Diagnostics.Add(
                $"{modifier.Label}: the Rounding Length is too short to split the triangles beside {rounded.RoundedCreaseLength:0.##} of crease, " +
                "so they were rounded at the mesh's own resolution. Lengthen it, or Remesh first.");
        }

        if (rounded.MovedVertexCount == 0)
        {
            build.Diagnostics.Add(
                prepared.CreaseEdgeCount == 0 ? $"{modifier.Label}: no crease folds {modifier.CreaseAngle:G4} deg or more; terrain unchanged." :
                rounded.RoundedCreaseLength <= 0.0 ? $"{modifier.Label}: no crease is as long as the Rounding Length; terrain unchanged." :
                $"{modifier.Label}: every point the creases reach is held (by Protect, a wall, the border or the boundaries); terrain unchanged.");
            return mesh;
        }

        if (rounded.Vertices.Any(value => double.IsNaN(value) || double.IsInfinity(value)))
        {
            build.Diagnostics.Add("Round Creases produced invalid mesh data. Incoming mesh kept.");
            return mesh;
        }

        // Without densifying, rounding moves heights only and the incoming topology is reused.
        var roundedMesh = addedVertices == 0
            ? RhinoGeometryConversions.BuildMeshWithNewHeights(mesh, rounded.Vertices, indexed.VertexCount)
            : TerrainBuildService.BuildMeshFromArrays(rounded.Vertices, indexed.Faces);
        if (roundedMesh.Faces.Count == 0 || roundedMesh.Vertices.Count == 0 || !roundedMesh.IsValid)
        {
            build.Diagnostics.Add("Round Creases produced an invalid mesh. Incoming mesh kept.");
            return mesh;
        }

        phase("mesh build");
        build.Diagnostics.Add(
            $"{modifier.Label}: rounded {rounded.RoundedCreaseLength:0.##} of crease; largest height change {rounded.MaxOffset:0.###}" +
            (addedVertices > 0 ? $"; split the band beside it ({addedVertices:N0} vertices added)." : "."));
        if (rounded.CoarseCreaseLength > 0.0)
        {
            build.Diagnostics.Add(
                $"{modifier.Label}: along {rounded.CoarseCreaseLength:0.##} of crease the triangles are still wider than half the Rounding Length, " +
                "so the rounding there is a chamfer.");
        }

        build.Diagnostics.Add($"Smooth phases: {phases}.");
        return roundedMesh;
    }

    private static void AddSelectedGradePathRoadBreaklines(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        SmoothModifierDefinition smooth,
        int smoothModifierIndex,
        double curveTolerance,
        List<MeshSmoother.BreaklinePolyline> breaklines)
    {
        HashSet<Guid> selectedSourceIds = TerrainBuildService.ResolveSourceObjectIds(snapshot, smooth.Breaklines);
        if (selectedSourceIds.Count == 0)
            return;

        var selectedPaths = new List<PathGrader.PathDefinition>();
        foreach (GradePathModifierDefinition gradePath in TerrainBuildService.EnumeratePriorEnabledGradePathModifiers(terrain, smoothModifierIndex))
        {
            (ResolvedGradePathDefinition[] resolvedPaths, _) = TerrainBuildService.ResolveGradePathDefinitions(
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
        HashSet<Guid> selectedSourceIds = TerrainBuildService.ResolveSourceObjectIds(snapshot, smooth.Breaklines);
        if (selectedSourceIds.Count == 0)
            return 0UL;

        var builder = new FingerprintBuilder();
        builder.Add("SmoothSelectedGradePathRoadBreaklinesV1");
        int matchedModifierCount = 0;
        foreach ((int _, GradePathModifierDefinition gradePath) in TerrainBuildService.EnumeratePriorEnabledGradePathModifiersWithIndex(terrain, smoothModifierIndex))
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
            builder.Add(TerrainBuildService.ComputeSourceSetFingerprint(snapshot, gradePath.Paths));
            builder.Add(TerrainBuildService.ComputeSourceSetFingerprint(snapshot, gradePath.WidthEdges));
            builder.Add(matchingIds.Length);
            foreach (Guid objectId in matchingIds)
                builder.Add(objectId);
        }

        return matchedModifierCount == 0 ? 0UL : builder.ToUInt64();
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
