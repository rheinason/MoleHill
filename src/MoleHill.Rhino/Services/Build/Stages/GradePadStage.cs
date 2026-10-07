using System.Diagnostics;
using MoleHill.Core.Analysis;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Shared;
using Rhino.Geometry;
using Rhino;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

internal static class GradePadStage
{
    internal static void Run(ModifierBuildContext c)
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

    private const double MinRepresentablePadPlaneNormalZ = 1e-3;

    private static ulong ComputeGradePadTopologyFingerprint(
        ulong upstreamFingerprint,
        double tolerance,
        GradePadModifierDefinition modifier,
        IReadOnlyList<PadGrader.PadBoundary> pads,
        IReadOnlyList<PadGrader.LockCurve> lockCurves)
    {
        var builder = new FingerprintBuilder();
        builder.Add("GradePadTopologyV4");
        builder.Add(upstreamFingerprint);
        builder.Add(tolerance);
        builder.Add(modifier.SlopeAngle);
        builder.Add(modifier.CutSlopeAngle);
        builder.Add(modifier.MaxDistance);
        builder.Add(pads.Count);
        foreach (var pad in pads)
        {
            builder.Add(pad.VertexCount);
            TerrainBuildService.AddDoubleArrayFingerprint(ref builder, pad.XyVertices);
            TerrainBuildService.AddDoubleArrayFingerprint(ref builder, pad.BoundaryVertices);
            builder.Add(pad.PlaneXCoeff);
            builder.Add(pad.PlaneYCoeff);
            builder.Add(pad.PlaneConstant);
            builder.Add(pad.StitchApronDistance);
        }

        builder.Add(lockCurves.Count);
        foreach (var lc in lockCurves)
        {
            builder.Add(lc.VertexCount);
            TerrainBuildService.AddDoubleArrayFingerprint(ref builder, lc.XyVertices);
        }

        return builder.ToUInt64();
    }

    private static ulong ComputeGradePadResolvedInputFingerprint(
        ulong topologyOutputFingerprint,
        IReadOnlyList<PadGrader.PadBoundary> pads,
        GradePadModifierDefinition modifier)
    {
        var builder = new FingerprintBuilder();
        builder.Add("GradePadResolved");
        builder.Add(topologyOutputFingerprint);
        builder.Add(modifier.SlopeAngle);
        builder.Add(modifier.CutSlopeAngle);
        builder.Add(modifier.MaxDistance);
        builder.Add(pads.Count);
        foreach (var pad in pads)
        {
            builder.Add(pad.VertexCount);
            TerrainBuildService.AddDoubleArrayFingerprint(ref builder, pad.XyVertices);
            TerrainBuildService.AddDoubleArrayFingerprint(ref builder, pad.BoundaryVertices);
            builder.Add(pad.PlaneXCoeff);
            builder.Add(pad.PlaneYCoeff);
            builder.Add(pad.PlaneConstant);
            builder.Add(pad.StitchApronDistance);
        }

        return builder.ToUInt64();
    }

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
        ulong preResolutionFingerprint = TerrainBuildService.ComputeModifierStageFingerprint(snapshot, terrain, modifier, upstreamFingerprint);

        if (runtimeCache.StageEntries.TryGetValue(stageKey, out var cachedEntry) &&
            cachedEntry.PreResolutionFingerprint == preResolutionFingerprint)
        {
            RhinoMesh? cachedMesh = TerrainBuildService.RestoreCachedMeshStage(build, cachedEntry, out outputFingerprint);
            timer.Stop();
            build.RecordTiming(stageName, timer.Elapsed, TerrainBuildService.AppendCacheHitDetail(TerrainBuildService.DescribeModifierMeshResult(modifier.Label, cachedMesh)));
            return cachedMesh;
        }

        var inputsTimer = Stopwatch.StartNew();
        int diagnosticsStart = build.Diagnostics.Count;
        int structuredDiagnosticsStart = build.StructuredDiagnostics.Count;
        StageSupport.ThrowIfCancellationRequested(shouldCancel);
        if (mesh == null)
        {
            TerrainBuildService.WarnMissingMesh(build, modifier.Label);
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
                out outputFingerprint);
        }

        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out int vertexCount, out var faces, out int faceCount, out var errorMessage))
        {
            build.Diagnostics.Add(errorMessage ?? "Could not extract mesh data for grade pad.");
            return TerrainBuildService.StoreMeshStageCache(
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
                TerrainBuildService.DescribeModifierMeshResult(modifier.Label, mesh),
                timer,
                out outputFingerprint,
                structuredDiagnostics: build.StructuredDiagnostics.Skip(structuredDiagnosticsStart));
        }

        TerrainTolerancePolicy.Profile toleranceProfile = TerrainBuildService.GetToleranceProfile(snapshot, terrain);
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
            TerrainBuildService.StageTimingDiagnosticThresholdMs);
        build.RecordTiming(
            "Grade Pad Constraints",
            resolvedInputs.ConstraintElapsed,
            $"{resolvedInputs.Constraints.Length:N0} constraints over {vertexCount:N0} verts",
            TerrainBuildService.StageTimingDiagnosticThresholdMs);
        build.Diagnostics.AddRange(resolvedInputs.Diagnostics);
        build.StructuredDiagnostics.AddRange(resolvedInputs.StructuredDiagnostics);
        StageSupport.ThrowIfCancellationRequested(shouldCancel);
        if (resolvedInputs.Pads.Length == 0)
        {
            build.Diagnostics.Add("Grade Pad has no valid closed boundaries.");
            return TerrainBuildService.StoreMeshStageCache(
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
                TerrainBuildService.DescribeModifierMeshResult(modifier.Label, mesh),
                timer,
                out outputFingerprint,
                structuredDiagnostics: build.StructuredDiagnostics.Skip(structuredDiagnosticsStart));
        }

        var locksTimer = Stopwatch.StartNew();
        var effectiveLocks = TerrainBuildService.CombinePadLockCurves(
            resolvedInputs.Locks,
            TerrainBuildService.UpstreamBreaklines(build, modifier.GradeThroughBreaklines),
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
        build.RecordTiming("Grade Pad Locks", locksTimer.Elapsed, $"{effectiveLocks.Length:N0} locks", TerrainBuildService.StageTimingDiagnosticThresholdMs);

        var dirtyTimer = Stopwatch.StartNew();
        List<GradingPatch> patchSummaries = TerrainBuildService.BuildPadPatchSummaries(resolvedInputs.Pads);
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
        build.RecordTiming("Grade Pad Dirty Scan + Fingerprint", dirtyTimer.Elapsed, null, TerrainBuildService.StageTimingDiagnosticThresholdMs);

        // Everything above is stage scaffolding, not grading: mesh extraction, curve resolution, lock
        // combination, overlap scanning and four fingerprints. It is timed separately because the
        // stage total was measurably larger than PadGrader's own cost and nothing said where the rest
        // went - see docs/architecture.md, "Rhino: edit-to-visible latency".
        inputsTimer.Stop();
        build.RecordTiming(
            "Grade Pad Inputs",
            inputsTimer.Elapsed,
            $"{vertexCount:N0} verts, {resolvedInputs.Pads.Length:N0} pads, {effectiveLocks.Length:N0} locks",
            TerrainBuildService.StageTimingDiagnosticThresholdMs);

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
                TerrainBuildService.AppendCacheHitDetail($"{topologyEntry.VertexCount:N0} verts, {topologyEntry.FaceCount:N0} faces"),
                TerrainBuildService.StageTimingDiagnosticThresholdMs);
        }
        else
        {
            StageSupport.ThrowIfCancellationRequested(shouldCancel);
            var topologyDiagnostics = new List<string>();
            var gradeResult = GradePadsWindowed(
                vertices,
                vertexCount,
                faces,
                faceCount,
                resolvedInputs.Pads,
                effectiveLocks,
                TerrainBuildService.UpstreamBreaklines(build, modifier.GradeThroughBreaklines),
                gradePadTolerance,
                toleranceProfile.DetailSize,
                runtimeCache,
                stageKey,
                topologyDiagnostics,
                out var gradeWarning,
                out IReadOnlyList<MoleHill.Core.Grading.OutputPolyline> failureOutputPolylines,
                out IReadOnlyList<GradingDiagnostic> failureStructuredDiagnostics);
            StageSupport.ThrowIfCancellationRequested(shouldCancel);
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
            List<ConstraintPolyline> outputConstraints = TerrainBuildService.CreateOutputPolylineConstraints(
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
                OutputFingerprint = TerrainBuildService.ComputeGradingTopologyOutputFingerprint("Pad", topologyVertices, topologyVertexCount, topologyFaces, topologyFaceCount),
                Vertices = topologyVertices,
                VertexCount = topologyVertexCount,
                Faces = topologyFaces,
                FaceCount = topologyFaceCount,
                PatchSummaries = gradeResult?.PatchSummaries.Count > 0
                    ? TerrainBuildService.ClonePatchSummaries(gradeResult.PatchSummaries)
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
                TerrainBuildService.StageTimingDiagnosticThresholdMs);
        }

        bool gradePadStageFailed = topologyEntry.Diagnostics.Any(
            static diagnostic => diagnostic.Contains("Grade Pad protected patch failed", StringComparison.OrdinalIgnoreCase));
        build.Diagnostics.Add(gradePadStageFailed
            ? $"Grade Pad protected patch failed; incoming mesh retained ({TerrainBuildService.DescribeTopologyCounts(vertexCount, faceCount, topologyEntry.VertexCount, topologyEntry.FaceCount)})."
            : $"Grade Pad local patch ({TerrainBuildService.DescribeTopologyCounts(vertexCount, faceCount, topologyEntry.VertexCount, topologyEntry.FaceCount)}).");

        ulong resolvedInputFingerprint = ComputeGradePadResolvedInputFingerprint(topologyEntry.OutputFingerprint, resolvedInputs.Pads, modifier);
        if (cachedEntry != null && cachedEntry.ResolvedInputFingerprint == resolvedInputFingerprint)
        {
            StageCacheEntry refreshedEntry = TerrainBuildService.CloneStageCacheEntry(cachedEntry, preResolutionFingerprint, resolvedInputFingerprint);
            runtimeCache.StageEntries[stageKey] = refreshedEntry;

            RhinoMesh? resolvedCachedMesh = TerrainBuildService.RestoreCachedMeshStage(build, refreshedEntry, out outputFingerprint);
            timer.Stop();
            build.RecordTiming(stageName, timer.Elapsed, TerrainBuildService.AppendCacheHitDetail(TerrainBuildService.DescribeModifierMeshResult(modifier.Label, resolvedCachedMesh)));
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
        StageSupport.ThrowIfCancellationRequested(shouldCancel);
        build.RecordTiming(
            "Grade Pad Filter",
            filterTimer.Elapsed,
            $"{topologyEntry.VertexCount:N0} verts",
            TerrainBuildService.StageTimingDiagnosticThresholdMs);

        var outputMeshTimer = Stopwatch.StartNew();
        RhinoMesh resultMesh = TerrainBuildService.FinalizeGradingMesh(
            RhinoGeometryConversions.BuildMesh(gradedVertices, topologyEntry.VertexCount, topologyEntry.Faces, topologyEntry.FaceCount),
            "Grade Pad",
            build);
        outputMeshTimer.Stop();
        build.RecordTiming(
            "Grade Pad Output Mesh",
            outputMeshTimer.Elapsed,
            $"{topologyEntry.VertexCount:N0} verts, {topologyEntry.FaceCount:N0} faces",
            TerrainBuildService.StageTimingDiagnosticThresholdMs);
        if (modifier.GradeThroughBreaklines && !gradePadStageFailed)
        {
            TerrainBuildService.DropRegradedBreaklines(
                build, gradedVertices, topologyEntry.VertexCount, topologyEntry.Faces, topologyEntry.FaceCount,
                snapshot.ModelAbsoluteTolerance, snapshot.ModelUnitSystem, "Grade Pad");
        }
        // PadGrader's constraint set contains temporary XY construction loops for the pad, shoulder,
        // and stitch apron. Their Z values are placeholders and some loops are deliberately softened
        // or replaced while assembling the final patch, so they are not durable elevation constraints
        // on resultMesh. The actual graded pad boundary is already published from OutputPolylines as a
        // persistent hard constraint above. Persisting the construction set made downstream consumers
        // reject the Grade Pad mesh as conflicting with metadata produced by the same stage.

        return TerrainBuildService.StoreMeshStageCache(
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
            TerrainBuildService.DescribeModifierMeshResult(modifier.Label, resultMesh),
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

    private static GradingResult? GradePadsWindowed(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PadGrader.PadBoundary[] pads,
        PadGrader.LockCurve[] locks,
        IReadOnlyList<ConstraintPolyline> hardConstraints,
        double tolerance,
        double detailSize,
        TerrainRuntimeCache runtimeCache,
        string memoKey,
        List<string> diagnostics,
        out string? warning,
        out IReadOnlyList<OutputPolyline> failureOutputPolylines,
        out IReadOnlyList<GradingDiagnostic> failureDiagnostics)
    {
        runtimeCache.GradingWindowMemos.TryGetValue(memoKey, out GradingWindows.Memo? previous);
        var next = new GradingWindows.Memo();
        GradeOutcome outcome = PadGrader.GradeWindowed(
            new PadGradeRequest
            {
                Terrain = new IndexedTriMesh(vertices, vertexCount, faces, faceCount),
                Pads = pads,
                LockCurves = locks,
                HardConstraints = hardConstraints,
                ModelTolerance = tolerance,
                TerrainDetailSize = detailSize
            },
            previous, next, diagnostics);
        runtimeCache.GradingWindowMemos[memoKey] = next;
        warning = outcome.ErrorMessage;
        failureOutputPolylines = outcome.FailureOutputPolylines;
        failureDiagnostics = outcome.FailureDiagnostics;
        return outcome.Result;
    }

    private sealed class ResolvedGradePadInputs
    {
        public required PadGrader.PadBoundary[] Pads { get; init; }

        public required PadGrader.LockCurve[] Locks { get; init; }

        public required ConstraintPolyline[] Constraints { get; init; }

        public required double SuggestedEdgeLength { get; init; }

        public required string[] Diagnostics { get; init; }

        public required IReadOnlyList<GradingDiagnostic> StructuredDiagnostics { get; init; }

        /// <summary>
        /// How long <c>PadGrader.CreateConstraints</c> took. Reported separately because it is the only
        /// part of input resolution that scales with the terrain mesh rather than with the number of
        /// pads, and it dominated the stage - see docs/architecture.md, "The geometry-heavy case".
        /// </summary>
        public TimeSpan ConstraintElapsed { get; init; }
    }
}
