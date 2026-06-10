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
        GradedRegionAssembler.SubMesh PadTop,
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

        var inserts = new List<GradedRegionAssembler.RegionInsert>(groups.Count);
        foreach (List<PadBuild> members in groups.Values)
        {
            if (members.Count == 1)
            {
                inserts.Add(BuildSinglePadInsert(members[0]));
                continue;
            }

            GradedRegionAssembler.RegionInsert? groupInsert =
                BuildInteractingGroupInsert(members, terrain, barriers, tolerance);
            if (groupInsert is null)
            {
                errorMessage = "Grade Pad interacting pad group could not be resolved explicitly; deferring to constraint-first path.";
                return null;
            }

            inserts.Add(groupInsert);
        }

        GradedRegionAssembler.AssembledMesh assembled = GradedRegionAssembler.Assemble(
            vertices, vertexCount, faces, faceCount, inserts, tolerance);

        if (!assembled.Success)
        {
            errorMessage = assembled.Warning ?? "Grade Pad explicit batter assembly failed.";
            return null;
        }

        MeshTopologyValidator.BoundaryGraphAnalysis topology =
            MeshTopologyValidator.AnalyzeBoundaryGraph(assembled.Faces, assembled.FaceCount);
        MeshTopologyValidator.BoundaryGraphAnalysis terrainTopology =
            MeshTopologyValidator.AnalyzeBoundaryGraph(faces, faceCount);
        if (topology.NonManifoldEdgeCount > 0 ||
            topology.HasOpenBoundaryChains ||
            topology.BoundaryComponentCount > terrainTopology.BoundaryComponentCount)
        {
            errorMessage = "Grade Pad explicit batter assembly produced non-manifold, open, or off-terrain topology.";
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

        double segmentLength = ComputePadConstraintSegmentLength(maxReach);
        if (terrainDetailSize > tolerance)
            segmentLength = Math.Min(segmentLength, terrainDetailSize);

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
            slopeAngleDeg: pad.SlopeAngleDeg,
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
        if (ClosedPolylineHasSelfIntersection(daylightXy, loop.Count) ||
            AnyPointInsidePolygon(daylightXy, loop.Count, pad.XyVertices, pad.VertexCount))
        {
            errorMessage = "Grade Pad batter shoulders self-overlap; deferring to constraint-first path.";
            return null;
        }

        GradedRegionAssembler.SubMesh? padTop = BuildPadTopFill(padLoop, pad);
        if (padTop is null)
        {
            errorMessage = "Grade Pad could not triangulate a pad top.";
            return null;
        }

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

        return new PadBuild(pad, padLoop, segmentLength, loop, daylightXy, padTop, batter);
    }

    private static GradedRegionAssembler.RegionInsert BuildSinglePadInsert(PadBuild build)
    {
        var subMeshes = new List<GradedRegionAssembler.SubMesh>(2);
        if (build.Batter is not null)
            subMeshes.Add(build.Batter);
        subMeshes.Add(build.PadTop);

        return new GradedRegionAssembler.RegionInsert
        {
            DaylightLoopXyz = build.Loop.DaylightXyz(),
            DaylightLoopCount = build.Loop.Count,
            SubMeshes = subMeshes
        };
    }

    /// <summary>
    /// Resolves a group of pads whose daylight regions overlap. Builds the union of their daylight
    /// polygons as the carve boundary, densely triangulates the union interior (footprint loops as
    /// constraints, batter row points as seeds), then assigns Z by the proven ownership sections
    /// (higher pad wins). The union boundary stays on terrain so it welds to the surrounding mesh.
    /// </summary>
    private static GradedRegionAssembler.RegionInsert? BuildInteractingGroupInsert(
        List<PadBuild> group,
        TerrainFaceGrid terrain,
        PreparedBarriers barriers,
        double tolerance)
    {
        var daylightPolys = new List<double[]>(group.Count);
        foreach (PadBuild build in group)
            daylightPolys.Add(build.DaylightXy);

        if (!ClipperGeometry.TryUnionClosedLoops(daylightPolys, tolerance, out List<double[]> unionLoops) ||
            unionLoops.Count != 1)
        {
            // A hole or disjoint union is beyond this resolver; defer to legacy.
            return null;
        }

        double[] unionLoop = unionLoops[0];
        int unionCount = unionLoop.Length / 2;
        if (unionCount < 3)
            return null;

        double weldTol = Math.Max(tolerance, 1e-6);
        double inverseCell = 1.0 / weldTol;
        var xyList = new List<double>();
        var pointIndex = new Dictionary<(long, long), int>();
        var segments = new List<(int a, int b)>();

        int AddPoint(double x, double y)
        {
            var key = ((long)Math.Round(x * inverseCell), (long)Math.Round(y * inverseCell));
            if (pointIndex.TryGetValue(key, out int existing))
                return existing;

            int index = xyList.Count / 2;
            xyList.Add(x);
            xyList.Add(y);
            pointIndex[key] = index;
            return index;
        }

        void AddClosedLoop(double[] xy, int count)
        {
            if (count < 3)
                return;

            int first = AddPoint(xy[0], xy[1]);
            int previous = first;
            for (int i = 1; i < count; i++)
            {
                int current = AddPoint(xy[i * 2], xy[i * 2 + 1]);
                if (current != previous)
                    segments.Add((previous, current));
                previous = current;
            }

            if (previous != first)
                segments.Add((previous, first));
        }

        // Union boundary first: its input indices [0, unionPointCount) identify perimeter vertices.
        AddClosedLoop(unionLoop, unionCount);
        int unionPointCount = xyList.Count / 2;

        foreach (PadBuild build in group)
            AddClosedLoop(build.PadLoop.XyVertices, build.PadLoop.VertexCount);

        // Batter row points seed the interior so the slope keeps its intrinsic resolution.
        foreach (PadBuild build in group)
        {
            if (build.Batter is null)
                continue;

            double[] sv = build.Batter.Vertices;
            for (int i = 0; i < build.Batter.VertexCount; i++)
                AddPoint(sv[i * 3], sv[i * 3 + 1]);
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

        // Pin the union perimeter exactly to terrain so it welds to the surrounding terrain seam.
        for (int i = 0; i < vc; i++)
        {
            int sourceId = extracted.SourceIds[i];
            if (sourceId >= 0 && sourceId < unionPointCount)
                graded[i * 3 + 2] = terrain.InterpolateZ(graded[i * 3], graded[i * 3 + 1]);
        }

        var fill = new GradedRegionAssembler.SubMesh
        {
            Vertices = graded,
            VertexCount = vc,
            Faces = extracted.Faces,
            FaceCount = extracted.FaceCount
        };

        var unionXyz = new double[unionCount * 3];
        for (int i = 0; i < unionCount; i++)
        {
            double x = unionLoop[i * 2];
            double y = unionLoop[i * 2 + 1];
            unionXyz[i * 3] = x;
            unionXyz[i * 3 + 1] = y;
            unionXyz[i * 3 + 2] = terrain.InterpolateZ(x, y);
        }

        return new GradedRegionAssembler.RegionInsert
        {
            DaylightLoopXyz = unionXyz,
            DaylightLoopCount = unionCount,
            SubMeshes = new[] { fill }
        };
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

    /// <summary>
    /// Triangulates the pad-top interior at the pad plane. The boundary is the densified footprint
    /// loop, so the pad-top edge matches the batter strip's inner ring vertex-for-vertex and welds
    /// cleanly. Returns null only if triangulation fails outright.
    /// </summary>
    private static GradedRegionAssembler.SubMesh? BuildPadTopFill(ConstraintLoop padLoop, PadBoundary pad)
    {
        var xyList = new List<double>(padLoop.VertexCount * 2);
        for (int i = 0; i < padLoop.VertexCount; i++)
        {
            xyList.Add(padLoop.XyVertices[i * 2]);
            xyList.Add(padLoop.XyVertices[i * 2 + 1]);
        }

        var segments = new List<(int a, int b)>(padLoop.VertexCount);
        for (int i = 0; i < padLoop.VertexCount; i++)
            segments.Add((i, (i + 1) % padLoop.VertexCount));

        TriangulationOutcome outcome = TriangulationHelper.Triangulate(
            xyList,
            padLoop.VertexCount,
            segments,
            maxArea: 0.0,
            minAngle: 0.0,
            convex: false,
            segmentSplitting: 0);

        if (outcome.Mesh == null)
            return null;

        TriangleNetExtractor.Result extracted = TriangleNetExtractor.Extract(outcome.Mesh);
        if (extracted.FaceCount == 0)
            return null;

        var verts = new double[extracted.VertexCount * 3];
        for (int i = 0; i < extracted.VertexCount; i++)
        {
            double x = extracted.Xy[i * 2];
            double y = extracted.Xy[i * 2 + 1];
            verts[i * 3] = x;
            verts[i * 3 + 1] = y;
            verts[i * 3 + 2] = pad.EvaluateZ(x, y);
        }

        return new GradedRegionAssembler.SubMesh
        {
            Vertices = verts,
            VertexCount = extracted.VertexCount,
            Faces = extracted.Faces,
            FaceCount = extracted.FaceCount
        };
    }

    private static bool AnyPointInsidePolygon(double[] pointsXy, int pointCount, double[] polygonXy, int polygonCount)
    {
        for (int i = 0; i < pointCount; i++)
        {
            if (PointInPolygon(pointsXy[i * 2], pointsXy[i * 2 + 1], polygonXy, polygonCount))
                return true;
        }

        return false;
    }
}
