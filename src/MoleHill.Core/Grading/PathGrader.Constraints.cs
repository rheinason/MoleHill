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
        if (!TryValidateConstraintGenerationInputs(vertices, vertexCount, faces, faceCount, paths, out ConstraintSet? invalidResult))
            return invalidResult!;

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
        if (!TryValidateConstraintGenerationInputs(vertices, vertexCount, faces, faceCount, paths, out ConstraintSet? invalidResult))
            return invalidResult!;

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
        if (!TryValidateConstraintGenerationInputs(vertices, vertexCount, faces, faceCount, paths, out ConstraintSet? invalidResult))
            return invalidResult!;

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

    private static bool TryValidateConstraintGenerationInputs(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PathDefinition[] paths,
        out ConstraintSet? invalidResult)
    {
        invalidResult = null;

        if (!ValidateConstraintGenerationTopology(vertices, vertexCount, faces, faceCount, out string? topologyError))
        {
            invalidResult = CreateInvalidConstraintSet(
                "grade_path.input.invalid_topology",
                topologyError ?? "Invalid topology for Grade Path constraint generation.");
            return false;
        }

        if (!GradingInputValidator.ValidatePathDefinitions(paths, out string? pathError))
        {
            invalidResult = CreateInvalidConstraintSet(
                "grade_path.input.invalid_path",
                pathError ?? "Invalid path definition.");
            return false;
        }

        return true;
    }

    private static bool ValidateConstraintGenerationTopology(
        double[]? vertices,
        int vertexCount,
        int[]? faces,
        int faceCount,
        out string? errorMessage)
    {
        errorMessage = null;

        if (!GradingInputValidator.ValidateVertexArray(vertices, vertexCount, "Topology", out errorMessage))
            return false;

        if (vertexCount < 2)
        {
            errorMessage = "Topology must contain at least 2 vertices.";
            return false;
        }

        if (faces == null)
        {
            errorMessage = "Topology faces are required.";
            return false;
        }

        if (faceCount < 0)
        {
            errorMessage = "Topology faceCount cannot be negative.";
            return false;
        }

        long requiredFaceValues = (long)faceCount * 3;
        if (requiredFaceValues > int.MaxValue)
        {
            errorMessage = "Topology face array is too large to validate safely.";
            return false;
        }

        if (faces.Length < requiredFaceValues)
        {
            errorMessage = "Topology face array is shorter than faceCount requires.";
            return false;
        }

        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            int a = faces[faceIndex * 3];
            int b = faces[faceIndex * 3 + 1];
            int c = faces[faceIndex * 3 + 2];
            if ((uint)a >= (uint)vertexCount ||
                (uint)b >= (uint)vertexCount ||
                (uint)c >= (uint)vertexCount)
            {
                errorMessage = $"Topology face {faceIndex} references a vertex outside the topology vertex range.";
                return false;
            }

            if (a == b || b == c || c == a)
            {
                errorMessage = $"Topology face {faceIndex} is degenerate.";
                return false;
            }
        }

        return true;
    }

    private static ConstraintSet CreateInvalidConstraintSet(string code, string message)
    {
        GradingDiagnostic diagnostic = GradingDiagnostic.Warning(
            code,
            message,
            operation: "grade_path");

        return new ConstraintSet
        {
            Constraints = Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
            SuggestedEdgeLength = 0.0,
            StructuredDiagnostics = [diagnostic]
        };
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
                    operation: "grade_path",
                    targetIndex: pathIndex);
                string? leftQualityWarning = BuildPathSectionQualityWarning(pathIndex, "left", leftStatuses, repairedLeftSections);
                if (leftQualityWarning != null)
                {
                    diagnostics.AddWarning(
                        "grade_path.shoulder.quality.left",
                        leftQualityWarning,
                        operation: "grade_path",
                        targetIndex: pathIndex);
                }

                diagnostics.AddInformation(
                    "grade_path.section_status.right",
                    BuildPathSectionStatusDiagnostic(pathIndex, "right", rightStatuses, repairedRightSections),
                    operation: "grade_path",
                    targetIndex: pathIndex);
                string? rightQualityWarning = BuildPathSectionQualityWarning(pathIndex, "right", rightStatuses, repairedRightSections);
                if (rightQualityWarning != null)
                {
                    diagnostics.AddWarning(
                        "grade_path.shoulder.quality.right",
                        rightQualityWarning,
                        operation: "grade_path",
                        targetIndex: pathIndex);
                }
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
                        operation: "grade_path",
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
                    dedupTol,
                    preparedBarriers,
                    barrierScratch,
                    barrierCandidates);
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
