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
        int defaultCornerFanSegments = 0,
        bool useNearestShoulderCandidate = false)
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

            // A pad top wins inside its own footprint: a neighbouring (e.g. higher) pad's batter must
            // not sweep across a flat pad surface. Batter blending applies only outside all footprints.
            if (insidePadTop)
            {
                gradedVertices[i * 3 + 2] = padTopZ;
            }
            else if (nearestPadIdx >= 0)
            {
                gradedVertices[i * 3 + 2] = useNearestShoulderCandidate
                    ? nearestCandidateZ
                    : highestCandidateZ;
            }

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
}
