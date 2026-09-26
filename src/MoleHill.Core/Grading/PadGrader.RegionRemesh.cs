using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

public static partial class PadGrader
{
    /// <summary>
    /// Robust fallback that grades by REPLACING the affected region rather than stitching into it.
    /// The daylight region is grown a margin outward, every terrain face whose centroid lands inside
    /// is dropped, and the resulting hole — whose rim is made entirely of ORIGINAL terrain vertices —
    /// is remeshed with one clean quality triangulation. Elevation is then assigned as a distance
    /// field over the dense patch (pad plane inside the footprint, cut/fill batter by distance, terrain
    /// elevation out near the rim; the daylight line falls out as the iso-contour where the batter
    /// meets terrain). Because the only triangulation is the fresh interior remesh and the only weld
    /// is along original shared vertices, the result is manifold and watertight by construction — none
    /// of the conforming / near-duplicate / weld degeneracies can occur. Slopes are smooth but the
    /// daylight line is a dense-mesh iso-contour (soft), which is acceptable for a fallback. Returns
    /// null when the region cannot be carved into clean single holes.
    /// </summary>
    internal static GradingResult? GradeWithRegionRemesh(
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

        // 1. Daylight loops per pad, unioned over interacting groups (reuses the explicit-path build).
        var builds = new List<PadBuild>(pads.Length);
        int nonDaylightingStations = 0;
        foreach (PadBoundary pad in pads)
        {
            PadBuild? build = BuildPad(pad, terrain, barriers, tolerance, terrainDetailSize, ref nonDaylightingStations, out errorMessage);
            if (build is null)
                return null;

            builds.Add(build.Value);
        }

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

        var regionLoops = new List<double[]>();
        foreach (List<PadBuild> members in groups.Values)
        {
            List<double[]>? unionLoops = UnionGroupDaylight(members, tolerance);
            if (unionLoops is null)
            {
                errorMessage = "Grade Pad interacting pad group could not be unioned; deferring.";
                return null;
            }

            regionLoops.AddRange(unionLoops);
        }

        // 2. Grow each region outward so the patch boundary sits beyond the daylight, in ground that
        // still reads at terrain elevation (a clean, terrain-Z seam).
        double margin = Math.Max(terrainDetailSize * 2.0, tolerance * 1000.0);
        var offsetLoops = new List<double[]>(regionLoops.Count);
        foreach (double[] loop in regionLoops)
        {
            offsetLoops.Add(
                ClipperGeometry.TryOffsetClosedLoop(loop, margin, tolerance, out double[] grown) && grown.Length >= 6
                    ? grown
                    : loop);
        }

        // 3. Drop every terrain face touched by a grown region. The kept faces' rim is made of
        // original terrain vertices only, so there are no cut points to conform. Testing full
        // face-region overlap matters on coarse upstream meshes: a pad can cross a terrain face even
        // when none of that face's vertices fall inside the grown region.
        bool VertexInsideAnyRegion(int vertexIndex)
        {
            double px = vertices[vertexIndex * 3];
            double py = vertices[vertexIndex * 3 + 1];
            foreach (double[] loop in offsetLoops)
            {
                if (PointInPolygon(px, py, loop, loop.Length / 2))
                    return true;
            }

            return false;
        }

        bool FaceTouchesAnyRegion(int a, int b, int c)
        {
            if (VertexInsideAnyRegion(a) || VertexInsideAnyRegion(b) || VertexInsideAnyRegion(c))
                return true;

            double ax = vertices[a * 3], ay = vertices[a * 3 + 1];
            double bx = vertices[b * 3], by = vertices[b * 3 + 1];
            double cx = vertices[c * 3], cy = vertices[c * 3 + 1];
            double faceCx = (ax + bx + cx) / 3.0;
            double faceCy = (ay + by + cy) / 3.0;

            foreach (double[] loop in offsetLoops)
            {
                int loopCount = loop.Length / 2;
                if (PointInPolygon(faceCx, faceCy, loop, loopCount))
                    return true;

                for (int i = 0; i < loopCount; i++)
                {
                    double px = loop[i * 2];
                    double py = loop[i * 2 + 1];
                    if (PointInTriangle(px, py, ax, ay, bx, by, cx, cy))
                        return true;
                }

                for (int i = 0; i < loopCount; i++)
                {
                    int j = (i + 1) % loopCount;
                    double px = loop[i * 2], py = loop[i * 2 + 1];
                    double qx = loop[j * 2], qy = loop[j * 2 + 1];
                    if (SegmentsIntersect(ax, ay, bx, by, px, py, qx, qy) ||
                        SegmentsIntersect(bx, by, cx, cy, px, py, qx, qy) ||
                        SegmentsIntersect(cx, cy, ax, ay, px, py, qx, qy))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        var keptFaces = new List<int>(faceCount * 3);
        var droppedFaces = new List<int>();
        for (int f = 0; f < faceCount; f++)
        {
            int a = faces[f * 3], b = faces[f * 3 + 1], c = faces[f * 3 + 2];
            bool touchesRegion = FaceTouchesAnyRegion(a, b, c);
            if (touchesRegion)
            {
                droppedFaces.Add(a);
                droppedFaces.Add(b);
                droppedFaces.Add(c);
            }
            else
            {
                keptFaces.Add(a);
                keptFaces.Add(b);
                keptFaces.Add(c);
            }
        }

        // 4. Each hole's rim, as ordered ORIGINAL vertex indices. A carved region can PINCH — two
        // carved parts meeting at a single shared terrain vertex (a degree-4 boundary junction, a
        // "figure-eight"), e.g. where two pads' grown regions, or a region and the terrain edge, just
        // touch at one corner. The boundary walker needs every boundary vertex to have exactly two
        // boundary edges, so a single pinch makes it bail on the whole region. Repair locally: drop
        // the kept faces still touching each pinch vertex so the two parts merge into one
        // simply-connected hole and the vertex becomes interior, then retry. The handful of extra
        // dropped faces are slivers of untouched terrain the remesh restores at terrain elevation.
        List<int[]> rimLoops;
        if (droppedFaces.Count == 0)
        {
            rimLoops = new List<int[]>();
        }
        else if (!TryExtractRimsWithPinchRepair(droppedFaces, keptFaces, out rimLoops))
        {
            errorMessage = "Grade Pad region remesh could not extract clean hole boundaries; deferring.";
            return null;
        }

        // 4a. Carve guarantee for small pads. A pad smaller than the local terrain triangles can have
        // no terrain vertex inside its (offset) region, so the vertex-based drop carves NO face for it
        // and the pad is left ungraded at terrain elevation. Detect such pads — centroid not inside any
        // hole rim — and drop the single KEPT face that contains the centroid so the pad gets a hole.
        // Only uncarved pads get this targeted drop (one face each), which keeps the dropped set
        // manifold; dropping a face per footprint vertex instead would pinch the dropped set.
        if (PadCentroidsNeedingCarve(rimLoops, vertices, pads, out List<(double X, double Y)> uncarvedCentroids))
        {
            var dropFaceStarts = new HashSet<int>();
            foreach ((double cx, double cy) in uncarvedCentroids)
            {
                for (int kf = 0; kf < keptFaces.Count; kf += 3)
                {
                    if (dropFaceStarts.Contains(kf))
                        continue;

                    int a = keptFaces[kf], b = keptFaces[kf + 1], c = keptFaces[kf + 2];
                    if (PointInTriangle(cx, cy,
                            vertices[a * 3], vertices[a * 3 + 1],
                            vertices[b * 3], vertices[b * 3 + 1],
                            vertices[c * 3], vertices[c * 3 + 1]))
                    {
                        dropFaceStarts.Add(kf);
                        break;
                    }
                }
            }

            if (dropFaceStarts.Count > 0)
            {
                var newKept = new List<int>(keptFaces.Count);
                for (int kf = 0; kf < keptFaces.Count; kf += 3)
                {
                    if (dropFaceStarts.Contains(kf))
                    {
                        droppedFaces.Add(keptFaces[kf]);
                        droppedFaces.Add(keptFaces[kf + 1]);
                        droppedFaces.Add(keptFaces[kf + 2]);
                    }
                    else
                    {
                        newKept.Add(keptFaces[kf]);
                        newKept.Add(keptFaces[kf + 1]);
                        newKept.Add(keptFaces[kf + 2]);
                    }
                }

                keptFaces = newKept;
                if (!TryExtractRimsWithPinchRepair(droppedFaces, keptFaces, out rimLoops))
                {
                    errorMessage = "Grade Pad region remesh could not extract clean hole boundaries after small-pad carve; deferring.";
                    return null;
                }
            }
        }

        if (droppedFaces.Count == 0)
        {
            errorMessage = "Grade Pad region remesh dropped no faces; deferring.";
            return null;
        }

        // 4b. Partition the rim loops. A rim that encloses at least one pad footprint is a graded hole
        // to remesh. A rim with NO pad is an interior island: a patch of terrain whose own vertices all
        // sit outside the carve regions, but which is spatially enclosed by a larger graded hole (the
        // vertex-based drop leaves such patches behind when several regions merge around them). The
        // enclosing hole's remesh fills that whole area, so the island's terrain faces must be dropped
        // or they double-cover the remesh and weld non-manifold. (This is what made dense multi-pad
        // scenes defer with "hole contained no pad".)
        var gradeRims = new List<int[]>(rimLoops.Count);
        var islandPolys = new List<double[]>();
        foreach (int[] rim in rimLoops)
        {
            var poly = new double[rim.Length * 2];
            for (int i = 0; i < rim.Length; i++)
            {
                poly[i * 2] = vertices[rim[i] * 3];
                poly[i * 2 + 1] = vertices[rim[i] * 3 + 1];
            }

            if (RimEnclosesAnyPad(poly, rim.Length, pads))
                gradeRims.Add(rim);
            else
                islandPolys.Add(poly);
        }

        if (gradeRims.Count == 0)
        {
            errorMessage = "Grade Pad region remesh found no pad-bearing hole; deferring.";
            return null;
        }

        if (islandPolys.Count > 0)
        {
            var filtered = new List<int>(keptFaces.Count);
            for (int f = 0; f < keptFaces.Count; f += 3)
            {
                int a = keptFaces[f], b = keptFaces[f + 1], c = keptFaces[f + 2];
                double cx = (vertices[a * 3] + vertices[b * 3] + vertices[c * 3]) / 3.0;
                double cy = (vertices[a * 3 + 1] + vertices[b * 3 + 1] + vertices[c * 3 + 1]) / 3.0;
                bool inIsland = false;
                foreach (double[] poly in islandPolys)
                {
                    if (PointInPolygon(cx, cy, poly, poly.Length / 2))
                    {
                        inIsland = true;
                        break;
                    }
                }

                if (!inIsland)
                {
                    filtered.Add(a);
                    filtered.Add(b);
                    filtered.Add(c);
                }
            }

            keptFaces = filtered;
        }

        // 5. Remesh each pad-bearing hole and grade it as a distance field. Original terrain vertices
        // are reused by index; remesh interior (Steiner) vertices are appended.
        var globalVertices = new List<double>(vertices);
        var globalFaces = new List<int>(keptFaces);

        foreach (int[] rim in gradeRims)
        {
            if (!RemeshAndGradeHole(rim, vertices, terrain, barriers, pads, tolerance, terrainDetailSize, globalVertices, globalFaces, out errorMessage))
                return null;
        }

        // 6. Compact unused vertices (the dropped region's interior originals are now orphaned).
        CompactUnusedVertices(globalVertices, globalFaces);
        OrientFacesUpward(globalVertices, globalFaces);

        int finalVertexCount = globalVertices.Count / 3;
        double[] graded = globalVertices.ToArray();
        int[] finalFaces = globalFaces.ToArray();
        int finalFaceCount = finalFaces.Length / 3;

        // 7. Watertight/manifold gate (should hold by construction). If the replacement patch left a
        // seam defect, run the same topology repair used by the explicit assembler, then gate again.
        MeshTopologyValidator.BoundaryGraphAnalysis topology =
            MeshTopologyValidator.AnalyzeBoundaryGraph(finalFaces, finalFaceCount);
        MeshTopologyValidator.BoundaryGraphAnalysis terrainTopology =
            MeshTopologyValidator.AnalyzeBoundaryGraph(faces, faceCount);
        if (topology.NonManifoldEdgeCount == 0 &&
            (topology.HasOpenBoundaryChains || topology.BoundaryComponentCount > terrainTopology.BoundaryComponentCount))
        {
            (graded, finalFaces) = MeshTopologyOperations.MakeWatertight(
                graded,
                finalVertexCount,
                finalFaces,
                finalFaceCount,
                Math.Max(tolerance, 1e-6),
                out _,
                out _);
            finalVertexCount = graded.Length / 3;
            finalFaceCount = finalFaces.Length / 3;
            topology = MeshTopologyValidator.AnalyzeBoundaryGraph(finalFaces, finalFaceCount);
        }

        bool hasNewOpenBoundaryChains = topology.HasOpenBoundaryChains && !terrainTopology.HasOpenBoundaryChains;
        if (topology.NonManifoldEdgeCount > 0 ||
            hasNewOpenBoundaryChains ||
            topology.BoundaryComponentCount > terrainTopology.BoundaryComponentCount)
        {
            errorMessage = GradedRegionAssembler.DescribeWeldTopologyFailure("Grade Pad region remesh", topology, terrainTopology);
            return null;
        }

        var original = new double[finalVertexCount * 3];
        for (int i = 0; i < finalVertexCount; i++)
        {
            double x = graded[i * 3];
            double y = graded[i * 3 + 1];
            original[i * 3] = x;
            original[i * 3 + 1] = y;
            original[i * 3 + 2] = terrain.InterpolateZ(x, y);
        }

        const string modeMessage =
            "Grade Pad topology mode: region remesh (affected region rebuilt as a clean dense patch, graded by distance field).";
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
            finalVertexCount,
            finalFaces,
            finalFaceCount,
            outputPolylines,
            diagnostics,
            patchSummaries,
            structured);
    }

    private static bool RemeshAndGradeHole(
        int[] rim,
        double[] vertices,
        TerrainFaceGrid terrain,
        PreparedBarriers barriers,
        PadBoundary[] pads,
        double tolerance,
        double terrainDetailSize,
        List<double> globalVertices,
        List<int> globalFaces,
        out string? errorMessage)
    {
        errorMessage = null;
        int rimCount = rim.Length;
        if (rimCount < 3)
        {
            errorMessage = "Grade Pad region remesh hole rim collapsed; deferring.";
            return false;
        }

        // Rim points first (input indices [0, rimCount)) so their source ids weld back to terrain.
        var xy = new List<double>(rimCount * 2);
        for (int i = 0; i < rimCount; i++)
        {
            xy.Add(vertices[rim[i] * 3]);
            xy.Add(vertices[rim[i] * 3 + 1]);
        }

        var segments = new List<(int a, int b)>(rimCount);
        for (int i = 0; i < rimCount; i++)
            segments.Add((i, (i + 1) % rimCount));

        // Dense interior WITHOUT quality refinement: maxArea>0 turns on conforming Delaunay, which
        // inserts Steiner points ALONG the rim segments and breaks the exact-vertex weld to the kept
        // terrain. Instead seed an explicit interior grid (kept inside the rim) and triangulate the
        // plain PSLG — the rim edges stay intact (original vertices => exact weld), and the grid gives
        // the smooth distance-field batter.
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        double rimPerimeter = 0.0;
        for (int i = 0; i < rimCount; i++)
        {
            double px = vertices[rim[i] * 3], py = vertices[rim[i] * 3 + 1];
            if (px < minX) minX = px;
            if (px > maxX) maxX = px;
            if (py < minY) minY = py;
            if (py > maxY) maxY = py;

            int next = (i + 1) % rimCount;
            double dx = vertices[rim[next] * 3] - px;
            double dy = vertices[rim[next] * 3 + 1] - py;
            rimPerimeter += Math.Sqrt((dx * dx) + (dy * dy));
        }

        // Interior grid spacing. terrainDetailSize is often 0 (unset) when called from Rhino, so
        // derive a sensible spacing from the rim's own edge lengths instead of collapsing to the model
        // tolerance (which would explode the grid to billions of points and hang). Also hard-cap the
        // grid to a bounded cell count per axis so a large region can never blow up.
        double avgRimEdge = rimPerimeter / Math.Max(rimCount, 1);
        double targetEdge = terrainDetailSize > tolerance ? terrainDetailSize * 0.6 : avgRimEdge;
        targetEdge = Math.Max(targetEdge, tolerance * 8.0);
        const int maxCellsPerAxis = 160;
        double span = Math.Max(maxX - minX, maxY - minY);
        if (span > 0.0)
            targetEdge = Math.Max(targetEdge, span / maxCellsPerAxis);

        var rimPoly = new double[rimCount * 2];
        for (int i = 0; i < rimCount; i++)
        {
            rimPoly[i * 2] = vertices[rim[i] * 3];
            rimPoly[i * 2 + 1] = vertices[rim[i] * 3 + 1];
        }

        // Pads whose footprint interior point lies inside this hole own its grade. (Interior point,
        // not vertex average: a concave pad's average can fall outside its own footprint and assign
        // the pad to the wrong hole.)
        var holePads = new List<PadBoundary>();
        foreach (PadBoundary pad in pads)
        {
            if (pad.VertexCount <= 0)
                continue;

            (double px, double py) = PolygonInteriorPoint(pad.XyVertices, pad.VertexCount);
            if (PointInPolygon(px, py, rimPoly, rimCount))
                holePads.Add(pad);
        }

        if (holePads.Count == 0)
        {
            errorMessage = "Grade Pad region remesh hole contained no pad; deferring.";
            return false;
        }

        // Seed each owning pad's footprint: densified boundary + an interior grid at the pad's own
        // scale. The terrain grid alone (capped to a bounded cell count over a possibly large region)
        // can step right over a small pad, leaving no vertex on its flat top — the pad then never
        // flattens. Seeding the footprint guarantees flat-top vertices regardless of pad size; the
        // section grader assigns them the pad plane.
        var seedKeys = new HashSet<(long, long)>();
        double seedCell = Math.Max(tolerance * 8.0, targetEdge * 0.25);
        bool AddSeed(double sx, double sy)
        {
            if (!PointInPolygon(sx, sy, rimPoly, rimCount))
                return false;

            var key = ((long)Math.Round(sx / seedCell), (long)Math.Round(sy / seedCell));
            if (!seedKeys.Add(key))
                return false;

            xy.Add(sx);
            xy.Add(sy);
            return true;
        }

        foreach ((double gx, double gy) in EnumeratePadSeedPoints(holePads, targetEdge))
            AddSeed(gx, gy);

        for (double gy = minY + targetEdge; gy < maxY; gy += targetEdge)
        {
            for (double gx = minX + targetEdge; gx < maxX; gx += targetEdge)
            {
                if (PointInPolygon(gx, gy, rimPoly, rimCount) &&
                    DistToPolygon(gx, gy, rimPoly, rimCount) > targetEdge * 0.4)
                {
                    AddSeed(gx, gy);
                }
            }
        }

        TriangulationOutcome outcome = TriangulationHelper.Triangulate(
            xy, xy.Count / 2, segments, maxArea: 0.0, minAngle: 0.0, convex: false, segmentSplitting: 0);
        if (outcome.Mesh == null)
        {
            errorMessage = outcome.WarningMessage ?? "Grade Pad region remesh triangulation failed; deferring.";
            return false;
        }

        TriangleNetExtractor.Result extracted = TriangleNetExtractor.Extract(outcome.Mesh);
        if (extracted.FaceCount == 0)
        {
            errorMessage = "Grade Pad region remesh produced no faces; deferring.";
            return false;
        }

        double[] rimXy = rimPoly;

        // Distance-field Z over the dense patch.
        int vc = extracted.VertexCount;
        var gradedZ = new double[vc * 3];
        var originalZ = new double[vc * 3];
        for (int i = 0; i < vc; i++)
        {
            double x = extracted.Xy[i * 2];
            double y = extracted.Xy[i * 2 + 1];
            double z = terrain.InterpolateZ(x, y);
            gradedZ[i * 3] = x; gradedZ[i * 3 + 1] = y; gradedZ[i * 3 + 2] = z;
            originalZ[i * 3] = x; originalZ[i * 3 + 1] = y; originalZ[i * 3 + 2] = z;
        }

        ApplyGradingToVerticesWithSections(
            gradedZ, originalZ, vc, holePads.ToArray(), barriers, terrain,
            hasBoundaryLoop: false, boundaryLoop: Array.Empty<double>(), boundaryVertexCount: 0,
            tolerance, keepShoulderOnBatterPlane: false, defaultCornerFanSegments: 6);

        // Weld: rim vertices -> their original terrain index (terrain Z, untouched); interior -> append.
        var remeshToGlobal = new int[vc];
        for (int i = 0; i < vc; i++)
        {
            int sourceId = extracted.SourceIds[i];
            if (sourceId >= 0 && sourceId < rimCount)
            {
                remeshToGlobal[i] = rim[sourceId];
                continue;
            }

            int index = globalVertices.Count / 3;
            globalVertices.Add(gradedZ[i * 3]);
            globalVertices.Add(gradedZ[i * 3 + 1]);
            globalVertices.Add(gradedZ[i * 3 + 2]);
            remeshToGlobal[i] = index;
        }

        for (int f = 0; f < extracted.FaceCount; f++)
        {
            int e0 = extracted.Faces[f * 3], e1 = extracted.Faces[f * 3 + 1], e2 = extracted.Faces[f * 3 + 2];

            // Triangle.NET fills the convex hull of the rim points; the rim is a jagged (non-convex)
            // terrain-face boundary, so hull faces spill into its concavity notches — ground that
            // belongs to the kept terrain. Keep only faces whose centroid lies inside the rim so the
            // remesh boundary matches the rim exactly and welds watertight.
            double ccx = (extracted.Xy[e0 * 2] + extracted.Xy[e1 * 2] + extracted.Xy[e2 * 2]) / 3.0;
            double ccy = (extracted.Xy[e0 * 2 + 1] + extracted.Xy[e1 * 2 + 1] + extracted.Xy[e2 * 2 + 1]) / 3.0;
            if (!PointInPolygon(ccx, ccy, rimXy, rimCount))
                continue;

            int g0 = remeshToGlobal[e0];
            int g1 = remeshToGlobal[e1];
            int g2 = remeshToGlobal[e2];
            if (g0 == g1 || g1 == g2 || g2 == g0)
                continue;

            globalFaces.Add(g0);
            globalFaces.Add(g1);
            globalFaces.Add(g2);
        }

        return true;
    }

    /// <summary>
    /// Interior seed points for each owning pad's flat top: an inset boundary ring (so the flat
    /// region reaches near the footprint edge) plus a pad-scale interior grid and the centroid. The
    /// pad-scale spacing guarantees a small pad still gets several flat-top vertices even when the
    /// hole's terrain-scale grid would step over it. All points are strictly inside the footprint, so
    /// the section grader assigns them the pad plane.
    /// </summary>
    private static IEnumerable<(double X, double Y)> EnumeratePadSeedPoints(List<PadBoundary> holePads, double terrainTargetEdge)
    {
        foreach (PadBoundary pad in holePads)
        {
            if (pad.VertexCount < 3)
                continue;

            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            for (int i = 0; i < pad.VertexCount; i++)
            {
                double vx = pad.XyVertices[i * 2];
                double vy = pad.XyVertices[i * 2 + 1];
                if (vx < minX) minX = vx;
                if (vx > maxX) maxX = vx;
                if (vy < minY) minY = vy;
                if (vy > maxY) maxY = vy;
            }

            double span = Math.Max(maxX - minX, maxY - minY);
            if (span <= 0.0)
                continue;

            // Pad-scale spacing: about three cells across the pad, so small pads seed FINER than the
            // terrain grid (the terrain grid can step right over them); large pads cap at the terrain
            // grid spacing.
            double s = Math.Min(terrainTargetEdge > 0.0 ? terrainTargetEdge : span / 3.0, span / 3.0);
            if (s <= 0.0)
                continue;

            // Guaranteed-interior point (a concave pad's vertex average can fall outside the
            // footprint); also the pull target for the inset ring below.
            (double cx, double cy) = PolygonInteriorPoint(pad.XyVertices, pad.VertexCount);
            yield return (cx, cy);

            // Inset boundary ring (10% toward the interior point; each point is verified inside the
            // footprint before it is yielded).
            for (int i = 0; i < pad.VertexCount; i++)
            {
                int j = (i + 1) % pad.VertexCount;
                double px = pad.XyVertices[i * 2], py = pad.XyVertices[i * 2 + 1];
                double qx = pad.XyVertices[j * 2], qy = pad.XyVertices[j * 2 + 1];
                double edgeLen = Math.Sqrt(((qx - px) * (qx - px)) + ((qy - py) * (qy - py)));
                int steps = Math.Max(1, (int)Math.Ceiling(edgeLen / s));
                for (int k = 0; k < steps; k++)
                {
                    double t = (double)k / steps;
                    double ex = px + ((qx - px) * t);
                    double ey = py + ((qy - py) * t);
                    double ix = ex + ((cx - ex) * 0.1);
                    double iy = ey + ((cy - ey) * 0.1);
                    if (PointInPolygon(ix, iy, pad.XyVertices, pad.VertexCount))
                        yield return (ix, iy);
                }
            }

            // Pad-scale interior grid.
            for (double gy = minY + (s * 0.5); gy < maxY; gy += s)
            {
                for (double gx = minX + (s * 0.5); gx < maxX; gx += s)
                {
                    if (PointInPolygon(gx, gy, pad.XyVertices, pad.VertexCount) &&
                        DistToPolygon(gx, gy, pad.XyVertices, pad.VertexCount) > s * 0.25)
                    {
                        yield return (gx, gy);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Pads whose interior point lies inside NO hole rim — they were not carved (their region was
    /// smaller than the local terrain triangles) and would be left ungraded. Returns those interior
    /// points so a containing terrain face can be dropped to give each one a hole. Interior point,
    /// not vertex average: a concave pad's average can fall outside its own footprint, which would
    /// both misreport the pad as uncarved and punch the hole in a face the pad does not even touch.
    /// </summary>
    private static bool PadCentroidsNeedingCarve(
        List<int[]> rimLoops, double[] vertices, PadBoundary[] pads, out List<(double X, double Y)> centroids)
    {
        centroids = new List<(double X, double Y)>();
        var rimPolys = new List<double[]>(rimLoops.Count);
        foreach (int[] rim in rimLoops)
        {
            var poly = new double[rim.Length * 2];
            for (int i = 0; i < rim.Length; i++)
            {
                poly[i * 2] = vertices[rim[i] * 3];
                poly[i * 2 + 1] = vertices[rim[i] * 3 + 1];
            }

            rimPolys.Add(poly);
        }

        foreach (PadBoundary pad in pads)
        {
            if (pad.VertexCount < 3)
                continue;

            (double cx, double cy) = PolygonInteriorPoint(pad.XyVertices, pad.VertexCount);

            bool inAnyRim = false;
            foreach (double[] poly in rimPolys)
            {
                if (PointInPolygon(cx, cy, poly, poly.Length / 2))
                {
                    inAnyRim = true;
                    break;
                }
            }

            if (!inAnyRim)
                centroids.Add((cx, cy));
        }

        return centroids.Count > 0;
    }

    /// <summary>
    /// True when any pad footprint's interior point lies inside the given rim polygon. Interior
    /// point, not vertex average: a pad-bearing rim misclassified as an island (because a concave
    /// pad's average fell outside the rim) gets its kept faces dropped and its hole never remeshed.
    /// </summary>
    private static bool RimEnclosesAnyPad(double[] rimPoly, int rimCount, PadBoundary[] pads)
    {
        foreach (PadBoundary pad in pads)
        {
            if (pad.VertexCount <= 0)
                continue;

            (double px, double py) = PolygonInteriorPoint(pad.XyVertices, pad.VertexCount);
            if (PointInPolygon(px, py, rimPoly, rimCount))
                return true;
        }

        return false;
    }

    private static void CompactUnusedVertices(List<double> vertices, List<int> faces)
    {
        int vertexCount = vertices.Count / 3;
        var oldToNew = new int[vertexCount];
        for (int i = 0; i < vertexCount; i++)
            oldToNew[i] = -1;

        var compact = new List<double>(vertices.Count);
        for (int f = 0; f < faces.Count; f++)
        {
            int v = faces[f];
            if (oldToNew[v] < 0)
            {
                oldToNew[v] = compact.Count / 3;
                compact.Add(vertices[v * 3]);
                compact.Add(vertices[v * 3 + 1]);
                compact.Add(vertices[v * 3 + 2]);
            }

            faces[f] = oldToNew[v];
        }

        vertices.Clear();
        vertices.AddRange(compact);
    }

    private static void OrientFacesUpward(List<double> vertices, List<int> faces)
    {
        for (int f = 0; f < faces.Count; f += 3)
        {
            int a = faces[f], b = faces[f + 1], c = faces[f + 2];
            double ax = vertices[a * 3], ay = vertices[a * 3 + 1];
            double bx = vertices[b * 3], by = vertices[b * 3 + 1];
            double cx = vertices[c * 3], cy = vertices[c * 3 + 1];
            double cross = ((bx - ax) * (cy - ay)) - ((by - ay) * (cx - ax));
            if (cross < 0.0)
            {
                faces[f + 1] = c;
                faces[f + 2] = b;
            }
        }
    }

    /// <summary>
    /// Extracts the dropped region's rim loops, repairing PINCH vertices (see
    /// <see cref="TryBreakDroppedRegionPinches"/>) and retrying until the boundary traces cleanly or
    /// the repair can make no further progress (then returns false to defer). Used at both carve
    /// stages — the initial vertex-based carve and the small-pad targeted carve — since either can
    /// leave a single-point self-touch.
    /// </summary>
    private static bool TryExtractRimsWithPinchRepair(List<int> droppedFaces, List<int> keptFaces, out List<int[]> rimLoops)
    {
        int passes = 0;
        while (!MeshBoundaryLoopBuilder.TryBuildBoundaryLoopsIndexed(droppedFaces.ToArray(), droppedFaces.Count / 3, out rimLoops))
        {
            if (passes++ >= 4 || !TryBreakDroppedRegionPinches(keptFaces, droppedFaces))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Repairs a PINCHED dropped region — a boundary vertex where the carved hole touches itself at a
    /// single point (naked-edge degree &gt; 2), so its outline is a figure-eight the loop walker cannot
    /// trace. For each such vertex, every KEPT face still incident to it is moved into the dropped set,
    /// which merges the touching parts into one simply-connected hole and makes the vertex interior.
    /// Returns true if any face was moved (the caller should retry boundary extraction).
    /// </summary>
    private static bool TryBreakDroppedRegionPinches(List<int> keptFaces, List<int> droppedFaces)
    {
        // Naked-edge degree per vertex over the dropped region (an edge on exactly one dropped face).
        var edgeCount = IndexedMeshTools.CreateEdgeKeyMap<int>(Math.Max(8, droppedFaces.Count));
        void Inc(int a, int b)
        {
            long k = IndexedMeshTools.GetEdgeKey(a, b);
            edgeCount[k] = edgeCount.GetValueOrDefault(k, 0) + 1;
        }

        int droppedCount = droppedFaces.Count / 3;
        for (int f = 0; f < droppedCount; f++)
        {
            int a = droppedFaces[f * 3], b = droppedFaces[f * 3 + 1], c = droppedFaces[f * 3 + 2];
            Inc(a, b);
            Inc(b, c);
            Inc(c, a);
        }

        var nakedDegree = new Dictionary<int, int>();
        foreach (var kv in edgeCount)
        {
            if (kv.Value != 1)
                continue;

            int a = (int)(kv.Key >> 32);
            int b = (int)(kv.Key & 0xFFFFFFFFL);
            nakedDegree[a] = nakedDegree.GetValueOrDefault(a, 0) + 1;
            nakedDegree[b] = nakedDegree.GetValueOrDefault(b, 0) + 1;
        }

        var pinchVertices = new HashSet<int>();
        foreach (var kv in nakedDegree)
        {
            if (kv.Value > 2)
                pinchVertices.Add(kv.Key);
        }

        if (pinchVertices.Count == 0)
            return false;

        var newKept = new List<int>(keptFaces.Count);
        bool moved = false;
        for (int f = 0; f < keptFaces.Count; f += 3)
        {
            int a = keptFaces[f], b = keptFaces[f + 1], c = keptFaces[f + 2];
            if (pinchVertices.Contains(a) || pinchVertices.Contains(b) || pinchVertices.Contains(c))
            {
                droppedFaces.Add(a);
                droppedFaces.Add(b);
                droppedFaces.Add(c);
                moved = true;
            }
            else
            {
                newKept.Add(a);
                newKept.Add(b);
                newKept.Add(c);
            }
        }

        if (moved)
        {
            keptFaces.Clear();
            keptFaces.AddRange(newKept);
        }

        return moved;
    }
}
