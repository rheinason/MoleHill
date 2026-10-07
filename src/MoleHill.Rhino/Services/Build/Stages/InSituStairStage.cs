using System.Diagnostics;
using MoleHill.Core.Engine;
using MoleHill.Core.Grading;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Shared;
using Rhino.Geometry;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

internal static class InSituStairStage
{
    internal static void Run(ModifierBuildContext c)
    {
        var inSituStair = (InSituStairModifierDefinition)c.Modifier;
        RhinoMesh? input = c.CurrentMesh;
        c.CurrentMesh = TerrainBuildService.ExecuteCachedMeshStage(
            c.Build,
            c.RuntimeCache,
            c.StageKey,
            "In-Situ Stair",
            TerrainBuildService.ComputeModifierStageFingerprint(c.Snapshot, c.Terrain, inSituStair, c.CurrentMeshFingerprint),
            () => input == null
                ? TerrainBuildService.WarnMissingMesh(c.Build, inSituStair.Label)
                : ApplyInSituStair(c.Snapshot, c.Terrain, input, inSituStair, c.Build, c.Mode),
            result => TerrainBuildService.DescribeModifierMeshResult(inSituStair.Label, result),
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

    private static void AddPersistentHardConstraints(
        TerrainBuildResult build,
        IReadOnlyList<ConstraintPolyline> constraints)
    {
        List<ConstraintPolyline> preservedConstraints = TerrainBuildService.CreatePreservedElevationConstraints(constraints);
        if (preservedConstraints.Count == 0)
            return;

        List<ConstraintPolyline> mergedConstraints = TerrainBuildService.CombineConstraints(build.PersistentHardConstraints, preservedConstraints);
        build.PersistentHardConstraints.Clear();
        build.PersistentHardConstraints.AddRange(mergedConstraints);
    }

    private static List<ConstraintPolyline> CreateInSituStairConstraints(
        IReadOnlyList<InSituStairReference> references)
    {
        var constraints = new List<ConstraintPolyline>(references.Count);
        foreach (InSituStairReference reference in references)
        {
            SurfaceStripGrader.SurfaceDefinition surface = reference.SupportSurface;
            if (surface.BoundaryVertexCount < 3)
                continue;

            int pointValueCount = Math.Min(surface.BoundaryVertices.Length, surface.BoundaryVertexCount * 3);
            if (pointValueCount < surface.BoundaryVertexCount * 3)
                continue;

            var points = new double[pointValueCount];
            Array.Copy(surface.BoundaryVertices, points, pointValueCount);
            constraints.Add(new ConstraintPolyline(
                points,
                surface.BoundaryVertexCount,
                IsClosed: true,
                PreserveInputElevation: true));
        }

        return constraints;
    }

    private static RhinoMesh ApplyInSituStair(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        InSituStairModifierDefinition modifier,
        TerrainBuildResult build,
        TerrainBuildMode mode)
    {
        modifier.ComputedSurfaceCount = null;
        modifier.ComputedTreadDepthSummary = null;
        modifier.ComputedStepCountSummary = null;

        if (mode == TerrainBuildMode.Preview)
        {
            build.Diagnostics.Add("In-Situ Stair preview deferred to full rebuild.");
            return RhinoGeometryConversions.DuplicateWithCachedData(mesh);
        }

        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out var vertices, out int vertexCount, out var faces, out int faceCount, out var errorMessage))
        {
            build.Diagnostics.Add(errorMessage ?? "Could not extract mesh data for in-situ stair grading.");
            return mesh;
        }

        if (modifier.RiserHeight <= 0)
        {
            build.Diagnostics.Add("In-Situ Stair riser height must be positive.");
            return mesh;
        }

        var referenceResolveTimer = Stopwatch.StartNew();
        var referenceMeshes = TerrainBuildSnapshotResolver.ResolveMeshes(snapshot, modifier.ReferenceSurface);
        referenceResolveTimer.Stop();
        build.RecordTiming(
            "In-Situ Stair Reference Resolve",
            referenceResolveTimer.Elapsed,
            $"{referenceMeshes.Count:N0} reference meshes",
            TerrainBuildService.StageTimingDiagnosticThresholdMs);
        if (referenceMeshes.Count == 0)
        {
            build.Diagnostics.Add("In-Situ Stair has no valid reference surface.");
            return mesh;
        }

        var referenceBuildTimer = Stopwatch.StartNew();
        if (!InSituStairReferenceBuilder.TryBuild(
                referenceMeshes,
                modifier.RiserHeight,
                modifier.SlopeAngle,
                modifier.MaxDistance,
                out var stairBuild,
                out errorMessage))
        {
            referenceBuildTimer.Stop();
            build.Diagnostics.Add(errorMessage ?? "In-Situ Stair could not interpret the reference surface.");
            return mesh;
        }
        referenceBuildTimer.Stop();
        build.RecordTiming(
            "In-Situ Stair Reference Build",
            referenceBuildTimer.Elapsed,
            $"{stairBuild!.SurfaceCount:N0} surfaces, {stairBuild.StepCountSummary} steps",
            TerrainBuildService.StageTimingDiagnosticThresholdMs);

        double tolerance = TerrainBuildService.GetToleranceProfile(snapshot, terrain).RemeshConstraintTolerance;

        modifier.ComputedSurfaceCount = stairBuild.SurfaceCount;
        modifier.ComputedTreadDepthSummary = stairBuild.TreadDepthSummary;
        modifier.ComputedStepCountSummary = stairBuild.StepCountSummary;

        double[] currentVertices = vertices;
        int currentVertexCount = vertexCount;
        int[] currentFaces = faces;
        int currentFaceCount = faceCount;
        var gradingWarnings = new List<string>();

        var gradingTimer = Stopwatch.StartNew();
        SurfaceStripGrader.SurfaceDefinition[] stairSurfaces = stairBuild.References
            .Select(static reference => reference.SupportSurface)
            .ToArray();
        var batchGradeTimer = Stopwatch.StartNew();
        var batchResult = SurfaceStripGrader.Grade(
            currentVertices,
            currentVertexCount,
            currentFaces,
            currentFaceCount,
            stairSurfaces,
            build.PersistentHardConstraints,
            out var batchGradingWarning,
            out var batchProfile);
        batchGradeTimer.Stop();
        build.RecordTiming(
            "In-Situ Stair Surface Grade",
            batchGradeTimer.Elapsed,
            $"batched {stairSurfaces.Length:N0} surfaces: {currentVertexCount:N0} verts/{currentFaceCount:N0} faces -> {batchResult?.VertexCount ?? 0:N0} verts/{batchResult?.FaceCount ?? 0:N0} faces; {batchProfile.FormatSummary()}",
            TerrainBuildService.StageTimingDiagnosticThresholdMs);

        if (batchResult == null)
        {
            build.Diagnostics.Add(batchGradingWarning ?? "In-Situ Stair grading failed.");
            return mesh;
        }

        currentVertices = batchResult.Vertices;
        currentVertexCount = batchResult.VertexCount;
        currentFaces = batchResult.Faces;
        currentFaceCount = batchResult.FaceCount;
        if (!string.IsNullOrWhiteSpace(batchGradingWarning))
            gradingWarnings.Add(batchGradingWarning);

        gradingTimer.Stop();
        build.RecordTiming(
            "In-Situ Stair Grade",
            gradingTimer.Elapsed,
            $"{stairBuild.References.Count:N0} surfaces -> {currentVertexCount:N0} verts, {currentFaceCount:N0} faces",
            TerrainBuildService.StageTimingDiagnosticThresholdMs);

        var outputTimer = Stopwatch.StartNew();
        int outputCountBefore = build.AuxiliaryObjects.Count;
        double minTreadDepth = Math.Max(0.0, modifier.MinTreadDepth);
        double? lowestWarnedTreadDepth = null;
        int treadDepthWarningCount = 0;
        foreach (var stairReference in stairBuild.References)
        {
            bool warnTreadDepth = minTreadDepth > 0 && stairReference.TreadDepth < minTreadDepth;
            if (warnTreadDepth)
            {
                treadDepthWarningCount++;
                lowestWarnedTreadDepth = lowestWarnedTreadDepth.HasValue
                    ? Math.Min(lowestWarnedTreadDepth.Value, stairReference.TreadDepth)
                    : stairReference.TreadDepth;
            }

            foreach (var stairBrep in stairReference.StairBreps)
            {
                build.AuxiliaryObjects.Add(new GeneratedRhinoObject
                {
                    Role = LayerRole.GradingAuxiliary,
                    Geometry = stairBrep,
                    Name = "Stair",
                    LayerPath = snapshot.LayerRoles.Path(LayerRole.GradingAuxiliary),
                    ColorArgb = warnTreadDepth ? TerrainBuildService.InSituStairTreadDepthWarningColorArgb : null
                });
            }

            if (modifier.ShowTreadLabels)
            {
                build.AuxiliaryObjects.Add(new GeneratedRhinoObject
                {
                    Role = LayerRole.Labels,
                    Geometry = new TextDot($"Tread {stairReference.TreadDepth:G4}", stairReference.TreadDepthLabelPoint),
                    Name = "Stair Tread Depth",
                    LayerPath = snapshot.LayerRoles.Path(LayerRole.Labels)
                });
            }
        }
        outputTimer.Stop();
        build.RecordTiming(
            "In-Situ Stair Outputs",
            outputTimer.Elapsed,
            $"{build.AuxiliaryObjects.Count - outputCountBefore:N0} auxiliary outputs",
            TerrainBuildService.StageTimingDiagnosticThresholdMs);

        build.Diagnostics.Add(stairBuild.StatusSummary);
        if (treadDepthWarningCount > 0 && lowestWarnedTreadDepth.HasValue)
        {
            string surfaceText = treadDepthWarningCount == 1 ? "surface" : "surfaces";
            build.Diagnostics.Add(
                $"In-Situ Stair tread depth warning: {treadDepthWarningCount:N0} {surfaceText} below minimum {minTreadDepth:G4}; lowest tread {lowestWarnedTreadDepth.Value:G4}. Stair solids shown bright red.");
        }
        foreach (var warning in stairBuild.Warnings)
            build.Diagnostics.Add(warning);
        foreach (var gradingWarning in gradingWarnings)
            build.Diagnostics.Add(gradingWarning);

        var persistTimer = Stopwatch.StartNew();
        AddPersistentHardConstraints(build, CreateInSituStairConstraints(stairBuild.References));
        persistTimer.Stop();
        build.RecordTiming(
            "In-Situ Stair Persist Constraints",
            persistTimer.Elapsed,
            $"{build.PersistentHardConstraints.Count:N0} hard constraints",
            TerrainBuildService.StageTimingDiagnosticThresholdMs);

        var meshBuildTimer = Stopwatch.StartNew();
        RhinoMesh stairMesh = RhinoGeometryConversions.BuildMesh(currentVertices, currentVertexCount, currentFaces, currentFaceCount);
        meshBuildTimer.Stop();
        build.RecordTiming(
            "In-Situ Stair Mesh Build",
            meshBuildTimer.Elapsed,
            $"{stairMesh.Vertices.Count:N0} verts, {stairMesh.Faces.Count:N0} faces",
            TerrainBuildService.StageTimingDiagnosticThresholdMs);

        var cleanupTimer = Stopwatch.StartNew();
        RhinoMesh cleanedStairMesh = TerrainBuildService.CleanTinyFaces(stairMesh, tolerance, "In-Situ Stair", build);
        cleanupTimer.Stop();
        build.RecordTiming(
            "In-Situ Stair Tiny Cleanup",
            cleanupTimer.Elapsed,
            ReferenceEquals(cleanedStairMesh, stairMesh)
                ? "unchanged"
                : $"{cleanedStairMesh.Vertices.Count:N0} verts, {cleanedStairMesh.Faces.Count:N0} faces",
            TerrainBuildService.StageTimingDiagnosticThresholdMs);

        return cleanedStairMesh;
    }
}
