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

    /// <summary>
    /// Grades pads by conforming the terrain to the daylight loops and KEEPING the entire conformed
    /// mesh (no carve/fill/weld). <see cref="GradedRegionAssembler.SplitConform"/> subdivides the
    /// terrain in place along the daylight loops and returns every resulting face; we then reassign Z
    /// by section over that one mesh. Because nothing is restitched, the result is watertight and
    /// manifold by construction — there is no seam to crack. Batter slopes follow the conformed
    /// terrain density rather than a clean ruled strip, so they can be slightly faceted on coarse
    /// terrain; this is a watertight fallback tier between the exact-slope explicit batter (primary)
    /// and the spiky constraint-first rebuild (last resort). Returns null (deferring) when the
    /// area splitter cannot produce a manifold conformed mesh for the scene.
    /// </summary>
    private static GradingResult? GradeWithSplitKeep(
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

        // Union interacting groups' daylight loops into the carve regions to conform the terrain to.
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

        var conformLoops = new List<double[]>(groups.Count + pads.Length);
        foreach (List<PadBuild> members in groups.Values)
        {
            List<double[]>? unionLoops = UnionGroupDaylight(members, tolerance);
            if (unionLoops is null)
            {
                errorMessage = "Grade Pad interacting pad group could not be unioned; deferring.";
                return null;
            }

            foreach (double[] unionLoop in unionLoops)
                conformLoops.Add(unionLoop);
        }

        // Also conform to each pad FOOTPRINT, not just its daylight. Conforming to the daylight alone
        // leaves the pad interior spanned by coarse terrain triangles that never get flattened (a pad
        // smaller than the local terrain triangle ends up with no flat top at all). Inserting the
        // footprint puts its boundary into the mesh at pad grade, so the pad top is flat.
        foreach (PadBuild build in builds)
        {
            if (build.Pad.VertexCount >= 3)
            {
                var footprint = new double[build.Pad.VertexCount * 2];
                Array.Copy(build.Pad.XyVertices, footprint, footprint.Length);
                conformLoops.Add(footprint);
            }
        }

        // Conform the terrain to the daylight + footprint loops and keep the whole mesh (no weld).
        MeshAreaSplitter.SplitResult? conformed = GradedRegionAssembler.SplitConform(
            vertices, vertexCount, faces, faceCount, conformLoops, tolerance);
        if (conformed is null)
        {
            errorMessage = "Grade Pad terrain conform (split-keep) failed; deferring.";
            return null;
        }

        // Watertight/manifold by construction — but gate anyway: the area splitter can go non-manifold
        // on dense/nested loops, and we must defer cleanly to the legacy path rather than emit it.
        MeshTopologyValidator.BoundaryGraphAnalysis topology =
            MeshTopologyValidator.AnalyzeBoundaryGraph(conformed.Faces, conformed.FaceCount);
        MeshTopologyValidator.BoundaryGraphAnalysis terrainTopology =
            MeshTopologyValidator.AnalyzeBoundaryGraph(faces, faceCount);
        if (topology.NonManifoldEdgeCount > 0 ||
            topology.HasOpenBoundaryChains ||
            topology.BoundaryComponentCount > terrainTopology.BoundaryComponentCount)
        {
            errorMessage = GradedRegionAssembler.DescribeWeldTopologyFailure("Grade Pad split-keep", topology, terrainTopology);
            return null;
        }

        int vc = conformed.VertexCount;
        double[] graded = new double[vc * 3];
        Array.Copy(conformed.Vertices, graded, vc * 3);
        var original = new double[vc * 3];
        for (int i = 0; i < vc; i++)
        {
            double x = conformed.Vertices[i * 3];
            double y = conformed.Vertices[i * 3 + 1];
            double z = terrain.InterpolateZ(x, y);
            graded[i * 3] = x; graded[i * 3 + 1] = y;
            original[i * 3] = x; original[i * 3 + 1] = y; original[i * 3 + 2] = z;
            graded[i * 3 + 2] = z;
        }

        ApplyGradingToVerticesWithSections(
            graded, original, vc, pads, barriers, terrain,
            hasBoundaryLoop: false, boundaryLoop: Array.Empty<double>(), boundaryVertexCount: 0,
            tolerance, keepShoulderOnBatterPlane: false, defaultCornerFanSegments: 6);

        // A watertight conform that did not actually flatten a pad top (the footprint was too small to
        // pick up interior mesh, or the splitter dropped its boundary) is geometrically wrong — defer
        // to the region-remesh path, which rebuilds the pad interior densely and always flattens.
        if (!PadTopsAreFlat(graded, conformed.Faces, conformed.FaceCount, pads, Math.Max(tolerance, 1e-3)))
        {
            errorMessage = "Grade Pad split-keep did not flatten every pad top; deferring to region remesh.";
            return null;
        }

        const string modeMessage =
            "Grade Pad topology mode: terrain conform (split-keep — daylight + footprint loops conformed in place, watertight by construction).";
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
            vc,
            conformed.Faces,
            conformed.FaceCount,
            outputPolylines,
            diagnostics,
            patchSummaries,
            structured);
    }

    /// <summary>
    /// True when every triangle whose three vertices fall inside exactly one pad footprint sits on
    /// that pad's plane (within <paramref name="zTolerance"/>). A pad with NO fully-interior triangle
    /// is treated as not flattened, since its top is then spanned by ungraded terrain faces.
    /// </summary>
    private static bool PadTopsAreFlat(double[] vertices, int[] faces, int faceCount, PadBoundary[] pads, double zTolerance)
    {
        var interiorFaceCount = new int[pads.Length];
        for (int f = 0; f < faceCount; f++)
        {
            int a = faces[f * 3], b = faces[f * 3 + 1], c = faces[f * 3 + 2];
            for (int p = 0; p < pads.Length; p++)
            {
                PadBoundary pad = pads[p];
                if (!PointInPolygon(vertices[a * 3], vertices[a * 3 + 1], pad.XyVertices, pad.VertexCount) ||
                    !PointInPolygon(vertices[b * 3], vertices[b * 3 + 1], pad.XyVertices, pad.VertexCount) ||
                    !PointInPolygon(vertices[c * 3], vertices[c * 3 + 1], pad.XyVertices, pad.VertexCount))
                {
                    continue;
                }

                interiorFaceCount[p]++;
                foreach (int idx in stackalloc[] { a, b, c })
                {
                    double expected = pad.EvaluateZ(vertices[idx * 3], vertices[idx * 3 + 1]);
                    if (Math.Abs(vertices[idx * 3 + 2] - expected) > zTolerance)
                        return false;
                }
            }
        }

        for (int p = 0; p < pads.Length; p++)
        {
            if (interiorFaceCount[p] == 0)
                return false;
        }

        return true;
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
        // A narrow or concave pad whose opposite batters collide folds the daylight loop into a
        // figure-8. Resolve that self-overlap into the clean outer envelope with a Clipper union
        // (as the path corridor does) rather than deferring the whole batch; batter seeds that fall
        // outside the envelope are dropped later in BuildHoleFill.
        int daylightCount = loop.Count;
        if (ClosedPolylineHasSelfIntersection(daylightXy, daylightCount))
        {
            if (ClipperGeometry.TryUnionClosedLoops(new[] { daylightXy }, tolerance, out List<double[]> cleanedLoops) &&
                ClipperGeometry.TryPickLargestLoop(cleanedLoops, out double[] envelope) &&
                !ClosedPolylineHasSelfIntersection(envelope, envelope.Length / 2))
            {
                daylightXy = envelope;
                daylightCount = envelope.Length / 2;
            }
            else
            {
                errorMessage = "Grade Pad batter shoulders self-overlap and could not be resolved; deferring to constraint-first path.";
                return null;
            }
        }

        // A daylight point that still lands strictly inside the footprint (beyond a small margin) means
        // the shoulder folded back across the pad top; the ruled strip cannot resolve that, so defer.
        double foldMargin = Math.Max(tolerance, segmentLength * 0.05);
        if (AnyPointInsidePolygon(daylightXy, daylightCount, pad.XyVertices, pad.VertexCount, foldMargin))
        {
            errorMessage = "Grade Pad batter shoulders fold across the pad; deferring to constraint-first path.";
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

        // Keep only the fill interior bounded by the conformed loop. Triangle.NET fills the convex
        // hull, so on a concave or merged boundary it spans faces past the loop (into concavities and
        // the hull skirt); those overlap the kept terrain and weld non-manifold. A centroid-in-polygon
        // clip is unreliable there — a spilled triangle in a deep concavity can have its centroid back
        // inside the loop. Instead extract the interior by flood fill bounded by the boundary segments:
        // a face is exterior iff it is reachable from a non-boundary naked (convex-hull) edge without
        // crossing a boundary segment. The result's boundary is EXACTLY the conformed loop, so it welds
        // to the kept terrain with no spill (overlap) or recession (gap).
        int[] interiorFaces = ExtractFillInterior(graded, extracted.Faces, extracted.FaceCount, extracted.SourceIds, boundaryPointCount);

        return new GradedRegionAssembler.SubMesh
        {
            Vertices = graded,
            VertexCount = vc,
            Faces = interiorFaces,
            FaceCount = interiorFaces.Length / 3
        };
    }

    private static long FillEdgeKey(int a, int b) =>
        a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

    /// <summary>
    /// Returns the faces inside the conformed boundary loop (input vertices [0, boundaryCount) in
    /// consecutive order). Triangle.NET fills the convex hull, so a concave boundary leaves hull-skirt
    /// faces outside the loop that must be dropped. Primary method is a segment-aware flood fill (a face
    /// is exterior iff reachable from a non-boundary naked edge without crossing a boundary segment),
    /// which is exact at concavities. If the flood leaks — a boundary segment that Triangle did not
    /// realise as a single mesh edge breaks the wall and the flood drains the interior — it would drop
    /// most of the fill; that is detected (kept &lt; half the faces) and the fill falls back to a
    /// leak-proof centroid-in-boundary test. Degenerate near-zero-area faces are always dropped.
    /// </summary>
    private static int[] ExtractFillInterior(double[] vertices, int[] faces, int faceCount, int[] sourceIds, int boundaryCount)
    {
        // Boundary segment edges, keyed on extracted vertex indices.
        var sourceToVertex = new Dictionary<int, int>(boundaryCount);
        for (int i = 0; i < sourceIds.Length; i++)
        {
            int s = sourceIds[i];
            if (s >= 0 && s < boundaryCount)
                sourceToVertex[s] = i;
        }

        var walls = new HashSet<long>();
        for (int i = 0; i < boundaryCount; i++)
        {
            if (sourceToVertex.TryGetValue(i, out int u) &&
                sourceToVertex.TryGetValue((i + 1) % boundaryCount, out int v))
            {
                walls.Add(FillEdgeKey(u, v));
            }
        }

        // Edge -> incident faces.
        var edgeFaces = new Dictionary<long, List<int>>(faceCount * 3);
        void Incident(int a, int b, int f)
        {
            long k = FillEdgeKey(a, b);
            if (!edgeFaces.TryGetValue(k, out List<int>? list))
            {
                list = new List<int>(2);
                edgeFaces[k] = list;
            }

            list.Add(f);
        }

        for (int f = 0; f < faceCount; f++)
        {
            int a = faces[f * 3], b = faces[f * 3 + 1], c = faces[f * 3 + 2];
            Incident(a, b, f);
            Incident(b, c, f);
            Incident(c, a, f);
        }

        // Seed exterior from faces with a naked edge that is NOT a boundary segment (the hull skirt);
        // at convex parts of the loop the naked edge IS a boundary segment, so interior faces there are
        // not seeded. Flood across non-boundary shared edges.
        var exterior = new bool[faceCount];
        var stack = new Stack<int>();
        for (int f = 0; f < faceCount; f++)
        {
            int a = faces[f * 3], b = faces[f * 3 + 1], c = faces[f * 3 + 2];
            foreach ((int u, int v) in stackalloc[] { (a, b), (b, c), (c, a) })
            {
                long k = FillEdgeKey(u, v);
                if (edgeFaces[k].Count == 1 && !walls.Contains(k))
                {
                    exterior[f] = true;
                    stack.Push(f);
                    break;
                }
            }
        }

        while (stack.Count > 0)
        {
            int f = stack.Pop();
            int a = faces[f * 3], b = faces[f * 3 + 1], c = faces[f * 3 + 2];
            foreach ((int u, int v) in stackalloc[] { (a, b), (b, c), (c, a) })
            {
                long k = FillEdgeKey(u, v);
                if (walls.Contains(k))
                    continue;

                foreach (int nf in edgeFaces[k])
                {
                    if (nf != f && !exterior[nf])
                    {
                        exterior[nf] = true;
                        stack.Push(nf);
                    }
                }
            }
        }

        int interiorCount = 0;
        for (int f = 0; f < faceCount; f++)
            if (!exterior[f])
                interiorCount++;

        // Leak guard: if the flood drained most of the interior (a broken wall), fall back to the
        // leak-proof centroid-in-boundary test against the reconstructed boundary polygon.
        if (interiorCount * 2 < faceCount)
        {
            var boundaryPoly = new double[boundaryCount * 2];
            int built = 0;
            for (int i = 0; i < boundaryCount; i++)
            {
                if (!sourceToVertex.TryGetValue(i, out int vi))
                    continue;

                boundaryPoly[built * 2] = vertices[vi * 3];
                boundaryPoly[built * 2 + 1] = vertices[vi * 3 + 1];
                built++;
            }

            if (built >= 3)
            {
                for (int f = 0; f < faceCount; f++)
                {
                    int a = faces[f * 3], b = faces[f * 3 + 1], c = faces[f * 3 + 2];
                    double cx = (vertices[a * 3] + vertices[b * 3] + vertices[c * 3]) / 3.0;
                    double cy = (vertices[a * 3 + 1] + vertices[b * 3 + 1] + vertices[c * 3 + 1]) / 3.0;
                    exterior[f] = !PointInPolygon(cx, cy, boundaryPoly, built);
                }
            }
        }

        var kept = new List<int>(faceCount * 3);
        for (int f = 0; f < faceCount; f++)
        {
            if (exterior[f])
                continue;

            int a = faces[f * 3], b = faces[f * 3 + 1], c = faces[f * 3 + 2];
            double area2 = Math.Abs(
                ((vertices[b * 3] - vertices[a * 3]) * (vertices[c * 3 + 1] - vertices[a * 3 + 1])) -
                ((vertices[b * 3 + 1] - vertices[a * 3 + 1]) * (vertices[c * 3] - vertices[a * 3])));
            if (area2 <= 1e-7)
                continue;

            kept.Add(a);
            kept.Add(b);
            kept.Add(c);
        }

        return kept.ToArray();
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
