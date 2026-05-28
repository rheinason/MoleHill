using TriangleNet;
using TriangleNet.Geometry;
using TriangleNet.Meshing;
using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

public static partial class PadGrader
{
    private static string BuildProtectedPadFailureMessage(string primaryMessage, IReadOnlyList<string> diagnostics)
    {
        if (diagnostics.Count == 0)
            return primaryMessage;

        return primaryMessage + Environment.NewLine +
            "Grade Pad diagnostics:" + Environment.NewLine +
            string.Join(Environment.NewLine, diagnostics);
    }

    private static IReadOnlyList<OutputPolyline> BuildProtectedPadFailurePolylines(
        PreparedPadSections prepared,
        double[] daylightLoopXy,
        double[] seamLoopXy,
        TerrainFaceGrid terrainFaceGrid)
    {
        var polylines = new List<OutputPolyline>(3)
        {
            BuildPadBoundaryPolyline(prepared)
        };

        AddDiagnosticLoopPolyline(polylines, daylightLoopXy, terrainFaceGrid.InterpolateZ);
        if (!LoopsCoincide(daylightLoopXy, seamLoopXy, GradingTolerances.DefaultModelTolerance * 8.0))
            AddDiagnosticLoopPolyline(polylines, seamLoopXy, terrainFaceGrid.InterpolateZ);

        return polylines;
    }

    private static void AddDiagnosticLoopPolyline(
        List<OutputPolyline> polylines,
        double[] loopXy,
        Func<double, double, double> evaluateZ)
    {
        int vertexCount = loopXy.Length / 2;
        if (vertexCount < 3)
            return;

        var xyz = new double[vertexCount * 3];
        for (int i = 0; i < vertexCount; i++)
        {
            double x = loopXy[i * 2];
            double y = loopXy[i * 2 + 1];
            xyz[i * 3] = x;
            xyz[i * 3 + 1] = y;
            xyz[i * 3 + 2] = evaluateZ(x, y);
        }

        polylines.Add(new OutputPolyline(xyz, vertexCount, isClosed: true));
    }

    private static string[] BuildProtectedPadInteractionDiagnostics(
        int padIndex,
        double[] seamLoopXy,
        IReadOnlyList<GradingPatch> priorPatches,
        double tolerance)
    {
        if (priorPatches.Count == 0 || seamLoopXy.Length < 6)
            return Array.Empty<string>();

        var diagnostics = new List<string>();
        int seamVertexCount = seamLoopXy.Length / 2;
        double nearTolerance = Math.Max(tolerance * 8.0, 1e-6);
        for (int patchIndex = 0; patchIndex < priorPatches.Count; patchIndex++)
        {
            GradingPatch patch = priorPatches[patchIndex];
            int priorVertexCount = patch.OwnedRegionLoopXy.Length / 2;
            if (patch.Kind != GradingPatchKind.Pad || priorVertexCount < 3)
                continue;

            int seamInsidePrior = 0;
            int seamNearPrior = 0;
            for (int i = 0; i < seamVertexCount; i++)
            {
                double x = seamLoopXy[i * 2];
                double y = seamLoopXy[i * 2 + 1];
                if (PointInPolygon(x, y, patch.OwnedRegionLoopXy, priorVertexCount))
                    seamInsidePrior++;
                if (DistToPolygon(x, y, patch.OwnedRegionLoopXy, priorVertexCount) <= nearTolerance)
                    seamNearPrior++;
            }

            int priorInsideSeam = 0;
            for (int i = 0; i < priorVertexCount; i++)
            {
                double x = patch.OwnedRegionLoopXy[i * 2];
                double y = patch.OwnedRegionLoopXy[i * 2 + 1];
                if (PointInPolygon(x, y, seamLoopXy, seamVertexCount))
                    priorInsideSeam++;
            }

            if (seamInsidePrior == 0 && seamNearPrior == 0 && priorInsideSeam == 0)
                continue;

            diagnostics.Add(
                $"Grade Pad[{padIndex}] protected stitch loop interacts with prior {patch.OwnerKey}: seam inside={seamInsidePrior}/{seamVertexCount}, seam near={seamNearPrior}/{seamVertexCount}, prior inside seam={priorInsideSeam}/{priorVertexCount}. Adjacent or overlapping protected pad regions can fragment the outside seam.");
        }

        return diagnostics.ToArray();
    }

    private static bool TryBuildInteractingProtectedPadRegions(
        PadBoundary[] pads,
        TerrainSpatialIndex terrain,
        PreparedBarriers barriers,
        double tolerance,
        double minStitchSegmentLength,
        out ProtectedPadRegion[] interactingRegions)
    {
        interactingRegions = Array.Empty<ProtectedPadRegion>();
        if (pads.Length == 0)
            return false;

        var regions = new List<ProtectedPadRegion>(pads.Length);
        for (int padIndex = 0; padIndex < pads.Length; padIndex++)
        {
            PadBoundary pad = pads[padIndex];
            if (pad.StitchApronDistance <= tolerance * 4.0)
                continue;

            PreparedPadSections prepared = BuildPreparedPadSections(
                pad,
                terrain.FaceGrid,
                barriers,
                terrain.HasBoundaryLoop,
                terrain.BoundaryLoopXy,
                terrain.BoundaryVertexCount,
                tolerance,
                keepShoulderOnBatterPlane: true);

            if (!TryBuildOrderedShoulderLoopFromSections(
                    prepared.BoundaryLoopXy,
                    prepared.BoundaryVertexCount,
                    prepared.ShoulderXy,
                    tolerance,
                    out double[] daylightLoopXy,
                    out _) ||
                daylightLoopXy.Length < 6)
            {
                continue;
            }

            daylightLoopXy = SimplifyClosedLoopByShortEdges(daylightLoopXy, Math.Max(minStitchSegmentLength, 1e-6));
            if (daylightLoopXy.Length < 6)
                continue;

            double[] stitchLoopXy = daylightLoopXy;
            if (TryBuildProtectedStitchLoopFromSections(
                    prepared.BoundaryLoopXy,
                    daylightLoopXy,
                    pad.StitchApronDistance,
                    terrain.HasBoundaryLoop ? terrain.BoundaryLoopXy : null,
                    terrain.HasBoundaryLoop ? terrain.BoundaryVertexCount : 0,
                    tolerance,
                    out double[] sectionStitchLoopXy,
                    out _) ||
                TryBuildProtectedStitchLoop(
                    daylightLoopXy,
                    pad.StitchApronDistance,
                    terrain.HasBoundaryLoop ? terrain.BoundaryLoopXy : null,
                    terrain.HasBoundaryLoop ? terrain.BoundaryVertexCount : 0,
                    tolerance,
                    out sectionStitchLoopXy,
                    out _))
            {
                stitchLoopXy = SimplifyClosedLoopByShortEdges(sectionStitchLoopXy, Math.Max(minStitchSegmentLength, 1e-6));
            }

            if (stitchLoopXy.Length < 6)
                continue;

            regions.Add(new ProtectedPadRegion(
                padIndex,
                prepared,
                daylightLoopXy,
                stitchLoopXy,
                InflateBounds(GradingPatch.ComputeBounds(stitchLoopXy), minStitchSegmentLength)));
        }

        if (regions.Count < 2)
            return false;

        var selected = new bool[regions.Count];
        bool foundInteraction = false;
        for (int i = 0; i < regions.Count; i++)
        {
            for (int j = i + 1; j < regions.Count; j++)
            {
                Bounds2D leftBounds = regions[i].Bounds;
                Bounds2D rightBounds = regions[j].Bounds;
                if (!leftBounds.Intersects(rightBounds))
                    continue;

                if (!LoopsTouchOrOverlap(regions[i].Prepared.BoundaryLoopXy, regions[j].StitchLoopXy, minStitchSegmentLength) &&
                    !LoopsTouchOrOverlap(regions[j].Prepared.BoundaryLoopXy, regions[i].StitchLoopXy, minStitchSegmentLength))
                {
                    continue;
                }

                selected[i] = true;
                selected[j] = true;
                foundInteraction = true;
            }
        }

        if (!foundInteraction)
            return false;

        interactingRegions = regions.Where((_, index) => selected[index]).ToArray();
        return interactingRegions.Length >= 2;
    }

    private static bool TryGradeCoupledProtectedPadsByWholeMeshRemesh(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PadBoundary[] pads,
        LockCurve[]? lockCurves,
        PreparedBarriers barriers,
        double tolerance,
        double terrainDetailSize,
        IReadOnlyList<ProtectedPadRegion> interactingRegions,
        out GradingResult? result,
        out string? failure)
    {
        result = null;
        failure = null;

        int localInputVertexCount = EstimateInputVerticesInBounds(vertices, vertexCount, interactingRegions);
        int softVertexBudget = Math.Max(2000, Math.Max(localInputVertexCount * 8, vertexCount * 2));
        int hardVertexBudget = Math.Max(10000, Math.Max(localInputVertexCount * 25, vertexCount * 4));
        if (hardVertexBudget > 0 && localInputVertexCount > 0 && hardVertexBudget < localInputVertexCount)
            hardVertexBudget = localInputVertexCount * 25;

        PadTopologyResult? topology = TryTriangulateCoupledProtectedPadTopology(
            vertices,
            vertexCount,
            faces,
            faceCount,
            pads,
            lockCurves,
            interactingRegions,
            tolerance,
            terrainDetailSize,
            out string? topologyWarning);
        if (topology == null)
        {
            failure = BuildProtectedPadFailureMessage(
                $"Grade Pad coupled protected patch failed: {topologyWarning ?? "topology remesh failed"}",
                BuildCoupledProtectedPadDiagnostics(interactingRegions, localInputVertexCount, 0, softVertexBudget, hardVertexBudget, topologyWarning));
            return true;
        }

        if (topology.VertexCount > hardVertexBudget)
        {
            failure = BuildProtectedPadFailureMessage(
                $"Grade Pad coupled protected patch failed: density-cap-hit ({topology.VertexCount:N0} vertices > {hardVertexBudget:N0}).",
                BuildCoupledProtectedPadDiagnostics(interactingRegions, localInputVertexCount, topology.VertexCount, softVertexBudget, hardVertexBudget, topologyWarning));
            return true;
        }

        var inputTerrain = new TerrainSpatialIndex(vertices, vertexCount, faces, faceCount);
        bool hasBoundaryLoop = TryBuildBoundaryLoop(topology.Vertices, topology.Faces, topology.FaceCount, out double[] boundaryLoop, out int boundaryVertexCount);
        if (inputTerrain.HasBoundaryLoop)
        {
            double terrainBoundaryTolerance = Math.Max(tolerance * 8.0, terrainDetailSize > 0.0 && double.IsFinite(terrainDetailSize) ? terrainDetailSize * 2.0 : tolerance * 16.0);
            int interiorNakedEdges = CountBoundaryEdgesAwayFromReferenceBoundary(
                topology.Vertices,
                topology.Faces,
                topology.FaceCount,
                vertices,
                faces,
                faceCount,
                terrainBoundaryTolerance);
            if (interiorNakedEdges > 50)
            {
                failure = BuildProtectedPadFailureMessage(
                    $"Grade Pad coupled protected patch failed: {interiorNakedEdges} interior naked edge(s).",
                    BuildCoupledProtectedPadDiagnostics(interactingRegions, localInputVertexCount, topology.VertexCount, softVertexBudget, hardVertexBudget, topologyWarning));
                return true;
            }
        }

        int[] topologyFaces = topology.Faces;
        int topologyFaceCount = topology.FaceCount;
        var TerrainFaceGrid = new TerrainFaceGrid(topology.Vertices, topology.VertexCount, topologyFaces, topologyFaceCount);
        var gradedVertices = (double[])topology.Vertices.Clone();
        ApplyGradingToVerticesWithSections(
            gradedVertices,
            topology.Vertices,
            topology.VertexCount,
            pads,
            barriers,
            TerrainFaceGrid,
            hasBoundaryLoop,
            boundaryLoop,
            boundaryVertexCount,
            tolerance,
            keepShoulderOnBatterPlane: true);
        gradedVertices = SmoothCoupledProtectedShoulders(
            gradedVertices,
            topology.VertexCount,
            topologyFaces,
            topologyFaceCount,
            pads,
            interactingRegions,
            tolerance);

        int repairedBoundaryLoopCount = 0;
        if (MeshTopologyOperations.TryFillSmallBranchedBoundaryLoops(
                gradedVertices,
                topology.VertexCount,
                topologyFaces,
                topologyFaceCount,
                tolerance,
                out int[] repairedTopologyFaces,
                out int repairedTopologyFaceCount,
                out repairedBoundaryLoopCount))
        {
            topologyFaces = repairedTopologyFaces;
            topologyFaceCount = repairedTopologyFaceCount;
        }

        var diagnostics = new List<string>();
        diagnostics.AddRange(BuildCoupledProtectedPadDiagnostics(
            interactingRegions,
            localInputVertexCount,
            topology.VertexCount,
            softVertexBudget,
            hardVertexBudget,
            topologyWarning));
        var gradedTopologyPatch = new PatchMeshResult
        {
            Vertices = gradedVertices,
            VertexCount = topology.VertexCount,
            Faces = topologyFaces,
            FaceCount = topologyFaceCount,
            StitchLoopXy = Array.Empty<double>()
        };
        foreach (ProtectedPadRegion region in interactingRegions)
            diagnostics.AddRange(BuildPadSlopeDiagnostics(region.PadIndex, region.Prepared, gradedTopologyPatch, tolerance));
        if (repairedBoundaryLoopCount > 0)
            diagnostics.Add($"Grade Pad coupled protected patch topology repair filled {repairedBoundaryLoopCount:N0} tiny branched boundary loop(s).");
        if (double.IsFinite(terrainDetailSize) && terrainDetailSize > 0.0)
            diagnostics.Add($"Grade Pad coupled protected patch detail size: {terrainDetailSize:F6}.");
        foreach (ProtectedPadRegion region in interactingRegions)
        {
            ComputeLoopDistanceStats(region.DaylightLoopXy, region.StitchLoopXy, out double shoulderToSeamMin, out double shoulderToSeamMax);
            ComputeLoopDistanceStats(region.StitchLoopXy, region.DaylightLoopXy, out double seamToShoulderMin, out double seamToShoulderMax);
            diagnostics.Add(
                $"Grade Pad[{region.PadIndex}] topology band width: shoulder->seam min={shoulderToSeamMin:F6}, max={shoulderToSeamMax:F6}; seam->shoulder min={seamToShoulderMin:F6}, max={seamToShoulderMax:F6}.");
            diagnostics.Add(
                $"Grade Pad[{region.PadIndex}] merged-mesh naked edges near seam: {CountBoundaryEdgesNearLoop(topology.Vertices, topologyFaces, topologyFaceCount, region.StitchLoopXy, tolerance * 4.0)}.");
        }
        if (topology.VertexCount > softVertexBudget)
            diagnostics.Add($"Grade Pad coupled protected patch density warning: {topology.VertexCount:N0} vertices exceeds soft budget {softVertexBudget:N0}.");

        result = BuildResult(
            topology.Vertices,
            topology.VertexCount,
            topologyFaces,
            topologyFaceCount,
            gradedVertices,
            topology.PadPolylines,
            diagnostics,
            BuildPadPatchSummaries(pads));
        return true;
    }

    private static IReadOnlyList<string> BuildCoupledProtectedPadDiagnostics(
        IReadOnlyList<ProtectedPadRegion> interactingRegions,
        int localInputVertexCount,
        int outputVertexCount,
        int softVertexBudget,
        int hardVertexBudget,
        string? topologyWarning)
    {
        var diagnostics = new List<string>
        {
            $"Grade Pad coupled protected patch: {interactingRegions.Count} interacting protected pad(s); local input vertices={localInputVertexCount:N0}, output vertices={outputVertexCount:N0}, soft cap={softVertexBudget:N0}, hard cap={hardVertexBudget:N0}.",
            "Grade Pad coupled protected patch policy: higher pad tops own overlaps; shoulders blend inside the shared protected region."
        };
        foreach (ProtectedPadRegion region in interactingRegions)
        {
            diagnostics.Add(
                $"Grade Pad[{region.PadIndex}] protected stitch apron: daylight->{region.Prepared.Pad.StitchApronDistance:F6} with {region.StitchLoopXy.Length / 2} stitch vertices.");
        }

        if (!string.IsNullOrWhiteSpace(topologyWarning))
            diagnostics.Add($"Grade Pad coupled protected patch topology warning: {topologyWarning}");

        return diagnostics;
    }

    private static double[] SmoothCoupledProtectedShoulders(
        double[] gradedVertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        IReadOnlyList<PadBoundary> pads,
        IReadOnlyList<ProtectedPadRegion> interactingRegions,
        double tolerance)
    {
        var boundaries = interactingRegions
            .Where(static region => region.StitchLoopXy.Length >= 6)
            .Select(static region => (region.StitchLoopXy, region.StitchLoopXy.Length / 2, 0.35))
            .ToArray();
        if (boundaries.Length == 0)
            return gradedVertices;

        var breaklines = pads
            .Where(static pad => pad.VertexCount >= 3)
            .Select(static pad => new MeshSmoother.BreaklinePolyline(pad.XyVertices, pad.VertexCount, IsClosed: true))
            .ToArray();

        return MeshSmoother.Smooth(
            gradedVertices,
            vertexCount,
            faces,
            faceCount,
            boundaries,
            globalStrength: 0.0,
            breaklines,
            breaklineFixity: 1.0,
            snapTolerance: tolerance * 8.0,
            iterations: 3);
    }

    private static PadTopologyResult? TryTriangulateCoupledProtectedPadTopology(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PadBoundary[] pads,
        LockCurve[]? lockCurves,
        IReadOnlyList<ProtectedPadRegion> interactingRegions,
        double tolerance,
        double terrainDetailSize,
        out string? warningOrError)
    {
        warningOrError = null;
        if (interactingRegions.Count != pads.Length)
            return null;

        double dedupTol = GradingTolerances.ModelToleranceOrDefault(tolerance);
        var stitchLoops = interactingRegions.Select(static region => region.StitchLoopXy).ToArray();
        if (!ClipperGeometry.TryUnionClosedLoops(stitchLoops, dedupTol, out List<double[]> unionLoops) ||
            unionLoops.Count == 0)
        {
            warningOrError = "coupled protected stitch envelopes could not be unioned.";
            return null;
        }

        var daylightLoops = interactingRegions.Select(static region => region.DaylightLoopXy).ToArray();
        List<double[]> unionDaylightLoops = new();
        if (ClipperGeometry.TryUnionClosedLoops(daylightLoops, dedupTol, out List<double[]> daylightUnion))
            unionDaylightLoops = daylightUnion;

        var xyList = new List<double>(vertexCount * 2);
        var zList = new List<double>(vertexCount);
        var segList = new List<(int a, int b)>();
        var cullSegList = new List<(int a, int b)>();
        var vertHash = new SpatialVertexHash(dedupTol);
        var TerrainFaceGrid = new TerrainFaceGrid(vertices, vertexCount, faces, faceCount);
        var originalIndexMap = new Dictionary<int, int>(vertexCount);
        double interiorSampleSpacing = ResolveCoupledInteriorSampleSpacing(dedupTol, terrainDetailSize);
        bool hasInputBoundaryLoop = TryBuildBoundaryLoop(vertices, faces, faceCount, out double[] inputBoundaryLoop, out int inputBoundaryVertexCount);

        int AddOriginalVertex(int originalIndex, bool forceInclude = false)
        {
            if (originalIndexMap.TryGetValue(originalIndex, out int existing))
                return existing;

            double x = vertices[originalIndex * 3];
            double y = vertices[originalIndex * 3 + 1];
            if (!forceInclude && IsInsideAnyLoop(x, y, unionLoops, dedupTol))
            {
                originalIndexMap.Add(originalIndex, -1);
                return -1;
            }

            int near = vertHash.FindNearest(xyList, x, y, dedupTol);
            if (near >= 0)
            {
                originalIndexMap.Add(originalIndex, near);
                return near;
            }

            int idx = zList.Count;
            xyList.Add(x);
            xyList.Add(y);
            zList.Add(vertices[originalIndex * 3 + 2]);
            vertHash.Insert(idx, x, y);
            originalIndexMap.Add(originalIndex, idx);
            return idx;
        }

        int AddVertex(double x, double y)
        {
            int near = vertHash.FindNearest(xyList, x, y, dedupTol);
            if (near >= 0)
                return near;

            int idx = zList.Count;
            xyList.Add(x);
            xyList.Add(y);
            zList.Add(TerrainFaceGrid.InterpolateZ(x, y));
            vertHash.Insert(idx, x, y);
            return idx;
        }

        for (int i = 0; i < vertexCount; i++)
            AddOriginalVertex(i);

        var edgeFaceCount = new Dictionary<long, int>(8, IndexedMeshTools.EdgeKeyComparer.Instance);
        for (int f = 0; f < faceCount; f++)
        {
            int a = faces[f * 3];
            int b = faces[f * 3 + 1];
            int c = faces[f * 3 + 2];
            IncrEdge(edgeFaceCount, a, b);
            IncrEdge(edgeFaceCount, b, c);
            IncrEdge(edgeFaceCount, c, a);
        }

        foreach (var kvp in edgeFaceCount)
        {
            if (kvp.Value != 1)
                continue;

            int a = (int)(kvp.Key >> 32);
            int b = (int)(kvp.Key & 0xFFFFFFFFL);
            int mappedA = AddOriginalVertex(a, forceInclude: true);
            int mappedB = AddOriginalVertex(b, forceInclude: true);
            if (mappedA >= 0 && mappedB >= 0 && mappedA != mappedB)
            {
                segList.Add((mappedA, mappedB));
                cullSegList.Add((mappedA, mappedB));
            }
        }

        var padPolylines = new List<OutputPolyline>(pads.Length);
        for (int padIndex = 0; padIndex < pads.Length; padIndex++)
        {
            PadBoundary pad = pads[padIndex];
            double shoulderDistance = ComputePadTransitionDistance(vertices, vertexCount, pad);
            double segmentLength = ComputePadConstraintSegmentLength(shoulderDistance);
            ConstraintLoop padLoop = BuildClosedConstraintLoop(pad.XyVertices, pad.VertexCount, segmentLength, dedupTol);
            AddClosedLoopVertices(padLoop.XyVertices, padLoop.VertexCount, AddVertex);

            var padPolyXyz = new double[padLoop.VertexCount * 3];
            for (int i = 0; i < padLoop.VertexCount; i++)
            {
                double x = padLoop.XyVertices[i * 2];
                double y = padLoop.XyVertices[i * 2 + 1];
                padPolyXyz[i * 3] = x;
                padPolyXyz[i * 3 + 1] = y;
                padPolyXyz[i * 3 + 2] = pad.EvaluateZ(x, y);
            }

            padPolylines.Add(new OutputPolyline(padPolyXyz, padLoop.VertexCount, isClosed: true));
        }

        for (int i = 0; i < unionDaylightLoops.Count; i++)
        {
            double[] loop = SimplifyClosedLoopByShortEdges(unionDaylightLoops[i], Math.Max(dedupTol * 4.0, 1e-6));
            AddClosedLoopVertices(loop, loop.Length / 2, AddVertex);
        }

        AddCoupledPadBandGuideVertices(interactingRegions, AddVertex);

        AddCoupledInteriorGuideVertices(
            unionLoops,
            daylightLoops,
            pads,
            interiorSampleSpacing,
            dedupTol,
            AddVertex);

        if (lockCurves != null)
        {
            foreach (LockCurve lockCurve in lockCurves)
            {
                if (lockCurve.VertexCount < 2 || lockCurve.XyVertices.Length < lockCurve.VertexCount * 2)
                    continue;

                for (int i = 0; i < lockCurve.VertexCount; i++)
                    AddVertex(lockCurve.XyVertices[i * 2], lockCurve.XyVertices[i * 2 + 1]);
            }
        }

        if (zList.Count < 3)
        {
            warningOrError = "Too few vertices for coupled protected triangulation.";
            return null;
        }

        TriangulationOutcome triangulation = TriangulationHelper.Triangulate(
            xyList,
            zList.Count,
            segList,
            maxArea: 0.0,
            minAngle: 0.0,
            convex: false);
        if (triangulation.Mesh == null)
        {
            warningOrError = triangulation.WarningMessage ?? "Coupled protected triangulation failed.";
            return null;
        }

        if (!string.IsNullOrWhiteSpace(triangulation.WarningMessage))
        {
            warningOrError = hasInputBoundaryLoop &&
                             triangulation.WarningMessage.Contains("Constraints could not be enforced", StringComparison.OrdinalIgnoreCase)
                ? "Guide-only coupled triangulation used terrain-boundary post-filtering."
                : triangulation.WarningMessage;
        }

        var extracted = TriangleNetExtractor.Extract(triangulation.Mesh);
        var topologyVertices = new double[extracted.VertexCount * 3];
        for (int i = 0; i < extracted.VertexCount; i++)
        {
            double x = extracted.Xy[i * 2];
            double y = extracted.Xy[i * 2 + 1];
            int sourceId = extracted.SourceIds[i];
            double z = sourceId >= 0 && sourceId < zList.Count
                ? zList[sourceId]
                : TerrainFaceGrid.InterpolateZ(x, y);
            topologyVertices[i * 3] = x;
            topologyVertices[i * 3 + 1] = y;
            topologyVertices[i * 3 + 2] = z;
        }

        int outVertCount = extracted.VertexCount;
        int outFaceCount = extracted.FaceCount;
        int[] topologyFaces = extracted.Faces;
        if (hasInputBoundaryLoop)
        {
            FilterMeshFacesInsideLoop(
                ref topologyVertices,
                ref outVertCount,
                ref topologyFaces,
                ref outFaceCount,
                inputBoundaryLoop,
                inputBoundaryVertexCount,
                dedupTol * 4.0);

            var cullResult = TriangleBoundaryCuller.Cull(
                topologyVertices,
                outVertCount,
                topologyFaces,
                outFaceCount,
                xyList.ToArray(),
                IndexedMeshTools.FlattenSegments(cullSegList),
                0);

            if (cullResult.Changed)
            {
                double[] culledVertices = IndexedMeshTools.CompactDoubleData(topologyVertices, 3, cullResult.NewToOld, cullResult.VertexCount);
                int culledInteriorNakedEdges = CountBoundaryEdgesAwayFromReferenceBoundary(
                    culledVertices,
                    cullResult.Faces,
                    cullResult.FaceCount,
                    vertices,
                    faces,
                    faceCount,
                    dedupTol * 4.0);

                if (culledInteriorNakedEdges <= 50)
                {
                    topologyVertices = culledVertices;
                    topologyFaces = cullResult.Faces;
                    outVertCount = cullResult.VertexCount;
                    outFaceCount = cullResult.FaceCount;
                }
            }
        }
        else
        {
            var cullResult = TriangleBoundaryCuller.Cull(
                topologyVertices,
                outVertCount,
                topologyFaces,
                outFaceCount,
                xyList.ToArray(),
                IndexedMeshTools.FlattenSegments(cullSegList),
                0);

            if (cullResult.Changed)
            {
                topologyVertices = IndexedMeshTools.CompactDoubleData(topologyVertices, 3, cullResult.NewToOld, cullResult.VertexCount);
                topologyFaces = cullResult.Faces;
                outVertCount = cullResult.VertexCount;
                outFaceCount = cullResult.FaceCount;
            }
        }

        return new PadTopologyResult
        {
            Vertices = topologyVertices,
            VertexCount = outVertCount,
            Faces = topologyFaces,
            FaceCount = outFaceCount,
            PadPolylines = padPolylines.ToArray()
        };
    }

    private static void FilterMeshFacesInsideLoop(
        ref double[] vertices,
        ref int vertexCount,
        ref int[] faces,
        ref int faceCount,
        double[] boundaryLoopXy,
        int boundaryVertexCount,
        double tolerance)
    {
        if (boundaryVertexCount < 3 || faceCount <= 0)
            return;

        var keptFaces = new List<int>(faces.Length);
        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            int a = faces[faceIndex * 3];
            int b = faces[faceIndex * 3 + 1];
            int c = faces[faceIndex * 3 + 2];
            double cx = (vertices[a * 3] + vertices[b * 3] + vertices[c * 3]) / 3.0;
            double cy = (vertices[a * 3 + 1] + vertices[b * 3 + 1] + vertices[c * 3 + 1]) / 3.0;
            if (PointInPolygon(cx, cy, boundaryLoopXy, boundaryVertexCount) ||
                DistToPolygon(cx, cy, boundaryLoopXy, boundaryVertexCount) <= tolerance)
            {
                keptFaces.Add(a);
                keptFaces.Add(b);
                keptFaces.Add(c);
            }
        }

        if (keptFaces.Count == faces.Length)
            return;

        int keptFaceCount = keptFaces.Count / 3;
        if (keptFaceCount == 0)
            return;

        int[] filteredFaces = keptFaces.ToArray();
        var compact = IndexedMeshTools.Compact(vertexCount, filteredFaces, keptFaceCount);
        vertices = IndexedMeshTools.CompactDoubleData(vertices, 3, compact.NewToOld, compact.VertexCount);
        faces = compact.Faces;
        vertexCount = compact.VertexCount;
        faceCount = compact.FaceCount;
    }

    private static double ResolveCoupledInteriorSampleSpacing(double tolerance, double terrainDetailSize)
    {
        double resolvedDetail = double.IsFinite(terrainDetailSize) && terrainDetailSize > 0.0
            ? terrainDetailSize
            : tolerance * 25.0;
        return Math.Clamp(resolvedDetail * 4.0, Math.Max(tolerance * 16.0, 0.25), 2.0);
    }

    private static void AddCoupledInteriorGuideVertices(
        IReadOnlyList<double[]> envelopeLoops,
        IReadOnlyList<double[]> daylightLoops,
        IReadOnlyList<PadBoundary> pads,
        double spacing,
        double tolerance,
        Func<double, double, int> addVertex)
    {
        if (envelopeLoops.Count == 0 || !double.IsFinite(spacing) || spacing <= tolerance)
            return;

        int added = 0;
        int maxAdded = 2500;
        double nearLoopTolerance = Math.Max(tolerance * 8.0, spacing * 0.28);
        foreach (double[] envelopeLoop in envelopeLoops)
        {
            int envelopeCount = envelopeLoop.Length / 2;
            if (envelopeCount < 3)
                continue;

            Bounds2D bounds = GradingPatch.ComputeBounds(envelopeLoop);
            double startX = Math.Floor(bounds.MinX / spacing) * spacing;
            double startY = Math.Floor(bounds.MinY / spacing) * spacing;
            for (double y = startY; y <= bounds.MaxY && added < maxAdded; y += spacing)
            {
                for (double x = startX; x <= bounds.MaxX && added < maxAdded; x += spacing)
                {
                    if (x < bounds.MinX + spacing * 0.25 ||
                        x > bounds.MaxX - spacing * 0.25 ||
                        y < bounds.MinY + spacing * 0.25 ||
                        y > bounds.MaxY - spacing * 0.25)
                    {
                        continue;
                    }

                    if (!PointInPolygon(x, y, envelopeLoop, envelopeCount))
                        continue;

                    if (IsNearAnyLoop(x, y, envelopeLoops, nearLoopTolerance) ||
                        IsNearAnyLoop(x, y, daylightLoops, nearLoopTolerance) ||
                        IsNearAnyPadBoundary(x, y, pads, nearLoopTolerance))
                    {
                        continue;
                    }

                    addVertex(x, y);
                    added++;
                }
            }
        }
    }

    private static void AddCoupledPadBandGuideVertices(
        IReadOnlyList<ProtectedPadRegion> regions,
        Func<double, double, int> addVertex)
    {
        for (int regionIndex = 0; regionIndex < regions.Count; regionIndex++)
        {
            ProtectedPadRegion region = regions[regionIndex];
            int daylightCount = region.DaylightLoopXy.Length / 2;
            int stitchCount = region.StitchLoopXy.Length / 2;
            int sampleCount = Math.Clamp((Math.Max(daylightCount, stitchCount) + 1) / 2, 16, 192);
            for (int i = 0; i < sampleCount; i++)
            {
                double station = i / (double)sampleCount;
                if (!TrySampleLoopAtFraction(region.Prepared.BoundaryLoopXy, region.Prepared.BoundaryVertexCount, station, out double bx, out double by) ||
                    !TrySampleLoopAtFraction(region.DaylightLoopXy, daylightCount, station, out double dx, out double dy))
                {
                    continue;
                }

                addVertex(bx, by);
                addVertex(LerpValue(bx, dx, 0.33), LerpValue(by, dy, 0.33));
                addVertex(LerpValue(bx, dx, 0.66), LerpValue(by, dy, 0.66));
                addVertex(dx, dy);

                if (stitchCount >= 3 &&
                    TrySampleLoopAtFraction(region.StitchLoopXy, stitchCount, station, out double sx, out double sy))
                {
                    addVertex(LerpValue(dx, sx, 0.5), LerpValue(dy, sy, 0.5));
                    addVertex(sx, sy);
                }
            }
        }
    }

    private static bool IsInsideAnyLoop(double x, double y, IReadOnlyList<double[]> loops, double tolerance)
    {
        for (int i = 0; i < loops.Count; i++)
        {
            double[] loop = loops[i];
            int vertexCount = loop.Length / 2;
            if (vertexCount < 3)
                continue;

            if (PointInPolygon(x, y, loop, vertexCount) ||
                DistToPolygon(x, y, loop, vertexCount) <= tolerance)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsNearAnyLoop(double x, double y, IReadOnlyList<double[]> loops, double tolerance)
    {
        for (int i = 0; i < loops.Count; i++)
        {
            double[] loop = loops[i];
            int vertexCount = loop.Length / 2;
            if (vertexCount >= 3 && DistToPolygon(x, y, loop, vertexCount) <= tolerance)
                return true;
        }

        return false;
    }

    private static bool IsNearAnyPadBoundary(double x, double y, IReadOnlyList<PadBoundary> pads, double tolerance)
    {
        for (int i = 0; i < pads.Count; i++)
        {
            PadBoundary pad = pads[i];
            if (pad.VertexCount >= 3 && DistToPolygon(x, y, pad.XyVertices, pad.VertexCount) <= tolerance)
                return true;
        }

        return false;
    }

    private static int EstimateInputVerticesInBounds(
        double[] vertices,
        int vertexCount,
        IReadOnlyList<ProtectedPadRegion> regions)
    {
        int count = 0;
        for (int i = 0; i < vertexCount; i++)
        {
            double x = vertices[i * 3];
            double y = vertices[i * 3 + 1];
            for (int r = 0; r < regions.Count; r++)
            {
                Bounds2D bounds = regions[r].Bounds;
                if (x < bounds.MinX || x > bounds.MaxX || y < bounds.MinY || y > bounds.MaxY)
                    continue;

                count++;
                break;
            }
        }

        return Math.Max(count, 1);
    }

    private static Bounds2D InflateBounds(Bounds2D bounds, double padding)
    {
        return new Bounds2D(
            bounds.MinX - padding,
            bounds.MaxX + padding,
            bounds.MinY - padding,
            bounds.MaxY + padding);
    }

    private static bool LoopsTouchOrOverlap(double[] leftLoopXy, double[] rightLoopXy, double tolerance)
    {
        int leftCount = leftLoopXy.Length / 2;
        int rightCount = rightLoopXy.Length / 2;
        if (leftCount < 3 || rightCount < 3)
            return false;

        for (int i = 0; i < leftCount; i++)
        {
            double x = leftLoopXy[i * 2];
            double y = leftLoopXy[i * 2 + 1];
            if (PointInPolygon(x, y, rightLoopXy, rightCount) ||
                DistToPolygon(x, y, rightLoopXy, rightCount) <= tolerance)
            {
                return true;
            }
        }

        for (int i = 0; i < rightCount; i++)
        {
            double x = rightLoopXy[i * 2];
            double y = rightLoopXy[i * 2 + 1];
            if (PointInPolygon(x, y, leftLoopXy, leftCount) ||
                DistToPolygon(x, y, leftLoopXy, leftCount) <= tolerance)
            {
                return true;
            }
        }

        for (int i = 0; i < leftCount; i++)
        {
            int iNext = (i + 1) % leftCount;
            double ax = leftLoopXy[i * 2];
            double ay = leftLoopXy[i * 2 + 1];
            double bx = leftLoopXy[iNext * 2];
            double by = leftLoopXy[iNext * 2 + 1];
            for (int j = 0; j < rightCount; j++)
            {
                int jNext = (j + 1) % rightCount;
                if (SegmentsIntersect(
                        ax,
                        ay,
                        bx,
                        by,
                        rightLoopXy[j * 2],
                        rightLoopXy[j * 2 + 1],
                        rightLoopXy[jNext * 2],
                        rightLoopXy[jNext * 2 + 1]))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
