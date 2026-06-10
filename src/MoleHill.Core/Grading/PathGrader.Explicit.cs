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
    /// produce a watertight, manifold result so the caller can fall back to the legacy path.
    /// </summary>
    /// <summary>
    /// Detects a road edge (centerline or either offset edge) crossing a hard-constraint barrier,
    /// honouring endpoint-touch tolerances so a path that merely starts/ends on a barrier is allowed.
    /// Mirrors the legacy preflight so the explicit path defers crossings to it for the error.
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
            double halfWidth = path.Width * 0.5;
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
                ComputeDirection(path.XyVertices, n, i, out double dx, out double dy);
                double px = -dy * halfWidth;
                double py = dx * halfWidth;

                if (GradingBarriers.IsInteriorCrossedByBarrier(roadBarriers, cx0, cy0, cx1, cy1, startTol, endTol, scratch, candidates) ||
                    GradingBarriers.IsInteriorCrossedByBarrier(roadBarriers, cx0 + px, cy0 + py, cx1 + px, cy1 + py, startTol, endTol, scratch, candidates) ||
                    GradingBarriers.IsInteriorCrossedByBarrier(roadBarriers, cx0 - px, cy0 - py, cx1 - px, cy1 - py, startTol, endTol, scratch, candidates))
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
        BatterStripBuilder.DaylightLoop Loop);

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

        // A road edge crossing a barrier is an error the legacy preflight reports; defer to it.
        if (AnyRoadEdgeCrossesBarrier(paths, hardConstraints, modelTolerance))
        {
            errorMessage = "Grade Path road edge crosses a hard constraint; deferring to constraint-first path.";
            return null;
        }

        // Lock curves clip the corridor batters (passed as barriers to the daylight ray-march).
        PreparedBarriers barriers = GradingBarriers.Build(hardConstraints);

        var corridors = new List<PathCorridor>(paths.Length);
        var daylightLoopsXy = new List<double[]>(paths.Length);
        var outputPolylines = new List<OutputPolyline>(paths.Length * 2);
        int nonDaylightingStations = 0;

        foreach (PathDefinition path in paths)
        {
            if (path.VertexCount < 2 || path.Width <= tolerance)
            {
                errorMessage = "Grade Path definition was degenerate.";
                return null;
            }

            double spacing = ComputeConstraintSegmentLength(path, shoulderDistance: 0.0);
            ConstraintPath center = BuildConstraintPolyline(path, spacing, tolerance);
            int n = center.VertexCount;
            if (n < 2)
            {
                errorMessage = "Grade Path centerline collapsed.";
                return null;
            }

            double halfWidth = path.Width * 0.5;
            var leftXyz = new double[n * 3];
            var rightXyz = new double[n * 3];
            for (int i = 0; i < n; i++)
            {
                double cx = center.XyVertices[i * 2];
                double cy = center.XyVertices[i * 2 + 1];
                double cz = center.ZValues[i];
                double nx = -center.TangentY[i];
                double ny = center.TangentX[i];

                leftXyz[i * 3] = cx + (nx * halfWidth);
                leftXyz[i * 3 + 1] = cy + (ny * halfWidth);
                leftXyz[i * 3 + 2] = cz;
                rightXyz[i * 3] = cx - (nx * halfWidth);
                rightXyz[i * 3 + 1] = cy - (ny * halfWidth);
                rightXyz[i * 3 + 2] = cz;
            }

            // Side stations only (left edge forward, right edge backward); the daylight at the road
            // ends is rounded by an arc afterward to avoid the overlapping-corner-fan pinch.
            int sideCount = n * 2;
            var stationXy = new double[sideCount * 2];
            var normals = new double[sideCount * 2];
            var footZ = new double[sideCount];
            for (int i = 0; i < n; i++)
            {
                stationXy[i * 2] = leftXyz[i * 3];
                stationXy[i * 2 + 1] = leftXyz[i * 3 + 1];
                normals[i * 2] = -center.TangentY[i];
                normals[i * 2 + 1] = center.TangentX[i];
                footZ[i] = center.ZValues[i];
            }

            for (int i = 0; i < n; i++)
            {
                int src = n - 1 - i;
                int dst = n + i;
                stationXy[dst * 2] = rightXyz[src * 3];
                stationXy[dst * 2 + 1] = rightXyz[src * 3 + 1];
                normals[dst * 2] = center.TangentY[src];
                normals[dst * 2 + 1] = -center.TangentX[src];
                footZ[dst] = center.ZValues[src];
            }

            BatterStripBuilder.DaylightLoop loop = BatterStripBuilder.BuildDaylightLoop(
                stationXy, sideCount, isClosed: true, normals, footZ,
                path.SlopeAngleDeg, path.MaxDistance, terrain, barriers, tolerance);

            foreach (BatterStripBuilder.DaylightStation station in loop.Stations)
            {
                if (station.Status == BatterStripBuilder.DaylightStatus.NonDaylighting)
                    nonDaylightingStations++;
            }

            // Build the daylight polygon: left-side day points, a rounded end arc, right-side day
            // points, a rounded start arc. Arcs connect the side day points smoothly (no pinch).
            double[] dayXy = loop.DaylightXy();
            var dayZ = new double[loop.Count];
            for (int i = 0; i < loop.Count; i++)
                dayZ[i] = loop.Stations[i].DayZ;

            var polyXy = new List<double>(dayXy.Length + 32);
            for (int i = 0; i < n; i++)
            {
                polyXy.Add(dayXy[i * 2]);
                polyXy.Add(dayXy[i * 2 + 1]);
            }

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

            double[] daylightPolyXy = polyXy.ToArray();
            if (GradingGeometry2D.ClosedPolylineSelfIntersects(daylightPolyXy, daylightPolyXy.Length / 2))
            {
                errorMessage = "Grade Path daylight loop self-intersects; deferring to constraint-first path.";
                return null;
            }

            corridors.Add(new PathCorridor(leftXyz, rightXyz, n, daylightPolyXy, spacing, loop));
            daylightLoopsXy.Add(daylightPolyXy);
            outputPolylines.Add(new OutputPolyline(leftXyz, n, isClosed: false));
            outputPolylines.Add(new OutputPolyline(rightXyz, n, isClosed: false));
        }

        // Interacting corridors need junction ownership; defer those.
        for (int i = 0; i < daylightLoopsXy.Count; i++)
        {
            for (int j = i + 1; j < daylightLoopsXy.Count; j++)
            {
                if (GradingGeometry2D.PolygonsOverlap(daylightLoopsXy[i], daylightLoopsXy[j]))
                {
                    errorMessage = "Grade Path corridors interact; deferring to constraint-first path.";
                    return null;
                }
            }
        }

        GradedRegionAssembler.SplitOutsideResult split = GradedRegionAssembler.SplitOutside(
            vertices, vertexCount, faces, faceCount, daylightLoopsXy, tolerance);
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
                boundaryXyz, boundaryLoop.Length, corridors[corridorIndex], paths, hardConstraints, terrain, tolerance);
            if (fill is null)
            {
                errorMessage = "Grade Path hole fill failed; deferring to constraint-first path.";
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
            errorMessage = "Grade Path unified assembly produced non-manifold, open, or off-terrain topology.";
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

        int steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) * radius / Math.Max(spacing, 1e-6)));
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
        int boundaryCount,
        PathCorridor corridor,
        PathDefinition[] paths,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> hardConstraints,
        TerrainFaceGrid terrain,
        double tolerance)
    {
        double weldTol = Math.Max(tolerance, 1e-6);
        double inverseCell = 1.0 / weldTol;
        var xyList = new List<double>();
        var inputZ = new List<double>();
        var pointIndex = new Dictionary<(long, long), int>();
        var segments = new List<(int a, int b)>();

        int AddPoint(double x, double y, double z)
        {
            var key = ((long)Math.Round(x * inverseCell), (long)Math.Round(y * inverseCell));
            if (pointIndex.TryGetValue(key, out int existing))
                return existing;

            int index = xyList.Count / 2;
            xyList.Add(x);
            xyList.Add(y);
            inputZ.Add(z);
            pointIndex[key] = index;
            return index;
        }

        // Conformed boundary first (indices [0, boundaryPointCount) are perimeter vertices to pin).
        int firstB = AddPoint(boundaryXyz[0], boundaryXyz[1], boundaryXyz[2]);
        int prevB = firstB;
        for (int i = 1; i < boundaryCount; i++)
        {
            int cur = AddPoint(boundaryXyz[i * 3], boundaryXyz[i * 3 + 1], boundaryXyz[i * 3 + 2]);
            if (cur != prevB)
                segments.Add((prevB, cur));
            prevB = cur;
        }

        if (prevB != firstB)
            segments.Add((prevB, firstB));

        int boundaryPointCount = xyList.Count / 2;

        int n = corridor.N;
        // Road edges (left + right) as open constraints + end-cap edges → crisp road top boundary.
        int[] leftIdx = new int[n];
        int[] rightIdx = new int[n];
        for (int i = 0; i < n; i++)
        {
            leftIdx[i] = AddPoint(corridor.LeftXyz[i * 3], corridor.LeftXyz[i * 3 + 1], 0.0);
            rightIdx[i] = AddPoint(corridor.RightXyz[i * 3], corridor.RightXyz[i * 3 + 1], 0.0);
        }

        for (int i = 0; i < n - 1; i++)
        {
            if (leftIdx[i] != leftIdx[i + 1]) segments.Add((leftIdx[i], leftIdx[i + 1]));
            if (rightIdx[i] != rightIdx[i + 1]) segments.Add((rightIdx[i], rightIdx[i + 1]));
        }

        if (leftIdx[n - 1] != rightIdx[n - 1]) segments.Add((leftIdx[n - 1], rightIdx[n - 1]));
        if (leftIdx[0] != rightIdx[0]) segments.Add((leftIdx[0], rightIdx[0]));

        // Batter row seeds from the side batter strip (density/slope).
        if (corridor.Loop.HasBatter)
        {
            BatterStripBuilder.BatterStrip strip = BatterStripBuilder.BuildBatterStrip(corridor.Loop, corridor.Spacing);
            for (int i = 0; i < strip.VertexCount; i++)
                AddPoint(strip.Vertices[i * 3], strip.Vertices[i * 3 + 1], 0.0);
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

        // Pin the conformed boundary exactly to terrain so it welds to the kept terrain.
        for (int i = 0; i < vc; i++)
        {
            int sourceId = extracted.SourceIds[i];
            if (sourceId >= 0 && sourceId < boundaryPointCount)
                graded[i * 3 + 2] = inputZ[sourceId];
        }

        return new GradedRegionAssembler.SubMesh
        {
            Vertices = graded,
            VertexCount = vc,
            Faces = extracted.Faces,
            FaceCount = extracted.FaceCount
        };
    }
}
