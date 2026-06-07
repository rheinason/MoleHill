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

        var constraints = new List<SurfaceRemesher.ConstraintPolyline>(
            hardConstraints.Count + pathConstraintSet.Constraints.Length);
        constraints.AddRange(hardConstraints);
        constraints.AddRange(pathConstraintSet.Constraints);

        diagnostics.AddInformation(
            "grade_path.topology_mode.constraint_insertion",
            $"Grade Path topology mode: constraint insertion via constraint-first rebuild ({pathConstraintSet.Constraints.Length:N0} grading constraints + {hardConstraints.Count:N0} hard constraints).",
            operation: "Grade Path");
        diagnostics.AddInformation(
            "grade_path.topology.tiny_boundary_loops_not_needed",
            "Grade Path tiny branched boundary loop repair was not needed by constraint-first topology.",
            operation: "Grade Path");

        return ConstraintFirstGradingEngine.TryBuild(
            "Grade Path",
            vertices,
            vertexCount,
            faces,
            faceCount,
            constraints,
            pathConstraintSet.SuggestedEdgeLength,
            tolerance,
            (topologyVertices, topologyVertexCount, topologyFaces, topologyFaceCount) =>
                ApplyGradingZ(
                    topologyVertices,
                    topologyVertexCount,
                    topologyFaces,
                    topologyFaceCount,
                    paths,
                    hardConstraints,
                    out _),
            outputPolylines,
            BuildPathPatchSummaries(paths),
            diagnostics.ToMessages(),
            diagnostics.ToStructuredDiagnostics(),
            appendOutputDiagnostics: null,
            out _,
            out errorMessage);
    }

    private static bool TryFindClosestClosedLoopLocation(
        double[] loopXy,
        int vertexCount,
        double px,
        double py,
        out ClosestClosedLoopLocation closest)
    {
        closest = default;
        if (vertexCount < 2)
            return false;

        double bestDistSq = double.MaxValue;
        bool found = false;
        for (int i = 0; i < vertexCount; i++)
        {
            int next = (i + 1) % vertexCount;
            double ax = loopXy[i * 2];
            double ay = loopXy[(i * 2) + 1];
            double bx = loopXy[next * 2];
            double by = loopXy[(next * 2) + 1];
            double abx = bx - ax;
            double aby = by - ay;
            double lengthSq = (abx * abx) + (aby * aby);
            double t = lengthSq > 1e-12
                ? Math.Clamp((((px - ax) * abx) + ((py - ay) * aby)) / lengthSq, 0.0, 1.0)
                : 0.0;
            double qx = ax + (abx * t);
            double qy = ay + (aby * t);
            double dx = px - qx;
            double dy = py - qy;
            double distSq = (dx * dx) + (dy * dy);
            if (distSq >= bestDistSq)
                continue;

            bestDistSq = distSq;
            closest = new ClosestClosedLoopLocation(i, t, Math.Sqrt(distSq));
            found = true;
        }

        return found;
    }
}
