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

    private readonly record struct ConstraintPath(double[] XyVertices, double[] ZValues, int VertexCount);

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
    /// Later paths in the array override earlier ones in overlapping zones.
    /// </summary>
    public static GradingResult? Grade(
        double[] vertices, int vertexCount,
        int[] faces, int faceCount,
        PathDefinition[] paths,
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
        var result = GradeWithEdges(vertices, vertexCount, faces, faceCount, paths, out errorMessage);
        if (result != null) return result;

        // Fallback: just modify Z of existing mesh
        errorMessage = null;
        return GradeZOnly(vertices, vertexCount, faces, faceCount, paths, out errorMessage);
    }

    public static double[] ApplyGradingZ(double[] topologyVertices, int vertexCount, PathDefinition[] paths)
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

        ApplyPathGrading(paths, outXy, origZ, newZ, vertexCount);

        var gradedVertices = new double[vertexCount * 3];
        for (int i = 0; i < vertexCount; i++)
        {
            gradedVertices[i * 3] = topologyVertices[i * 3];
            gradedVertices[i * 3 + 1] = topologyVertices[i * 3 + 1];
            gradedVertices[i * 3 + 2] = newZ[i];
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
            AddConstraintPolyline(constraints, constraintPath.XyVertices, constraintPath.ZValues, constraintPath.VertexCount);

            var leftRoadXy = new double[constraintPath.VertexCount * 2];
            var rightRoadXy = new double[constraintPath.VertexCount * 2];
            double[]? leftShoulderXy = shoulderDistance > dedupTol ? new double[constraintPath.VertexCount * 2] : null;
            double[]? rightShoulderXy = shoulderDistance > dedupTol ? new double[constraintPath.VertexCount * 2] : null;

            for (int i = 0; i < constraintPath.VertexCount; i++)
            {
                double cx = constraintPath.XyVertices[i * 2];
                double cy = constraintPath.XyVertices[i * 2 + 1];
                ComputeDirection(constraintPath.XyVertices, constraintPath.VertexCount, i, out double dx, out double dy);

                double roadPx = -dy * halfWidth;
                double roadPy = dx * halfWidth;
                leftRoadXy[i * 2] = cx + roadPx;
                leftRoadXy[i * 2 + 1] = cy + roadPy;
                rightRoadXy[i * 2] = cx - roadPx;
                rightRoadXy[i * 2 + 1] = cy - roadPy;

                if (leftShoulderXy != null && rightShoulderXy != null)
                {
                    double shoulderOffset = halfWidth + shoulderDistance;
                    double shoulderPx = -dy * shoulderOffset;
                    double shoulderPy = dx * shoulderOffset;
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
            }

            if (rightShoulderXy != null)
            {
                foreach (var run in CreateBoundaryClippedRuns(rightShoulderXy, constraintPath.ZValues, constraintPath.VertexCount, hasBoundaryLoop, boundaryLoop, boundaryVertexCount, dedupTol))
                    AddConstraintPolyline(constraints, run.XyVertices, run.ZValues, run.VertexCount);
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
        out string? errorMessage)
    {
        errorMessage = null;
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
        var edgeFaceCount = new Dictionary<long, int>();
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

        // For each path, compute road edges and add as open constrained polylines
        foreach (var path in paths)
        {
            double halfWidth = path.Width * 0.5;
            double shoulderDistance = ComputePathShoulderDistance(vertices, vertexCount, path);
            var constraintPath = BuildConstraintPolyline(
                path,
                ComputeConstraintSegmentLength(path, shoulderDistance),
                dedupTol);
            int n = constraintPath.VertexCount;

            var centerIdx = new int[n];
            var leftIdx = new int[n];
            var rightIdx = new int[n];
            double[]? leftShoulderXy = shoulderDistance > dedupTol ? new double[n * 2] : null;
            double[]? rightShoulderXy = shoulderDistance > dedupTol ? new double[n * 2] : null;
            int[]? leftShoulderIdx = shoulderDistance > dedupTol ? new int[n] : null;
            int[]? rightShoulderIdx = shoulderDistance > dedupTol ? new int[n] : null;

            for (int i = 0; i < n; i++)
            {
                double cx = constraintPath.XyVertices[i * 2], cy = constraintPath.XyVertices[i * 2 + 1];
                ComputeDirection(constraintPath.XyVertices, constraintPath.VertexCount, i, out double dx, out double dy);

                // Perpendicular offset
                double roadPx = -dy * halfWidth, roadPy = dx * halfWidth;

                centerIdx[i] = AddVertex(cx, cy);
                leftIdx[i] = AddVertex(cx + roadPx, cy + roadPy);
                rightIdx[i] = AddVertex(cx - roadPx, cy - roadPy);

                if (leftShoulderIdx != null && rightShoulderIdx != null)
                {
                    double shoulderOffset = halfWidth + shoulderDistance;
                    double shoulderPx = -dy * shoulderOffset;
                    double shoulderPy = dx * shoulderOffset;
                    leftShoulderXy![i * 2] = cx + shoulderPx;
                    leftShoulderXy[i * 2 + 1] = cy + shoulderPy;
                    rightShoulderXy![i * 2] = cx - shoulderPx;
                    rightShoulderXy[i * 2 + 1] = cy - shoulderPy;
                }
            }

            if (leftShoulderIdx != null && leftShoulderXy != null)
                leftShoulderIdx = AddShoulderConstraint(leftShoulderXy, n, hasBoundaryLoop, boundaryLoop, boundaryVertexCount, dedupTol, AddVertex);

            if (rightShoulderIdx != null && rightShoulderXy != null)
                rightShoulderIdx = AddShoulderConstraint(rightShoulderXy, n, hasBoundaryLoop, boundaryLoop, boundaryVertexCount, dedupTol, AddVertex);

            // Add constrained segments (open polylines, NOT closed)
            for (int i = 0; i < n - 1; i++)
            {
                if (centerIdx[i] != centerIdx[i + 1])
                    segList.Add((centerIdx[i], centerIdx[i + 1]));
                if (leftIdx[i] != leftIdx[i + 1])
                    segList.Add((leftIdx[i], leftIdx[i + 1]));
                if (rightIdx[i] != rightIdx[i + 1])
                    segList.Add((rightIdx[i], rightIdx[i + 1]));

                if (leftShoulderIdx != null && leftShoulderIdx[i] >= 0 && leftShoulderIdx[i + 1] >= 0 && leftShoulderIdx[i] != leftShoulderIdx[i + 1])
                    segList.Add((leftShoulderIdx[i], leftShoulderIdx[i + 1]));
                if (rightShoulderIdx != null && rightShoulderIdx[i] >= 0 && rightShoulderIdx[i + 1] >= 0 && rightShoulderIdx[i] != rightShoulderIdx[i + 1])
                    segList.Add((rightShoulderIdx[i], rightShoulderIdx[i + 1]));
            }
        }

        // Triangulate (NO quality constraints)
        int totalVerts = zList.Count;
        if (totalVerts < 3)
        {
            errorMessage = "Too few vertices for triangulation.";
            return null;
        }

        var triMesh = TriangulationHelper.Triangulate(
            xyList, totalVerts, segList,
            0, 0, // no quality refinement
            out string? triWarning,
            convex: false);

        if (triMesh == null)
        {
            errorMessage = triWarning ?? "Triangulation failed.";
            return null;
        }

        if (triWarning != null)
            errorMessage = triWarning;

        var extracted = TriangleNetExtractor.Extract(triMesh);
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

        // Grade Z
        ApplyPathGrading(paths, outXy, origZ, newZ, outVertCount);

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

        return BuildResult(outXy, origZ, newZ, finalVerts, outVertCount, finalFaces, outFaceCount);
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
        errorMessage = "Using Z-only grading (road edges may not be sharp).";

        var outXy = new double[vertexCount * 2];
        var origZ = new double[vertexCount];
        var newZ = new double[vertexCount];

        for (int i = 0; i < vertexCount; i++)
        {
            outXy[i * 2] = vertices[i * 3];
            outXy[i * 2 + 1] = vertices[i * 3 + 1];
            origZ[i] = vertices[i * 3 + 2];
            newZ[i] = vertices[i * 3 + 2];
        }

        ApplyPathGrading(paths, outXy, origZ, newZ, vertexCount);

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
    /// Shared grading logic: for each vertex, find nearest path and assign Z.
    /// </summary>
    private static void ApplyPathGrading(
        PathDefinition[] paths,
        double[] outXy, double[] origZ, double[] newZ, int vertCount)
    {
        foreach (var path in paths)
        {
            double halfWidth = path.Width * 0.5;
            double slopeRatio = Math.Tan(path.SlopeAngleDeg * Math.PI / 180.0);

            // Compute bbox for fast filtering
            double mnX = double.MaxValue, mxX = double.MinValue;
            double mnY = double.MaxValue, mxY = double.MinValue;
            for (int i = 0; i < path.VertexCount; i++)
            {
                double x = path.XyVertices[i * 2], y = path.XyVertices[i * 2 + 1];
                if (x < mnX) mnX = x; if (x > mxX) mxX = x;
                if (y < mnY) mnY = y; if (y > mxY) mxY = y;
            }

            double maxInfluence = halfWidth + ComputePathShoulderDistance(outXy, origZ, vertCount, path);

            mnX -= maxInfluence; mxX += maxInfluence;
            mnY -= maxInfluence; mxY += maxInfluence;

            for (int i = 0; i < vertCount; i++)
            {
                double px = outXy[i * 2], py = outXy[i * 2 + 1];

                if (px < mnX || px > mxX || py < mnY || py > mxY) continue;

                if (!TryFindClosestPathSample(path, px, py, out double closestDist, out double closestPathZ))
                    continue;

                if (closestDist <= halfWidth + 1e-6)
                {
                    // Inside road — use path Z
                    newZ[i] = closestPathZ;
                }
                else
                {
                    // Transition zone (same as pad transition)
                    double distFromEdge = closestDist - halfWidth;
                    double dz = origZ[i] - closestPathZ;
                    double absDz = Math.Abs(dz);
                    double neededDist = slopeRatio > 1e-12 ? absDz / slopeRatio : double.MaxValue;
                    if (path.MaxDistance > 0) neededDist = Math.Min(neededDist, path.MaxDistance);

                    if (distFromEdge < neededDist)
                    {
                        double rise = distFromEdge * slopeRatio;
                        if (rise < absDz)
                            newZ[i] = closestPathZ + Math.Sign(dz) * rise;
                    }
                }
            }
        }
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

        return ComputePathShoulderDistance(xy, z, vertexCount, path);
    }

    private static double ComputePathShoulderDistance(double[] xy, double[] z, int vertexCount, PathDefinition path)
    {
        if (path.MaxDistance > 0)
            return path.MaxDistance;

        double slopeRatio = Math.Tan(path.SlopeAngleDeg * Math.PI / 180.0);
        if (slopeRatio <= 1e-12)
            return 100.0;

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

            if (TryFindClosestPathSample(path, px, py, out _, out double pathZ))
            {
                double dz = Math.Abs(z[i] - pathZ);
                if (dz > maxZDiff)
                    maxZDiff = dz;
            }
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

    private static ConstraintPath BuildConstraintPolyline(PathDefinition path, double maxSegmentLength, double dedupTol)
    {
        if (path.VertexCount < 2 || maxSegmentLength <= dedupTol)
            return new ConstraintPath((double[])path.XyVertices.Clone(), (double[])path.ZValues.Clone(), path.VertexCount);

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

        return new ConstraintPath(xy.ToArray(), z.ToArray(), xy.Count / 2);
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
            yield return new ConstraintPath((double[])xyVertices.Clone(), (double[])zValues.Clone(), vertexCount);
            yield break;
        }

        var runXy = new List<double>(vertexCount * 2);
        var runZ = new List<double>(vertexCount);

        for (int i = 0; i < vertexCount - 1; i++)
        {
            bool startInside = IsInsideOrOnBoundary(xyVertices[i * 2], xyVertices[i * 2 + 1], hasBoundaryLoop, boundaryLoop, boundaryVertexCount, tolerance);
            bool endInside = IsInsideOrOnBoundary(xyVertices[(i + 1) * 2], xyVertices[(i + 1) * 2 + 1], hasBoundaryLoop, boundaryLoop, boundaryVertexCount, tolerance);
            if (!startInside || !endInside)
            {
                if (runZ.Count >= 2)
                    yield return new ConstraintPath(runXy.ToArray(), runZ.ToArray(), runZ.Count);

                runXy.Clear();
                runZ.Clear();
                continue;
            }

            if (runZ.Count == 0)
            {
                runXy.Add(xyVertices[i * 2]);
                runXy.Add(xyVertices[i * 2 + 1]);
                runZ.Add(zValues[i]);
            }

            runXy.Add(xyVertices[(i + 1) * 2]);
            runXy.Add(xyVertices[(i + 1) * 2 + 1]);
            runZ.Add(zValues[i + 1]);
        }

        if (runZ.Count >= 2)
            yield return new ConstraintPath(runXy.ToArray(), runZ.ToArray(), runZ.Count);
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

    private static int[]? AddShoulderConstraint(
        double[] shoulderXy,
        int vertexCount,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double tolerance,
        Func<double, double, int> addVertex)
    {
        var usable = new bool[vertexCount];
        bool hasSegment = false;

        for (int i = 0; i < vertexCount - 1; i++)
        {
            bool startInside = IsInsideOrOnBoundary(shoulderXy[i * 2], shoulderXy[i * 2 + 1], hasBoundaryLoop, boundaryLoop, boundaryVertexCount, tolerance);
            bool endInside = IsInsideOrOnBoundary(shoulderXy[(i + 1) * 2], shoulderXy[(i + 1) * 2 + 1], hasBoundaryLoop, boundaryLoop, boundaryVertexCount, tolerance);
            if (!startInside || !endInside)
                continue;

            usable[i] = true;
            usable[i + 1] = true;
            hasSegment = true;
        }

        if (!hasSegment)
            return null;

        var indices = new int[vertexCount];
        Array.Fill(indices, -1);
        for (int i = 0; i < vertexCount; i++)
        {
            if (!usable[i])
                continue;

            indices[i] = addVertex(shoulderXy[i * 2], shoulderXy[i * 2 + 1]);
        }

        return indices;
    }

    private static bool IsInsideOrOnBoundary(
        double x,
        double y,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double tolerance)
    {
        if (!hasBoundaryLoop)
            return true;

        return PadGrader.PointInPolygon(x, y, boundaryLoop, boundaryVertexCount)
            || PadGrader.DistToPolygon(x, y, boundaryLoop, boundaryVertexCount) <= tolerance;
    }

    private static bool TryFindClosestPathSample(PathDefinition path, double px, double py, out double closestDist, out double closestPathZ)
    {
        closestDist = double.MaxValue;
        closestPathZ = 0;

        for (int s = 0; s < path.VertexCount - 1; s++)
        {
            double ax = path.XyVertices[s * 2];
            double ay = path.XyVertices[s * 2 + 1];
            double bx = path.XyVertices[(s + 1) * 2];
            double by = path.XyVertices[(s + 1) * 2 + 1];

            double sdx = bx - ax;
            double sdy = by - ay;
            double segLen = sdx * sdx + sdy * sdy;
            if (segLen < 1e-20)
                continue;

            double t = ((px - ax) * sdx + (py - ay) * sdy) / segLen;
            t = Math.Clamp(t, 0.0, 1.0);

            double projX = ax + t * sdx;
            double projY = ay + t * sdy;
            double dist = Math.Sqrt((px - projX) * (px - projX) + (py - projY) * (py - projY));

            if (dist < closestDist)
            {
                closestDist = dist;
                closestPathZ = path.ZValues[s] + t * (path.ZValues[s + 1] - path.ZValues[s]);
            }
        }

        return closestDist < double.MaxValue;
    }

    /// <summary>
    /// Build GradingResult with volumes and daylight line.
    /// </summary>
    private static GradingResult BuildResult(
        double[] outXy, double[] origZ, double[] newZ,
        double[] finalVerts, int vertCount,
        int[] finalFaces, int faceCount)
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
            daylightPts.ToArray(), daylightPts.Count / 3);
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
