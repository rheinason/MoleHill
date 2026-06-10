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
        DaylightStatus Status);

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
        double slopeAngleDeg,
        double maxDistance,
        TerrainFaceGrid terrain,
        PreparedBarriers barriers,
        double tolerance)
    {
        if (footprintZ is null) throw new ArgumentNullException(nameof(footprintZ));
        return BuildDaylightLoopCore(
            footprintXy, footprintCount, isClosed, outwardNormals,
            (_, x, y) => footprintZ(x, y),
            slopeAngleDeg, maxDistance, terrain, barriers, tolerance);
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
        double slopeAngleDeg,
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
            slopeAngleDeg, maxDistance, terrain, barriers, tolerance);
    }

    private static DaylightLoop BuildDaylightLoopCore(
        double[] footprintXy,
        int footprintCount,
        bool isClosed,
        double[] outwardNormals,
        Func<int, double, double, double> footprintZ,
        double slopeAngleDeg,
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

        double slopeRatio = Math.Tan(Math.Clamp(slopeAngleDeg, 0.1, 89.9) * Math.PI / 180.0);
        double zTolerance = GradingTolerances.VertexAdjustmentZTolerance(tolerance);
        double searchDistance = maxDistance > 0.0
            ? maxDistance
            : Math.Max(terrain.BoundsDiagonal, 1.0);

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
                slopeRatio,
                searchDistance,
                maxDistance,
                zTolerance,
                terrain,
                barriers,
                barrierScratch,
                barrierCandidates);
        }

        return new DaylightLoop { Stations = stations, IsClosed = isClosed };
    }

    private static DaylightStation BuildStation(
        double fx,
        double fy,
        double fz,
        double nx,
        double ny,
        double slopeRatio,
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
                out double clippedY))
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

        return new DaylightStation(fx, fy, fz, dayX, dayY, dayZ, reach, status);
    }

    private static DaylightStation Flat(double fx, double fy, double fz) =>
        new(fx, fy, fz, fx, fy, fz, 0.0, DaylightStatus.Flat);

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
