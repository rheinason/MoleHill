using MoleHill.Core.Engine;
using MoleHill.Core.Processing;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Shared;
using RhinoMesh = Rhino.Geometry.Mesh;

namespace MoleHill.Rhino.Services;

// Certified surface-simplification stage; reusable mesh/constraint matching lives in Core.
internal static class SimplifyStage
{
    internal static void Run(ModifierBuildContext c)
    {
        var simplify = (SimplifyModifierDefinition)c.Modifier;
        RhinoMesh? input = c.CurrentMesh;
        List<MoleHill.Core.Engine.ConstraintPolyline> effectiveConstraints =
            TerrainBuildService.CombineConstraints(c.Build.PersistentHardConstraints, c.Build.PersistentElevationConstraints);
        c.CurrentMesh = TerrainBuildService.ExecuteCachedMeshStage(
            c.Build,
            c.RuntimeCache,
            c.StageKey,
            "Simplify",
            ComputeSimplifyStageFingerprint(c.Snapshot, c.Terrain, simplify, c.CurrentMeshFingerprint, effectiveConstraints),
            () => input == null ? TerrainBuildService.WarnMissingMesh(c.Build, simplify.Label) : ApplySimplify(c.Snapshot, c.Terrain, input, simplify, c.Build, c.ShouldCancel),
            result => TerrainBuildService.DescribeModifierMeshResult(simplify.Label, result),
            out ulong fingerprint,
            c.ShouldCancel);
        c.CurrentMeshFingerprint = fingerprint;
    }

    private static RhinoMesh ApplySimplify(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        RhinoMesh mesh,
        SimplifyModifierDefinition modifier,
        TerrainBuildResult build,
        Func<bool>? shouldCancel)
    {
        if (!RhinoGeometryConversions.TryExtractMeshData(mesh, out double[] vertices, out _, out int[] faces, out _, out string? extractionError))
        {
            build.Diagnostics.Add(extractionError ?? "Could not extract mesh data for simplification.");
            return mesh;
        }

        TerrainTolerancePolicy.Profile profile = TerrainBuildService.GetToleranceProfile(snapshot, terrain);
        double numericalTolerance = profile.RemeshConstraintTolerance;
        List<ConstraintPolyline> effectiveConstraints =
            TerrainBuildService.CombineConstraints(build.PersistentHardConstraints, build.PersistentElevationConstraints);
        if (!TryResolveSimplifyConstraintEdges(
            vertices, faces, effectiveConstraints, numericalTolerance,
            out int[] requiredSegments, out string? constraintFailure))
        {
            build.Diagnostics.Add($"{modifier.Label}: {constraintFailure} Incoming mesh kept.");
            return mesh;
        }

        SurfaceSimplifier.Options simplifyOptions;
        switch (modifier.Mode)
        {
            case SimplifyModifierDefinition.MaximumDeviationMode:
                simplifyOptions = new SurfaceSimplifier.Options
                {
                    Mode = SurfaceSimplifier.SimplificationMode.MaximumDeviation,
                    MaximumDeviation = modifier.MaximumDeviation,
                    NumericalTolerance = numericalTolerance,
                    ShouldCancel = shouldCancel
                };
                break;
            case SimplifyModifierDefinition.TargetVertexCountMode:
                simplifyOptions = new SurfaceSimplifier.Options
                {
                    Mode = SurfaceSimplifier.SimplificationMode.TargetVertexCount,
                    TargetVertexCount = modifier.TargetVertexCount,
                    NumericalTolerance = numericalTolerance,
                    ShouldCancel = shouldCancel
                };
                break;
            case SimplifyModifierDefinition.RetainPercentageMode:
                int percentageUsedVertexCount = CountUsedVertices(vertices.Length / 3, faces);
                if (!TryResolvePercentageTarget(
                    percentageUsedVertexCount, modifier.RetainPercentage,
                    out int percentageTarget, out string? percentageFailure))
                {
                    build.Diagnostics.Add($"{modifier.Label}: {percentageFailure} Incoming mesh kept.");
                    return mesh;
                }
                simplifyOptions = new SurfaceSimplifier.Options
                {
                    Mode = SurfaceSimplifier.SimplificationMode.TargetVertexCount,
                    TargetVertexCount = percentageTarget,
                    NumericalTolerance = numericalTolerance,
                    ShouldCancel = shouldCancel
                };
                break;
            default:
                build.Diagnostics.Add($"{modifier.Label}: Unknown simplification mode '{modifier.Mode}'. Incoming mesh kept.");
                return mesh;
        }

        SurfaceSimplifier.Result result = SurfaceSimplifier.Simplify(
            vertices, faces, requiredSegments, simplifyOptions);
        build.Diagnostics.Add(
            $"{modifier.Label}: {result.Diagnostic} " +
            $"Faces {result.InputFaceCount:N0} -> {result.OutputFaceCount:N0}; " +
            $"protected vertices {result.ProtectedVertexCount:N0}; rounds {result.Rounds:N0}; termination {result.Termination}.");

        return result.Reduced
            ? TerrainBuildService.BuildMeshFromArrays(result.Vertices, result.Faces)
            : mesh;
    }

    private static int CountUsedVertices(int vertexCount, int[] faces)
    {
        var used = new bool[vertexCount];
        int count = 0;
        foreach (int vertex in faces)
        {
            if (used[vertex])
                continue;
            used[vertex] = true;
            count++;
        }
        return count;
    }

    internal static bool TryResolvePercentageTarget(
        int usedVertexCount,
        double retainPercentage,
        out int targetVertexCount,
        out string? failure)
    {
        targetVertexCount = 0;
        failure = null;
        if (usedVertexCount < 0)
        {
            failure = "Used vertex count cannot be negative.";
            return false;
        }
        if (!double.IsFinite(retainPercentage) || retainPercentage < 0.0 || retainPercentage > 100.0)
        {
            failure = "Retain percentage must be between 0 and 100.";
            return false;
        }

        targetVertexCount = (int)Math.Floor(usedVertexCount * retainPercentage / 100.0);
        return true;
    }

    internal static bool TryResolveSimplifyConstraintEdges(
        double[] vertices,
        int[] faces,
        IReadOnlyList<ConstraintPolyline> constraints,
        double tolerance,
        out int[] requiredSegments,
        out string? failure) =>
        SurfaceConstraintEdgeResolver.TryResolve(
            vertices, faces, constraints, tolerance, out requiredSegments, out failure);

    private static ulong ComputeSimplifyStageFingerprint(
        TerrainBuildSnapshot snapshot,
        TerrainDefinition terrain,
        SimplifyModifierDefinition modifier,
        ulong upstreamFingerprint,
        IReadOnlyList<ConstraintPolyline> effectiveConstraints)
    {
        var builder = new FingerprintBuilder();
        builder.Add("SurfaceSimplifierV1");
        builder.Add(TerrainBuildService.ComputeModifierStageFingerprint(snapshot, terrain, modifier, upstreamFingerprint));
        builder.Add(TerrainBuildService.ComputeConstraintsFingerprint(effectiveConstraints));
        return builder.ToUInt64();
    }
}
