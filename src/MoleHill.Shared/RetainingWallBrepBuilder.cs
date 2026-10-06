using MoleHill.Core.Grading;
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
    // common set of plan stations, anchored at matching bends, so every input corner is preserved
    // opposite its partner and the four longitudinal rails all share the same station count. Because the
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

    // Synchronize corresponding plan bends before sampling between them, then split into the four
    // longitudinal rails: front/back are the toe/top XY columns, bottom/top are the low/high Z of that
    // station (so the wall face height is the elevation difference between the two rails, as before).
    private static RailSet? BuildRails(Point3d[] toePts, Point3d[] topPts, bool isClosed, double tolerance)
    {
        WallRailStationing.Result stations = WallRailStationing.Synchronize(
            Flatten(toePts), Flatten(topPts), isClosed, tolerance);
        int count = stations.First.Length / 3;
        if (count < 2)
            return null;
        var frontBottom = new Point3d[count];
        var frontTop = new Point3d[count];
        var backBottom = new Point3d[count];
        var backTop = new Point3d[count];

        for (int i = 0; i < count; i++)
        {
            Point3d toe = new(stations.First[i * 3], stations.First[i * 3 + 1], stations.First[i * 3 + 2]);
            Point3d top = new(stations.Second[i * 3], stations.Second[i * 3 + 1], stations.Second[i * 3 + 2]);
            double low = Math.Min(toe.Z, top.Z);
            double high = Math.Max(toe.Z, top.Z);
            frontBottom[i] = new Point3d(toe.X, toe.Y, low);
            frontTop[i] = new Point3d(toe.X, toe.Y, high);
            backBottom[i] = new Point3d(top.X, top.Y, low);
            backTop[i] = new Point3d(top.X, top.Y, high);
        }

        return new RailSet(frontBottom, frontTop, backBottom, backTop);
    }

    private static double[] Flatten(Point3d[] points)
    {
        var result = new double[points.Length * 3];
        for (int i = 0; i < points.Length; i++)
        {
            result[i * 3] = points[i].X;
            result[i * 3 + 1] = points[i].Y;
            result[i * 3 + 2] = points[i].Z;
        }
        return result;
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

}
