using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

public static partial class PathGrader
{
    /// <summary>
    /// Re-triangulate with road edges as constrained segments, then grade Z.
    /// </summary>
    private static GradingResult? GradeWithEdges(
        double[] vertices, int vertexCount,
        int[] faces, int faceCount,
        PathDefinition[] paths,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> hardConstraints,
        double modelTolerance,
        out string? errorMessage,
        out bool fatalError)
    {
        errorMessage = null;
        fatalError = false;

        PreparedBarriers roadBarriers = GradingBarriers.Build(hardConstraints);
        var barrierScratch = new SpatialHashGrid2D.QueryScratch(Math.Max(roadBarriers.Segments.Length, 1));
        var barrierCandidates = new List<int>(8);

        if (roadBarriers.Segments.Length > 0)
        {
            double endpointTouchTolerance = GradingTolerances.ModelToleranceOrDefault(modelTolerance);
            foreach (var path in paths)
            {
                double halfWidth = path.Width * 0.5;
                int vertexCountOnPath = path.VertexCount;
                for (int i = 0; i < vertexCountOnPath - 1; i++)
                {
                    double endpointCapTolerance = Math.Max(endpointTouchTolerance, halfWidth);
                    double startTouchTolerance = i == 0 ? endpointCapTolerance : endpointTouchTolerance;
                    double endTouchTolerance = i == vertexCountOnPath - 2 ? endpointCapTolerance : endpointTouchTolerance;
                    double cx0 = path.XyVertices[i * 2];
                    double cy0 = path.XyVertices[(i * 2) + 1];
                    double cx1 = path.XyVertices[(i + 1) * 2];
                    double cy1 = path.XyVertices[((i + 1) * 2) + 1];
                    ComputeDirection(path.XyVertices, vertexCountOnPath, i, out double dx, out double dy);
                    double roadPx = -dy * halfWidth;
                    double roadPy = dx * halfWidth;

                    if (GradingBarriers.IsInteriorCrossedByBarrier(roadBarriers, cx0, cy0, cx1, cy1, startTouchTolerance, endTouchTolerance, barrierScratch, barrierCandidates) ||
                        GradingBarriers.IsInteriorCrossedByBarrier(roadBarriers, cx0 + roadPx, cy0 + roadPy, cx1 + roadPx, cy1 + roadPy, startTouchTolerance, endTouchTolerance, barrierScratch, barrierCandidates) ||
                        GradingBarriers.IsInteriorCrossedByBarrier(roadBarriers, cx0 - roadPx, cy0 - roadPy, cx1 - roadPx, cy1 - roadPy, startTouchTolerance, endTouchTolerance, barrierScratch, barrierCandidates))
                    {
                        errorMessage = "Road edge crosses a hard constraint. Redesign the path or convert the conflicting constraint to a contour.";
                        fatalError = true;
                        return null;
                    }
                }
            }
        }

        return GradeWithConstraintInsertionTopology(
            vertices,
            vertexCount,
            faces,
            faceCount,
            paths,
            hardConstraints,
            modelTolerance,
            out errorMessage);
    }

    private static GradingResult? GradeWithConstraintInsertionTopology(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PathDefinition[] paths,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> hardConstraints,
        double modelTolerance,
        out string? errorMessage)
    {
        double tolerance = GradingTolerances.ModelToleranceOrDefault(modelTolerance);
        var diagnostics = new GradingDiagnosticCollector();
        var outputPolylines = new List<OutputPolyline>(paths.Length * 2);
        ConstraintSet pathConstraintSet = BuildConstraintSetInternal(
            vertices,
            vertexCount,
            faces,
            faceCount,
            paths,
            tolerance,
            hardConstraints,
            outputPolylines,
            diagnostics);

        // Local topology insertion: split only the terrain faces the road constraints cross and keep
        // the rest of the terrain intact. This preserves the surrounding mesh density and avoids the
        // whole-terrain re-triangulation (radial spokes) of the constraint-first rebuild.
        if (!MeshConstraintTopologyInserter.TryInsert(
                vertices,
                vertexCount,
                faces,
                faceCount,
                pathConstraintSet.Constraints,
                tolerance,
                out double[] topologyVertices,
                out int topologyVertexCount,
                out int[] topologyFaces,
                out int topologyFaceCount,
                out string? topologyError))
        {
            errorMessage = topologyError ?? "Topology-preserving path insertion failed.";
            return null;
        }

        double[] gradedVertices = ApplyGradingZ(
            topologyVertices,
            topologyVertexCount,
            topologyFaces,
            topologyFaceCount,
            paths,
            hardConstraints,
            out _);
        if (MeshTopologyOperations.TryFillSmallBranchedBoundaryLoops(
                gradedVertices,
                topologyVertexCount,
                topologyFaces,
                topologyFaceCount,
                tolerance,
                out int[] repairedTopologyFaces,
                out int repairedTopologyFaceCount,
                out int repairedBoundaryLoopCount))
        {
            topologyFaces = repairedTopologyFaces;
            topologyFaceCount = repairedTopologyFaceCount;
            diagnostics.AddInformation(
                "grade_path.topology.tiny_boundary_loops_filled",
                $"Grade Path topology repair filled {repairedBoundaryLoopCount:N0} tiny branched boundary loop(s).",
                operation: "grade_path");
        }

        var outXy = new double[topologyVertexCount * 2];
        var origZ = new double[topologyVertexCount];
        var newZ = new double[topologyVertexCount];
        for (int i = 0; i < topologyVertexCount; i++)
        {
            outXy[i * 2] = topologyVertices[i * 3];
            outXy[i * 2 + 1] = topologyVertices[i * 3 + 1];
            origZ[i] = topologyVertices[i * 3 + 2];
            newZ[i] = gradedVertices[i * 3 + 2];
        }

        diagnostics.AddInformation(
            "grade_path.topology_mode.constraint_insertion",
            $"Grade Path topology mode: constraint insertion ({vertexCount:N0} verts/{faceCount:N0} faces -> {topologyVertexCount:N0} verts/{topologyFaceCount:N0} faces).",
            operation: "grade_path");
        diagnostics.Add(GradingTopologyDiagnostics.BuildMeshSummary(
            "grade_path.topology.summary",
            "Grade Path",
            vertexCount,
            faceCount,
            topologyVertexCount,
            topologyFaceCount,
            topologyFaces,
            operation: "grade_path"));
        errorMessage = null;
        return BuildResult(
            outXy,
            origZ,
            newZ,
            gradedVertices,
            topologyVertexCount,
            topologyFaces,
            topologyFaceCount,
            outputPolylines,
            BuildPathPatchSummaries(paths),
            diagnostics.ToMessages(),
            diagnostics.ToStructuredDiagnostics());
    }
}
