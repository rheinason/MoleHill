using TriangleNet;
using TriangleNet.Geometry;
using TriangleNet.Meshing;
using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

public static partial class PadGrader
{
    private static void ApplyGradingToVerticesWithSections(
        double[] gradedVertices,
        double[] originalVertices,
        int vertexCount,
        PadBoundary[] pads,
        PreparedBarriers barriers,
        TerrainFaceGrid TerrainFaceGrid,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double tolerance,
        bool keepShoulderOnBatterPlane = false,
        int defaultCornerFanSegments = 0)
    {
        if (pads.Length == 0)
            return;

        var preparedPads = new PreparedPadSections[pads.Length];
        var interiorBounds = new Bounds2D[pads.Length];
        var influenceBounds = new Bounds2D[pads.Length];
        double globalMinX = double.MaxValue;
        double globalMaxX = double.MinValue;
        double globalMinY = double.MaxValue;
        double globalMaxY = double.MinValue;

        for (int i = 0; i < pads.Length; i++)
        {
            preparedPads[i] = BuildPreparedPadSections(
                pads[i],
                TerrainFaceGrid,
                barriers,
                hasBoundaryLoop,
                boundaryLoop,
                boundaryVertexCount,
                tolerance,
                keepShoulderOnBatterPlane,
                defaultCornerFanSegments);
            interiorBounds[i] = new Bounds2D(
                preparedPads[i].MinX,
                preparedPads[i].MaxX,
                preparedPads[i].MinY,
                preparedPads[i].MaxY);
            influenceBounds[i] = new Bounds2D(
                preparedPads[i].InfluenceMinX,
                preparedPads[i].InfluenceMaxX,
                preparedPads[i].InfluenceMinY,
                preparedPads[i].InfluenceMaxY);

            if (preparedPads[i].InfluenceMinX < globalMinX) globalMinX = preparedPads[i].InfluenceMinX;
            if (preparedPads[i].InfluenceMaxX > globalMaxX) globalMaxX = preparedPads[i].InfluenceMaxX;
            if (preparedPads[i].InfluenceMinY < globalMinY) globalMinY = preparedPads[i].InfluenceMinY;
            if (preparedPads[i].InfluenceMaxY > globalMaxY) globalMaxY = preparedPads[i].InfluenceMaxY;
        }

        var interiorIndex = SpatialHashGrid2D.Build(interiorBounds);
        var influenceIndex = SpatialHashGrid2D.Build(influenceBounds);
        int barrierCount = Math.Max(barriers.Segments.Length, 1);
        System.Threading.Tasks.Parallel.For(
            0,
            vertexCount,
            () => (
                InteriorScratch: new SpatialHashGrid2D.QueryScratch(pads.Length),
                InfluenceScratch: new SpatialHashGrid2D.QueryScratch(pads.Length),
                BarrierScratch: new SpatialHashGrid2D.QueryScratch(barrierCount),
                InteriorCandidates: new List<int>(8),
                InfluenceCandidates: new List<int>(8),
                BarrierCandidates: new List<int>(8)),
            (i, _, state) =>
        {
            double px = gradedVertices[i * 3];
            double py = gradedVertices[i * 3 + 1];
            if (px < globalMinX || px > globalMaxX || py < globalMinY || py > globalMaxY)
                return state;

            interiorIndex.GatherCandidates(Bounds2D.FromPoint(px, py), state.InteriorCandidates, state.InteriorScratch);
            bool insidePadTop = false;
            double padTopZ = double.NegativeInfinity;
            foreach (int padIndex in state.InteriorCandidates)
            {
                var prepared = preparedPads[padIndex];
                if (px < prepared.MinX || px > prepared.MaxX || py < prepared.MinY || py > prepared.MaxY)
                    continue;

                if (PointInPolygon(px, py, prepared.Pad.XyVertices, prepared.Pad.VertexCount))
                {
                    padTopZ = Math.Max(padTopZ, pads[padIndex].EvaluateZ(px, py));
                    insidePadTop = true;
                }
            }

            influenceIndex.GatherCandidates(Bounds2D.FromPoint(px, py), state.InfluenceCandidates, state.InfluenceScratch);
            double nearestDistance = double.MaxValue;
            int nearestPadIdx = -1;
            double nearestCandidateZ = insidePadTop ? padTopZ : 0.0;
            double highestCandidateZ = insidePadTop ? padTopZ : double.NegativeInfinity;
            double zTolerance = GradingTolerances.VertexAdjustmentZTolerance(tolerance);

            foreach (int padIndex in state.InfluenceCandidates)
            {
                var prepared = preparedPads[padIndex];
                if (px < prepared.InfluenceMinX || px > prepared.InfluenceMaxX || py < prepared.InfluenceMinY || py > prepared.InfluenceMaxY)
                    continue;
                if (!TryFindClosestLoopLocation(prepared.BoundaryLoopXy, prepared.BoundaryVertexCount, px, py, out ClosestLoopLocation closest))
                    continue;
                if (!TryInterpolatePadSection(
                        prepared,
                        closest,
                        out double boundaryX,
                        out double boundaryY,
                        out double boundaryZ,
                        out double shoulderX,
                        out double shoulderY,
                        out double shoulderZ))
                {
                    continue;
                }

                if (barriers.Segments.Length > 0 &&
                    GradingBarriers.IsCrossedByBarrier(
                        barriers,
                        px,
                        py,
                        boundaryX,
                        boundaryY,
                        state.BarrierScratch,
                        state.BarrierCandidates))
                {
                    continue;
                }

                double sectionReach = Math.Sqrt(((shoulderX - boundaryX) * (shoulderX - boundaryX)) + ((shoulderY - boundaryY) * (shoulderY - boundaryY)));
                if (sectionReach <= 1e-9 || closest.Distance > sectionReach + 1e-9)
                    continue;

                double normalizedDistance = Math.Clamp(closest.Distance / sectionReach, 0.0, 1.0);
                double candidateZ = boundaryZ + ((shoulderZ - boundaryZ) * normalizedDistance);
                candidateZ = ClampBetween(candidateZ, boundaryZ, shoulderZ);
                if (Math.Abs(candidateZ - originalVertices[i * 3 + 2]) <= GradingTolerances.VertexAdjustmentZTolerance(tolerance))
                    continue;
                if (insidePadTop && candidateZ <= padTopZ + zTolerance)
                    continue;

                highestCandidateZ = Math.Max(highestCandidateZ, candidateZ);

                if (closest.Distance < nearestDistance - 1e-12 ||
                    (Math.Abs(closest.Distance - nearestDistance) <= 1e-12 && padIndex > nearestPadIdx))
                {
                    nearestDistance = closest.Distance;
                    nearestPadIdx = padIndex;
                    nearestCandidateZ = candidateZ;
                }
            }

            if (nearestPadIdx >= 0)
                gradedVertices[i * 3 + 2] = highestCandidateZ;
            else if (insidePadTop)
                gradedVertices[i * 3 + 2] = padTopZ;

            return state;
        }, _ => { });
    }

    private static PreparedPadSections BuildPreparedPadSections(
        PadBoundary pad,
        TerrainFaceGrid TerrainFaceGrid,
        PreparedBarriers barriers,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double tolerance,
        bool keepShoulderOnBatterPlane = false,
        int defaultCornerFanSegments = 0)
    {
        double[] initialDistances = ComputePadBoundaryDistances(
            pad.XyVertices,
            pad.VertexCount,
            TerrainFaceGrid,
            pad);
        double shoulderDistance = 0.0;
        foreach (double distance in initialDistances)
            shoulderDistance = Math.Max(shoulderDistance, distance);

        double segmentLength = ComputePadConstraintSegmentLength(shoulderDistance);
        ConstraintLoop padLoop = BuildClosedConstraintLoop(pad.XyVertices, pad.VertexCount, segmentLength, tolerance);
        double[] shoulderDistances = ComputePadBoundaryDistances(
            padLoop.XyVertices,
            padLoop.VertexCount,
            TerrainFaceGrid,
            pad);
        double minX = double.MaxValue;
        double maxX = double.MinValue;
        double minY = double.MaxValue;
        double maxY = double.MinValue;
        double influenceMinX = double.MaxValue;
        double influenceMaxX = double.MinValue;
        double influenceMinY = double.MaxValue;
        double influenceMaxY = double.MinValue;

        int effectiveCornerFanSegments = pad.CornerFanSegments > 0
            ? pad.CornerFanSegments
            : (pad.StitchApronDistance > tolerance * 4.0 ? 6 : defaultCornerFanSegments);
        double[] targetBoundaryXy = padLoop.XyVertices;
        double[] targetShoulderXy;
        bool hasShoulderLoop;
        if (effectiveCornerFanSegments > 0)
        {
            hasShoulderLoop = TryBuildExpandedOffsetPolygon(
                padLoop.XyVertices,
                padLoop.VertexCount,
                shoulderDistances,
                effectiveCornerFanSegments,
                TerrainFaceGrid,
                pad,
                out targetBoundaryXy,
                out targetShoulderXy,
                out _);
        }
        else
        {
            hasShoulderLoop = TryBuildOffsetPolygon(
                padLoop.XyVertices,
                padLoop.VertexCount,
                shoulderDistances,
                out targetShoulderXy,
                out _);
        }

        if (hasShoulderLoop && !ReferenceEquals(targetBoundaryXy, padLoop.XyVertices))
            padLoop = new ConstraintLoop(targetBoundaryXy, targetBoundaryXy.Length / 2);

        var shoulderXy = new double[padLoop.VertexCount * 2];
        var shoulderZ = new double[padLoop.VertexCount];

        for (int i = 0; i < padLoop.VertexCount; i++)
        {
            double boundaryX = padLoop.XyVertices[i * 2];
            double boundaryY = padLoop.XyVertices[i * 2 + 1];
            double resolvedShoulderX = boundaryX;
            double resolvedShoulderY = boundaryY;

            if (hasShoulderLoop)
            {
                ResolvePadShoulderEndpoint(
                    barriers,
                    hasBoundaryLoop,
                    boundaryLoop,
                    boundaryVertexCount,
                    tolerance,
                    boundaryX,
                    boundaryY,
                    targetShoulderXy[i * 2],
                    targetShoulderXy[i * 2 + 1],
                    out resolvedShoulderX,
                    out resolvedShoulderY);

            }

            shoulderXy[i * 2] = resolvedShoulderX;
            shoulderXy[i * 2 + 1] = resolvedShoulderY;
            double boundaryZ = pad.EvaluateZ(boundaryX, boundaryY);
            double actualReach = Math.Sqrt(((resolvedShoulderX - boundaryX) * (resolvedShoulderX - boundaryX)) + ((resolvedShoulderY - boundaryY) * (resolvedShoulderY - boundaryY)));
            if (actualReach <= tolerance)
            {
                shoulderZ[i] = boundaryZ;
            }
            else
            {
                double terrainZ = TerrainFaceGrid.InterpolateZ(resolvedShoulderX, resolvedShoulderY);
                if (!keepShoulderOnBatterPlane)
                {
                    shoulderZ[i] = terrainZ;
                }
                else
                {
                    double branchSign = Math.Sign(terrainZ - boundaryZ);
                    if (Math.Abs(branchSign) <= 1e-12)
                    {
                        shoulderZ[i] = boundaryZ;
                    }
                    else
                    {
                        double slopeRatio = Math.Tan(pad.SlopeAngleDeg * Math.PI / 180.0);
                        double batterReach = DistToPolygon(
                            resolvedShoulderX,
                            resolvedShoulderY,
                            padLoop.XyVertices,
                            padLoop.VertexCount);
                        if (batterReach <= tolerance)
                            batterReach = actualReach;
                        shoulderZ[i] = boundaryZ + (branchSign * slopeRatio * batterReach);
                    }
                }
            }

            if (boundaryX < minX) minX = boundaryX;
            if (boundaryX > maxX) maxX = boundaryX;
            if (boundaryY < minY) minY = boundaryY;
            if (boundaryY > maxY) maxY = boundaryY;

            influenceMinX = Math.Min(influenceMinX, Math.Min(boundaryX, resolvedShoulderX));
            influenceMaxX = Math.Max(influenceMaxX, Math.Max(boundaryX, resolvedShoulderX));
            influenceMinY = Math.Min(influenceMinY, Math.Min(boundaryY, resolvedShoulderY));
            influenceMaxY = Math.Max(influenceMaxY, Math.Max(boundaryY, resolvedShoulderY));
        }

        return new PreparedPadSections(
            pad,
            padLoop.XyVertices,
            padLoop.VertexCount,
            shoulderXy,
            shoulderZ,
            minX,
            maxX,
            minY,
            maxY,
            influenceMinX,
            influenceMaxX,
            influenceMinY,
            influenceMaxY);
    }

    private static bool TryFindClosestLoopLocation(
        double[] xyVertices,
        int vertexCount,
        double px,
        double py,
        out ClosestLoopLocation closest)
    {
        closest = default;
        double closestDistSq = double.MaxValue;
        if (vertexCount < 2)
            return false;

        for (int i = 0; i < vertexCount; i++)
        {
            int next = (i + 1) % vertexCount;
            double ax = xyVertices[i * 2];
            double ay = xyVertices[i * 2 + 1];
            double bx = xyVertices[next * 2];
            double by = xyVertices[next * 2 + 1];
            double dx = bx - ax;
            double dy = by - ay;
            double lenSq = (dx * dx) + (dy * dy);
            if (lenSq < 1e-20)
                continue;

            double t = Math.Clamp((((px - ax) * dx) + ((py - ay) * dy)) / lenSq, 0.0, 1.0);
            double projectedX = ax + (dx * t);
            double projectedY = ay + (dy * t);
            double offsetX = px - projectedX;
            double offsetY = py - projectedY;
            double distSq = (offsetX * offsetX) + (offsetY * offsetY);
            if (distSq >= closestDistSq)
                continue;

            closestDistSq = distSq;
            closest = new ClosestLoopLocation(i, t, Math.Sqrt(distSq));
        }

        return closestDistSq < double.MaxValue;
    }

    private static bool TryInterpolatePadSection(
        PreparedPadSections prepared,
        ClosestLoopLocation closest,
        out double boundaryX,
        out double boundaryY,
        out double boundaryZ,
        out double shoulderX,
        out double shoulderY,
        out double shoulderZ)
    {
        boundaryX = 0.0;
        boundaryY = 0.0;
        boundaryZ = 0.0;
        shoulderX = 0.0;
        shoulderY = 0.0;
        shoulderZ = 0.0;

        int segmentIndex = closest.SegmentIndex;
        if (segmentIndex < 0 || segmentIndex >= prepared.BoundaryVertexCount)
            return false;

        int next = (segmentIndex + 1) % prepared.BoundaryVertexCount;
        boundaryX = LerpValue(prepared.BoundaryLoopXy[segmentIndex * 2], prepared.BoundaryLoopXy[next * 2], closest.SegmentT);
        boundaryY = LerpValue(prepared.BoundaryLoopXy[(segmentIndex * 2) + 1], prepared.BoundaryLoopXy[(next * 2) + 1], closest.SegmentT);
        boundaryZ = prepared.Pad.EvaluateZ(boundaryX, boundaryY);
        shoulderX = LerpValue(prepared.ShoulderXy[segmentIndex * 2], prepared.ShoulderXy[next * 2], closest.SegmentT);
        shoulderY = LerpValue(prepared.ShoulderXy[(segmentIndex * 2) + 1], prepared.ShoulderXy[(next * 2) + 1], closest.SegmentT);
        shoulderZ = LerpValue(prepared.ShoulderZ[segmentIndex], prepared.ShoulderZ[next], closest.SegmentT);
        return double.IsFinite(shoulderZ);
    }

    private static void ResolvePadShoulderEndpoint(
        PreparedBarriers barriers,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double tolerance,
        double startX,
        double startY,
        double targetX,
        double targetY,
        out double resolvedX,
        out double resolvedY)
    {
        resolvedX = targetX;
        resolvedY = targetY;
        var barrierScratch = new SpatialHashGrid2D.QueryScratch(Math.Max(barriers.Segments.Length, 1));
        var barrierCandidates = new List<int>(8);

        if (barriers.Segments.Length > 0)
        {
            GradingBarriers.TryClipSegment(
                barriers,
                startX,
                startY,
                resolvedX,
                resolvedY,
                barrierScratch,
                barrierCandidates,
                out resolvedX,
                out resolvedY);
        }

        if (!hasBoundaryLoop)
            return;

        List<ClippedSegment> pieces = BoundaryClipper.ClipSegmentToBoundary(
            startX,
            startY,
            0.0,
            resolvedX,
            resolvedY,
            0.0,
            hasBoundaryLoop,
            boundaryLoop,
            boundaryVertexCount,
            tolerance);

        double furthestEndT = double.MinValue;
        bool foundPiece = false;
        foreach (ClippedSegment piece in pieces)
        {
            if (piece.StartT > 1e-9 || piece.EndT <= furthestEndT)
                continue;

            resolvedX = piece.EndX;
            resolvedY = piece.EndY;
            furthestEndT = piece.EndT;
            foundPiece = true;
        }

        if (foundPiece)
            return;

        resolvedX = startX;
        resolvedY = startY;
    }

    private static double LerpValue(double start, double end, double t)
    {
        return start + ((end - start) * t);
    }

    private static double ClampBetween(double value, double a, double b)
    {
        double min = Math.Min(a, b);
        double max = Math.Max(a, b);
        return Math.Max(min, Math.Min(max, value));
    }

    private static void ApplyGradingToVertices(
        double[] gradedVertices,
        double[] originalVertices,
        int vertexCount,
        PadBoundary[] pads,
        PreparedBarriers barriers)
    {
        if (pads.Length == 0)
            return;

        double globalMinX = double.MaxValue;
        double globalMaxX = double.MinValue;
        double globalMinY = double.MaxValue;
        double globalMaxY = double.MinValue;
        var padBounds = new PadInfluenceBounds[pads.Length];
        var interiorBounds = new Bounds2D[pads.Length];
        var influenceBounds = new Bounds2D[pads.Length];

        for (int p = 0; p < pads.Length; p++)
        {
            var pad = pads[p];
            double minX = double.MaxValue;
            double maxX = double.MinValue;
            double minY = double.MaxValue;
            double maxY = double.MinValue;

            for (int i = 0; i < pad.VertexCount; i++)
            {
                double vx = pad.XyVertices[i * 2];
                double vy = pad.XyVertices[i * 2 + 1];
                if (vx < minX) minX = vx;
                if (vx > maxX) maxX = vx;
                if (vy < minY) minY = vy;
                if (vy > maxY) maxY = vy;
            }

            double transitionDistance = ComputePadTransitionDistance(originalVertices, vertexCount, pad);
            double influenceMinX = minX - transitionDistance;
            double influenceMaxX = maxX + transitionDistance;
            double influenceMinY = minY - transitionDistance;
            double influenceMaxY = maxY + transitionDistance;
            padBounds[p] = new PadInfluenceBounds(
                pad,
                minX,
                maxX,
                minY,
                maxY,
                influenceMinX,
                influenceMaxX,
                influenceMinY,
                influenceMaxY);
            interiorBounds[p] = new Bounds2D(minX, maxX, minY, maxY);
            influenceBounds[p] = new Bounds2D(influenceMinX, influenceMaxX, influenceMinY, influenceMaxY);

            if (influenceMinX < globalMinX) globalMinX = influenceMinX;
            if (influenceMaxX > globalMaxX) globalMaxX = influenceMaxX;
            if (influenceMinY < globalMinY) globalMinY = influenceMinY;
            if (influenceMaxY > globalMaxY) globalMaxY = influenceMaxY;
        }

        var interiorIndex = SpatialHashGrid2D.Build(interiorBounds);
        var influenceIndex = SpatialHashGrid2D.Build(influenceBounds);

        int barrierCount = Math.Max(barriers.Segments.Length, 1);
        System.Threading.Tasks.Parallel.For(
            0,
            vertexCount,
            () => (
                InteriorScratch: new SpatialHashGrid2D.QueryScratch(pads.Length),
                InfluenceScratch: new SpatialHashGrid2D.QueryScratch(pads.Length),
                BarrierScratch: new SpatialHashGrid2D.QueryScratch(barrierCount),
                InteriorCandidates: new List<int>(8),
                InfluenceCandidates: new List<int>(8),
                BarrierCandidates: new List<int>(8)),
            (i, _, state) =>
        {
            double px = gradedVertices[i * 3];
            double py = gradedVertices[i * 3 + 1];

            if (px < globalMinX || px > globalMaxX || py < globalMinY || py > globalMaxY)
                return state;

            interiorIndex.GatherCandidates(
                Bounds2D.FromPoint(px, py),
                state.InteriorCandidates,
                state.InteriorScratch);

            int insidePadIdx = -1;
            foreach (int p in state.InteriorCandidates)
            {
                var bounds = padBounds[p];
                if (px < bounds.MinX || px > bounds.MaxX || py < bounds.MinY || py > bounds.MaxY)
                    continue;

                if (PointInPolygon(px, py, bounds.Pad.XyVertices, bounds.Pad.VertexCount))
                    insidePadIdx = Math.Max(insidePadIdx, p);
            }

            if (insidePadIdx >= 0)
            {
                gradedVertices[i * 3 + 2] = pads[insidePadIdx].EvaluateZ(px, py);
                return state;
            }

            influenceIndex.GatherCandidates(
                Bounds2D.FromPoint(px, py),
                state.InfluenceCandidates,
                state.InfluenceScratch);

            double nearestDist = double.MaxValue;
            int nearestPadIdx = -1;
            double nearestBoundaryZ = 0.0;
            double nearestBoundaryPx = px;
            double nearestBoundaryPy = py;
            foreach (int p in state.InfluenceCandidates)
            {
                var bounds = padBounds[p];
                if (px < bounds.InfluenceMinX || px > bounds.InfluenceMaxX || py < bounds.InfluenceMinY || py > bounds.InfluenceMaxY)
                    continue;

                double dist = DistToBoundaryWithZ(px, py, bounds.Pad.BoundaryVertices, bounds.Pad.VertexCount,
                    out double boundaryZ, out double bpx, out double bpy);
                if (dist < nearestDist - 1e-12 ||
                    (Math.Abs(dist - nearestDist) <= 1e-12 && (nearestPadIdx < 0 || p < nearestPadIdx)))
                {
                    nearestDist = dist;
                    nearestPadIdx = p;
                    nearestBoundaryZ = boundaryZ;
                    nearestBoundaryPx = bpx;
                    nearestBoundaryPy = bpy;
                }
            }

            if (nearestPadIdx < 0)
                return state;

            // Skip grading if a barrier lies between this vertex and its nearest pad boundary point.
            if (barriers.Segments.Length > 0 &&
                GradingBarriers.IsCrossedByBarrier(
                    barriers, px, py, nearestBoundaryPx, nearestBoundaryPy,
                    state.BarrierScratch, state.BarrierCandidates))
                return state;

            var pad = pads[nearestPadIdx];
            double slopeRatio = Math.Tan(pad.SlopeAngleDeg * Math.PI / 180.0);
            double dz = originalVertices[i * 3 + 2] - nearestBoundaryZ;
            double absDz = Math.Abs(dz);
            if (absDz <= GradingTolerances.VertexAdjustmentZTolerance(GradingTolerances.DefaultModelTolerance))
                return state;

            double neededDist = slopeRatio > 1e-12 ? absDz / slopeRatio : double.MaxValue;
            if (pad.MaxDistance > 0)
                neededDist = Math.Min(neededDist, pad.MaxDistance);

            if (nearestDist >= neededDist)
                return state;

            double rise = nearestDist * slopeRatio;
            if (rise < absDz)
            {
                double candidateZ = nearestBoundaryZ + Math.Sign(dz) * rise;
                if (Math.Abs(candidateZ - originalVertices[i * 3 + 2]) > GradingTolerances.VertexAdjustmentZTolerance(GradingTolerances.DefaultModelTolerance))
                    gradedVertices[i * 3 + 2] = candidateZ;
            }
            return state;
        }, _ => { });
    }

    private static double ComputePadTransitionDistance(double[] vertices, int vertexCount, PadBoundary pad)
    {
        double slopeRatio = Math.Tan(pad.SlopeAngleDeg * Math.PI / 180.0);
        double maxZDiff = 0;
        for (int i = 0; i < vertexCount; i++)
        {
            double dz = Math.Abs(vertices[i * 3 + 2] - pad.EvaluateZ(vertices[i * 3], vertices[i * 3 + 1]));
            if (dz > maxZDiff)
                maxZDiff = dz;
        }

        double transitionDistance = slopeRatio > 1e-12 ? maxZDiff / slopeRatio : 100.0;
        if (pad.MaxDistance > 0)
            transitionDistance = Math.Min(transitionDistance, pad.MaxDistance);
        return transitionDistance;
    }

    private static double[]? AddPadShoulderConstraint(
        double[] padLoopXy,
        int padLoopVertexCount,
        double[] shoulderDistances,
        List<double> xyList,
        List<double> zList,
        SpatialVertexHash vertHash,
        TerrainFaceGrid TerrainFaceGrid,
        List<(int a, int b)> segList,
        double dedupTol,
        double[]? boundaryLoop,
        int boundaryVertexCount,
        PreparedBarriers barriers,
        SpatialHashGrid2D.QueryScratch barrierScratch,
        List<int> barrierCandidates)
    {
        if (!TryBuildShoulderLoop(
            padLoopXy,
            padLoopVertexCount,
            shoulderDistances,
            boundaryLoop,
            boundaryVertexCount,
            dedupTol,
            out var shoulderXy,
            out _))
        {
            return null;
        }

        int shoulderVertexCount = shoulderXy.Length / 2;
        var shoulderIndices = new int[shoulderVertexCount];
        for (int i = 0; i < shoulderVertexCount; i++)
        {
            double px = shoulderXy[i * 2];
            double py = shoulderXy[i * 2 + 1];

            int near = vertHash.FindNearest(xyList, px, py, dedupTol);
            if (near >= 0)
            {
                shoulderIndices[i] = near;
            }
            else
            {
                shoulderIndices[i] = zList.Count;
                xyList.Add(px);
                xyList.Add(py);
                zList.Add(TerrainFaceGrid.InterpolateZ(px, py));
                vertHash.Insert(shoulderIndices[i], px, py);
            }
        }

        int AddVertex(double x, double y)
        {
            int near = vertHash.FindNearest(xyList, x, y, dedupTol);
            if (near >= 0)
                return near;
            int idx = zList.Count;
            xyList.Add(x);
            xyList.Add(y);
            zList.Add(TerrainFaceGrid.InterpolateZ(x, y));
            vertHash.Insert(idx, x, y);
            return idx;
        }

        // Add shoulder ring segments, clipping each at the first barrier hit.
        // When clipped, the arc terminates at the barrier intersection, producing
        // open support runs instead of a single closed ring.
        for (int i = 0; i < shoulderVertexCount; i++)
        {
            int next = (i + 1) % shoulderVertexCount;
            double ax = shoulderXy[i * 2],    ay = shoulderXy[i * 2 + 1];
            double bx = shoulderXy[next * 2], by = shoulderXy[next * 2 + 1];

            bool clipped = GradingBarriers.TryClipSegment(
                barriers, ax, ay, bx, by,
                barrierScratch, barrierCandidates,
                out double cbx, out double cby);

            int startIdx = shoulderIndices[i];
            int endIdx = clipped ? AddVertex(cbx, cby) : shoulderIndices[next];

            if (startIdx != endIdx)
                segList.Add((startIdx, endIdx));
        }

        return shoulderXy;
    }

    /// <summary>
    /// For each vertex of the pad boundary, interpolates the terrain Z and computes the
    /// horizontal distance the slope transition needs to travel to reach the terrain surface.
    /// Capped at MaxDistance when set.
    /// </summary>
    private static double[] ComputePadBoundaryDistances(
        double[] padLoopXy,
        int padLoopVertexCount,
        TerrainFaceGrid TerrainFaceGrid,
        PadBoundary pad)
    {
        double slopeRatio = Math.Tan(pad.SlopeAngleDeg * Math.PI / 180.0);
        var distances = new double[padLoopVertexCount];
        double signedArea = ClipperGeometry.SignedArea(padLoopXy);
        bool hasOrientation = Math.Abs(signedArea) > 1e-12;
        bool ccw = signedArea > 0.0;
        for (int i = 0; i < padLoopVertexCount; i++)
        {
            double bx = padLoopXy[i * 2];
            double by = padLoopXy[i * 2 + 1];
            double terrainZ = TerrainFaceGrid.InterpolateZ(bx, by);
            double padZ = pad.EvaluateZ(bx, by);
            double dz = Math.Abs(terrainZ - padZ);
            double d = slopeRatio > 1e-12 ? dz / slopeRatio : 100.0;
            if (d > 1e-9 && hasOrientation &&
                TryComputePadOutwardEdgeNormals(
                    padLoopXy,
                    padLoopVertexCount,
                    i,
                    ccw,
                    out double previousNormalX,
                    out double previousNormalY,
                    out double nextNormalX,
                    out double nextNormalY))
            {
                double branchSign = Math.Sign(terrainZ - padZ);
                double previousReach = ComputePadDaylightReach(
                    TerrainFaceGrid,
                    bx,
                    by,
                    padZ,
                    previousNormalX,
                    previousNormalY,
                    slopeRatio,
                    branchSign,
                    d,
                    pad.MaxDistance);
                double nextReach = ComputePadDaylightReach(
                    TerrainFaceGrid,
                    bx,
                    by,
                    padZ,
                    nextNormalX,
                    nextNormalY,
                    slopeRatio,
                    branchSign,
                    d,
                    pad.MaxDistance);

                d = Math.Max(previousReach, nextReach);
            }

            if (pad.MaxDistance > 0)
                d = Math.Min(d, pad.MaxDistance);
            distances[i] = d;
        }

        return distances;
    }

    private static bool TryComputePadOutwardEdgeNormals(
        double[] polygonXy,
        int vertexCount,
        int index,
        bool ccw,
        out double previousNormalX,
        out double previousNormalY,
        out double nextNormalX,
        out double nextNormalY)
    {
        previousNormalX = 0.0;
        previousNormalY = 0.0;
        nextNormalX = 0.0;
        nextNormalY = 0.0;
        if (vertexCount < 3)
            return false;

        int previous = (index + vertexCount - 1) % vertexCount;
        int next = (index + 1) % vertexCount;

        if (!TryComputePadOutwardEdgeNormal(
                polygonXy[previous * 2],
                polygonXy[previous * 2 + 1],
                polygonXy[index * 2],
                polygonXy[index * 2 + 1],
                ccw,
                out previousNormalX,
                out previousNormalY) ||
            !TryComputePadOutwardEdgeNormal(
                polygonXy[index * 2],
                polygonXy[index * 2 + 1],
                polygonXy[next * 2],
                polygonXy[next * 2 + 1],
                ccw,
                out nextNormalX,
                out nextNormalY))
        {
            return false;
        }

        return true;
    }

    private static bool TryComputePadOutwardEdgeNormal(
        double ax,
        double ay,
        double bx,
        double by,
        bool ccw,
        out double normalX,
        out double normalY)
    {
        normalX = 0.0;
        normalY = 0.0;
        double dx = bx - ax;
        double dy = by - ay;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length <= 1e-12)
            return false;

        normalX = ccw ? dy / length : -dy / length;
        normalY = ccw ? -dx / length : dx / length;
        return true;
    }

    private static double ComputePadDaylightReach(
        TerrainFaceGrid TerrainFaceGrid,
        double boundaryX,
        double boundaryY,
        double boundaryZ,
        double dirX,
        double dirY,
        double slopeRatio,
        double branchSign,
        double fallbackReach,
        double maxDistance)
    {
        if (slopeRatio <= 1e-12 ||
            Math.Abs(branchSign) <= 1e-12 ||
            !double.IsFinite(fallbackReach) ||
            fallbackReach <= 1e-9)
        {
            return Math.Max(0.0, fallbackReach);
        }

        double searchDistance = maxDistance > 0.0
            ? maxDistance
            : Math.Max(Math.Max(fallbackReach * 4.0, 1.0), TerrainFaceGrid.BoundsDiagonal);
        if (searchDistance <= 1e-9)
            return Math.Max(0.0, fallbackReach);

        if (TryFindPadDaylightReachByTriangleIntervals(
                TerrainFaceGrid,
                boundaryX,
                boundaryY,
                boundaryZ,
                dirX,
                dirY,
                slopeRatio,
                branchSign,
                searchDistance,
                out double daylightReach,
                out double bestApproachReach))
        {
            return daylightReach;
        }

        if (TryFindPadDaylightReach(
                TerrainFaceGrid,
                boundaryX,
                boundaryY,
                boundaryZ,
                dirX,
                dirY,
                slopeRatio,
                branchSign,
                searchDistance,
                out daylightReach,
                out bestApproachReach))
        {
            return daylightReach;
        }

        return Math.Min(fallbackReach, searchDistance);
    }

    private static bool TryFindPadDaylightReachByTriangleIntervals(
        TerrainFaceGrid TerrainFaceGrid,
        double edgeX,
        double edgeY,
        double edgeZ,
        double dirX,
        double dirY,
        double slopeRatio,
        double branchSign,
        double maxReach,
        out double daylightReach,
        out double bestApproachReach)
    {
        daylightReach = 0.0;
        bestApproachReach = 0.0;
        if (maxReach <= 1e-9)
            return false;

        return TerrainFaceGrid.TryFindRayDaylightReach(
            edgeX,
            edgeY,
            edgeZ,
            dirX,
            dirY,
            slopeRatio,
            branchSign,
            maxReach,
            out daylightReach,
            out bestApproachReach);
    }

    private static bool TryFindPadDaylightReach(
        TerrainFaceGrid TerrainFaceGrid,
        double edgeX,
        double edgeY,
        double edgeZ,
        double dirX,
        double dirY,
        double slopeRatio,
        double branchSign,
        double maxReach,
        out double daylightReach,
        out double bestApproachReach)
    {
        daylightReach = 0.0;
        bestApproachReach = 0.0;
        if (maxReach <= 1e-9)
            return false;

        const double diffTolerance = 1e-4;
        double step = Math.Clamp(maxReach / 48.0, 0.1, 5.0);
        int sampleCount = Math.Max(1, (int)Math.Ceiling(maxReach / step));
        double startDiff = EvaluatePadSectionDifference(TerrainFaceGrid, edgeX, edgeY, edgeZ, dirX, dirY, slopeRatio, branchSign, 0.0);
        double bestAbsDiff = Math.Abs(startDiff);
        double prevReach;
        double prevDiff;
        int sampleIndex;

        if (Math.Abs(startDiff) > diffTolerance)
        {
            prevReach = 0.0;
            prevDiff = startDiff;
            sampleIndex = 1;
        }
        else
        {
            double firstReach = Math.Min(maxReach, step);
            double firstDiff = EvaluatePadSectionDifference(TerrainFaceGrid, edgeX, edgeY, edgeZ, dirX, dirY, slopeRatio, branchSign, firstReach);
            if (Math.Abs(firstDiff) <= diffTolerance ||
                (branchSign > 0.0 && firstDiff < diffTolerance) ||
                (branchSign < 0.0 && firstDiff > -diffTolerance))
            {
                daylightReach = 0.0;
                return true;
            }

            prevReach = firstReach;
            prevDiff = firstDiff;
            sampleIndex = 2;
        }

        for (; sampleIndex <= sampleCount; sampleIndex++)
        {
            double currentReach = sampleIndex == sampleCount
                ? maxReach
                : Math.Min(maxReach, sampleIndex * step);
            double currentDiff = EvaluatePadSectionDifference(TerrainFaceGrid, edgeX, edgeY, edgeZ, dirX, dirY, slopeRatio, branchSign, currentReach);
            double currentAbsDiff = Math.Abs(currentDiff);
            if (currentAbsDiff < bestAbsDiff)
            {
                bestAbsDiff = currentAbsDiff;
                bestApproachReach = currentReach;
            }

            if (Math.Abs(currentDiff) <= diffTolerance)
            {
                daylightReach = currentReach;
                return true;
            }

            bool crossed = branchSign > 0.0
                ? prevDiff > diffTolerance && currentDiff < diffTolerance
                : prevDiff < -diffTolerance && currentDiff > -diffTolerance;
            if (crossed)
            {
                daylightReach = RefinePadDaylightReach(
                    TerrainFaceGrid,
                    edgeX,
                    edgeY,
                    edgeZ,
                    dirX,
                    dirY,
                    slopeRatio,
                    branchSign,
                    prevReach,
                    currentReach);
                return true;
            }

            prevReach = currentReach;
            prevDiff = currentDiff;
        }

        return false;
    }

    private static double RefinePadDaylightReach(
        TerrainFaceGrid TerrainFaceGrid,
        double edgeX,
        double edgeY,
        double edgeZ,
        double dirX,
        double dirY,
        double slopeRatio,
        double branchSign,
        double lowReach,
        double highReach)
    {
        const double diffTolerance = 1e-5;
        double low = lowReach;
        double high = highReach;

        for (int i = 0; i < 24; i++)
        {
            double mid = (low + high) * 0.5;
            double diff = EvaluatePadSectionDifference(TerrainFaceGrid, edgeX, edgeY, edgeZ, dirX, dirY, slopeRatio, branchSign, mid);
            if (Math.Abs(diff) <= diffTolerance || (high - low) <= 1e-4)
                return mid;

            if (branchSign > 0.0)
            {
                if (diff > 0.0) low = mid;
                else high = mid;
            }
            else
            {
                if (diff < 0.0) low = mid;
                else high = mid;
            }
        }

        return (low + high) * 0.5;
    }

    private static double EvaluatePadSectionDifference(
        TerrainFaceGrid TerrainFaceGrid,
        double edgeX,
        double edgeY,
        double edgeZ,
        double dirX,
        double dirY,
        double slopeRatio,
        double branchSign,
        double reach)
    {
        double terrainZ = TerrainFaceGrid.InterpolateZ(edgeX + (dirX * reach), edgeY + (dirY * reach));
        double gradeZ = edgeZ + (branchSign * slopeRatio * reach);
        return terrainZ - gradeZ;
    }

    private static bool TryBuildShoulderLoop(
        double[] padLoopXy,
        int padLoopVertexCount,
        double[] shoulderDistances,
        double[]? boundaryLoop,
        int boundaryVertexCount,
        double tolerance,
        out double[] shoulderXy,
        out string? skipReason)
    {
        shoulderXy = Array.Empty<double>();
        skipReason = null;
        double maxDist = 0;
        foreach (double d in shoulderDistances) if (d > maxDist) maxDist = d;
        if (maxDist <= tolerance)
            return false;

        if (!TryBuildShoulderLoopWithClipper(
            padLoopXy,
            padLoopVertexCount,
            shoulderDistances,
            boundaryLoop,
            boundaryVertexCount,
            tolerance,
            out shoulderXy,
            out string? offsetFailure))
        {
            skipReason = offsetFailure ?? "Grade Pad shoulder ring was skipped because the daylight offset could not be constructed cleanly.";
            return false;
        }

        return true;
    }

    private static bool TryBuildShoulderLoopFromSections(
        double[] padLoopXy,
        int padLoopVertexCount,
        double[] shoulderXy,
        double[]? boundaryLoop,
        int boundaryVertexCount,
        double tolerance,
        out double[] shoulderLoopXy,
        out string? failureReason,
        int cornerFanSegments = 0)
    {
        shoulderLoopXy = Array.Empty<double>();
        failureReason = null;

        if (!TryBuildPadTransitionQuadsFromSections(
                padLoopXy,
                padLoopVertexCount,
                shoulderXy,
                tolerance,
                out List<double[]> stripLoops))
        {
            failureReason = "Grade Pad shoulder ring was skipped because the resolved daylight strips degenerated.";
            return false;
        }

        if (!ClipperGeometry.TryUnionClosedLoops(stripLoops, tolerance, out List<double[]> unionLoops))
        {
            failureReason = "Grade Pad shoulder ring was skipped because the resolved daylight region could not be unioned cleanly.";
            return false;
        }

        if (boundaryLoop != null)
        {
            if (!ClipperGeometry.TryIntersectClosedLoops(unionLoops, boundaryLoop, tolerance, out unionLoops))
            {
                failureReason = "Grade Pad shoulder ring was skipped because the resolved daylight region exited the terrain boundary.";
                return false;
            }
        }

        if (!ClipperGeometry.TryPickLargestLoop(unionLoops, out shoulderLoopXy))
        {
            failureReason = "Grade Pad shoulder ring was skipped because the resolved daylight region produced no valid outer loop.";
            return false;
        }

        if (!ClipperGeometry.TrySimplifyClosedLoop(shoulderLoopXy, tolerance, out shoulderLoopXy) ||
            shoulderLoopXy.Length / 2 < 3)
        {
            shoulderLoopXy = Array.Empty<double>();
            failureReason = "Grade Pad shoulder ring was skipped because the resolved daylight region degenerated after simplification.";
            return false;
        }

        return true;
    }

    private static bool TryBuildOrderedShoulderLoopFromSections(
        double[] padLoopXy,
        int padLoopVertexCount,
        double[] shoulderXy,
        double tolerance,
        out double[] shoulderLoopXy,
        out string? failureReason)
    {
        shoulderLoopXy = Array.Empty<double>();
        failureReason = null;
        if (padLoopVertexCount < 3 || shoulderXy.Length < padLoopVertexCount * 2)
        {
            failureReason = "Grade Pad shoulder ring was skipped because the resolved daylight sections were invalid.";
            return false;
        }

        var points = new List<double>(padLoopVertexCount * 2);
        for (int i = 0; i < padLoopVertexCount; i++)
        {
            double bx = padLoopXy[i * 2];
            double by = padLoopXy[i * 2 + 1];
            double sx = shoulderXy[i * 2];
            double sy = shoulderXy[i * 2 + 1];
            if (DistanceSquaredXY(bx, by, sx, sy) <= tolerance * tolerance)
                continue;

            AddLoopPoint(points, sx, sy, tolerance);
        }

        if (points.Count >= 4 &&
            DistanceSquaredXY(points[0], points[1], points[^2], points[^1]) <= tolerance * tolerance)
        {
            points.RemoveRange(points.Count - 2, 2);
        }

        if (points.Count / 2 < 3)
        {
            failureReason = "Grade Pad shoulder ring was skipped because the resolved daylight sections collapsed.";
            return false;
        }

        shoulderLoopXy = points.ToArray();
        return true;
    }

    private static void AppendCornerFanStrips(
        double[] padLoopXy,
        int padLoopVertexCount,
        double[] shoulderXy,
        double tolerance,
        int cornerFanSegments,
        List<double[]> stripLoops)
    {
        if (padLoopVertexCount < 3) return;
        double signedArea = ClipperGeometry.SignedArea(padLoopXy);
        if (Math.Abs(signedArea) < 1e-12) return;
        bool ccw = signedArea > 0.0;
        int fanCount = cornerFanSegments + 1;

        for (int i = 0; i < padLoopVertexCount; i++)
        {
            int prev = (i + padLoopVertexCount - 1) % padLoopVertexCount;
            int next = (i + 1) % padLoopVertexCount;

            double x0 = padLoopXy[prev * 2];
            double y0 = padLoopXy[prev * 2 + 1];
            double x1 = padLoopXy[i * 2];
            double y1 = padLoopXy[i * 2 + 1];
            double x2 = padLoopXy[next * 2];
            double y2 = padLoopXy[next * 2 + 1];

            double dx0 = x1 - x0;
            double dy0 = y1 - y0;
            double dx1 = x2 - x1;
            double dy1 = y2 - y1;
            double len0 = Math.Sqrt(dx0 * dx0 + dy0 * dy0);
            double len1 = Math.Sqrt(dx1 * dx1 + dy1 * dy1);
            if (len0 < 1e-12 || len1 < 1e-12) continue;

            double n0x = ccw ? dy0 / len0 : -dy0 / len0;
            double n0y = ccw ? -dx0 / len0 : dx0 / len0;
            double n1x = ccw ? dy1 / len1 : -dy1 / len1;
            double n1y = ccw ? -dx1 / len1 : dx1 / len1;

            double turnCross = dx0 * dy1 - dy0 * dx1;
            bool isReentrant = ccw ? turnCross < -1e-12 : turnCross > 1e-12;
            if (isReentrant) continue;

            double prevAngle = Math.Atan2(n0y, n0x);
            double nextAngle = Math.Atan2(n1y, n1x);
            double sweep = ComputeOutwardAngleSweep(prevAngle, nextAngle, ccw);
            if (Math.Abs(sweep) <= 10.0 * Math.PI / 180.0) continue;

            double svx = shoulderXy[i * 2] - x1;
            double svy = shoulderXy[i * 2 + 1] - y1;
            double d = (svx * n0x + svy * n0y);
            if (d <= tolerance) continue;

            for (int f = 0; f < fanCount - 1; f++)
            {
                double theta0 = prevAngle + sweep * f / (fanCount - 1);
                double theta1 = prevAngle + sweep * (f + 1) / (fanCount - 1);
                double ax = x1 + Math.Cos(theta0) * d;
                double ay = y1 + Math.Sin(theta0) * d;
                double bx = x1 + Math.Cos(theta1) * d;
                double by = y1 + Math.Sin(theta1) * d;
                double[] tri = [x1, y1, ax, ay, bx, by];
                if (Math.Abs(ClipperGeometry.SignedArea(tri)) > tolerance * tolerance)
                    stripLoops.Add(tri);
            }
        }
    }

    private static bool TryBuildShoulderLoopWithClipper(
        double[] padLoopXy,
        int padLoopVertexCount,
        double[] shoulderDistances,
        double[]? boundaryLoop,
        int boundaryVertexCount,
        double tolerance,
        out double[] shoulderXy,
        out string? failureReason)
    {
        shoulderXy = Array.Empty<double>();
        failureReason = null;

        if (!TryBuildPadTransitionQuads(padLoopXy, padLoopVertexCount, shoulderDistances, tolerance, out List<double[]> stripLoops))
        {
            failureReason = "Grade Pad shoulder ring was skipped because the daylight strips degenerated.";
            return false;
        }

        if (!ClipperGeometry.TryUnionClosedLoops(stripLoops, tolerance, out List<double[]> unionLoops))
        {
            failureReason = "Grade Pad shoulder ring was skipped because the daylight region could not be unioned cleanly.";
            return false;
        }

        if (boundaryLoop != null)
        {
            if (!ClipperGeometry.TryIntersectClosedLoops(unionLoops, boundaryLoop, tolerance, out unionLoops))
            {
                failureReason = "Grade Pad shoulder ring was skipped because the daylight region exited the terrain boundary.";
                return false;
            }
        }

        if (!ClipperGeometry.TryPickLargestLoop(unionLoops, out shoulderXy))
        {
            failureReason = "Grade Pad shoulder ring was skipped because Clipper produced no valid outer loop.";
            return false;
        }

        if (!ClipperGeometry.TrySimplifyClosedLoop(shoulderXy, tolerance, out shoulderXy) ||
            shoulderXy.Length / 2 < 3)
        {
            shoulderXy = Array.Empty<double>();
            failureReason = "Grade Pad shoulder ring was skipped because the daylight region degenerated after simplification.";
            return false;
        }

        double maxDistance = 0.0;
        foreach (double distance in shoulderDistances)
            maxDistance = Math.Max(maxDistance, distance);

        ConstraintLoop resampledShoulder = BuildClosedConstraintLoop(
            shoulderXy,
            shoulderXy.Length / 2,
            ComputePadConstraintSegmentLength(maxDistance),
            tolerance);
        shoulderXy = resampledShoulder.XyVertices;

        return true;
    }

    private static bool TryBuildPadTransitionQuads(
        double[] padLoopXy,
        int padLoopVertexCount,
        double[] shoulderDistances,
        double tolerance,
        out List<double[]> stripLoops)
    {
        stripLoops = new List<double[]>(padLoopVertexCount);
        if (padLoopVertexCount < 3 || shoulderDistances.Length < padLoopVertexCount)
            return false;

        bool ccw = ClipperGeometry.SignedArea(padLoopXy) > 0.0;
        for (int i = 0; i < padLoopVertexCount; i++)
        {
            int next = (i + 1) % padLoopVertexCount;
            double ax = padLoopXy[i * 2];
            double ay = padLoopXy[i * 2 + 1];
            double bx = padLoopXy[next * 2];
            double by = padLoopXy[next * 2 + 1];
            double dx = bx - ax;
            double dy = by - ay;
            double length = Math.Sqrt((dx * dx) + (dy * dy));
            if (length <= tolerance)
                continue;

            double d0 = Math.Max(0.0, shoulderDistances[i]);
            double d1 = Math.Max(0.0, shoulderDistances[next]);
            if (d0 <= tolerance && d1 <= tolerance)
                continue;

            double nx = ccw ? dy / length : -dy / length;
            double ny = ccw ? -dx / length : dx / length;
            double sax = ax + (nx * d0);
            double say = ay + (ny * d0);
            double sbx = bx + (nx * d1);
            double sby = by + (ny * d1);
            double[] quad =
            [
                ax, ay,
                bx, by,
                sbx, sby,
                sax, say
            ];

            if (Math.Abs(ClipperGeometry.SignedArea(quad)) <= tolerance * tolerance)
                continue;

            stripLoops.Add(quad);
        }

        return stripLoops.Count > 0;
    }

    private static bool TryBuildPadTransitionQuadsFromSections(
        double[] padLoopXy,
        int padLoopVertexCount,
        double[] shoulderXy,
        double tolerance,
        out List<double[]> stripLoops)
    {
        stripLoops = new List<double[]>(padLoopVertexCount);
        if (padLoopVertexCount < 3 || shoulderXy.Length < padLoopVertexCount * 2)
            return false;

        for (int i = 0; i < padLoopVertexCount; i++)
        {
            int next = (i + 1) % padLoopVertexCount;
            double ax = padLoopXy[i * 2];
            double ay = padLoopXy[i * 2 + 1];
            double bx = padLoopXy[next * 2];
            double by = padLoopXy[next * 2 + 1];
            double sax = shoulderXy[i * 2];
            double say = shoulderXy[i * 2 + 1];
            double sbx = shoulderXy[next * 2];
            double sby = shoulderXy[next * 2 + 1];

            double edgeDx = bx - ax;
            double edgeDy = by - ay;
            if ((edgeDx * edgeDx) + (edgeDy * edgeDy) <= tolerance * tolerance)
                continue;

            double[] quad =
            [
                ax, ay,
                bx, by,
                sbx, sby,
                sax, say
            ];

            if (Math.Abs(ClipperGeometry.SignedArea(quad)) <= tolerance * tolerance)
                continue;

            stripLoops.Add(quad);
        }

        return stripLoops.Count > 0;
    }

    private static bool PolygonHasConcaveVertex(double[] polygonXy, int vertexCount)
    {
        if (vertexCount < 4)
            return false;

        double signedArea = 0.0;
        for (int i = 0; i < vertexCount; i++)
        {
            int next = (i + 1) % vertexCount;
            signedArea += (polygonXy[i * 2] * polygonXy[next * 2 + 1]) - (polygonXy[next * 2] * polygonXy[i * 2 + 1]);
        }

        if (Math.Abs(signedArea) <= 1e-12)
            return false;

        bool ccw = signedArea > 0.0;
        for (int i = 0; i < vertexCount; i++)
        {
            int prev = (i + vertexCount - 1) % vertexCount;
            int next = (i + 1) % vertexCount;

            double ax = polygonXy[i * 2] - polygonXy[prev * 2];
            double ay = polygonXy[i * 2 + 1] - polygonXy[prev * 2 + 1];
            double bx = polygonXy[next * 2] - polygonXy[i * 2];
            double by = polygonXy[next * 2 + 1] - polygonXy[i * 2 + 1];
            double cross = (ax * by) - (ay * bx);

            if (ccw ? cross < -1e-12 : cross > 1e-12)
                return true;
        }

        return false;
    }

    private static double UpdateSuggestedEdgeLength(
        double current,
        double[] points,
        int pointCount,
        int stride,
        bool isClosed)
    {
        if (pointCount < 2)
            return current;

        int segmentCount = isClosed ? pointCount : pointCount - 1;
        for (int i = 0; i < segmentCount; i++)
        {
            int next = (i + 1) % pointCount;
            double dx = points[next * stride] - points[i * stride];
            double dy = points[next * stride + 1] - points[i * stride + 1];
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length > 1e-9)
                current = Math.Min(current, length);
        }

        return current;
    }

    private static double ComputePadConstraintSegmentLength(double shoulderDistance)
    {
        if (shoulderDistance <= 1e-9)
            return 1.0;

        return Math.Clamp(shoulderDistance * 0.2, 0.5, 1.0);
    }

    private static double ComputeMinimumStitchSegmentLength(double modelTolerance, double terrainDetailSize)
    {
        double tolerance = GradingTolerances.ModelToleranceOrDefault(modelTolerance);
        double resolvedDetail = double.IsFinite(terrainDetailSize) && terrainDetailSize > 0.0
            ? terrainDetailSize
            : tolerance * 20.0;
        double targetStitchSpacing = Math.Max(tolerance * 8.0, resolvedDetail * 0.5);
        return Math.Max(tolerance * 4.0, targetStitchSpacing * 0.35);
    }
}
