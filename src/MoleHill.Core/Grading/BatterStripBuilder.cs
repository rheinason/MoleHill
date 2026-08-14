using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

/// <summary>
/// Shared core for explicit batter (side-slope) construction used by both Grade Pad and
/// Grade Path. Phase 1 covers the daylight loop: for each footprint station it marches a
/// ray outward at the target slope until it meets existing ground, producing a daylight
/// point with the exact terrain elevation at that location. Later phases build the ruled
/// batter strip mesh between the footprint loop and this daylight loop.
/// </summary>
internal static class BatterStripBuilder
{
    /// <summary>Outcome of the per-station daylight ray-march.</summary>
    internal enum DaylightStatus
    {
        /// <summary>The slope ray met existing ground; <see cref="DaylightStation.DayZ"/> sits on terrain.</summary>
        Daylighted,

        /// <summary>Ground was never met within the pad's max influence distance; clamped to that distance.</summary>
        ClampedToMaxDistance,

        /// <summary>A lock-curve barrier interrupted the ray before it could daylight.</summary>
        ClampedToBarrier,

        /// <summary>No max distance was set and the ray never met ground within the terrain extent.</summary>
        NonDaylighting,

        /// <summary>Footprint and terrain elevations coincide here, so there is no batter (day == foot).</summary>
        Flat
    }

    /// <summary>
    /// One station around the footprint: the footprint point (at pad/edge elevation) and the
    /// daylight point it projects to (XY plus terrain elevation), with the horizontal reach
    /// between them and how the daylight point was resolved.
    /// </summary>
    internal readonly record struct DaylightStation(
        double FootX,
        double FootY,
        double FootZ,
        double DayX,
        double DayY,
        double DayZ,
        double Reach,
        DaylightStatus Status)
    {
        /// <summary>
        /// When <see cref="Status"/> is <see cref="DaylightStatus.ClampedToBarrier"/>, the index (into
        /// the prepared barriers) of the barrier segment the ray clipped against; -1 otherwise. Lets the
        /// daylight loop be collapsed back onto the barrier's own (terrain) vertices along clamped runs.
        /// </summary>
        public int BarrierSegmentIndex { get; init; } = -1;
    }

    /// <summary>An ordered ring (or open chain) of daylight stations around one footprint.</summary>
    internal sealed class DaylightLoop
    {
        public required DaylightStation[] Stations { get; init; }

        public required bool IsClosed { get; init; }

        public int Count => Stations.Length;

        /// <summary>True when at least one station produced a real batter (non-flat reach).</summary>
        public bool HasBatter
        {
            get
            {
                foreach (DaylightStation station in Stations)
                {
                    if (station.Status != DaylightStatus.Flat && station.Reach > 1e-9)
                        return true;
                }

                return false;
            }
        }

        public double[] FootprintXy()
        {
            var xy = new double[Stations.Length * 2];
            for (int i = 0; i < Stations.Length; i++)
            {
                xy[i * 2] = Stations[i].FootX;
                xy[i * 2 + 1] = Stations[i].FootY;
            }

            return xy;
        }

        public double[] DaylightXy()
        {
            var xy = new double[Stations.Length * 2];
            for (int i = 0; i < Stations.Length; i++)
            {
                xy[i * 2] = Stations[i].DayX;
                xy[i * 2 + 1] = Stations[i].DayY;
            }

            return xy;
        }

        public double[] DaylightXyz()
        {
            var xyz = new double[Stations.Length * 3];
            for (int i = 0; i < Stations.Length; i++)
            {
                xyz[i * 3] = Stations[i].DayX;
                xyz[i * 3 + 1] = Stations[i].DayY;
                xyz[i * 3 + 2] = Stations[i].DayZ;
            }

            return xyz;
        }
    }

    /// <summary>
    /// Marches a daylight ray outward from every footprint station and assembles the daylight
    /// loop. <paramref name="outwardNormals"/> is a flat [nx0,ny0,nx1,ny1,...] array, one unit
    /// outward normal per footprint vertex (see <see cref="ComputeClosedLoopOutwardNormals"/>
    /// for closed pad footprints). <paramref name="footprintZ"/> evaluates the graded surface
    /// elevation at the footprint (e.g. <c>PadBoundary.EvaluateZ</c>).
    /// </summary>
    /// <summary>Planar/general footprint elevation: <paramref name="footprintZ"/> evaluates Z at (x,y).</summary>
    internal static DaylightLoop BuildDaylightLoop(
        double[] footprintXy,
        int footprintCount,
        bool isClosed,
        double[] outwardNormals,
        Func<double, double, double> footprintZ,
        double cutSlopeAngleDeg,
        double fillSlopeAngleDeg,
        double maxDistance,
        TerrainFaceGrid terrain,
        PreparedBarriers barriers,
        double tolerance)
    {
        if (footprintZ is null) throw new ArgumentNullException(nameof(footprintZ));
        return BuildDaylightLoopCore(
            footprintXy, footprintCount, isClosed, outwardNormals,
            (_, x, y) => footprintZ(x, y),
            cutSlopeAngleDeg, fillSlopeAngleDeg, maxDistance, terrain, barriers, tolerance);
    }

    /// <summary>
    /// Per-station footprint elevation, for non-planar footprints such as a road corridor outline
    /// that follows the centerline profile. <paramref name="footprintZByStation"/> is aligned to the
    /// station list (one Z per footprint vertex/fan station).
    /// </summary>
    internal static DaylightLoop BuildDaylightLoop(
        double[] footprintXy,
        int footprintCount,
        bool isClosed,
        double[] outwardNormals,
        double[] footprintZByStation,
        double cutSlopeAngleDeg,
        double fillSlopeAngleDeg,
        double maxDistance,
        TerrainFaceGrid terrain,
        PreparedBarriers barriers,
        double tolerance)
    {
        if (footprintZByStation is null) throw new ArgumentNullException(nameof(footprintZByStation));
        if (footprintZByStation.Length < footprintCount)
            throw new ArgumentException("Per-station Z array is shorter than the station count.", nameof(footprintZByStation));
        return BuildDaylightLoopCore(
            footprintXy, footprintCount, isClosed, outwardNormals,
            (i, _, _) => footprintZByStation[i],
            cutSlopeAngleDeg, fillSlopeAngleDeg, maxDistance, terrain, barriers, tolerance);
    }

    private static DaylightLoop BuildDaylightLoopCore(
        double[] footprintXy,
        int footprintCount,
        bool isClosed,
        double[] outwardNormals,
        Func<int, double, double, double> footprintZ,
        double cutSlopeAngleDeg,
        double fillSlopeAngleDeg,
        double maxDistance,
        TerrainFaceGrid terrain,
        PreparedBarriers barriers,
        double tolerance)
    {
        if (footprintXy is null) throw new ArgumentNullException(nameof(footprintXy));
        if (outwardNormals is null) throw new ArgumentNullException(nameof(outwardNormals));
        if (terrain is null) throw new ArgumentNullException(nameof(terrain));
        if (footprintCount < 2)
            throw new ArgumentOutOfRangeException(nameof(footprintCount), "A footprint needs at least two stations.");
        if (footprintXy.Length < footprintCount * 2)
            throw new ArgumentException("Footprint vertex array is shorter than the vertex count.", nameof(footprintXy));
        if (outwardNormals.Length < footprintCount * 2)
            throw new ArgumentException("Outward-normal array is shorter than the vertex count.", nameof(outwardNormals));

        double cutSlopeRatio = Math.Tan(Math.Clamp(cutSlopeAngleDeg, 0.1, 89.9) * Math.PI / 180.0);
        double fillSlopeRatio = Math.Tan(Math.Clamp(fillSlopeAngleDeg, 0.1, 89.9) * Math.PI / 180.0);
        double zTolerance = GradingTolerances.VertexAdjustmentZTolerance(tolerance);
        double searchDistance = maxDistance > 0.0
            ? maxDistance
            : terrain.BoundsDiagonal;

        var barrierScratch = new SpatialHashGrid2D.QueryScratch(Math.Max(barriers.Segments.Length, 1));
        var barrierCandidates = new List<int>(8);

        var stations = new DaylightStation[footprintCount];
        for (int i = 0; i < footprintCount; i++)
        {
            double fx = footprintXy[i * 2];
            double fy = footprintXy[i * 2 + 1];
            double fz = footprintZ(i, fx, fy);
            double nx = outwardNormals[i * 2];
            double ny = outwardNormals[i * 2 + 1];

            stations[i] = BuildStation(
                fx,
                fy,
                fz,
                nx,
                ny,
                cutSlopeRatio,
                fillSlopeRatio,
                searchDistance,
                maxDistance,
                zTolerance,
                terrain,
                barriers,
                barrierScratch,
                barrierCandidates);
        }

        RegularizeDaylightSpikes(stations, isClosed, terrain);
        return new DaylightLoop { Stations = stations, IsClosed = isClosed };
    }

    /// <summary>
    /// Tames the grazing-daylight instability. Where a cut batter daylights into rising terrain at a near
    /// tangent angle, the zero-crossing of (grade − terrain) is ill-conditioned, so one station's ray
    /// reaches far out while its neighbours stop short — a jagged daylight line that becomes sliver
    /// "spikes" downstream. A one-pass median filter on the per-station reach clamps DOWN only the clear
    /// outliers (a reach well above both neighbours): a smooth ramp keeps its median (unchanged), a single
    /// spike collapses to its larger neighbour. Only daylighted stations are touched; the clamped point is
    /// re-read on terrain so it still meets ground. Conservative by design — it never lengthens a reach, so
    /// it cannot push a daylight point past where the batter actually meets the ground.
    /// </summary>
    internal static void RegularizeDaylightSpikes(DaylightStation[] stations, bool isClosed, TerrainFaceGrid terrain)
    {
        int n = stations.Length;
        if (n < 3)
            return;

        const double spikeFactor = 1.5; // only clamp a reach more than 50% above its larger neighbour
        var updated = (DaylightStation[])stations.Clone();
        bool any = false;

        int lo = isClosed ? 0 : 1;
        int hi = isClosed ? n : n - 1;
        for (int i = lo; i < hi; i++)
        {
            DaylightStation s = stations[i];
            if (s.Status != DaylightStatus.Daylighted || s.Reach <= 1e-9)
                continue;

            int p = (i - 1 + n) % n;
            int q = (i + 1) % n;
            double median = Median3(stations[p].Reach, s.Reach, stations[q].Reach);
            if (s.Reach <= median * spikeFactor)
                continue;

            double dirX = s.DayX - s.FootX;
            double dirY = s.DayY - s.FootY;
            double len = Math.Sqrt((dirX * dirX) + (dirY * dirY));
            if (len <= 1e-12)
                continue;

            dirX /= len;
            dirY /= len;
            double dayX = s.FootX + (dirX * median);
            double dayY = s.FootY + (dirY * median);
            double dayZ = terrain.InterpolateZ(dayX, dayY);
            updated[i] = new DaylightStation(s.FootX, s.FootY, s.FootZ, dayX, dayY, dayZ, median, s.Status)
            {
                BarrierSegmentIndex = s.BarrierSegmentIndex
            };
            any = true;
        }

        if (any)
            Array.Copy(updated, stations, n);
    }

    private static double Median3(double a, double b, double c) =>
        Math.Max(Math.Min(a, b), Math.Min(Math.Max(a, b), c));

    private static DaylightStation BuildStation(
        double fx,
        double fy,
        double fz,
        double nx,
        double ny,
        double cutSlopeRatio,
        double fillSlopeRatio,
        double searchDistance,
        double maxDistance,
        double zTolerance,
        TerrainFaceGrid terrain,
        PreparedBarriers barriers,
        SpatialHashGrid2D.QueryScratch barrierScratch,
        List<int> barrierCandidates)
    {
        double normalLengthSq = (nx * nx) + (ny * ny);
        double terrainZatFoot = terrain.InterpolateZ(fx, fy);
        double diff = terrainZatFoot - fz;
        double branchSign = Math.Sign(diff);

        // Cut (terrain above grade) and fill (terrain below grade) can use different batter slopes.
        double slopeRatio = branchSign < 0.0 ? fillSlopeRatio : cutSlopeRatio;

        // No elevation difference, no usable slope, or no usable outward direction: there is
        // nothing to batter here. The daylight point collapses onto the footprint.
        if (Math.Abs(diff) <= zTolerance || slopeRatio <= 1e-12 || normalLengthSq <= 1e-18)
            return Flat(fx, fy, fz);

        bool found = terrain.TryFindRayDaylightReach(
            fx,
            fy,
            fz,
            nx,
            ny,
            slopeRatio,
            branchSign,
            searchDistance,
            out double reach,
            out double bestApproachReach);

        DaylightStatus status;
        if (found)
        {
            status = DaylightStatus.Daylighted;
        }
        else if (maxDistance > 0.0)
        {
            reach = maxDistance;
            status = DaylightStatus.ClampedToMaxDistance;
        }
        else
        {
            reach = bestApproachReach > 1e-9 ? bestApproachReach : searchDistance;
            status = DaylightStatus.NonDaylighting;
        }

        if (reach <= 1e-9)
            return Flat(fx, fy, fz);

        double dayX = fx + (nx * reach);
        double dayY = fy + (ny * reach);

        int barrierSegmentIndex = -1;
        if (barriers.Segments.Length > 0 &&
            GradingBarriers.TryClipSegment(
                barriers,
                fx,
                fy,
                dayX,
                dayY,
                barrierScratch,
                barrierCandidates,
                out double clippedX,
                out double clippedY,
                out barrierSegmentIndex))
        {
            dayX = clippedX;
            dayY = clippedY;
            reach = Math.Sqrt(((dayX - fx) * (dayX - fx)) + ((dayY - fy) * (dayY - fy)));
            status = DaylightStatus.ClampedToBarrier;
            if (reach <= 1e-9)
                return Flat(fx, fy, fz);
        }

        // On a true daylight hit the slope plane meets terrain exactly, so reading the terrain
        // gives a seam that matches the surrounding mesh. When the ray was clamped (max distance
        // or barrier) the batter stops short of ground, so stay on the slope plane instead.
        double dayZ = status == DaylightStatus.Daylighted
            ? terrain.InterpolateZ(dayX, dayY)
            : fz + (branchSign * slopeRatio * reach);

        return new DaylightStation(fx, fy, fz, dayX, dayY, dayZ, reach, status)
        {
            BarrierSegmentIndex = status == DaylightStatus.ClampedToBarrier ? barrierSegmentIndex : -1
        };
    }

    private static DaylightStation Flat(double fx, double fy, double fz) =>
        new(fx, fy, fz, fx, fy, fz, 0.0, DaylightStatus.Flat);

    /// <summary>
    /// Builds the carve-loop XY for the daylight loop, collapsing the over-tessellation a barrier-clamped
    /// batter run introduces. When a contiguous run of stations all clamp to the same lock-curve barrier
    /// (a retaining wall), each footprint station contributes a daylight point on that barrier — far more
    /// points than the barrier breakline itself has. The kept terrain below the wall is then fanned from
    /// this dense ring down to the sparse wall-toe vertices, producing thin near-vertical slivers
    /// ("spikes") along the wall. Along each clamped run this emits only the run endpoints (where it
    /// transitions to a terrain-following daylighted station) and the barrier's own vertices at each
    /// segment change — the lock-curve vertices, which are existing terrain vertices — so the carve loop
    /// rides the wall breakline exactly and the splitter leaves the wall face intact instead of
    /// re-tessellating it. Daylighted, max-distance and flat stations follow terrain and keep their
    /// density. Returns the plain <see cref="DaylightLoop.DaylightXy"/> when nothing was clamped or the
    /// collapse would drop below a valid polygon.
    /// </summary>
    internal static double[] BuildDecimatedCarveXy(DaylightLoop loop, PreparedBarriers barriers, double tolerance)
    {
        DaylightStation[] stations = loop.Stations;
        int n = stations.Length;
        if (n < 4 || barriers.Segments.Length == 0)
            return loop.DaylightXy();

        bool anyClamped = false;
        for (int i = 0; i < n; i++)
        {
            if (stations[i].Status == DaylightStatus.ClampedToBarrier)
            {
                anyClamped = true;
                break;
            }
        }

        if (!anyClamped)
            return loop.DaylightXy();

        double eps = Math.Max(tolerance, 1e-9);
        var result = new List<double>(n * 2);

        void Emit(double x, double y)
        {
            if (result.Count >= 2)
            {
                double lx = result[^2];
                double ly = result[^1];
                if (((x - lx) * (x - lx)) + ((y - ly) * (y - ly)) <= eps * eps)
                    return;
            }

            result.Add(x);
            result.Add(y);
        }

        for (int i = 0; i < n; i++)
        {
            DaylightStation s = stations[i];
            if (s.Status != DaylightStatus.ClampedToBarrier)
            {
                Emit(s.DayX, s.DayY);
                continue;
            }

            int prev = ((i - 1) % n + n) % n;
            int next = (i + 1) % n;
            DaylightStation p = stations[prev];

            // Within a clamped run, insert the barrier vertex shared by the two segments at a
            // segment change — that vertex is a lock-curve (terrain) vertex, so the carve aligns
            // to the wall breakline there.
            if (p.Status == DaylightStatus.ClampedToBarrier &&
                s.BarrierSegmentIndex >= 0 &&
                p.BarrierSegmentIndex >= 0 &&
                s.BarrierSegmentIndex != p.BarrierSegmentIndex &&
                TrySharedBarrierVertex(barriers, p.BarrierSegmentIndex, s.BarrierSegmentIndex, eps, out double vx, out double vy))
            {
                Emit(vx, vy);
            }

            // Run endpoints (a neighbour daylights) carry the transition between the wall and the
            // terrain-following batter, so they are kept; interior same-segment points are dropped.
            bool isEntry = p.Status != DaylightStatus.ClampedToBarrier;
            bool isExit = stations[next].Status != DaylightStatus.ClampedToBarrier;
            if (isEntry || isExit)
                Emit(s.DayX, s.DayY);
        }

        // Drop a duplicate closing point the wrap-around may have produced.
        if (result.Count >= 4)
        {
            double fx = result[0], fy = result[1];
            double lx = result[^2], ly = result[^1];
            if (((fx - lx) * (fx - lx)) + ((fy - ly) * (fy - ly)) <= eps * eps)
            {
                result.RemoveAt(result.Count - 1);
                result.RemoveAt(result.Count - 1);
            }
        }

        if (result.Count / 2 < 3)
            return loop.DaylightXy();

        return result.ToArray();
    }

    /// <summary>Returns the endpoint shared by two barrier segments (adjacent on a polyline), if any.</summary>
    private static bool TrySharedBarrierVertex(
        PreparedBarriers barriers, int indexA, int indexB, double eps, out double vx, out double vy)
    {
        BarrierSegment a = barriers.Segments[indexA];
        BarrierSegment b = barriers.Segments[indexB];
        double epsSq = eps * eps;

        (double x, double y)[] endpointsA = { (a.Ax, a.Ay), (a.Bx, a.By) };
        (double x, double y)[] endpointsB = { (b.Ax, b.Ay), (b.Bx, b.By) };
        foreach ((double x, double y) ea in endpointsA)
        {
            foreach ((double x, double y) eb in endpointsB)
            {
                double dx = ea.x - eb.x;
                double dy = ea.y - eb.y;
                if ((dx * dx) + (dy * dy) <= epsSq)
                {
                    vx = ea.x;
                    vy = ea.y;
                    return true;
                }
            }
        }

        vx = 0.0;
        vy = 0.0;
        return false;
    }

    /// <summary>
    /// Computes one outward unit normal per vertex of a closed footprint loop, using the
    /// angle bisector of the two adjacent edges. Orientation (CW/CCW) is detected from the
    /// signed area so the normals always point away from the loop interior.
    /// </summary>
    internal static double[] ComputeClosedLoopOutwardNormals(double[] xy, int count)
    {
        if (xy is null) throw new ArgumentNullException(nameof(xy));
        if (count < 3) throw new ArgumentOutOfRangeException(nameof(count), "A closed loop needs at least three vertices.");

        double signedArea = 0.0;
        for (int i = 0; i < count; i++)
        {
            int next = (i + 1) % count;
            signedArea += (xy[i * 2] * xy[next * 2 + 1]) - (xy[next * 2] * xy[i * 2 + 1]);
        }

        bool ccw = signedArea > 0.0;
        var normals = new double[count * 2];
        for (int i = 0; i < count; i++)
        {
            int prev = (i + count - 1) % count;
            int next = (i + 1) % count;

            EdgeOutwardNormal(xy, prev, i, ccw, out double n0x, out double n0y);
            EdgeOutwardNormal(xy, i, next, ccw, out double n1x, out double n1y);

            double bx = n0x + n1x;
            double by = n0y + n1y;
            double length = Math.Sqrt((bx * bx) + (by * by));
            if (length <= 1e-12)
            {
                // Near-180° spike: adjacent edge normals cancel. Fall back to the outgoing edge
                // normal, which is still a valid outward direction.
                bx = n1x;
                by = n1y;
                length = Math.Sqrt((bx * bx) + (by * by));
                if (length <= 1e-12)
                {
                    normals[i * 2] = 0.0;
                    normals[i * 2 + 1] = 0.0;
                    continue;
                }
            }

            normals[i * 2] = bx / length;
            normals[i * 2 + 1] = by / length;
        }

        return normals;
    }

    private static void EdgeOutwardNormal(double[] xy, int a, int b, bool ccw, out double nx, out double ny)
    {
        double dx = xy[b * 2] - xy[a * 2];
        double dy = xy[b * 2 + 1] - xy[a * 2 + 1];
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length <= 1e-12)
        {
            nx = 0.0;
            ny = 0.0;
            return;
        }

        // For a CCW loop the outward normal of edge a→b is (dy, -dx); for CW it is (-dy, dx).
        nx = ccw ? dy / length : -dy / length;
        ny = ccw ? -dx / length : dx / length;
    }

    /// <summary>
    /// A footprint resampled into ray-march stations: each station is a footprint point paired
    /// with one outward direction. Convex corners are expanded into a fan of stations sharing
    /// the same point but sweeping the corner arc, so the batter daylights as a clean cone there
    /// (instead of a single skewed diagonal facet).
    /// </summary>
    internal readonly record struct FootprintStations(double[] Xy, double[] Normals, int Count, double[] Z);

    private const double CornerFanMinSweepRad = 10.0 * Math.PI / 180.0;

    /// <summary>
    /// Builds ray-march stations for a closed footprint loop, inserting corner fans at convex
    /// vertices when <paramref name="cornerFanSegments"/> &gt;= 1. Concave (reentrant) vertices
    /// and shallow corners get a single bisector station.
    /// </summary>
    internal static FootprintStations BuildClosedFootprintStations(double[] xy, int count, int cornerFanSegments) =>
        BuildClosedFootprintStations(xy, count, vertexZ: null, cornerFanSegments);

    /// <summary>
    /// As above, but carries a per-vertex elevation through to each emitted station (corner-fan
    /// stations inherit their corner's Z). Use for non-planar footprints such as a road corridor
    /// outline that follows the centerline profile.
    /// </summary>
    internal static FootprintStations BuildClosedFootprintStations(double[] xy, int count, double[]? vertexZ, int cornerFanSegments)
    {
        if (xy is null) throw new ArgumentNullException(nameof(xy));
        if (count < 3) throw new ArgumentOutOfRangeException(nameof(count), "A closed loop needs at least three vertices.");

        double signedArea = 0.0;
        for (int i = 0; i < count; i++)
        {
            int next = (i + 1) % count;
            signedArea += (xy[i * 2] * xy[next * 2 + 1]) - (xy[next * 2] * xy[i * 2 + 1]);
        }

        bool ccw = signedArea > 0.0;
        var stationXy = new List<double>(count * 2);
        var normals = new List<double>(count * 2);
        List<double>? zList = vertexZ is null ? null : new List<double>(count);

        for (int i = 0; i < count; i++)
        {
            int prev = (i + count - 1) % count;
            int next = (i + 1) % count;
            double x = xy[i * 2];
            double y = xy[i * 2 + 1];

            EdgeOutwardNormal(xy, prev, i, ccw, out double n0x, out double n0y);
            EdgeOutwardNormal(xy, i, next, ccw, out double n1x, out double n1y);

            double dx0 = xy[i * 2] - xy[prev * 2];
            double dy0 = xy[i * 2 + 1] - xy[prev * 2 + 1];
            double dx1 = xy[next * 2] - xy[i * 2];
            double dy1 = xy[next * 2 + 1] - xy[i * 2 + 1];
            double turnCross = (dx0 * dy1) - (dy0 * dx1);
            bool isReentrant = ccw ? turnCross < -1e-12 : turnCross > 1e-12;

            bool emittedFan = false;
            if (cornerFanSegments >= 1 && !isReentrant &&
                ((n0x * n0x) + (n0y * n0y)) > 1e-18 && ((n1x * n1x) + (n1y * n1y)) > 1e-18)
            {
                double prevAngle = Math.Atan2(n0y, n0x);
                // Signed shortest turn from the incoming edge normal to the outgoing one. This is the
                // exterior angle at a convex corner (its sign follows the loop orientation), and is 0
                // for collinear points — correct for both CW and CCW loops.
                double sweep = Math.Atan2((n0x * n1y) - (n0y * n1x), (n0x * n1x) + (n0y * n1y));
                if (Math.Abs(sweep) > CornerFanMinSweepRad)
                {
                    int fanCount = cornerFanSegments + 1;
                    for (int f = 0; f < fanCount; f++)
                    {
                        double theta = prevAngle + (sweep * f / (fanCount - 1));
                        stationXy.Add(x);
                        stationXy.Add(y);
                        normals.Add(Math.Cos(theta));
                        normals.Add(Math.Sin(theta));
                        zList?.Add(vertexZ![i]);
                    }

                    emittedFan = true;
                }
            }

            if (!emittedFan)
            {
                double bx = n0x + n1x;
                double by = n0y + n1y;
                double length = Math.Sqrt((bx * bx) + (by * by));
                if (length <= 1e-12)
                {
                    bx = n1x;
                    by = n1y;
                    length = Math.Sqrt((bx * bx) + (by * by));
                }

                stationXy.Add(x);
                stationXy.Add(y);
                zList?.Add(vertexZ![i]);
                if (length <= 1e-12)
                {
                    normals.Add(0.0);
                    normals.Add(0.0);
                }
                else
                {
                    normals.Add(bx / length);
                    normals.Add(by / length);
                }
            }
        }

        return new FootprintStations(
            stationXy.ToArray(),
            normals.ToArray(),
            stationXy.Count / 2,
            zList?.ToArray() ?? Array.Empty<double>());
    }

    /// <summary>
    /// An explicit triangulated batter (side-slope) surface, plus the vertex indices of its
    /// inner (footprint) and outer (daylight) rings so the assembler can weld it to the pad top
    /// and the terrain seam without re-triangulating.
    /// </summary>
    internal sealed class BatterStrip
    {
        public required double[] Vertices { get; init; }

        public required int VertexCount { get; init; }

        public required int[] Faces { get; init; }

        public required int FaceCount { get; init; }

        public required int[] FootprintRingIndices { get; init; }

        public required int[] DaylightRingIndices { get; init; }

        public int RowCount { get; init; }
    }

    /// <summary>
    /// Builds an explicit ruled batter mesh between the footprint ring (row 0, at footprint
    /// elevation) and the daylight ring (last row, on terrain). The reach is subdivided into N
    /// rows derived from <paramref name="edgeLength"/> so the slope resolution is intrinsic and
    /// independent of the surrounding terrain density. Each row's elevation is the linear blend
    /// of footprint and daylight Z, so every facet sits on the target slope plane by
    /// construction. Degenerate (zero-area) facets, e.g. at flat stations or corner fan apexes,
    /// are dropped.
    /// </summary>
    internal static BatterStrip BuildBatterStrip(DaylightLoop loop, double edgeLength, int maxRows = 64)
    {
        if (loop is null) throw new ArgumentNullException(nameof(loop));
        int count = loop.Count;
        if (count < 2) throw new ArgumentOutOfRangeException(nameof(loop), "A batter strip needs at least two stations.");

        double maxReach = 0.0;
        foreach (DaylightStation station in loop.Stations)
            maxReach = Math.Max(maxReach, station.Reach);

        int rows = 1;
        if (edgeLength > 1e-9 && maxReach > edgeLength)
            rows = Math.Min(maxRows, (int)Math.Ceiling(maxReach / edgeLength));
        rows = Math.Max(1, rows);

        int ringCount = rows + 1;
        var vertices = new double[count * ringCount * 3];
        for (int r = 0; r <= rows; r++)
        {
            double t = r / (double)rows;
            for (int i = 0; i < count; i++)
            {
                DaylightStation s = loop.Stations[i];
                int index = (r * count) + i;
                vertices[index * 3] = s.FootX + ((s.DayX - s.FootX) * t);
                vertices[index * 3 + 1] = s.FootY + ((s.DayY - s.FootY) * t);
                vertices[index * 3 + 2] = s.FootZ + ((s.DayZ - s.FootZ) * t);
            }
        }

        int segmentCount = loop.IsClosed ? count : count - 1;
        var faces = new List<int>(segmentCount * rows * 6);
        for (int r = 0; r < rows; r++)
        {
            for (int s = 0; s < segmentCount; s++)
            {
                int i = s;
                int j = (s + 1) % count;
                int a = (r * count) + i;
                int b = (r * count) + j;
                int c = ((r + 1) * count) + j;
                int d = ((r + 1) * count) + i;

                AddTriangleIfNonDegenerate(faces, vertices, a, b, c);
                AddTriangleIfNonDegenerate(faces, vertices, a, c, d);
            }
        }

        var footprintRing = new int[count];
        var daylightRing = new int[count];
        for (int i = 0; i < count; i++)
        {
            footprintRing[i] = i;
            daylightRing[i] = (rows * count) + i;
        }

        return new BatterStrip
        {
            Vertices = vertices,
            VertexCount = count * ringCount,
            Faces = faces.ToArray(),
            FaceCount = faces.Count / 3,
            FootprintRingIndices = footprintRing,
            DaylightRingIndices = daylightRing,
            RowCount = rows
        };
    }

    /// <summary>
    /// Interior batter seed points (flat XYZ) for the corridor/pad hole-fill triangulation, with the row
    /// count chosen <b>per station from that station's own reach</b> (≈ one seed every
    /// <paramref name="edgeLength"/> down the slope). This is the uniform-density alternative to
    /// harvesting <see cref="BuildBatterStrip"/>'s ruled grid, whose row count is a single
    /// <c>ceil(maxReach / edgeLength)</c> taken from the <i>deepest</i> cross-section and applied to every
    /// station — so a shallow batter gets the deep section's many rows crammed into a short reach, which
    /// shows up as tight parallel bands. Only the interior rows are emitted (the footprint and daylight
    /// rings are already added as constraints/boundary by the caller); a station shallower than one
    /// <paramref name="edgeLength"/> contributes no interior seed at all. Z is the slope-plane blend, but
    /// callers seed by XY only and re-derive Z from the section grader.
    /// </summary>
    internal static double[] BuildBatterSeeds(DaylightLoop loop, double edgeLength, int maxRows = 64)
    {
        if (loop is null) throw new ArgumentNullException(nameof(loop));

        var seeds = new List<double>();
        foreach (DaylightStation s in loop.Stations)
        {
            if (s.Status == DaylightStatus.Flat || s.Reach <= 1e-9)
                continue;

            int rows = 1;
            if (edgeLength > 1e-9 && s.Reach > edgeLength)
                rows = Math.Min(maxRows, (int)Math.Ceiling(s.Reach / edgeLength));

            for (int r = 1; r < rows; r++)
            {
                double t = r / (double)rows;
                seeds.Add(s.FootX + ((s.DayX - s.FootX) * t));
                seeds.Add(s.FootY + ((s.DayY - s.FootY) * t));
                seeds.Add(s.FootZ + ((s.DayZ - s.FootZ) * t));
            }
        }

        return seeds.ToArray();
    }

    private static void AddTriangleIfNonDegenerate(List<int> faces, double[] vertices, int a, int b, int c)
    {
        double ax = vertices[a * 3], ay = vertices[a * 3 + 1], az = vertices[a * 3 + 2];
        double bx = vertices[b * 3], by = vertices[b * 3 + 1], bz = vertices[b * 3 + 2];
        double cx = vertices[c * 3], cy = vertices[c * 3 + 1], cz = vertices[c * 3 + 2];

        double ux = bx - ax, uy = by - ay, uz = bz - az;
        double vx = cx - ax, vy = cy - ay, vz = cz - az;
        double nx = (uy * vz) - (uz * vy);
        double ny = (uz * vx) - (ux * vz);
        double nz = (ux * vy) - (uy * vx);
        double areaTimesTwoSq = (nx * nx) + (ny * ny) + (nz * nz);
        if (areaTimesTwoSq <= 1e-18)
            return;

        faces.Add(a);
        faces.Add(b);
        faces.Add(c);
    }
}
