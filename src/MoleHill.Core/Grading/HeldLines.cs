using MoleHill.Core.Engine;
using MoleHill.Core.Geometry;

namespace MoleHill.Core.Grading;

/// <summary>
/// Plan line segments a height-only edit of the terrain holds: the edit is zero on them and fades back in
/// with plan distance, so nothing steps where it stops. Shared by Round Creases (<see cref="CreaseRounder"/>)
/// and Displace (<see cref="Displacement.TerrainDisplacer"/>), which hold the same things — protect curves,
/// walls, the border — and must fade out of them the same way.
/// </summary>
internal sealed class HeldLines
{
    private readonly SpatialHashGrid2D _index;

    private HeldLines(double[] segments)
    {
        Segments = segments;
        var bounds = new Bounds2D[segments.Length / 4];
        for (int i = 0; i < bounds.Length; i++)
            bounds[i] = SegmentBounds(segments[i * 4], segments[i * 4 + 1], segments[i * 4 + 2], segments[i * 4 + 3]);
        _index = SpatialHashGrid2D.Build(bounds);
    }

    /// <summary>The segments, flat <c>[ax, ay, bx, by]</c> per segment.</summary>
    public double[] Segments { get; }

    public int SegmentCount => Segments.Length / 4;

    public bool IsEmpty => Segments.Length == 0;

    /// <summary>Holds flat <c>[ax, ay, bx, by]</c> segments.</summary>
    public static HeldLines FromSegments(double[] segments) => new(segments);

    /// <summary>Holds every segment of <paramref name="lines"/> (closing the closed ones).</summary>
    public static HeldLines FromPolylines(IEnumerable<MeshSmoother.BreaklinePolyline> lines)
    {
        var flat = new List<double>();
        foreach (MeshSmoother.BreaklinePolyline line in lines)
            AddPolyline(flat, line.XyPts, line.PointCount, line.IsClosed);
        return new HeldLines(flat.ToArray());
    }

    /// <summary>Appends the segments of a plan polyline to a flat segment list.</summary>
    public static void AddPolyline(List<double> flat, double[] xy, int pointCount, bool isClosed)
    {
        int count = Math.Min(pointCount, xy.Length / 2);
        int segmentCount = isClosed && count > 2 ? count : count - 1;
        for (int i = 0; i < segmentCount; i++)
        {
            int j = (i + 1) % count;
            flat.Add(xy[i * 2]);
            flat.Add(xy[i * 2 + 1]);
            flat.Add(xy[j * 2]);
            flat.Add(xy[j * 2 + 1]);
        }
    }

    /// <summary>Appends the plan segment between mesh vertices <paramref name="a"/> and <paramref name="b"/>.</summary>
    public static void AddMeshEdge(List<double> flat, double[] vertices, int a, int b)
    {
        flat.Add(vertices[a * 3]);
        flat.Add(vertices[a * 3 + 1]);
        flat.Add(vertices[b * 3]);
        flat.Add(vertices[b * 3 + 1]);
    }

    /// <summary>Does (px, py) lie within <paramref name="tolerance"/> of a held segment, in plan?</summary>
    public bool IsOn(double px, double py, double tolerance, List<int>? candidates = null, SpatialHashGrid2D.QueryScratch? scratch = null)
    {
        if (Segments.Length == 0)
            return false;

        candidates ??= new List<int>(4);
        _index.GatherCandidates(Bounds2D.FromPoint(px, py, tolerance), candidates, scratch);
        double tolSq = tolerance * tolerance;
        double[] s = Segments;
        foreach (int i in candidates)
        {
            double ax = s[i * 4], ay = s[i * 4 + 1], bx = s[i * 4 + 2], by = s[i * 4 + 3];
            double t = Geometry2D.ParameterOnSegmentClamped(ax, ay, bx, by, px, py);
            if (Geometry2D.DistanceSquared(px, py, ax + ((bx - ax) * t), ay + ((by - ay) * t)) <= tolSq)
                return true;
        }

        return false;
    }

    /// <summary>Plan distance from (px, py) to the nearest held segment, or <paramref name="reach"/> when none is nearer.</summary>
    public double NearestDistance(double px, double py, double reach, List<int> candidates, SpatialHashGrid2D.QueryScratch? scratch = null)
    {
        if (Segments.Length == 0 || !(reach > 0.0))
            return Math.Max(reach, 0.0);

        _index.GatherCandidates(Bounds2D.FromPoint(px, py, reach), candidates, scratch);
        double nearest = reach;
        double[] s = Segments;
        foreach (int i in candidates)
        {
            double ax = s[i * 4], ay = s[i * 4 + 1], bx = s[i * 4 + 2], by = s[i * 4 + 3];
            double t = Geometry2D.ParameterOnSegmentClamped(ax, ay, bx, by, px, py);
            double d = Math.Sqrt(Geometry2D.DistanceSquared(px, py, ax + ((bx - ax) * t), ay + ((by - ay) * t)));
            nearest = Math.Min(nearest, d);
        }

        return nearest;
    }

    /// <summary>
    /// 0 on a held line, rising smoothly (smoothstep) to 1 at <paramref name="reach"/> from the nearest one.
    /// A reach of zero means no fade: 1 everywhere.
    /// </summary>
    public double Weight(double px, double py, double reach, List<int> candidates, SpatialHashGrid2D.QueryScratch? scratch = null)
    {
        if (Segments.Length == 0 || !(reach > 0.0))
            return 1.0;

        return Smoothstep(NearestDistance(px, py, reach, candidates, scratch) / reach);
    }

    /// <summary>3u² − 2u³ on u clamped to [0, 1]: zero slope at both ends, so a fade never kinks.</summary>
    public static double Smoothstep(double u)
    {
        u = Math.Clamp(u, 0.0, 1.0);
        return u * u * (3.0 - (2.0 * u));
    }

    /// <summary>
    /// Vertices inside any of <paramref name="boundaries"/> (closed plan polygons); every vertex when there
    /// are none.
    /// </summary>
    public static bool[] InsideBoundaries(double[] vertices, int vertexCount, (double[] xyVerts, int vertCount)[] boundaries)
    {
        var inside = new bool[vertexCount];
        if (boundaries.Length == 0)
        {
            Array.Fill(inside, true);
            return inside;
        }

        var bounds = new Bounds2D[boundaries.Length];
        for (int i = 0; i < boundaries.Length; i++)
            bounds[i] = PolylineBounds(boundaries[i].xyVerts, boundaries[i].vertCount);

        for (int v = 0; v < vertexCount; v++)
        {
            double px = vertices[v * 3], py = vertices[v * 3 + 1];
            var point = Bounds2D.FromPoint(px, py);
            for (int i = 0; i < boundaries.Length && !inside[v]; i++)
                inside[v] = bounds[i].Intersects(point) && Geometry2D.PointInPolygon(px, py, boundaries[i].xyVerts, boundaries[i].vertCount);
        }

        return inside;
    }

    /// <summary>
    /// Marks every vertex of a wall face held and adds the face's edges to <paramref name="fade"/>, so the
    /// edit neither moves a wall (walls are never buried) nor steps beside one.
    /// </summary>
    public static void HoldWallFaces(double[] vertices, int[] faces, int faceCount, bool[] wallFaces, bool[] held, List<double> fade)
    {
        for (int f = 0; f < faceCount; f++)
        {
            if (!wallFaces[f])
                continue;
            for (int k = 0; k < 3; k++)
            {
                int a = faces[f * 3 + k];
                int b = faces[f * 3 + ((k + 1) % 3)];
                held[a] = true;
                AddMeshEdge(fade, vertices, a, b);
            }
        }
    }

    internal static Bounds2D SegmentBounds(double ax, double ay, double bx, double by) =>
        new(Math.Min(ax, bx), Math.Max(ax, bx), Math.Min(ay, by), Math.Max(ay, by));

    internal static Bounds2D PolylineBounds(double[] xy, int count)
    {
        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
        double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
        for (int i = 0; i < count; i++)
        {
            minX = Math.Min(minX, xy[i * 2]);
            maxX = Math.Max(maxX, xy[i * 2]);
            minY = Math.Min(minY, xy[i * 2 + 1]);
            maxY = Math.Max(maxY, xy[i * 2 + 1]);
        }

        return new Bounds2D(minX, maxX, minY, maxY);
    }
}
