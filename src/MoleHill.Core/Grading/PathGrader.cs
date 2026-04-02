using System.Buffers;
using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

/// <summary>
/// Grades a terrain mesh along path curves (roads, sidewalks, etc.).
/// Adds road edges (path offset by half-width) as constrained edges,
/// re-triangulates, then grades Z: inside road = path Z, outside = slope transition.
/// Falls back to Z-only modification if triangulation fails.
/// </summary>
public static class PathGrader
{
    public sealed class ConstraintSet
    {
        public required SurfaceRemesher.ConstraintPolyline[] Constraints { get; init; }

        public required double SuggestedEdgeLength { get; init; }
    }

    private readonly record struct ConstraintPath(
        double[] XyVertices,
        double[] ZValues,
        int VertexCount,
        double[] TangentX,
        double[] TangentY);
    private readonly record struct ClosestPathLocation(
        int SegmentIndex,
        double SegmentT,
        double Distance,
        double PathZ,
        double SideSign,
        double ProjectedX,
        double ProjectedY,
        double DirectionX,
        double DirectionY);

    private readonly record struct PreparedPath(
        double HalfWidth,
        double SlopeRatio,
        double MaxDistance,
        double ShoulderDistance,
        double MaxInfluence,
        ConstraintPath SamplePath,
        double[] LeftReferenceDz,
        double[] RightReferenceDz,
        double MinX,
        double MaxX,
        double MinY,
        double MaxY);

    public sealed class PathDefinition
    {
        public double[] XyVertices { get; }
        public double[] ZValues { get; }
        public int VertexCount { get; }
        public double Width { get; }
        public double SlopeAngleDeg { get; }
        public double MaxDistance { get; }

        public PathDefinition(double[] xyVertices, double[] zValues, int vertexCount,
                              double width, double slopeAngleDeg = 33.0, double maxDistance = 0.0)
        {
            XyVertices = xyVertices;
            ZValues = zValues;
            VertexCount = vertexCount;
            Width = width;
            SlopeAngleDeg = Math.Max(0.1, Math.Min(89.9, slopeAngleDeg));
            MaxDistance = maxDistance;
        }
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
        out string? errorMessage)
    {
        return Grade(vertices, vertexCount, faces, faceCount, paths, Array.Empty<SurfaceRemesher.ConstraintPolyline>(), out errorMessage);
    }

    public static GradingResult? Grade(
        double[] vertices, int vertexCount,
        int[] faces, int faceCount,
        PathDefinition[] paths,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> hardConstraints,
        out string? errorMessage)
    {
        errorMessage = null;

        if (paths.Length == 0)
        {
            errorMessage = "No path definitions provided.";
            return null;
        }

        foreach (var path in paths)
        {
            if (path.VertexCount < 2)
            {
                errorMessage = "Each path must have at least 2 vertices.";
                return null;
            }
            if (path.Width <= 0)
            {
                errorMessage = "Path width must be positive.";
                return null;
            }
        }

        // Try re-triangulation with road edge constraints
        var result = GradeWithEdges(vertices, vertexCount, faces, faceCount, paths, hardConstraints, out errorMessage, out bool fatalError);
        if (result != null) return result;
        if (fatalError) return null;

        // Fallback: just modify Z of existing mesh
        errorMessage = null;
        return GradeZOnly(vertices, vertexCount, faces, faceCount, paths, hardConstraints, out errorMessage);
    }

    public static double[] ApplyGradingZ(double[] topologyVertices, int vertexCount, PathDefinition[] paths)
    {
        return ApplyGradingZ(topologyVertices, vertexCount, paths, out _);
    }

    public static double[] ApplyGradingZ(double[] topologyVertices, int vertexCount, PathDefinition[] paths, out int changedVertexCount)
    {
        return ApplyGradingZ(topologyVertices, vertexCount, paths, Array.Empty<SurfaceRemesher.ConstraintPolyline>(), out changedVertexCount);
    }

    public static double[] ApplyGradingZ(
        double[] topologyVertices,
        int vertexCount,
        PathDefinition[] paths,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> barrierConstraints,
        out int changedVertexCount)
    {
        var outXy = new double[vertexCount * 2];
        var origZ = new double[vertexCount];
        var newZ = new double[vertexCount];

        for (int i = 0; i < vertexCount; i++)
        {
            outXy[i * 2] = topologyVertices[i * 3];
            outXy[i * 2 + 1] = topologyVertices[i * 3 + 1];
            origZ[i] = topologyVertices[i * 3 + 2];
            newZ[i] = topologyVertices[i * 3 + 2];
        }

        ApplyPathGrading(paths, barrierConstraints, outXy, origZ, newZ, vertexCount);

        var gradedVertices = new double[vertexCount * 3];
        changedVertexCount = 0;
        for (int i = 0; i < vertexCount; i++)
        {
            gradedVertices[i * 3] = topologyVertices[i * 3];
            gradedVertices[i * 3 + 1] = topologyVertices[i * 3 + 1];
            gradedVertices[i * 3 + 2] = newZ[i];
            if (Math.Abs(newZ[i] - origZ[i]) > 1e-9)
                changedVertexCount++;
        }

        return gradedVertices;
    }

    public static ConstraintSet CreateConstraints(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PathDefinition[] paths,
        double tolerance)
    {
        const double minimumTolerance = 1e-3;
        double dedupTol = Math.Max(tolerance, minimumTolerance);
        bool hasBoundaryLoop = PadGrader.TryBuildBoundaryLoop(vertices, faces, faceCount, out var boundaryLoop, out int boundaryVertexCount);
        var constraints = new List<SurfaceRemesher.ConstraintPolyline>(paths.Length * 5);
        double suggestedEdgeLength = double.MaxValue;

        foreach (var path in paths)
        {
            double halfWidth = path.Width * 0.5;
            double shoulderDistance = ComputePathShoulderDistance(vertices, vertexCount, path);
            double segmentLength = ComputeConstraintSegmentLength(path, shoulderDistance);
            suggestedEdgeLength = Math.Min(suggestedEdgeLength, segmentLength);

            var constraintPath = BuildConstraintPolyline(path, segmentLength, dedupTol);
            ComputeInsideCornerGuideSuppression(
                constraintPath.XyVertices,
                constraintPath.VertexCount,
                out bool[] suppressLeftGuides,
                out bool[] suppressRightGuides);
            double guideSpacing = ComputeGuideSpacing(path.Width, shoulderDistance);
            bool[] keepLeftGuides = ComputeGuideSelection(constraintPath.XyVertices, constraintPath.VertexCount, guideSpacing, suppressLeftGuides);
            bool[] keepRightGuides = ComputeGuideSelection(constraintPath.XyVertices, constraintPath.VertexCount, guideSpacing, suppressRightGuides);
            AddConstraintPolyline(constraints, constraintPath.XyVertices, constraintPath.ZValues, constraintPath.VertexCount);

            int xyBufferLength = constraintPath.VertexCount * 2;
            var leftRoadXy = ArrayPool<double>.Shared.Rent(xyBufferLength);
            var rightRoadXy = ArrayPool<double>.Shared.Rent(xyBufferLength);
            double[]? leftShoulderXy = shoulderDistance > dedupTol ? ArrayPool<double>.Shared.Rent(xyBufferLength) : null;
            double[]? rightShoulderXy = shoulderDistance > dedupTol ? ArrayPool<double>.Shared.Rent(xyBufferLength) : null;

            try
            {
                for (int i = 0; i < constraintPath.VertexCount; i++)
                {
                    double cx = constraintPath.XyVertices[i * 2];
                    double cy = constraintPath.XyVertices[i * 2 + 1];
                    ComputeDirection(constraintPath.XyVertices, constraintPath.VertexCount, i, out double roadDx, out double roadDy);
                    GetConstraintPathTangent(constraintPath, i, out double shoulderDx, out double shoulderDy);

                    double roadPx = -roadDy * halfWidth;
                    double roadPy = roadDx * halfWidth;
                    leftRoadXy[i * 2] = cx + roadPx;
                    leftRoadXy[i * 2 + 1] = cy + roadPy;
                    rightRoadXy[i * 2] = cx - roadPx;
                    rightRoadXy[i * 2 + 1] = cy - roadPy;

                    if (leftShoulderXy != null && rightShoulderXy != null)
                    {
                        double shoulderOffset = halfWidth + shoulderDistance;
                        double shoulderPx = -shoulderDy * shoulderOffset;
                        double shoulderPy = shoulderDx * shoulderOffset;
                        leftShoulderXy[i * 2] = cx + shoulderPx;
                        leftShoulderXy[i * 2 + 1] = cy + shoulderPy;
                        rightShoulderXy[i * 2] = cx - shoulderPx;
                        rightShoulderXy[i * 2 + 1] = cy - shoulderPy;
                    }
                }

                AddConstraintPolyline(constraints, leftRoadXy, constraintPath.ZValues, constraintPath.VertexCount);
                AddConstraintPolyline(constraints, rightRoadXy, constraintPath.ZValues, constraintPath.VertexCount);

                if (leftShoulderXy != null)
                {
                    foreach (var run in CreateBoundaryClippedRuns(leftShoulderXy, constraintPath.ZValues, constraintPath.VertexCount, hasBoundaryLoop, boundaryLoop, boundaryVertexCount, dedupTol))
                        AddConstraintPolyline(constraints, run.XyVertices, run.ZValues, run.VertexCount);

                    AddShoulderGuideConstraints(
                        constraints,
                        leftRoadXy,
                        leftShoulderXy,
                        constraintPath.ZValues,
                        constraintPath.VertexCount,
                        keepLeftGuides,
                        hasBoundaryLoop,
                        boundaryLoop,
                        boundaryVertexCount,
                        dedupTol);
                }

                if (rightShoulderXy != null)
                {
                    foreach (var run in CreateBoundaryClippedRuns(rightShoulderXy, constraintPath.ZValues, constraintPath.VertexCount, hasBoundaryLoop, boundaryLoop, boundaryVertexCount, dedupTol))
                        AddConstraintPolyline(constraints, run.XyVertices, run.ZValues, run.VertexCount);

                    AddShoulderGuideConstraints(
                        constraints,
                        rightRoadXy,
                        rightShoulderXy,
                        constraintPath.ZValues,
                        constraintPath.VertexCount,
                        keepRightGuides,
                        hasBoundaryLoop,
                        boundaryLoop,
                        boundaryVertexCount,
                        dedupTol);
                }
            }
            finally
            {
                ArrayPool<double>.Shared.Return(leftRoadXy);
                ArrayPool<double>.Shared.Return(rightRoadXy);
                if (leftShoulderXy != null)
                    ArrayPool<double>.Shared.Return(leftShoulderXy);
                if (rightShoulderXy != null)
                    ArrayPool<double>.Shared.Return(rightShoulderXy);
            }
        }

        return new ConstraintSet
        {
            Constraints = constraints.ToArray(),
            SuggestedEdgeLength = suggestedEdgeLength < double.MaxValue ? suggestedEdgeLength : 0.0
        };
    }

    /// <summary>
    /// Re-triangulate with road edges as constrained segments, then grade Z.
    /// </summary>
    private static GradingResult? GradeWithEdges(
        double[] vertices, int vertexCount,
        int[] faces, int faceCount,
        PathDefinition[] paths,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> hardConstraints,
        out string? errorMessage,
        out bool fatalError)
    {
        errorMessage = null;
        fatalError = false;
        const double dedupTol = 1e-3;

        var vertHash = new PadGrader.SpatialHash(dedupTol);
        var xyList = new List<double>(vertexCount * 2);
        var zList = new List<double>(vertexCount);
        var segList = new List<(int a, int b)>();

        // Add terrain vertices
        for (int i = 0; i < vertexCount; i++)
        {
            double x = vertices[i * 3], y = vertices[i * 3 + 1];
            xyList.Add(x);
            xyList.Add(y);
            zList.Add(vertices[i * 3 + 2]);
            vertHash.Insert(i, x, y);
        }

        var faceGrid = new PadGrader.FaceGrid(vertices, vertexCount, faces, faceCount);
        bool hasBoundaryLoop = PadGrader.TryBuildBoundaryLoop(vertices, faces, faceCount, out var boundaryLoop, out int boundaryVertexCount);

        // Add mesh boundary edges as constraints (keeps triangulation within original mesh)
        var edgeFaceCount = new Dictionary<long, int>(8, IndexedMeshTools.EdgeKeyComparer.Instance);
        for (int f = 0; f < faceCount; f++)
        {
            int a = faces[f * 3], b = faces[f * 3 + 1], c = faces[f * 3 + 2];
            IncrEdge(edgeFaceCount, a, b);
            IncrEdge(edgeFaceCount, b, c);
            IncrEdge(edgeFaceCount, c, a);
        }
        foreach (var kvp in edgeFaceCount)
        {
            if (kvp.Value == 1)
            {
                int a = (int)(kvp.Key >> 32);
                int b = (int)(kvp.Key & 0xFFFFFFFFL);
                segList.Add((a, b));
            }
        }

        int AddVertex(double x, double y)
        {
            int near = vertHash.FindNearest(xyList, x, y, dedupTol);
            if (near >= 0) return near;

            int idx = zList.Count;
            xyList.Add(x);
            xyList.Add(y);
            zList.Add(faceGrid.InterpolateZ(x, y)); // always terrain Z
            vertHash.Insert(idx, x, y);
            return idx;
        }

        int AddConstraintVertex(SurfaceRemesher.ConstraintPolyline constraint, int pointIndex)
        {
            double x = constraint.Points[pointIndex * 3];
            double y = constraint.Points[pointIndex * 3 + 1];
            double z = constraint.PreserveInputElevation
                ? constraint.Points[pointIndex * 3 + 2]
                : faceGrid.InterpolateZ(x, y);

            int near = vertHash.FindNearest(xyList, x, y, dedupTol);
            if (near >= 0)
            {
                if (constraint.PreserveInputElevation)
                    zList[near] = z;
                return near;
            }

            int idx = zList.Count;
            xyList.Add(x);
            xyList.Add(y);
            zList.Add(z);
            vertHash.Insert(idx, x, y);
            return idx;
        }

        foreach (var hardConstraint in hardConstraints)
        {
            int pointCount = NormalizeConstraintPointCount(hardConstraint, dedupTol);
            if (pointCount < 2)
                continue;

            var indices = new int[pointCount];
            for (int pointIndex = 0; pointIndex < pointCount; pointIndex++)
                indices[pointIndex] = AddConstraintVertex(hardConstraint, pointIndex);

            for (int pointIndex = 0; pointIndex < pointCount - 1; pointIndex++)
            {
                if (indices[pointIndex] != indices[pointIndex + 1])
                    segList.Add((indices[pointIndex], indices[pointIndex + 1]));
            }

            if (hardConstraint.IsClosed &&
                indices[pointCount - 1] != indices[0])
            {
                segList.Add((indices[pointCount - 1], indices[0]));
            }
        }

        // Build barrier index from hard constraints for shoulder clipping and road-edge validation.
        PreparedBarriers roadBarriers = GradingBarriers.Build(hardConstraints);
        var barrierScratch = new SpatialHashGrid2D.QueryScratch(Math.Max(roadBarriers.Segments.Length, 1));
        var barrierCandidates = new List<int>(8);

        // Validate that no road center/edge segment crosses a hard constraint.
        if (roadBarriers.Segments.Length > 0)
        {
            foreach (var path in paths)
            {
                double halfWidth = path.Width * 0.5;
                int vc = path.VertexCount;
                for (int i = 0; i < vc - 1; i++)
                {
                    double cx0 = path.XyVertices[i * 2],     cy0 = path.XyVertices[i * 2 + 1];
                    double cx1 = path.XyVertices[(i + 1) * 2], cy1 = path.XyVertices[(i + 1) * 2 + 1];
                    ComputeDirection(path.XyVertices, vc, i, out double dx, out double dy);
                    double roadPx = -dy * halfWidth, roadPy = dx * halfWidth;

                    // Check center, left edge, right edge
                    if (GradingBarriers.IsCrossedByBarrier(roadBarriers, cx0, cy0, cx1, cy1, barrierScratch, barrierCandidates) ||
                        GradingBarriers.IsCrossedByBarrier(roadBarriers, cx0 + roadPx, cy0 + roadPy, cx1 + roadPx, cy1 + roadPy, barrierScratch, barrierCandidates) ||
                        GradingBarriers.IsCrossedByBarrier(roadBarriers, cx0 - roadPx, cy0 - roadPy, cx1 - roadPx, cy1 - roadPy, barrierScratch, barrierCandidates))
                    {
                        errorMessage = "Road edge crosses a hard constraint. Redesign the path or convert the conflicting constraint to a contour.";
                        fatalError = true;
                        return null;
                    }
                }
            }
        }

        var outputPolylines = new List<OutputPolyline>(paths.Length * 2);

        // For each path, compute road edges and add as open constrained polylines
        foreach (var path in paths)
        {
            double halfWidth = path.Width * 0.5;
            double slopeRatio = Math.Tan(path.SlopeAngleDeg * Math.PI / 180.0);

            // Use a global worst-case distance only for segment-length and buffer sizing;
            // actual shoulder positions are computed per-vertex below.
            double globalShoulderDistance = ComputePathShoulderDistance(vertices, vertexCount, path);
            var constraintPath = BuildConstraintPolyline(
                path,
                ComputeConstraintSegmentLength(path, globalShoulderDistance),
                dedupTol);
            int n = constraintPath.VertexCount;
            ComputeInsideCornerGuideSuppression(
                constraintPath.XyVertices,
                n,
                out bool[] suppressLeftGuides,
                out bool[] suppressRightGuides);

            // Per-vertex shoulder distances: slope-cast from road edge to terrain at each sample.
            var shoulderDistances = new double[n];
            double maxShoulderDistance = 0.0;
            for (int i = 0; i < n; i++)
            {
                double cx = constraintPath.XyVertices[i * 2], cy = constraintPath.XyVertices[i * 2 + 1];
                double terrainZ = faceGrid.InterpolateZ(cx, cy);
                double dz = Math.Abs(constraintPath.ZValues[i] - terrainZ);
                double d = slopeRatio > 1e-12 ? dz / slopeRatio : 100.0;
                if (path.MaxDistance > 0) d = Math.Min(d, path.MaxDistance);
                shoulderDistances[i] = d;
                if (d > maxShoulderDistance) maxShoulderDistance = d;
            }
            double guideSpacing = ComputeGuideSpacing(path.Width, maxShoulderDistance);
            bool[] keepLeftGuides = ComputeGuideSelection(constraintPath.XyVertices, n, guideSpacing, suppressLeftGuides);
            bool[] keepRightGuides = ComputeGuideSelection(constraintPath.XyVertices, n, guideSpacing, suppressRightGuides);

            var centerIdx = new int[n];
            var leftIdx = new int[n];
            var rightIdx = new int[n];
            double[]? leftShoulderXy = maxShoulderDistance > dedupTol ? ArrayPool<double>.Shared.Rent(n * 2) : null;
            double[]? rightShoulderXy = maxShoulderDistance > dedupTol ? ArrayPool<double>.Shared.Rent(n * 2) : null;
            int[]? leftShoulderIdx = maxShoulderDistance > dedupTol ? new int[n] : null;
            int[]? rightShoulderIdx = maxShoulderDistance > dedupTol ? new int[n] : null;

            // Road-edge output polylines (graded Z = path Z at each station)
            var leftEdgeXyz = new double[n * 3];
            var rightEdgeXyz = new double[n * 3];

            try
            {
                for (int i = 0; i < n; i++)
                {
                    double cx = constraintPath.XyVertices[i * 2], cy = constraintPath.XyVertices[i * 2 + 1];
                    double cz = constraintPath.ZValues[i];
                    ComputeDirection(constraintPath.XyVertices, constraintPath.VertexCount, i, out double roadDx, out double roadDy);
                    GetConstraintPathTangent(constraintPath, i, out double shoulderDx, out double shoulderDy);

                    // Perpendicular offset
                    double roadPx = -roadDy * halfWidth, roadPy = roadDx * halfWidth;

                    centerIdx[i] = AddVertex(cx, cy);
                    leftIdx[i] = AddVertex(cx + roadPx, cy + roadPy);
                    rightIdx[i] = AddVertex(cx - roadPx, cy - roadPy);

                    leftEdgeXyz[i * 3]     = cx + roadPx;
                    leftEdgeXyz[i * 3 + 1] = cy + roadPy;
                    leftEdgeXyz[i * 3 + 2] = cz;
                    rightEdgeXyz[i * 3]     = cx - roadPx;
                    rightEdgeXyz[i * 3 + 1] = cy - roadPy;
                    rightEdgeXyz[i * 3 + 2] = cz;

                    if (leftShoulderIdx != null && rightShoulderIdx != null)
                    {
                        double shoulderOffset = halfWidth + shoulderDistances[i];
                        double shoulderPx = -shoulderDy * shoulderOffset;
                        double shoulderPy = shoulderDx * shoulderOffset;
                        leftShoulderXy![i * 2] = cx + shoulderPx;
                        leftShoulderXy[i * 2 + 1] = cy + shoulderPy;
                        rightShoulderXy![i * 2] = cx - shoulderPx;
                        rightShoulderXy[i * 2 + 1] = cy - shoulderPy;
                    }
                }

                outputPolylines.Add(new OutputPolyline(leftEdgeXyz, n));
                outputPolylines.Add(new OutputPolyline(rightEdgeXyz, n));

                // Pre-clip each shoulder point to the first barrier between the road edge and the shoulder.
                // This ensures the shoulder constraint polyline itself stays on the near side of barriers,
                // not just the radial guide segments.
                if (roadBarriers.Segments.Length > 0)
                {
                    for (int i = 0; i < n; i++)
                    {
                        if (leftShoulderXy != null)
                        {
                            double roadX = xyList[leftIdx[i] * 2];
                            double roadY = xyList[leftIdx[i] * 2 + 1];
                            GradingBarriers.TryClipSegment(
                                roadBarriers, roadX, roadY,
                                leftShoulderXy[i * 2], leftShoulderXy[i * 2 + 1],
                                barrierScratch, barrierCandidates,
                                out leftShoulderXy[i * 2], out leftShoulderXy[i * 2 + 1]);
                        }

                        if (rightShoulderXy != null)
                        {
                            double roadX = xyList[rightIdx[i] * 2];
                            double roadY = xyList[rightIdx[i] * 2 + 1];
                            GradingBarriers.TryClipSegment(
                                roadBarriers, roadX, roadY,
                                rightShoulderXy[i * 2], rightShoulderXy[i * 2 + 1],
                                barrierScratch, barrierCandidates,
                                out rightShoulderXy[i * 2], out rightShoulderXy[i * 2 + 1]);
                        }
                    }
                }

                if (leftShoulderIdx != null && leftShoulderXy != null)
                {
                    leftShoulderIdx = AddShoulderConstraint(
                        leftShoulderXy, n,
                        hasBoundaryLoop, boundaryLoop, boundaryVertexCount,
                        dedupTol, AddVertex, segList,
                        roadBarriers, barrierScratch, barrierCandidates);
                }

                if (rightShoulderIdx != null && rightShoulderXy != null)
                {
                    rightShoulderIdx = AddShoulderConstraint(
                        rightShoulderXy, n,
                        hasBoundaryLoop, boundaryLoop, boundaryVertexCount,
                        dedupTol, AddVertex, segList,
                        roadBarriers, barrierScratch, barrierCandidates);
                }

                // Add constrained segments (open polylines, NOT closed)
                for (int i = 0; i < n - 1; i++)
                {
                    if (centerIdx[i] != centerIdx[i + 1])
                        segList.Add((centerIdx[i], centerIdx[i + 1]));
                    if (leftIdx[i] != leftIdx[i + 1])
                        segList.Add((leftIdx[i], leftIdx[i + 1]));
                    if (rightIdx[i] != rightIdx[i + 1])
                        segList.Add((rightIdx[i], rightIdx[i + 1]));
                }

                AddShoulderGuideSegments(
                    xyList, leftIdx, leftShoulderXy, leftShoulderIdx, n,
                    keepLeftGuides,
                    hasBoundaryLoop, boundaryLoop, boundaryVertexCount,
                    dedupTol, AddVertex, segList,
                    roadBarriers, barrierScratch, barrierCandidates);

                AddShoulderGuideSegments(
                    xyList, rightIdx, rightShoulderXy, rightShoulderIdx, n,
                    keepRightGuides,
                    hasBoundaryLoop, boundaryLoop, boundaryVertexCount,
                    dedupTol, AddVertex, segList,
                    roadBarriers, barrierScratch, barrierCandidates);
            }
            finally
            {
                if (leftShoulderXy != null)
                    ArrayPool<double>.Shared.Return(leftShoulderXy);
                if (rightShoulderXy != null)
                    ArrayPool<double>.Shared.Return(rightShoulderXy);
            }
        }

        // Triangulate (NO quality constraints)
        int totalVerts = zList.Count;
        if (totalVerts < 3)
        {
            errorMessage = "Too few vertices for triangulation.";
            return null;
        }

        TriangulationOutcome triangulation = TriangulationHelper.Triangulate(
            xyList, totalVerts, segList,
            0, 0, // no quality refinement
            convex: false);

        if (triangulation.Mesh == null)
        {
            errorMessage = triangulation.WarningMessage ?? "Triangulation failed.";
            return null;
        }

        if (triangulation.WarningMessage != null)
            errorMessage = triangulation.WarningMessage;

        var extracted = TriangleNetExtractor.Extract(triangulation.Mesh);
        int outVertCount = extracted.VertexCount;
        int outFaceCount = extracted.FaceCount;

        var outXy = new double[outVertCount * 2];
        var origZ = new double[outVertCount];
        var newZ = new double[outVertCount];

        for (int i = 0; i < outVertCount; i++)
        {
            double x = extracted.Xy[i * 2];
            double y = extracted.Xy[i * 2 + 1];
            int sourceId = extracted.SourceIds[i];
            outXy[i * 2] = x;
            outXy[i * 2 + 1] = y;

            if (sourceId >= 0 && sourceId < totalVerts)
            {
                origZ[i] = zList[sourceId];
                newZ[i] = zList[sourceId];
            }
            else
            {
                double iz = faceGrid.InterpolateZ(x, y);
                origZ[i] = iz;
                newZ[i] = iz;
            }
        }

        // Grade Z — pass original terrain sampler so reference profile is derived
        // directly from the pre-grading field rather than from sparse new vertices.
        Func<double, double, double> interpolateOriginalZ = (x, y) => faceGrid.InterpolateZ(x, y);
        ApplyPathGrading(
            paths,
            hardConstraints,
            outXy,
            origZ,
            newZ,
            outVertCount,
            interpolateOriginalZ,
            hasBoundaryLoop,
            boundaryLoop,
            boundaryVertexCount,
            dedupTol);

        // Build output
        var finalVerts = new double[outVertCount * 3];
        for (int i = 0; i < outVertCount; i++)
        {
            finalVerts[i * 3] = outXy[i * 2];
            finalVerts[i * 3 + 1] = outXy[i * 2 + 1];
            finalVerts[i * 3 + 2] = newZ[i];
        }

        var finalFaces = extracted.Faces;
        var cullResult = TriangleBoundaryCuller.Cull(
            finalVerts,
            outVertCount,
            finalFaces,
            outFaceCount,
            xyList.ToArray(),
            IndexedMeshTools.FlattenSegments(segList),
            0);

        if (cullResult.Changed)
        {
            outXy = IndexedMeshTools.CompactDoubleData(outXy, 2, cullResult.NewToOld, cullResult.VertexCount);
            origZ = IndexedMeshTools.CompactDoubleData(origZ, 1, cullResult.NewToOld, cullResult.VertexCount);
            newZ = IndexedMeshTools.CompactDoubleData(newZ, 1, cullResult.NewToOld, cullResult.VertexCount);
            finalVerts = IndexedMeshTools.CompactDoubleData(finalVerts, 3, cullResult.NewToOld, cullResult.VertexCount);
            finalFaces = cullResult.Faces;
            outVertCount = cullResult.VertexCount;
            outFaceCount = cullResult.FaceCount;
        }

        return BuildResult(outXy, origZ, newZ, finalVerts, outVertCount, finalFaces, outFaceCount, outputPolylines);
    }

    /// <summary>
    /// Fallback: modify Z values of existing mesh without re-triangulation.
    /// </summary>
    private static GradingResult? GradeZOnly(
        double[] vertices, int vertexCount,
        int[] faces, int faceCount,
        PathDefinition[] paths,
        out string? errorMessage)
    {
        return GradeZOnly(vertices, vertexCount, faces, faceCount, paths, Array.Empty<SurfaceRemesher.ConstraintPolyline>(), out errorMessage);
    }

    private static GradingResult? GradeZOnly(
        double[] vertices, int vertexCount,
        int[] faces, int faceCount,
        PathDefinition[] paths,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> hardConstraints,
        out string? errorMessage)
    {
        errorMessage = "Using Z-only grading (road edges may not be sharp).";

        var outXy = new double[vertexCount * 2];
        var origZ = new double[vertexCount];
        var newZ = new double[vertexCount];
        bool hasBoundaryLoop = PadGrader.TryBuildBoundaryLoop(vertices, faces, faceCount, out double[] boundaryLoop, out int boundaryVertexCount);

        for (int i = 0; i < vertexCount; i++)
        {
            outXy[i * 2] = vertices[i * 3];
            outXy[i * 2 + 1] = vertices[i * 3 + 1];
            origZ[i] = vertices[i * 3 + 2];
            newZ[i] = vertices[i * 3 + 2];
        }

        ApplyPathGrading(
            paths,
            hardConstraints,
            outXy,
            origZ,
            newZ,
            vertexCount,
            hasBoundaryLoop: hasBoundaryLoop,
            boundaryLoop: boundaryLoop,
            boundaryVertexCount: boundaryVertexCount);

        var finalVerts = new double[vertexCount * 3];
        for (int i = 0; i < vertexCount; i++)
        {
            finalVerts[i * 3] = vertices[i * 3];
            finalVerts[i * 3 + 1] = vertices[i * 3 + 1];
            finalVerts[i * 3 + 2] = newZ[i];
        }

        return BuildResult(outXy, origZ, newZ, finalVerts, vertexCount, (int[])faces.Clone(), faceCount);
    }

    /// <summary>
    /// Shared grading logic: evaluate all nearby paths per vertex so overlapping
    /// corridors blend by proximity instead of depending on input order.
    /// </summary>
    /// <param name="interpolateOriginalZ">
    /// Optional sampler returning original terrain Z at any XY point.
    /// Must represent the unmodified input terrain — not any already-graded
    /// or remeshed geometry. When null, falls back to vertex-accumulation
    /// for the reference shoulder profile.
    /// </param>
    private static void ApplyPathGrading(
        PathDefinition[] paths,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> barrierConstraints,
        double[] outXy, double[] origZ, double[] newZ, int vertCount,
        Func<double, double, double>? interpolateOriginalZ = null,
        bool hasBoundaryLoop = false,
        double[]? boundaryLoop = null,
        int boundaryVertexCount = 0,
        double boundaryTolerance = 1e-3)
    {
        PreparedBarriers preparedBarriers = GradingBarriers.Build(barrierConstraints);
        var setupScratch = new SpatialHashGrid2D.QueryScratch(Math.Max(preparedBarriers.Segments.Length, 1));
        var setupCandidates = new List<int>(8);
        var preparedPaths = new PreparedPath[paths.Length];
        for (int pathIndex = 0; pathIndex < paths.Length; pathIndex++)
        {
            PathDefinition path = paths[pathIndex];
            double halfWidth = path.Width * 0.5;
            double slopeRatio = Math.Tan(path.SlopeAngleDeg * Math.PI / 180.0);
            double shoulderDistance = ComputePathShoulderDistance(outXy, origZ, vertCount, path, preparedBarriers, setupScratch, setupCandidates);
            var samplePath = BuildConstraintPolyline(
                path,
                ComputeConstraintSegmentLength(path, shoulderDistance),
                dedupTol: 1e-6);

            double[] leftReferenceDz, rightReferenceDz;
            if (interpolateOriginalZ != null)
            {
                BuildShoulderReferenceProfileDirect(
                    samplePath,
                    halfWidth,
                    shoulderDistance,
                    interpolateOriginalZ,
                    preparedBarriers,
                    setupScratch,
                    setupCandidates,
                    hasBoundaryLoop,
                    boundaryLoop ?? Array.Empty<double>(),
                    boundaryVertexCount,
                    boundaryTolerance,
                    out leftReferenceDz,
                    out rightReferenceDz);
            }
            else
            {
                BuildShoulderReferenceProfile(
                    samplePath,
                    halfWidth,
                    shoulderDistance,
                    outXy,
                    origZ,
                    vertCount,
                    preparedBarriers,
                    setupScratch,
                    setupCandidates,
                    out leftReferenceDz,
                    out rightReferenceDz);
            }

            double mnX = double.MaxValue, mxX = double.MinValue;
            double mnY = double.MaxValue, mxY = double.MinValue;
            for (int i = 0; i < samplePath.VertexCount; i++)
            {
                double x = samplePath.XyVertices[i * 2], y = samplePath.XyVertices[i * 2 + 1];
                if (x < mnX) mnX = x; if (x > mxX) mxX = x;
                if (y < mnY) mnY = y; if (y > mxY) mxY = y;
            }

            double maxInfluence = halfWidth + shoulderDistance;
            preparedPaths[pathIndex] = new PreparedPath(
                halfWidth,
                slopeRatio,
                path.MaxDistance,
                shoulderDistance,
                maxInfluence,
                samplePath,
                leftReferenceDz,
                rightReferenceDz,
                mnX - maxInfluence,
                mxX + maxInfluence,
                mnY - maxInfluence,
                mxY + maxInfluence);
        }

        System.Threading.Tasks.Parallel.For(
            0,
            vertCount,
            () => (
                Scratch: new SpatialHashGrid2D.QueryScratch(preparedBarriers.Segments.Length),
                Candidates: new List<int>(8)),
            (i, _, state) =>
        {
            double px = outXy[i * 2];
            double py = outXy[i * 2 + 1];
            double originalZ = origZ[i];

            double roadWeightSum = 0.0;
            double roadZSum = 0.0;
            double shoulderWeightSum = 0.0;
            double shoulderDeltaSum = 0.0;

            foreach (var preparedPath in preparedPaths)
            {
                if (!TryComputePathInfluence(
                        preparedPath,
                        preparedBarriers,
                        state.Scratch,
                        state.Candidates,
                        px,
                        py,
                        originalZ,
                        out bool insideRoad,
                        out double candidateZ,
                        out double weight))
                {
                    continue;
                }

                if (insideRoad)
                {
                    roadWeightSum += weight;
                    roadZSum += candidateZ * weight;
                }
                else
                {
                    shoulderWeightSum += weight;
                    shoulderDeltaSum += (candidateZ - originalZ) * weight;
                }
            }

            if (roadWeightSum > 1e-12)
            {
                newZ[i] = roadZSum / roadWeightSum;
            }
            else if (shoulderWeightSum > 1e-12)
            {
                newZ[i] = originalZ + (shoulderDeltaSum / shoulderWeightSum);
            }

            return state;
        }, _ => { });
    }

    private static bool TryComputePathInfluence(
        PreparedPath preparedPath,
        PreparedBarriers preparedBarriers,
        SpatialHashGrid2D.QueryScratch barrierScratch,
        List<int> barrierCandidates,
        double px,
        double py,
        double originalZ,
        out bool insideRoad,
        out double candidateZ,
        out double weight)
    {
        insideRoad = false;
        candidateZ = 0.0;
        weight = 0.0;

        if (px < preparedPath.MinX || px > preparedPath.MaxX || py < preparedPath.MinY || py > preparedPath.MaxY)
            return false;

        if (!TryFindClosestPathLocation(preparedPath.SamplePath, px, py, out ClosestPathLocation closest))
            return false;

        if (closest.Distance > preparedPath.MaxInfluence + 1e-6)
            return false;

        if (closest.Distance <= preparedPath.HalfWidth + 1e-6)
        {
            insideRoad = true;
            candidateZ = closest.PathZ;
            weight = ComputeRoadBlendWeight(preparedPath.HalfWidth, closest.Distance);
            return true;
        }

        double distFromEdge = closest.Distance - preparedPath.HalfWidth;
        if (IsBlockedByBarrier(preparedBarriers, preparedPath.HalfWidth, closest, px, py, barrierScratch, barrierCandidates))
            return false;

        double dzActual = originalZ - closest.PathZ;
        double dz = dzActual;
        if (TryGetShoulderReferenceDz(preparedPath, closest, out double referenceDz) &&
            Math.Abs(referenceDz) > 1e-12)
        {
            // Only use the reference when it agrees with the vertex's actual terrain
            // direction (same sign) or when dzActual is near-zero (flat terrain dip/bump
            // that needs lifting/lowering by the smooth profile).
            // Opposite-sign means the reference profile came from the wrong side
            // (SideSign flip) — using it would push the vertex in the wrong direction.
            if (referenceDz * dzActual >= -1e-12)
                dz = referenceDz;
        }

        double absDz = Math.Abs(dz);
        if (absDz <= 1e-12)
            return false;

        double neededDist = preparedPath.SlopeRatio > 1e-12
            ? absDz / preparedPath.SlopeRatio
            : double.MaxValue;
        if (preparedPath.MaxDistance > 0)
            neededDist = Math.Min(neededDist, preparedPath.MaxDistance);
        if (preparedPath.ShoulderDistance > 0)
            neededDist = Math.Min(neededDist, preparedPath.ShoulderDistance);

        if (distFromEdge >= neededDist)
            return false;

        double rise = distFromEdge * preparedPath.SlopeRatio;
        if (rise >= absDz)
            return false;

        candidateZ = closest.PathZ + Math.Sign(dz) * rise;

        weight = ComputeShoulderBlendWeight(distFromEdge, neededDist);
        return weight > 1e-12;
    }

    private static double ComputeRoadBlendWeight(double halfWidth, double closestDist)
    {
        if (halfWidth <= 1e-9)
            return 4.0;

        double closeness = 1.0 - Math.Clamp(closestDist / halfWidth, 0.0, 1.0);
        return 1.0 + (closeness * closeness * 3.0);
    }

    private static double ComputeShoulderBlendWeight(double distFromEdge, double neededDist)
    {
        if (neededDist <= 1e-9)
            return 0.0;

        double closeness = 1.0 - Math.Clamp(distFromEdge / neededDist, 0.0, 1.0);
        return closeness * closeness;
    }

    private static void BuildShoulderReferenceProfile(
        ConstraintPath samplePath,
        double halfWidth,
        double shoulderDistance,
        double[] xy,
        double[] z,
        int vertexCount,
        PreparedBarriers barriers,
        SpatialHashGrid2D.QueryScratch barrierScratch,
        List<int> barrierCandidates,
        out double[] leftReferenceDz,
        out double[] rightReferenceDz)
    {
        leftReferenceDz = CreateNaNArray(samplePath.VertexCount);
        rightReferenceDz = CreateNaNArray(samplePath.VertexCount);
        if (samplePath.VertexCount < 2 || shoulderDistance <= 1e-9)
            return;

        var leftSum = new double[samplePath.VertexCount];
        var leftWeight = new double[samplePath.VertexCount];
        var rightSum = new double[samplePath.VertexCount];
        var rightWeight = new double[samplePath.VertexCount];

        for (int i = 0; i < vertexCount; i++)
        {
            double px = xy[i * 2];
            double py = xy[i * 2 + 1];
            if (!TryFindClosestPathLocation(samplePath, px, py, out ClosestPathLocation closest))
                continue;

            double distFromEdge = closest.Distance - halfWidth;
            if (distFromEdge <= 1e-6 || distFromEdge > shoulderDistance + 1e-6)
                continue;

            if (barriers.Segments.Length > 0 && IsBlockedByBarrier(barriers, halfWidth, closest, px, py, barrierScratch, barrierCandidates))
                continue;

            double dz = z[i] - closest.PathZ;
            if (Math.Abs(dz) <= 1e-9)
                continue;

            double normalizedDistance = Math.Clamp(distFromEdge / shoulderDistance, 0.0, 1.0);
            double sampleWeight = Math.Max(normalizedDistance * normalizedDistance, 1e-3);
            if (closest.SideSign >= 0.0)
                AccumulateReferenceSample(leftSum, leftWeight, closest.SegmentIndex, closest.SegmentT, dz, sampleWeight);
            else
                AccumulateReferenceSample(rightSum, rightWeight, closest.SegmentIndex, closest.SegmentT, dz, sampleWeight);
        }

        FinalizeReferenceProfile(leftSum, leftWeight, leftReferenceDz);
        FinalizeReferenceProfile(rightSum, rightWeight, rightReferenceDz);
    }

    /// <summary>
    /// Builds the shoulder reference dz profile by directly sampling the original
    /// terrain field at each path station, rather than accumulating from sparse
    /// mesh vertices. Uses a 3-point transverse aggregate (50 %, 75 %, 100 % of
    /// the shoulder reach) with equal weights to reduce sensitivity to local terrain
    /// anomalies at any single offset. Retains one smoothing pass for longitudinal
    /// continuity. Produces no NaN gaps so gap-filling is not required.
    /// </summary>
    private static void BuildShoulderReferenceProfileDirect(
        ConstraintPath samplePath,
        double halfWidth,
        double shoulderDistance,
        Func<double, double, double> interpolateOriginalZ,
        PreparedBarriers barriers,
        SpatialHashGrid2D.QueryScratch barrierScratch,
        List<int> barrierCandidates,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double boundaryTolerance,
        out double[] leftReferenceDz,
        out double[] rightReferenceDz)
    {
        int n = samplePath.VertexCount;
        leftReferenceDz  = new double[n];
        rightReferenceDz = new double[n];

        if (n < 2 || shoulderDistance <= 1e-9)
            return;

        for (int i = 0; i < n; i++)
        {
            double cx = samplePath.XyVertices[i * 2];
            double cy = samplePath.XyVertices[i * 2 + 1];
            double pathZ = samplePath.ZValues[i];

            // Use smooth tangent when available; fall back to ComputeDirection.
            double dx, dy;
            if (samplePath.TangentX != null && samplePath.TangentX.Length == n)
            { dx = samplePath.TangentX[i]; dy = samplePath.TangentY[i]; }
            else
            { ComputeDirection(samplePath.XyVertices, n, i, out dx, out dy); }
            double nx = -dy;  // left normal
            double ny =  dx;

            // Sample at 50 %, 75 %, 100 % of the shoulder reach measured from the road edge.
            for (int side = -1; side <= 1; side += 2)  // -1 = right, +1 = left
            {
                double roadEdgeX = cx + side * nx * halfWidth;
                double roadEdgeY = cy + side * ny * halfWidth;
                double s0 = SampleOriginalTerrainAlongShoulderRay(
                    interpolateOriginalZ,
                    barriers,
                    barrierScratch,
                    barrierCandidates,
                    hasBoundaryLoop,
                    boundaryLoop,
                    boundaryVertexCount,
                    boundaryTolerance,
                    roadEdgeX,
                    roadEdgeY,
                    roadEdgeX + side * nx * (shoulderDistance * 0.50),
                    roadEdgeY + side * ny * (shoulderDistance * 0.50));
                double s1 = SampleOriginalTerrainAlongShoulderRay(
                    interpolateOriginalZ,
                    barriers,
                    barrierScratch,
                    barrierCandidates,
                    hasBoundaryLoop,
                    boundaryLoop,
                    boundaryVertexCount,
                    boundaryTolerance,
                    roadEdgeX,
                    roadEdgeY,
                    roadEdgeX + side * nx * (shoulderDistance * 0.75),
                    roadEdgeY + side * ny * (shoulderDistance * 0.75));
                double s2 = SampleOriginalTerrainAlongShoulderRay(
                    interpolateOriginalZ,
                    barriers,
                    barrierScratch,
                    barrierCandidates,
                    hasBoundaryLoop,
                    boundaryLoop,
                    boundaryVertexCount,
                    boundaryTolerance,
                    roadEdgeX,
                    roadEdgeY,
                    roadEdgeX + side * nx * shoulderDistance,
                    roadEdgeY + side * ny * shoulderDistance);
                double dz = (s0 + s1 + s2) / 3.0 - pathZ;

                if (side > 0)
                    leftReferenceDz[i]  = dz;
                else
                    rightReferenceDz[i] = dz;
            }
        }

        // One smoothing pass to avoid raw terrain frequency becoming grading oscillation.
        SmoothReferenceSamples(leftReferenceDz,  1);
        SmoothReferenceSamples(rightReferenceDz, 1);
    }

    private static double SampleOriginalTerrainAlongShoulderRay(
        Func<double, double, double> interpolateOriginalZ,
        PreparedBarriers barriers,
        SpatialHashGrid2D.QueryScratch barrierScratch,
        List<int> barrierCandidates,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double boundaryTolerance,
        double startX,
        double startY,
        double targetX,
        double targetY)
    {
        if (barriers.Segments.Length > 0)
        {
            GradingBarriers.TryClipSegment(
                barriers,
                startX,
                startY,
                targetX,
                targetY,
                barrierScratch,
                barrierCandidates,
                out targetX,
                out targetY);
        }

        if (hasBoundaryLoop)
        {
            List<ClippedSegment> pieces = BoundaryClipper.ClipSegmentToBoundary(
                startX,
                startY,
                0.0,
                targetX,
                targetY,
                0.0,
                hasBoundaryLoop,
                boundaryLoop,
                boundaryVertexCount,
                boundaryTolerance);

            double furthestEndT = double.MinValue;
            bool foundPiece = false;
            foreach (ClippedSegment piece in pieces)
            {
                if (piece.StartT > 1e-9 || piece.EndT <= furthestEndT)
                    continue;

                targetX = piece.EndX;
                targetY = piece.EndY;
                furthestEndT = piece.EndT;
                foundPiece = true;
            }

            if (!foundPiece)
            {
                if (!BoundaryClipper.IsInsideOrOnBoundary(startX, startY, hasBoundaryLoop, boundaryLoop, boundaryVertexCount, boundaryTolerance))
                    return interpolateOriginalZ(startX, startY);

                targetX = startX;
                targetY = startY;
            }
        }

        return interpolateOriginalZ(targetX, targetY);
    }

    private static void AccumulateReferenceSample(
        double[] sum,
        double[] weight,
        int segmentIndex,
        double segmentT,
        double dz,
        double sampleWeight)
    {
        if (segmentIndex < 0 || segmentIndex >= sum.Length - 1 || sampleWeight <= 0.0)
            return;

        double startWeight = sampleWeight * (1.0 - segmentT);
        double endWeight = sampleWeight * segmentT;
        sum[segmentIndex] += dz * startWeight;
        weight[segmentIndex] += startWeight;
        sum[segmentIndex + 1] += dz * endWeight;
        weight[segmentIndex + 1] += endWeight;
    }

    private static void FinalizeReferenceProfile(double[] sum, double[] weight, double[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            if (weight[i] > 1e-9)
                values[i] = sum[i] / weight[i];
        }

        FillMissingReferenceSamples(values);
        SmoothReferenceSamples(values, 3);
    }

    private static void FillMissingReferenceSamples(double[] values)
    {
        int firstFinite = -1;
        for (int i = 0; i < values.Length; i++)
        {
            if (double.IsFinite(values[i]))
            {
                firstFinite = i;
                break;
            }
        }

        if (firstFinite < 0)
            return;

        for (int i = 0; i < firstFinite; i++)
            values[i] = values[firstFinite];

        int previousFinite = firstFinite;
        for (int i = firstFinite + 1; i < values.Length; i++)
        {
            if (!double.IsFinite(values[i]))
                continue;

            int nextFinite = i;
            if (nextFinite - previousFinite > 1)
            {
                double start = values[previousFinite];
                double end = values[nextFinite];
                int gap = nextFinite - previousFinite;
                for (int gapIndex = 1; gapIndex < gap; gapIndex++)
                {
                    double t = gapIndex / (double)gap;
                    values[previousFinite + gapIndex] = start + ((end - start) * t);
                }
            }

            previousFinite = nextFinite;
        }

        for (int i = previousFinite + 1; i < values.Length; i++)
            values[i] = values[previousFinite];
    }

    private static void SmoothReferenceSamples(double[] values, int passes)
    {
        if (values.Length < 3 || passes <= 0 || !HasFiniteSamples(values))
            return;

        var scratch = new double[values.Length];
        for (int pass = 0; pass < passes; pass++)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (!double.IsFinite(values[i]))
                {
                    scratch[i] = values[i];
                    continue;
                }

                double weightedSum = values[i] * 0.5;
                double totalWeight = 0.5;

                if (i > 0 && double.IsFinite(values[i - 1]))
                {
                    weightedSum += values[i - 1] * 0.25;
                    totalWeight += 0.25;
                }

                if (i < values.Length - 1 && double.IsFinite(values[i + 1]))
                {
                    weightedSum += values[i + 1] * 0.25;
                    totalWeight += 0.25;
                }

                scratch[i] = weightedSum / totalWeight;
            }

            Array.Copy(scratch, values, values.Length);
        }
    }

    private static double[] CreateNaNArray(int length)
    {
        var values = new double[length];
        Array.Fill(values, double.NaN);
        return values;
    }

    private static bool HasFiniteSamples(double[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            if (double.IsFinite(values[i]))
                return true;
        }

        return false;
    }

    private static bool TryGetShoulderReferenceDz(
        PreparedPath preparedPath,
        ClosestPathLocation closest,
        out double referenceDz)
    {
        referenceDz = 0.0;
        double[] values = closest.SideSign >= 0.0
            ? preparedPath.LeftReferenceDz
            : preparedPath.RightReferenceDz;
        if (values.Length < 2 || closest.SegmentIndex < 0 || closest.SegmentIndex >= values.Length - 1)
            return false;

        double start = values[closest.SegmentIndex];
        double end = values[closest.SegmentIndex + 1];
        if (!double.IsFinite(start) && !double.IsFinite(end))
            return false;

        if (!double.IsFinite(start))
            start = end;
        else if (!double.IsFinite(end))
            end = start;

        referenceDz = start + ((end - start) * closest.SegmentT);
        return double.IsFinite(referenceDz);
    }

    private static double ComputePathShoulderDistance(double[] vertices, int vertexCount, PathDefinition path)
    {
        var xy = new double[vertexCount * 2];
        var z = new double[vertexCount];
        for (int i = 0; i < vertexCount; i++)
        {
            xy[i * 2] = vertices[i * 3];
            xy[i * 2 + 1] = vertices[i * 3 + 1];
            z[i] = vertices[i * 3 + 2];
        }

        var scratch = new SpatialHashGrid2D.QueryScratch(1);
        return ComputePathShoulderDistance(xy, z, vertexCount, path, PreparedBarriers.Empty, scratch, new List<int>());
    }

    private static double ComputePathShoulderDistance(double[] xy, double[] z, int vertexCount, PathDefinition path,
        PreparedBarriers barriers, SpatialHashGrid2D.QueryScratch barrierScratch, List<int> barrierCandidates)
    {
        if (path.MaxDistance > 0)
            return path.MaxDistance;

        double slopeRatio = Math.Tan(path.SlopeAngleDeg * Math.PI / 180.0);
        if (slopeRatio <= 1e-12)
            return 100.0;

        double halfWidth = path.Width * 0.5;
        double mnX = double.MaxValue, mxX = double.MinValue;
        double mnY = double.MaxValue, mxY = double.MinValue;
        for (int i = 0; i < path.VertexCount; i++)
        {
            double x = path.XyVertices[i * 2];
            double y = path.XyVertices[i * 2 + 1];
            if (x < mnX) mnX = x;
            if (x > mxX) mxX = x;
            if (y < mnY) mnY = y;
            if (y > mxY) mxY = y;
        }

        double maxZDiff = 0;
        for (int i = 0; i < vertexCount; i++)
        {
            double px = xy[i * 2];
            double py = xy[i * 2 + 1];
            if (px < mnX - 200 || px > mxX + 200 || py < mnY - 200 || py > mxY + 200)
                continue;

            if (!TryFindClosestPathLocation(path.XyVertices, path.ZValues, path.VertexCount, px, py, out ClosestPathLocation closest))
                continue;

            if (barriers.Segments.Length > 0 && IsBlockedByBarrier(barriers, halfWidth, closest, px, py, barrierScratch, barrierCandidates))
                continue;

            double dz = Math.Abs(z[i] - closest.PathZ);
            if (dz > maxZDiff)
                maxZDiff = dz;
        }

        return maxZDiff / slopeRatio;
    }

    private static double ComputeConstraintSegmentLength(PathDefinition path, double shoulderDistance)
    {
        double baseSpacing = shoulderDistance > 1e-6 ? shoulderDistance * 0.5 : path.Width;
        double minSpacing = Math.Max(path.Width * 0.5, 1.0);
        double maxSpacing = Math.Max(minSpacing, path.Width * 2.0);
        return Math.Clamp(baseSpacing, minSpacing, maxSpacing);
    }

    private static ConstraintPath MakeGeometryConstraintPath(double[] xy, double[] z, int count)
        => new ConstraintPath(xy, z, count, Array.Empty<double>(), Array.Empty<double>());

    private static ConstraintPath BuildConstraintPolyline(PathDefinition path, double maxSegmentLength, double dedupTol)
    {
        if (path.VertexCount < 2 || maxSegmentLength <= dedupTol)
        {
            double[] xyOut = (double[])path.XyVertices.Clone();
            int n = path.VertexCount;
            ComputeSmoothedTangents(xyOut, n, out double[] tx, out double[] ty);
            return new ConstraintPath(xyOut, (double[])path.ZValues.Clone(), n, tx, ty);
        }

        var xy = new List<double>(path.VertexCount * 4);
        var z = new List<double>(path.VertexCount * 2);
        for (int segmentIndex = 0; segmentIndex < path.VertexCount - 1; segmentIndex++)
        {
            double ax = path.XyVertices[segmentIndex * 2];
            double ay = path.XyVertices[segmentIndex * 2 + 1];
            double bx = path.XyVertices[(segmentIndex + 1) * 2];
            double by = path.XyVertices[(segmentIndex + 1) * 2 + 1];
            double az = path.ZValues[segmentIndex];
            double bz = path.ZValues[segmentIndex + 1];

            double segLen = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
            int divisions = Math.Max(1, (int)Math.Ceiling(segLen / maxSegmentLength));

            for (int step = 0; step < divisions; step++)
            {
                double t = (double)step / divisions;
                AddConstraintSample(xy, z, ax + (bx - ax) * t, ay + (by - ay) * t, az + ((bz - az) * t), dedupTol);
            }
        }

        AddConstraintSample(
            xy,
            z,
            path.XyVertices[(path.VertexCount - 1) * 2],
            path.XyVertices[(path.VertexCount - 1) * 2 + 1],
            path.ZValues[path.VertexCount - 1],
            dedupTol);

        double[] xyArr = xy.ToArray();
        int count = xy.Count / 2;
        ComputeSmoothedTangents(xyArr, count, out double[] tangentX, out double[] tangentY);
        return new ConstraintPath(xyArr, z.ToArray(), count, tangentX, tangentY);
    }

    /// <summary>
    /// Computes a smooth unit tangent at each station of a polyline.
    /// Uses length-weighted averaging of adjacent segment directions.
    /// Zero-length segments are skipped. If adjacent segments are nearly
    /// anti-parallel (dot &lt; -0.7, i.e. ≥ 135° reversal), only the longer
    /// segment contributes to avoid spurious bisector normals at tight U-turns.
    /// Falls back to the longer adjacent segment if the weighted sum is degenerate.
    /// </summary>
    private static void ComputeSmoothedTangents(
        double[] xyVertices,
        int vertexCount,
        out double[] tangentX,
        out double[] tangentY)
    {
        tangentX = new double[vertexCount];
        tangentY = new double[vertexCount];

        if (vertexCount < 2)
            return;

        for (int i = 0; i < vertexCount; i++)
        {
            double sumX = 0.0, sumY = 0.0;

            // Previous segment: from i-1 to i
            double prevDx = 0, prevDy = 0, prevLen = 0;
            if (i > 0)
            {
                prevDx = xyVertices[i * 2]     - xyVertices[(i - 1) * 2];
                prevDy = xyVertices[i * 2 + 1] - xyVertices[(i - 1) * 2 + 1];
                prevLen = Math.Sqrt(prevDx * prevDx + prevDy * prevDy);
                if (prevLen < 1e-9) prevLen = 0; // zero-length — skip
            }

            // Next segment: from i to i+1
            double nextDx = 0, nextDy = 0, nextLen = 0;
            if (i < vertexCount - 1)
            {
                nextDx = xyVertices[(i + 1) * 2]     - xyVertices[i * 2];
                nextDy = xyVertices[(i + 1) * 2 + 1] - xyVertices[i * 2 + 1];
                nextLen = Math.Sqrt(nextDx * nextDx + nextDy * nextDy);
                if (nextLen < 1e-9) nextLen = 0;
            }

            bool hasPrev = prevLen > 1e-9;
            bool hasNext = nextLen > 1e-9;

            if (hasPrev && hasNext)
            {
                // Normalized directions
                double pnx = prevDx / prevLen, pny = prevDy / prevLen;
                double nnx = nextDx / nextLen, nny = nextDy / nextLen;

                // Check for near-U-turn (dot < -0.7, roughly ≥ 135°)
                double dot = pnx * nnx + pny * nny;
                if (dot < -0.7)
                {
                    // Use only the longer segment to avoid bisector artifacts
                    if (prevLen >= nextLen) { sumX = pnx * prevLen; sumY = pny * prevLen; }
                    else                    { sumX = nnx * nextLen; sumY = nny * nextLen; }
                }
                else
                {
                    // Length-weighted blend of both segments
                    sumX = pnx * prevLen + nnx * nextLen;
                    sumY = pny * prevLen + nny * nextLen;
                }
            }
            else if (hasPrev)
            {
                sumX = prevDx; sumY = prevDy;
            }
            else if (hasNext)
            {
                sumX = nextDx; sumY = nextDy;
            }

            double len = Math.Sqrt(sumX * sumX + sumY * sumY);
            if (len < 1e-9)
            {
                // Fully degenerate — fall back to whichever segment is longer
                if (prevLen >= nextLen && prevLen > 1e-9)      { len = prevLen; sumX = prevDx; sumY = prevDy; }
                else if (nextLen > 1e-9)                        { len = nextLen; sumX = nextDx; sumY = nextDy; }
                else                                            { tangentX[i] = 1.0; tangentY[i] = 0.0; continue; }
                len = Math.Sqrt(sumX * sumX + sumY * sumY);
            }

            tangentX[i] = sumX / len;
            tangentY[i] = sumY / len;
        }
    }

    private static void ComputeInsideCornerGuideSuppression(
        double[] xyVertices,
        int vertexCount,
        out bool[] suppressLeftGuides,
        out bool[] suppressRightGuides)
    {
        suppressLeftGuides = new bool[vertexCount];
        suppressRightGuides = new bool[vertexCount];

        if (vertexCount < 3)
            return;

        const double minTurnAngleDeg = 30.0;
        double dotThreshold = Math.Cos(minTurnAngleDeg * Math.PI / 180.0);

        for (int i = 1; i < vertexCount - 1; i++)
        {
            double prevDx = xyVertices[i * 2] - xyVertices[(i - 1) * 2];
            double prevDy = xyVertices[i * 2 + 1] - xyVertices[(i - 1) * 2 + 1];
            double nextDx = xyVertices[(i + 1) * 2] - xyVertices[i * 2];
            double nextDy = xyVertices[(i + 1) * 2 + 1] - xyVertices[i * 2 + 1];

            double prevLen = Math.Sqrt(prevDx * prevDx + prevDy * prevDy);
            double nextLen = Math.Sqrt(nextDx * nextDx + nextDy * nextDy);
            if (prevLen <= 1e-9 || nextLen <= 1e-9)
                continue;

            prevDx /= prevLen;
            prevDy /= prevLen;
            nextDx /= nextLen;
            nextDy /= nextLen;

            double dot = Math.Clamp((prevDx * nextDx) + (prevDy * nextDy), -1.0, 1.0);
            if (dot >= dotThreshold)
                continue;

            double turn = (prevDx * nextDy) - (prevDy * nextDx);
            if (Math.Abs(turn) <= 1e-9)
                continue;

            bool[] target = turn > 0.0 ? suppressLeftGuides : suppressRightGuides;
            int window = dot <= 0.5 ? 2 : 1;
            int start = Math.Max(0, i - window);
            int end = Math.Min(vertexCount - 1, i + window);
            for (int j = start; j <= end; j++)
                target[j] = true;
        }
    }

    private static double ComputeGuideSpacing(double width, double shoulderDistance)
    {
        double baseSpacing = Math.Max(width * 2.0, shoulderDistance * 2.0);
        return Math.Clamp(baseSpacing, 4.0, 15.0);
    }

    private static bool[] ComputeGuideSelection(
        double[] xyVertices,
        int vertexCount,
        double targetSpacing,
        bool[] suppressGuides)
    {
        var keepGuides = new bool[vertexCount];
        if (vertexCount == 0)
            return keepGuides;

        keepGuides[0] = true;
        if (vertexCount == 1)
            return keepGuides;

        int last = vertexCount - 1;
        keepGuides[last] = true;
        if (vertexCount == 2)
            return keepGuides;

        var cumulativeLength = new double[vertexCount];
        for (int i = 1; i < vertexCount; i++)
        {
            double dx = xyVertices[i * 2] - xyVertices[(i - 1) * 2];
            double dy = xyVertices[i * 2 + 1] - xyVertices[(i - 1) * 2 + 1];
            cumulativeLength[i] = cumulativeLength[i - 1] + Math.Sqrt((dx * dx) + (dy * dy));
        }

        double lastKeptLength = cumulativeLength[0];
        int keptInteriorCount = 0;
        for (int i = 1; i < last; i++)
        {
            if (suppressGuides[i])
                continue;

            if (cumulativeLength[i] - lastKeptLength < targetSpacing - 1e-9)
                continue;

            keepGuides[i] = true;
            lastKeptLength = cumulativeLength[i];
            keptInteriorCount++;
        }

        double totalLength = cumulativeLength[last];
        if ((totalLength - lastKeptLength) > targetSpacing * 1.5)
        {
            double targetLength = totalLength - targetSpacing;
            int bestIndex = -1;
            double bestError = double.MaxValue;
            for (int i = 1; i < last; i++)
            {
                if (suppressGuides[i] || keepGuides[i])
                    continue;

                double fromPrevious = cumulativeLength[i] - lastKeptLength;
                double toEnd = totalLength - cumulativeLength[i];
                if (fromPrevious < targetSpacing * 0.5 || toEnd < targetSpacing * 0.5)
                    continue;

                double error = Math.Abs(cumulativeLength[i] - targetLength);
                if (error < bestError)
                {
                    bestError = error;
                    bestIndex = i;
                }
            }

            if (bestIndex >= 0)
                keepGuides[bestIndex] = true;
        }

        if (keptInteriorCount == 0)
        {
            int bestIndex = -1;
            double bestError = double.MaxValue;
            double targetLength = totalLength * 0.5;
            for (int i = 1; i < last; i++)
            {
                if (suppressGuides[i])
                    continue;

                double error = Math.Abs(cumulativeLength[i] - targetLength);
                if (error < bestError)
                {
                    bestError = error;
                    bestIndex = i;
                }
            }

            if (bestIndex >= 0)
                keepGuides[bestIndex] = true;
        }

        return keepGuides;
    }

    private static void AddConstraintSample(List<double> xy, List<double> z, double x, double y, double elevation, double dedupTol)
    {
        if (xy.Count >= 2)
        {
            double dx = x - xy[^2];
            double dy = y - xy[^1];
            if (dx * dx + dy * dy <= dedupTol * dedupTol)
                return;
        }

        xy.Add(x);
        xy.Add(y);
        z.Add(elevation);
    }

    private static void ComputeDirection(double[] xyVertices, int vertexCount, int index, out double dx, out double dy)
    {
        if (index == 0)
        {
            dx = xyVertices[2] - xyVertices[0];
            dy = xyVertices[3] - xyVertices[1];
        }
        else if (index == vertexCount - 1)
        {
            dx = xyVertices[index * 2] - xyVertices[(index - 1) * 2];
            dy = xyVertices[index * 2 + 1] - xyVertices[(index - 1) * 2 + 1];
        }
        else
        {
            dx = xyVertices[(index + 1) * 2] - xyVertices[(index - 1) * 2];
            dy = xyVertices[(index + 1) * 2 + 1] - xyVertices[(index - 1) * 2 + 1];
        }

        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-12)
            len = 1.0;

        dx /= len;
        dy /= len;
    }

    private static void GetConstraintPathTangent(ConstraintPath path, int index, out double dx, out double dy)
    {
        if (path.TangentX.Length == path.VertexCount &&
            path.TangentY.Length == path.VertexCount &&
            index >= 0 &&
            index < path.VertexCount)
        {
            dx = path.TangentX[index];
            dy = path.TangentY[index];
            return;
        }

        ComputeDirection(path.XyVertices, path.VertexCount, index, out dx, out dy);
    }

    private static IEnumerable<ConstraintPath> CreateBoundaryClippedRuns(
        double[] xyVertices,
        double[] zValues,
        int vertexCount,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double tolerance)
    {
        if (!hasBoundaryLoop)
        {
            yield return MakeGeometryConstraintPath(CopyLeadingDoubles(xyVertices, vertexCount * 2), CopyLeadingDoubles(zValues, vertexCount), vertexCount);
            yield break;
        }

        var runXy = new List<double>(vertexCount * 2);
        var runZ = new List<double>(vertexCount);

        for (int i = 0; i < vertexCount - 1; i++)
        {
            List<ClippedSegment> pieces = BoundaryClipper.ClipSegmentToBoundary(
                xyVertices[i * 2],
                xyVertices[i * 2 + 1],
                zValues[i],
                xyVertices[(i + 1) * 2],
                xyVertices[(i + 1) * 2 + 1],
                zValues[i + 1],
                hasBoundaryLoop,
                boundaryLoop,
                boundaryVertexCount,
                tolerance);

            if (pieces.Count == 0)
            {
                if (runZ.Count >= 2)
                    yield return MakeGeometryConstraintPath(runXy.ToArray(), runZ.ToArray(), runZ.Count);

                runXy.Clear();
                runZ.Clear();
                continue;
            }

            foreach (ClippedSegment piece in pieces)
            {
                if (runZ.Count > 0)
                {
                    double dx = runXy[^2] - piece.StartX;
                    double dy = runXy[^1] - piece.StartY;
                    if ((dx * dx) + (dy * dy) > tolerance * tolerance)
                    {
                        yield return MakeGeometryConstraintPath(runXy.ToArray(), runZ.ToArray(), runZ.Count);
                        runXy.Clear();
                        runZ.Clear();
                    }
                }

                AppendRunPoint(runXy, runZ, piece.StartX, piece.StartY, piece.StartZ, tolerance);
                AppendRunPoint(runXy, runZ, piece.EndX, piece.EndY, piece.EndZ, tolerance);
            }
        }

        if (runZ.Count >= 2)
            yield return MakeGeometryConstraintPath(runXy.ToArray(), runZ.ToArray(), runZ.Count);
    }

    private static void AppendRunPoint(
        List<double> runXy,
        List<double> runZ,
        double x,
        double y,
        double z,
        double tolerance)
    {
        if (runZ.Count > 0)
        {
            double dx = runXy[^2] - x;
            double dy = runXy[^1] - y;
            if ((dx * dx) + (dy * dy) <= tolerance * tolerance)
            {
                runXy[^2] = x;
                runXy[^1] = y;
                runZ[^1] = z;
                return;
            }
        }

        runXy.Add(x);
        runXy.Add(y);
        runZ.Add(z);
    }

    private static void AddConstraintPolyline(
        List<SurfaceRemesher.ConstraintPolyline> constraints,
        double[] xyVertices,
        double[] zValues,
        int vertexCount)
    {
        if (vertexCount < 2)
            return;

        var points = new double[vertexCount * 3];
        for (int i = 0; i < vertexCount; i++)
        {
            points[i * 3] = xyVertices[i * 2];
            points[i * 3 + 1] = xyVertices[i * 2 + 1];
            points[i * 3 + 2] = zValues[i];
        }

        constraints.Add(new SurfaceRemesher.ConstraintPolyline(points, vertexCount, IsClosed: false, PreserveInputElevation: false));
    }

    private static int NormalizeConstraintPointCount(SurfaceRemesher.ConstraintPolyline constraint, double tolerance)
    {
        if (!constraint.IsClosed || constraint.PointCount < 3)
            return constraint.PointCount;

        int last = constraint.PointCount - 1;
        double dx = constraint.Points[last * 3] - constraint.Points[0];
        double dy = constraint.Points[last * 3 + 1] - constraint.Points[1];
        return dx * dx + dy * dy <= tolerance * tolerance
            ? last
            : constraint.PointCount;
    }

    private static bool IsBlockedByBarrier(
        PreparedBarriers preparedBarriers,
        double halfWidth,
        ClosestPathLocation closest,
        double px,
        double py,
        SpatialHashGrid2D.QueryScratch barrierScratch,
        List<int> barrierCandidates)
    {
        if (preparedBarriers.Segments.Length == 0)
            return false;

        double side = closest.SideSign >= 0.0 ? 1.0 : -1.0;
        double normalX = -closest.DirectionY;
        double normalY = closest.DirectionX;
        double edgeX = closest.ProjectedX + (normalX * halfWidth * side);
        double edgeY = closest.ProjectedY + (normalY * halfWidth * side);

        return GradingBarriers.IsCrossedByBarrier(
            preparedBarriers, edgeX, edgeY, px, py, barrierScratch, barrierCandidates);
    }

    private static double[] CopyLeadingDoubles(double[] values, int length)
    {
        var copy = new double[length];
        Array.Copy(values, copy, length);
        return copy;
    }

    private static int[]? AddShoulderConstraint(
        double[] shoulderXy,
        int vertexCount,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double tolerance,
        Func<double, double, int> addVertex,
        List<(int a, int b)> segList,
        PreparedBarriers barriers,
        SpatialHashGrid2D.QueryScratch barrierScratch,
        List<int> barrierCandidates)
    {
        var indices = new int[vertexCount];
        Array.Fill(indices, -1);
        bool hasSegment = false;

        for (int i = 0; i < vertexCount - 1; i++)
        {
            double startX = shoulderXy[i * 2];
            double startY = shoulderXy[i * 2 + 1];
            double endX = shoulderXy[(i + 1) * 2];
            double endY = shoulderXy[(i + 1) * 2 + 1];

            bool clippedByBarrier = GradingBarriers.TryClipSegment(
                barriers, startX, startY, endX, endY,
                barrierScratch, barrierCandidates,
                out endX, out endY);

            List<ClippedSegment> pieces = BoundaryClipper.ClipSegmentToBoundary(
                startX, startY, 0.0,
                endX, endY, 0.0,
                hasBoundaryLoop,
                boundaryLoop,
                boundaryVertexCount,
                tolerance);

            foreach (ClippedSegment piece in pieces)
            {
                int startIndex = piece.StartT <= 1e-9
                    ? EnsureShoulderSampleIndex(indices, i, shoulderXy, addVertex)
                    : addVertex(piece.StartX, piece.StartY);
                int endIndex = (!clippedByBarrier && piece.EndT >= 1.0 - 1e-9)
                    ? EnsureShoulderSampleIndex(indices, i + 1, shoulderXy, addVertex)
                    : addVertex(piece.EndX, piece.EndY);

                if (startIndex == endIndex)
                    continue;

                segList.Add((startIndex, endIndex));
                hasSegment = true;
            }
        }

        return hasSegment ? indices : null;
    }

    private static void AddShoulderGuideConstraints(
        List<SurfaceRemesher.ConstraintPolyline> constraints,
        double[] roadXy,
        double[] shoulderXy,
        double[] zValues,
        int vertexCount,
        bool[]? keepGuides,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double tolerance)
    {
        for (int i = 0; i < vertexCount; i++)
        {
            if (keepGuides != null && !keepGuides[i])
                continue;

            double roadX = roadXy[i * 2];
            double roadY = roadXy[i * 2 + 1];
            double shoulderX = shoulderXy[i * 2];
            double shoulderY = shoulderXy[i * 2 + 1];
            List<ClippedSegment> pieces = BoundaryClipper.ClipSegmentToBoundary(
                roadX,
                roadY,
                zValues[i],
                shoulderX,
                shoulderY,
                zValues[i],
                hasBoundaryLoop,
                boundaryLoop,
                boundaryVertexCount,
                tolerance);

            foreach (ClippedSegment piece in pieces)
            {
                double dx = piece.EndX - piece.StartX;
                double dy = piece.EndY - piece.StartY;
                if ((dx * dx) + (dy * dy) <= tolerance * tolerance)
                    continue;

                constraints.Add(new SurfaceRemesher.ConstraintPolyline(
                    new[]
                    {
                        piece.StartX, piece.StartY, piece.StartZ,
                        piece.EndX, piece.EndY, piece.EndZ
                    },
                    PointCount: 2,
                    IsClosed: false,
                    PreserveInputElevation: false));
            }
        }
    }

    private static void AddShoulderGuideSegments(
        List<double> xyList,
        int[] roadIndices,
        double[]? shoulderXy,
        int[]? shoulderIndices,
        int vertexCount,
        bool[]? keepGuides,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double tolerance,
        Func<double, double, int> addVertex,
        List<(int a, int b)> segList,
        PreparedBarriers barriers,
        SpatialHashGrid2D.QueryScratch barrierScratch,
        List<int> barrierCandidates)
    {
        if (shoulderXy == null)
            return;

        for (int i = 0; i < vertexCount; i++)
        {
            if (keepGuides != null && !keepGuides[i])
                continue;

            double roadX = xyList[roadIndices[i] * 2];
            double roadY = xyList[roadIndices[i] * 2 + 1];
            double shoulderX = shoulderXy[i * 2];
            double shoulderY = shoulderXy[i * 2 + 1];

            bool clippedByBarrier = GradingBarriers.TryClipSegment(
                barriers, roadX, roadY, shoulderX, shoulderY,
                barrierScratch, barrierCandidates,
                out shoulderX, out shoulderY);

            List<ClippedSegment> pieces = BoundaryClipper.ClipSegmentToBoundary(
                roadX, roadY, 0.0,
                shoulderX, shoulderY, 0.0,
                hasBoundaryLoop,
                boundaryLoop,
                boundaryVertexCount,
                tolerance);

            foreach (ClippedSegment piece in pieces)
            {
                int startIndex = piece.StartT <= 1e-9
                    ? roadIndices[i]
                    : addVertex(piece.StartX, piece.StartY);
                int endIndex = (!clippedByBarrier && piece.EndT >= 1.0 - 1e-9 && shoulderIndices != null && shoulderIndices[i] >= 0)
                    ? shoulderIndices[i]
                    : addVertex(piece.EndX, piece.EndY);

                if (startIndex != endIndex)
                    segList.Add((startIndex, endIndex));
            }
        }
    }

    private static int EnsureShoulderSampleIndex(
        int[] indices,
        int pointIndex,
        double[] shoulderXy,
        Func<double, double, int> addVertex)
    {
        if (indices[pointIndex] >= 0)
            return indices[pointIndex];

        indices[pointIndex] = addVertex(shoulderXy[pointIndex * 2], shoulderXy[pointIndex * 2 + 1]);
        return indices[pointIndex];
    }

    private static bool IsInsideOrOnBoundary(
        double x,
        double y,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double tolerance)
    {
        return BoundaryClipper.IsInsideOrOnBoundary(x, y, hasBoundaryLoop, boundaryLoop, boundaryVertexCount, tolerance);
    }

    private static bool TryFindClosestPathSample(PathDefinition path, double px, double py, out double closestDist, out double closestPathZ)
    {
        if (!TryFindClosestPathLocation(path.XyVertices, path.ZValues, path.VertexCount, px, py, out ClosestPathLocation closest))
        {
            closestDist = double.MaxValue;
            closestPathZ = 0.0;
            return false;
        }

        closestDist = closest.Distance;
        closestPathZ = closest.PathZ;
        return true;
    }

    private static bool TryFindClosestPathLocation(
        ConstraintPath path,
        double px,
        double py,
        out ClosestPathLocation closest)
    {
        return TryFindClosestPathLocation(
            path.XyVertices, path.ZValues, path.VertexCount,
            path.TangentX, path.TangentY,
            px, py, out closest);
    }

    private static bool TryFindClosestPathLocation(
        double[] xyVertices,
        double[] zValues,
        int vertexCount,
        double px,
        double py,
        out ClosestPathLocation closest)
    {
        return TryFindClosestPathLocation(
            xyVertices, zValues, vertexCount,
            null, null,
            px, py, out closest);
    }

    /// <summary>
    /// Finds the closest point on the path polyline and returns a
    /// <see cref="ClosestPathLocation"/>. When smooth tangent arrays are
    /// provided, <c>SideSign</c> and <c>DirectionX/Y</c> are computed from
    /// the smoothly interpolated tangent at the closest position rather than
    /// the raw segment direction. This eliminates the discrete SideSign flip
    /// that occurs at segment-ownership (Voronoi) boundaries near path bends,
    /// which was the primary cause of cut/fill polarity inversions in the
    /// shoulder reference-profile lookup.
    /// </summary>
    private static bool TryFindClosestPathLocation(
        double[] xyVertices,
        double[] zValues,
        int vertexCount,
        double[]? tangentX,
        double[]? tangentY,
        double px,
        double py,
        out ClosestPathLocation closest)
    {
        double closestDistSq = double.MaxValue;
        closest = default;

        for (int s = 0; s < vertexCount - 1; s++)
        {
            double ax = xyVertices[s * 2];
            double ay = xyVertices[s * 2 + 1];
            double bx = xyVertices[(s + 1) * 2];
            double by = xyVertices[(s + 1) * 2 + 1];

            double sdx = bx - ax;
            double sdy = by - ay;
            double segLen = sdx * sdx + sdy * sdy;
            if (segLen < 1e-20)
                continue;

            double t = ((px - ax) * sdx + (py - ay) * sdy) / segLen;
            t = Math.Clamp(t, 0.0, 1.0);

            double projX = ax + t * sdx;
            double projY = ay + t * sdy;
            double dx = px - projX;
            double dy = py - projY;
            double distSq = (dx * dx) + (dy * dy);

            if (distSq < closestDistSq)
            {
                double segLength = Math.Sqrt(segLen);

                // Compute direction and SideSign from smooth tangent when available.
                // Linearly interpolate station tangents at parameter t, then normalize.
                // Falls back to raw segment direction when no tangents are provided.
                double dirX, dirY, sideSign;
                if (tangentX != null && tangentY != null &&
                    s < tangentX.Length && s + 1 < tangentX.Length)
                {
                    double blendX = tangentX[s] + t * (tangentX[s + 1] - tangentX[s]);
                    double blendY = tangentY[s] + t * (tangentY[s + 1] - tangentY[s]);
                    double blendLen = Math.Sqrt(blendX * blendX + blendY * blendY);
                    if (blendLen > 1e-9)
                    {
                        dirX = blendX / blendLen;
                        dirY = blendY / blendLen;
                    }
                    else
                    {
                        // Interpolated tangent degenerate — fall back to segment direction
                        dirX = sdx / segLength;
                        dirY = sdy / segLength;
                    }
                    // SideSign: cross product of smooth tangent with (query − projected)
                    sideSign = dirX * (py - projY) - dirY * (px - projX);
                }
                else
                {
                    dirX = sdx / segLength;
                    dirY = sdy / segLength;
                    sideSign = (sdx * (py - ay)) - (sdy * (px - ax));
                }

                closestDistSq = distSq;
                closest = new ClosestPathLocation(
                    s,
                    t,
                    Math.Sqrt(distSq),
                    zValues[s] + t * (zValues[s + 1] - zValues[s]),
                    sideSign,
                    projX,
                    projY,
                    dirX,
                    dirY);
            }
        }

        return closestDistSq < double.MaxValue;
    }

    /// <summary>
    /// Build GradingResult with volumes and daylight line.
    /// </summary>
    private static GradingResult BuildResult(
        double[] outXy, double[] origZ, double[] newZ,
        double[] finalVerts, int vertCount,
        int[] finalFaces, int faceCount,
        IReadOnlyList<OutputPolyline>? outputPolylines = null)
    {
        double cutVol = 0, fillVol = 0;
        for (int f = 0; f < faceCount; f++)
        {
            int i0 = finalFaces[f * 3], i1 = finalFaces[f * 3 + 1], i2 = finalFaces[f * 3 + 2];

            double area2d = Math.Abs(
                (outXy[i1 * 2] - outXy[i0 * 2]) * (outXy[i2 * 2 + 1] - outXy[i0 * 2 + 1])
              - (outXy[i2 * 2] - outXy[i0 * 2]) * (outXy[i1 * 2 + 1] - outXy[i0 * 2 + 1])
            ) * 0.5;

            double dz0 = newZ[i0] - origZ[i0];
            double dz1 = newZ[i1] - origZ[i1];
            double dz2 = newZ[i2] - origZ[i2];
            double avgDz = (dz0 + dz1 + dz2) / 3.0;

            double vol = area2d * avgDz;
            if (vol > 0) fillVol += vol;
            else cutVol += -vol;
        }

        var daylightPts = new List<double>();
        var processedEdges = new HashSet<long>();

        for (int f = 0; f < faceCount; f++)
        {
            int i0 = finalFaces[f * 3], i1 = finalFaces[f * 3 + 1], i2 = finalFaces[f * 3 + 2];
            CheckDaylightEdge(i0, i1, outXy, newZ, origZ, processedEdges, daylightPts);
            CheckDaylightEdge(i1, i2, outXy, newZ, origZ, processedEdges, daylightPts);
            CheckDaylightEdge(i2, i0, outXy, newZ, origZ, processedEdges, daylightPts);
        }

        return new GradingResult(
            finalVerts, vertCount,
            finalFaces, faceCount,
            cutVol, fillVol,
            daylightPts.ToArray(), daylightPts.Count / 3,
            outputPolylines);
    }

    private static void IncrEdge(Dictionary<long, int> dict, int a, int b)
    {
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        dict[key] = dict.GetValueOrDefault(key, 0) + 1;
    }

    private static void CheckDaylightEdge(int a, int b,
        double[] xy, double[] newZ, double[] origZ,
        HashSet<long> processed, List<double> pts)
    {
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        if (!processed.Add(key)) return;

        double dzA = newZ[a] - origZ[a];
        double dzB = newZ[b] - origZ[b];
        const double threshold = 0.001;

        if ((dzA > threshold && dzB < -threshold) || (dzA < -threshold && dzB > threshold))
        {
            double t = dzA / (dzA - dzB);
            pts.Add(xy[a * 2] + t * (xy[b * 2] - xy[a * 2]));
            pts.Add(xy[a * 2 + 1] + t * (xy[b * 2 + 1] - xy[a * 2 + 1]));
            pts.Add(newZ[a] + t * (newZ[b] - newZ[a]));
        }
        else if (Math.Abs(dzA) <= threshold && Math.Abs(dzB) > threshold)
        {
            pts.Add(xy[a * 2]); pts.Add(xy[a * 2 + 1]); pts.Add(newZ[a]);
        }
        else if (Math.Abs(dzB) <= threshold && Math.Abs(dzA) > threshold)
        {
            pts.Add(xy[b * 2]); pts.Add(xy[b * 2 + 1]); pts.Add(newZ[b]);
        }
    }
}
