using MoleHill.Core.Engine;
using static MoleHill.Core.Grading.FaceCutGeometry;

namespace MoleHill.Core.Grading;

/// <summary>
/// Geometry for one terrain face. Built on demand from the flat arrays and never retained per face, so
/// face count costs no managed objects. Pass it by <c>in</c> to avoid copying at helper call sites.
/// Edge 0 runs A-B, edge 1 B-C, edge 2 C-A.
/// </summary>
internal readonly struct FaceData
{
    public FaceData(double[] vertices, int[] faces, int faceIndex)
    {
        I0 = faces[faceIndex * 3];
        I1 = faces[(faceIndex * 3) + 1];
        I2 = faces[(faceIndex * 3) + 2];
        A = new Point2D(vertices[I0 * 3], vertices[(I0 * 3) + 1]);
        B = new Point2D(vertices[I1 * 3], vertices[(I1 * 3) + 1]);
        C = new Point2D(vertices[I2 * 3], vertices[(I2 * 3) + 1]);
        Az = vertices[(I0 * 3) + 2];
        Bz = vertices[(I1 * 3) + 2];
        Cz = vertices[(I2 * 3) + 2];
        Bounds = new Bounds2D(
            Math.Min(A.X, Math.Min(B.X, C.X)),
            Math.Max(A.X, Math.Max(B.X, C.X)),
            Math.Min(A.Y, Math.Min(B.Y, C.Y)),
            Math.Max(A.Y, Math.Max(B.Y, C.Y)));
    }

    public int I0 { get; }
    public int I1 { get; }
    public int I2 { get; }
    public Point2D A { get; }
    public Point2D B { get; }
    public Point2D C { get; }
    public double Az { get; }
    public double Bz { get; }
    public double Cz { get; }
    public Bounds2D Bounds { get; }

    public Point2D GetVertex(int index) => index switch
    {
        0 => A,
        1 => B,
        _ => C
    };

    public Point2D GetEdgeStart(int edgeIndex) => edgeIndex switch
    {
        0 => A,
        1 => B,
        _ => C
    };

    public Point2D GetEdgeEnd(int edgeIndex) => edgeIndex switch
    {
        0 => B,
        1 => C,
        _ => A
    };

    /// <summary>The global vertex ids of edge <paramref name="edgeIndex"/>, in face winding order.</summary>
    public (int Start, int End) GetEdgeVertices(int edgeIndex) => edgeIndex switch
    {
        0 => (I0, I1),
        1 => (I1, I2),
        _ => (I2, I0)
    };

    /// <summary>True when the face runs clockwise in plan.</summary>
    public bool IsClockwise => Cross(B.X - A.X, B.Y - A.Y, C.X - A.X, C.Y - A.Y) < 0.0;

    public bool IsNearVertex(Point2D point, double tolerance)
    {
        double tolSq = tolerance * tolerance;
        return DistanceSquared(point, A) <= tolSq ||
               DistanceSquared(point, B) <= tolSq ||
               DistanceSquared(point, C) <= tolSq;
    }

    public bool ContainsPoint(Point2D point, double tolerance)
    {
        double x0 = A.X;
        double y0 = A.Y;
        double x1 = B.X;
        double y1 = B.Y;
        double x2 = C.X;
        double y2 = C.Y;
        double denom = ((y1 - y2) * (x0 - x2)) + ((x2 - x1) * (y0 - y2));
        if (Math.Abs(denom) <= 1e-16)
            return false;

        double w0 = (((y1 - y2) * (point.X - x2)) + ((x2 - x1) * (point.Y - y2))) / denom;
        double w1 = (((y2 - y0) * (point.X - x2)) + ((x0 - x2) * (point.Y - y2))) / denom;
        double w2 = 1.0 - w0 - w1;
        const double barycentricTolerance = 1e-8;
        return w0 >= -barycentricTolerance &&
               w1 >= -barycentricTolerance &&
               w2 >= -barycentricTolerance;
    }

    public double InterpolateZ(Point2D point)
    {
        double x0 = A.X;
        double y0 = A.Y;
        double x1 = B.X;
        double y1 = B.Y;
        double x2 = C.X;
        double y2 = C.Y;
        double denom = ((y1 - y2) * (x0 - x2)) + ((x2 - x1) * (y0 - y2));
        if (Math.Abs(denom) <= 1e-16)
            return Az;

        double w0 = (((y1 - y2) * (point.X - x2)) + ((x2 - x1) * (point.Y - y2))) / denom;
        double w1 = (((y2 - y0) * (point.X - x2)) + ((x0 - x2) * (point.Y - y2))) / denom;
        double w2 = 1.0 - w0 - w1;
        return (w0 * Az) + (w1 * Bz) + (w2 * Cz);
    }

    public int GetEdgeIndex(Point2D point, double tolerance)
    {
        for (int edgeIndex = 0; edgeIndex < 3; edgeIndex++)
        {
            if (PointOnSegment(point, GetEdgeStart(edgeIndex), GetEdgeEnd(edgeIndex), tolerance))
                return edgeIndex;
        }

        return -1;
    }
}
