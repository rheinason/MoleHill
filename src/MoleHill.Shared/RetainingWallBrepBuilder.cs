using MoleHill.Core.Grading;
using Rhino;
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
        IReadOnlyList<Brep> pieces = BuildPieces(toePts, topPts, tolerance, isClosed);
        return pieces.Count == 1 ? pieces[0] : null;
    }

    /// <summary>
    /// The wall as one solid per stretch where it has both plan width and height. A kerb dropped to zero
    /// height for a crossing, or rails drawn to a point or round nose, leave stations with no section at
    /// all, and a loft through them collapses; so the wall is cut there, each piece ending in a tiny cap at
    /// the interpolated point where its section vanishes. Empty when no piece could be made watertight.
    /// A <paramref name="freestandingHeight"/> stands the wall up from two rails lying on the ground: at each
    /// station it runs from the lower rail up to the higher one plus that height, so the wall is that tall
    /// above the ground on its higher side however the ground falls across it.
    /// </summary>
    public static IReadOnlyList<Brep> BuildPieces(
        Point3d[] toePts, Point3d[] topPts, double tolerance, bool isClosed = false, double freestandingHeight = 0.0)
    {
        int minimum = isClosed ? 3 : 2;
        if (toePts.Length < minimum || topPts.Length < minimum)
            return Array.Empty<Brep>();

        WallRailStationing.Result stations = WallRailStationing.Synchronize(
            Flatten(toePts), Flatten(topPts), isClosed, tolerance);
        Point3d[] toe = Unflatten(stations.First);
        Point3d[] top = Unflatten(stations.Second);
        if (toe.Length < 2 || toe.Length != top.Length)
            return Array.Empty<Brep>();
        if (freestandingHeight > 0.0)
            StandUp(toe, top, freestandingHeight);

        var result = new List<Brep>();
        foreach ((Point3d[] pieceToe, Point3d[] pieceTop, bool pieceClosed) in SplitAtVanishingSections(toe, top, isClosed, SectionEpsilon(tolerance)))
        {
            RailSet rails = ToRailSet(pieceToe, pieceTop);
            if (rails.Count < (pieceClosed ? 3 : 2))
                continue;
            Brep? piece = BuildFromRails(rails, pieceClosed, tolerance);
            if (piece != null)
                result.Add(piece);
        }

        return result;
    }

    /// <summary>The smallest section side a piece keeps: below it the cap would be shorter than the join tolerance.</summary>
    internal static double SectionEpsilon(double tolerance) => Math.Max(2.0 * Math.Abs(tolerance), 1e-9);

    /// <summary>
    /// Splits synchronized stations into runs whose section (plan width or height, whichever is smaller)
    /// is at least <paramref name="epsilon"/>. Each run is extended by a station interpolated to where its
    /// section is back down to <paramref name="epsilon"/>, so a wall tapering into a kerb cut or a point keeps
    /// its shape almost all the way. A wall with no vanishing section is returned whole.
    /// </summary>
    internal static List<(Point3d[] Toe, Point3d[] Top, bool IsClosed)> SplitAtVanishingSections(
        Point3d[] toe, Point3d[] top, bool isClosed, double epsilon)
    {
        int n = toe.Length;
        var slack = new double[n];
        bool anyVanishing = false;
        for (int i = 0; i < n; i++)
        {
            slack[i] = Section(toe[i], top[i]) - epsilon;
            anyVanishing |= slack[i] < 0.0;
        }

        var pieces = new List<(Point3d[] Toe, Point3d[] Top, bool IsClosed)>();
        if (!anyVanishing)
        {
            pieces.Add((toe, top, isClosed));
            return pieces;
        }

        // A closed ring with a cut opens at it: start the walk on a vanishing station and come back to it.
        if (isClosed)
        {
            int start = Array.FindIndex(slack, value => value < 0.0);
            toe = Rotate(toe, start);
            top = Rotate(top, start);
            slack = Rotate(slack, start);
            n = toe.Length;
        }

        int index = 0;
        while (index < n)
        {
            while (index < n && slack[index] < 0.0)
                index++;
            if (index >= n)
                break;

            int runStart = index;
            while (index < n && slack[index] >= 0.0)
                index++;
            int runEnd = index - 1;

            var pieceToe = new List<Point3d>();
            var pieceTop = new List<Point3d>();
            if (runStart > 0)
                AddInterpolated(pieceToe, pieceTop, toe, top, slack, runStart, runStart - 1);
            for (int i = runStart; i <= runEnd; i++)
            {
                pieceToe.Add(toe[i]);
                pieceTop.Add(top[i]);
            }
            if (runEnd < n - 1)
                AddInterpolated(pieceToe, pieceTop, toe, top, slack, runEnd, runEnd + 1);

            if (pieceToe.Count >= 2)
                pieces.Add((pieceToe.ToArray(), pieceTop.ToArray(), false));
        }

        return pieces;
    }

    private static void StandUp(Point3d[] toe, Point3d[] top, double height)
    {
        for (int i = 0; i < toe.Length; i++)
        {
            double low = Math.Min(toe[i].Z, top[i].Z);
            double high = Math.Max(toe[i].Z, top[i].Z) + height;
            toe[i] = new Point3d(toe[i].X, toe[i].Y, low);
            top[i] = new Point3d(top[i].X, top[i].Y, high);
        }
    }

    private static double Section(Point3d toe, Point3d top)
    {
        double width = Math.Sqrt((toe.X - top.X) * (toe.X - top.X) + (toe.Y - top.Y) * (toe.Y - top.Y));
        return Math.Min(width, Math.Abs(top.Z - toe.Z));
    }

    // The station between a kept station and a vanishing one where the section is back down to epsilon.
    private static void AddInterpolated(
        List<Point3d> pieceToe, List<Point3d> pieceTop, Point3d[] toe, Point3d[] top, double[] slack, int kept, int vanishing)
    {
        double denominator = slack[kept] - slack[vanishing];
        double s = denominator > 0.0 ? Math.Clamp(slack[kept] / denominator, 0.0, 1.0) : 0.0;
        if (s <= 1e-9)
            return;

        pieceToe.Add(toe[kept] + (toe[vanishing] - toe[kept]) * s);
        pieceTop.Add(top[kept] + (top[vanishing] - top[kept]) * s);
    }

    // Starts the ring at index start and repeats that station at the end, so the walk comes back to it.
    private static T[] Rotate<T>(T[] values, int start)
    {
        var result = new T[values.Length + 1];
        for (int i = 0; i < values.Length; i++)
            result[i] = values[(start + i) % values.Length];
        result[^1] = values[start];
        return result;
    }

    private static Brep? BuildFromRails(RailSet rails, bool isClosed, double tolerance)
    {
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
                return SplitAtBends(result);
        }

        // Never select the largest joined fragment: doing so silently publishes a wall with a missing
        // section. A watertight mesh built from the same shared stations is a deterministic fallback
        // when Rhino's surface join cannot weld a dense or sharply turning loft.
        return BuildMeshFallback(rails, isClosed);
    }

    // Split synchronized stations into the four longitudinal rails: front/back are the toe/top XY columns,
    // bottom/top are the low/high Z of that station (so the wall face height is the elevation difference
    // between the two rails, as before).
    private static RailSet ToRailSet(Point3d[] toePts, Point3d[] topPts)
    {
        int count = toePts.Length;
        var frontBottom = new Point3d[count];
        var frontTop = new Point3d[count];
        var backBottom = new Point3d[count];
        var backTop = new Point3d[count];

        for (int i = 0; i < count; i++)
        {
            Point3d toe = toePts[i];
            Point3d top = topPts[i];
            double low = Math.Min(toe.Z, top.Z);
            double high = Math.Max(toe.Z, top.Z);
            frontBottom[i] = new Point3d(toe.X, toe.Y, low);
            frontTop[i] = new Point3d(toe.X, toe.Y, high);
            backBottom[i] = new Point3d(top.X, top.Y, low);
            backTop[i] = new Point3d(top.X, top.Y, high);
        }

        return new RailSet(frontBottom, frontTop, backBottom, backTop);
    }

    private static Point3d[] Unflatten(double[] flat)
    {
        var result = new Point3d[flat.Length / 3];
        for (int i = 0; i < result.Length; i++)
            result[i] = new Point3d(flat[i * 3], flat[i * 3 + 1], flat[i * 3 + 2]);
        return result;
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

    // Each lofted side is ONE face that bends at every plan corner of the rails. Meshing a face welds its
    // vertices across those bends, so the preview averaged the normals of two panels meeting at 90° and
    // shaded every wall panel as a dark-to-light smear. Baking never showed it, because AddBrep splits
    // kinky faces on the way into the document; splitting here makes the preview mesh one face per panel
    // too, so preview and bake shade alike. A solid that fails to split is kept as it was.
    private static Brep SplitAtBends(Brep brep)
    {
        Brep split = brep.DuplicateBrep();
        if (split.Faces.SplitKinkyFaces(RhinoMath.DefaultAngleTolerance, compact: true) && IsCompleteSolid(split))
            return split;
        split.Dispose();
        return brep;
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
