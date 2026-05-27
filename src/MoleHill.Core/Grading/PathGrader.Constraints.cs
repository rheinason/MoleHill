using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

public static partial class PathGrader
{
    public static ConstraintSet CreateConstraints(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PathDefinition[] paths,
        double tolerance)
    {
        return BuildConstraintSetInternal(
            vertices,
            vertexCount,
            faces,
            faceCount,
            paths,
            tolerance,
            Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
            outputPolylines: null);
    }

    public static ConstraintSet CreateRemeshFallbackConstraints(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PathDefinition[] paths,
        double tolerance)
    {
        return BuildConstraintSetInternal(
            vertices,
            vertexCount,
            faces,
            faceCount,
            paths,
            tolerance,
            Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
            outputPolylines: null,
            includeStationConstraints: false);
    }

    public static ConstraintSet CreateRoadEdgeRemeshFallbackConstraints(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PathDefinition[] paths,
        double tolerance)
    {
        return BuildConstraintSetInternal(
            vertices,
            vertexCount,
            faces,
            faceCount,
            paths,
            tolerance,
            Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
            outputPolylines: null,
            includeStationConstraints: false,
            includeShoulderConstraints: false,
            resamplePrimaryRails: true);
    }

    private static ConstraintSet BuildConstraintSetInternal(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PathDefinition[] paths,
        double tolerance,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> barrierConstraints,
        List<OutputPolyline>? outputPolylines,
        GradingDiagnosticCollector? diagnostics = null,
        bool includeStationConstraints = true,
        bool includeShoulderConstraints = true,
        bool resamplePrimaryRails = false)
    {
        double dedupTol = GradingTolerances.ModelToleranceOrDefault(tolerance);
        bool hasBoundaryLoop = MeshBoundaryLoopBuilder.TryBuildBoundaryLoop(vertices, faces, faceCount, out var boundaryLoop, out int boundaryVertexCount);
        var constraints = new List<SurfaceRemesher.ConstraintPolyline>(paths.Length * 5);
        double suggestedEdgeLength = double.MaxValue;
        var coincidenceSnapper = new ConstraintCoincidenceSnapper(
            vertices,
            vertexCount,
            faces,
            faceCount,
            Math.Max(dedupTol, GradingTolerances.ConstraintSnapTolerance(dedupTol)));
        var faceGrid = new TerrainFaceGrid(vertices, vertexCount, faces, faceCount);
        PreparedBarriers preparedBarriers = GradingBarriers.Build(barrierConstraints);
        var barrierScratch = new SpatialHashGrid2D.QueryScratch(Math.Max(preparedBarriers.Segments.Length, 1));
        var barrierCandidates = new List<int>(8);

        for (int pathIndex = 0; pathIndex < paths.Length; pathIndex++)
        {
            PathDefinition path = paths[pathIndex];
            double halfWidth = path.Width * 0.5;
            BuildPathSections(
                path,
                ComputePathSectionSearchDistance(path, faceGrid.InterpolateZ),
                faceGrid.InterpolateZ,
                preparedBarriers,
                barrierScratch,
                barrierCandidates,
                hasBoundaryLoop,
                boundaryLoop,
                boundaryVertexCount,
                dedupTol,
                out ConstraintPath constraintPath,
                out double[] leftRoadXy,
                out double[] rightRoadXy,
                out double[] leftShoulderXy,
                out double[] rightShoulderXy,
                out double[] leftShoulderZ,
                out double[] rightShoulderZ,
                out PathSectionResolutionStatus[] leftStatuses,
                out PathSectionResolutionStatus[] rightStatuses,
                out int repairedLeftSections,
                out int repairedRightSections,
                out double maxInfluence);

            if (diagnostics != null)
            {
                diagnostics.AddInformation(
                    "grade_path.section_status.left",
                    BuildPathSectionStatusDiagnostic(pathIndex, "left", leftStatuses, repairedLeftSections),
                    operation: "Grade Path",
                    targetIndex: pathIndex);
                diagnostics.AddInformation(
                    "grade_path.section_status.right",
                    BuildPathSectionStatusDiagnostic(pathIndex, "right", rightStatuses, repairedRightSections),
                    operation: "Grade Path",
                    targetIndex: pathIndex);
            }

            double actualReach = Math.Max(0.0, maxInfluence - halfWidth);
            double segmentLength = ComputeConstraintSegmentLength(path, Math.Max(actualReach, path.Width));
            suggestedEdgeLength = Math.Min(suggestedEdgeLength, segmentLength);
            bool[]? keepStationConstraints = null;
            if (includeStationConstraints)
            {
                keepStationConstraints = BuildStationConstraintSelection(
                    constraintPath,
                    leftStatuses,
                    rightStatuses,
                    segmentLength);

                if (diagnostics != null)
                {
                    int keptStationCount = 0;
                    for (int i = 0; i < keepStationConstraints.Length; i++)
                    {
                        if (keepStationConstraints[i])
                            keptStationCount++;
                    }

                    diagnostics.AddInformation(
                        "grade_path.station_constraints",
                        $"Grade Path[{pathIndex}] station constraints: kept={keptStationCount}, sampled={constraintPath.VertexCount}.",
                        operation: "Grade Path",
                        targetIndex: pathIndex);
                }
            }

            if (resamplePrimaryRails)
            {
                constraintPath = ResampleConstraintPath(constraintPath, segmentLength, dedupTol);
                leftRoadXy = ResampleConstraintRow(leftRoadXy, constraintPath.XyVertices, segmentLength, dedupTol);
                rightRoadXy = ResampleConstraintRow(rightRoadXy, constraintPath.XyVertices, segmentLength, dedupTol);
            }

            AddBoundaryClippedConstraintRuns(
                constraints,
                constraintPath.XyVertices,
                constraintPath.ZValues,
                constraintPath.VertexCount,
                hasBoundaryLoop,
                boundaryLoop,
                boundaryVertexCount,
                dedupTol);
            AddBoundaryClippedConstraintRuns(
                constraints,
                leftRoadXy,
                constraintPath.ZValues,
                constraintPath.VertexCount,
                hasBoundaryLoop,
                boundaryLoop,
                boundaryVertexCount,
                dedupTol);
            AddBoundaryClippedConstraintRuns(
                constraints,
                rightRoadXy,
                constraintPath.ZValues,
                constraintPath.VertexCount,
                hasBoundaryLoop,
                boundaryLoop,
                boundaryVertexCount,
                dedupTol);

            if (outputPolylines != null)
            {
                var leftEdgeXyz = new double[constraintPath.VertexCount * 3];
                var rightEdgeXyz = new double[constraintPath.VertexCount * 3];
                for (int i = 0; i < constraintPath.VertexCount; i++)
                {
                    leftEdgeXyz[i * 3] = leftRoadXy[i * 2];
                    leftEdgeXyz[(i * 3) + 1] = leftRoadXy[(i * 2) + 1];
                    leftEdgeXyz[(i * 3) + 2] = constraintPath.ZValues[i];
                    rightEdgeXyz[i * 3] = rightRoadXy[i * 2];
                    rightEdgeXyz[(i * 3) + 1] = rightRoadXy[(i * 2) + 1];
                    rightEdgeXyz[(i * 3) + 2] = constraintPath.ZValues[i];
                }

                outputPolylines.Add(new OutputPolyline(leftEdgeXyz, constraintPath.VertexCount));
                outputPolylines.Add(new OutputPolyline(rightEdgeXyz, constraintPath.VertexCount));
            }

            if (includeShoulderConstraints && HasDistinctShoulderSamples(leftRoadXy, leftShoulderXy, constraintPath.VertexCount, dedupTol))
            {
                foreach (var run in CreateClippedRuns(
                             leftShoulderXy,
                             leftShoulderZ,
                             constraintPath.VertexCount,
                             hasBoundaryLoop,
                             boundaryLoop,
                             boundaryVertexCount,
                             dedupTol,
                             preparedBarriers,
                             barrierScratch,
                             barrierCandidates))
                    AddConstraintPolyline(constraints, run.XyVertices, run.ZValues, run.VertexCount);

            }

            if (includeShoulderConstraints && HasDistinctShoulderSamples(rightRoadXy, rightShoulderXy, constraintPath.VertexCount, dedupTol))
            {
                foreach (var run in CreateClippedRuns(
                             rightShoulderXy,
                             rightShoulderZ,
                             constraintPath.VertexCount,
                             hasBoundaryLoop,
                             boundaryLoop,
                             boundaryVertexCount,
                             dedupTol,
                             preparedBarriers,
                             barrierScratch,
                             barrierCandidates))
                    AddConstraintPolyline(constraints, run.XyVertices, run.ZValues, run.VertexCount);

            }

            if (includeStationConstraints)
            {
                AddPathStationConstraints(
                    constraints,
                    constraintPath,
                    leftRoadXy,
                    rightRoadXy,
                    leftShoulderXy,
                    rightShoulderXy,
                    leftShoulderZ,
                    rightShoulderZ,
                    hasBoundaryLoop,
                    boundaryLoop,
                    boundaryVertexCount,
                    keepStationConstraints,
                    constraintPath.VertexCount,
                    dedupTol);
            }
        }

        for (int i = 0; i < constraints.Count; i++)
            constraints[i] = coincidenceSnapper.SnapConstraintPolyline(constraints[i]);

        return new ConstraintSet
        {
            Constraints = constraints.ToArray(),
            SuggestedEdgeLength = suggestedEdgeLength < double.MaxValue ? suggestedEdgeLength : 0.0,
            StructuredDiagnostics = diagnostics?.ToStructuredDiagnostics() ?? Array.Empty<GradingDiagnostic>()
        };
    }
}
