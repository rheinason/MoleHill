using System.Diagnostics;
using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

/// <summary>
/// Grades a terrain mesh along path curves (roads, sidewalks, etc.).
/// Adds road edges (path offset by half-width) as constrained edges,
/// re-triangulates, then grades Z: inside road = path Z, outside = slope transition.
/// </summary>
public static partial class PathGrader
{
    internal sealed class PerformanceTimings
    {
        public double TotalMilliseconds { get; internal set; }

        public double InputValidationMilliseconds { get; internal set; }

        public long InputValidationAllocatedBytes { get; internal set; }

        public double TerrainGridMilliseconds { get; internal set; }

        public long TerrainGridAllocatedBytes { get; internal set; }

        public double BarrierPreparationMilliseconds { get; internal set; }

        public long BarrierPreparationAllocatedBytes { get; internal set; }

        public double CorridorDaylightMilliseconds { get; internal set; }

        public long CorridorDaylightAllocatedBytes { get; internal set; }

        public double LoopPreparationMilliseconds { get; internal set; }

        public long LoopPreparationAllocatedBytes { get; internal set; }

        public double ConformSplitMilliseconds { get; internal set; }

        public long ConformSplitAllocatedBytes { get; internal set; }

        internal MeshAreaTopologySplitter.PerformanceTimings? ConformSplitDetails { get; set; }

        public double TopologyValidationMilliseconds { get; internal set; }

        public long TopologyValidationAllocatedBytes { get; internal set; }

        public double ApplyGradingZMilliseconds { get; internal set; }

        public long ApplyGradingZAllocatedBytes { get; internal set; }

        public double ResultAssemblyMilliseconds { get; internal set; }

        public long ResultAssemblyAllocatedBytes { get; internal set; }
    }

    /// <summary>
    /// Apply path grading to a terrain mesh.
    /// Overlapping paths are blended by proximity so junction behavior is stable
    /// regardless of the input order.
    /// </summary>
    public static GradingResult? Grade(
        double[] vertices, int vertexCount,
        int[] faces, int faceCount,
        PathDefinition[] paths,
        out string? errorMessage,
        double modelTolerance = GradingTolerances.DefaultModelTolerance,
        bool preferSplitKeep = false)
    {
        return Grade(vertices, vertexCount, faces, faceCount, paths, Array.Empty<ConstraintPolyline>(), out errorMessage, modelTolerance, preferSplitKeep);
    }

    public static GradingResult? Grade(
        double[] vertices, int vertexCount,
        int[] faces, int faceCount,
        PathDefinition[] paths,
        IReadOnlyList<ConstraintPolyline> hardConstraints,
        out string? errorMessage,
        double modelTolerance = GradingTolerances.DefaultModelTolerance,
        bool preferSplitKeep = false)
    {
        return GradeCore(
            vertices,
            vertexCount,
            faces,
            faceCount,
            paths,
            hardConstraints,
            out errorMessage,
            modelTolerance,
            preferSplitKeep,
            performanceTimings: null);
    }

    internal static GradingResult? Grade(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PathDefinition[] paths,
        IReadOnlyList<ConstraintPolyline> hardConstraints,
        out string? errorMessage,
        double modelTolerance,
        bool preferSplitKeep,
        PerformanceTimings performanceTimings)
    {
        ArgumentNullException.ThrowIfNull(performanceTimings);
        return GradeCore(
            vertices,
            vertexCount,
            faces,
            faceCount,
            paths,
            hardConstraints,
            out errorMessage,
            modelTolerance,
            preferSplitKeep,
            performanceTimings);
    }

    private static GradingResult? GradeCore(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PathDefinition[] paths,
        IReadOnlyList<ConstraintPolyline> hardConstraints,
        out string? errorMessage,
        double modelTolerance,
        bool preferSplitKeep,
        PerformanceTimings? performanceTimings)
    {
        Stopwatch? totalTimer = performanceTimings != null ? Stopwatch.StartNew() : null;
        Stopwatch? validationTimer = performanceTimings != null ? Stopwatch.StartNew() : null;
        long validationAllocatedBefore = performanceTimings != null
            ? GC.GetTotalAllocatedBytes(precise: true)
            : 0;
        try
        {
            errorMessage = null;
            hardConstraints ??= Array.Empty<ConstraintPolyline>();

            if (!GradingInputValidator.ValidateTerrainMesh(vertices, vertexCount, faces, faceCount, out errorMessage))
                return null;

            if (!GradingInputValidator.ValidateConstraintPolylines(hardConstraints, "Hard", out errorMessage))
                return null;

            if (!GradingInputValidator.ValidatePathDefinitions(paths, out errorMessage))
                return null;
            if (performanceTimings != null)
            {
                performanceTimings.InputValidationMilliseconds = validationTimer!.Elapsed.TotalMilliseconds;
                performanceTimings.InputValidationAllocatedBytes =
                    GC.GetTotalAllocatedBytes(precise: true) - validationAllocatedBefore;
            }

            paths = StopPathsAtHardConstraints(paths, hardConstraints, modelTolerance, out int stops, out int leftInside);
            if (paths.Length == 0)
            {
                errorMessage = "Grade Path lies entirely inside hard constraints; there is nothing left to grade.";
                return null;
            }

            GradingResult? graded = GradeTiers(
                vertices, vertexCount, faces, faceCount, paths, hardConstraints, out errorMessage, modelTolerance,
                preferSplitKeep, performanceTimings);
            if (graded == null || stops == 0)
                return graded;

            string stopMessage =
                $"Grade Path stopped at {stops:N0} crossing(s) of a hard constraint" +
                (leftInside > 0 ? $"; {leftInside:N0} piece(s) inside a graded area were left to it." : ".");
            return WithExtraDiagnostic(graded, GradingDiagnostic.Information("grade_path.barrier_stops", stopMessage, operation: "grade_path"));
        }
        finally
        {
            if (performanceTimings != null)
                performanceTimings.TotalMilliseconds = totalTimer!.Elapsed.TotalMilliseconds;
        }
    }

    /// <summary>The tier cascade: explicit corridor, then split-keep, then constraint insertion.</summary>
    private static GradingResult? GradeTiers(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PathDefinition[] paths,
        IReadOnlyList<ConstraintPolyline> hardConstraints,
        out string? errorMessage,
        double modelTolerance,
        bool preferSplitKeep,
        PerformanceTimings? performanceTimings)
    {
        errorMessage = null;

        string? preferredSplitKeepFailureReason = null;
        if (preferSplitKeep)
        {
            GradingResult? preferredSplitKeep = GradeWithSplitKeep(
                vertices,
                vertexCount,
                faces,
                faceCount,
                paths,
                hardConstraints,
                modelTolerance,
                out preferredSplitKeepFailureReason,
                performanceTimings);
            if (preferredSplitKeep != null)
            {
                errorMessage = null;
                return WithExtraDiagnostic(
                    preferredSplitKeep,
                    GradingDiagnostic.Information(
                        "grade_path.split_keep.preferred",
                        "Grade Path performance mode: terrain conform (split-keep) was selected before explicit corridor assembly for this large constrained mesh.",
                        operation: "grade_path"));
            }
        }

        // Primary path: explicit corridor construction (ruled road surface + side batters welded into
        // terrain). Defers for interacting corridors, hard constraints, or any case it cannot make
        // watertight and manifold.
        GradingResult? explicitResult = GradeWithExplicitCorridor(
            vertices, vertexCount, faces, faceCount, paths, hardConstraints, modelTolerance, out string? explicitFailureReason);
        if (explicitResult != null)
        {
            errorMessage = null;
            return explicitResult;
        }

        // Record WHY the preferred explicit corridor path deferred so a fallback success does not
        // silently mask an explicit-path regression.
        GradingDiagnostic? explicitFallbackDiagnostic = string.IsNullOrWhiteSpace(explicitFailureReason)
            ? null
            : GradingDiagnostic.Information(
                "grade_path.explicit.fallback",
                $"Explicit corridor construction deferred: {explicitFailureReason}",
                operation: "grade_path");

        // Middle tier: conform the terrain to the corridor loops and keep the whole mesh (watertight
        // by construction) — covers corridors the explicit carve/fill/weld cannot trace, without
        // dropping to the sliver-prone constraint-insertion rebuild.
        GradingResult? splitKeep = null;
        string? splitKeepFailureReason = preferredSplitKeepFailureReason;
        if (!preferSplitKeep)
        {
            splitKeep = GradeWithSplitKeep(
                vertices,
                vertexCount,
                faces,
                faceCount,
                paths,
                hardConstraints,
                modelTolerance,
                out splitKeepFailureReason,
                performanceTimings);
        }
        if (splitKeep != null)
        {
            errorMessage = null;
            return explicitFallbackDiagnostic != null
                ? WithExtraDiagnostic(splitKeep, explicitFallbackDiagnostic.Value)
                : splitKeep;
        }

        GradingDiagnostic? splitKeepFallbackDiagnostic = string.IsNullOrWhiteSpace(splitKeepFailureReason)
            ? null
            : GradingDiagnostic.Information(
                "grade_path.split_keep.fallback",
                $"Split-keep conform deferred to the topology rebuild: {splitKeepFailureReason}",
                operation: "grade_path");

        // Grade Path must own and rebuild topology. Do not silently fall back to Z-only grading.
        var result = GradeWithEdges(vertices, vertexCount, faces, faceCount, paths, hardConstraints, modelTolerance, out string? topologyError, out _);
        if (result != null)
        {
            errorMessage = null;
            if (explicitFallbackDiagnostic != null)
                result = WithExtraDiagnostic(result, explicitFallbackDiagnostic.Value);
            if (splitKeepFallbackDiagnostic != null)
                result = WithExtraDiagnostic(result, splitKeepFallbackDiagnostic.Value);
            return result;
        }

        errorMessage = string.IsNullOrWhiteSpace(topologyError)
            ? "Grade Path topology rebuild failed."
            : $"Grade Path topology rebuild failed: {topologyError}";
        return null;
    }

    /// <summary>Returns a copy of <paramref name="result"/> with one extra diagnostic appended.</summary>
    private static GradingResult WithExtraDiagnostic(GradingResult result, GradingDiagnostic diagnostic)
    {
        string[] diagnostics = result.Diagnostics.Concat(new[] { diagnostic.Message }).ToArray();
        GradingDiagnostic[] structured = result.StructuredDiagnostics.Concat(new[] { diagnostic }).ToArray();

        return new GradingResult(
            result.Vertices,
            result.VertexCount,
            result.Faces,
            result.FaceCount,
            result.CutVolume,
            result.FillVolume,
            result.DaylightVertices,
            result.DaylightVertexCount,
            result.OutputPolylines,
            diagnostics,
            result.PatchSummaries,
            structured);
    }
}
