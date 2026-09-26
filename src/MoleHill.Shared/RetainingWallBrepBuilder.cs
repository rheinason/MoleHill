using Rhino.Geometry;

namespace MoleHill.Shared;

internal static class RetainingWallBrepBuilder
{
    private sealed class RailSet
    {
        public Point3d[] FrontBottom { get; }
        public Point3d[] FrontTop { get; }
        public Point3d[] BackBottom { get; }
        public Point3d[] BackTop { get; }
        public int Count => FrontBottom.Length;

        public RailSet(Point3d[] frontBottom, Point3d[] frontTop, Point3d[] backBottom, Point3d[] backTop)
        {
            FrontBottom = frontBottom;
            FrontTop = frontTop;
            BackBottom = backBottom;
            BackTop = backTop;
        }
    }

    // Build the wall solid as four CONTINUOUS lofted side surfaces (front, back, bottom, top) plus two
    // end caps, rather than one small box per rail segment. The two input rails are resampled onto a
    // common set of stations — the union of both rails' vertex arc-length fractions — so every input
    // corner is preserved and the four longitudinal rails all share the same station count. Because the
    // side surfaces are lofted from those shared rail polylines, their edges match exactly and JoinBreps
    // welds them into a single watertight solid. The previous per-segment approach produced hundreds of
    // faces that JoinBreps could not re-weld once the rails were dense, unequal in count, or steep,
    // leaving a fragmented, invalid Brep (see the copied Terrain 1 retaining-wall case).
    public static Brep? Build(Point3d[] toePts, Point3d[] topPts, double tolerance, bool isClosed = false)
    {
        int minimum = isClosed ? 3 : 2;
        if (toePts.Length < minimum || topPts.Length < minimum)
            return null;

        RailSet? rails = BuildRails(toePts, topPts, isClosed, tolerance);
        if (rails == null || rails.Count < minimum)
            return null;

        var faces = new List<Brep>();
        AddLoft(faces, rails.FrontBottom, rails.FrontTop, isClosed);   // front face (along toe rail)
        AddLoft(faces, rails.BackBottom, rails.BackTop, isClosed);     // back face (along top rail)
        AddLoft(faces, rails.FrontBottom, rails.BackBottom, isClosed); // bottom face
        AddLoft(faces, rails.FrontTop, rails.BackTop, isClosed);       // top face

        if (!isClosed)
        {
            int last = rails.Count - 1;
            AddCap(faces, rails.FrontBottom[0], rails.FrontTop[0], rails.BackTop[0], rails.BackBottom[0], tolerance);
            AddCap(faces, rails.FrontBottom[last], rails.BackBottom[last], rails.BackTop[last], rails.FrontTop[last], tolerance);
        }

        if (faces.Count == 0)
            return null;

        Brep[]? joined = Brep.JoinBreps(faces, tolerance);
        if (joined is { Length: 1 })
        {
            Brep result = joined[0];
            if (!result.IsSolid)
                result = result.CapPlanarHoles(tolerance) ?? result;
            if (IsCompleteSolid(result))
                return result;
        }

        // Never select the largest joined fragment: doing so silently publishes a wall with a missing
        // section. A watertight mesh built from the same shared stations is a deterministic fallback
        // when Rhino's surface join cannot weld a dense or sharply turning loft.
        return BuildMeshFallback(rails, isClosed);
    }

    // Sample both rails at the union of their vertex stations, then split each station into the four
    // longitudinal rails: front/back are the toe/top XY columns, bottom/top are the low/high Z of that
    // station (so the wall face height is the elevation difference between the two rails, as before).
    private static RailSet? BuildRails(Point3d[] toePts, Point3d[] topPts, bool isClosed, double tolerance)
    {
        double[] toeCum = BuildCumLen(toePts, isClosed);
        double[] topCum = BuildCumLen(topPts, isClosed);
        double toeLen = toeCum[^1];
        double topLen = topCum[^1];
        if (toeLen <= 1e-12 || topLen <= 1e-12)
            return null;

        List<double> fractions = MergeStationFractions(toeCum, toeLen, topCum, topLen, isClosed);
        if (fractions.Count < 2)
            return null;

        int count = fractions.Count;
        var frontBottom = new Point3d[count];
        var frontTop = new Point3d[count];
        var backBottom = new Point3d[count];
        var backTop = new Point3d[count];

        for (int i = 0; i < count; i++)
        {
            Point3d toe = PointAtFraction(toePts, toeCum, toeLen, isClosed, fractions[i]);
            Point3d top = PointAtFraction(topPts, topCum, topLen, isClosed, fractions[i]);
            double low = Math.Min(toe.Z, top.Z);
            double high = Math.Max(toe.Z, top.Z);
            frontBottom[i] = new Point3d(toe.X, toe.Y, low);
            frontTop[i] = new Point3d(toe.X, toe.Y, high);
            backBottom[i] = new Point3d(top.X, top.Y, low);
            backTop[i] = new Point3d(top.X, top.Y, high);
        }

        return new RailSet(frontBottom, frontTop, backBottom, backTop);
    }

    // Union of both rails' normalized vertex fractions, deduplicated. Open walls always include the two
    // ends (0 and 1); closed walls drop the wrap-around duplicate at 1 so the loft can close cleanly.
    private static List<double> MergeStationFractions(
        double[] toeCum,
        double toeLen,
        double[] topCum,
        double topLen,
        bool isClosed)
    {
        var set = new SortedSet<double>();
        AddVertexFractions(set, toeCum, toeLen, isClosed);
        AddVertexFractions(set, topCum, topLen, isClosed);
        if (!isClosed)
        {
            set.Add(0.0);
            set.Add(1.0);
        }

        const double stationGap = 1e-7;
        var fractions = new List<double>(set.Count);
        foreach (double fraction in set)
        {
            if (isClosed && fraction >= 1.0 - stationGap)
                continue;
            if (fractions.Count == 0 || fraction - fractions[^1] > stationGap)
                fractions.Add(fraction);
        }

        return fractions;
    }

    private static void AddVertexFractions(SortedSet<double> set, double[] cum, double length, bool isClosed)
    {
        // For closed rails cum has one extra (wrap) entry; the vertices are cum[0..n-1].
        int vertexCount = isClosed ? cum.Length - 1 : cum.Length;
        for (int i = 0; i < vertexCount; i++)
            set.Add(cum[i] / length);
    }

    private static void AddLoft(List<Brep> faces, Point3d[] railA, Point3d[] railB, bool isClosed)
    {
        Curve a = MakeRailCurve(railA, isClosed);
        Curve b = MakeRailCurve(railB, isClosed);
        Brep[]? loft = Brep.CreateFromLoft(
            new[] { a, b },
            Point3d.Unset,
            Point3d.Unset,
            LoftType.Straight,
            closed: false);

        if (loft == null)
            return;

        foreach (Brep brep in loft)
        {
            if (brep != null && brep.IsValid)
                faces.Add(brep);
        }
    }

    private static Curve MakeRailCurve(Point3d[] rail, bool isClosed)
    {
        if (!isClosed)
            return new PolylineCurve(rail);

        // Closed ring: repeat the first station so the rail forms a closed loop; lofting two closed
        // loops (closed:false) yields a closed band, which the four bands then join into a solid ring.
        var closed = new Point3d[rail.Length + 1];
        Array.Copy(rail, closed, rail.Length);
        closed[^1] = rail[0];
        return new PolylineCurve(closed);
    }

    // Add a planar end cap from four corners in CCW order, degrading to a triangle when the wall tapers
    // to zero height at that end (front-bottom coincides with front-top, etc.).
    private static void AddCap(List<Brep> faces, Point3d a, Point3d b, Point3d c, Point3d d, double tolerance)
    {
        double pointTol = Math.Max(tolerance * 1e-3, Math.Max(Math.Abs(tolerance) * 1e-12, double.Epsilon));
        var pts = new List<Point3d>(4);
        foreach (Point3d p in new[] { a, b, c, d })
        {
            if (pts.Count == 0 || p.DistanceTo(pts[^1]) > pointTol)
                pts.Add(p);
        }
        if (pts.Count > 1 && pts[0].DistanceTo(pts[^1]) <= pointTol)
            pts.RemoveAt(pts.Count - 1);

        Brep? cap = pts.Count switch
        {
            3 => Brep.CreateFromCornerPoints(pts[0], pts[1], pts[2], tolerance),
            4 => Brep.CreateFromCornerPoints(pts[0], pts[1], pts[2], pts[3], tolerance),
            _ => null
        };

        if (cap != null && cap.IsValid)
            faces.Add(cap);
    }

    private static bool IsCompleteSolid(Brep? brep)
    {
        return brep is { IsValid: true, IsSolid: true } &&
               brep.Edges.All(edge => edge.Valence != EdgeAdjacency.Naked);
    }

    private static Brep? BuildMeshFallback(RailSet rails, bool isClosed)
    {
        var mesh = new Mesh();
        for (int i = 0; i < rails.Count; i++)
        {
            mesh.Vertices.Add(rails.FrontBottom[i]);
            mesh.Vertices.Add(rails.FrontTop[i]);
            mesh.Vertices.Add(rails.BackTop[i]);
            mesh.Vertices.Add(rails.BackBottom[i]);
        }

        int segmentCount = isClosed ? rails.Count : rails.Count - 1;
        for (int i = 0; i < segmentCount; i++)
        {
            int next = (i + 1) % rails.Count;
            int a = i * 4;
            int b = next * 4;
            mesh.Faces.AddFace(a, b, b + 1, a + 1);             // front
            mesh.Faces.AddFace(a + 3, a + 2, b + 2, b + 3);     // back
            mesh.Faces.AddFace(a, a + 3, b + 3, b);             // bottom
            mesh.Faces.AddFace(a + 1, b + 1, b + 2, a + 2);     // top
        }

        if (!isClosed)
        {
            int last = (rails.Count - 1) * 4;
            mesh.Faces.AddFace(0, 1, 2, 3);
            mesh.Faces.AddFace(last, last + 3, last + 2, last + 1);
        }

        mesh.Vertices.CullUnused();
        MeshNormalOrientation.UnifyAndComputeNormals(mesh);
        mesh.Compact();
        if (!mesh.IsValid || !mesh.IsClosed)
            return null;

        Brep? fallback = Brep.CreateFromMesh(mesh, trimmedTriangles: true);
        return IsCompleteSolid(fallback) ? fallback : null;
    }

    // Cumulative arc length per vertex. For a closed rail an extra trailing entry carries the length of
    // the wrap-around segment, so cum[^1] is the full ring length.
    private static double[] BuildCumLen(Point3d[] points, bool isClosed)
    {
        int extra = isClosed ? 1 : 0;
        var cum = new double[points.Length + extra];
        for (int i = 1; i < points.Length; i++)
            cum[i] = cum[i - 1] + points[i - 1].DistanceTo(points[i]);

        if (isClosed)
            cum[^1] = cum[points.Length - 1] + points[^1].DistanceTo(points[0]);

        return cum;
    }

    private static Point3d PointAtFraction(Point3d[] points, double[] cum, double length, bool isClosed, double fraction)
    {
        if (points.Length == 1)
            return points[0];

        double along = Math.Clamp(fraction, 0.0, 1.0) * length;
        int segments = isClosed ? points.Length : points.Length - 1;
        for (int i = 0; i < segments; i++)
        {
            double s0 = cum[i];
            double s1 = cum[i + 1];
            if (along > s1 && i < segments - 1)
                continue;

            int next = (i + 1) % points.Length;
            double span = s1 - s0;
            double t = span <= 1e-12 ? 0.0 : (along - s0) / span;
            t = Math.Clamp(t, 0.0, 1.0);
            Point3d p0 = points[i];
            Point3d p1 = points[next];
            return new Point3d(
                p0.X + ((p1.X - p0.X) * t),
                p0.Y + ((p1.Y - p0.Y) * t),
                p0.Z + ((p1.Z - p0.Z) * t));
        }

        return points[isClosed ? 0 : points.Length - 1];
    }
}
