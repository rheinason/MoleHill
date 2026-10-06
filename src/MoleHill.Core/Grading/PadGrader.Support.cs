using MoleHill.Core.Geometry;

namespace MoleHill.Core.Grading;

public static partial class PadGrader
{
    internal static bool TryBuildBoundaryLoop(double[] vertices, int[] faces, int faceCount, out double[] boundaryXy, out int boundaryVertexCount)
    {
        return MeshBoundaryLoopBuilder.TryBuildBoundaryLoop(vertices, faces, faceCount, out boundaryXy, out boundaryVertexCount);
    }

    internal static bool AllPointsInsideOrOnBoundary(double[] xy, int vertexCount, double[] boundaryLoop, int boundaryVertexCount, double tolerance)
    {
        return Geometry2D.AllPointsInsideOrOnBoundary(xy, vertexCount, boundaryLoop, boundaryVertexCount, tolerance);
    }

    private static bool TryIntersectLines(
        double ax, double ay, double adx, double ady,
        double bx, double by, double bdx, double bdy,
        out double ix, out double iy)
    {
        double denom = adx * bdy - ady * bdx;
        if (Math.Abs(denom) < 1e-12)
        {
            ix = 0;
            iy = 0;
            return false;
        }

        double t = ((bx - ax) * bdy - (by - ay) * bdx) / denom;
        ix = ax + t * adx;
        iy = ay + t * ady;
        return true;
    }

    internal static double DistToBoundaryWithZ(
        double px,
        double py,
        double[] boundaryVertices,
        int boundaryVertexCount,
        out double boundaryZ,
        out double closestBx,
        out double closestBy)
    {
        boundaryZ = 0;
        closestBx = px;
        closestBy = py;
        double minDist = double.MaxValue;

        for (int i = 0; i < boundaryVertexCount; i++)
        {
            int next = (i + 1) % boundaryVertexCount;
            double ax = boundaryVertices[i * 3];
            double ay = boundaryVertices[i * 3 + 1];
            double az = boundaryVertices[i * 3 + 2];
            double bx = boundaryVertices[next * 3];
            double by = boundaryVertices[next * 3 + 1];
            double bz = boundaryVertices[next * 3 + 2];

            double dx = bx - ax;
            double dy = by - ay;
            double lenSq = dx * dx + dy * dy;
            double t = 0;
            double cx = ax;
            double cy = ay;
            if (lenSq > 1e-20)
            {
                t = Math.Clamp(((px - ax) * dx + (py - ay) * dy) / lenSq, 0.0, 1.0);
                cx = ax + t * dx;
                cy = ay + t * dy;
            }

            double dist = Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
            if (dist >= minDist)
                continue;

            minDist = dist;
            boundaryZ = az + (bz - az) * t;
            closestBx = cx;
            closestBy = cy;
        }

        return minDist;
    }
}
