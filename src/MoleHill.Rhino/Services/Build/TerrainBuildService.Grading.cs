using System.Diagnostics;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using MoleHill.Rhino.Model;
using MoleHill.Shared;
using Rhino.Geometry;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

// Grading stages: Grade Pad, Grade Path, and In-Situ Stair — input resolution, constraints, bounds, and patch summaries.
internal sealed partial class TerrainBuildService
{
    private static RhinoMesh? BuildGradePadMesh(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        GradePadModifierDefinition modifier,
        TerrainBuildResult build,
        TerrainRuntimeCache runtimeCache,
        int modifierIndex,
        string stageKey,
        RhinoMesh? mesh,
        ulong upstreamFingerprint,
        TerrainBuildMode mode,
        out ulong outputFingerprint,
        Func<bool>? shouldCancel = null)
    {
        const string stageName = "Grade Pad";
        string topologyStageKey = TerrainStageKey.CreateGradingTopology(stageKey, "Pad");
        var timer = Stopwatch.StartNew();
        ulong preResolutionFingerprint = ComputeModifierStageFingerprint(snapshot, terrain, modifier, upstreamFingerprint);

        if (runtimeCache.StageEntries.TryGetValue(stageKey, out var cachedEntry) &&
            cachedEntry.PreResolutionFingerprint == preResolutionFingerprint)
        {
            RhinoMesh? cachedMesh = RestoreCachedMeshStage(build, cachedEntry, out outputFingerprint);
            timer.Stop();
            build.RecordTiming(stageName, timer.Elapsed, AppendCacheHitDetail(DescribeModifierMeshResult(modifier.Label, cachedMesh)));
            return cachedMesh;
        }

        var inputsTimer = Stopwatch.StartNew();
        int diagnosticsStart = build.Diagnostics.Count;
        int structuredDiagnosticsStart = build.StructuredDiagnostics.Count;
        ThrowIfCancellationRequested(shouldCancel);
        if (mesh == null)
        {
            WarnMissingMesh(build, modifier.Label);
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
                out outputFingerprint);
        }

        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out int vertexCount, out var faces, out int faceCount, out var errorMessage))
        {
            build.Diagnostics.Add(errorMessage ?? "Could not extract mesh data for grade pad.");
            return StoreMeshStageCache(
                build,
                runtimeCache,
                stageKey,
                stageName,
                preResolutionFingerprint,
                preResolutionFingerprint,
                mesh,
                build.PersistentHardConstraints,
                Array.Empty<GeneratedRhinoObject>(),
                build.Diagnostics.Skip(diagnosticsStart),
                DescribeModifierMeshResult(modifier.Label, mesh),
                timer,
                out outputFingerprint,
                structuredDiagnostics: build.StructuredDiagnostics.Skip(structuredDiagnosticsStart));
        }

        TerrainTolerancePolicy.Profile toleranceProfile = GetToleranceProfile(snapshot, terrain);
        double curveTolerance = toleranceProfile.CurveChordTolerance;
        double gradePadTolerance = toleranceProfile.GradePadTolerance;
        var resolveTimer = Stopwatch.StartNew();
        ResolvedGradePadInputs resolvedInputs = ResolveGradePadInputs(
            snapshot,
            vertices,
            vertexCount,
            faces,
            faceCount,
            modifier,
            curveTolerance,
            gradePadTolerance);
        resolveTimer.Stop();
        build.RecordTiming(
            "Grade Pad Resolve",
            resolveTimer.Elapsed,
            $"{resolvedInputs.Pads.Length:N0} pads; constraints {resolvedInputs.ConstraintElapsed.TotalSeconds:0.##} s of it",
            StageTimingDiagnosticThresholdMs);
        build.RecordTiming(
            "Grade Pad Constraints",
            resolvedInputs.ConstraintElapsed,
            $"{resolvedInputs.Constraints.Length:N0} constraints over {vertexCount:N0} verts",
            StageTimingDiagnosticThresholdMs);
        build.Diagnostics.AddRange(resolvedInputs.Diagnostics);
        build.StructuredDiagnostics.AddRange(resolvedInputs.StructuredDiagnostics);
        ThrowIfCancellationRequested(shouldCancel);
        if (resolvedInputs.Pads.Length == 0)
        {
            build.Diagnostics.Add("Grade Pad has no valid closed boundaries.");
            return StoreMeshStageCache(
                build,
                runtimeCache,
                stageKey,
                stageName,
                preResolutionFingerprint,
                preResolutionFingerprint,
                mesh,
                build.PersistentHardConstraints,
                Array.Empty<GeneratedRhinoObject>(),
                build.Diagnostics.Skip(diagnosticsStart),
                DescribeModifierMeshResult(modifier.Label, mesh),
                timer,
                out outputFingerprint,
                structuredDiagnostics: build.StructuredDiagnostics.Skip(structuredDiagnosticsStart));
        }

        var locksTimer = Stopwatch.StartNew();
        var effectiveLocks = CombinePadLockCurves(
            resolvedInputs.Locks,
            UpstreamBreaklines(build, modifier.GradeThroughBreaklines),
            resolvedInputs.Pads,
            vertices,
            vertexCount,
            toleranceProfile.DetailSize,
            out int skippedPersistentLockCount);
        if (skippedPersistentLockCount > 0)
        {
            build.Diagnostics.Add(
                $"Grade Pad ignored {skippedPersistentLockCount:N0} persistent hard constraint(s) outside the pad influence envelope when building pad lock barriers.");
        }
        locksTimer.Stop();
        build.RecordTiming("Grade Pad Locks", locksTimer.Elapsed, $"{effectiveLocks.Length:N0} locks", StageTimingDiagnosticThresholdMs);

        var dirtyTimer = Stopwatch.StartNew();
        List<GradingPatch> patchSummaries = BuildPadPatchSummaries(resolvedInputs.Pads);
        List<string> dirtyStageKeys = runtimeCache.FindIntersectingGradingStageKeys(
            TerrainRuntimeCache.GetStagePrefix(mode),
            terrain.Modifiers,
            modifierIndex,
            patchSummaries,
            topologyStageKey);
        if (dirtyStageKeys.Count > 0)
        {
            runtimeCache.InvalidateStages(dirtyStageKeys);
            build.Diagnostics.Add(
                $"Grade Pad invalidated {dirtyStageKeys.Count} overlapping downstream grading stage(s): {string.Join(", ", dirtyStageKeys.Select(TerrainStageKey.GetBase))}.");
        }

        ulong topologyFingerprint = ComputeGradePadTopologyFingerprint(
            upstreamFingerprint,
            gradePadTolerance,
            modifier,
            resolvedInputs.Pads,
            effectiveLocks);
        dirtyTimer.Stop();
        build.RecordTiming("Grade Pad Dirty Scan + Fingerprint", dirtyTimer.Elapsed, null, StageTimingDiagnosticThresholdMs);

        // Everything above is stage scaffolding, not grading: mesh extraction, curve resolution, lock
        // combination, overlap scanning and four fingerprints. It is timed separately because the
        // stage total was measurably larger than PadGrader's own cost and nothing said where the rest
        // went - see docs/architecture.md, "Rhino: edit-to-visible latency".
        inputsTimer.Stop();
        build.RecordTiming(
            "Grade Pad Inputs",
            inputsTimer.Elapsed,
            $"{vertexCount:N0} verts, {resolvedInputs.Pads.Length:N0} pads, {effectiveLocks.Length:N0} locks",
            StageTimingDiagnosticThresholdMs);

        GradingTopologyCacheEntry? topologyEntry;
        var topologyTimer = Stopwatch.StartNew();
        if (runtimeCache.GradingTopologyEntries.TryGetValue(topologyStageKey, out var cachedTopologyEntry) &&
            string.Equals(cachedTopologyEntry.GraderKind, "Pad", StringComparison.Ordinal) &&
            cachedTopologyEntry.Fingerprint == topologyFingerprint)
        {
            // Shared, not copied. A GradingTopologyCacheEntry is immutable after construction and every
            // consumer below only reads it - PadGrader.ApplyGradingZ writes into its own fresh array and
            // BuildMesh reads. Copying it here duplicated the whole retained grading topology (3 doubles
            // per vertex plus the face array) on every hot-cache Grade Pad build. CreateWorkerCopy and
            // ReplaceBuildCachesFrom already pass these entries by reference.
            topologyEntry = cachedTopologyEntry;
            build.Diagnostics.AddRange(topologyEntry.Diagnostics);
            build.StructuredDiagnostics.AddRange(topologyEntry.StructuredDiagnostics);
            // The pad boundary a fresh grade publishes as a hard constraint. Skipping it here made
            // downstream grading depend on whether this topology happened to be cached.
            build.PersistentHardConstraints.AddRange(
                TerrainRuntimeCacheCloner.CloneConstraints(topologyEntry.OutputConstraints));
            topologyTimer.Stop();
            build.RecordTiming(
                "Grade Pad Topology",
                topologyTimer.Elapsed,
                AppendCacheHitDetail($"{topologyEntry.VertexCount:N0} verts, {topologyEntry.FaceCount:N0} faces"),
                StageTimingDiagnosticThresholdMs);
        }
        else
        {
            ThrowIfCancellationRequested(shouldCancel);
            var topologyDiagnostics = new List<string>();
            var gradeResult = GradePadsWindowed(
                vertices,
                vertexCount,
                faces,
                faceCount,
                resolvedInputs.Pads,
                effectiveLocks,
                UpstreamBreaklines(build, modifier.GradeThroughBreaklines),
                gradePadTolerance,
                toleranceProfile.DetailSize,
                runtimeCache,
                stageKey,
                topologyDiagnostics,
                out var gradeWarning,
                out IReadOnlyList<MoleHill.Core.Grading.OutputPolyline> failureOutputPolylines,
                out IReadOnlyList<GradingDiagnostic> failureStructuredDiagnostics);
            ThrowIfCancellationRequested(shouldCancel);
            runtimeCache.CoreCaseRecorder?.RecordPad(
                modifier.Label,
                vertices,
                vertexCount,
                faces,
                faceCount,
                resolvedInputs.Pads,
                effectiveLocks.Length > 0 ? effectiveLocks : null,
                gradeResult != null,
                gradeResult?.VertexCount,
                gradeResult?.FaceCount,
                gradeWarning);

            if (!string.IsNullOrWhiteSpace(gradeWarning))
                topologyDiagnostics.Add(gradeWarning!);

            double[] topologyVertices;
            int topologyVertexCount;
            int[] topologyFaces;
            int topologyFaceCount;
            bool gradePadTopologyFailed = gradeResult == null;
            List<ConstraintPolyline> outputConstraints = CreateOutputPolylineConstraints(
                gradeResult?.OutputPolylines ?? failureOutputPolylines);
            build.PersistentHardConstraints.AddRange(TerrainRuntimeCacheCloner.CloneConstraints(outputConstraints));
            if (gradeResult == null)
            {
                if (string.IsNullOrWhiteSpace(gradeWarning))
                    topologyDiagnostics.Add("Grade Pad protected patch failed; incoming mesh retained.");
                build.Diagnostics.AddRange(topologyDiagnostics);
                build.StructuredDiagnostics.AddRange(failureStructuredDiagnostics);
                topologyVertices = (double[])vertices.Clone();
                topologyVertexCount = vertexCount;
                topologyFaces = (int[])faces.Clone();
                topologyFaceCount = faceCount;
            }
            else
            {
                if (gradeResult.Diagnostics.Count > 0)
                    topologyDiagnostics.AddRange(gradeResult.Diagnostics);

                build.Diagnostics.AddRange(topologyDiagnostics);
                build.StructuredDiagnostics.AddRange(gradeResult.StructuredDiagnostics);
                topologyVertices = gradeResult.Vertices;
                topologyVertexCount = gradeResult.VertexCount;
                topologyFaces = gradeResult.Faces;
                topologyFaceCount = gradeResult.FaceCount;
            }

            topologyTimer.Stop();

            topologyEntry = new GradingTopologyCacheEntry
            {
                GraderKind = "Pad",
                Fingerprint = topologyFingerprint,
                OutputFingerprint = ComputeGradingTopologyOutputFingerprint("Pad", topologyVertices, topologyVertexCount, topologyFaces, topologyFaceCount),
                Vertices = topologyVertices,
                VertexCount = topologyVertexCount,
                Faces = topologyFaces,
                FaceCount = topologyFaceCount,
                PatchSummaries = gradeResult?.PatchSummaries.Count > 0
                    ? ClonePatchSummaries(gradeResult.PatchSummaries)
                    : patchSummaries,
                OutputConstraints = outputConstraints,
                Diagnostics = topologyDiagnostics,
                StructuredDiagnostics = gradeResult?.StructuredDiagnostics.ToList() ?? failureStructuredDiagnostics.ToList()
            };
            // The entry was just built from arrays this scope owns (PadGrader's output, or a fresh copy
            // of the upstream on the failure branch) and nothing mutates them afterwards, so the cache
            // takes it as it is rather than duplicating the whole topology a second time.
            runtimeCache.GradingTopologyEntries[topologyStageKey] = topologyEntry;
            // Named apart from the stage row: both used to read "Grade Pad", so the stage total and the
            // grader's own time were indistinguishable in a timing report and summed in the perf lane.
            build.RecordTiming(
                "Grade Pad Topology",
                topologyTimer.Elapsed,
                gradePadTopologyFailed
                    ? $"failed; upstream {topologyVertexCount:N0} verts, {topologyFaceCount:N0} faces retained"
                    : $"{topologyVertexCount:N0} verts, {topologyFaceCount:N0} faces",
                StageTimingDiagnosticThresholdMs);
        }

        bool gradePadStageFailed = topologyEntry.Diagnostics.Any(
            static diagnostic => diagnostic.Contains("Grade Pad protected patch failed", StringComparison.OrdinalIgnoreCase));
        build.Diagnostics.Add(gradePadStageFailed
            ? $"Grade Pad protected patch failed; incoming mesh retained ({DescribeTopologyCounts(vertexCount, faceCount, topologyEntry.VertexCount, topologyEntry.FaceCount)})."
            : $"Grade Pad local patch ({DescribeTopologyCounts(vertexCount, faceCount, topologyEntry.VertexCount, topologyEntry.FaceCount)}).");

        ulong resolvedInputFingerprint = ComputeGradePadResolvedInputFingerprint(topologyEntry.OutputFingerprint, resolvedInputs.Pads, modifier);
        if (cachedEntry != null && cachedEntry.ResolvedInputFingerprint == resolvedInputFingerprint)
        {
            StageCacheEntry refreshedEntry = CloneStageCacheEntry(cachedEntry, preResolutionFingerprint, resolvedInputFingerprint);
            runtimeCache.StageEntries[stageKey] = refreshedEntry;

            RhinoMesh? resolvedCachedMesh = RestoreCachedMeshStage(build, refreshedEntry, out outputFingerprint);
            timer.Stop();
            build.RecordTiming(stageName, timer.Elapsed, AppendCacheHitDetail(DescribeModifierMeshResult(modifier.Label, resolvedCachedMesh)));
            return resolvedCachedMesh;
        }

        var filterTimer = Stopwatch.StartNew();
        // Preview uses raw topology + Z-only grading. Full mode topology already has graded vertices
        // from PadGrader.Grade, so no second pass is needed.
        double[] gradedVertices = mode == TerrainBuildMode.Preview
            ? PadGrader.ApplyGradingZ(
                topologyEntry.Vertices,
                topologyEntry.VertexCount,
                topologyEntry.Faces,
                topologyEntry.FaceCount,
                resolvedInputs.Pads,
                effectiveLocks.Length > 0 ? effectiveLocks : null)
            : topologyEntry.Vertices;
        filterTimer.Stop();
        ThrowIfCancellationRequested(shouldCancel);
        build.RecordTiming(
            "Grade Pad Filter",
            filterTimer.Elapsed,
            $"{topologyEntry.VertexCount:N0} verts",
            StageTimingDiagnosticThresholdMs);

        var outputMeshTimer = Stopwatch.StartNew();
        RhinoMesh resultMesh = FinalizeGradingMesh(
            RhinoGeometryConversions.BuildMesh(gradedVertices, topologyEntry.VertexCount, topologyEntry.Faces, topologyEntry.FaceCount),
            "Grade Pad",
            build);
        outputMeshTimer.Stop();
        build.RecordTiming(
            "Grade Pad Output Mesh",
            outputMeshTimer.Elapsed,
            $"{topologyEntry.VertexCount:N0} verts, {topologyEntry.FaceCount:N0} faces",
            StageTimingDiagnosticThresholdMs);
        if (modifier.GradeThroughBreaklines && !gradePadStageFailed)
        {
            DropRegradedBreaklines(
                build, gradedVertices, topologyEntry.VertexCount, topologyEntry.Faces, topologyEntry.FaceCount,
                snapshot.ModelAbsoluteTolerance, snapshot.ModelUnitSystem, "Grade Pad");
        }
        // PadGrader's constraint set contains temporary XY construction loops for the pad, shoulder,
        // and stitch apron. Their Z values are placeholders and some loops are deliberately softened
        // or replaced while assembling the final patch, so they are not durable elevation constraints
        // on resultMesh. The actual graded pad boundary is already published from OutputPolylines as a
        // persistent hard constraint above. Persisting the construction set made downstream consumers
        // reject the Grade Pad mesh as conflicting with metadata produced by the same stage.

        return StoreMeshStageCache(
            build,
            runtimeCache,
            stageKey,
            stageName,
            preResolutionFingerprint,
            resolvedInputFingerprint,
            resultMesh,
            build.PersistentHardConstraints,
            Array.Empty<GeneratedRhinoObject>(),
            build.Diagnostics.Skip(diagnosticsStart),
            DescribeModifierMeshResult(modifier.Label, resultMesh),
            timer,
            out outputFingerprint,
            structuredDiagnostics: build.StructuredDiagnostics.Skip(structuredDiagnosticsStart));
    }

    private static ResolvedGradePadInputs ResolveGradePadInputs(
        TerrainBuildSnapshot snapshot,
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        GradePadModifierDefinition modifier,
        double curveTolerance,
        double gradePadTolerance)
    {
        var pads = new List<PadGrader.PadBoundary>();
        var diagnostics = new List<string>();
        foreach (var curve in TerrainBuildSnapshotResolver.ResolveCurves(snapshot, modifier.Boundaries))
        {
            if (!RhinoSourceResolver.TryGetPolyline(curve, curveTolerance, requireClosed: true, out var polyline))
                continue;

            int count = polyline.Count;
            if (count > 1 && polyline[0].DistanceTo(polyline[^1]) < curveTolerance)
                count--;
            if (count < 3)
                continue;

            double stitchApronDistance = ModelUnits.FromMeters(0.5, snapshot.ModelUnitSystem);
            double padCutSlope = modifier.CutSlopeAngle > 0.0 ? modifier.CutSlopeAngle : modifier.SlopeAngle;
            if (!TryCreatePlanarPadBoundary(polyline, count, padCutSlope, modifier.MaxDistance, stitchApronDistance, modifier.SlopeAngle, out var pad, out string? diagnostic))
            {
                if (!string.IsNullOrWhiteSpace(diagnostic))
                    diagnostics.Add(diagnostic!);
                continue;
            }

            pads.Add(pad!);
        }

        PadGrader.PadBoundary[] padArray = pads.ToArray();
        PadGrader.LockCurve[] lockArray = Array.Empty<PadGrader.LockCurve>();
        var constraintTimer = Stopwatch.StartNew();
        PadGrader.ConstraintSet constraintSet = padArray.Length == 0
            ? new PadGrader.ConstraintSet
            {
                Constraints = Array.Empty<ConstraintPolyline>(),
                SuggestedEdgeLength = 0.0,
                Diagnostics = Array.Empty<string>(),
                StructuredDiagnostics = Array.Empty<GradingDiagnostic>()
            }
            : PadGrader.CreateConstraints(
                vertices,
                vertexCount,
                faces,
                faceCount,
                padArray,
                lockArray.Length == 0 ? null : lockArray,
                gradePadTolerance);
        constraintTimer.Stop();
        var allDiagnostics = new List<string>(diagnostics.Count + constraintSet.Diagnostics.Length);
        allDiagnostics.AddRange(diagnostics);
        allDiagnostics.AddRange(constraintSet.Diagnostics);

        return new ResolvedGradePadInputs
        {
            Pads = padArray,
            Locks = lockArray,
            Constraints = constraintSet.Constraints,
            SuggestedEdgeLength = constraintSet.SuggestedEdgeLength,
            Diagnostics = allDiagnostics.ToArray(),
            StructuredDiagnostics = constraintSet.StructuredDiagnostics,
            ConstraintElapsed = constraintTimer.Elapsed
        };
    }

    private static bool TryCreatePlanarPadBoundary(
        Polyline polyline,
        int vertexCount,
        double slopeAngle,
        double maxDistance,
        double stitchApronDistance,
        double fillSlopeAngle,
        out PadGrader.PadBoundary? pad,
        out string? diagnostic)
    {
        pad = null;
        diagnostic = null;

        var boundaryVertices = new double[vertexCount * 3];
        var points = new Point3d[vertexCount];
        for (int i = 0; i < vertexCount; i++)
        {
            Point3d point = polyline[i];
            points[i] = point;
            boundaryVertices[i * 3] = point.X;
            boundaryVertices[i * 3 + 1] = point.Y;
            boundaryVertices[i * 3 + 2] = point.Z;
        }

        if (!TryGetPadPlaneCoefficients(points, out double planeXCoeff, out double planeYCoeff, out double planeConstant))
        {
            diagnostic = "Grade Pad skipped a boundary because it did not define a stable planar pad.";
            return false;
        }

        pad = PadGrader.PadBoundary.CreatePlanar(
            boundaryVertices,
            vertexCount,
            planeXCoeff,
            planeYCoeff,
            planeConstant,
            slopeAngle,
            maxDistance,
            stitchApronDistance: stitchApronDistance,
            fillSlopeAngleDeg: fillSlopeAngle);
        return true;
    }

    private static bool TryGetPadPlaneCoefficients(
        IReadOnlyList<Point3d> points,
        out double planeXCoeff,
        out double planeYCoeff,
        out double planeConstant)
    {
        planeXCoeff = 0.0;
        planeYCoeff = 0.0;
        planeConstant = 0.0;

        if (points.Count < 3)
            return false;

        PlaneFitResult fit = Plane.FitPlaneToPoints(points, out Plane plane);
        if (fit == PlaneFitResult.Failure || Math.Abs(plane.Normal.Z) < MinRepresentablePadPlaneNormalZ)
            return false;

        if (plane.Normal.Z < 0)
            plane.Flip();

        planeXCoeff = -plane.Normal.X / plane.Normal.Z;
        planeYCoeff = -plane.Normal.Y / plane.Normal.Z;
        planeConstant = plane.Origin.Z + (plane.Normal.X * plane.Origin.X + plane.Normal.Y * plane.Origin.Y) / plane.Normal.Z;
        return true;
    }

    private static PadGrader.LockCurve[] CombinePadLockCurves(
        IReadOnlyList<PadGrader.LockCurve> localLocks,
        IReadOnlyList<ConstraintPolyline> persistentHardConstraints,
        IReadOnlyList<PadGrader.PadBoundary> pads,
        double[] terrainVertices,
        int terrainVertexCount,
        double terrainDetailSize,
        out int skippedPersistentLockCount)
    {
        skippedPersistentLockCount = 0;
        if (localLocks.Count == 0 && persistentHardConstraints.Count == 0)
            return Array.Empty<PadGrader.LockCurve>();

        var combined = new List<PadGrader.LockCurve>(localLocks.Count + persistentHardConstraints.Count);
        combined.AddRange(localLocks);
        Bounds2D[] padInfluenceBounds = BuildPadInfluenceBoundsForLocks(
            pads,
            terrainVertices,
            terrainVertexCount,
            terrainDetailSize);

        foreach (var constraint in persistentHardConstraints)
        {
            if (constraint.PointCount < 2 || constraint.IsClosed)
                continue;

            if (padInfluenceBounds.Length > 0 &&
                !ConstraintIntersectsAnyBounds(constraint, padInfluenceBounds))
            {
                skippedPersistentLockCount++;
                continue;
            }

            var xyVertices = new double[constraint.PointCount * 2];
            for (int i = 0; i < constraint.PointCount; i++)
            {
                xyVertices[i * 2] = constraint.Points[i * 3];
                xyVertices[i * 2 + 1] = constraint.Points[i * 3 + 1];
            }

            combined.Add(new PadGrader.LockCurve(xyVertices, constraint.PointCount));
        }

        return combined.ToArray();
    }

    private static Bounds2D[] BuildPadInfluenceBoundsForLocks(
        IReadOnlyList<PadGrader.PadBoundary> pads,
        double[] terrainVertices,
        int terrainVertexCount,
        double terrainDetailSize)
    {
        if (pads.Count == 0)
            return Array.Empty<Bounds2D>();

        ComputeTerrainBoundsAndZRange(
            terrainVertices,
            terrainVertexCount,
            out Bounds2D terrainBounds,
            out double terrainMinZ,
            out double terrainMaxZ);
        double terrainDiagonal = Math.Sqrt(
            ((terrainBounds.MaxX - terrainBounds.MinX) * (terrainBounds.MaxX - terrainBounds.MinX)) +
            ((terrainBounds.MaxY - terrainBounds.MinY) * (terrainBounds.MaxY - terrainBounds.MinY)));
        double basePadding = Math.Max(terrainDetailSize * 2.0, Math.Max(terrainDiagonal * 1e-12, double.Epsilon));

        var bounds = new Bounds2D[pads.Count];
        for (int i = 0; i < pads.Count; i++)
        {
            PadGrader.PadBoundary pad = pads[i];
            Bounds2D padBounds = GradingPatch.ComputeBounds(pad.XyVertices);
            double reach = EstimatePadReach(pad, padBounds, terrainMinZ, terrainMaxZ, terrainDiagonal);
            double expansion = Math.Max(basePadding, reach + pad.StitchApronDistance + terrainDetailSize);
            bounds[i] = ExpandBounds(padBounds, expansion);
        }

        return bounds;
    }

    private static void ComputeTerrainBoundsAndZRange(
        double[] vertices,
        int vertexCount,
        out Bounds2D bounds,
        out double minZ,
        out double maxZ)
    {
        if (vertexCount <= 0 || vertices.Length < 3)
        {
            bounds = new Bounds2D(0.0, 0.0, 0.0, 0.0);
            minZ = 0.0;
            maxZ = 0.0;
            return;
        }

        double minX = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double minY = double.PositiveInfinity;
        double maxY = double.NegativeInfinity;
        minZ = double.PositiveInfinity;
        maxZ = double.NegativeInfinity;
        int limit = Math.Min(vertexCount, vertices.Length / 3);
        for (int i = 0; i < limit; i++)
        {
            double x = vertices[i * 3];
            double y = vertices[(i * 3) + 1];
            double z = vertices[(i * 3) + 2];
            if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
                continue;

            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
            if (z < minZ) minZ = z;
            if (z > maxZ) maxZ = z;
        }

        if (!double.IsFinite(minX))
        {
            bounds = new Bounds2D(0.0, 0.0, 0.0, 0.0);
            minZ = 0.0;
            maxZ = 0.0;
            return;
        }

        bounds = new Bounds2D(minX, maxX, minY, maxY);
    }

    private static double EstimatePadReach(
        PadGrader.PadBoundary pad,
        Bounds2D padBounds,
        double terrainMinZ,
        double terrainMaxZ,
        double terrainDiagonal)
    {
        if (pad.MaxDistance > 0.0)
            return pad.MaxDistance;

        double slopeRatio = Math.Tan(pad.SlopeAngleDeg * Math.PI / 180.0);
        if (!double.IsFinite(slopeRatio) || slopeRatio <= 1e-9)
            return terrainDiagonal;

        double padMinZ = double.PositiveInfinity;
        double padMaxZ = double.NegativeInfinity;
        AccumulatePadZRange(pad, padBounds.MinX, padBounds.MinY, ref padMinZ, ref padMaxZ);
        AccumulatePadZRange(pad, padBounds.MinX, padBounds.MaxY, ref padMinZ, ref padMaxZ);
        AccumulatePadZRange(pad, padBounds.MaxX, padBounds.MinY, ref padMinZ, ref padMaxZ);
        AccumulatePadZRange(pad, padBounds.MaxX, padBounds.MaxY, ref padMinZ, ref padMaxZ);

        double dz = Math.Max(
            Math.Abs(terrainMinZ - padMaxZ),
            Math.Abs(terrainMaxZ - padMinZ));
        double reach = dz / slopeRatio;
        if (!double.IsFinite(reach))
            return terrainDiagonal;

        return Math.Min(Math.Max(reach, 0.0), terrainDiagonal);
    }

    private static void AccumulatePadZRange(
        PadGrader.PadBoundary pad,
        double x,
        double y,
        ref double minZ,
        ref double maxZ)
    {
        double z = pad.EvaluateZ(x, y);
        if (!double.IsFinite(z))
            return;

        if (z < minZ) minZ = z;
        if (z > maxZ) maxZ = z;
    }

    private static Bounds2D ExpandBounds(Bounds2D bounds, double expansion)
    {
        return new Bounds2D(
            bounds.MinX - expansion,
            bounds.MaxX + expansion,
            bounds.MinY - expansion,
            bounds.MaxY + expansion);
    }

    private static bool ConstraintIntersectsAnyBounds(
        ConstraintPolyline constraint,
        IReadOnlyList<Bounds2D> bounds)
    {
        if (constraint.Points.Length < constraint.PointCount * 3)
            return false;

        Bounds2D constraintBounds = ComputeConstraintBounds(constraint);
        for (int i = 0; i < bounds.Count; i++)
        {
            if (constraintBounds.Intersects(bounds[i]))
                return true;
        }

        return false;
    }

    private static Bounds2D ComputeConstraintBounds(ConstraintPolyline constraint)
    {
        double minX = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double minY = double.PositiveInfinity;
        double maxY = double.NegativeInfinity;
        for (int i = 0; i < constraint.PointCount; i++)
        {
            double x = constraint.Points[i * 3];
            double y = constraint.Points[(i * 3) + 1];
            if (!double.IsFinite(x) || !double.IsFinite(y))
                continue;

            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        return double.IsFinite(minX)
            ? new Bounds2D(minX, maxX, minY, maxY)
            : new Bounds2D(0.0, 0.0, 0.0, 0.0);
    }

    internal static ConstraintConflictDiagnostics.ConflictSummary AnalyzeHardConstraintConflicts(
        IReadOnlyList<ConstraintPolyline> pathConstraints,
        IReadOnlyList<ConstraintPolyline> hardConstraints,
        double tolerance)
    {
        return ConstraintConflictDiagnostics.Analyze(
            pathConstraints
                .Select(static constraint => new ConstraintConflictDiagnostics.PolylineData(constraint.Points, constraint.PointCount, constraint.IsClosed))
                .ToArray(),
            hardConstraints
                .Select(static constraint => new ConstraintConflictDiagnostics.PolylineData(constraint.Points, constraint.PointCount, constraint.IsClosed))
                .ToArray(),
            tolerance);
    }

    internal static void AddOutputPolylinesAsBreaklines(
        IReadOnlyList<MoleHill.Core.Grading.OutputPolyline> polylines,
        TerrainBuildResult build)
    {
        build.PersistentHardConstraints.AddRange(CreateOutputPolylineConstraints(polylines));
    }

    private static List<ConstraintPolyline> CreateOutputPolylineConstraints(
        IReadOnlyList<MoleHill.Core.Grading.OutputPolyline> polylines)
    {
        var constraints = new List<ConstraintPolyline>(polylines.Count);
        foreach (var poly in polylines)
        {
            if (poly.VertexCount < 2)
                continue;
            constraints.Add(new ConstraintPolyline(
                poly.Vertices,
                poly.VertexCount,
                poly.IsClosed,
                PreserveInputElevation: true));
        }

        return constraints;
    }

    internal static List<ConstraintPolyline> CreatePreservedElevationConstraints(
        IReadOnlyList<ConstraintPolyline> constraints)
    {
        var preservedConstraints = new List<ConstraintPolyline>(constraints.Count);
        foreach (ConstraintPolyline constraint in constraints)
        {
            if (constraint.PointCount < 2)
                continue;

            int pointValueCount = Math.Min(constraint.Points.Length, constraint.PointCount * 3);
            if (pointValueCount < constraint.PointCount * 3)
                continue;

            var points = new double[pointValueCount];
            Array.Copy(constraint.Points, points, pointValueCount);
            preservedConstraints.Add(new ConstraintPolyline(
                points,
                constraint.PointCount,
                constraint.IsClosed,
                PreserveInputElevation: true));
        }

        return preservedConstraints;
    }

    internal static (ResolvedGradePathDefinition[] Paths, IReadOnlyList<VariablePathWidthResolver.Diagnostic> Diagnostics)
        ResolveGradePathDefinitions(
            TerrainBuildSnapshot snapshot,
            GradePathModifierDefinition modifier,
            double curveTolerance,
            double gradePathTolerance)
    {
        var paths = new List<PathGrader.PathDefinition>();
        var sourceIds = new List<Guid>();
        double requestedEdgeLength = TerrainBuildHeuristics.GetGradePathCurveSamplingLength(modifier.Width);
        foreach (ResolvedSourceObject source in TerrainBuildSnapshotResolver.ResolveObjects(snapshot, modifier.Paths))
        {
            if (source.Geometry is not Curve curve)
                continue;
            if (!RhinoSourceResolver.TryGetPolyline(
                    curve,
                    curveTolerance,
                    requireClosed: false,
                    requestedEdgeLength,
                    maxArea: 0.0,
                    out var polyline))
            {
                continue;
            }

            var pathXy = new double[polyline.Count * 2];
            var pathZ = new double[polyline.Count];
            for (int i = 0; i < polyline.Count; i++)
            {
                pathXy[i * 2] = polyline[i].X;
                pathXy[i * 2 + 1] = polyline[i].Y;
                pathZ[i] = polyline[i].Z;
            }

            double pathCutSlope = modifier.CutSlopeAngle > 0.0 ? modifier.CutSlopeAngle : modifier.SlopeAngle;
            paths.Add(new PathGrader.PathDefinition(
                pathXy,
                pathZ,
                polyline.Count,
                modifier.Width,
                pathCutSlope,
                modifier.MaxDistance,
                modifier.SlopeAngle,
                isClosed: curve.IsClosed));
            sourceIds.Add(source.ObjectId);
        }

        // Variable width is opt-in: with the toggle off the parked WidthEdges references stay on the
        // definition but never reach the resolver, so the corridor is a plain constant-Width path.
        if (!modifier.UseVariableWidth)
        {
            var constantWidth = new ResolvedGradePathDefinition[paths.Count];
            for (int i = 0; i < constantWidth.Length; i++)
                constantWidth[i] = new ResolvedGradePathDefinition(sourceIds[i], paths[i]);
            return (constantWidth, Array.Empty<VariablePathWidthResolver.Diagnostic>());
        }

        var widthEdges = new List<VariablePathWidthResolver.EdgeDefinition>();
        int edgeSourceIndex = 0;
        foreach (Curve curve in TerrainBuildSnapshotResolver.ResolveCurves(snapshot, modifier.WidthEdges))
        {
            if (RhinoSourceResolver.TryGetPolyline(
                    curve,
                    curveTolerance,
                    requireClosed: false,
                    requestedEdgeLength,
                    maxArea: 0.0,
                    out Polyline polyline) &&
                polyline.Count >= 2)
            {
                var xy = new double[polyline.Count * 2];
                for (int i = 0; i < polyline.Count; i++)
                {
                    xy[i * 2] = polyline[i].X;
                    xy[(i * 2) + 1] = polyline[i].Y;
                }
                widthEdges.Add(new VariablePathWidthResolver.EdgeDefinition(xy, polyline.Count, curve.IsClosed, edgeSourceIndex));
            }
            edgeSourceIndex++;
        }

        VariablePathWidthResolver.Result widthResult = VariablePathWidthResolver.Resolve(
            paths,
            widthEdges,
            new VariablePathWidthResolver.Options
            {
                MaxEdgeDistance = modifier.MaxEdgeDistance,
                Tolerance = gradePathTolerance
            });
        var resolved = new ResolvedGradePathDefinition[widthResult.Paths.Length];
        for (int i = 0; i < resolved.Length; i++)
            resolved[i] = new ResolvedGradePathDefinition(sourceIds[i], widthResult.Paths[i]);
        return (resolved, widthResult.Diagnostics);
    }

    private static List<GradingPatch> BuildPadPatchSummaries(IReadOnlyList<PadGrader.PadBoundary> pads)
    {
        var patches = new List<GradingPatch>(pads.Count);
        for (int i = 0; i < pads.Count; i++)
        {
            var pad = pads[i];
            double priority = ComputePadOwnershipPriority(pad);
            patches.Add(new GradingPatch
            {
                OwnerKey = $"pad:{i}",
                Kind = GradingPatchKind.Pad,
                Priority = priority,
                OwnedRegionLoopXy = (double[])pad.XyVertices.Clone(),
                DaylightLoopXy = Array.Empty<double>(),
                StitchLoopXy = Array.Empty<double>(),
                DirtyBounds = GradingPatch.ComputeBounds(pad.XyVertices),
                UsesFallbackBand = false
            });
        }

        return patches;
    }

    private static double ComputePadOwnershipPriority(PadGrader.PadBoundary pad)
    {
        double sumX = 0.0;
        double sumY = 0.0;
        for (int i = 0; i < pad.VertexCount; i++)
        {
            sumX += pad.XyVertices[i * 2];
            sumY += pad.XyVertices[i * 2 + 1];
        }

        double cx = sumX / pad.VertexCount;
        double cy = sumY / pad.VertexCount;
        return pad.EvaluateZ(cx, cy);
    }

    internal static List<GradingPatch> BuildPathPatchSummaries(IReadOnlyList<PathGrader.PathDefinition> paths)
    {
        var patches = new List<GradingPatch>(paths.Count);
        for (int i = 0; i < paths.Count; i++)
        {
            var path = paths[i];
            double minX = double.MaxValue;
            double maxX = double.MinValue;
            double minY = double.MaxValue;
            double maxY = double.MinValue;
            for (int v = 0; v < path.VertexCount; v++)
            {
                double x = path.XyVertices[v * 2];
                double y = path.XyVertices[v * 2 + 1];
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }

            double halfWidth = path.Width * 0.5;
            double shoulderAllowance = path.MaxDistance > 0.0
                ? path.MaxDistance
                : Math.Max(path.Width * 2.0, halfWidth);
            double expansion = halfWidth + shoulderAllowance;
            double[] ownedLoop =
            {
                minX - expansion, minY - expansion,
                maxX + expansion, minY - expansion,
                maxX + expansion, maxY + expansion,
                minX - expansion, maxY + expansion
            };

            patches.Add(new GradingPatch
            {
                OwnerKey = $"path:{i}",
                Kind = GradingPatchKind.Path,
                Priority = i,
                OwnedRegionLoopXy = ownedLoop,
                DaylightLoopXy = Array.Empty<double>(),
                StitchLoopXy = Array.Empty<double>(),
                DirtyBounds = GradingPatch.ComputeBounds(ownedLoop),
                UsesFallbackBand = false
            });
        }

        return patches;
    }

    internal static GradingTopologyCacheEntry BuildPathTopologySummary(
        IReadOnlyList<double> inputVertices,
        int inputVertexCount,
        IReadOnlyList<int> inputFaces,
        int inputFaceCount,
        GradingResult? gradingResult,
        IReadOnlyList<GradingPatch> conservativePatchSummaries,
        IReadOnlyList<string> diagnostics)
    {
        IReadOnlyList<double> vertices = gradingResult?.Vertices ?? inputVertices;
        int vertexCount = gradingResult?.VertexCount ?? inputVertexCount;
        IReadOnlyList<int> faces = gradingResult?.Faces ?? inputFaces;
        int faceCount = gradingResult?.FaceCount ?? inputFaceCount;
        List<GradingPatch> patchSummaries = gradingResult?.PatchSummaries.Count > 0
            ? ClonePatchSummaries(gradingResult.PatchSummaries)
            : ClonePatchSummaries(conservativePatchSummaries);

        return new GradingTopologyCacheEntry
        {
            GraderKind = "Path",
            Fingerprint = 0,
            OutputFingerprint = ComputeGradingTopologyOutputFingerprint("Path", vertices, vertexCount, faces, faceCount),
            Vertices = Array.Empty<double>(),
            VertexCount = vertexCount,
            Faces = Array.Empty<int>(),
            FaceCount = faceCount,
            PatchSummaries = patchSummaries,
            Diagnostics = diagnostics.ToList()
        };
    }

    private static List<GradingPatch> ClonePatchSummaries(IReadOnlyList<GradingPatch> patchSummaries)
    {
        return patchSummaries
            .Select(static patch => new GradingPatch
            {
                OwnerKey = patch.OwnerKey,
                Kind = patch.Kind,
                Priority = patch.Priority,
                OwnedRegionLoopXy = (double[])patch.OwnedRegionLoopXy.Clone(),
                DaylightLoopXy = patch.DaylightLoopXy != null ? (double[])patch.DaylightLoopXy.Clone() : Array.Empty<double>(),
                StitchLoopXy = patch.StitchLoopXy != null ? (double[])patch.StitchLoopXy.Clone() : Array.Empty<double>(),
                DirtyBounds = patch.DirtyBounds,
                UsesFallbackBand = patch.UsesFallbackBand
            })
            .ToList();
    }
}
