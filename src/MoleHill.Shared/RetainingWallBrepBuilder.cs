using Rhino.Geometry;

namespace MoleHill.Shared;

internal static class RetainingWallBrepBuilder
{
    private readonly record struct Profile(
        Point3d FrontBottom,
        Point3d FrontTop,
        Point3d BackBottom,
        Point3d BackTop,
        double Height);

    private readonly record struct LoftPiece(
        Point3d ToeStart,
        Point3d ToeEnd,
        Point3d TopStart,
        Point3d TopEnd);

    public static Brep? Build(Point3d[] toePts, Point3d[] topPts, double tolerance, bool isClosed = false)
    {
        int minimum = isClosed ? 3 : 2;
        if (toePts.Length < minimum || topPts.Length < minimum)
            return null;

        List<LoftPiece> pieces = isClosed
            ? SmartLoftClosed(toePts, topPts)
            : SmartLoftOpen(toePts, topPts);

        if (pieces.Count == 0)
            return null;

        var faces = new List<Brep>();
        Profile? firstStart = null;
        Profile? lastEnd = null;
        double minHeight = Math.Max(tolerance * 0.01, 1e-6);

        foreach (LoftPiece piece in pieces)
        {
            Profile s = ProfileAt(piece.ToeStart, piece.TopStart);
            Profile e = ProfileAt(piece.ToeEnd, piece.TopEnd);
            firstStart ??= s;
            lastEnd = e;

            if (s.Height < minHeight && e.Height < minHeight)
                continue;

            // Front face: along toe rail, low Z to high Z. Loft from (s.fb -> e.fb) to (s.ft -> e.ft).
            AddPieceFace(faces, s.FrontBottom, e.FrontBottom, e.FrontTop, s.FrontTop, tolerance);
            // Back face: along top rail, high Z to low Z (reversed for outward +Y normal).
            AddPieceFace(faces, s.BackTop, e.BackTop, e.BackBottom, s.BackBottom, tolerance);
            // Bottom face: low-Z surface from toe XY to top XY.
            AddPieceFace(faces, s.FrontBottom, s.BackBottom, e.BackBottom, e.FrontBottom, tolerance);
            // Top face: high-Z surface from toe XY to top XY.
            AddPieceFace(faces, s.FrontTop, e.FrontTop, e.BackTop, s.BackTop, tolerance);
        }

        if (!isClosed)
        {
            if (firstStart.HasValue && firstStart.Value.Height >= minHeight)
            {
                Profile s = firstStart.Value;
                AddPieceFace(faces, s.FrontBottom, s.FrontTop, s.BackTop, s.BackBottom, tolerance);
            }
            if (lastEnd.HasValue && lastEnd.Value.Height >= minHeight)
            {
                Profile e = lastEnd.Value;
                AddPieceFace(faces, e.FrontBottom, e.BackBottom, e.BackTop, e.FrontTop, tolerance);
            }
        }

        if (faces.Count == 0)
            return null;

        Brep[]? joined = Brep.JoinBreps(faces, tolerance);
        if (joined == null || joined.Length == 0)
            return null;

        Brep result = joined[0];
        if (result == null)
            return null;

        if (!result.IsValid)
            result = result.CapPlanarHoles(tolerance) ?? result;

        return result.IsValid ? result : null;
    }

    private static Profile ProfileAt(Point3d toe, Point3d top)
    {
        double low = Math.Min(toe.Z, top.Z);
        double high = Math.Max(toe.Z, top.Z);
        return new Profile(
            new Point3d(toe.X, toe.Y, low),
            new Point3d(toe.X, toe.Y, high),
            new Point3d(top.X, top.Y, low),
            new Point3d(top.X, top.Y, high),
            high - low);
    }

    // AddPieceFace: build a single face Brep from 4 corners in CCW order with respect to
    // the desired outward normal. Lofts edge (P0,P1) to edge (P3,P2) for the non-degenerate
    // case, and falls back to a triangle when one edge collapses to a point.
    private static void AddPieceFace(List<Brep> faces, Point3d a, Point3d b, Point3d c, Point3d d, double tolerance)
    {
        double pointTol = Math.Max(tolerance * 1e-3, 1e-9);

        // Dedup adjacent identical corners and the seam.
        var pts = new List<Point3d>(4);
        foreach (Point3d p in new[] { a, b, c, d })
        {
            if (pts.Count == 0 || p.DistanceTo(pts[^1]) > pointTol)
                pts.Add(p);
        }
        if (pts.Count > 1 && pts[0].DistanceTo(pts[^1]) <= pointTol)
            pts.RemoveAt(pts.Count - 1);

        if (pts.Count < 3)
            return;

        if (pts.Count == 3)
        {
            Brep? tri = Brep.CreateFromCornerPoints(pts[0], pts[1], pts[2], tolerance);
            if (tri != null && tri.IsValid)
                faces.Add(tri);
            return;
        }

        // 4 distinct corners. Loft edges (P0,P1) and (P3,P2) — the two opposite "rails" of the quad.
        // This keeps the surface CCW around (P0, P1, P2, P3).
        var edge1 = new LineCurve(pts[0], pts[1]);
        var edge2 = new LineCurve(pts[3], pts[2]);
        Brep[]? loft = Brep.CreateFromLoft(
            new Curve[] { edge1, edge2 },
            Point3d.Unset,
            Point3d.Unset,
            LoftType.Straight,
            closed: false);

        bool added = false;
        if (loft != null)
        {
            foreach (Brep brep in loft)
            {
                if (brep != null && brep.IsValid)
                {
                    faces.Add(brep);
                    added = true;
                }
            }
        }

        if (!added)
        {
            // Loft fell over (e.g., near-zero edge length). Fall back to a planar quad if possible,
            // otherwise split into two triangles.
            Brep? quad = Brep.CreateFromCornerPoints(pts[0], pts[1], pts[2], pts[3], tolerance);
            if (quad != null && quad.IsValid)
            {
                faces.Add(quad);
                return;
            }
            Brep? t1 = Brep.CreateFromCornerPoints(pts[0], pts[1], pts[2], tolerance);
            Brep? t2 = Brep.CreateFromCornerPoints(pts[0], pts[2], pts[3], tolerance);
            if (t1 != null && t1.IsValid) faces.Add(t1);
            if (t2 != null && t2.IsValid) faces.Add(t2);
        }
    }

    private static List<LoftPiece> SmartLoftOpen(Point3d[] toe, Point3d[] top)
    {
        int xMax = toe.Length - 1;
        int yMax = top.Length - 1;
        if (xMax == 0 && yMax == 0)
            return new();

        var pieces = new List<LoftPiece>();
        int xi = 0;
        int yi = 0;
        Point3d lastXMid = xMax >= 1 ? SegMidOpen(toe, 0) : toe[0];
        Point3d lastYMid = yMax >= 1 ? SegMidOpen(top, 0) : top[0];

        int safety = (xMax + yMax) * 4 + 16;
        while ((xi < xMax || yi < yMax) && safety-- > 0)
        {
            bool advX;
            bool advY;
            if (xi >= xMax) { advX = false; advY = true; }
            else if (yi >= yMax) { advX = true; advY = false; }
            else
            {
                Point3d nextXMid = SegMidOpen(toe, xi);
                Point3d nextYMid = SegMidOpen(top, yi);
                double both = Distance2D(nextXMid, nextYMid);
                double xAdvance = Distance2D(nextXMid, lastYMid);
                double yAdvance = Distance2D(lastXMid, nextYMid);
                if (both <= xAdvance && both <= yAdvance) { advX = true; advY = true; }
                else if (yAdvance < xAdvance) { advX = false; advY = true; }
                else { advX = true; advY = false; }
            }

            Point3d toeStart = toe[xi];
            Point3d topStart = top[yi];
            if (advX)
            {
                lastXMid = SegMidOpen(toe, xi);
                xi++;
            }
            if (advY)
            {
                lastYMid = SegMidOpen(top, yi);
                yi++;
            }
            pieces.Add(new LoftPiece(toeStart, toe[xi], topStart, top[yi]));
        }

        return pieces;
    }

    private static List<LoftPiece> SmartLoftClosed(Point3d[] toe, Point3d[] top)
    {
        int xMax = toe.Length;
        int yMax = top.Length;
        if (xMax < 3 || yMax < 3)
            return new();

        var pieces = new List<LoftPiece>();
        int xi = 0;
        int yi = 0;
        Point3d lastXMid = SegMidClosed(toe, 0);
        Point3d lastYMid = SegMidClosed(top, 0);

        int safety = (xMax + yMax) * 4 + 16;
        while ((xi < xMax || yi < yMax) && safety-- > 0)
        {
            bool advX;
            bool advY;
            if (xi >= xMax) { advX = false; advY = true; }
            else if (yi >= yMax) { advX = true; advY = false; }
            else
            {
                Point3d nextXMid = SegMidClosed(toe, xi);
                Point3d nextYMid = SegMidClosed(top, yi);
                double both = Distance2D(nextXMid, nextYMid);
                double xAdvance = Distance2D(nextXMid, lastYMid);
                double yAdvance = Distance2D(lastXMid, nextYMid);
                if (both <= xAdvance && both <= yAdvance) { advX = true; advY = true; }
                else if (yAdvance < xAdvance) { advX = false; advY = true; }
                else { advX = true; advY = false; }
            }

            Point3d toeStart = toe[xi % xMax];
            Point3d topStart = top[yi % yMax];
            if (advX)
            {
                lastXMid = SegMidClosed(toe, xi);
                xi++;
            }
            if (advY)
            {
                lastYMid = SegMidClosed(top, yi);
                yi++;
            }
            pieces.Add(new LoftPiece(toeStart, toe[xi % xMax], topStart, top[yi % yMax]));
        }

        return pieces;
    }

    private static Point3d SegMidOpen(Point3d[] points, int segIndex)
    {
        Point3d a = points[segIndex];
        Point3d b = points[segIndex + 1];
        return new Point3d(
            (a.X + b.X) * 0.5,
            (a.Y + b.Y) * 0.5,
            (a.Z + b.Z) * 0.5);
    }

    private static Point3d SegMidClosed(Point3d[] points, int segIndex)
    {
        int n = points.Length;
        Point3d a = points[segIndex % n];
        Point3d b = points[(segIndex + 1) % n];
        return new Point3d(
            (a.X + b.X) * 0.5,
            (a.Y + b.Y) * 0.5,
            (a.Z + b.Z) * 0.5);
    }

    private static double Distance2D(Point3d a, Point3d b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}
