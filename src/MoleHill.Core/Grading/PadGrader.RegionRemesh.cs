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
    /// null (deferring to the legacy path) when the region cannot be carved into clean single holes.
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
        double margin = Math.Max(terrainDetailSize * 2.0, 1.0);
        var offsetLoops = new List<double[]>(regionLoops.Count);
        foreach (double[] loop in regionLoops)
        {
            offsetLoops.Add(
                ClipperGeometry.TryOffsetClosedLoop(loop, margin, tolerance, out double[] grown) && grown.Length >= 6
                    ? grown
                    : loop);
        }

        // 3. Drop every terrain face whose centroid falls inside a grown region. The kept faces' rim
        // is then made of original terrain vertices only — no cut points, so nothing to conform.
        // Keep a face only when ALL three of its vertices lie outside every grown region. Dropping a
        // face if ANY vertex is inside makes the carved region solid (no straddling faces left behind
        // as interior islands or holes), and guarantees the rim is built from faces entirely in
        // untouched terrain — so every rim vertex is an original terrain vertex shared with the keep.
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

        var keptFaces = new List<int>(faceCount * 3);
        var droppedFaces = new List<int>();
        for (int f = 0; f < faceCount; f++)
        {
            int a = faces[f * 3], b = faces[f * 3 + 1], c = faces[f * 3 + 2];
            bool touchesRegion = VertexInsideAnyRegion(a) || VertexInsideAnyRegion(b) || VertexInsideAnyRegion(c);
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

        if (droppedFaces.Count == 0)
        {
            errorMessage = "Grade Pad region remesh dropped no faces; deferring.";
            return null;
        }

        // 4. Each hole's rim, as ordered ORIGINAL vertex indices.
        if (!MeshBoundaryLoopBuilder.TryBuildBoundaryLoopsIndexed(droppedFaces.ToArray(), droppedFaces.Count / 3, out List<int[]> rimLoops))
        {
            errorMessage = "Grade Pad region remesh could not extract clean hole boundaries; deferring.";
            return null;
        }

        // 5. Remesh each hole and grade it as a distance field. Original terrain vertices are reused
        // by index; remesh interior (Steiner) vertices are appended.
        var globalVertices = new List<double>(vertices);
        var globalFaces = new List<int>(keptFaces);

        foreach (int[] rim in rimLoops)
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

        // 7. Watertight/manifold gate (should hold by construction).
        MeshTopologyValidator.BoundaryGraphAnalysis topology =
            MeshTopologyValidator.AnalyzeBoundaryGraph(finalFaces, finalFaceCount);
        MeshTopologyValidator.BoundaryGraphAnalysis terrainTopology =
            MeshTopologyValidator.AnalyzeBoundaryGraph(faces, faceCount);
        if (topology.NonManifoldEdgeCount > 0 ||
            topology.HasOpenBoundaryChains ||
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
        double targetEdge = Math.Max(terrainDetailSize * 0.6, tolerance * 8.0);
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        for (int i = 0; i < rimCount; i++)
        {
            double px = vertices[rim[i] * 3], py = vertices[rim[i] * 3 + 1];
            if (px < minX) minX = px;
            if (px > maxX) maxX = px;
            if (py < minY) minY = py;
            if (py > maxY) maxY = py;
        }

        var rimPoly = new double[rimCount * 2];
        for (int i = 0; i < rimCount; i++)
        {
            rimPoly[i * 2] = vertices[rim[i] * 3];
            rimPoly[i * 2 + 1] = vertices[rim[i] * 3 + 1];
        }

        for (double gy = minY + targetEdge; gy < maxY; gy += targetEdge)
        {
            for (double gx = minX + targetEdge; gx < maxX; gx += targetEdge)
            {
                if (PointInPolygon(gx, gy, rimPoly, rimCount) &&
                    DistToPolygon(gx, gy, rimPoly, rimCount) > targetEdge * 0.4)
                {
                    xy.Add(gx);
                    xy.Add(gy);
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

        // Pads whose footprint centroid lies inside this hole own its grade.
        var rimXy = new double[rimCount * 2];
        for (int i = 0; i < rimCount; i++)
        {
            rimXy[i * 2] = vertices[rim[i] * 3];
            rimXy[i * 2 + 1] = vertices[rim[i] * 3 + 1];
        }

        var holePads = new List<PadBoundary>();
        foreach (PadBoundary pad in pads)
        {
            double cx = 0.0, cy = 0.0;
            for (int i = 0; i < pad.VertexCount; i++)
            {
                cx += pad.XyVertices[i * 2];
                cy += pad.XyVertices[i * 2 + 1];
            }

            if (pad.VertexCount > 0 && PointInPolygon(cx / pad.VertexCount, cy / pad.VertexCount, rimXy, rimCount))
                holePads.Add(pad);
        }

        if (holePads.Count == 0)
        {
            errorMessage = "Grade Pad region remesh hole contained no pad; deferring.";
            return false;
        }

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
}
