using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

public static partial class PadGrader
{
    private readonly record struct PadBuild(
        PadBoundary Pad,
        ConstraintLoop PadLoop,
        double SegmentLength,
        BatterStripBuilder.DaylightLoop Loop,
        double[] DaylightXy,
        GradedRegionAssembler.SubMesh? Batter);

    /// <summary>
    /// Grades pads by building explicit batter geometry (the Civil 3D / InRoads grading-object
    /// model): each pad footprint is rayed out to daylight, an explicit ruled side-slope strip is
    /// built from the footprint to the daylight loop, the pad top is filled at the pad plane, and
    /// the pieces are welded into the terrain after the terrain inside each daylight loop is carved
    /// away. The slope is exact by construction and independent of terrain density. Interacting pads
    /// (overlapping daylight) are resolved together over their unified region by priority ownership.
    /// Returns null (without throwing) when it cannot produce a watertight, manifold result, so the
    /// caller can fall back to the legacy constraint-first path.
    /// </summary>
    private static GradingResult? GradeWithExplicitBatter(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PadBoundary[] pads,
        LockCurve[] lockCurves,
        double modelTolerance,
        double terrainDetailSize,
        out string? errorMessage)
    {
        errorMessage = null;

        if (pads.Length == 0)
        {
            errorMessage = "Grade Pad received no pads.";
            return null;
        }

        double tolerance = GradingTolerances.ModelToleranceOrDefault(modelTolerance);
        var terrain = new TerrainFaceGrid(vertices, vertexCount, faces, faceCount);
        PreparedBarriers barriers = GradingBarriers.BuildFromLockCurves(lockCurves);

        var builds = new List<PadBuild>(pads.Length);
        int nonDaylightingStations = 0;

        foreach (PadBoundary pad in pads)
        {
            PadBuild? build = BuildPad(pad, terrain, barriers, tolerance, terrainDetailSize, ref nonDaylightingStations, out errorMessage);
            if (build is null)
                return null;

            builds.Add(build.Value);
        }

        // Group pads whose daylight regions overlap; each group is resolved together.
        int[] group = GroupByDaylightOverlap(builds);
        var groups = new Dictionary<int, List<PadBuild>>();
        for (int i = 0; i < builds.Count; i++)
        {
            if (!groups.TryGetValue(group[i], out List<PadBuild>? members))
            {
                members = new List<PadBuild>();
                groups[group[i]] = members;
            }

            members.Add(builds[i]);
        }

        // Union each interacting group's daylight loops into carve region(s) for the terrain split.
        var groupLoops = new List<double[]>(groups.Count);
        foreach (List<PadBuild> members in groups.Values)
        {
            List<double[]>? unionLoops = UnionGroupDaylight(members, tolerance);
            if (unionLoops is null)
            {
                errorMessage = "Grade Pad interacting pad group could not be unioned; deferring to constraint-first path.";
                return null;
            }

            // A group whose members only touch unions into several disjoint loops; carve each one.
            foreach (double[] unionLoop in unionLoops)
                groupLoops.Add(unionLoop);
        }

        // Split the terrain along the carve regions, preserving terrain detail everywhere else.
        GradedRegionAssembler.SplitOutsideResult split = GradedRegionAssembler.SplitOutside(
            vertices, vertexCount, faces, faceCount, groupLoops, tolerance);
        if (!split.Success)
        {
            errorMessage = split.Warning ?? "Grade Pad terrain split failed.";
            return null;
        }

        // Fill each conformed hole with every pad whose footprint lies inside it. Assigning by
        // geometry (not by group) is what keeps adjacent pads correct: when two pads' daylight
        // regions sit closer than a terrain face, the split merges them into one conformed hole, and
        // that hole must be filled by ALL the pads it contains — otherwise the uncovered pad's region
        // overlaps its neighbour's fill and the weld goes non-manifold.
        var fills = new List<GradedRegionAssembler.SubMesh>(split.HoleBoundaryLoops.Count);
        foreach (int[] boundaryLoop in split.HoleBoundaryLoops)
        {
            double[] boundaryXyz = ExtractLoopXyz(split.Vertices, boundaryLoop);
            List<PadBuild> holeMembers = PadsInsideBoundary(builds, boundaryXyz, boundaryLoop.Length);
            if (holeMembers.Count == 0)
            {
                errorMessage = "Grade Pad could not match a hole boundary to any pad; deferring.";
                return null;
            }

            GradedRegionAssembler.SubMesh? fill = BuildHoleFill(
                boundaryXyz, boundaryLoop.Length, holeMembers, terrain, barriers, tolerance);
            if (fill is null)
            {
                errorMessage = "Grade Pad hole fill failed; deferring to constraint-first path.";
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
            errorMessage = GradedRegionAssembler.DescribeWeldTopologyFailure("Grade Pad", topology, terrainTopology);
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
            "Grade Pad topology mode: explicit batter construction (ruled side-slopes welded into terrain).";
        var diagnostics = new List<string> { modeMessage };
        var structured = new List<GradingDiagnostic>
        {
            GradingDiagnostic.Information("grade_pad.topology.mode", modeMessage, operation: "grade_pad")
        };

        if (nonDaylightingStations > 0)
        {
            string message =
                $"Grade Pad batter did not reach existing ground at {nonDaylightingStations} station(s); the slope was clamped to the search extent there.";
            diagnostics.Add(message);
            structured.Add(GradingDiagnostic.Warning("grade_pad.daylight.incomplete", message, operation: "grade_pad"));
        }

        OutputPolyline[] outputPolylines = BuildPadBoundaryPolylines(pads).ToArray();
        IReadOnlyList<GradingPatch> patchSummaries = BuildPadPatchSummaries(pads);

        return GradingResultBuilder.BuildFromXyz(
            original,
            graded,
            assembled.VertexCount,
            assembled.Faces,
            assembled.FaceCount,
            outputPolylines,
            diagnostics,
            patchSummaries,
            structured);
    }

    private static PadBuild? BuildPad(
        PadBoundary pad,
        TerrainFaceGrid terrain,
        PreparedBarriers barriers,
        double tolerance,
        double terrainDetailSize,
        ref int nonDaylightingStations,
        out string? errorMessage)
    {
        errorMessage = null;

        double[] boundaryDistances = ComputePadBoundaryDistances(pad.XyVertices, pad.VertexCount, terrain, pad);
        double maxReach = 0.0;
        foreach (double distance in boundaryDistances)
            maxReach = Math.Max(maxReach, distance);

        // Density: scale spacing with the batter reach so the strip is ~3 rows of roughly square
        // triangles, independent of reach. (The previous Min(_, terrainDetailSize) collapsed spacing
        // to the terrain detail size ~0.25, producing ~30 redundant radial rows on a planar batter.)
        double segmentLength = Math.Max(maxReach / 3.0, Math.Max(terrainDetailSize, 1.0));

        ConstraintLoop padLoop = BuildClosedConstraintLoop(pad.XyVertices, pad.VertexCount, segmentLength, tolerance);
        if (padLoop.VertexCount < 3)
        {
            errorMessage = "Grade Pad footprint collapsed below three vertices.";
            return null;
        }

        int cornerFanSegments = pad.CornerFanSegments > 0 ? pad.CornerFanSegments : 6;
        BatterStripBuilder.FootprintStations stations =
            BatterStripBuilder.BuildClosedFootprintStations(padLoop.XyVertices, padLoop.VertexCount, cornerFanSegments);

        BatterStripBuilder.DaylightLoop loop = BatterStripBuilder.BuildDaylightLoop(
            stations.Xy,
            stations.Count,
            isClosed: true,
            outwardNormals: stations.Normals,
            footprintZ: pad.EvaluateZ,
            cutSlopeAngleDeg: pad.SlopeAngleDeg,
            fillSlopeAngleDeg: pad.FillSlopeAngleDeg,
            maxDistance: pad.MaxDistance,
            terrain: terrain,
            barriers: barriers,
            tolerance: tolerance);

        foreach (BatterStripBuilder.DaylightStation station in loop.Stations)
        {
            if (station.Status == BatterStripBuilder.DaylightStatus.NonDaylighting)
                nonDaylightingStations++;
        }

        double[] daylightXy = loop.DaylightXy();

        // Self-overlapping shoulders (narrow or concave pads whose opposite batters collide) fold
        // the daylight loop back across the footprint. The simple ruled strip cannot resolve that
        // collision, so defer to the constraint-first path.
        // A daylight point that lands strictly inside the footprint means the shoulder folded back
        // across the pad. Stations where terrain already sits at pad grade are Flat: their daylight
        // point collapses ONTO the footprint boundary (reach ~ 0), which is not a fold — exclude
        // those by requiring genuine interior penetration beyond a small margin.
        double foldMargin = Math.Max(tolerance, segmentLength * 0.05);
        if (ClosedPolylineHasSelfIntersection(daylightXy, loop.Count) ||
            AnyPointInsidePolygon(daylightXy, loop.Count, pad.XyVertices, pad.VertexCount, foldMargin))
        {
            errorMessage = "Grade Pad batter shoulders self-overlap; deferring to constraint-first path.";
            return null;
        }

        // The batter strip is built only to harvest its row vertices as interior seeds for the hole
        // fill (controls slope/density); it is not welded directly.
        GradedRegionAssembler.SubMesh? batter = null;
        if (loop.HasBatter)
        {
            BatterStripBuilder.BatterStrip strip = BatterStripBuilder.BuildBatterStrip(loop, segmentLength);
            if (strip.FaceCount > 0)
            {
                batter = new GradedRegionAssembler.SubMesh
                {
                    Vertices = strip.Vertices,
                    VertexCount = strip.VertexCount,
                    Faces = strip.Faces,
                    FaceCount = strip.FaceCount
                };
            }
        }

        return new PadBuild(pad, padLoop, segmentLength, loop, daylightXy, batter);
    }

    /// <summary>
    /// Unions an interacting group's per-pad daylight polygons. Returns the resulting carve loop(s):
    /// usually one, but a group whose members only touch (rather than truly overlap) unions into
    /// several disjoint loops, each carved and filled independently. Returns null only when the union
    /// contains an actual hole (mixed winding / annulus), which this resolver does not handle.
    /// </summary>
    private static List<double[]>? UnionGroupDaylight(List<PadBuild> group, double tolerance)
    {
        if (group.Count == 1)
            return new List<double[]> { group[0].DaylightXy };

        var polys = new List<double[]>(group.Count);
        foreach (PadBuild build in group)
            polys.Add(build.DaylightXy);

        if (!ClipperGeometry.TryUnionClosedLoops(polys, tolerance, out List<double[]> unionLoops) || unionLoops.Count == 0)
            return null;

        // A hole shows up as a loop wound opposite to the outer loops; an annulus carve is beyond
        // this resolver, so defer it. Disjoint loops all share the outer winding and are fine.
        int positive = 0, negative = 0;
        foreach (double[] loop in unionLoops)
        {
            double area = ClipperGeometry.SignedArea(loop);
            if (area > 0.0) positive++;
            else if (area < 0.0) negative++;
        }

        if (positive > 0 && negative > 0)
            return null;

        var loops = unionLoops.Where(loop => loop.Length >= 6).ToList();
        return loops.Count > 0 ? loops : null;
    }

    /// <summary>Returns the pads whose footprint centroid falls inside the given conformed boundary.</summary>
    private static List<PadBuild> PadsInsideBoundary(List<PadBuild> builds, double[] boundaryXyz, int boundaryCount)
    {
        var boundaryXy = new double[boundaryCount * 2];
        for (int i = 0; i < boundaryCount; i++)
        {
            boundaryXy[i * 2] = boundaryXyz[i * 3];
            boundaryXy[i * 2 + 1] = boundaryXyz[i * 3 + 1];
        }

        var inside = new List<PadBuild>();
        foreach (PadBuild build in builds)
        {
            double cx = 0.0, cy = 0.0;
            int n = build.Pad.VertexCount;
            for (int i = 0; i < n; i++)
            {
                cx += build.Pad.XyVertices[i * 2];
                cy += build.Pad.XyVertices[i * 2 + 1];
            }

            if (n > 0 && PointInPolygon(cx / n, cy / n, boundaryXy, boundaryCount))
                inside.Add(build);
        }

        return inside;
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

    /// <summary>
    /// Triangulates a carved hole: the conformed boundary (outer, terrain Z) and footprint loops as
    /// constraints, batter-row points as interior seeds; assigns pad-top + batter-section Z and pins
    /// the boundary to the exact conformed terrain Z so it welds to the kept terrain.
    /// </summary>
    private static GradedRegionAssembler.SubMesh? BuildHoleFill(
        double[] boundaryXyz,
        int boundaryCount,
        List<PadBuild> group,
        TerrainFaceGrid terrain,
        PreparedBarriers barriers,
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

        void AddClosed(double[] pts, bool xyz, int count)
        {
            if (count < 3)
                return;

            int stride = xyz ? 3 : 2;
            int first = AddPoint(pts[0], pts[1], xyz ? pts[2] : 0.0);
            int previous = first;
            for (int i = 1; i < count; i++)
            {
                int current = AddPoint(pts[i * stride], pts[i * stride + 1], xyz ? pts[i * stride + 2] : 0.0);
                if (current != previous)
                    segments.Add((previous, current));
                previous = current;
            }

            if (previous != first)
                segments.Add((previous, first));
        }

        // Conformed boundary first: input indices [0, boundaryPointCount) are perimeter vertices.
        AddClosed(boundaryXyz, xyz: true, boundaryCount);
        int boundaryPointCount = xyList.Count / 2;

        foreach (PadBuild build in group)
            AddClosed(build.PadLoop.XyVertices, xyz: false, build.PadLoop.VertexCount);

        // Batter-row seeds (density/slope). Keep only seeds inside the conformed boundary: when the
        // daylight was clipped (off-terrain or against an adjacent pad) some rows fall outside it, and
        // triangulating those would push fill faces past the boundary and overlap the kept terrain.
        var boundaryXy = new double[boundaryPointCount * 2];
        for (int i = 0; i < boundaryPointCount; i++)
        {
            boundaryXy[i * 2] = xyList[i * 2];
            boundaryXy[i * 2 + 1] = xyList[i * 2 + 1];
        }

        foreach (PadBuild build in group)
        {
            if (build.Batter is null)
                continue;

            double[] sv = build.Batter.Vertices;
            for (int i = 0; i < build.Batter.VertexCount; i++)
            {
                double sx = sv[i * 3];
                double sy = sv[i * 3 + 1];
                if (PointInPolygon(sx, sy, boundaryXy, boundaryPointCount))
                    AddPoint(sx, sy, 0.0);
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
        var graded = new double[vc * 3];
        var original = new double[vc * 3];
        for (int i = 0; i < vc; i++)
        {
            double x = extracted.Xy[i * 2];
            double y = extracted.Xy[i * 2 + 1];
            double z = terrain.InterpolateZ(x, y);
            graded[i * 3] = x; graded[i * 3 + 1] = y; graded[i * 3 + 2] = z;
            original[i * 3] = x; original[i * 3 + 1] = y; original[i * 3 + 2] = z;
        }

        var groupPads = new PadBoundary[group.Count];
        for (int i = 0; i < group.Count; i++)
            groupPads[i] = group[i].Pad;

        ApplyGradingToVerticesWithSections(
            graded, original, vc, groupPads, barriers, terrain,
            hasBoundaryLoop: false, boundaryLoop: Array.Empty<double>(), boundaryVertexCount: 0,
            tolerance, keepShoulderOnBatterPlane: false, defaultCornerFanSegments: 6);

        // Pin the conformed boundary exactly to terrain so it welds to the kept terrain seam.
        for (int i = 0; i < vc; i++)
        {
            int sourceId = extracted.SourceIds[i];
            if (sourceId >= 0 && sourceId < boundaryPointCount)
                graded[i * 3 + 2] = inputZ[sourceId];
        }

        // Clip the fill to the conformed boundary: a concave or merged boundary can make the
        // triangulator span past it (toward the convex hull), and those faces would overlap the kept
        // terrain and weld non-manifold. Keep only faces whose centroid lies inside the boundary.
        int[] clippedFaces = ClipFacesToBoundary(graded, extracted.Faces, extracted.FaceCount, boundaryXy, boundaryPointCount);

        return new GradedRegionAssembler.SubMesh
        {
            Vertices = graded,
            VertexCount = vc,
            Faces = clippedFaces,
            FaceCount = clippedFaces.Length / 3
        };
    }

    /// <summary>Drops triangles whose centroid falls outside the conformed boundary polygon.</summary>
    private static int[] ClipFacesToBoundary(double[] vertices, int[] faces, int faceCount, double[] boundaryXy, int boundaryCount)
    {
        var kept = new List<int>(faceCount * 3);
        for (int f = 0; f < faceCount; f++)
        {
            int a = faces[f * 3], b = faces[f * 3 + 1], c = faces[f * 3 + 2];
            double ax = vertices[a * 3], ay = vertices[a * 3 + 1];
            double bx = vertices[b * 3], by = vertices[b * 3 + 1];
            double cxv = vertices[c * 3], cyv = vertices[c * 3 + 1];

            // Drop degenerate (near-zero area) slivers the triangulator can leave at the boundary.
            double area2 = Math.Abs(((bx - ax) * (cyv - ay)) - ((by - ay) * (cxv - ax)));
            if (area2 <= 1e-7)
                continue;

            double cx = (ax + bx + cxv) / 3.0;
            double cy = (ay + by + cyv) / 3.0;
            if (PointInPolygon(cx, cy, boundaryXy, boundaryCount))
            {
                kept.Add(a);
                kept.Add(b);
                kept.Add(c);
            }
        }

        return kept.Count > 0 ? kept.ToArray() : faces;
    }

    /// <summary>Union-find grouping of pads whose daylight polygons overlap. Returns a group id per pad.</summary>
    private static int[] GroupByDaylightOverlap(List<PadBuild> builds)
    {
        int n = builds.Count;
        var parent = new int[n];
        for (int i = 0; i < n; i++)
            parent[i] = i;

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                if (GradingGeometry2D.PolygonsOverlap(builds[i].DaylightXy, builds[j].DaylightXy))
                    parent[Find(i)] = Find(j);
            }
        }

        var groupId = new int[n];
        for (int i = 0; i < n; i++)
            groupId[i] = Find(i);

        return groupId;
    }

    private static bool AnyPointInsidePolygon(
        double[] pointsXy, int pointCount, double[] polygonXy, int polygonCount, double interiorMargin)
    {
        for (int i = 0; i < pointCount; i++)
        {
            double px = pointsXy[i * 2];
            double py = pointsXy[i * 2 + 1];
            if (PointInPolygon(px, py, polygonXy, polygonCount) &&
                DistToPolygon(px, py, polygonXy, polygonCount) > interiorMargin)
            {
                return true;
            }
        }

        return false;
    }
}
