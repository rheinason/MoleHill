using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

public static partial class PathGrader
{
    public static double[] ApplyGradingZ(double[] topologyVertices, int vertexCount, PathDefinition[] paths)
    {
        return ApplyGradingZ(topologyVertices, vertexCount, paths, out _);
    }

    private static void ValidateApplyGradingZInputs(
        double[] topologyVertices,
        int vertexCount,
        PathDefinition[] paths,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> barrierConstraints)
    {
        if (!GradingInputValidator.ValidateVertexArray(topologyVertices, vertexCount, "Topology", out string? errorMessage))
            throw new ArgumentException(errorMessage, nameof(topologyVertices));

        ValidatePathAndBarrierInputs(paths, barrierConstraints);
    }

    private static void ValidatePathAndBarrierInputs(
        PathDefinition[] paths,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> barrierConstraints)
    {
        if (!GradingInputValidator.ValidatePathDefinitions(paths, out string? errorMessage, requireAny: false))
            throw new ArgumentException(errorMessage, nameof(paths));

        if (!GradingInputValidator.ValidateConstraintPolylines(barrierConstraints, "Barrier", out errorMessage))
            throw new ArgumentException(errorMessage, nameof(barrierConstraints));
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
        ConstraintPath path,
        SpatialHashGrid2D? segmentGrid,
        double maxDistance,
        double px,
        double py,
        SpatialHashGrid2D.QueryScratch scratch,
        List<int> candidates,
        out ClosestPathLocation closest)
    {
        if (segmentGrid is null || !double.IsFinite(maxDistance))
            return TryFindClosestPathLocation(path, px, py, out closest);

        segmentGrid.GatherCandidates(
            Bounds2D.FromPoint(px, py, Math.Max(maxDistance, 0.0)),
            candidates,
            scratch);
        if (candidates.Count == 0)
        {
            closest = default;
            return false;
        }

        // The linear implementation resolves equal-distance ties by source segment order.
        candidates.Sort();
        return TryFindClosestPathLocationCore(
            path.XyVertices,
            path.ZValues,
            path.VertexCount,
            path.TangentX,
            path.TangentY,
            px,
            py,
            candidates,
            out closest);
    }

    private static SpatialHashGrid2D? BuildPathSegmentGrid(ConstraintPath path)
    {
        return BuildPathSegmentGrid(path.XyVertices, path.VertexCount);
    }

    private static SpatialHashGrid2D? BuildPathSegmentGrid(
        double[] xyVertices,
        int vertexCount,
        bool force = false)
    {
        int segmentCount = Math.Max(0, vertexCount - 1);
        if (!force && segmentCount < 64)
            return null;

        var bounds = new Bounds2D[segmentCount];
        var valid = new bool[segmentCount];
        for (int segment = 0; segment < segmentCount; segment++)
        {
            double ax = xyVertices[segment * 2];
            double ay = xyVertices[segment * 2 + 1];
            double bx = xyVertices[(segment + 1) * 2];
            double by = xyVertices[(segment + 1) * 2 + 1];
            bounds[segment] = new Bounds2D(
                Math.Min(ax, bx),
                Math.Max(ax, bx),
                Math.Min(ay, by),
                Math.Max(ay, by));
            valid[segment] = ((bx - ax) * (bx - ax)) + ((by - ay) * (by - ay)) >= 1e-20;
        }

        return SpatialHashGrid2D.Build(bounds, valid);
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
        barrierConstraints ??= Array.Empty<SurfaceRemesher.ConstraintPolyline>();
        ValidateApplyGradingZInputs(topologyVertices, vertexCount, paths, barrierConstraints);

        if (paths.Length == 0)
        {
            changedVertexCount = 0;
            return (double[])topologyVertices.Clone();
        }

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

    public static double[] ApplyGradingZ(
        double[] topologyVertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PathDefinition[] paths,
        out int changedVertexCount)
    {
        return ApplyGradingZ(
            topologyVertices,
            vertexCount,
            faces,
            faceCount,
            paths,
            Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
            out changedVertexCount);
    }

    public static double[] ApplyGradingZ(
        double[] topologyVertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PathDefinition[] paths,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> barrierConstraints,
        out int changedVertexCount)
    {
        barrierConstraints ??= Array.Empty<SurfaceRemesher.ConstraintPolyline>();
        ValidateApplyGradingZInputs(topologyVertices, vertexCount, faces, faceCount, paths, barrierConstraints);

        if (paths.Length == 0)
        {
            changedVertexCount = 0;
            return (double[])topologyVertices.Clone();
        }

        var outXy = new double[vertexCount * 2];
        var origZ = new double[vertexCount];
        var newZ = new double[vertexCount];
        bool hasBoundaryLoop = MeshBoundaryLoopBuilder.TryBuildBoundaryLoop(topologyVertices, faces, faceCount, out double[] boundaryLoop, out int boundaryVertexCount);
        var faceGrid = new TerrainFaceGrid(topologyVertices, vertexCount, faces, faceCount);

        for (int i = 0; i < vertexCount; i++)
        {
            outXy[i * 2] = topologyVertices[i * 3];
            outXy[i * 2 + 1] = topologyVertices[i * 3 + 1];
            origZ[i] = topologyVertices[i * 3 + 2];
            newZ[i] = topologyVertices[i * 3 + 2];
        }

        ApplyPathGrading(
            paths,
            barrierConstraints,
            outXy,
            origZ,
            newZ,
            vertexCount,
            faceGrid.InterpolateZ,
            hasBoundaryLoop,
            boundaryLoop,
            boundaryVertexCount,
            boundaryTolerance: 1e-3);

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

    private static void ValidateApplyGradingZInputs(
        double[] topologyVertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PathDefinition[] paths,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> barrierConstraints)
    {
        if (!GradingInputValidator.ValidateTerrainMesh(topologyVertices, vertexCount, faces, faceCount, out string? errorMessage))
            throw new ArgumentException(errorMessage, nameof(topologyVertices));

        ValidatePathAndBarrierInputs(paths, barrierConstraints);
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
        return TryFindClosestPathLocationCore(
            xyVertices,
            zValues,
            vertexCount,
            tangentX,
            tangentY,
            px,
            py,
            candidates: null,
            out closest);
    }

    private static bool TryFindClosestPathLocationCore(
        double[] xyVertices,
        double[] zValues,
        int vertexCount,
        double[]? tangentX,
        double[]? tangentY,
        double px,
        double py,
        List<int>? candidates,
        out ClosestPathLocation closest)
    {
        double closestDistSq = double.MaxValue;
        closest = default;
        int candidateCount = candidates?.Count ?? Math.Max(0, vertexCount - 1);

        for (int candidateIndex = 0; candidateIndex < candidateCount; candidateIndex++)
        {
            int s = candidates is null ? candidateIndex : candidates[candidateIndex];
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
                        // Interpolated tangent degenerate; fall back to segment direction.
                        dirX = sdx / segLength;
                        dirY = sdy / segLength;
                    }
                    // SideSign: cross product of smooth tangent with (query - projected).
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

    internal static double RunClosestPathQueriesForDiagnostics(
        double[] xyVertices,
        double[] zValues,
        int vertexCount,
        double[] queryXy,
        int queryCount)
    {
        double checksum = 0.0;
        for (int index = 0; index < queryCount; index++)
        {
            double x = queryXy[index * 2];
            double y = queryXy[index * 2 + 1];
            if (TryFindClosestPathLocation(
                xyVertices,
                zValues,
                vertexCount,
                x,
                y,
                out ClosestPathLocation closest))
            {
                checksum += closest.Distance + closest.PathZ + closest.SegmentIndex;
            }
        }

        return checksum;
    }

    internal static double RunIndexedClosestPathQueriesForDiagnostics(
        double[] xyVertices,
        double[] zValues,
        int vertexCount,
        double[] queryXy,
        int queryCount,
        double maxDistance)
    {
        SpatialHashGrid2D grid = BuildPathSegmentGrid(xyVertices, vertexCount, force: true)!;
        var scratch = new SpatialHashGrid2D.QueryScratch(Math.Max(0, vertexCount - 1));
        var candidates = new List<int>(16);
        double checksum = 0.0;
        for (int index = 0; index < queryCount; index++)
        {
            double x = queryXy[index * 2];
            double y = queryXy[index * 2 + 1];
            grid.GatherCandidates(
                Bounds2D.FromPoint(x, y, Math.Max(maxDistance, 0.0)),
                candidates,
                scratch);
            candidates.Sort();
            if (TryFindClosestPathLocationCore(
                    xyVertices,
                    zValues,
                    vertexCount,
                    null,
                    null,
                    x,
                    y,
                    candidates,
                    out ClosestPathLocation closest))
            {
                checksum += closest.Distance + closest.PathZ + closest.SegmentIndex;
            }
        }

        return checksum;
    }
}
