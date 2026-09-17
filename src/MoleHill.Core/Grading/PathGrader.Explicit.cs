using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

public static partial class PathGrader
{
    /// <summary>
    /// Grades paths by building explicit corridor geometry. Each path is treated as a closed
    /// footprint — the road corridor outline (left edge forward, right edge back), carrying the
    /// centerline profile elevation per station — which feeds the shared <see cref="BatterStripBuilder"/>
    /// exactly like a pad with a non-planar footprint. The road surface is an explicit ruled strip
    /// between the edges, the side batters daylight outward, and everything is welded into the
    /// terrain by <see cref="GradedRegionAssembler"/>. Returns null (without throwing) when it cannot
    /// produce a watertight, manifold result.
    /// </summary>
    /// <summary>
    /// Detects a road edge (centerline or either offset edge) crossing a hard-constraint barrier,
    /// honouring endpoint-touch tolerances so a path that merely starts/ends on a barrier is allowed.
    /// Keeps barrier crossings out of the explicit path so the main path can report the unsupported
    /// condition consistently.
    /// </summary>
    private static bool AnyRoadEdgeCrossesBarrier(
        PathDefinition[] paths,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> hardConstraints,
        double modelTolerance)
    {
        PreparedBarriers roadBarriers = GradingBarriers.Build(hardConstraints);
        if (roadBarriers.Segments.Length == 0)
            return false;

        var scratch = new SpatialHashGrid2D.QueryScratch(Math.Max(roadBarriers.Segments.Length, 1));
        var candidates = new List<int>(8);
        double endpointTouchTolerance = GradingTolerances.ModelToleranceOrDefault(modelTolerance);

        foreach (PathDefinition path in paths)
        {
            double halfWidth = path.MaximumHalfWidth();
            int n = path.VertexCount;
            for (int i = 0; i < n - 1; i++)
            {
                double endpointCapTolerance = Math.Max(endpointTouchTolerance, halfWidth);
                double startTol = i == 0 ? endpointCapTolerance : endpointTouchTolerance;
                double endTol = i == n - 2 ? endpointCapTolerance : endpointTouchTolerance;
                double cx0 = path.XyVertices[i * 2];
                double cy0 = path.XyVertices[(i * 2) + 1];
                double cx1 = path.XyVertices[(i + 1) * 2];
                double cy1 = path.XyVertices[((i + 1) * 2) + 1];
                GetPathEdgePoint(path, i, left: true, out double lx0, out double ly0);
                GetPathEdgePoint(path, i + 1, left: true, out double lx1, out double ly1);
                GetPathEdgePoint(path, i, left: false, out double rx0, out double ry0);
                GetPathEdgePoint(path, i + 1, left: false, out double rx1, out double ry1);

                if (GradingBarriers.IsInteriorCrossedByBarrier(roadBarriers, cx0, cy0, cx1, cy1, startTol, endTol, scratch, candidates) ||
                    GradingBarriers.IsInteriorCrossedByBarrier(roadBarriers, lx0, ly0, lx1, ly1, startTol, endTol, scratch, candidates) ||
                    GradingBarriers.IsInteriorCrossedByBarrier(roadBarriers, rx0, ry0, rx1, ry1, startTol, endTol, scratch, candidates))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private readonly record struct PathCorridor(
        double[] LeftXyz,
        double[] RightXyz,
        int N,
        double[] DaylightXy,
        double Spacing,
        BatterStripBuilder.DaylightLoop Loop,
        bool IsSingleLine = false);

    private static GradingResult? GradeWithExplicitCorridor(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PathDefinition[] paths,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> hardConstraints,
        double modelTolerance,
        out string? errorMessage)
    {
        errorMessage = null;

        if (paths.Length == 0)
        {
            errorMessage = "Grade Path received no paths.";
            return null;
        }

        double tolerance = GradingTolerances.ModelToleranceOrDefault(modelTolerance);
        var terrain = new TerrainFaceGrid(vertices, vertexCount, faces, faceCount);

        // A road edge crossing a barrier is unsupported by the explicit path; defer cleanly.
        if (AnyRoadEdgeCrossesBarrier(paths, hardConstraints, modelTolerance))
        {
            errorMessage = "Grade Path road edge crosses a hard constraint; explicit corridor grading deferred.";
            return null;
        }

        // A one-sided grade daylights away from its rail, so the rail lies *on* the carve boundary
        // rather than inside it. The explicit fill then carries that rail twice — once as a boundary
        // vertex at terrain elevation, once as a rail vertex at the authored elevation — and the weld
        // keeps the terrain one, which silently flattens the rail to existing ground. (Found live on a
        // retaining wall: the batter itself was correct, only the rail row was wrong, so nothing threw
        // and nothing looked obviously broken.) Split-keep conforms the rail in place and gets this
        // right, so defer to it rather than weld a wrong elevation.
        foreach (PathDefinition path in paths)
        {
            if (path.OutwardNormals is { Length: > 0 })
            {
                errorMessage = "Grade Path one-sided rail grading is handled by terrain conform; explicit corridor grading deferred.";
                return null;
            }
        }

        // Lock curves clip the corridor batters (passed as barriers to the daylight ray-march).
        PreparedBarriers barriers = GradingBarriers.Build(hardConstraints);

        var outputPolylines = new List<OutputPolyline>(paths.Length * 2);
        int nonDaylightingStations = 0;
        List<PathCorridor>? corridors = BuildCorridors(
            paths, terrain, barriers, tolerance, outputPolylines, ref nonDaylightingStations, out errorMessage);
        if (corridors is null)
            return null;

        var daylightLoopsXy = new List<double[]>(corridors.Count);
        foreach (PathCorridor corridor in corridors)
            daylightLoopsXy.Add(corridor.DaylightXy);

        // Interacting corridors need junction ownership; defer those.
        for (int i = 0; i < daylightLoopsXy.Count; i++)
        {
            for (int j = i + 1; j < daylightLoopsXy.Count; j++)
            {
                if (GradingGeometry2D.PolygonsOverlap(daylightLoopsXy[i], daylightLoopsXy[j]))
                {
                    errorMessage = "Grade Path corridors interact; deferring to topology rebuild.";
                    return null;
                }
            }
        }

        GradedRegionAssembler.SplitOutsideResult split = GradedRegionAssembler.SplitOutside(
            vertices, vertexCount, faces, faceCount, daylightLoopsXy, tolerance, hardConstraints);
        if (!split.Success)
        {
            errorMessage = split.Warning ?? "Grade Path terrain split failed.";
            return null;
        }

        var fills = new List<GradedRegionAssembler.SubMesh>(split.HoleBoundaryLoops.Count);
        foreach (int[] boundaryLoop in split.HoleBoundaryLoops)
        {
            double[] boundaryXyz = ExtractLoopXyz(split.Vertices, boundaryLoop);
            int corridorIndex = MatchCorridorForBoundary(boundaryXyz, boundaryLoop.Length, daylightLoopsXy);
            if (corridorIndex < 0)
            {
                errorMessage = "Grade Path could not match a hole boundary to a corridor; deferring.";
                return null;
            }

            GradedRegionAssembler.SubMesh? fill = BuildCorridorHoleFill(
                boundaryXyz, boundaryLoop, boundaryLoop.Length, corridors[corridorIndex], paths, hardConstraints, terrain, tolerance);
            if (fill is null)
            {
                errorMessage = "Grade Path hole fill failed; deferring to topology rebuild.";
                return null;
            }

            fills.Add(fill);
        }

        GradedRegionAssembler.AssembledMesh assembled = GradedRegionAssembler.WeldGradedRegion(
            split.Vertices, split.OutsideFaces, split.OutsideFaceCount, fills, tolerance);

        MeshTopologyValidator.BoundaryGraphAnalysis topology =
            MeshTopologyValidator.AnalyzeBoundaryGraph(assembled.Faces, assembled.FaceCount);
        MeshTopologyValidator.BoundaryGraphAnalysis terrainTopology =
            MeshTopologyValidator.AnalyzeBoundaryGraph(faces, faceCount);
        if (topology.NonManifoldEdgeCount > 0 ||
            topology.HasOpenBoundaryChains ||
            topology.BoundaryComponentCount > terrainTopology.BoundaryComponentCount)
        {
            errorMessage = GradedRegionAssembler.DescribeWeldTopologyFailure("Grade Path", topology, terrainTopology);
            return null;
        }

        double[] graded = assembled.Vertices;
        var original = new double[graded.Length];
        for (int i = 0; i < assembled.VertexCount; i++)
        {
            double x = graded[i * 3];
            double y = graded[i * 3 + 1];
            original[i * 3] = x;
            original[i * 3 + 1] = y;
            original[i * 3 + 2] = terrain.InterpolateZ(x, y);
        }

        const string modeMessage =
            "Grade Path topology mode: explicit corridor construction (ruled road surface and rounded side batters welded into terrain).";
        var diagnostics = new List<string> { modeMessage };
        var structured = new List<GradingDiagnostic>
        {
            GradingDiagnostic.Information("grade_path.topology.mode", modeMessage, operation: "grade_path")
        };

        if (nonDaylightingStations > 0)
        {
            string message =
                $"Grade Path batter did not reach existing ground at {nonDaylightingStations} station(s); the slope was clamped to the search extent there.";
            diagnostics.Add(message);
            structured.Add(GradingDiagnostic.Warning("grade_path.daylight.incomplete", message, operation: "grade_path"));
        }

        return GradingResultBuilder.BuildFromXyz(
            original,
            graded,
            assembled.VertexCount,
            assembled.Faces,
            assembled.FaceCount,
            outputPolylines,
            diagnostics,
            BuildPathPatchSummaries(paths),
            structured);
    }

    /// <summary>
    /// Builds one corridor per path: resampled centerline, offset left/right road edges, the
    /// daylight loop from the shared batter-strip ray-march, and the corridor daylight polygon
    /// (side day points + rounded end arcs, self-overlap resolved to the Clipper outer envelope).
    /// Shared by the explicit corridor tier and the split-keep conform tier so both grade the exact
    /// same corridor geometry. Appends the road edges to <paramref name="outputPolylines"/> when
    /// provided. Returns null (with a reason) when a path is degenerate or a self-intersecting
    /// daylight loop cannot be resolved.
    /// </summary>
    private static List<PathCorridor>? BuildCorridors(
        PathDefinition[] paths,
        TerrainFaceGrid terrain,
        PreparedBarriers barriers,
        double tolerance,
        List<OutputPolyline>? outputPolylines,
        ref int nonDaylightingStations,
        out string? errorMessage)
    {
        errorMessage = null;
        var corridors = new List<PathCorridor>(paths.Length);

        foreach (PathDefinition path in paths)
        {
            if (path.VertexCount < 2 || (!path.IsSingleLine && path.Width <= tolerance))
            {
                errorMessage = "Grade Path definition was degenerate.";
                return null;
            }

            // A rail carrying explicit outward normals grades one way only (the retaining-wall case:
            // a wall batters away from its partner rail, never through it).
            bool oneSided = path.OutwardNormals is { Length: > 0 };

            double spacing = path.IsSingleLine
                ? ComputeSingleLineSegmentLength(path, terrain, tolerance)
                : ComputeConstraintSegmentLength(path, shoulderDistance: 0.0);

            // Those normals are supplied one per authored vertex, so a one-sided rail must keep the
            // stationing it arrived with — resampling it would leave the directions misaligned with
            // the stations they belong to. Its stationing is the planner's, not ours to second-guess.
            ConstraintPath center = oneSided
                ? BuildConstraintPolyline(path, maxSegmentLength: 0.0, tolerance)
                : BuildConstraintPolyline(path, spacing, tolerance);
            int n = center.VertexCount;
            if (n < 2)
            {
                errorMessage = "Grade Path centerline collapsed.";
                return null;
            }

            // A single line is its own footprint, so both rails collapse onto the centerline. The
            // stations still walk out and back, which is what gives the left and right batters their
            // own rays and their own slopes.
            double halfWidth = path.IsSingleLine ? 0.0 : path.Width * 0.5;
            var leftXyz = new double[n * 3];
            var rightXyz = new double[n * 3];
            for (int i = 0; i < n; i++)
            {
                double cx = center.XyVertices[i * 2];
                double cy = center.XyVertices[i * 2 + 1];
                double cz = center.ZValues[i];
                double nx = -center.TangentY[i];
                double ny = center.TangentX[i];

                leftXyz[i * 3] = center.LeftEdgeXy?[i * 2] ?? cx + (nx * halfWidth);
                leftXyz[i * 3 + 1] = center.LeftEdgeXy?[(i * 2) + 1] ?? cy + (ny * halfWidth);
                leftXyz[i * 3 + 2] = cz;
                rightXyz[i * 3] = center.RightEdgeXy?[i * 2] ?? cx - (nx * halfWidth);
                rightXyz[i * 3 + 1] = center.RightEdgeXy?[(i * 2) + 1] ?? cy - (ny * halfWidth);
                rightXyz[i * 3 + 2] = cz;
            }

            // Side stations (left edge forward, right edge backward); the daylight at the road
            // ends is rounded by an arc afterward to avoid the overlapping-corner-fan pinch.
            int sideCount = oneSided ? n : n * 2;
            var stationXy = new double[sideCount * 2];
            var normals = new double[sideCount * 2];
            var footZ = new double[sideCount];
            var slopeAngles = new double[sideCount * 2];
            for (int i = 0; i < n; i++)
            {
                stationXy[i * 2] = leftXyz[i * 3];
                stationXy[i * 2 + 1] = leftXyz[i * 3 + 1];
                if (oneSided)
                {
                    NormalizeOrFallback(
                        path.OutwardNormals![i * 2], path.OutwardNormals[(i * 2) + 1],
                        -center.TangentY[i], center.TangentX[i],
                        out normals[i * 2], out normals[(i * 2) + 1]);
                }
                else
                {
                    double ldx = leftXyz[i * 3] - center.XyVertices[i * 2];
                    double ldy = leftXyz[(i * 3) + 1] - center.XyVertices[(i * 2) + 1];
                    NormalizeOrFallback(ldx, ldy, -center.TangentY[i], center.TangentX[i], out normals[i * 2], out normals[(i * 2) + 1]);
                }

                footZ[i] = center.ZValues[i];
                slopeAngles[i * 2] = path.LeftCutSlopeAngleDeg;
                slopeAngles[(i * 2) + 1] = path.LeftFillSlopeAngleDeg;
            }

            if (!oneSided)
            {
                for (int i = 0; i < n; i++)
                {
                    int src = n - 1 - i;
                    int dst = n + i;
                    stationXy[dst * 2] = rightXyz[src * 3];
                    stationXy[dst * 2 + 1] = rightXyz[src * 3 + 1];
                    double rdx = rightXyz[src * 3] - center.XyVertices[src * 2];
                    double rdy = rightXyz[(src * 3) + 1] - center.XyVertices[(src * 2) + 1];
                    NormalizeOrFallback(rdx, rdy, center.TangentY[src], -center.TangentX[src], out normals[dst * 2], out normals[(dst * 2) + 1]);
                    footZ[dst] = center.ZValues[src];
                    slopeAngles[dst * 2] = path.RightCutSlopeAngleDeg;
                    slopeAngles[(dst * 2) + 1] = path.RightFillSlopeAngleDeg;
                }
            }

            // A two-sided ring always wraps (out along one side, back along the other). A one-sided
            // rail wraps only if the rail itself is closed — getting this wrong costs the spike
            // regularizer its wrap-around at the seam of a closed wall.
            bool daylightWraps = !oneSided || path.IsClosed;

            BatterStripBuilder.DaylightLoop loop = BatterStripBuilder.BuildDaylightLoop(
                stationXy, sideCount, isClosed: daylightWraps, normals, footZ,
                path.SlopeAngleDeg, path.FillSlopeAngleDeg, path.MaxDistance, terrain, barriers, tolerance,
                path.HasAsymmetricSides ? slopeAngles : null);

            foreach (BatterStripBuilder.DaylightStation station in loop.Stations)
            {
                if (station.Status == BatterStripBuilder.DaylightStatus.NonDaylighting)
                    nonDaylightingStations++;
            }

            // Build the daylight polygon: left-side day points, a rounded end arc, right-side day
            // points, a rounded start arc. Arcs connect the side day points smoothly (no pinch).
            double[] dayXy = loop.DaylightXy();

            var polyXy = new List<double>(dayXy.Length + 32);
            for (int i = 0; i < n; i++)
            {
                polyXy.Add(dayXy[i * 2]);
                polyXy.Add(dayXy[i * 2 + 1]);
            }

            if (oneSided)
            {
                // Only one side daylights, so the envelope closes along the rail itself rather than
                // sweeping an arc past the ends — a wall's batter must not wrap around its end.
                for (int i = n - 1; i >= 0; i--)
                {
                    polyXy.Add(center.XyVertices[i * 2]);
                    polyXy.Add(center.XyVertices[(i * 2) + 1]);
                }
            }
            else
            {
                // End arc bulges along +tangent at the last station; start arc along -tangent at the first.
                AddEndArc(polyXy, dayXy[(n - 1) * 2], dayXy[(n - 1) * 2 + 1], dayXy[n * 2], dayXy[n * 2 + 1],
                    center.XyVertices[(n - 1) * 2], center.XyVertices[(n - 1) * 2 + 1],
                    center.TangentX[n - 1], center.TangentY[n - 1], spacing);

                for (int i = n; i < sideCount; i++)
                {
                    polyXy.Add(dayXy[i * 2]);
                    polyXy.Add(dayXy[i * 2 + 1]);
                }

                AddEndArc(polyXy, dayXy[(sideCount - 1) * 2], dayXy[(sideCount - 1) * 2 + 1], dayXy[0], dayXy[1],
                    center.XyVertices[0], center.XyVertices[1],
                    -center.TangentX[0], -center.TangentY[0], spacing);
            }

            double[] daylightPolyXy = polyXy.ToArray();

            // The raw per-station daylight points can fold over themselves where the daylight reach
            // varies sharply between neighbours (a tight curve, rapidly changing terrain, or a barrier
            // clipping some rays short but not others). Resolve any self-overlap into the clean outer
            // envelope with a Clipper union instead of deferring to the fallback. Batter seeds that end
            // up outside this envelope are filtered out later in BuildCorridorHoleFill.
            if (GradingGeometry2D.ClosedPolylineSelfIntersects(daylightPolyXy, daylightPolyXy.Length / 2))
            {
                if (!ClipperGeometry.TryUnionClosedLoops(new[] { daylightPolyXy }, tolerance, out List<double[]> cleanedLoops) ||
                    !ClipperGeometry.TryPickLargestLoop(cleanedLoops, out double[] envelope) ||
                    GradingGeometry2D.ClosedPolylineSelfIntersects(envelope, envelope.Length / 2))
                {
                    errorMessage = "Grade Path daylight loop self-intersects and could not be resolved; deferring to topology rebuild.";
                    return null;
                }

                daylightPolyXy = envelope;
            }

            corridors.Add(new PathCorridor(leftXyz, rightXyz, n, daylightPolyXy, spacing, loop, path.IsSingleLine));
            if (outputPolylines != null)
            {
                // The two rails coincide on a single line, so publish the design line once — emitting
                // it twice would install the same breakline as two persistent constraints.
                outputPolylines.Add(new OutputPolyline(leftXyz, n, isClosed: false));
                if (!path.IsSingleLine)
                    outputPolylines.Add(new OutputPolyline(rightXyz, n, isClosed: false));
            }
        }

        return corridors;
    }

    private static void NormalizeOrFallback(
        double x,
        double y,
        double fallbackX,
        double fallbackY,
        out double normalizedX,
        out double normalizedY)
    {
        double length = Math.Sqrt((x * x) + (y * y));
        if (length <= 1e-12)
        {
            normalizedX = fallbackX;
            normalizedY = fallbackY;
            return;
        }

        normalizedX = x / length;
        normalizedY = y / length;
    }

    /// <summary>
    /// Appends a rounded end arc to the daylight polygon between two consecutive side daylight points
    /// (A and B), centered on the road centerline endpoint (cx,cy), bulging outward. Endpoints A and B
    /// are already in the polygon, so only intermediate points are added.
    /// </summary>
    private static void AddEndArc(
        List<double> polyXy,
        double ax, double ay, double bx, double by,
        double cx, double cy,
        double outwardX, double outwardY,
        double spacing)
    {
        double angleA = Math.Atan2(ay - cy, ax - cx);
        double angleB = Math.Atan2(by - cy, bx - cx);
        double radius = (Math.Sqrt(((ax - cx) * (ax - cx)) + ((ay - cy) * (ay - cy))) +
                         Math.Sqrt(((bx - cx) * (bx - cx)) + ((by - cy) * (by - cy)))) * 0.5;

        double sweep = angleB - angleA;
        while (sweep <= -Math.PI) sweep += 2.0 * Math.PI;
        while (sweep > Math.PI) sweep -= 2.0 * Math.PI;

        // Pick the sweep whose arc midpoint bulges in the outward direction (away from the road),
        // not inward across the corridor.
        double midAngle = angleA + (sweep * 0.5);
        if ((Math.Cos(midAngle) * outwardX) + (Math.Sin(midAngle) * outwardY) < 0.0)
            sweep += sweep > 0.0 ? -2.0 * Math.PI : 2.0 * Math.PI;

        double spacingFloor = ScaleAwareTolerance.LengthFloor(Math.Max(radius, spacing));
        int steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) * radius / Math.Max(spacing, spacingFloor)));
        for (int s = 1; s < steps; s++)
        {
            double theta = angleA + (sweep * s / steps);
            polyXy.Add(cx + (radius * Math.Cos(theta)));
            polyXy.Add(cy + (radius * Math.Sin(theta)));
        }
    }

    private static double[] ExtractLoopXyz(double[] vertices, int[] loop)
    {
        var xyz = new double[loop.Length * 3];
        for (int i = 0; i < loop.Length; i++)
        {
            int v = loop[i];
            xyz[i * 3] = vertices[v * 3];
            xyz[i * 3 + 1] = vertices[v * 3 + 1];
            xyz[i * 3 + 2] = vertices[v * 3 + 2];
        }

        return xyz;
    }

    private static int MatchCorridorForBoundary(double[] boundaryXyz, int boundaryCount, List<double[]> corridorLoops)
    {
        double cx = 0.0, cy = 0.0;
        for (int i = 0; i < boundaryCount; i++)
        {
            cx += boundaryXyz[i * 3];
            cy += boundaryXyz[i * 3 + 1];
        }

        cx /= boundaryCount;
        cy /= boundaryCount;

        for (int c = 0; c < corridorLoops.Count; c++)
        {
            if (GradingGeometry2D.PointInPolygon(cx, cy, corridorLoops[c], corridorLoops[c].Length / 2))
                return c;
        }

        int best = -1;
        double bestDistSq = double.MaxValue;
        for (int c = 0; c < corridorLoops.Count; c++)
        {
            int gc = corridorLoops[c].Length / 2;
            double gx = 0.0, gy = 0.0;
            for (int i = 0; i < gc; i++)
            {
                gx += corridorLoops[c][i * 2];
                gy += corridorLoops[c][i * 2 + 1];
            }

            gx /= gc;
            gy /= gc;
            double d = ((gx - cx) * (gx - cx)) + ((gy - cy) * (gy - cy));
            if (d < bestDistSq)
            {
                bestDistSq = d;
                best = c;
            }
        }

        return best;
    }

    /// <summary>
    /// Triangulates a corridor hole: conformed boundary + road edges (left/right + end caps) as
    /// constraints, road centerline and batter rows as seeds; assigns road-profile + batter Z via the
    /// path section grader and pins the boundary to terrain.
    /// </summary>
    private static GradedRegionAssembler.SubMesh? BuildCorridorHoleFill(
        double[] boundaryXyz,
        int[] boundaryLoop,
        int boundaryCount,
        PathCorridor corridor,
        PathDefinition[] paths,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> hardConstraints,
        TerrainFaceGrid terrain,
        double tolerance)
    {
        // Input point dedup. Cell size is 2x the weld tolerance and lookups scan the 2x2 cell block
        // covering [p - tol, p + tol], merging by actual Euclidean distance — a single-cell hash both
        // misses near pairs that straddle a cell boundary (banker's rounding splits p +/- epsilon across
        // cells) and falsely merges far pairs sharing a cell (cell diagonal = tol*sqrt(2)), corrupting
        // the boundary loop the fill relies on. Mirrors PadGrader.BuildHoleFill.
        double weldTol = ScaleAwareTolerance.ResolveLength(tolerance, terrain.BoundsDiagonal);
        double weldTolSq = weldTol * weldTol;
        double inverseCell = 1.0 / (2.0 * weldTol);
        var xyList = new List<double>();
        var inputZ = new List<double>();
        var pointCells = new Dictionary<(long, long), List<int>>();
        var segments = new List<(int a, int b)>();

        int AddPoint(double x, double y, double z)
        {
            long cx0 = (long)Math.Round((x - weldTol) * inverseCell);
            long cy0 = (long)Math.Round((y - weldTol) * inverseCell);
            for (long cx = cx0; cx <= cx0 + 1; cx++)
            {
                for (long cy = cy0; cy <= cy0 + 1; cy++)
                {
                    if (!pointCells.TryGetValue((cx, cy), out List<int>? bucket))
                        continue;

                    foreach (int existing in bucket)
                    {
                        double dx = xyList[existing * 2] - x;
                        double dy = xyList[(existing * 2) + 1] - y;
                        if ((dx * dx) + (dy * dy) <= weldTolSq)
                            return existing;
                    }
                }
            }

            int index = xyList.Count / 2;
            xyList.Add(x);
            xyList.Add(y);
            inputZ.Add(z);
            var key = ((long)Math.Round(x * inverseCell), (long)Math.Round(y * inverseCell));
            if (!pointCells.TryGetValue(key, out List<int>? list))
            {
                list = new List<int>(1);
                pointCells[key] = list;
            }

            list.Add(index);
            return index;
        }

        // Conformed boundary first (indices [0, boundaryPointCount) are perimeter vertices to pin).
        // Record which fill input index each terrain hole-boundary vertex mapped to, so the weld can
        // stitch by shared identity and collapse any terrain pair the dedup merged (see BuildHoleFill).
        var loopToInput = new int[boundaryCount];
        int firstB = AddPoint(boundaryXyz[0], boundaryXyz[1], boundaryXyz[2]);
        loopToInput[0] = firstB;
        int prevB = firstB;
        for (int i = 1; i < boundaryCount; i++)
        {
            int cur = AddPoint(boundaryXyz[i * 3], boundaryXyz[i * 3 + 1], boundaryXyz[i * 3 + 2]);
            loopToInput[i] = cur;
            if (cur != prevB)
                segments.Add((prevB, cur));
            prevB = cur;
        }

        if (prevB != firstB)
            segments.Add((prevB, firstB));

        int boundaryPointCount = xyList.Count / 2;

        var inputToTerrain = new int[boundaryPointCount];
        for (int i = 0; i < boundaryPointCount; i++)
            inputToTerrain[i] = -1;

        var terrainMerges = new List<(int From, int To)>();
        for (int k = 0; k < boundaryCount; k++)
        {
            int input = loopToInput[k];
            if (input < 0 || input >= boundaryPointCount)
                continue;

            if (inputToTerrain[input] < 0)
                inputToTerrain[input] = boundaryLoop[k];
            else if (inputToTerrain[input] != boundaryLoop[k])
                terrainMerges.Add((boundaryLoop[k], inputToTerrain[input]));
        }

        int n = corridor.N;
        // Road edges (left + right) as open constraints + end-cap edges → crisp road top boundary.
        // Carry the road profile Z and pin them so the edge follows the profile exactly (the section
        // grader is ambiguous right on the road/batter boundary).
        var pinnedZ = new Dictionary<int, double>();
        for (int b = 0; b < boundaryPointCount; b++)
            pinnedZ[b] = inputZ[b];

        int[] leftIdx = new int[n];
        int[] rightIdx = new int[n];
        for (int i = 0; i < n; i++)
        {
            leftIdx[i] = AddPoint(corridor.LeftXyz[i * 3], corridor.LeftXyz[i * 3 + 1], corridor.LeftXyz[i * 3 + 2]);
            rightIdx[i] = AddPoint(corridor.RightXyz[i * 3], corridor.RightXyz[i * 3 + 1], corridor.RightXyz[i * 3 + 2]);
            pinnedZ[leftIdx[i]] = corridor.LeftXyz[i * 3 + 2];
            pinnedZ[rightIdx[i]] = corridor.RightXyz[i * 3 + 2];
        }

        for (int i = 0; i < n - 1; i++)
        {
            if (leftIdx[i] != leftIdx[i + 1]) segments.Add((leftIdx[i], leftIdx[i + 1]));
            if (rightIdx[i] != rightIdx[i + 1]) segments.Add((rightIdx[i], rightIdx[i + 1]));
        }

        if (leftIdx[n - 1] != rightIdx[n - 1]) segments.Add((leftIdx[n - 1], rightIdx[n - 1]));
        if (leftIdx[0] != rightIdx[0]) segments.Add((leftIdx[0], rightIdx[0]));

        // Batter row seeds from the side batter strip (density/slope). When the daylight envelope was
        // simplified (a fold was resolved by Clipper), some original batter-row points can fall outside
        // the conformed boundary; keep only seeds inside it so the triangulation stays within the hole.
        if (corridor.Loop.HasBatter)
        {
            var boundaryXy = new double[boundaryCount * 2];
            for (int i = 0; i < boundaryCount; i++)
            {
                boundaryXy[i * 2] = boundaryXyz[i * 3];
                boundaryXy[i * 2 + 1] = boundaryXyz[i * 3 + 1];
            }

            // Seed density guard: a seed within roughly a quarter station-spacing of ANY existing fill
            // input point (boundary, road edge, or another seed) creates near-degenerate micro-faces
            // whose blended Z reads as jagged spikes; occupancy of a coarse grid approximates that
            // min-distance cheaply.
            double seedSpacing = Math.Max(
                corridor.Spacing * 0.25,
                Math.Max(tolerance * 1000.0, ScaleAwareTolerance.LengthFloor(terrain.BoundsDiagonal)));
            double invSeedCell = 1.0 / seedSpacing;
            var seedCells = new HashSet<(long, long)>();
            for (int i = 0; i < xyList.Count / 2; i++)
                seedCells.Add(((long)Math.Floor(xyList[i * 2] * invSeedCell), (long)Math.Floor(xyList[(i * 2) + 1] * invSeedCell)));

            void AddSeedPoint(double sx, double sy)
            {
                var cell = ((long)Math.Floor(sx * invSeedCell), (long)Math.Floor(sy * invSeedCell));
                for (long cx = cell.Item1 - 1; cx <= cell.Item1 + 1; cx++)
                {
                    for (long cy = cell.Item2 - 1; cy <= cell.Item2 + 1; cy++)
                    {
                        if (seedCells.Contains((cx, cy)))
                            return;
                    }
                }

                AddPoint(sx, sy, 0.0);
                seedCells.Add(cell);
            }

            double[] seeds = BatterStripBuilder.BuildBatterSeeds(corridor.Loop, corridor.Spacing);
            for (int i = 0; i < seeds.Length / 3; i++)
            {
                double sx = seeds[i * 3];
                double sy = seeds[i * 3 + 1];
                if (GradingGeometry2D.PointInPolygon(sx, sy, boundaryXy, boundaryCount))
                    AddSeedPoint(sx, sy);
            }

            // Where the pre-clip batter rows were dropped (a barrier clipped the ray, or the daylight
            // envelope was Clipper-simplified past the fold), re-seed each station against the ACTUAL
            // conformed boundary: find how far the station ray stays inside the hole and place rows
            // within that span. Without this the fill spans long steep slivers from the road edge
            // straight to the boundary in exactly those regions.
            foreach (BatterStripBuilder.DaylightStation station in corridor.Loop.Stations)
            {
                double dx = station.DayX - station.FootX;
                double dy = station.DayY - station.FootY;
                if ((dx * dx) + (dy * dy) <= 1e-12)
                    continue;

                double tMax = 0.0;
                for (double t = 1.0; t >= 0.15; t -= 0.1)
                {
                    if (GradingGeometry2D.PointInPolygon(station.FootX + (dx * t), station.FootY + (dy * t), boundaryXy, boundaryCount))
                    {
                        tMax = t;
                        break;
                    }
                }

                if (tMax <= 0.0)
                    continue;

                AddSeedPoint(station.FootX + (dx * tMax / 3.0), station.FootY + (dy * tMax / 3.0));
                AddSeedPoint(station.FootX + (dx * tMax * 2.0 / 3.0), station.FootY + (dy * tMax * 2.0 / 3.0));
            }
        }

        TriangulationOutcome outcome = TriangulationHelper.Triangulate(
            xyList, xyList.Count / 2, segments, maxArea: 0.0, minAngle: 0.0, convex: false, segmentSplitting: 0);
        if (outcome.Mesh == null)
            return null;

        TriangleNetExtractor.Result extracted = TriangleNetExtractor.Extract(outcome.Mesh);
        if (extracted.FaceCount == 0)
            return null;

        int vc = extracted.VertexCount;
        var holeVerts = new double[vc * 3];
        for (int i = 0; i < vc; i++)
        {
            double x = extracted.Xy[i * 2];
            double y = extracted.Xy[i * 2 + 1];
            holeVerts[i * 3] = x;
            holeVerts[i * 3 + 1] = y;
            holeVerts[i * 3 + 2] = terrain.InterpolateZ(x, y);
        }

        double[] graded = ApplyGradingZ(holeVerts, vc, paths, hardConstraints, out _);

        // Pin the conformed boundary to terrain and the road edges to the road profile.
        for (int i = 0; i < vc; i++)
        {
            int sourceId = extracted.SourceIds[i];
            if (sourceId >= 0 && pinnedZ.TryGetValue(sourceId, out double pinZ))
                graded[i * 3 + 2] = pinZ;
        }

        // Identity weld map: a fill vertex sourced from a boundary input point reproduces terrain
        // vertex inputToTerrain[sourceId]; everything else is the fill's own interior (-1).
        var boundaryTerrainIndex = new int[vc];
        for (int i = 0; i < vc; i++)
        {
            int sourceId = extracted.SourceIds[i];
            boundaryTerrainIndex[i] = sourceId >= 0 && sourceId < boundaryPointCount
                ? inputToTerrain[sourceId]
                : -1;
        }

        return new GradedRegionAssembler.SubMesh
        {
            Vertices = graded,
            VertexCount = vc,
            Faces = extracted.Faces,
            FaceCount = extracted.FaceCount,
            BoundaryTerrainIndex = boundaryTerrainIndex,
            TerrainMerges = terrainMerges.Count > 0 ? terrainMerges.ToArray() : null
        };
    }
}
