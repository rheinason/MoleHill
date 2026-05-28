using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

internal static class GradingTopologyDiagnostics
{
    public static GradingDiagnostic BuildMeshSummary(
        string code,
        string operationLabel,
        int inputVertexCount,
        int inputFaceCount,
        int outputVertexCount,
        int outputFaceCount,
        int[] outputFaces,
        string? operation = null,
        int? targetIndex = null)
    {
        MeshTopologyValidator.BoundaryGraphAnalysis analysis = MeshTopologyValidator.AnalyzeBoundaryGraph(outputFaces, outputFaceCount);
        GradingDiagnosticSeverity severity =
            analysis.NonManifoldEdgeCount > 0 ||
            analysis.HasOpenBoundaryChains ||
            analysis.BoundaryComponentCount > 1
                ? GradingDiagnosticSeverity.Warning
                : GradingDiagnosticSeverity.Information;

        return new GradingDiagnostic(
            severity,
            code,
            FormatMeshSummaryMessage(
                operationLabel,
                inputVertexCount,
                inputFaceCount,
                outputVertexCount,
                outputFaceCount,
                analysis),
            operation,
            targetIndex);
    }

    public static string BuildMeshSummaryMessage(
        string operationLabel,
        int inputVertexCount,
        int inputFaceCount,
        int outputVertexCount,
        int outputFaceCount,
        int[] outputFaces)
    {
        MeshTopologyValidator.BoundaryGraphAnalysis analysis = MeshTopologyValidator.AnalyzeBoundaryGraph(outputFaces, outputFaceCount);
        return FormatMeshSummaryMessage(
            operationLabel,
            inputVertexCount,
            inputFaceCount,
            outputVertexCount,
            outputFaceCount,
            analysis);
    }

    private static string FormatMeshSummaryMessage(
        string operationLabel,
        int inputVertexCount,
        int inputFaceCount,
        int outputVertexCount,
        int outputFaceCount,
        MeshTopologyValidator.BoundaryGraphAnalysis analysis)
    {
        return
            $"{operationLabel} topology summary: {inputVertexCount:N0} verts/{inputFaceCount:N0} faces -> {outputVertexCount:N0} verts/{outputFaceCount:N0} faces; " +
            $"boundary edges={analysis.BoundaryEdgeCount:N0}, boundary vertices={analysis.BoundaryVertexCount:N0}, boundary components={analysis.BoundaryComponentCount:N0}, " +
            $"open chains={analysis.HasOpenBoundaryChains}, nonmanifold edges={analysis.NonManifoldEdgeCount:N0}.";
    }
}
