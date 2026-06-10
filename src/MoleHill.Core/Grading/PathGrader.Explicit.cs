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

        // Lock curves / preserved breaklines act as barriers that clip the corridor batters, and are
        // embedded into the welded terrain by the assembler.
        PreparedBarriers barriers = GradingBarriers.Build(hardConstraints);

        var inserts = new List<GradedRegionAssembler.RegionInsert>(paths.Length);
        var outputPolylines = new List<OutputPolyline>(paths.Length * 2);
        var daylightPolygons = new List<double[]>(paths.Length);
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

            // Offset left/right road edges along the smoothed tangent normals; both carry the
            // centerline profile elevation (the road is level across its width at each station).
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

            // Corridor outline footprint: left edge forward, then right edge backward. The implicit
            // closing segments (left[n-1]->right[n-1] and right[0]->left[0]) are the end caps.
            int outlineCount = n * 2;
            var outlineXy = new double[outlineCount * 2];
            var outlineZ = new double[outlineCount];
            for (int i = 0; i < n; i++)
            {
                outlineXy[i * 2] = leftXyz[i * 3];
                outlineXy[i * 2 + 1] = leftXyz[i * 3 + 1];
                outlineZ[i] = leftXyz[i * 3 + 2];
            }

            for (int i = 0; i < n; i++)
            {
                int src = n - 1 - i;
                int dst = n + i;
                outlineXy[dst * 2] = rightXyz[src * 3];
                outlineXy[dst * 2 + 1] = rightXyz[src * 3 + 1];
                outlineZ[dst] = rightXyz[src * 3 + 2];
            }

            if (GradingGeometry2D.ClosedPolylineSelfIntersects(outlineXy, outlineCount))
            {
                errorMessage = "Grade Path corridor outline self-intersects (sharp turn narrower than its width); deferring to constraint-first path.";
                return null;
            }

            BatterStripBuilder.FootprintStations stations =
                BatterStripBuilder.BuildClosedFootprintStations(outlineXy, outlineCount, outlineZ, cornerFanSegments: 6);

            BatterStripBuilder.DaylightLoop loop = BatterStripBuilder.BuildDaylightLoop(
                stations.Xy,
                stations.Count,
                isClosed: true,
                outwardNormals: stations.Normals,
                footprintZByStation: stations.Z,
                slopeAngleDeg: path.SlopeAngleDeg,
                maxDistance: path.MaxDistance,
                terrain: terrain,
                barriers: barriers,
                tolerance: tolerance);

            foreach (BatterStripBuilder.DaylightStation station in loop.Stations)
            {
                if (station.Status == BatterStripBuilder.DaylightStatus.NonDaylighting)
                    nonDaylightingStations++;
            }

            double[] daylightXy = loop.DaylightXy();

            var subMeshes = new List<GradedRegionAssembler.SubMesh>(2)
            {
                BuildCorridorSurface(leftXyz, rightXyz, n)
            };

            if (loop.HasBatter)
            {
                BatterStripBuilder.BatterStrip strip = BatterStripBuilder.BuildBatterStrip(loop, spacing);
                if (strip.FaceCount > 0)
                {
                    subMeshes.Add(new GradedRegionAssembler.SubMesh
                    {
                        Vertices = strip.Vertices,
                        VertexCount = strip.VertexCount,
                        Faces = strip.Faces,
                        FaceCount = strip.FaceCount
                    });
                }
            }

            inserts.Add(new GradedRegionAssembler.RegionInsert
            {
                DaylightLoopXyz = loop.DaylightXyz(),
                DaylightLoopCount = loop.Count,
                SubMeshes = subMeshes
            });
            daylightPolygons.Add(daylightXy);

            outputPolylines.Add(new OutputPolyline(leftXyz, n, isClosed: false));
            outputPolylines.Add(new OutputPolyline(rightXyz, n, isClosed: false));
        }

        // Junctions / overlapping corridors need ownership resolution the explicit path does not yet
        // perform; defer interacting paths to the constraint-first engine.
        for (int i = 0; i < daylightPolygons.Count; i++)
        {
            for (int j = i + 1; j < daylightPolygons.Count; j++)
            {
                if (GradingGeometry2D.PolygonsOverlap(daylightPolygons[i], daylightPolygons[j]))
                {
                    errorMessage = "Grade Path corridors interact; deferring to constraint-first path.";
                    return null;
                }
            }
        }

        GradedRegionAssembler.AssembledMesh assembled = GradedRegionAssembler.Assemble(
            vertices,
            vertexCount,
            faces,
            faceCount,
            inserts,
            tolerance,
            hardConstraints);

        if (!assembled.Success)
        {
            errorMessage = assembled.Warning ?? "Grade Path explicit corridor assembly failed.";
            return null;
        }

        MeshTopologyValidator.BoundaryGraphAnalysis topology =
            MeshTopologyValidator.AnalyzeBoundaryGraph(assembled.Faces, assembled.FaceCount);
        if (topology.NonManifoldEdgeCount > 0 || topology.HasOpenBoundaryChains)
        {
            errorMessage = "Grade Path explicit corridor assembly produced non-manifold or open topology.";
            return null;
        }

        // A batter that daylights past the terrain edge leaves an unfilled gap (extra boundary loop).
        // The legacy path clips shoulders to the terrain boundary, so defer those cases to it.
        MeshTopologyValidator.BoundaryGraphAnalysis terrainTopology =
            MeshTopologyValidator.AnalyzeBoundaryGraph(faces, faceCount);
        if (topology.BoundaryComponentCount > terrainTopology.BoundaryComponentCount)
        {
            errorMessage = "Grade Path batter reaches past the terrain boundary; deferring to constraint-first path.";
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
            "Grade Path topology mode: explicit corridor construction (ruled road surface and side batters welded into terrain).";
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
    /// Builds the explicit road surface as a ruled strip between the left and right edges, level
    /// across the width at each station's centerline elevation. The strip boundary is the corridor
    /// outline, so it welds to the batter inner ring.
    /// </summary>
    private static GradedRegionAssembler.SubMesh BuildCorridorSurface(double[] leftXyz, double[] rightXyz, int n)
    {
        var vertices = new double[n * 2 * 3];
        for (int i = 0; i < n; i++)
        {
            vertices[i * 3] = leftXyz[i * 3];
            vertices[i * 3 + 1] = leftXyz[i * 3 + 1];
            vertices[i * 3 + 2] = leftXyz[i * 3 + 2];
            int r = n + i;
            vertices[r * 3] = rightXyz[i * 3];
            vertices[r * 3 + 1] = rightXyz[i * 3 + 1];
            vertices[r * 3 + 2] = rightXyz[i * 3 + 2];
        }

        var faces = new List<int>((n - 1) * 6);
        for (int i = 0; i < n - 1; i++)
        {
            int la = i;
            int lb = i + 1;
            int ra = n + i;
            int rb = n + i + 1;
            // Two triangles per station quad (left[i], right[i], right[i+1], left[i+1]).
            faces.Add(la); faces.Add(ra); faces.Add(rb);
            faces.Add(la); faces.Add(rb); faces.Add(lb);
        }

        return new GradedRegionAssembler.SubMesh
        {
            Vertices = vertices,
            VertexCount = n * 2,
            Faces = faces.ToArray(),
            FaceCount = faces.Count / 3
        };
    }
}
