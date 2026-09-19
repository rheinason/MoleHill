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
        GradingDiagnosticSeverity severity = IsHealthy(analysis)
            ? GradingDiagnosticSeverity.Information
            : GradingDiagnosticSeverity.Warning;

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
            $"open chains={analysis.HasOpenBoundaryChains}, nonmanifold edges={analysis.NonManifoldEdgeCount:N0}, topology healthy={IsHealthy(analysis)}.";
    }

    private static bool IsHealthy(MeshTopologyValidator.BoundaryGraphAnalysis analysis)
    {
        return analysis.NonManifoldEdgeCount == 0 &&
            !analysis.HasOpenBoundaryChains &&
            analysis.BoundaryComponentCount == 1;
    }

    /// <summary>
    /// The output contract for a grading tier: it may not leave the terrain's topology worse than it
    /// found it. Returns false and describes the damage when it does.
    ///
    /// The bar is deliberately "not worse", not <see cref="IsHealthy"/>. A terrain may legitimately
    /// carry an interior hole — a courtyard, an excluded region — and such a terrain has more than one
    /// boundary component before any grading runs. Demanding absolute health would reject every grade
    /// on it. What is never legitimate is a grade that *adds* a loop, opens a naked-edge chain, or
    /// tears the mesh non-manifold.
    ///
    /// This exists because the last tier had no floor at all. When the tiers above it deferred, the
    /// constraint-insertion tier returned whatever it produced — measured as unhealthy in its own
    /// summary diagnostic, and shipped anyway with a null error. Downstream that reads as a successful
    /// grade, and the retaining-wall stage then rebuilds from a broken mesh and loses the terrain.
    /// </summary>
    public static bool IsNotWorseThanInput(
        int[] inputFaces,
        int inputFaceCount,
        int[] outputFaces,
        int outputFaceCount,
        out string? damageMessage)
    {
        MeshTopologyValidator.BoundaryGraphAnalysis input =
            MeshTopologyValidator.AnalyzeBoundaryGraph(inputFaces, inputFaceCount);
        MeshTopologyValidator.BoundaryGraphAnalysis output =
            MeshTopologyValidator.AnalyzeBoundaryGraph(outputFaces, outputFaceCount);

        if (output.NonManifoldEdgeCount > input.NonManifoldEdgeCount)
        {
            damageMessage =
                $"non-manifold edges rose from {input.NonManifoldEdgeCount:N0} to {output.NonManifoldEdgeCount:N0}";
            return false;
        }

        if (output.HasOpenBoundaryChains && !input.HasOpenBoundaryChains)
        {
            damageMessage = "it opened naked-edge chains in a terrain that had none";
            return false;
        }

        if (output.BoundaryComponentCount > input.BoundaryComponentCount)
        {
            damageMessage =
                $"boundary loops rose from {input.BoundaryComponentCount:N0} to {output.BoundaryComponentCount:N0}";
            return false;
        }

        damageMessage = null;
        return true;
    }
}
