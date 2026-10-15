using MoleHill.Core.Engine;
using MoleHill.Core.Geometry;

namespace MoleHill.Core.Grading;

/// <summary>
/// Rounds the terrain's creases — batter crests and toes — with a parabolic vertical curve of a stated
/// horizontal length, the road designer's answer to a change of grade. A crease whose grade changes by κ
/// (measured across it) moves a point at plan distance d by κ·(L/2 − d)² / 2L: κL/8 on the crease and
/// nothing at L/2. That is exactly a box blur of width L across the kink, so the bands of neighbouring
/// creases add up correctly — a short batter's crest and toe become one S-curve. Crests only drop, toes
/// only rise, plan never moves, and detail inside the band rides on top of the offset.
/// <para>
/// The offset is computed as a blur would produce it — every crease edge pulls each nearby point by its κ
/// integrated against a radial kernel (<see cref="Kernel"/>) — so bends, pad corners and the fan of folds
/// in a coned batter end round as one surface, and no crease is counted twice however the mesh splits it.
/// Wall faces (at or above <see cref="WallFaceMinSlopeDeg"/>) never move and their edges are never
/// creases: walls are never buried. Protect curves keep their creases sharp. The offset fades to zero over
/// L/2 approaching a protect curve, a wall, the mesh border and the scoping boundaries, so nothing steps
/// where rounding stops.
/// </para>
/// </summary>
public static class CreaseRounder
{
    /// <summary>Faces at least this steep are walls: never moved, never part of a crease.</summary>
    public const double WallFaceMinSlopeDeg = 70.0;

    /// <summary>One crease chain: a polyline of mesh vertices with the grade change at each point.</summary>
    internal sealed class CreaseChain
    {
        public required int[] Vertices { get; init; }

        /// <summary>Plan XY per chain point, flat.</summary>
        public required double[] Xy { get; init; }

        /// <summary>Grade change across the crease per chain point (rise over run, signed: negative on a crest).</summary>
        public required double[] Kappa { get; init; }

        /// <summary>Plan length of the connected crease network this chain belongs to.</summary>
        public required double ComponentLength { get; init; }

        public required double Length { get; init; }

        /// <summary>Per segment: the plan clearance from the crease to the nearest off-crease vertex of its two faces.</summary>
        public required double[] SegmentClearance { get; init; }
    }

    public sealed class PreparedCreaseRounding
    {
        public required int VertexCount { get; init; }

        /// <summary>Crease edges found (before the length filter of <see cref="Round"/>).</summary>
        public required int CreaseEdgeCount { get; init; }

        /// <summary>Plan length of every crease found.</summary>
        public required double CreaseLength { get; init; }

        /// <summary>Vertices that never move: on a protect curve, on a wall, on the border, or outside every boundary.</summary>
        public required bool[] Held { get; init; }

        internal CreaseChain[] Chains { get; init; } = null!;

        /// <summary>Crease segments as (chain, segment index), indexed by <see cref="CreaseIndex"/>.</summary>
        internal (int Chain, int Segment)[] CreaseSegments { get; init; } = null!;

        internal SpatialHashGrid2D CreaseIndex { get; init; } = null!;

        /// <summary>Lines the offset fades to zero at.</summary>
        internal HeldLines Fade { get; init; } = null!;
    }

    public readonly record struct CreaseRoundingResult(
        double[] Vertices,
        int MovedVertexCount,
        double MaxOffset,
        double RoundedCreaseLength,
        double CoarseCreaseLength);

    /// <summary>
    /// Finds the creases of <paramref name="mesh"/> and everything rounding them depends on except the
    /// length, so scrubbing the length reuses this.
    /// </summary>
    /// <param name="boundaries">Closed plan polygons scoping the rounding; empty = the whole terrain.</param>
    /// <param name="protect">Curves whose creases stay sharp and whose vertices hold their height.</param>
    /// <param name="creaseAngleDeg">Dihedral angle at or above which an edge is a crease.</param>
    /// <param name="tolerance">Plan distance within which a vertex lies on a protect curve.</param>
    public static PreparedCreaseRounding Prepare(
        IndexedTriMesh mesh,
        (double[] xyVerts, int vertCount)[] boundaries,
        MeshSmoother.BreaklinePolyline[] protect,
        double creaseAngleDeg,
        double tolerance)
    {
        (double[] vertices, int vertexCount, int[] faces, int faceCount) = mesh;
        double tol = Math.Max(tolerance, 1e-9);

        bool[] wallFaces = FeaturePolylineGraph.BuildFrozenFaceMask(vertices, faces, faceCount, WallFaceMinSlopeDeg, degenerateAltitude: tol);
        bool[] inside = HeldLines.InsideBoundaries(vertices, vertexCount, boundaries);
        HeldLines protectLines = HeldLines.FromPolylines(protect);
        bool[] onProtect = BuildProtectMask(vertices, vertexCount, protectLines, tol);

        // Edge → its faces (count > 2 is non-manifold; such an edge is neither crease nor border).
        var edgeFaces = IndexedMeshTools.CreateEdgeKeyMap<(int F0, int F1, int Count)>(faceCount * 2);
        for (int f = 0; f < faceCount; f++)
        {
            for (int k = 0; k < 3; k++)
            {
                int a = faces[f * 3 + k];
                int b = faces[f * 3 + ((k + 1) % 3)];
                if (a == b)
                    continue;
                long key = IndexedMeshTools.GetEdgeKey(a, b);
                edgeFaces[key] = edgeFaces.TryGetValue(key, out var e)
                    ? (e.F0, e.Count == 1 ? f : e.F1, e.Count + 1)
                    : (f, -1, 1);
            }
        }

        var held = new bool[vertexCount];
        var fade = new List<double>();
        for (int v = 0; v < vertexCount; v++)
            held[v] = !inside[v] || onProtect[v];

        HeldLines.HoldWallFaces(vertices, faces, faceCount, wallFaces, held, fade);

        double cosThreshold = Math.Cos(Math.Clamp(creaseAngleDeg, 0.1, 179.0) * Math.PI / 180.0);
        var creaseEdges = new List<(int A, int B, double Kappa, double Clearance)>();
        double creaseLength = 0.0;
        foreach ((long key, (int f0, int f1, int count)) in edgeFaces)
        {
            int a = (int)(key >> 32);
            int b = (int)(key & 0xFFFFFFFFL);
            if (count == 1)
            {
                held[a] = true;
                held[b] = true;
                HeldLines.AddMeshEdge(fade, vertices, a, b);
                continue;
            }

            if (count != 2 || wallFaces[f0] || wallFaces[f1])
                continue;
            if (!TryMeasureCrease(vertices, faces, a, b, f0, f1, cosThreshold, out double kappa, out double clearance))
                continue;
            if (!inside[a] || !inside[b])
                continue;
            if (onProtect[a] && onProtect[b] && protectLines.IsOn(
                    (vertices[a * 3] + vertices[b * 3]) * 0.5,
                    (vertices[a * 3 + 1] + vertices[b * 3 + 1]) * 0.5,
                    tol))
                continue;

            creaseEdges.Add((a, b, kappa, clearance));
            creaseLength += PlanDistance(vertices, a, b);
        }

        // A boundary scopes the rounding, so the offset fades to nothing at it like at any other held line.
        foreach ((double[] xy, int count) in boundaries)
        {
            for (int i = 0; i < count; i++)
            {
                int j = (i + 1) % count;
                fade.Add(xy[i * 2]);
                fade.Add(xy[i * 2 + 1]);
                fade.Add(xy[j * 2]);
                fade.Add(xy[j * 2 + 1]);
            }
        }

        fade.AddRange(protectLines.Segments);

        CreaseChain[] chains = BuildChains(vertices, creaseEdges);
        var segmentRefs = new List<(int, int)>();
        var segmentBounds = new List<Bounds2D>();
        for (int c = 0; c < chains.Length; c++)
        {
            double[] xy = chains[c].Xy;
            for (int s = 0; s < chains[c].Vertices.Length - 1; s++)
            {
                segmentRefs.Add((c, s));
                segmentBounds.Add(HeldLines.SegmentBounds(xy[s * 2], xy[s * 2 + 1], xy[s * 2 + 2], xy[s * 2 + 3]));
            }
        }

        return new PreparedCreaseRounding
        {
            VertexCount = vertexCount,
            CreaseEdgeCount = creaseEdges.Count,
            CreaseLength = creaseLength,
            Held = held,
            Chains = chains,
            CreaseSegments = segmentRefs.ToArray(),
            CreaseIndex = SpatialHashGrid2D.Build(segmentBounds.ToArray()),
            Fade = HeldLines.FromSegments(fade.ToArray())
        };
    }

    /// <summary>
    /// Rounds every crease of <paramref name="prepared"/> whose connected network is at least
    /// <paramref name="roundingLength"/> long (shorter folds are surface detail, not a crest or a toe).
    /// Returns new vertices; only Z changes.
    /// </summary>
    public static CreaseRoundingResult Round(double[] vertices, PreparedCreaseRounding prepared, double roundingLength)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (vertices.Length < prepared.VertexCount * 3)
            throw new ArgumentException("Prepared crease rounding does not match the vertex array.", nameof(vertices));

        var result = (double[])vertices.Clone();
        if (!(roundingLength > 0.0) || prepared.Chains.Length == 0)
            return new CreaseRoundingResult(result, 0, 0.0, 0.0, 0.0);

        double r = roundingLength * 0.5;
        bool[] chainActive = ActiveChains(prepared, roundingLength);
        double roundedLength = 0.0;
        double coarseLength = 0.0;
        for (int c = 0; c < chainActive.Length; c++)
        {
            CreaseChain chain = prepared.Chains[c];
            if (!chainActive[c])
                continue;

            roundedLength += chain.Length;
            for (int s = 0; s < chain.SegmentClearance.Length; s++)
            {
                if (chain.SegmentClearance[s] > r)
                    coarseLength += Math.Sqrt(Geometry2D.DistanceSquared(chain.Xy[s * 2], chain.Xy[s * 2 + 1], chain.Xy[s * 2 + 2], chain.Xy[s * 2 + 3]));
            }
        }

        if (roundedLength <= 0.0)
            return new CreaseRoundingResult(result, 0, 0.0, 0.0, 0.0);

        var offsets = new double[prepared.VertexCount];
        System.Threading.Tasks.Parallel.For(
            0,
            prepared.VertexCount,
            () => (Candidates: new List<int>(16), Scratch: new SpatialHashGrid2D.QueryScratch()),
            (v, _, local) =>
            {
                if (prepared.Held[v])
                    return local;

                double px = vertices[v * 3];
                double py = vertices[v * 3 + 1];
                prepared.CreaseIndex.GatherCandidates(Bounds2D.FromPoint(px, py, r), local.Candidates, local.Scratch);
                if (local.Candidates.Count == 0)
                    return local;

                double offset = 0.0;
                foreach (int candidate in local.Candidates)
                {
                    (int c, int seg) = prepared.CreaseSegments[candidate];
                    if (!chainActive[c])
                        continue;
                    CreaseChain chain = prepared.Chains[c];
                    offset += SegmentPull(
                        px, py,
                        chain.Xy[seg * 2], chain.Xy[seg * 2 + 1], chain.Kappa[seg],
                        chain.Xy[seg * 2 + 2], chain.Xy[seg * 2 + 3], chain.Kappa[seg + 1],
                        r);
                }

                if (offset != 0.0)
                    offsets[v] = offset * prepared.Fade.Weight(px, py, r, local.Candidates, local.Scratch);
                return local;
            },
            _ => { });

        int moved = 0;
        double maxOffset = 0.0;
        for (int v = 0; v < prepared.VertexCount; v++)
        {
            if (offsets[v] == 0.0)
                continue;
            result[v * 3 + 2] += offsets[v];
            moved++;
            maxOffset = Math.Max(maxOffset, Math.Abs(offsets[v]));
        }

        return new CreaseRoundingResult(result, moved, maxOffset, roundedLength, coarseLength);
    }

    /// <summary>
    /// Splits the triangles within half the rounding length of a crease that are too coarse to carry the
    /// curve — longer-edged than a quarter of the length — so a curve, not a chamfer, can be built into
    /// them. Midpoint splits only: the surface does not change. A wall face never asks for a split, though
    /// one sharing an edge that a band triangle splits is bisected with it to stay conforming. Returns null
    /// when nothing needed splitting; otherwise <see cref="Prepare"/> the returned mesh before rounding it.
    /// </summary>
    /// <param name="maxAddedVertices">Refuse (null, with <paramref name="overBudget"/> set) when the band
    /// could need more vertices than this: a short length over long creases asks for millions.</param>
    public static (IndexedTriMesh Mesh, int AddedVertices)? DensifyBand(
        IndexedTriMesh mesh,
        PreparedCreaseRounding prepared,
        double roundingLength,
        double tolerance,
        int maxAddedVertices,
        out bool overBudget)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        overBudget = false;
        if (!(roundingLength > 0.0) || prepared.Chains.Length == 0)
            return null;

        double r = roundingLength * 0.5;
        bool[] chainActive = ActiveChains(prepared, roundingLength);
        if (!chainActive.Any(static active => active))
            return null;

        // A band of width L along the creases at edge length L/4 holds about 18 vertices per L of crease.
        // That is an upper bound (part of the band is usually fine already), so refusing on it is safe.
        double activeLength = 0.0;
        for (int c = 0; c < chainActive.Length; c++)
        {
            if (chainActive[c])
                activeLength += prepared.Chains[c].Length;
        }

        if (activeLength / roundingLength * 18.5 > maxAddedVertices)
        {
            overBudget = true;
            return null;
        }

        double cosWall = Math.Cos(WallFaceMinSlopeDeg * Math.PI / 180.0);
        var candidates = new List<int>(16);
        var scratch = new SpatialHashGrid2D.QueryScratch();
        LocalMeshRefiner.Result result = LocalMeshRefiner.Refine(
            mesh.Vertices,
            mesh.Faces,
            Array.Empty<ConstraintPolyline>(),
            new LocalMeshRefiner.Options
            {
                TargetEdgeLength = r * 0.5,
                Tolerance = tolerance,
                DoFlips = false,
                TriangleFilter = triangle => !IsWallFace(triangle, cosWall) && IsNearActiveCrease(triangle, r, prepared, chainActive, candidates, scratch)
            });

        if (!result.Success || result.AddedVertices == 0)
            return null;

        return (new IndexedTriMesh(result.Vertices, result.Vertices.Length / 3, result.Faces, result.Faces.Length / 3), result.AddedVertices);
    }

    private static bool[] ActiveChains(PreparedCreaseRounding prepared, double roundingLength)
    {
        var active = new bool[prepared.Chains.Length];
        for (int c = 0; c < active.Length; c++)
            active[c] = prepared.Chains[c].ComponentLength >= roundingLength;
        return active;
    }

    private static bool IsWallFace(LocalMeshRefiner.RegionTriangle t, double cosWall)
    {
        double ux = t.Bx - t.Ax, uy = t.By - t.Ay, uz = t.Bz - t.Az;
        double vx = t.Cx - t.Ax, vy = t.Cy - t.Ay, vz = t.Cz - t.Az;
        double nx = (uy * vz) - (uz * vy);
        double ny = (uz * vx) - (ux * vz);
        double nz = (ux * vy) - (uy * vx);
        double length = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
        return length > 1e-18 && Math.Abs(nz) / length < cosWall;
    }

    /// <summary>Does the triangle come within <paramref name="r"/> of a crease that will be rounded, in plan?</summary>
    private static bool IsNearActiveCrease(
        LocalMeshRefiner.RegionTriangle t,
        double r,
        PreparedCreaseRounding prepared,
        bool[] chainActive,
        List<int> candidates,
        SpatialHashGrid2D.QueryScratch scratch)
    {
        var bounds = new Bounds2D(
            Math.Min(t.Ax, Math.Min(t.Bx, t.Cx)) - r,
            Math.Max(t.Ax, Math.Max(t.Bx, t.Cx)) + r,
            Math.Min(t.Ay, Math.Min(t.By, t.Cy)) - r,
            Math.Max(t.Ay, Math.Max(t.By, t.Cy)) + r);
        prepared.CreaseIndex.GatherCandidates(bounds, candidates, scratch);
        foreach (int candidate in candidates)
        {
            (int c, int seg) = prepared.CreaseSegments[candidate];
            if (!chainActive[c])
                continue;
            double[] xy = prepared.Chains[c].Xy;
            double sx = xy[seg * 2], sy = xy[seg * 2 + 1], ex = xy[seg * 2 + 2], ey = xy[seg * 2 + 3];
            if (Geometry2D.PointInTriangleInclusive(sx, sy, t.Ax, t.Ay, t.Bx, t.By, t.Cx, t.Cy) ||
                SegmentDistance(sx, sy, ex, ey, t.Ax, t.Ay, t.Bx, t.By) < r ||
                SegmentDistance(sx, sy, ex, ey, t.Bx, t.By, t.Cx, t.Cy) < r ||
                SegmentDistance(sx, sy, ex, ey, t.Cx, t.Cy, t.Ax, t.Ay) < r)
                return true;
        }

        return false;
    }

    /// <summary>Plan distance between segments a–b and c–d (zero where they cross).</summary>
    private static double SegmentDistance(double ax, double ay, double bx, double by, double cx, double cy, double dx, double dy)
    {
        if (Geometry2D.SegmentsTouch(ax, ay, bx, by, cx, cy, dx, dy))
            return 0.0;

        return Math.Min(
            Math.Min(PointSegmentDistance(ax, ay, cx, cy, dx, dy), PointSegmentDistance(bx, by, cx, cy, dx, dy)),
            Math.Min(PointSegmentDistance(cx, cy, ax, ay, bx, by), PointSegmentDistance(dx, dy, ax, ay, bx, by)));
    }

    private static double PointSegmentDistance(double px, double py, double ax, double ay, double bx, double by)
    {
        double t = Geometry2D.ParameterOnSegmentClamped(ax, ay, bx, by, px, py);
        return Math.Sqrt(Geometry2D.DistanceSquared(px, py, ax + ((bx - ax) * t), ay + ((by - ay) * t)));
    }

    private static readonly double[] GaussNodes =
    {
        -0.9815606342467192, -0.9041172563704749, -0.7699026741943047, -0.5873179542866175,
        -0.3678314989981802, -0.1252334085114689, 0.1252334085114689, 0.3678314989981802,
        0.5873179542866175, 0.7699026741943047, 0.9041172563704749, 0.9815606342467192,
    };

    private static readonly double[] GaussWeights =
    {
        0.0471753363865118, 0.1069393259953184, 0.1600783285433462, 0.2031674267230659,
        0.2334925365383548, 0.2491470458134028, 0.2491470458134028, 0.2334925365383548,
        0.2031674267230659, 0.1600783285433462, 0.1069393259953184, 0.0471753363865118,
    };

    /// <summary>
    /// The offset one crease segment asks of the point (px, py): κ integrated along the segment against
    /// <see cref="Kernel"/>. κ is linear along the segment between its end values.
    /// </summary>
    private static double SegmentPull(double px, double py, double ax, double ay, double ka, double bx, double by, double kb, double r)
    {
        double dx = bx - ax, dy = by - ay;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length <= 1e-12)
            return 0.0;

        double ux = dx / length, uy = dy / length;

        // Along-segment coordinate of the foot of the perpendicular, and the distance to the line.
        double foot = ((px - ax) * ux) + ((py - ay) * uy);
        double d = Math.Abs(((px - ax) * uy) - ((py - ay) * ux));
        if (d >= r)
            return 0.0;

        // Only the part of the segment inside the disc of radius r around the point pulls.
        double half = Math.Sqrt((r * r) - (d * d));
        double lo = Math.Max(0.0, foot - half);
        double hi = Math.Min(length, foot + half);
        if (hi <= lo)
            return 0.0;

        // Integrate outward from the foot on each side with t = foot ± span·w², which removes the
        // logarithmic peak of the kernel at the foot when the point lies on the crease.
        double sum = 0.0;
        if (foot > lo)
            sum += Side(Math.Min(foot, hi), -1.0, Math.Min(foot, hi) - lo);
        if (foot < hi)
            sum += Side(Math.Max(foot, lo), 1.0, hi - Math.Max(foot, lo));
        return sum;

        double Side(double from, double direction, double span)
        {
            if (span <= 0.0)
                return 0.0;

            // The near end may not be the foot (the foot lies off the segment); then measure from it.
            double total = 0.0;
            for (int i = 0; i < GaussNodes.Length; i++)
            {
                double w = 0.5 * (GaussNodes[i] + 1.0);
                double t = from + (direction * span * w * w);
                double along = t - foot;
                double rho = Math.Sqrt((d * d) + (along * along));
                double kappa = Geometry2D.Lerp(ka, kb, Math.Clamp(t / length, 0.0, 1.0));
                total += GaussWeights[i] * 0.5 * kappa * Kernel(rho, r) * 2.0 * span * w;
            }

            return total;
        }
    }

    /// <summary>
    /// How strongly a unit length of crease with unit grade change pulls a point ρ away. It is the radial
    /// solution of ∇²Φ = G − δ for the 2D kernel G whose every straight-line projection is the 1D box of
    /// width 2r: so a straight crease of any length beyond r gets exactly the box-blur parabola
    /// κ(r − d)²/4r, while corners, coned batter ends and a crease meeting another add up as one blur —
    /// no crease is counted twice, however the mesh splits it into edges. Zero at and beyond r.
    /// </summary>
    private static double Kernel(double rho, double r)
    {
        if (rho >= r)
            return 0.0;
        rho = Math.Max(rho, r * 1e-12);
        double root = Math.Sqrt((r * r) - (rho * rho));
        return ((r * Math.Log((r + root) / rho)) - root) / (2.0 * Math.PI * r);
    }

    /// <summary>
    /// The edge (a, b) shared by faces f0 and f1 is a crease when they fold at least the threshold; its
    /// grade change is measured across the edge in plan, positive where the ground turns upward.
    /// </summary>
    private static bool TryMeasureCrease(
        double[] vertices, int[] faces, int a, int b, int f0, int f1, double cosThreshold,
        out double kappa, out double clearance)
    {
        kappa = 0.0;
        clearance = 0.0;
        if (!TryFacePlane(vertices, faces, f0, out double n0x, out double n0y, out double n0z, out double g0x, out double g0y) ||
            !TryFacePlane(vertices, faces, f1, out double n1x, out double n1y, out double n1z, out double g1x, out double g1y))
            return false;

        double cos = (n0x * n1x) + (n0y * n1y) + (n0z * n1z);
        if (cos > cosThreshold)
            return false;

        double ax = vertices[a * 3], ay = vertices[a * 3 + 1];
        double dx = vertices[b * 3] - ax, dy = vertices[b * 3 + 1] - ay;
        double len = Math.Sqrt((dx * dx) + (dy * dy));
        if (len <= 1e-12)
            return false;
        double nx = -dy / len, ny = dx / len;

        int o0 = OppositeVertex(faces, f0, a, b);
        int o1 = OppositeVertex(faces, f1, a, b);
        double s0 = ((vertices[o0 * 3] - ax) * nx) + ((vertices[o0 * 3 + 1] - ay) * ny);
        double s1 = ((vertices[o1 * 3] - ax) * nx) + ((vertices[o1 * 3 + 1] - ay) * ny);

        // Both faces on one side of the edge is a fold-over, not a crease a 2.5D terrain can round.
        if (s0 * s1 >= 0.0)
            return false;

        (double gPlusX, double gPlusY, double gMinusX, double gMinusY) = s1 > 0.0
            ? (g1x, g1y, g0x, g0y)
            : (g0x, g0y, g1x, g1y);
        kappa = ((gPlusX - gMinusX) * nx) + ((gPlusY - gMinusY) * ny);
        clearance = Math.Min(Math.Abs(s0), Math.Abs(s1));
        return true;
    }

    private static bool TryFacePlane(
        double[] vertices, int[] faces, int f,
        out double nx, out double ny, out double nz, out double gx, out double gy)
    {
        int a = faces[f * 3], b = faces[f * 3 + 1], c = faces[f * 3 + 2];
        double ux = vertices[b * 3] - vertices[a * 3], uy = vertices[b * 3 + 1] - vertices[a * 3 + 1], uz = vertices[b * 3 + 2] - vertices[a * 3 + 2];
        double vx = vertices[c * 3] - vertices[a * 3], vy = vertices[c * 3 + 1] - vertices[a * 3 + 1], vz = vertices[c * 3 + 2] - vertices[a * 3 + 2];
        nx = (uy * vz) - (uz * vy);
        ny = (uz * vx) - (ux * vz);
        nz = (ux * vy) - (uy * vx);
        double len = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
        gx = gy = 0.0;
        if (len <= 1e-18 || Math.Abs(nz) <= 1e-12 * len)
            return false;

        nx /= len;
        ny /= len;
        nz /= len;

        // z = gx·x + gy·y + c on the face's plane.
        gx = -nx / nz;
        gy = -ny / nz;
        return true;
    }

    private static int OppositeVertex(int[] faces, int f, int a, int b)
    {
        for (int k = 0; k < 3; k++)
        {
            int v = faces[f * 3 + k];
            if (v != a && v != b)
                return v;
        }

        return a;
    }

    /// <summary>
    /// Walks crease edges into chains, broken where more or fewer than two creases meet, and measures
    /// each connected crease network's plan length.
    /// </summary>
    private static CreaseChain[] BuildChains(double[] vertices, List<(int A, int B, double Kappa, double Clearance)> edges)
    {
        if (edges.Count == 0)
            return Array.Empty<CreaseChain>();

        var adjacency = new Dictionary<int, List<int>>();
        for (int e = 0; e < edges.Count; e++)
        {
            Add(edges[e].A, e);
            Add(edges[e].B, e);
        }

        // Connected networks, by union-find over vertices.
        var parent = new Dictionary<int, int>();
        foreach (int v in adjacency.Keys)
            parent[v] = v;
        foreach ((int a, int b, _, _) in edges)
            parent[Find(a)] = Find(b);
        var componentLength = new Dictionary<int, double>();
        foreach ((int a, int b, _, _) in edges)
        {
            int root = Find(a);
            componentLength[root] = componentLength.GetValueOrDefault(root) + PlanDistance(vertices, a, b);
        }

        var used = new bool[edges.Count];
        var chains = new List<CreaseChain>();

        // Open chains start at every vertex where the chain cannot continue unambiguously.
        foreach ((int v, List<int> incident) in adjacency)
        {
            if (incident.Count == 2)
                continue;
            foreach (int e in incident)
            {
                if (!used[e])
                    chains.Add(Walk(v, e));
            }
        }

        // What is left are closed loops (a pad's crest all the way round).
        for (int e = 0; e < edges.Count; e++)
        {
            if (!used[e])
                chains.Add(Walk(edges[e].A, e));
        }

        return chains.ToArray();

        CreaseChain Walk(int start, int firstEdge)
        {
            var points = new List<int> { start };
            var segmentKappa = new List<double>();
            var segmentClearance = new List<double>();
            int current = start;
            int edge = firstEdge;
            while (true)
            {
                used[edge] = true;
                (int a, int b, double kappa, double clearance) = edges[edge];
                int next = a == current ? b : a;
                segmentKappa.Add(kappa);
                segmentClearance.Add(clearance);
                points.Add(next);
                current = next;
                if (current == start || adjacency[current].Count != 2)
                    break;
                int following = adjacency[current][0] == edge ? adjacency[current][1] : adjacency[current][0];
                if (used[following])
                    break;
                edge = following;
            }

            bool closed = points.Count > 2 && points[^1] == points[0];
            var xy = new double[points.Count * 2];
            var segLength = new double[points.Count - 1];
            double length = 0.0;
            for (int i = 0; i < points.Count; i++)
            {
                xy[i * 2] = vertices[points[i] * 3];
                xy[i * 2 + 1] = vertices[points[i] * 3 + 1];
                if (i > 0)
                {
                    segLength[i - 1] = PlanDistance(vertices, points[i - 1], points[i]);
                    length += segLength[i - 1];
                }
            }

            // κ per point: the length-weighted mean of its segments, so the offset is continuous along the
            // chain. A closed chain's seam point averages the first and last segments.
            var kappaAt = new double[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                double sum = 0.0, weight = 0.0;
                if (i > 0)
                {
                    sum += segmentKappa[i - 1] * segLength[i - 1];
                    weight += segLength[i - 1];
                }
                if (i < segmentKappa.Count)
                {
                    sum += segmentKappa[i] * segLength[i];
                    weight += segLength[i];
                }
                if (closed && (i == 0 || i == points.Count - 1))
                {
                    int other = i == 0 ? segmentKappa.Count - 1 : 0;
                    sum += segmentKappa[other] * segLength[other];
                    weight += segLength[other];
                }

                kappaAt[i] = weight > 0.0 ? sum / weight : segmentKappa[Math.Min(i, segmentKappa.Count - 1)];
            }

            return new CreaseChain
            {
                Vertices = points.ToArray(),
                Xy = xy,
                Kappa = kappaAt,
                ComponentLength = componentLength[Find(start)],
                Length = length,
                SegmentClearance = segmentClearance.ToArray()
            };
        }

        void Add(int v, int e)
        {
            if (!adjacency.TryGetValue(v, out List<int>? list))
                adjacency[v] = list = new List<int>(2);
            list.Add(e);
        }

        int Find(int v)
        {
            while (parent[v] != v)
            {
                parent[v] = parent[parent[v]];
                v = parent[v];
            }

            return v;
        }
    }

    private static bool[] BuildProtectMask(double[] vertices, int vertexCount, HeldLines protect, double tolerance)
    {
        var mask = new bool[vertexCount];
        if (protect.IsEmpty)
            return mask;

        var candidates = new List<int>(4);
        for (int v = 0; v < vertexCount; v++)
            mask[v] = protect.IsOn(vertices[v * 3], vertices[v * 3 + 1], tolerance, candidates);

        return mask;
    }

    private static double PlanDistance(double[] vertices, int a, int b) =>
        Math.Sqrt(Geometry2D.DistanceSquared(vertices[a * 3], vertices[a * 3 + 1], vertices[b * 3], vertices[b * 3 + 1]));
}
