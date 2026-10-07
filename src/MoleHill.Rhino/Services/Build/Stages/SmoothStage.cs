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
