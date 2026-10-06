using System.Diagnostics;
using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

public static partial class PathGrader
{
    /// <summary>
    /// Grades paths by conforming the terrain to the corridor loops and KEEPING the entire conformed
    /// mesh (no carve/fill/weld). <see cref="GradedRegionAssembler.SplitConform"/> subdivides the
    /// terrain in place along each corridor's daylight envelope and road-edge footprint and returns
    /// every resulting face; Z is then reassigned over that one mesh by the path section grader.
    /// Because nothing is restitched, the result is watertight and manifold by construction — there
    /// is no seam to crack, and no conformed hole boundary to trace (the failure mode that makes the
    /// explicit corridor defer when its daylight reaches the terrain edge). Batter slopes follow the
    /// conformed terrain density rather than a clean ruled strip, so they can be slightly faceted on
    /// coarse terrain; this is a watertight middle tier between the exact-slope explicit corridor
    /// (primary) and the constraint-insertion rebuild (last resort). Returns null (deferring) when
    /// the area splitter cannot produce a manifold conformed mesh for the scene.
    /// </summary>
    private static GradingResult? GradeWithSplitKeep(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PathDefinition[] paths,
        IReadOnlyList<ConstraintPolyline> hardConstraints,
        double modelTolerance,
        out string? errorMessage,
        PerformanceTimings? performanceTimings)
    {
        errorMessage = null;

        if (paths.Length == 0)
        {
            errorMessage = "Grade Path received no paths.";
            return null;
        }

        double tolerance = GradingTolerances.ModelToleranceOrDefault(modelTolerance);
        Stopwatch? phaseTimer = performanceTimings != null ? Stopwatch.StartNew() : null;
        long phaseAllocatedBefore = performanceTimings != null
            ? GC.GetTotalAllocatedBytes(precise: true)
            : 0;
        var terrain = new TerrainFaceGrid(vertices, vertexCount, faces, faceCount);
        if (performanceTimings != null)
        {
            performanceTimings.TerrainGridMilliseconds = phaseTimer!.Elapsed.TotalMilliseconds;
            performanceTimings.TerrainGridAllocatedBytes =
                GC.GetTotalAllocatedBytes(precise: true) - phaseAllocatedBefore;
            phaseAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            phaseTimer.Restart();
        }

        // A road edge crossing a barrier is unsupported here just like in the explicit tier; defer so
        // the topology rebuild reports the condition consistently.
        if (AnyRoadEdgeCrossesBarrier(paths, hardConstraints, modelTolerance))
        {
            errorMessage = "Grade Path road edge crosses a hard constraint; split-keep conform deferred.";
            return null;
        }

        PreparedBarriers barriers = GradingBarriers.Build(hardConstraints);
        if (performanceTimings != null)
        {
            performanceTimings.BarrierPreparationMilliseconds = phaseTimer!.Elapsed.TotalMilliseconds;
            performanceTimings.BarrierPreparationAllocatedBytes =
                GC.GetTotalAllocatedBytes(precise: true) - phaseAllocatedBefore;
            phaseAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            phaseTimer.Restart();
        }

        var outputPolylines = new List<OutputPolyline>(paths.Length * 2);
        int nonDaylightingStations = 0;
        List<PathCorridor>? corridors = BuildCorridors(
            paths, terrain, barriers, tolerance, outputPolylines, ref nonDaylightingStations, out errorMessage);
        if (corridors is null)
            return null;
        if (performanceTimings != null)
        {
            performanceTimings.CorridorDaylightMilliseconds = phaseTimer!.Elapsed.TotalMilliseconds;
            performanceTimings.CorridorDaylightAllocatedBytes =
                GC.GetTotalAllocatedBytes(precise: true) - phaseAllocatedBefore;
            phaseAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            phaseTimer.Restart();
        }

        // Conform to each corridor's daylight envelope AND its road-edge footprint. Conforming to the
        // daylight alone leaves the road spanned by coarse terrain triangles with no crisp edge; the
        // footprint puts the left/right road edges into the mesh so the road top reads cleanly.
        var conformLoops = new List<double[]>(corridors.Count * 2);
        foreach (PathCorridor corridor in corridors)
        {
            conformLoops.Add(corridor.DaylightXy);
            if (corridor.RailLoopXy != null)
                conformLoops.Add(corridor.RailLoopXy);

            double[]? footprint = BuildCorridorFootprintLoop(corridor, tolerance);
            if (footprint != null)
                conformLoops.Add(footprint);
        }
        if (performanceTimings != null)
        {
            performanceTimings.LoopPreparationMilliseconds = phaseTimer!.Elapsed.TotalMilliseconds;
            performanceTimings.LoopPreparationAllocatedBytes =
                GC.GetTotalAllocatedBytes(precise: true) - phaseAllocatedBefore;
            phaseAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            phaseTimer.Restart();
        }

        MeshAreaTopologySplitter.PerformanceTimings? conformSplitDetails = performanceTimings != null
            ? new MeshAreaTopologySplitter.PerformanceTimings()
            : null;
        if (performanceTimings != null)
            performanceTimings.ConformSplitDetails = conformSplitDetails;
        MeshAreaSplitter.SplitResult? conformed = GradedRegionAssembler.SplitConform(
            vertices,
            vertexCount,
            faces,
            faceCount,
            conformLoops,
            tolerance,
            hardConstraints,
            conformSplitDetails);
        if (conformed is null)
        {
            errorMessage = "Grade Path terrain conform (split-keep) failed; deferring.";
            return null;
        }
        if (performanceTimings != null)
        {
            performanceTimings.ConformSplitMilliseconds = phaseTimer!.Elapsed.TotalMilliseconds;
            performanceTimings.ConformSplitAllocatedBytes =
                GC.GetTotalAllocatedBytes(precise: true) - phaseAllocatedBefore;
            phaseAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            phaseTimer.Restart();
        }

        // Watertight/manifold by construction — but gate anyway: the area splitter can go non-manifold
        // on dense/nested loops, and we must defer cleanly rather than emit it.
        MeshTopologyValidator.FlatBoundaryTopology conformedTopology =
            MeshTopologyValidator.AnalyzeBoundaryTopology(conformed.Faces, conformed.FaceCount);
        MeshTopologyValidator.BoundaryGraphAnalysis topology = conformedTopology.Analysis;
        MeshTopologyValidator.BoundaryGraphAnalysis terrainTopology =
            MeshTopologyValidator.AnalyzeBoundaryGraph(faces, faceCount);
        if (topology.NonManifoldEdgeCount > 0 ||
            topology.HasOpenBoundaryChains ||
            topology.BoundaryComponentCount > terrainTopology.BoundaryComponentCount)
        {
            errorMessage = GradedRegionAssembler.DescribeWeldTopologyFailure("Grade Path split-keep", topology, terrainTopology);
            return null;
        }
        if (performanceTimings != null)
        {
            performanceTimings.TopologyValidationMilliseconds = phaseTimer!.Elapsed.TotalMilliseconds;
            performanceTimings.TopologyValidationAllocatedBytes =
                GC.GetTotalAllocatedBytes(precise: true) - phaseAllocatedBefore;
            phaseAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            phaseTimer.Restart();
        }

        double[] gradedVertices = ApplyGradingZWithBoundaryTopology(
            conformed.Vertices,
            conformed.VertexCount,
            conformed.Faces,
            conformed.FaceCount,
            paths,
            hardConstraints,
            conformedTopology,
            out _);
        if (performanceTimings != null)
        {
            performanceTimings.ApplyGradingZMilliseconds = phaseTimer!.Elapsed.TotalMilliseconds;
            performanceTimings.ApplyGradingZAllocatedBytes =
                GC.GetTotalAllocatedBytes(precise: true) - phaseAllocatedBefore;
            phaseAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            phaseTimer.Restart();
        }

        int vc = conformed.VertexCount;
        var outXy = new double[vc * 2];
        var origZ = new double[vc];
        var newZ = new double[vc];
        for (int i = 0; i < vc; i++)
        {
            outXy[i * 2] = conformed.Vertices[i * 3];
            outXy[i * 2 + 1] = conformed.Vertices[i * 3 + 1];
            origZ[i] = conformed.Vertices[i * 3 + 2];
            newZ[i] = gradedVertices[i * 3 + 2];
        }

        const string modeMessage =
            "Grade Path topology mode: terrain conform (split-keep — corridor road edges and daylight loops conformed in place, watertight by construction).";
        var diagnostics = new List<string> { modeMessage };
        var structured = new List<GradingDiagnostic>
        {
            GradingDiagnostic.Information("grade_path.topology_mode.split_keep", modeMessage, operation: "grade_path")
        };

        if (nonDaylightingStations > 0)
        {
            string message =
                $"Grade Path batter did not reach existing ground at {nonDaylightingStations} station(s); the slope was clamped to the search extent there.";
            diagnostics.Add(message);
            structured.Add(GradingDiagnostic.Warning("grade_path.daylight.incomplete", message, operation: "grade_path"));
        }

        errorMessage = null;
        GradingResult result = BuildResult(
            outXy,
            origZ,
            newZ,
            gradedVertices,
            vc,
            conformed.Faces,
            conformed.FaceCount,
            outputPolylines,
            BuildPathPatchSummaries(paths),
            diagnostics,
            structured);
        if (performanceTimings != null)
        {
            performanceTimings.ResultAssemblyMilliseconds = phaseTimer!.Elapsed.TotalMilliseconds;
            performanceTimings.ResultAssemblyAllocatedBytes =
                GC.GetTotalAllocatedBytes(precise: true) - phaseAllocatedBefore;
        }

        return result;
    }

    /// <summary>
    /// Closed corridor footprint loop (left road edge forward, right road edge back) used as a
    /// conform constraint. At a bend tighter than the half-width the inner edge folds over itself;
    /// resolve that to the Clipper outer envelope so the splitter gets a simple loop. Returns null
    /// when no clean loop can be produced — the corridor then conforms to its daylight envelope only.
    /// </summary>
    private static double[]? BuildCorridorFootprintLoop(PathCorridor corridor, double tolerance)
    {
        int n = corridor.N;
        if (n < 2)
            return null;

        // A single line has no footprint area — its rails coincide — so there is no loop to conform
        // to. An open one-sided rail is one side of its own daylight envelope; a closed one conforms as
        // its own ring (PathCorridor.RailLoopXy).
        if (corridor.IsSingleLine)
            return null;

        var loop = new double[n * 4];
        for (int i = 0; i < n; i++)
        {
            loop[i * 2] = corridor.LeftXyz[i * 3];
            loop[i * 2 + 1] = corridor.LeftXyz[i * 3 + 1];
        }

        for (int i = 0; i < n; i++)
        {
            int src = n - 1 - i;
            int dst = n + i;
            loop[dst * 2] = corridor.RightXyz[src * 3];
            loop[dst * 2 + 1] = corridor.RightXyz[src * 3 + 1];
        }

        if (!GradingGeometry2D.ClosedPolylineSelfIntersects(loop, loop.Length / 2))
            return loop;

        if (ClipperGeometry.TryUnionClosedLoops(new[] { loop }, tolerance, out List<double[]> cleaned) &&
            ClipperGeometry.TryPickLargestLoop(cleaned, out double[] envelope) &&
            !GradingGeometry2D.ClosedPolylineSelfIntersects(envelope, envelope.Length / 2))
        {
            return envelope;
        }

        return null;
    }
}
