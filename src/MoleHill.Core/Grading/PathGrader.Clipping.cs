using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

public static partial class PathGrader
{
    private static IEnumerable<ConstraintPath> CreateBoundaryClippedRuns(
        double[] xyVertices,
        double[] zValues,
        int vertexCount,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double tolerance)
    {
        return CreateClippedRuns(
            xyVertices,
            zValues,
            vertexCount,
            hasBoundaryLoop,
            boundaryLoop,
            boundaryVertexCount,
            tolerance,
            PreparedBarriers.Empty,
            new SpatialHashGrid2D.QueryScratch(1),
            new List<int>(8));
    }

    private static IEnumerable<ConstraintPath> CreateClippedRuns(
        double[] xyVertices,
        double[] zValues,
        int vertexCount,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double tolerance,
        PreparedBarriers barriers,
        SpatialHashGrid2D.QueryScratch barrierScratch,
        List<int> barrierCandidates)
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
            double startX = xyVertices[i * 2];
            double startY = xyVertices[i * 2 + 1];
            double startZ = zValues[i];
            double endX = xyVertices[(i + 1) * 2];
            double endY = xyVertices[(i + 1) * 2 + 1];
            double endZ = zValues[i + 1];

            if (barriers.Segments.Length > 0 &&
                GradingBarriers.TryClipSegment(
                    barriers,
                    startX,
                    startY,
                    endX,
                    endY,
                    barrierScratch,
                    barrierCandidates,
                    out double clippedEndX,
                    out double clippedEndY))
            {
                double segmentDx = endX - startX;
                double segmentDy = endY - startY;
                double segmentLengthSquared = (segmentDx * segmentDx) + (segmentDy * segmentDy);
                double clipT = segmentLengthSquared <= 1e-12
                    ? 0.0
                    : (((clippedEndX - startX) * segmentDx) + ((clippedEndY - startY) * segmentDy)) / segmentLengthSquared;
                endX = clippedEndX;
                endY = clippedEndY;
                endZ = InterpolateSectionValue(startZ, endZ, Math.Clamp(clipT, 0.0, 1.0));
            }

            List<ClippedSegment> pieces = BoundaryClipper.ClipSegmentToBoundary(
                startX,
                startY,
                startZ,
                endX,
                endY,
                endZ,
                hasBoundaryLoop,
                boundaryLoop,
                boundaryVertexCount,
                tolerance);

            if (pieces.Count == 0)
            {
                    if (runZ.Count >= 2)
                        yield return SimplifyConstraintPathWithClipper(
                            MakeGeometryConstraintPath(runXy.ToArray(), runZ.ToArray(), runZ.Count),
                            tolerance);

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
                        yield return SimplifyConstraintPathWithClipper(
                            MakeGeometryConstraintPath(runXy.ToArray(), runZ.ToArray(), runZ.Count),
                            tolerance);
                        runXy.Clear();
                        runZ.Clear();
                    }
                }

                AppendRunPoint(runXy, runZ, piece.StartX, piece.StartY, piece.StartZ, tolerance);
                AppendRunPoint(runXy, runZ, piece.EndX, piece.EndY, piece.EndZ, tolerance);
            }
        }

        if (runZ.Count >= 2)
            yield return SimplifyConstraintPathWithClipper(
                MakeGeometryConstraintPath(runXy.ToArray(), runZ.ToArray(), runZ.Count),
                tolerance);
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

    private static void AddBoundaryClippedConstraintRuns(
        List<SurfaceRemesher.ConstraintPolyline> constraints,
        double[] xyVertices,
        double[] zValues,
        int vertexCount,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double tolerance)
    {
        foreach (ConstraintPath run in CreateBoundaryClippedRuns(
                     xyVertices,
                     zValues,
                     vertexCount,
                     hasBoundaryLoop,
                     boundaryLoop,
                     boundaryVertexCount,
                     tolerance))
        {
            AddConstraintPolyline(constraints, run.XyVertices, run.ZValues, run.VertexCount);
        }
    }

    private static void AppendConstraintPoint(List<double> points, double x, double y, double z, double tolerance)
    {
        if (points.Count >= 3)
        {
            double dx = x - points[^3];
            double dy = y - points[^2];
            if ((dx * dx) + (dy * dy) <= tolerance * tolerance)
            {
                points[^3] = x;
                points[^2] = y;
                points[^1] = z;
                return;
            }
        }

        points.Add(x);
        points.Add(y);
        points.Add(z);
    }

    private static ConstraintPath SimplifyConstraintPathWithClipper(ConstraintPath path, double tolerance)
    {
        if (path.VertexCount < 3 ||
            IsApproximatelyStraight(path.XyVertices, path.VertexCount, tolerance) ||
            !ClipperGeometry.TrySimplifyOpenPolyline(path.XyVertices, tolerance, out double[] simplifiedXy))
        {
            return path;
        }

        int simplifiedCount = simplifiedXy.Length / 2;
        if (simplifiedCount < 2)
            return path;

        var simplifiedZ = new double[simplifiedCount];
        for (int i = 0; i < simplifiedCount; i++)
        {
            if (!TrySampleConstraintPathZ(
                path.XyVertices,
                path.ZValues,
                path.VertexCount,
                simplifiedXy[i * 2],
                simplifiedXy[i * 2 + 1],
                out simplifiedZ[i]))
            {
                return path;
            }
        }

        return MakeGeometryConstraintPath(simplifiedXy, simplifiedZ, simplifiedCount);
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
}