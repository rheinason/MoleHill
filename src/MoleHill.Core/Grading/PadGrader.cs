using TriangleNet;
using TriangleNet.Geometry;
using TriangleNet.Meshing;
using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

/// <summary>
/// Grades a terrain mesh by assigning a planar finished surface inside
/// boundary curves, with controlled slope transitions.
/// Re-triangulates the entire mesh with pad boundaries as constrained edges.
/// </summary>
public static partial class PadGrader
{
    public const double DefaultStitchApronDistance = 0.0;

    /// <summary>
    /// Apply pad grading to a terrain mesh.
    /// Each pad carries its own slope angle and max distance.
    /// Later pads in the array override earlier ones in overlapping zones.
    /// </summary>
    public static GradingResult? Grade(
        double[] vertices, int vertexCount,
        int[] faces, int faceCount,
        PadBoundary[] pads,
        LockCurve[]? lockCurves,
        out string? errorMessage,
        double modelTolerance = GradingTolerances.DefaultModelTolerance)
    {
        return Grade(
            vertices,
            vertexCount,
            faces,
            faceCount,
            pads,
            lockCurves,
            out errorMessage,
            out _,
            modelTolerance,
            terrainDetailSize: 0.0);
    }

    public static GradingResult? Grade(
        double[] vertices, int vertexCount,
        int[] faces, int faceCount,
        PadBoundary[] pads,
        LockCurve[]? lockCurves,
        out string? errorMessage,
        out IReadOnlyList<OutputPolyline> failureOutputPolylines,
        double modelTolerance = GradingTolerances.DefaultModelTolerance,
        double terrainDetailSize = 0.0)
    {
        errorMessage = null;
        failureOutputPolylines = Array.Empty<OutputPolyline>();
        lockCurves ??= Array.Empty<LockCurve>();

        if (!GradingInputValidator.ValidateTerrainMesh(vertices, vertexCount, faces, faceCount, out errorMessage))
            return null;

        if (!GradingInputValidator.ValidateLockCurves(lockCurves, out errorMessage))
            return null;

        if (!ValidatePads(pads, out errorMessage))
            return null;

        pads = OrderPadsForOwnership(pads);

        GradingResult? rebuilt = GradeWithConstraintFirstTopology(
            vertices,
            vertexCount,
            faces,
            faceCount,
            pads,
            lockCurves,
            modelTolerance,
            terrainDetailSize,
            out failureOutputPolylines,
            out errorMessage);

        if (rebuilt != null &&
            ShouldPreferProtectedPadLocalRefinement(
                pads,
                modelTolerance,
                vertexCount,
                faceCount,
                rebuilt,
                out string protectedPadRefinementReason))
        {
            GradingResult? localRefinement = GradeWithRefinedZOnlyFallback(
                vertices,
                vertexCount,
                faces,
                faceCount,
                pads,
                lockCurves,
                modelTolerance,
                protectedPadRefinementReason);
            if (localRefinement != null)
                return localRefinement;
        }

        if (rebuilt == null)
        {
            string? constraintFirstError = errorMessage;
            GradingResult? localRefinementFallback = GradeWithRefinedZOnlyFallback(
                vertices,
                vertexCount,
                faces,
                faceCount,
                pads,
                lockCurves,
                modelTolerance,
                constraintFirstError ?? "unknown");
            if (localRefinementFallback != null)
            {
                errorMessage = null;
                return localRefinementFallback;
            }

            errorMessage = constraintFirstError;
        }

        if (rebuilt == null && string.IsNullOrWhiteSpace(errorMessage))
            errorMessage = "Grade Pad constraint-first rebuild failed.";

        if (rebuilt != null)
            errorMessage = null;

        return rebuilt;
    }

    private static bool ShouldPreferProtectedPadLocalRefinement(
        PadBoundary[] pads,
        double modelTolerance,
        int inputVertexCount,
        int inputFaceCount,
        GradingResult result,
        out string reason)
    {
        reason = string.Empty;
        double tolerance = GradingTolerances.ModelToleranceOrDefault(modelTolerance);
        bool hasProtectedPad = pads.Any(pad => pad.StitchApronDistance > tolerance * 4.0);
        if (!hasProtectedPad)
            return false;

        if (result.VertexCount <= inputVertexCount || result.FaceCount <= inputFaceCount)
        {
            reason = "constraint-first protected-pad topology did not add enough transition topology";
            return true;
        }

        if (pads.Length == 1 &&
            result.Diagnostics.Any(static diagnostic => diagnostic.Contains("batter slope warning", StringComparison.OrdinalIgnoreCase)))
        {
            reason = "constraint-first protected-pad topology produced excessive slope deviation";
            return true;
        }

        return false;
    }


    private sealed class PatchMeshResult
    {
        public required double[] Vertices { get; init; }
        public required int VertexCount { get; init; }
        public required int[] Faces { get; init; }
        public required int FaceCount { get; init; }
        public double[]? StitchLoopXy { get; init; }
        public int CornerConstraintCount { get; init; }
        public bool CornerConstraintsRejected { get; set; }
    }

    private readonly record struct OrderedPad(PadBoundary Pad, int OriginalIndex, double Priority);

    private static PadBoundary[] OrderPadsForOwnership(PadBoundary[] pads)
    {
        if (pads.Length <= 1)
            return pads;

        return pads
            .Select((pad, index) => new OrderedPad(pad, index, ComputePadOwnershipPriority(pad)))
            .OrderBy(entry => entry.Priority)
            .ThenBy(entry => entry.OriginalIndex)
            .Select(entry => entry.Pad)
            .ToArray();
    }

    private static double ComputePadOwnershipPriority(PadBoundary pad)
    {
        double sumX = 0.0;
        double sumY = 0.0;
        for (int i = 0; i < pad.VertexCount; i++)
        {
            sumX += pad.XyVertices[i * 2];
            sumY += pad.XyVertices[i * 2 + 1];
        }

        double centroidX = sumX / pad.VertexCount;
        double centroidY = sumY / pad.VertexCount;
        return pad.EvaluateZ(centroidX, centroidY);
    }


}
