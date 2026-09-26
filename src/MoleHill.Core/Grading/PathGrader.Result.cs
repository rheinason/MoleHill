using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

public static partial class PathGrader
{
    public static GradingResult? TryRepairRejectedStitchedResult(GradingResult result, out string? diagnostic)
    {
        diagnostic = null;
        if (result.VertexCount <= 0 || result.FaceCount <= 0)
            return null;

        MeshArtifactCleaner.CleanupResult cleanup = MeshArtifactCleaner.Clean(
            result.Vertices,
            result.VertexCount,
            result.Faces,
            result.FaceCount,
            new MeshArtifactCleaner.Options(
                MinComponentFaceCount: 1,
                MinComponentAreaRatio: 0.0,
                MinFaceAngleDegrees: 1.0,
                MaxAspectRatio: 100.0,
                KeepLargestComponentOnly: true));

        if (cleanup.RemovedFaceCount <= 0)
        {
            diagnostic = $"Grade Path stitched repair found no removable detached components (components {cleanup.Before.ComponentCount}, boundary edges {cleanup.Before.BoundaryEdgeCount}).";
            return null;
        }

        diagnostic =
            $"Grade Path stitched repair kept the largest connected component only (components {cleanup.Before.ComponentCount}->{cleanup.After.ComponentCount}, removed components={cleanup.RemovedComponentCount}, removed faces={cleanup.RemovedFaceCount}, boundary edges {cleanup.Before.BoundaryEdgeCount}->{cleanup.After.BoundaryEdgeCount}).";

        string[] diagnostics = result.Diagnostics.Count == 0
            ? new[] { diagnostic }
            : result.Diagnostics.Concat(new[] { diagnostic }).ToArray();
        GradingDiagnostic[] structuredDiagnostics = result.StructuredDiagnostics
            .Concat(new[]
            {
                GradingDiagnostic.Warning(
                    "grade_path.stitched_repair.detached_components_removed",
                    diagnostic,
                    operation: "grade_path")
            })
            .ToArray();

        return new GradingResult(
            cleanup.Vertices,
            cleanup.VertexCount,
            cleanup.Faces,
            cleanup.FaceCount,
            result.CutVolume,
            result.FillVolume,
            result.DaylightVertices,
            result.DaylightVertexCount,
            result.OutputPolylines,
            diagnostics,
            result.PatchSummaries,
            structuredDiagnostics);
    }

    private static GradingResult BuildResult(
        double[] outXy, double[] origZ, double[] newZ,
        double[] finalVerts, int vertCount,
        int[] finalFaces, int faceCount,
        IReadOnlyList<OutputPolyline>? outputPolylines = null,
        IReadOnlyList<GradingPatch>? patchSummaries = null,
        IReadOnlyList<string>? diagnostics = null,
        IReadOnlyList<GradingDiagnostic>? structuredDiagnostics = null)
    {
        return GradingResultBuilder.BuildFromComponents(
            outXy,
            origZ,
            newZ,
            finalVerts, vertCount,
            finalFaces, faceCount,
            outputPolylines,
            diagnostics,
            patchSummaries: patchSummaries,
            structuredDiagnostics: structuredDiagnostics);
    }
}
