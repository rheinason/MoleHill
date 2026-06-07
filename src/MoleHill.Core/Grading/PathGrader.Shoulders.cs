using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

public static partial class PathGrader
{
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

    private static bool[] BuildStationConstraintSelection(
        ConstraintPath constraintPath,
        PathSectionResolutionStatus[] leftStatuses,
        PathSectionResolutionStatus[] rightStatuses,
        double targetSpacing)
    {
        int vertexCount = constraintPath.VertexCount;
        var keepStations = new bool[vertexCount];
        if (vertexCount == 0)
            return keepStations;

        ComputeInsideCornerGuideSuppression(
            constraintPath.XyVertices,
            vertexCount,
            out bool[] suppressLeftGuides,
            out bool[] suppressRightGuides);

        var suppressStations = new bool[vertexCount];
        for (int i = 0; i < vertexCount; i++)
            suppressStations[i] = suppressLeftGuides[i] || suppressRightGuides[i];

        keepStations = ComputeGuideSelection(
            constraintPath.XyVertices,
            vertexCount,
            Math.Max(targetSpacing, 1e-6),
            suppressStations);

        keepStations[0] = true;
        keepStations[vertexCount - 1] = true;
        int statusCount = Math.Min(vertexCount, Math.Min(leftStatuses.Length, rightStatuses.Length));
        for (int i = 1; i < statusCount; i++)
        {
            if (leftStatuses[i] == leftStatuses[i - 1] &&
                rightStatuses[i] == rightStatuses[i - 1])
            {
                continue;
            }

            keepStations[i - 1] = true;
            keepStations[i] = true;
        }

        return keepStations;
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

    private static void AddPathStationConstraints(
        List<SurfaceRemesher.ConstraintPolyline> constraints,
        ConstraintPath centerPath,
        double[] leftRoadXy,
        double[] rightRoadXy,
        double[] leftShoulderXy,
        double[] rightShoulderXy,
        double[] leftShoulderZ,
        double[] rightShoulderZ,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        bool[]? keepStations,
        int vertexCount,
        double tolerance,
        PreparedBarriers barriers,
        SpatialHashGrid2D.QueryScratch barrierScratch,
        List<int> barrierCandidates)
    {
        for (int i = 0; i < vertexCount; i++)
        {
            if (keepStations != null && (i >= keepStations.Length || !keepStations[i]))
                continue;

            var points = new List<double>(15);
            double centerX = centerPath.XyVertices[i * 2];
            double centerY = centerPath.XyVertices[i * 2 + 1];
            double centerZ = centerPath.ZValues[i];

            AppendConstraintPoint(points, leftShoulderXy[i * 2], leftShoulderXy[i * 2 + 1], leftShoulderZ[i], tolerance);
            AppendConstraintPoint(points, leftRoadXy[i * 2], leftRoadXy[i * 2 + 1], centerZ, tolerance);
            AppendConstraintPoint(points, centerX, centerY, centerZ, tolerance);
            AppendConstraintPoint(points, rightRoadXy[i * 2], rightRoadXy[i * 2 + 1], centerZ, tolerance);
            AppendConstraintPoint(points, rightShoulderXy[i * 2], rightShoulderXy[i * 2 + 1], rightShoulderZ[i], tolerance);

            int pointCount = points.Count / 3;
            if (pointCount < 2)
                continue;

            var stationXy = new double[pointCount * 2];
            var stationZ = new double[pointCount];
            for (int pointIndex = 0; pointIndex < pointCount; pointIndex++)
            {
                stationXy[pointIndex * 2] = points[pointIndex * 3];
                stationXy[(pointIndex * 2) + 1] = points[(pointIndex * 3) + 1];
                stationZ[pointIndex] = points[(pointIndex * 3) + 2];
            }

            foreach (ConstraintPath run in CreateClippedRuns(
                         stationXy,
                         stationZ,
                         pointCount,
                         hasBoundaryLoop,
                         boundaryLoop,
                         boundaryVertexCount,
                         tolerance,
                         barriers,
                         barrierScratch,
                         barrierCandidates))
            {
                AddConstraintPolyline(constraints, run.XyVertices, run.ZValues, run.VertexCount);
            }
        }
    }

    private static bool IsApproximatelyStraight(double[] xyVertices, int vertexCount, double tolerance)
    {
        if (vertexCount < 3)
            return true;

        double ax = xyVertices[0];
        double ay = xyVertices[1];
        double bx = xyVertices[(vertexCount - 1) * 2];
        double by = xyVertices[((vertexCount - 1) * 2) + 1];
        double dx = bx - ax;
        double dy = by - ay;
        double lengthSq = (dx * dx) + (dy * dy);
        if (lengthSq <= tolerance * tolerance)
            return true;

        double toleranceSq = tolerance * tolerance;
        for (int i = 1; i < vertexCount - 1; i++)
        {
            double px = xyVertices[i * 2];
            double py = xyVertices[i * 2 + 1];
            double t = Math.Clamp((((px - ax) * dx) + ((py - ay) * dy)) / lengthSq, 0.0, 1.0);
            double projectedX = ax + (dx * t);
            double projectedY = ay + (dy * t);
            double offsetX = px - projectedX;
            double offsetY = py - projectedY;
            if (((offsetX * offsetX) + (offsetY * offsetY)) > toleranceSq)
                return false;
        }

        return true;
    }

    private static bool TrySampleConstraintPathZ(
        double[] xyVertices,
        double[] zValues,
        int vertexCount,
        double px,
        double py,
        out double sampledZ)
    {
        sampledZ = 0.0;
        double bestDistanceSq = double.MaxValue;
        if (vertexCount < 2)
            return false;

        for (int i = 0; i < vertexCount - 1; i++)
        {
            double ax = xyVertices[i * 2];
            double ay = xyVertices[i * 2 + 1];
            double bx = xyVertices[(i + 1) * 2];
            double by = xyVertices[(i + 1) * 2 + 1];
            double dx = bx - ax;
            double dy = by - ay;
            double segmentLengthSq = (dx * dx) + (dy * dy);
            double t = 0.0;
            if (segmentLengthSq > 1e-18)
                t = Math.Clamp((((px - ax) * dx) + ((py - ay) * dy)) / segmentLengthSq, 0.0, 1.0);

            double projectedX = ax + (dx * t);
            double projectedY = ay + (dy * t);
            double offsetX = px - projectedX;
            double offsetY = py - projectedY;
            double distanceSq = (offsetX * offsetX) + (offsetY * offsetY);
            if (distanceSq >= bestDistanceSq)
                continue;

            bestDistanceSq = distanceSq;
            sampledZ = InterpolateSectionValue(zValues[i], zValues[i + 1], t);
        }

        return bestDistanceSq < double.MaxValue;
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

    private static double[] CopyLeadingDoubles(double[] values, int length)
    {
        var copy = new double[length];
        Array.Copy(values, copy, length);
        return copy;
    }

    private static bool HasDistinctShoulderSamples(double[] edgeXy, double[] shoulderXy, int vertexCount, double tolerance)
    {
        double tolSq = tolerance * tolerance;
        for (int i = 0; i < vertexCount; i++)
        {
            double dx = shoulderXy[i * 2] - edgeXy[i * 2];
            double dy = shoulderXy[(i * 2) + 1] - edgeXy[(i * 2) + 1];
            if ((dx * dx) + (dy * dy) > tolSq)
                return true;
        }

        return false;
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
        double[] roadZValues,
        double[] shoulderZValues,
        int vertexCount,
        bool[]? keepGuides,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double tolerance)
    {
        AddShoulderGuideConstraints(
            constraints,
            roadXy,
            shoulderXy,
            roadZValues,
            shoulderZValues,
            vertexCount,
            keepGuides,
            hasBoundaryLoop,
            boundaryLoop,
            boundaryVertexCount,
            tolerance,
            PreparedBarriers.Empty,
            new SpatialHashGrid2D.QueryScratch(1),
            new List<int>(8));
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

    private static void AddShoulderGuideConstraints(
        List<SurfaceRemesher.ConstraintPolyline> constraints,
        double[] roadXy,
        double[] shoulderXy,
        double[] roadZValues,
        double[] shoulderZValues,
        int vertexCount,
        bool[]? keepGuides,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double tolerance,
        PreparedBarriers barriers,
        SpatialHashGrid2D.QueryScratch barrierScratch,
        List<int> barrierCandidates)
    {
        for (int i = 0; i < vertexCount; i++)
        {
            if (keepGuides != null && !keepGuides[i])
                continue;

            double roadX = roadXy[i * 2];
            double roadY = roadXy[i * 2 + 1];
            double shoulderX = shoulderXy[i * 2];
            double shoulderY = shoulderXy[i * 2 + 1];
            double shoulderZ = shoulderZValues[i];

            if (barriers.Segments.Length > 0)
            {
                double originalShoulderX = shoulderX;
                double originalShoulderY = shoulderY;
                GradingBarriers.TryClipSegment(
                    barriers,
                    roadX,
                    roadY,
                    shoulderX,
                    shoulderY,
                    barrierScratch,
                    barrierCandidates,
                    out shoulderX,
                    out shoulderY);

                double dx = originalShoulderX - roadX;
                double dy = originalShoulderY - roadY;
                double lengthSquared = (dx * dx) + (dy * dy);
                if (lengthSquared > 1e-12)
                {
                    double clipT = (((shoulderX - roadX) * dx) + ((shoulderY - roadY) * dy)) / lengthSquared;
                    shoulderZ = InterpolateSectionValue(roadZValues[i], shoulderZValues[i], Math.Clamp(clipT, 0.0, 1.0));
                }
            }

            List<ClippedSegment> pieces = BoundaryClipper.ClipSegmentToBoundary(
                roadX,
                roadY,
                roadZValues[i],
                shoulderX,
                shoulderY,
                shoulderZ,
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
}
