using TriangleNet;
using TriangleNet.Geometry;
using TriangleNet.Meshing;
using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

/// <summary>
/// Grades a terrain mesh by assigning a planar finished surface inside
/// boundary curves, with controlled slope transitions.
/// Re-triangulates the entire mesh with pad boundaries as constrained edges.
/// </summary>
public static partial class PadGrader
{
    public const double DefaultStitchApronDistance = 0.0;

    /// <summary>
    /// Apply pad grading to a terrain mesh.
    /// Each pad carries its own slope angle and max distance.
    /// Later pads in the array override earlier ones in overlapping zones.
    /// </summary>
    public static GradingResult? Grade(
        double[] vertices, int vertexCount,
        int[] faces, int faceCount,
        PadBoundary[] pads,
        LockCurve[]? lockCurves,
        out string? errorMessage,
        double modelTolerance = GradingTolerances.DefaultModelTolerance)
    {
        return Grade(
            vertices,
            vertexCount,
            faces,
            faceCount,
            pads,
            lockCurves,
            out errorMessage,
            out _,
            modelTolerance,
            terrainDetailSize: 0.0);
    }

    public static GradingResult? Grade(
        double[] vertices, int vertexCount,
        int[] faces, int faceCount,
        PadBoundary[] pads,
        LockCurve[]? lockCurves,
        out string? errorMessage,
        out IReadOnlyList<OutputPolyline> failureOutputPolylines,
        double modelTolerance = GradingTolerances.DefaultModelTolerance,
        double terrainDetailSize = 0.0)
    {
        errorMessage = null;
        failureOutputPolylines = Array.Empty<OutputPolyline>();

        if (!ValidatePads(pads, out errorMessage))
            return null;

        pads = OrderPadsForOwnership(pads);

        GradingResult? stitched = GradeWithStitchedPatches(
            vertices,
            vertexCount,
            faces,
            faceCount,
            pads,
            lockCurves,
            modelTolerance,
            terrainDetailSize,
            out failureOutputPolylines,
            out errorMessage);

        if (stitched == null && ShouldRetryProtectedPadsInReversePriority(pads, errorMessage))
        {
            IReadOnlyList<OutputPolyline> primaryFailurePolylines = failureOutputPolylines;
            string? primaryError = errorMessage;
            PadBoundary[] reversedPads = pads.AsEnumerable().Reverse().ToArray();
            GradingResult? reversed = GradeWithStitchedPatches(
                vertices,
                vertexCount,
                faces,
                faceCount,
                reversedPads,
                lockCurves,
                modelTolerance,
                terrainDetailSize,
                out IReadOnlyList<OutputPolyline> reversedFailurePolylines,
                out string? reversedError);

            if (reversed != null)
                return reversed;

            failureOutputPolylines = reversedFailurePolylines.Count > 0
                ? reversedFailurePolylines
                : primaryFailurePolylines;
            errorMessage = primaryError ?? reversedError;
        }

        if (stitched == null && string.IsNullOrWhiteSpace(errorMessage))
            errorMessage = "Grade Pad local patch rebuild failed.";

        return stitched;
    }

    private static bool ShouldRetryProtectedPadsInReversePriority(PadBoundary[] pads, string? errorMessage)
    {
        if (pads.Length < 2 ||
            !pads.Any(static pad => pad.StitchApronDistance > GradingTolerances.DefaultModelTolerance * 4.0) ||
            string.IsNullOrWhiteSpace(errorMessage))
        {
            return false;
        }

        return errorMessage.Contains("interacts with prior pad", StringComparison.OrdinalIgnoreCase) ||
               errorMessage.Contains("fragment the outside seam", StringComparison.OrdinalIgnoreCase) ||
               errorMessage.Contains("coupled remesh could not enforce protected pad constraints", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class PatchMeshResult
    {
        public required double[] Vertices { get; init; }
        public required int VertexCount { get; init; }
        public required int[] Faces { get; init; }
        public required int FaceCount { get; init; }
        public double[]? StitchLoopXy { get; init; }
        public int CornerConstraintCount { get; init; }
        public bool CornerConstraintsRejected { get; set; }
    }

    private readonly record struct OrderedPad(PadBoundary Pad, int OriginalIndex, double Priority);

    private static PadBoundary[] OrderPadsForOwnership(PadBoundary[] pads)
    {
        if (pads.Length <= 1)
            return pads;

        return pads
            .Select((pad, index) => new OrderedPad(pad, index, ComputePadOwnershipPriority(pad)))
            .OrderBy(entry => entry.Priority)
            .ThenBy(entry => entry.OriginalIndex)
            .Select(entry => entry.Pad)
            .ToArray();
    }

    private static double ComputePadOwnershipPriority(PadBoundary pad)
    {
        double sumX = 0.0;
        double sumY = 0.0;
        for (int i = 0; i < pad.VertexCount; i++)
        {
            sumX += pad.XyVertices[i * 2];
            sumY += pad.XyVertices[i * 2 + 1];
        }

        double centroidX = sumX / pad.VertexCount;
        double centroidY = sumY / pad.VertexCount;
        return pad.EvaluateZ(centroidX, centroidY);
    }


    private static GradingResult? GradeWithStitchedPatches(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PadBoundary[] pads,
        LockCurve[]? lockCurves,
        double modelTolerance,
        double terrainDetailSize,
        out IReadOnlyList<OutputPolyline> failureOutputPolylines,
        out string? errorMessage)
    {
        errorMessage = null;
        failureOutputPolylines = Array.Empty<OutputPolyline>();
        double dedupTol = GradingTolerances.ModelToleranceOrDefault(modelTolerance);
        double minStitchSegmentLength = ComputeMinimumStitchSegmentLength(dedupTol, terrainDetailSize);

        double[] currentVertices = (double[])vertices.Clone();
        int currentVertexCount = vertexCount;
        int[] currentFaces = (int[])faces.Clone();
        int currentFaceCount = faceCount;
        var outputPolylines = new List<OutputPolyline>(pads.Length);
        var diagnostics = new List<string>();
        var patchSummaries = new List<GradingPatch>(pads.Length);
        PreparedBarriers barriers = lockCurves != null && lockCurves.Length > 0
            ? GradingBarriers.BuildFromLockCurves(lockCurves)
            : PreparedBarriers.Empty;
        var originalTerrain = new TerrainSpatialIndex(vertices, vertexCount, faces, faceCount);

        bool[] coupledPadSolved = new bool[pads.Length];
        if (TryBuildInteractingProtectedPadRegions(
                pads,
                originalTerrain,
                barriers,
                dedupTol,
                minStitchSegmentLength,
                out ProtectedPadRegion[] interactingRegions))
        {
            PadBoundary[] coupledPads = interactingRegions
                .Select(static region => region.Prepared.Pad)
                .ToArray();

            if (TryGradeCoupledProtectedPadsByWholeMeshRemesh(
                    vertices,
                    vertexCount,
                    faces,
                    faceCount,
                    coupledPads,
                    lockCurves,
                    barriers,
                    dedupTol,
                    terrainDetailSize,
                    interactingRegions,
                    out GradingResult? coupledResult,
                    out string? coupledFailure))
            {
                if (coupledResult != null)
                {
                    if (interactingRegions.Length == pads.Length)
                        return coupledResult;

                    currentVertices = coupledResult.Vertices;
                    currentVertexCount = coupledResult.VertexCount;
                    currentFaces = coupledResult.Faces;
                    currentFaceCount = coupledResult.FaceCount;
                    diagnostics.AddRange(coupledResult.Diagnostics);
                    outputPolylines.AddRange(coupledResult.OutputPolylines);
                    foreach (ProtectedPadRegion region in interactingRegions)
                    {
                        coupledPadSolved[region.PadIndex] = true;
                        patchSummaries.Add(BuildPadPatchSummary(region.Prepared, region.StitchLoopXy, region.PadIndex, dedupTol));
                    }
                }
                else
                {
                    errorMessage = coupledFailure ?? "Grade Pad coupled protected patch failed.";
                    failureOutputPolylines = interactingRegions
                        .SelectMany(region => BuildProtectedPadFailurePolylines(region.Prepared, region.DaylightLoopXy, region.StitchLoopXy, originalTerrain.FaceGrid))
                        .ToArray();
                    return null;
                }
            }
        }

        for (int padIndex = 0; padIndex < pads.Length; padIndex++)
        {
            if (coupledPadSolved[padIndex])
                continue;

            double[] padStartVertices = currentVertices;
            int padStartVertexCount = currentVertexCount;
            int[] padStartFaces = currentFaces;
            int padStartFaceCount = currentFaceCount;
            var currentTerrain = new TerrainSpatialIndex(currentVertices, currentVertexCount, currentFaces, currentFaceCount);
            TerrainFaceGrid currentFaceGrid = currentTerrain.FaceGrid;
            bool hasTerrainBoundary = currentTerrain.HasBoundaryLoop;
            double[] terrainBoundaryLoop = currentTerrain.BoundaryLoopXy;
            int terrainBoundaryVertexCount = currentTerrain.BoundaryVertexCount;

            PreparedPadSections prepared = BuildPreparedPadSections(
                pads[padIndex],
                currentFaceGrid,
                barriers,
                hasTerrainBoundary,
                terrainBoundaryLoop,
                terrainBoundaryVertexCount,
                dedupTol,
                keepShoulderOnBatterPlane: pads[padIndex].StitchApronDistance > dedupTol * 4.0);

            if (!TryBuildOrderedShoulderLoopFromSections(
                    prepared.BoundaryLoopXy,
                    prepared.BoundaryVertexCount,
                    prepared.ShoulderXy,
                    dedupTol,
                    out double[] seamLoopXy,
                    out string? seamFailure))
            {
                if (!HasMeaningfulPadShoulderReach(prepared, dedupTol * 16.0))
                {
                    seamLoopXy = (double[])prepared.BoundaryLoopXy.Clone();
                    diagnostics.Add(seamFailure ?? $"Grade Pad[{padIndex}] daylight seam collapsed to the pad boundary.");
                }
                else
                {
                    errorMessage = seamFailure ?? "Grade Pad could not build a valid daylight seam.";
                    return null;
                }
            }

            seamLoopXy = AlignClosedLoopToReference(seamLoopXy, prepared.ShoulderXy, dedupTol);
            seamLoopXy = SimplifyClosedLoopByShortEdges(seamLoopXy, Math.Max(minStitchSegmentLength, 1e-6));
            if ((seamLoopXy.Length / 2) < 3)
            {
                errorMessage = "Grade Pad simplified daylight seam collapsed below 3 vertices.";
                return null;
            }

            bool useTopologyBand = ShouldUseTopologyBand(prepared.ShoulderXy, seamLoopXy, dedupTol);
            if (!useTopologyBand)
                seamLoopXy = AlignClosedLoopToReference(prepared.ShoulderXy, seamLoopXy, dedupTol);

            double[] daylightLoopXy = (double[])seamLoopXy.Clone();
            bool daylightCoincidesWithPad = LoopsCoincide(daylightLoopXy, prepared.BoundaryLoopXy, dedupTol * 4.0);
            string? stitchSkipReason = null;
            if (!daylightCoincidesWithPad &&
                (TryBuildProtectedStitchLoopFromSections(
                     prepared.BoundaryLoopXy,
                     daylightLoopXy,
                     prepared.Pad.StitchApronDistance,
                     hasTerrainBoundary ? terrainBoundaryLoop : null,
                     hasTerrainBoundary ? terrainBoundaryVertexCount : 0,
                     dedupTol,
                     out double[] protectedStitchLoopXy,
                     out stitchSkipReason) ||
                 TryBuildProtectedStitchLoop(
                     daylightLoopXy,
                     prepared.Pad.StitchApronDistance,
                     hasTerrainBoundary ? terrainBoundaryLoop : null,
                     hasTerrainBoundary ? terrainBoundaryVertexCount : 0,
                     dedupTol,
                     out protectedStitchLoopXy,
                     out stitchSkipReason)))
            {
                seamLoopXy = AlignClosedLoopToReference(protectedStitchLoopXy, daylightLoopXy, dedupTol);
                diagnostics.Add($"Grade Pad[{padIndex}] protected stitch apron: daylight->{prepared.Pad.StitchApronDistance:F6} with {seamLoopXy.Length / 2} stitch vertices.");
            }
            else if (!daylightCoincidesWithPad && !string.IsNullOrWhiteSpace(stitchSkipReason))
            {
                diagnostics.Add(stitchSkipReason!);
            }

            bool seamCoincidesWithPad = LoopsCoincide(seamLoopXy, prepared.BoundaryLoopXy, dedupTol * 4.0);
            MeshAreaSplitter.AreaBoundary[] splitBoundaries = seamCoincidesWithPad
                ? [new MeshAreaSplitter.AreaBoundary(seamLoopXy, seamLoopXy.Length / 2)]
                :
                [
                    new MeshAreaSplitter.AreaBoundary(seamLoopXy, seamLoopXy.Length / 2),
                    new MeshAreaSplitter.AreaBoundary(prepared.BoundaryLoopXy, prepared.BoundaryVertexCount)
                ];
            var split = MeshAreaTopologySplitter.Split(
                currentVertices,
                currentVertexCount,
                currentFaces,
                currentFaceCount,
                splitBoundaries,
                dedupTol,
                out string? splitError);
            if (split == null)
            {
                errorMessage = splitError ?? "Grade Pad could not split terrain at the daylight seam.";
                return null;
            }

            if (!TryExtractAreaMeshes(
                    split,
                    static areaIndex => areaIndex >= 0,
                    out double[] localVertices,
                    out int localVertexCount,
                    out int[] localFaces,
                    out int localFaceCount))
            {
                errorMessage = "Grade Pad could not extract the local affected mesh after seam splitting.";
                return null;
            }

            bool hasOutsideMesh = TryExtractAreaMeshes(
                split,
                static areaIndex => areaIndex < 0,
                out double[] outsideVertices,
                out int outsideVertexCount,
                out int[] outsideFaces,
                out int outsideFaceCount);
            if (!hasOutsideMesh)
            {
                diagnostics.Add($"Grade Pad[{padIndex}] daylight seam reached or crossed the terrain boundary; grading was clipped to the available terrain and no outside stitch mesh was available.");
            }
            else if (TryBuildSeamLoopFromOutsideMesh(outsideVertices, outsideFaces, outsideFaceCount, seamLoopXy, dedupTol, out double[] terrainStitchLoopXy) &&
                     LoopsCoincide(terrainStitchLoopXy, seamLoopXy, dedupTol * 8.0))
            {
                seamLoopXy = AlignClosedLoopToReference(terrainStitchLoopXy, seamLoopXy, dedupTol);
                if (prepared.Pad.StitchApronDistance <= dedupTol * 4.0)
                    daylightLoopXy = (double[])seamLoopXy.Clone();

                seamCoincidesWithPad = LoopsCoincide(seamLoopXy, prepared.BoundaryLoopXy, dedupTol * 4.0);
                diagnostics.Add($"Grade Pad[{padIndex}] using terrain-side stitch loop with {seamLoopXy.Length / 2} vertices.");
            }

            PatchMeshResult? patch = null;
            string? patchError = null;
            bool allowSplitLocalFallback = prepared.Pad.StitchApronDistance <= dedupTol * 4.0;
            if (hasOutsideMesh && !seamCoincidesWithPad)
            {
                patch = TryBuildPadPatchMesh(
                    currentFaceGrid,
                    barriers,
                    hasTerrainBoundary,
                    terrainBoundaryLoop,
                    terrainBoundaryVertexCount,
                    prepared,
                    daylightLoopXy,
                    seamLoopXy,
                    out patchError);
            }
            if (patch == null)
            {
                if (!allowSplitLocalFallback)
                {
                    diagnostics.AddRange(BuildProtectedPadInteractionDiagnostics(padIndex, seamLoopXy, patchSummaries, dedupTol));
                    failureOutputPolylines = BuildProtectedPadFailurePolylines(prepared, daylightLoopXy, seamLoopXy, currentFaceGrid);
                    errorMessage = BuildProtectedPadFailureMessage(
                        patchError ??
                        $"Grade Pad[{padIndex}] explicit protected patch could not be built; split-local fallback is disabled because it can produce invalid shoulder topology.",
                        diagnostics);
                    return null;
                }

                if (patchError != null)
                    diagnostics.Add(patchError);
                diagnostics.Add($"Grade Pad[{padIndex}] using split local patch.");
                patch = BuildSplitLocalPadPatchMesh(
                    currentFaceGrid,
                    barriers,
                    prepared,
                    seamLoopXy,
                    localVertices,
                    localVertexCount,
                    localFaces,
                    localFaceCount,
                    dedupTol);
            }

            if (!TryBuildBoundaryLoop(patch.Vertices, patch.Faces, patch.FaceCount, out double[] patchBoundaryLoopXy, out _))
            {
                if (!allowSplitLocalFallback)
                {
                    diagnostics.AddRange(BuildProtectedPadInteractionDiagnostics(padIndex, seamLoopXy, patchSummaries, dedupTol));
                    failureOutputPolylines = BuildProtectedPadFailurePolylines(prepared, daylightLoopXy, seamLoopXy, currentFaceGrid);
                    errorMessage = BuildProtectedPadFailureMessage(
                        $"Grade Pad[{padIndex}] explicit patch did not produce a single closed stitch boundary; split-local fallback is disabled because it can produce invalid shoulder topology.",
                        diagnostics);
                    return null;
                }

                diagnostics.Add($"Grade Pad[{padIndex}] patch did not produce a single closed stitch boundary; using split local patch.");
                patch = BuildSplitLocalPadPatchMesh(
                    currentFaceGrid,
                    barriers,
                    prepared,
                    seamLoopXy,
                    localVertices,
                    localVertexCount,
                    localFaces,
                    localFaceCount,
                    dedupTol);
                if (!TryBuildBoundaryLoop(patch.Vertices, patch.Faces, patch.FaceCount, out patchBoundaryLoopXy, out _))
                {
                    errorMessage = $"Grade Pad[{padIndex}] split local patch did not produce a single closed stitch boundary.";
                    return null;
                }
            }

            if (hasOutsideMesh)
            {
                SeamGraph seamGraph = SeamGraph.Build(
                    patchBoundaryLoopXy,
                    patch.Vertices,
                    patch.Faces,
                    patch.FaceCount,
                    outsideVertices,
                    outsideFaces,
                    outsideFaceCount,
                    dedupTol);
                if (!seamGraph.PatchHasFullSegmentMatch)
                {
                    if (!allowSplitLocalFallback)
                    {
                        diagnostics.AddRange(BuildProtectedPadInteractionDiagnostics(padIndex, seamLoopXy, patchSummaries, dedupTol));
                        failureOutputPolylines = BuildProtectedPadFailurePolylines(prepared, daylightLoopXy, seamLoopXy, currentFaceGrid);
                        errorMessage = BuildProtectedPadFailureMessage(
                            $"Grade Pad[{padIndex}] explicit patch seam integrity check failed (patch={seamGraph.PatchMatchedSegments}/{seamGraph.SeamVertexCount}, outside={seamGraph.TerrainMatchedSegments}/{seamGraph.SeamVertexCount}, patch-near={seamGraph.PatchBoundarySegmentsNearSeam}, outside-near={seamGraph.TerrainBoundarySegmentsNearSeam}); split-local fallback is disabled because it can produce invalid shoulder topology.",
                            diagnostics);
                        return null;
                    }

                    diagnostics.Add(
                        $"Grade Pad[{padIndex}] patch seam integrity check failed (patch={seamGraph.PatchMatchedSegments}/{seamGraph.SeamVertexCount}); using split local patch.");
                    patch = BuildSplitLocalPadPatchMesh(
                        currentFaceGrid,
                        barriers,
                        prepared,
                        seamLoopXy,
                        localVertices,
                        localVertexCount,
                        localFaces,
                        localFaceCount,
                        dedupTol);
                    if (!TryBuildBoundaryLoop(patch.Vertices, patch.Faces, patch.FaceCount, out patchBoundaryLoopXy, out _))
                    {
                        errorMessage = $"Grade Pad[{padIndex}] split local patch did not produce a single closed stitch boundary.";
                        return null;
                    }

                    seamGraph = SeamGraph.Build(
                        patchBoundaryLoopXy,
                        patch.Vertices,
                        patch.Faces,
                        patch.FaceCount,
                        outsideVertices,
                        outsideFaces,
                        outsideFaceCount,
                        dedupTol);
                    if (!seamGraph.PatchHasFullSegmentMatch)
                    {
                        errorMessage =
                            $"Grade Pad[{padIndex}] split local patch seam integrity check failed (patch={seamGraph.PatchMatchedSegments}/{seamGraph.SeamVertexCount}).";
                        return null;
                    }
                }
            }

            diagnostics.AddRange(BuildPadSlopeDiagnostics(padIndex, prepared, patch, dedupTol));
            if (patch.CornerConstraintsRejected)
                diagnostics.Add($"Grade Pad[{padIndex}] explicit patch corner constraints were rejected by the triangulator; retried without them.");
            if (patch.CornerConstraintCount > 0)
                diagnostics.Add($"Grade Pad[{padIndex}] explicit patch corner constraints: {patch.CornerConstraintCount}.");

            if (hasOutsideMesh)
            {
                diagnostics.AddRange(BuildPadStitchDiagnostics(
                    padIndex,
                    daylightLoopXy,
                    seamLoopXy,
                    patchBoundaryLoopXy,
                    patch.Vertices,
                    patch.Faces,
                    patch.FaceCount,
                    outsideVertices,
                    outsideFaces,
                    outsideFaceCount,
                    dedupTol));

                bool merged = TryMergePatchWithOutsideTerrain(
                    outsideVertices,
                    outsideVertexCount,
                    outsideFaces,
                    outsideFaceCount,
                    patch,
                    seamLoopXy,
                    hasTerrainBoundary ? terrainBoundaryLoop : null,
                    dedupTol,
                    rejectInteriorSeamBoundaryEdges: !allowSplitLocalFallback,
                    out currentVertices,
                    out currentVertexCount,
                    out currentFaces,
                    out currentFaceCount,
                    out string? mergeFailure);
                if (!merged)
                {
                    if (!allowSplitLocalFallback)
                    {
                        var singleRegion = new ProtectedPadRegion(
                            padIndex,
                            prepared,
                            daylightLoopXy,
                            seamLoopXy,
                            InflateBounds(GradingPatch.ComputeBounds(seamLoopXy), minStitchSegmentLength));
                        if (TryGradeCoupledProtectedPadsByWholeMeshRemesh(
                                padStartVertices,
                                padStartVertexCount,
                                padStartFaces,
                                padStartFaceCount,
                                [pads[padIndex]],
                                lockCurves,
                                barriers,
                                dedupTol,
                                terrainDetailSize,
                                [singleRegion],
                                out GradingResult? remeshResult,
                                out string? remeshFailure))
                        {
                            if (remeshResult != null)
                            {
                                diagnostics.Add($"Grade Pad[{padIndex}] explicit stitched merge rejected: {mergeFailure}; retrying protected whole-mesh remesh.");
                                diagnostics.AddRange(remeshResult.Diagnostics);
                                outputPolylines.AddRange(remeshResult.OutputPolylines);
                                patchSummaries.Add(BuildPadPatchSummary(prepared, seamLoopXy, padIndex, dedupTol));
                                currentVertices = remeshResult.Vertices;
                                currentVertexCount = remeshResult.VertexCount;
                                currentFaces = remeshResult.Faces;
                                currentFaceCount = remeshResult.FaceCount;
                                continue;
                            }

                            diagnostics.AddRange(BuildProtectedPadInteractionDiagnostics(padIndex, seamLoopXy, patchSummaries, dedupTol));
                            failureOutputPolylines = BuildProtectedPadFailurePolylines(prepared, daylightLoopXy, seamLoopXy, currentFaceGrid);
                            errorMessage = BuildProtectedPadFailureMessage(
                                remeshFailure ??
                                $"Grade Pad[{padIndex}] explicit stitched merge rejected: {mergeFailure}; protected whole-mesh remesh failed.",
                                diagnostics);
                            return null;
                        }

                        diagnostics.AddRange(BuildProtectedPadInteractionDiagnostics(padIndex, seamLoopXy, patchSummaries, dedupTol));
                        failureOutputPolylines = BuildProtectedPadFailurePolylines(prepared, daylightLoopXy, seamLoopXy, currentFaceGrid);
                        errorMessage = BuildProtectedPadFailureMessage(
                            $"Grade Pad[{padIndex}] explicit stitched merge rejected: {mergeFailure}; split-local fallback is disabled because it can produce invalid shoulder topology.",
                            diagnostics);
                        return null;
                    }

                    diagnostics.Add($"Grade Pad[{padIndex}] explicit stitched merge rejected: {mergeFailure}; retrying split local patch.");
                    PatchMeshResult fallbackPatch = BuildSplitLocalPadPatchMesh(
                        currentFaceGrid,
                        barriers,
                        prepared,
                        seamLoopXy,
                        localVertices,
                        localVertexCount,
                        localFaces,
                        localFaceCount,
                        dedupTol);

                    if (!TryMergePatchWithOutsideTerrain(
                            outsideVertices,
                            outsideVertexCount,
                            outsideFaces,
                            outsideFaceCount,
                            fallbackPatch,
                            seamLoopXy,
                            hasTerrainBoundary ? terrainBoundaryLoop : null,
                            dedupTol,
                            rejectInteriorSeamBoundaryEdges: !allowSplitLocalFallback,
                            out currentVertices,
                            out currentVertexCount,
                            out currentFaces,
                            out currentFaceCount,
                            out mergeFailure))
                    {
                        diagnostics.Add(
                            $"Grade Pad[{padIndex}] stitched merge rejected: {mergeFailure}; pad skipped and previous topology kept.");
                        currentVertices = padStartVertices;
                        currentVertexCount = padStartVertexCount;
                        currentFaces = padStartFaces;
                        currentFaceCount = padStartFaceCount;
                        continue;
                    }

                    patch = fallbackPatch;
                    diagnostics.Add($"Grade Pad[{padIndex}] using split local patch after protected stitch merge retry.");
                }

                diagnostics.Add(
                    $"Grade Pad[{padIndex}] merged-mesh naked edges near seam: {CountBoundaryEdgesNearLoop(currentVertices, currentFaces, currentFaceCount, seamLoopXy, dedupTol * 4.0)}.");
            }
            else
            {
                currentVertices = patch.Vertices;
                currentVertexCount = patch.VertexCount;
                currentFaces = patch.Faces;
                currentFaceCount = patch.FaceCount;
            }

            outputPolylines.Add(BuildPadBoundaryPolyline(prepared));
            patchSummaries.Add(BuildPadPatchSummary(prepared, seamLoopXy, padIndex, dedupTol));
        }

        var sampledOriginalVertices = new double[currentVertexCount * 3];
        for (int i = 0; i < currentVertexCount; i++)
        {
            double x = currentVertices[i * 3];
            double y = currentVertices[i * 3 + 1];
            sampledOriginalVertices[i * 3] = x;
            sampledOriginalVertices[i * 3 + 1] = y;
            sampledOriginalVertices[i * 3 + 2] = originalTerrain.InterpolateZ(x, y);
        }

        return BuildResult(
            sampledOriginalVertices,
            currentVertexCount,
            currentFaces,
            currentFaceCount,
            currentVertices,
            outputPolylines,
            diagnostics,
            patchSummaries);
    }

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

        var TerrainFaceGrid = new TerrainFaceGrid(topology.Vertices, topology.VertexCount, topology.Faces, topology.FaceCount);
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
            topology.Faces,
            topology.FaceCount,
            pads,
            interactingRegions,
            tolerance);

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
            Faces = topology.Faces,
            FaceCount = topology.FaceCount,
            StitchLoopXy = Array.Empty<double>()
        };
        foreach (ProtectedPadRegion region in interactingRegions)
            diagnostics.AddRange(BuildPadSlopeDiagnostics(region.PadIndex, region.Prepared, gradedTopologyPatch, tolerance));
        if (double.IsFinite(terrainDetailSize) && terrainDetailSize > 0.0)
            diagnostics.Add($"Grade Pad coupled protected patch detail size: {terrainDetailSize:F6}.");
        foreach (ProtectedPadRegion region in interactingRegions)
        {
            ComputeLoopDistanceStats(region.DaylightLoopXy, region.StitchLoopXy, out double shoulderToSeamMin, out double shoulderToSeamMax);
            ComputeLoopDistanceStats(region.StitchLoopXy, region.DaylightLoopXy, out double seamToShoulderMin, out double seamToShoulderMax);
            diagnostics.Add(
                $"Grade Pad[{region.PadIndex}] topology band width: shoulder->seam min={shoulderToSeamMin:F6}, max={shoulderToSeamMax:F6}; seam->shoulder min={seamToShoulderMin:F6}, max={seamToShoulderMax:F6}.");
            diagnostics.Add(
                $"Grade Pad[{region.PadIndex}] merged-mesh naked edges near seam: {CountBoundaryEdgesNearLoop(topology.Vertices, topology.Faces, topology.FaceCount, region.StitchLoopXy, tolerance * 4.0)}.");
        }
        if (topology.VertexCount > softVertexBudget)
            diagnostics.Add($"Grade Pad coupled protected patch density warning: {topology.VertexCount:N0} vertices exceeds soft budget {softVertexBudget:N0}.");

        result = BuildResult(
            topology.Vertices,
            topology.VertexCount,
            topology.Faces,
            topology.FaceCount,
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
            int sampleCount = Math.Clamp(Math.Max(daylightCount, stitchCount), 16, 512);
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

    private static PatchMeshResult? TryBuildPadPatchMesh(
        TerrainFaceGrid terrainFaceGrid,
        PreparedBarriers barriers,
        bool hasTerrainBoundary,
        double[] terrainBoundaryLoop,
        int terrainBoundaryVertexCount,
        PreparedPadSections prepared,
        double[] daylightLoopXy,
        double[] seamLoopXy,
        out string? errorMessage)
    {
        errorMessage = null;
        const double dedupTol = 1e-3;

        int outerVertexCount = seamLoopXy.Length / 2;
        if (outerVertexCount < 3 || prepared.BoundaryVertexCount < 3)
        {
            errorMessage = "Grade Pad patch requires both a seam loop and a pad boundary.";
            return null;
        }

        PatchMeshResult? explicitPatch = TryBuildExplicitPadPatchMesh(
            terrainFaceGrid,
            barriers,
            hasTerrainBoundary,
            terrainBoundaryLoop,
            terrainBoundaryVertexCount,
            prepared,
            daylightLoopXy,
            seamLoopXy,
            dedupTol,
            addCornerConstraints: false,
            addGuideVertices: false,
            out errorMessage);
        if (explicitPatch != null)
            return explicitPatch;

        var polygon = new Polygon(outerVertexCount + prepared.BoundaryVertexCount + prepared.BoundaryVertexCount);

        var outerVertices = new Vertex[outerVertexCount];
        for (int i = 0; i < outerVertexCount; i++)
            outerVertices[i] = new Vertex(seamLoopXy[i * 2], seamLoopXy[i * 2 + 1]) { ID = i };
        polygon.Add(new Contour(outerVertices), false);

        var innerVertices = new Vertex[prepared.BoundaryVertexCount];
        for (int i = 0; i < prepared.BoundaryVertexCount; i++)
            innerVertices[i] = new Vertex(prepared.BoundaryLoopXy[i * 2], prepared.BoundaryLoopXy[i * 2 + 1]) { ID = outerVertexCount + i };
        for (int i = 0; i < prepared.BoundaryVertexCount; i++)
        {
            polygon.Add(innerVertices[i]);
        }

        for (int i = 0; i < prepared.BoundaryVertexCount; i++)
        {
            Vertex current = innerVertices[i];
            Vertex next = innerVertices[(i + 1) % prepared.BoundaryVertexCount];
            if (!ReferenceEquals(current, next))
                polygon.Add(new Segment(current, next, 1), false);
        }

        for (int i = 0; i < prepared.BoundaryVertexCount; i++)
        {
            double bx = prepared.BoundaryLoopXy[i * 2];
            double by = prepared.BoundaryLoopXy[i * 2 + 1];
            double sx = prepared.ShoulderXy[i * 2];
            double sy = prepared.ShoulderXy[i * 2 + 1];
            double dx = sx - bx;
            double dy = sy - by;
            if ((dx * dx) + (dy * dy) <= dedupTol * dedupTol)
                continue;

            var shoulderVertex = new Vertex(sx, sy)
            {
                ID = outerVertexCount + prepared.BoundaryVertexCount + polygon.Points.Count
            };
            polygon.Add(shoulderVertex);
            polygon.Add(new Segment(innerVertices[i], shoulderVertex, 1), false);
        }

        IMesh mesh;
        try
        {
            mesh = TriangulationHelper.TriangulatePolygon(
                polygon,
                new ConstraintOptions { ConformingDelaunay = false, Convex = false, SegmentSplitting = 0 },
                null);
        }
        catch (Exception ex)
        {
            errorMessage = $"Grade Pad patch triangulation failed: {ex.Message}";
            return null;
        }

        if (mesh.Triangles.Count == 0)
        {
            errorMessage = "Grade Pad patch triangulation produced no triangles.";
            return null;
        }

        var extracted = TriangleNetExtractor.Extract(mesh);
        var patchVertices = new double[extracted.VertexCount * 3];
        for (int i = 0; i < extracted.VertexCount; i++)
        {
            double x = extracted.Xy[i * 2];
            double y = extracted.Xy[i * 2 + 1];
            patchVertices[i * 3] = x;
            patchVertices[i * 3 + 1] = y;
            patchVertices[i * 3 + 2] = terrainFaceGrid.InterpolateZ(x, y);
        }

        var gradedPatchVertices = (double[])patchVertices.Clone();
        ApplyGradingToVerticesWithSections(
            gradedPatchVertices,
            patchVertices,
            extracted.VertexCount,
            new[] { prepared.Pad },
            barriers,
            terrainFaceGrid,
            hasTerrainBoundary,
            terrainBoundaryLoop,
            terrainBoundaryVertexCount,
            dedupTol);

        // The daylight seam is the stitch boundary, so pin it to the terrain surface.
        for (int i = 0; i < extracted.VertexCount; i++)
        {
            double x = gradedPatchVertices[i * 3];
            double y = gradedPatchVertices[i * 3 + 1];
            for (int seamIndex = 0; seamIndex < outerVertexCount; seamIndex++)
            {
                double dx = x - seamLoopXy[seamIndex * 2];
                double dy = y - seamLoopXy[seamIndex * 2 + 1];
                if ((dx * dx) + (dy * dy) > dedupTol * dedupTol)
                    continue;

                gradedPatchVertices[i * 3 + 2] = terrainFaceGrid.InterpolateZ(x, y);
                break;
            }
        }

        return new PatchMeshResult
        {
            Vertices = gradedPatchVertices,
            VertexCount = extracted.VertexCount,
            Faces = extracted.Faces,
            FaceCount = extracted.FaceCount,
            StitchLoopXy = seamLoopXy
        };
    }

    private static bool TryBuildProtectedStitchLoop(
        double[] daylightLoopXy,
        double apronDistance,
        double[]? terrainBoundaryLoop,
        int terrainBoundaryVertexCount,
        double tolerance,
        out double[] stitchLoopXy,
        out string? skipReason)
    {
        stitchLoopXy = Array.Empty<double>();
        skipReason = null;
        int daylightVertexCount = daylightLoopXy.Length / 2;
        if (daylightVertexCount < 3)
        {
            skipReason = "Grade Pad protected stitch apron skipped because the daylight loop was invalid.";
            return false;
        }

        if (apronDistance <= tolerance * 4.0)
        {
            return false;
        }

        var distances = new double[daylightVertexCount];
        Array.Fill(distances, apronDistance);
        if (!TryBuildShoulderLoopWithClipper(
                daylightLoopXy,
                daylightVertexCount,
                distances,
                terrainBoundaryLoop,
                terrainBoundaryVertexCount,
                tolerance,
                out stitchLoopXy,
                out string? offsetFailure))
        {
            skipReason = offsetFailure ?? "Grade Pad protected stitch apron skipped because the outer offset could not be constructed cleanly.";
            return false;
        }

        stitchLoopXy = SimplifyClosedLoopByShortEdges(stitchLoopXy, Math.Max(tolerance * 4.0, 1e-6));
        if ((stitchLoopXy.Length / 2) < 3 || LoopsCoincide(stitchLoopXy, daylightLoopXy, tolerance * 4.0))
        {
            stitchLoopXy = Array.Empty<double>();
            skipReason = "Grade Pad protected stitch apron skipped because the outer stitch loop collapsed to the daylight loop.";
            return false;
        }

        return true;
    }

    private static bool TryBuildProtectedStitchLoopFromSections(
        double[] boundaryLoopXy,
        double[] daylightLoopXy,
        double apronDistance,
        double[]? terrainBoundaryLoop,
        int terrainBoundaryVertexCount,
        double tolerance,
        out double[] stitchLoopXy,
        out string? skipReason)
    {
        stitchLoopXy = Array.Empty<double>();
        skipReason = null;
        int vertexCount = daylightLoopXy.Length / 2;
        if (vertexCount < 3 || boundaryLoopXy.Length / 2 != vertexCount)
        {
            skipReason = "Grade Pad protected stitch apron skipped because the daylight sections were not aligned to the pad boundary.";
            return false;
        }

        if (apronDistance <= tolerance * 4.0)
            return false;

        var points = new List<double>(vertexCount * 2);
        for (int i = 0; i < vertexCount; i++)
        {
            double bx = boundaryLoopXy[i * 2];
            double by = boundaryLoopXy[i * 2 + 1];
            double sx = daylightLoopXy[i * 2];
            double sy = daylightLoopXy[i * 2 + 1];
            double dx = sx - bx;
            double dy = sy - by;
            double length = Math.Sqrt((dx * dx) + (dy * dy));
            if (length <= tolerance)
            {
                skipReason = "Grade Pad protected stitch apron skipped because a daylight section collapsed.";
                return false;
            }

            dx /= length;
            dy /= length;
            double tx = sx + (dx * apronDistance);
            double ty = sy + (dy * apronDistance);

            if (terrainBoundaryLoop != null)
            {
                ResolvePadShoulderEndpoint(
                    PreparedBarriers.Empty,
                    true,
                    terrainBoundaryLoop,
                    terrainBoundaryVertexCount,
                    tolerance,
                    sx,
                    sy,
                    tx,
                    ty,
                    out tx,
                    out ty);
            }

            AddLoopPoint(points, tx, ty, tolerance);
        }

        if (points.Count >= 4 &&
            DistanceSquaredXY(points[0], points[1], points[^2], points[^1]) <= tolerance * tolerance)
        {
            points.RemoveRange(points.Count - 2, 2);
        }

        if (points.Count / 2 < 3 || LoopsCoincide(points.ToArray(), daylightLoopXy, tolerance * 4.0))
        {
            stitchLoopXy = Array.Empty<double>();
            skipReason = "Grade Pad protected stitch apron skipped because the section stitch loop collapsed.";
            return false;
        }

        stitchLoopXy = points.ToArray();
        return true;
    }

    private static PatchMeshResult? TryBuildExplicitPadPatchMesh(
        TerrainFaceGrid terrainFaceGrid,
        PreparedBarriers barriers,
        bool hasTerrainBoundary,
        double[] terrainBoundaryLoop,
        int terrainBoundaryVertexCount,
        PreparedPadSections prepared,
        double[] daylightLoopXy,
        double[] seamLoopXy,
        double tolerance,
        bool addCornerConstraints,
        bool addGuideVertices,
        out string? errorMessage)
    {
        errorMessage = null;
        bool usesTopologyBand = ShouldUseTopologyBand(daylightLoopXy, seamLoopXy, tolerance) ||
                                 daylightLoopXy.Length != seamLoopXy.Length;
        if (!TryBuildAlignedPadPatchLoops(
                terrainFaceGrid,
                prepared,
                daylightLoopXy,
                daylightLoopXy,
                tolerance,
                out double[] boundaryLoopXy,
                out double[] boundaryLoopZ,
                out double[] shoulderLoopXy,
                out double[] shoulderLoopZ,
                out double[] daylightLoopAlignedXy,
                out double[] daylightLoopZ,
                out errorMessage))
        {
            return null;
        }

        int targetCount = daylightLoopAlignedXy.Length / 2;
        if (targetCount < 3)
        {
            errorMessage = "Explicit Grade Pad patch requires at least 3 seam samples.";
            return null;
        }

        if (prepared.Pad.CornerFanSegments >= 1)
            ForceFanBoundaryToCorner(boundaryLoopXy, boundaryLoopZ, daylightLoopAlignedXy,
                targetCount, prepared.Pad, tolerance);

        var (patchVertices, patchVertexCount, patchFaces, patchFaceCount) =
            BuildExplicitPadPatch(
                prepared.Pad, terrainFaceGrid,
                boundaryLoopXy, boundaryLoopZ,
                shoulderLoopXy, shoulderLoopZ,
                daylightLoopAlignedXy, daylightLoopZ,
                targetCount, usesTopologyBand: false, tolerance);

        double[] stitchLoopXy = (double[])daylightLoopAlignedXy.Clone();
        if (usesTopologyBand)
        {
            double[] seamLoopAlignedXy = AlignClosedLoopToReference(seamLoopXy, daylightLoopAlignedXy, tolerance);
            if (!TryBuildApronPatchMesh(
                    terrainFaceGrid,
                    daylightLoopAlignedXy,
                    daylightLoopZ,
                    seamLoopAlignedXy,
                    tolerance,
                    out PatchMeshResult? apronPatch,
                    out string? apronError))
            {
                errorMessage = apronError ?? "Explicit Grade Pad apron triangulation failed.";
                return null;
            }

            MergeMeshes(
                patchVertices,
                patchVertexCount,
                patchFaces,
                patchFaceCount,
                apronPatch.Vertices,
                apronPatch.VertexCount,
                apronPatch.Faces,
                apronPatch.FaceCount,
                tolerance,
                out patchVertices,
                out patchVertexCount,
                out patchFaces,
                out patchFaceCount);
            stitchLoopXy = seamLoopAlignedXy;
        }

        if (patchFaceCount == 0)
        {
            errorMessage = "Explicit Grade Pad strip+fan produced no faces.";
            return null;
        }

        for (int i = 0; i < patchVertexCount; i++)
        {
            double x = patchVertices[i * 3];
            double y = patchVertices[i * 3 + 1];
            int stitchCount = stitchLoopXy.Length / 2;
            for (int seamIndex = 0; seamIndex < stitchCount; seamIndex++)
            {
                double dx = x - stitchLoopXy[seamIndex * 2];
                double dy = y - stitchLoopXy[seamIndex * 2 + 1];
                if ((dx * dx) + (dy * dy) > tolerance * tolerance)
                    continue;

                patchVertices[i * 3 + 2] = terrainFaceGrid.InterpolateZ(x, y);
                break;
            }
        }

        int extraPatchBoundaryEdges = CountBoundaryEdgesAwayFromLoop(
            patchVertices, patchFaces, patchFaceCount, stitchLoopXy, tolerance * 4.0);
        if (extraPatchBoundaryEdges > 0)
        {
            errorMessage = $"Explicit Grade Pad strip+fan produced {extraPatchBoundaryEdges} interior naked edge(s).";
            return null;
        }

        return new PatchMeshResult
        {
            Vertices = patchVertices,
            VertexCount = patchVertexCount,
            Faces = patchFaces,
            FaceCount = patchFaceCount,
            StitchLoopXy = (double[])stitchLoopXy.Clone(),
            CornerConstraintCount = 0
        };
    }

    private static void ForceFanBoundaryToCorner(
        double[] boundaryLoopXy,
        double[] boundaryLoopZ,
        double[] seamLoopAlignedXy,
        int count,
        PadBoundary pad,
        double tolerance)
    {
        double signedArea = ClipperGeometry.SignedArea(pad.XyVertices);
        bool ccw = signedArea > 0.0;

        for (int i = 0; i < count; i++)
        {
            double sx = seamLoopAlignedXy[i * 2];
            double sy = seamLoopAlignedXy[i * 2 + 1];

            for (int j = 0; j < pad.VertexCount; j++)
            {
                int prev = (j + pad.VertexCount - 1) % pad.VertexCount;
                int jnext = (j + 1) % pad.VertexCount;

                double cx = pad.XyVertices[j * 2];
                double cy = pad.XyVertices[j * 2 + 1];
                double dx0 = cx - pad.XyVertices[prev * 2];
                double dy0 = cy - pad.XyVertices[prev * 2 + 1];
                double dx1 = pad.XyVertices[jnext * 2] - cx;
                double dy1 = pad.XyVertices[jnext * 2 + 1] - cy;
                double len0 = Math.Sqrt(dx0 * dx0 + dy0 * dy0);
                double len1 = Math.Sqrt(dx1 * dx1 + dy1 * dy1);
                if (len0 < 1e-12 || len1 < 1e-12) continue;

                double turnCross = dx0 * dy1 - dy0 * dx1;
                bool isReentrant = ccw ? turnCross < -1e-12 : turnCross > 1e-12;
                if (isReentrant) continue;

                double n0x = ccw ? dy0 / len0 : -dy0 / len0;
                double n0y = ccw ? -dx0 / len0 : dx0 / len0;
                double n1x = ccw ? dy1 / len1 : -dy1 / len1;
                double n1y = ccw ? -dx1 / len1 : dx1 / len1;

                double vx = sx - cx;
                double vy = sy - cy;
                double dist = Math.Sqrt(vx * vx + vy * vy);
                if (dist < tolerance) continue;
                vx /= dist;
                vy /= dist;

                double c0 = n0x * vy - n0y * vx;
                double c1 = vx * n1y - vy * n1x;
                bool inSector = ccw ? (c0 >= -1e-9 && c1 >= -1e-9) : (c0 <= 1e-9 && c1 <= 1e-9);
                if (!inSector) continue;

                boundaryLoopXy[i * 2] = cx;
                boundaryLoopXy[i * 2 + 1] = cy;
                boundaryLoopZ[i] = pad.EvaluateZ(cx, cy);
                break;
            }
        }
    }

    private static bool TryBuildApronPatchMesh(
        TerrainFaceGrid terrainFaceGrid,
        double[] daylightLoopXy,
        double[] daylightLoopZ,
        double[] seamLoopXy,
        double tolerance,
        out PatchMeshResult apronPatch,
        out string? errorMessage)
    {
        apronPatch = new PatchMeshResult
        {
            Vertices = Array.Empty<double>(),
            VertexCount = 0,
            Faces = Array.Empty<int>(),
            FaceCount = 0,
            StitchLoopXy = Array.Empty<double>()
        };
        errorMessage = null;

        int daylightCount = daylightLoopXy.Length / 2;
        int seamCount = seamLoopXy.Length / 2;
        if (daylightCount < 3 || seamCount < 3)
        {
            errorMessage = "Explicit Grade Pad apron requires valid daylight and stitch loops.";
            return false;
        }

        var polygon = new Polygon(daylightCount + seamCount);
        var seamVertices = new Vertex[seamCount];
        for (int i = 0; i < seamCount; i++)
            seamVertices[i] = new Vertex(seamLoopXy[i * 2], seamLoopXy[i * 2 + 1]) { ID = i };
        polygon.Add(new Contour(seamVertices), false);

        var daylightVertices = new Vertex[daylightCount];
        for (int i = 0; i < daylightCount; i++)
            daylightVertices[i] = new Vertex(daylightLoopXy[i * 2], daylightLoopXy[i * 2 + 1]) { ID = seamCount + i };

        double holeX = 0.0;
        double holeY = 0.0;
        for (int i = 0; i < daylightCount; i++)
        {
            holeX += daylightLoopXy[i * 2];
            holeY += daylightLoopXy[i * 2 + 1];
        }

        holeX /= daylightCount;
        holeY /= daylightCount;
        polygon.Add(new Contour(daylightVertices), new TriangleNet.Geometry.Point(holeX, holeY));

        IMesh mesh;
        try
        {
            mesh = TriangulationHelper.TriangulatePolygon(
                polygon,
                new ConstraintOptions { ConformingDelaunay = false, Convex = false, SegmentSplitting = 0 },
                null);
        }
        catch (Exception ex)
        {
            errorMessage = $"Explicit Grade Pad apron triangulation failed: {ex.Message}";
            return false;
        }

        var extracted = TriangleNetExtractor.Extract(mesh);
        if (extracted.FaceCount == 0)
        {
            errorMessage = "Explicit Grade Pad apron triangulation produced no faces.";
            return false;
        }

        var vertices = new double[extracted.VertexCount * 3];
        for (int i = 0; i < extracted.VertexCount; i++)
        {
            double x = extracted.Xy[i * 2];
            double y = extracted.Xy[i * 2 + 1];
            vertices[i * 3] = x;
            vertices[i * 3 + 1] = y;
            vertices[i * 3 + 2] = terrainFaceGrid.InterpolateZ(x, y);
        }

        for (int i = 0; i < extracted.VertexCount; i++)
        {
            double x = vertices[i * 3];
            double y = vertices[i * 3 + 1];
            for (int daylightIndex = 0; daylightIndex < daylightCount; daylightIndex++)
            {
                double dx = x - daylightLoopXy[daylightIndex * 2];
                double dy = y - daylightLoopXy[daylightIndex * 2 + 1];
                if ((dx * dx) + (dy * dy) > tolerance * tolerance)
                    continue;

                vertices[i * 3 + 2] = daylightLoopZ[daylightIndex];
                break;
            }
        }

        apronPatch = new PatchMeshResult
        {
            Vertices = vertices,
            VertexCount = extracted.VertexCount,
            Faces = extracted.Faces,
            FaceCount = extracted.FaceCount,
            StitchLoopXy = (double[])seamLoopXy.Clone()
        };
        return true;
    }

    private static (double[] vertices, int vertexCount, int[] faces, int faceCount)
        BuildExplicitPadPatch(
            PadBoundary pad,
            TerrainFaceGrid terrainFaceGrid,
            double[] boundaryLoopXy,
            double[] boundaryLoopZ,
            double[] shoulderLoopXy,
            double[] shoulderLoopZ,
            double[] seamLoopXy,
            double[] seamLoopZ,
            int targetCount,
            bool usesTopologyBand,
            double tolerance)
    {
        var vertexList = new List<double>(targetCount * 6);
        var xyList = new List<double>(targetCount * 4);
        var faceList = new List<int>(targetCount * 6);
        var edgeUseCount = new Dictionary<long, int>(targetCount * 12);
        double vertexDedupeTolerance = Math.Max(Math.Min(tolerance * 0.01, 1e-6), 1e-9);
        var xyHash = new SpatialVertexHash(vertexDedupeTolerance);
        double toleranceSq = tolerance * tolerance;
        double minimumPatchTriangleArea2 = toleranceSq * 1e-4;

        int GetVertex(double x, double y, double z)
        {
            int existing = xyHash.FindNearest(xyList, x, y, vertexDedupeTolerance);
            if (existing >= 0) return existing;
            int idx = vertexList.Count / 3;
            xyList.Add(x);
            xyList.Add(y);
            vertexList.Add(x);
            vertexList.Add(y);
            vertexList.Add(z);
            xyHash.Insert(idx, x, y);
            return idx;
        }

        static long EdgeKey(int a, int b)
        {
            if (a > b) (a, b) = (b, a);
            return ((long)a << 32) | (uint)b;
        }

        int EdgeUseCount(int a, int b)
        {
            return edgeUseCount.TryGetValue(EdgeKey(a, b), out int count) ? count : 0;
        }

        bool EmitTri(int a, int b, int c)
        {
            if (a == b || b == c || a == c) return false;
            double ax = vertexList[a * 3], ay = vertexList[a * 3 + 1];
            double bx = vertexList[b * 3], by = vertexList[b * 3 + 1];
            double cx = vertexList[c * 3], cy = vertexList[c * 3 + 1];
            double area2 = Math.Abs((bx - ax) * (cy - ay) - (by - ay) * (cx - ax));
            if (area2 <= minimumPatchTriangleArea2) return false;
            faceList.Add(a); faceList.Add(b); faceList.Add(c);
            edgeUseCount[EdgeKey(a, b)] = EdgeUseCount(a, b) + 1;
            edgeUseCount[EdgeKey(b, c)] = EdgeUseCount(b, c) + 1;
            edgeUseCount[EdgeKey(c, a)] = EdgeUseCount(c, a) + 1;
            return true;
        }

        double TriangleSlopeDelta(int a, int b, int c)
        {
            if (a == b || b == c || a == c)
                return double.MaxValue;

            double ax = vertexList[a * 3], ay = vertexList[a * 3 + 1], az = vertexList[a * 3 + 2];
            double bx = vertexList[b * 3], by = vertexList[b * 3 + 1], bz = vertexList[b * 3 + 2];
            double cx = vertexList[c * 3], cy = vertexList[c * 3 + 1], cz = vertexList[c * 3 + 2];
            double ux = bx - ax;
            double uy = by - ay;
            double uz = bz - az;
            double vx = cx - ax;
            double vy = cy - ay;
            double vz = cz - az;
            double nx = (uy * vz) - (uz * vy);
            double ny = (uz * vx) - (ux * vz);
            double nz = (ux * vy) - (uy * vx);
            double normalLengthSquared = (nx * nx) + (ny * ny) + (nz * nz);
            if (normalLengthSquared <= minimumPatchTriangleArea2 * minimumPatchTriangleArea2)
                return double.MaxValue;

            double horizontal = Math.Sqrt((nx * nx) + (ny * ny));
            double slopeDeg = Math.Atan2(horizontal, Math.Abs(nz)) * 180.0 / Math.PI;
            return Math.Abs(slopeDeg - pad.SlopeAngleDeg);
        }

        static bool FirstDiagonalIsBetter(
            double firstA,
            double firstB,
            double secondA,
            double secondB)
        {
            double firstMax = Math.Max(firstA, firstB);
            double secondMax = Math.Max(secondA, secondB);
            if (firstMax < secondMax - 1e-9)
                return true;
            if (secondMax < firstMax - 1e-9)
                return false;

            return firstA + firstB <= secondA + secondB;
        }

        // Piece A: pad top. Use a constrained triangulation with every sampled pad
        // edge as a segment so the flat top shares vertices with the shoulder without
        // collapsing to a single center fan.
        bool padTopBuilt = TryEmitConstrainedPadTop();
        if (!padTopBuilt)
        {
            var padTopPoly = new Polygon(targetCount);
            var padTopVerts = new Vertex[targetCount];
            for (int i = 0; i < targetCount; i++)
                padTopVerts[i] = new Vertex(boundaryLoopXy[i * 2], boundaryLoopXy[i * 2 + 1]) { ID = i };
            padTopPoly.Add(new Contour(padTopVerts), false);

            IMesh? padTop = null;
            try
            {
                padTop = TriangulationHelper.TriangulatePolygon(
                    padTopPoly,
                    new ConstraintOptions { ConformingDelaunay = false, Convex = false, SegmentSplitting = 0 },
                    null);
            }
            catch { /* fallback: no pad top triangles */ }

            if (padTop != null)
            {
                var padTopExt = TriangleNetExtractor.Extract(padTop);
                var padTopIdxMap = new int[padTopExt.VertexCount];
                for (int i = 0; i < padTopExt.VertexCount; i++)
                {
                    double x = padTopExt.Xy[i * 2];
                    double y = padTopExt.Xy[i * 2 + 1];
                    padTopIdxMap[i] = GetVertex(x, y, pad.EvaluateZ(x, y));
                }
                for (int f = 0; f < padTopExt.FaceCount; f++)
                    EmitTri(padTopIdxMap[padTopExt.Faces[f * 3]],
                            padTopIdxMap[padTopExt.Faces[f * 3 + 1]],
                            padTopIdxMap[padTopExt.Faces[f * 3 + 2]]);
            }
        }

        // Piece B: shoulder strip/fan (boundary -> shoulder)
        int shoulderRowCount = DeterminePadShoulderRowCount(boundaryLoopXy, shoulderLoopXy, targetCount, tolerance);
        for (int row = 0; row < shoulderRowCount - 1; row++)
        {
            double t0 = (double)row / (shoulderRowCount - 1);
            double t1 = (double)(row + 1) / (shoulderRowCount - 1);
            for (int i = 0; i < targetCount; i++)
            {
                int next = (i + 1) % targetCount;
                int a0 = GetVertex(
                    LerpValue(boundaryLoopXy[i * 2], shoulderLoopXy[i * 2], t0),
                    LerpValue(boundaryLoopXy[i * 2 + 1], shoulderLoopXy[i * 2 + 1], t0),
                    LerpValue(boundaryLoopZ[i], shoulderLoopZ[i], t0));
                int b0 = GetVertex(
                    LerpValue(boundaryLoopXy[next * 2], shoulderLoopXy[next * 2], t0),
                    LerpValue(boundaryLoopXy[next * 2 + 1], shoulderLoopXy[next * 2 + 1], t0),
                    LerpValue(boundaryLoopZ[next], shoulderLoopZ[next], t0));
                int a1 = GetVertex(
                    LerpValue(boundaryLoopXy[i * 2], shoulderLoopXy[i * 2], t1),
                    LerpValue(boundaryLoopXy[i * 2 + 1], shoulderLoopXy[i * 2 + 1], t1),
                    LerpValue(boundaryLoopZ[i], shoulderLoopZ[i], t1));
                int b1 = GetVertex(
                    LerpValue(boundaryLoopXy[next * 2], shoulderLoopXy[next * 2], t1),
                    LerpValue(boundaryLoopXy[next * 2 + 1], shoulderLoopXy[next * 2 + 1], t1),
                    LerpValue(boundaryLoopZ[next], shoulderLoopZ[next], t1));

                double firstDelta0 = TriangleSlopeDelta(a0, b0, b1);
                double firstDelta1 = TriangleSlopeDelta(a0, b1, a1);
                double secondDelta0 = TriangleSlopeDelta(a0, b0, a1);
                double secondDelta1 = TriangleSlopeDelta(b0, b1, a1);
                if (FirstDiagonalIsBetter(firstDelta0, firstDelta1, secondDelta0, secondDelta1))
                {
                    EmitTri(a0, b0, b1);
                    EmitTri(a0, b1, a1);
                }
                else
                {
                    EmitTri(a0, b0, a1);
                    EmitTri(b0, b1, a1);
                }
            }
        }

        // Piece C: topology band (shoulder -> seam), only when seam differs from shoulder
        if (usesTopologyBand)
        {
            for (int i = 0; i < targetCount; i++)
            {
                int next = (i + 1) % targetCount;
                int na = GetVertex(shoulderLoopXy[i * 2],    shoulderLoopXy[i * 2 + 1],    shoulderLoopZ[i]);
                int nb = GetVertex(shoulderLoopXy[next * 2], shoulderLoopXy[next * 2 + 1], shoulderLoopZ[next]);
                int fa = GetVertex(seamLoopXy[i * 2],         seamLoopXy[i * 2 + 1],         seamLoopZ[i]);
                int fb = GetVertex(seamLoopXy[next * 2],      seamLoopXy[next * 2 + 1],      seamLoopZ[next]);
                EmitTri(na, nb, fb);
                EmitTri(na, fb, fa);
                if (EdgeUseCount(fa, fb) == 0)
                {
                    if (!EmitTri(fa, fb, na))
                        EmitTri(fa, fb, nb);
                }
            }
        }

        bool TryEmitConstrainedPadTop()
        {
            if (targetCount < 3)
                return false;

            var polygon = new Polygon(targetCount);
            var vertices = new Vertex[targetCount];
            for (int i = 0; i < targetCount; i++)
            {
                vertices[i] = new Vertex(boundaryLoopXy[i * 2], boundaryLoopXy[i * 2 + 1]) { ID = i };
                polygon.Add(vertices[i]);
            }

            for (int i = 0; i < targetCount; i++)
            {
                int next = (i + 1) % targetCount;
                polygon.Add(new Segment(vertices[i], vertices[next], 1), false);
            }

            AddPadTopGuideVertices(polygon, boundaryLoopXy, targetCount, tolerance);

            IMesh mesh;
            try
            {
                mesh = TriangulationHelper.TriangulatePolygon(
                    polygon,
                    new ConstraintOptions { ConformingDelaunay = false, Convex = false, SegmentSplitting = 0 },
                    null);
            }
            catch
            {
                return false;
            }

            var extracted = TriangleNetExtractor.Extract(mesh);
            if (extracted.FaceCount == 0)
                return false;

            var topIndexMap = new int[extracted.VertexCount];
            for (int i = 0; i < extracted.VertexCount; i++)
            {
                double x = extracted.Xy[i * 2];
                double y = extracted.Xy[i * 2 + 1];
                topIndexMap[i] = GetVertex(x, y, pad.EvaluateZ(x, y));
            }

            for (int f = 0; f < extracted.FaceCount; f++)
            {
                EmitTri(
                    topIndexMap[extracted.Faces[f * 3]],
                    topIndexMap[extracted.Faces[f * 3 + 1]],
                    topIndexMap[extracted.Faces[f * 3 + 2]]);
            }

            return true;
        }

        void AddPadTopGuideVertices(Polygon polygon, double[] loopXy, int loopVertexCount, double tol)
        {
            if (loopVertexCount < 3)
                return;

            double minX = double.MaxValue;
            double maxX = double.MinValue;
            double minY = double.MaxValue;
            double maxY = double.MinValue;
            double perimeter = 0.0;
            for (int i = 0; i < loopVertexCount; i++)
            {
                int next = (i + 1) % loopVertexCount;
                double x = loopXy[i * 2];
                double y = loopXy[i * 2 + 1];
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
                perimeter += Math.Sqrt(DistanceSquaredXY(
                    x,
                    y,
                    loopXy[next * 2],
                    loopXy[next * 2 + 1]));
            }

            double averageEdgeLength = perimeter / loopVertexCount;
            double spacing = Math.Max(tol * 64.0, averageEdgeLength * 2.0);
            if (!double.IsFinite(spacing) || spacing <= tol)
                return;

            int maxGuideCount = 256;
            int added = 0;
            int startId = polygon.Points.Count;
            for (double y = minY + spacing; y < maxY - spacing * 0.5 && added < maxGuideCount; y += spacing)
            {
                for (double x = minX + spacing; x < maxX - spacing * 0.5 && added < maxGuideCount; x += spacing)
                {
                    if (!PointInPolygon(x, y, loopXy, loopVertexCount))
                        continue;
                    if (DistToPolygon(x, y, loopXy, loopVertexCount) <= spacing * 0.35)
                        continue;

                    polygon.Add(new Vertex(x, y) { ID = startId + added });
                    added++;
                }
            }
        }

        double[] verts = vertexList.ToArray();
        return (verts, verts.Length / 3, faceList.ToArray(), faceList.Count / 3);
    }

    private static int DeterminePadShoulderRowCount(
        double[] boundaryLoopXy,
        double[] shoulderLoopXy,
        int vertexCount,
        double tolerance)
    {
        double maxReach = 0.0;
        double perimeter = 0.0;
        for (int i = 0; i < vertexCount; i++)
        {
            int next = (i + 1) % vertexCount;
            double reach = Math.Sqrt(DistanceSquaredXY(
                boundaryLoopXy[i * 2],
                boundaryLoopXy[i * 2 + 1],
                shoulderLoopXy[i * 2],
                shoulderLoopXy[i * 2 + 1]));
            maxReach = Math.Max(maxReach, reach);

            perimeter += Math.Sqrt(DistanceSquaredXY(
                boundaryLoopXy[i * 2],
                boundaryLoopXy[i * 2 + 1],
                boundaryLoopXy[next * 2],
                boundaryLoopXy[next * 2 + 1]));
        }

        if (maxReach <= tolerance * 16.0)
            return 2;

        double averageEdgeLength = vertexCount > 0 ? perimeter / vertexCount : maxReach;
        double targetSpacing = Math.Max(tolerance * 64.0, averageEdgeLength * 0.75);
        int intermediateRows = Math.Clamp((int)Math.Ceiling(maxReach / targetSpacing) - 1, 0, 4);
        return intermediateRows + 2;
    }

    private static int AddCornerConstraintSegments(
        Polygon polygon,
        PreparedPadSections prepared,
        double[] boundaryLoopXy,
        double[] shoulderLoopXy,
        double tolerance,
        Func<double, double, Vertex> resolveVertex)
    {
        int added = 0;
        int sampleCount = boundaryLoopXy.Length / 2;
        var addedSegments = new HashSet<long>();

        int AddUnique(Vertex first, Vertex second)
        {
            if (ReferenceEquals(first, second))
                return 0;

            int a = Math.Min(first.ID, second.ID);
            int b = Math.Max(first.ID, second.ID);
            long key = ((long)a << 32) | (uint)b;
            if (!addedSegments.Add(key))
                return 0;

            polygon.Add(new Segment(first, second, 1), false);
            return 1;
        }

        for (int i = 0; i < prepared.Pad.VertexCount; i++)
        {
            double bx = prepared.Pad.XyVertices[i * 2];
            double by = prepared.Pad.XyVertices[i * 2 + 1];
            int previous = (i - 1 + prepared.Pad.VertexCount) % prepared.Pad.VertexCount;
            int next = (i + 1) % prepared.Pad.VertexCount;
            double previousLength = Math.Sqrt(DistanceSquaredXY(
                prepared.Pad.XyVertices[previous * 2],
                prepared.Pad.XyVertices[previous * 2 + 1],
                bx,
                by));
            double nextLength = Math.Sqrt(DistanceSquaredXY(
                bx,
                by,
                prepared.Pad.XyVertices[next * 2],
                prepared.Pad.XyVertices[next * 2 + 1]));
            int nearestSampleIndex = 0;
            double nearestSampleDistanceSquared = double.MaxValue;
            for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
            {
                double sampleX = boundaryLoopXy[sampleIndex * 2];
                double sampleY = boundaryLoopXy[sampleIndex * 2 + 1];
                double distanceSquared = DistanceSquaredXY(bx, by, sampleX, sampleY);
                if (distanceSquared < nearestSampleDistanceSquared)
                {
                    nearestSampleDistanceSquared = distanceSquared;
                    nearestSampleIndex = sampleIndex;
                }
            }

            Vertex cornerVertex = resolveVertex(bx, by);
            Vertex shoulderVertex = resolveVertex(shoulderLoopXy[nearestSampleIndex * 2], shoulderLoopXy[nearestSampleIndex * 2 + 1]);
            added += AddUnique(cornerVertex, shoulderVertex);
        }

        return added;
    }

    private static void AddCornerFanConstraints(
        Polygon polygon,
        PreparedPadSections prepared,
        double tolerance,
        Func<double, double, Vertex> resolveVertex)
    {
        int count = prepared.BoundaryVertexCount;
        if (count < 3) return;
        double signedArea = ClipperGeometry.SignedArea(prepared.BoundaryLoopXy);
        if (Math.Abs(signedArea) < 1e-12) return;
        bool ccw = signedArea > 0.0;
        int fanCount = prepared.Pad.CornerFanSegments + 1;

        for (int i = 0; i < count; i++)
        {
            int prev = (i + count - 1) % count;
            int next = (i + 1) % count;

            double x0 = prepared.BoundaryLoopXy[prev * 2];
            double y0 = prepared.BoundaryLoopXy[prev * 2 + 1];
            double x1 = prepared.BoundaryLoopXy[i * 2];
            double y1 = prepared.BoundaryLoopXy[i * 2 + 1];
            double x2 = prepared.BoundaryLoopXy[next * 2];
            double y2 = prepared.BoundaryLoopXy[next * 2 + 1];

            double dx0 = x1 - x0;
            double dy0 = y1 - y0;
            double dx1 = x2 - x1;
            double dy1 = y2 - y1;
            double len0 = Math.Sqrt(dx0 * dx0 + dy0 * dy0);
            double len1 = Math.Sqrt(dx1 * dx1 + dy1 * dy1);
            if (len0 < 1e-12 || len1 < 1e-12) continue;

            double n0x = ccw ? dy0 / len0 : -dy0 / len0;
            double n0y = ccw ? -dx0 / len0 : dx0 / len0;

            double turnCross = dx0 * dy1 - dy0 * dx1;
            bool isReentrant = ccw ? turnCross < -1e-12 : turnCross > 1e-12;
            if (isReentrant) continue;

            double n1x = ccw ? dy1 / len1 : -dy1 / len1;
            double n1y = ccw ? -dx1 / len1 : dx1 / len1;
            double prevAngle = Math.Atan2(n0y, n0x);
            double nextAngle = Math.Atan2(n1y, n1x);
            double sweep = ComputeOutwardAngleSweep(prevAngle, nextAngle, ccw);
            if (Math.Abs(sweep) <= 10.0 * Math.PI / 180.0) continue;

            double svx = prepared.ShoulderXy[i * 2] - x1;
            double svy = prepared.ShoulderXy[i * 2 + 1] - y1;
            double d = svx * n0x + svy * n0y;
            if (d <= tolerance) continue;

            // Add arc vertices as interior steering points (no segment constraints from
            // the pad boundary corner, which would produce single-sided naked edges).
            for (int f = 1; f < fanCount - 1; f++)
            {
                double theta = prevAngle + sweep * f / (fanCount - 1);
                resolveVertex(x1 + Math.Cos(theta) * d, y1 + Math.Sin(theta) * d);
            }
        }
    }

    private static bool IsNearPadCorner(PreparedPadSections prepared, double x, double y, double tolerance)
    {
        double minAdjacentLength = double.MaxValue;
        for (int i = 0; i < prepared.BoundaryVertexCount; i++)
        {
            int previous = (i - 1 + prepared.BoundaryVertexCount) % prepared.BoundaryVertexCount;
            int next = (i + 1) % prepared.BoundaryVertexCount;
            double px = prepared.BoundaryLoopXy[previous * 2];
            double py = prepared.BoundaryLoopXy[previous * 2 + 1];
            double cx = prepared.BoundaryLoopXy[i * 2];
            double cy = prepared.BoundaryLoopXy[i * 2 + 1];
            double nx = prepared.BoundaryLoopXy[next * 2];
            double ny = prepared.BoundaryLoopXy[next * 2 + 1];
            minAdjacentLength = Math.Min(minAdjacentLength, Math.Sqrt(DistanceSquaredXY(px, py, cx, cy)));
            minAdjacentLength = Math.Min(minAdjacentLength, Math.Sqrt(DistanceSquaredXY(cx, cy, nx, ny)));
        }

        if (!double.IsFinite(minAdjacentLength) || minAdjacentLength <= tolerance)
            return false;

        double cornerRadius = Math.Clamp(minAdjacentLength * 0.18, tolerance * 32.0, minAdjacentLength * 0.35);
        double cornerRadiusSquared = cornerRadius * cornerRadius;
        for (int i = 0; i < prepared.BoundaryVertexCount; i++)
        {
            double cx = prepared.BoundaryLoopXy[i * 2];
            double cy = prepared.BoundaryLoopXy[i * 2 + 1];
            if (DistanceSquaredXY(x, y, cx, cy) <= cornerRadiusSquared)
                return true;
        }

        return false;
    }

    private static void AddGuideVertices(
        double ax,
        double ay,
        double bx,
        double by,
        int loopSampleCount,
        double tolerance,
        Func<double, double, Vertex> resolveVertex)
    {
        double dx = bx - ax;
        double dy = by - ay;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        double targetSpacing = Math.Max(tolerance * 64.0, length / Math.Max(2.0, Math.Sqrt(Math.Max(loopSampleCount, 4)) * 0.5));
        int interiorCount = Math.Clamp((int)Math.Floor(length / targetSpacing), 0, 3);
        for (int i = 1; i <= interiorCount; i++)
        {
            double t = (double)i / (interiorCount + 1);
            resolveVertex(LerpValue(ax, bx, t), LerpValue(ay, by, t));
        }
    }

    private static bool TryBuildAlignedPadPatchLoops(
        TerrainFaceGrid terrainFaceGrid,
        PreparedPadSections prepared,
        double[] daylightLoopXy,
        double[] seamLoopXy,
        double tolerance,
        out double[] boundaryLoopXy,
        out double[] boundaryLoopZ,
        out double[] shoulderLoopXy,
        out double[] shoulderLoopZ,
        out double[] alignedSeamLoopXy,
        out double[] seamLoopZ,
        out string? errorMessage)
    {
        errorMessage = null;
        boundaryLoopXy = Array.Empty<double>();
        boundaryLoopZ = Array.Empty<double>();
        shoulderLoopXy = Array.Empty<double>();
        shoulderLoopZ = Array.Empty<double>();
        alignedSeamLoopXy = Array.Empty<double>();
        seamLoopZ = Array.Empty<double>();

        double[] alignedSeam = AlignClosedLoopToReference(seamLoopXy, daylightLoopXy, tolerance);
        int targetCount = alignedSeam.Length / 2;
        if (targetCount < 3)
        {
            errorMessage = "Grade Pad explicit patch requires at least 3 seam samples.";
            return false;
        }

        alignedSeamLoopXy = alignedSeam;
        boundaryLoopXy = new double[targetCount * 2];
        boundaryLoopZ = new double[targetCount];
        shoulderLoopXy = new double[targetCount * 2];
        shoulderLoopZ = new double[targetCount];
        seamLoopZ = new double[targetCount];

        int daylightVertexCount = daylightLoopXy.Length / 2;
        bool loopsShareSampleCount = daylightVertexCount == targetCount;
        for (int i = 0; i < targetCount; i++)
        {
            double sx = alignedSeam[i * 2];
            double sy = alignedSeam[i * 2 + 1];
            seamLoopZ[i] = terrainFaceGrid.InterpolateZ(sx, sy);

            double daylightX;
            double daylightY;
            double boundaryX;
            double boundaryY;
            double boundaryZ;
            double sectionShoulderZ;

            if (loopsShareSampleCount)
            {
                daylightX = daylightLoopXy[i * 2];
                daylightY = daylightLoopXy[i * 2 + 1];
                sectionShoulderZ = terrainFaceGrid.InterpolateZ(daylightX, daylightY);
                if (prepared.BoundaryVertexCount == targetCount)
                {
                    boundaryX = prepared.BoundaryLoopXy[i * 2];
                    boundaryY = prepared.BoundaryLoopXy[i * 2 + 1];
                    boundaryZ = prepared.Pad.EvaluateZ(boundaryX, boundaryY);
                    sectionShoulderZ = prepared.ShoulderZ[i];
                }
                else
                {
                    double stationFraction = ComputeLoopVertexStationFraction(alignedSeam, targetCount, i);
                    if (!TrySampleLoopAtFraction(prepared.BoundaryLoopXy, prepared.BoundaryVertexCount, stationFraction, out boundaryX, out boundaryY))
                    {
                        errorMessage = "Grade Pad explicit patch could not project the stitch seam back onto the grading sections.";
                        return false;
                    }

                    boundaryZ = prepared.Pad.EvaluateZ(boundaryX, boundaryY);
                    sectionShoulderZ = EvaluatePreparedPadBatterPlaneZ(
                        prepared,
                        daylightX,
                        daylightY,
                        sectionShoulderZ,
                        tolerance);
                }
            }
            else
            {
                double stationFraction = ComputeLoopVertexStationFraction(alignedSeam, targetCount, i);
                if (!TrySampleLoopAtFraction(daylightLoopXy, daylightVertexCount, stationFraction, out daylightX, out daylightY))
                {
                    errorMessage = "Grade Pad explicit patch could not project the stitch seam back onto the daylight shoulder.";
                    return false;
                }

                if (!TrySampleLoopAtFraction(prepared.BoundaryLoopXy, prepared.BoundaryVertexCount, stationFraction, out boundaryX, out boundaryY))
                {
                    errorMessage = "Grade Pad explicit patch could not project the stitch seam back onto the grading sections.";
                    return false;
                }

                boundaryZ = prepared.Pad.EvaluateZ(boundaryX, boundaryY);
                sectionShoulderZ = terrainFaceGrid.InterpolateZ(daylightX, daylightY);
                sectionShoulderZ = EvaluatePreparedPadBatterPlaneZ(
                    prepared,
                    daylightX,
                    daylightY,
                    sectionShoulderZ,
                    tolerance);
            }

            boundaryLoopXy[i * 2] = boundaryX;
            boundaryLoopXy[i * 2 + 1] = boundaryY;
            boundaryLoopZ[i] = boundaryZ;
            shoulderLoopXy[i * 2] = daylightX;
            shoulderLoopXy[i * 2 + 1] = daylightY;
            shoulderLoopZ[i] = sectionShoulderZ;
        }

        return true;
    }

    private static double EvaluateStripZ(
        double x,
        double y,
        double[] innerLoopXy,
        double[] innerLoopZ,
        double[] outerLoopXy,
        double[] outerLoopZ,
        int vertexCount)
    {
        if (vertexCount < 2 ||
            !TryFindClosestLoopLocation(innerLoopXy, vertexCount, x, y, out ClosestLoopLocation closest))
        {
            return innerLoopZ.Length > 0 ? innerLoopZ[0] : 0.0;
        }

        int segmentIndex = closest.SegmentIndex;
        int next = (segmentIndex + 1) % vertexCount;
        double t = closest.SegmentT;

        double innerX = LerpValue(innerLoopXy[segmentIndex * 2], innerLoopXy[next * 2], t);
        double innerY = LerpValue(innerLoopXy[(segmentIndex * 2) + 1], innerLoopXy[(next * 2) + 1], t);
        double innerZ = LerpValue(innerLoopZ[segmentIndex], innerLoopZ[next], t);
        double outerX = LerpValue(outerLoopXy[segmentIndex * 2], outerLoopXy[next * 2], t);
        double outerY = LerpValue(outerLoopXy[(segmentIndex * 2) + 1], outerLoopXy[(next * 2) + 1], t);
        double outerZ = LerpValue(outerLoopZ[segmentIndex], outerLoopZ[next], t);

        double dx = outerX - innerX;
        double dy = outerY - innerY;
        double lengthSq = (dx * dx) + (dy * dy);
        if (lengthSq <= 1e-12)
            return innerZ;

        double projectedT = (((x - innerX) * dx) + ((y - innerY) * dy)) / lengthSq;
        projectedT = Math.Clamp(projectedT, 0.0, 1.0);
        return LerpValue(innerZ, outerZ, projectedT);
    }

    private static double ComputeLoopStationFraction(double[] loopXy, int vertexCount, ClosestLoopLocation location)
    {
        if (vertexCount < 2)
            return 0.0;

        double perimeter = 0.0;
        double station = 0.0;
        for (int i = 0; i < vertexCount; i++)
        {
            int next = (i + 1) % vertexCount;
            double segmentLength = Math.Sqrt(DistanceSquaredXY(
                loopXy[i * 2],
                loopXy[i * 2 + 1],
                loopXy[next * 2],
                loopXy[next * 2 + 1]));
            if (i < location.SegmentIndex)
                station += segmentLength;
            else if (i == location.SegmentIndex)
                station += segmentLength * Math.Clamp(location.SegmentT, 0.0, 1.0);

            perimeter += segmentLength;
        }

        return perimeter <= 1e-12 ? 0.0 : Math.Clamp(station / perimeter, 0.0, 1.0);
    }

    private static double ComputeLoopVertexStationFraction(double[] loopXy, int vertexCount, int vertexIndex)
    {
        if (vertexCount < 2)
            return 0.0;

        int clampedVertexIndex = Math.Clamp(vertexIndex, 0, vertexCount - 1);
        double perimeter = 0.0;
        double station = 0.0;
        for (int i = 0; i < vertexCount; i++)
        {
            int next = (i + 1) % vertexCount;
            double segmentLength = Math.Sqrt(DistanceSquaredXY(
                loopXy[i * 2],
                loopXy[i * 2 + 1],
                loopXy[next * 2],
                loopXy[next * 2 + 1]));
            if (i < clampedVertexIndex)
                station += segmentLength;

            perimeter += segmentLength;
        }

        return perimeter <= 1e-12 ? 0.0 : Math.Clamp(station / perimeter, 0.0, 1.0);
    }

    private static bool TrySampleLoopAtFraction(double[] loopXy, int vertexCount, double stationFraction, out double x, out double y)
    {
        x = 0.0;
        y = 0.0;
        if (vertexCount < 2)
            return false;

        double perimeter = 0.0;
        for (int i = 0; i < vertexCount; i++)
        {
            int next = (i + 1) % vertexCount;
            perimeter += Math.Sqrt(DistanceSquaredXY(
                loopXy[i * 2],
                loopXy[i * 2 + 1],
                loopXy[next * 2],
                loopXy[next * 2 + 1]));
        }

        if (perimeter <= 1e-12)
            return false;

        double targetStation = Math.Clamp(stationFraction, 0.0, 1.0) * perimeter;
        double accumulated = 0.0;
        for (int i = 0; i < vertexCount; i++)
        {
            int next = (i + 1) % vertexCount;
            double ax = loopXy[i * 2];
            double ay = loopXy[i * 2 + 1];
            double bx = loopXy[next * 2];
            double by = loopXy[next * 2 + 1];
            double segmentLength = Math.Sqrt(DistanceSquaredXY(ax, ay, bx, by));
            if (segmentLength <= 1e-12)
                continue;

            if (accumulated + segmentLength >= targetStation || i == vertexCount - 1)
            {
                double t = Math.Clamp((targetStation - accumulated) / segmentLength, 0.0, 1.0);
                x = LerpValue(ax, bx, t);
                y = LerpValue(ay, by, t);
                return true;
            }

            accumulated += segmentLength;
        }

        x = loopXy[0];
        y = loopXy[1];
        return true;
    }

    private static bool IsFoldDirectionAtCorner(PreparedPadSections prepared, int vertexIndex, double tolerance)
    {
        int count = prepared.BoundaryVertexCount;
        double cx = prepared.BoundaryLoopXy[vertexIndex * 2];
        double cy = prepared.BoundaryLoopXy[vertexIndex * 2 + 1];
        double fx = prepared.ShoulderXy[vertexIndex * 2] - cx;
        double fy = prepared.ShoulderXy[vertexIndex * 2 + 1] - cy;
        double fLen = Math.Sqrt(fx * fx + fy * fy);
        if (fLen < tolerance) return false;
        fx /= fLen;
        fy /= fLen;

        const double cosThreshold = 0.94; // about 20 degrees
        int prev = (vertexIndex - 1 + count) % count;
        int next = (vertexIndex + 1) % count;

        double pdx = cx - prepared.BoundaryLoopXy[prev * 2];
        double pdy = cy - prepared.BoundaryLoopXy[prev * 2 + 1];
        double pLen = Math.Sqrt(pdx * pdx + pdy * pdy);
        if (pLen > tolerance)
        {
            double dot = (fx * pdy - fy * pdx) / pLen;
            if (Math.Abs(dot) >= cosThreshold) return false;
        }

        double ndx = prepared.BoundaryLoopXy[next * 2] - cx;
        double ndy = prepared.BoundaryLoopXy[next * 2 + 1] - cy;
        double nLen = Math.Sqrt(ndx * ndx + ndy * ndy);
        if (nLen > tolerance)
        {
            double dot = (fx * ndy - fy * ndx) / nLen;
            if (Math.Abs(dot) >= cosThreshold) return false;
        }

        return true;
    }

    private static double EvaluatePreparedPadBatterPlaneZ(
        PreparedPadSections prepared,
        double x,
        double y,
        double fallbackZ,
        double tolerance)
    {
        if (!TryFindClosestLoopLocation(prepared.BoundaryLoopXy, prepared.BoundaryVertexCount, x, y, out ClosestLoopLocation closest))
            return fallbackZ;

        int initialNext = (closest.SegmentIndex + 1) % prepared.BoundaryVertexCount;
        double initialAx = prepared.BoundaryLoopXy[closest.SegmentIndex * 2];
        double initialAy = prepared.BoundaryLoopXy[closest.SegmentIndex * 2 + 1];
        double initialBx = prepared.BoundaryLoopXy[initialNext * 2];
        double initialBy = prepared.BoundaryLoopXy[initialNext * 2 + 1];
        double initialLength = Math.Sqrt(DistanceSquaredXY(initialAx, initialAy, initialBx, initialBy));
        if (initialLength > tolerance)
        {
            int cornerIndex = -1;
            double distanceFromStart = closest.SegmentT * initialLength;
            double distanceFromEnd = (1.0 - closest.SegmentT) * initialLength;
            double startFoldLength = Math.Sqrt(DistanceSquaredXY(
                prepared.BoundaryLoopXy[closest.SegmentIndex * 2],
                prepared.BoundaryLoopXy[closest.SegmentIndex * 2 + 1],
                prepared.ShoulderXy[closest.SegmentIndex * 2],
                prepared.ShoulderXy[closest.SegmentIndex * 2 + 1]));
            double endFoldLength = Math.Sqrt(DistanceSquaredXY(
                prepared.BoundaryLoopXy[initialNext * 2],
                prepared.BoundaryLoopXy[initialNext * 2 + 1],
                prepared.ShoulderXy[initialNext * 2],
                prepared.ShoulderXy[initialNext * 2 + 1]));

            if (distanceFromStart <= Math.Max(tolerance * 32.0, startFoldLength * 0.5) &&
                IsFoldDirectionAtCorner(prepared, closest.SegmentIndex, tolerance))
                cornerIndex = closest.SegmentIndex;
            if (distanceFromEnd <= Math.Max(tolerance * 32.0, endFoldLength * 0.5) &&
                IsFoldDirectionAtCorner(prepared, initialNext, tolerance) &&
                (cornerIndex < 0 || distanceFromEnd < distanceFromStart))
            {
                cornerIndex = initialNext;
            }

            if (cornerIndex >= 0)
            {
                int previousSegment = (cornerIndex - 1 + prepared.BoundaryVertexCount) % prepared.BoundaryVertexCount;
                int nextSegment = cornerIndex;
                int afterCorner = (cornerIndex + 1) % prepared.BoundaryVertexCount;
                double cornerX = prepared.BoundaryLoopXy[cornerIndex * 2];
                double cornerY = prepared.BoundaryLoopXy[cornerIndex * 2 + 1];
                double foldX = prepared.ShoulderXy[cornerIndex * 2] - cornerX;
                double foldY = prepared.ShoulderXy[cornerIndex * 2 + 1] - cornerY;
                double foldLengthSq = (foldX * foldX) + (foldY * foldY);
                if (foldLengthSq > tolerance * tolerance)
                {
                    double pointSide = (foldX * (y - cornerY)) - (foldY * (x - cornerX));
                    double previousMidX = ((prepared.BoundaryLoopXy[previousSegment * 2] + cornerX) * 0.5) - cornerX;
                    double previousMidY = ((prepared.BoundaryLoopXy[previousSegment * 2 + 1] + cornerY) * 0.5) - cornerY;
                    double nextMidX = ((prepared.BoundaryLoopXy[afterCorner * 2] + cornerX) * 0.5) - cornerX;
                    double nextMidY = ((prepared.BoundaryLoopXy[afterCorner * 2 + 1] + cornerY) * 0.5) - cornerY;
                    double previousSide = (foldX * previousMidY) - (foldY * previousMidX);
                    double nextSide = (foldX * nextMidY) - (foldY * nextMidX);

                    if (Math.Abs(pointSide) > tolerance &&
                        Math.Abs(previousSide) > tolerance &&
                        Math.Abs(nextSide) > tolerance)
                    {
                        if (Math.Sign(pointSide) == Math.Sign(previousSide) &&
                            Math.Sign(pointSide) != Math.Sign(nextSide))
                        {
                            double segmentAx = prepared.BoundaryLoopXy[previousSegment * 2];
                            double segmentAy = prepared.BoundaryLoopXy[previousSegment * 2 + 1];
                            double segmentDx = cornerX - segmentAx;
                            double segmentDy = cornerY - segmentAy;
                            double segmentLengthSq = (segmentDx * segmentDx) + (segmentDy * segmentDy);
                            if (segmentLengthSq > tolerance * tolerance)
                            {
                                double t = Math.Clamp((((x - segmentAx) * segmentDx) + ((y - segmentAy) * segmentDy)) / segmentLengthSq, 0.0, 1.0);
                                double projectionX = segmentAx + (segmentDx * t);
                                double projectionY = segmentAy + (segmentDy * t);
                                double distance = Math.Sqrt(DistanceSquaredXY(x, y, projectionX, projectionY));
                                closest = new ClosestLoopLocation(previousSegment, t, distance);
                            }
                        }
                        else if (Math.Sign(pointSide) == Math.Sign(nextSide) &&
                                 Math.Sign(pointSide) != Math.Sign(previousSide))
                        {
                            double segmentBx = prepared.BoundaryLoopXy[afterCorner * 2];
                            double segmentBy = prepared.BoundaryLoopXy[afterCorner * 2 + 1];
                            double segmentDx = segmentBx - cornerX;
                            double segmentDy = segmentBy - cornerY;
                            double segmentLengthSq = (segmentDx * segmentDx) + (segmentDy * segmentDy);
                            if (segmentLengthSq > tolerance * tolerance)
                            {
                                double t = Math.Clamp((((x - cornerX) * segmentDx) + ((y - cornerY) * segmentDy)) / segmentLengthSq, 0.0, 1.0);
                                double projectionX = cornerX + (segmentDx * t);
                                double projectionY = cornerY + (segmentDy * t);
                                double distance = Math.Sqrt(DistanceSquaredXY(x, y, projectionX, projectionY));
                                closest = new ClosestLoopLocation(nextSegment, t, distance);
                            }
                        }
                    }
                }
            }
        }

        if (!TryInterpolatePadSection(
            prepared,
            closest,
            out double boundaryX,
            out double boundaryY,
            out double boundaryZ,
            out double shoulderX,
            out double shoulderY,
            out double shoulderZ))
        {
            return fallbackZ;
        }

        int next = (closest.SegmentIndex + 1) % prepared.BoundaryVertexCount;
        double ax = prepared.BoundaryLoopXy[closest.SegmentIndex * 2];
        double ay = prepared.BoundaryLoopXy[closest.SegmentIndex * 2 + 1];
        double bx = prepared.BoundaryLoopXy[next * 2];
        double by = prepared.BoundaryLoopXy[next * 2 + 1];
        double edgeX = bx - ax;
        double edgeY = by - ay;
        double edgeLength = Math.Sqrt((edgeX * edgeX) + (edgeY * edgeY));
        if (edgeLength <= tolerance)
            return fallbackZ;

        bool ccw = ClipperGeometry.SignedArea(prepared.BoundaryLoopXy) >= 0.0;
        double normalX = ccw ? edgeY / edgeLength : -edgeY / edgeLength;
        double normalY = ccw ? -edgeX / edgeLength : edgeX / edgeLength;
        double projectedReach = ((x - boundaryX) * normalX) + ((y - boundaryY) * normalY);
        if (projectedReach < 0.0)
            projectedReach = closest.Distance;

        double sectionReach = Math.Sqrt(((shoulderX - boundaryX) * (shoulderX - boundaryX)) + ((shoulderY - boundaryY) * (shoulderY - boundaryY)));
        if (sectionReach <= tolerance)
            return boundaryZ;

        projectedReach = Math.Clamp(projectedReach, 0.0, sectionReach);
        double branchSign = Math.Sign(shoulderZ - boundaryZ);
        if (Math.Abs(branchSign) <= 1e-12)
            return boundaryZ;

        double slopeRatio = Math.Tan(prepared.Pad.SlopeAngleDeg * Math.PI / 180.0);
        double z = boundaryZ + (branchSign * slopeRatio * projectedReach);
        return ClampBetween(z, boundaryZ, shoulderZ);
    }

    private static bool TryBuildClosedLoopNormalizedStations(double[] loopXy, int vertexCount, out double[] stations)
    {
        stations = Array.Empty<double>();
        if (vertexCount < 3 || loopXy.Length < vertexCount * 2)
            return false;

        if (!TryBuildClosedLoopCumulativeDistances(loopXy, vertexCount, out double[] cumulative, out double perimeter) ||
            perimeter <= 1e-9)
        {
            return false;
        }

        stations = new double[vertexCount];
        for (int i = 0; i < vertexCount; i++)
            stations[i] = cumulative[i] / perimeter;

        return true;
    }

    private static double[] AlignClosedLoopToReference(double[] loopXy, double[] referenceLoopXy, double tolerance)
    {
        if ((loopXy.Length / 2) < 3)
            return (double[])loopXy.Clone();

        double[] result = (double[])loopXy.Clone();
        if (ClipperGeometry.SignedArea(result) * ClipperGeometry.SignedArea(referenceLoopXy) < 0.0)
            result = ReverseClosedLoop(result);

        int vertexCount = result.Length / 2;
        double refX = referenceLoopXy[0];
        double refY = referenceLoopXy[1];
        int bestIndex = 0;
        double bestDistSq = double.MaxValue;
        for (int i = 0; i < vertexCount; i++)
        {
            double dx = result[i * 2] - refX;
            double dy = result[i * 2 + 1] - refY;
            double distSq = (dx * dx) + (dy * dy);
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                bestIndex = i;
            }
        }

        if (bestIndex == 0 || bestDistSq <= tolerance * tolerance)
            return RotateClosedLoop(result, bestIndex);

        return RotateClosedLoop(result, bestIndex);
    }

    private static double[] ReverseClosedLoop(double[] loopXy)
    {
        int vertexCount = loopXy.Length / 2;
        var reversed = new double[loopXy.Length];
        for (int i = 0; i < vertexCount; i++)
        {
            int source = vertexCount - 1 - i;
            reversed[i * 2] = loopXy[source * 2];
            reversed[i * 2 + 1] = loopXy[source * 2 + 1];
        }

        return reversed;
    }

    private static double[] RotateClosedLoop(double[] loopXy, int startIndex)
    {
        int vertexCount = loopXy.Length / 2;
        if (vertexCount == 0 || startIndex % vertexCount == 0)
            return (double[])loopXy.Clone();

        var rotated = new double[loopXy.Length];
        for (int i = 0; i < vertexCount; i++)
        {
            int source = (startIndex + i) % vertexCount;
            rotated[i * 2] = loopXy[source * 2];
            rotated[i * 2 + 1] = loopXy[source * 2 + 1];
        }

        return rotated;
    }

    private static bool ResampleClosedLoop(
        double[] loopXy,
        int vertexCount,
        int targetCount,
        out double[] resampledXy)
    {
        resampledXy = Array.Empty<double>();
        if (vertexCount < 3 || targetCount < 3)
            return false;

        double perimeter = 0.0;
        var cumulative = new double[vertexCount + 1];
        for (int i = 0; i < vertexCount; i++)
        {
            int next = (i + 1) % vertexCount;
            double dx = loopXy[next * 2] - loopXy[i * 2];
            double dy = loopXy[next * 2 + 1] - loopXy[i * 2 + 1];
            perimeter += Math.Sqrt((dx * dx) + (dy * dy));
            cumulative[i + 1] = perimeter;
        }

        if (perimeter <= 1e-9)
            return false;

        resampledXy = new double[targetCount * 2];
        int segmentIndex = 0;
        for (int i = 0; i < targetCount; i++)
        {
            double distance = (perimeter * i) / targetCount;
            while (segmentIndex < vertexCount - 1 && cumulative[segmentIndex + 1] < distance)
                segmentIndex++;

            int next = (segmentIndex + 1) % vertexCount;
            double segmentStart = cumulative[segmentIndex];
            double segmentEnd = cumulative[segmentIndex + 1];
            double segmentLength = segmentEnd - segmentStart;
            double t = segmentLength > 1e-9 ? (distance - segmentStart) / segmentLength : 0.0;
            resampledXy[i * 2] = LerpValue(loopXy[segmentIndex * 2], loopXy[next * 2], t);
            resampledXy[i * 2 + 1] = LerpValue(loopXy[segmentIndex * 2 + 1], loopXy[next * 2 + 1], t);
        }

        return true;
    }

    private static bool SampleClosedLoopAtNormalizedStations(
        double[] loopXy,
        int vertexCount,
        double[] normalizedStations,
        out double[] sampledXy)
    {
        sampledXy = Array.Empty<double>();
        if (vertexCount < 3 || normalizedStations.Length < 3)
            return false;

        if (!TryBuildClosedLoopCumulativeDistances(loopXy, vertexCount, out double[] cumulative, out double perimeter) ||
            perimeter <= 1e-9)
        {
            return false;
        }

        sampledXy = new double[normalizedStations.Length * 2];
        int segmentIndex = 0;
        for (int i = 0; i < normalizedStations.Length; i++)
        {
            double distance = Math.Clamp(normalizedStations[i], 0.0, 1.0) * perimeter;
            while (segmentIndex < vertexCount - 1 && cumulative[segmentIndex + 1] < distance)
                segmentIndex++;

            int next = (segmentIndex + 1) % vertexCount;
            double segmentStart = cumulative[segmentIndex];
            double segmentEnd = cumulative[segmentIndex + 1];
            double segmentLength = segmentEnd - segmentStart;
            double t = segmentLength > 1e-9 ? (distance - segmentStart) / segmentLength : 0.0;
            sampledXy[i * 2] = LerpValue(loopXy[segmentIndex * 2], loopXy[next * 2], t);
            sampledXy[i * 2 + 1] = LerpValue(loopXy[segmentIndex * 2 + 1], loopXy[next * 2 + 1], t);
        }

        return true;
    }

    private static bool ResampleClosedLoopWithVertexZ(
        double[] loopXy,
        double[] loopZ,
        int vertexCount,
        int targetCount,
        out double[] resampledXy,
        out double[] resampledZ)
    {
        resampledXy = Array.Empty<double>();
        resampledZ = Array.Empty<double>();
        if (loopZ.Length < vertexCount)
            return false;
        if (!ResampleClosedLoop(loopXy, vertexCount, targetCount, out resampledXy))
            return false;

        double perimeter = 0.0;
        var cumulative = new double[vertexCount + 1];
        for (int i = 0; i < vertexCount; i++)
        {
            int next = (i + 1) % vertexCount;
            double dx = loopXy[next * 2] - loopXy[i * 2];
            double dy = loopXy[next * 2 + 1] - loopXy[i * 2 + 1];
            perimeter += Math.Sqrt((dx * dx) + (dy * dy));
            cumulative[i + 1] = perimeter;
        }

        resampledZ = new double[targetCount];
        int segmentIndex = 0;
        for (int i = 0; i < targetCount; i++)
        {
            double distance = (perimeter * i) / targetCount;
            while (segmentIndex < vertexCount - 1 && cumulative[segmentIndex + 1] < distance)
                segmentIndex++;

            int next = (segmentIndex + 1) % vertexCount;
            double segmentStart = cumulative[segmentIndex];
            double segmentEnd = cumulative[segmentIndex + 1];
            double segmentLength = segmentEnd - segmentStart;
            double t = segmentLength > 1e-9 ? (distance - segmentStart) / segmentLength : 0.0;
            resampledZ[i] = LerpValue(loopZ[segmentIndex], loopZ[next], t);
        }

        return true;
    }

    private static bool SampleClosedLoopAtNormalizedStationsWithVertexZ(
        double[] loopXy,
        double[] loopZ,
        int vertexCount,
        double[] normalizedStations,
        out double[] sampledXy,
        out double[] sampledZ)
    {
        sampledXy = Array.Empty<double>();
        sampledZ = Array.Empty<double>();
        if (loopZ.Length < vertexCount)
            return false;
        if (!SampleClosedLoopAtNormalizedStations(loopXy, vertexCount, normalizedStations, out sampledXy))
            return false;

        if (!TryBuildClosedLoopCumulativeDistances(loopXy, vertexCount, out double[] cumulative, out double perimeter) ||
            perimeter <= 1e-9)
        {
            return false;
        }

        sampledZ = new double[normalizedStations.Length];
        int segmentIndex = 0;
        for (int i = 0; i < normalizedStations.Length; i++)
        {
            double distance = Math.Clamp(normalizedStations[i], 0.0, 1.0) * perimeter;
            while (segmentIndex < vertexCount - 1 && cumulative[segmentIndex + 1] < distance)
                segmentIndex++;

            int next = (segmentIndex + 1) % vertexCount;
            double segmentStart = cumulative[segmentIndex];
            double segmentEnd = cumulative[segmentIndex + 1];
            double segmentLength = segmentEnd - segmentStart;
            double t = segmentLength > 1e-9 ? (distance - segmentStart) / segmentLength : 0.0;
            sampledZ[i] = LerpValue(loopZ[segmentIndex], loopZ[next], t);
        }

        return true;
    }

    private static bool TryBuildClosedLoopCumulativeDistances(
        double[] loopXy,
        int vertexCount,
        out double[] cumulative,
        out double perimeter)
    {
        cumulative = Array.Empty<double>();
        perimeter = 0.0;
        if (vertexCount < 3 || loopXy.Length < vertexCount * 2)
            return false;

        cumulative = new double[vertexCount + 1];
        for (int i = 0; i < vertexCount; i++)
        {
            int next = (i + 1) % vertexCount;
            double dx = loopXy[next * 2] - loopXy[i * 2];
            double dy = loopXy[next * 2 + 1] - loopXy[i * 2 + 1];
            perimeter += Math.Sqrt((dx * dx) + (dy * dy));
            cumulative[i + 1] = perimeter;
        }

        return perimeter > 1e-9;
    }

    private static double[] SimplifyClosedLoopByShortEdges(double[] loopXy, double minEdgeLength)
    {
        int vertexCount = loopXy.Length / 2;
        if (vertexCount < 4 || minEdgeLength <= 0.0)
            return (double[])loopXy.Clone();

        double minEdgeLengthSq = minEdgeLength * minEdgeLength;
        var keep = new bool[vertexCount];
        Array.Fill(keep, true);

        bool changed;
        do
        {
            changed = false;
            int keptCount = 0;
            for (int i = 0; i < vertexCount; i++)
                if (keep[i])
                    keptCount++;

            if (keptCount <= 3)
                break;

            for (int i = 0; i < vertexCount; i++)
            {
                if (!keep[i])
                    continue;

                int next = FindNextKeptIndex(keep, i);
                if (next == i)
                    break;

                double dx = loopXy[next * 2] - loopXy[i * 2];
                double dy = loopXy[next * 2 + 1] - loopXy[i * 2 + 1];
                if ((dx * dx) + (dy * dy) > minEdgeLengthSq)
                    continue;

                if (next == 0)
                    continue;

                keep[next] = false;
                changed = true;
            }
        }
        while (changed);

        int finalCount = 0;
        for (int i = 0; i < vertexCount; i++)
            if (keep[i])
                finalCount++;

        if (finalCount < 3 || finalCount == vertexCount)
            return (double[])loopXy.Clone();

        var simplified = new double[finalCount * 2];
        int write = 0;
        for (int i = 0; i < vertexCount; i++)
        {
            if (!keep[i])
                continue;

            simplified[write * 2] = loopXy[i * 2];
            simplified[write * 2 + 1] = loopXy[i * 2 + 1];
            write++;
        }

        return simplified;
    }

    private static int FindNextKeptIndex(bool[] keep, int start)
    {
        int count = keep.Length;
        for (int offset = 1; offset <= count; offset++)
        {
            int candidate = (start + offset) % count;
            if (keep[candidate])
                return candidate;
        }

        return start;
    }

    private static PatchMeshResult? TryBuildPadTopMesh(
        PadBoundary pad,
        double[] boundaryLoopXy,
        int vertexCount,
        out string? errorMessage)
    {
        errorMessage = null;
        var polygon = new Polygon(vertexCount);
        var boundaryVertices = new Vertex[vertexCount];
        for (int i = 0; i < vertexCount; i++)
            boundaryVertices[i] = new Vertex(boundaryLoopXy[i * 2], boundaryLoopXy[i * 2 + 1]) { ID = i };
        polygon.Add(new Contour(boundaryVertices), false);

        IMesh mesh;
        try
        {
            mesh = TriangulationHelper.TriangulatePolygon(
                polygon,
                new ConstraintOptions { ConformingDelaunay = false, Convex = false, SegmentSplitting = 0 },
                null);
        }
        catch (Exception ex)
        {
            errorMessage = $"Explicit Grade Pad top triangulation failed: {ex.Message}";
            return null;
        }

        if (mesh.Triangles.Count == 0)
        {
            errorMessage = "Explicit Grade Pad top triangulation produced no triangles.";
            return null;
        }

        var extracted = TriangleNetExtractor.Extract(mesh);
        var vertices = new double[extracted.VertexCount * 3];
        for (int i = 0; i < extracted.VertexCount; i++)
        {
            double x = extracted.Xy[i * 2];
            double y = extracted.Xy[i * 2 + 1];
            vertices[i * 3] = x;
            vertices[i * 3 + 1] = y;
            vertices[i * 3 + 2] = pad.EvaluateZ(x, y);
        }

        return new PatchMeshResult
        {
            Vertices = vertices,
            VertexCount = extracted.VertexCount,
            Faces = extracted.Faces,
            FaceCount = extracted.FaceCount
        };
    }

    private static PatchMeshResult BuildRuledStripMesh(
        double[] innerLoopXy,
        double[] innerLoopZ,
        double[] outerLoopXy,
        double[] outerLoopZ,
        int ringCount,
        double tolerance,
        int intermediateRingCount)
    {
        int rowCount = intermediateRingCount + 2;
        var vertices = new double[ringCount * rowCount * 3];
        var faces = new List<int>(ringCount * (rowCount - 1) * 6);

        for (int i = 0; i < ringCount; i++)
        {
            double innerX = innerLoopXy[i * 2];
            double innerY = innerLoopXy[i * 2 + 1];
            double innerZ = innerLoopZ[i];
            double outerX = outerLoopXy[i * 2];
            double outerY = outerLoopXy[i * 2 + 1];
            double outerZ = outerLoopZ[i];
            for (int row = 0; row < rowCount; row++)
            {
                double t = rowCount > 1 ? (double)row / (rowCount - 1) : 0.0;
                int vertexIndex = (i * rowCount) + row;
                vertices[vertexIndex * 3] = LerpValue(innerX, outerX, t);
                vertices[vertexIndex * 3 + 1] = LerpValue(innerY, outerY, t);
                vertices[vertexIndex * 3 + 2] = LerpValue(innerZ, outerZ, t);
            }
        }

        for (int row = 0; row < rowCount - 1; row++)
        {
            for (int i = 0; i < ringCount; i++)
            {
                int next = (i + 1) % ringCount;
                int i0 = (i * rowCount) + row;
                int o0 = i0 + 1;
                int i1 = (next * rowCount) + row;
                int o1 = i1 + 1;

                bool innerCollapsed = VerticesCoincident(vertices, i0, i1, tolerance);
                bool outerCollapsed = VerticesCoincident(vertices, o0, o1, tolerance);
                if (innerCollapsed && outerCollapsed)
                {
                    continue;
                }

                if (innerCollapsed)
                {
                    AddTriangleIfNonDegenerate(vertices, faces, i0, o1, o0, tolerance);
                    continue;
                }

                if (outerCollapsed)
                {
                    AddTriangleIfNonDegenerate(vertices, faces, i0, i1, o0, tolerance);
                    continue;
                }

                double diagonalA = DistanceSquaredXY(vertices, i0, o1);
                double diagonalB = DistanceSquaredXY(vertices, i1, o0);
                if (diagonalA <= diagonalB)
                {
                    AddTriangleIfNonDegenerate(vertices, faces, i0, i1, o1, tolerance);
                    AddTriangleIfNonDegenerate(vertices, faces, i0, o1, o0, tolerance);
                }
                else
                {
                    AddTriangleIfNonDegenerate(vertices, faces, i0, i1, o0, tolerance);
                    AddTriangleIfNonDegenerate(vertices, faces, i1, o1, o0, tolerance);
                }
            }
        }

        return new PatchMeshResult
        {
            Vertices = vertices,
            VertexCount = vertices.Length / 3,
            Faces = faces.ToArray(),
            FaceCount = faces.Count / 3
        };
    }

    private static double[] BuildPadBoundaryLoopZ(PadBoundary pad, double[] boundaryLoopXy, int vertexCount)
    {
        var boundaryLoopZ = new double[vertexCount];
        for (int i = 0; i < vertexCount; i++)
            boundaryLoopZ[i] = pad.EvaluateZ(boundaryLoopXy[i * 2], boundaryLoopXy[i * 2 + 1]);

        return boundaryLoopZ;
    }

    private static OutputPolyline BuildPadBoundaryPolyline(PreparedPadSections prepared)
    {
        var xyz = new double[prepared.BoundaryVertexCount * 3];
        for (int i = 0; i < prepared.BoundaryVertexCount; i++)
        {
            double x = prepared.BoundaryLoopXy[i * 2];
            double y = prepared.BoundaryLoopXy[i * 2 + 1];
            xyz[i * 3] = x;
            xyz[i * 3 + 1] = y;
            xyz[i * 3 + 2] = prepared.Pad.EvaluateZ(x, y);
        }

        return new OutputPolyline(xyz, prepared.BoundaryVertexCount, isClosed: true);
    }

    private static IReadOnlyList<OutputPolyline> BuildPadBoundaryPolylines(PadBoundary[] pads)
    {
        var polylines = new List<OutputPolyline>(pads.Length);
        foreach (var pad in pads)
        {
            var xyz = new double[pad.VertexCount * 3];
            for (int i = 0; i < pad.VertexCount; i++)
            {
                double x = pad.XyVertices[i * 2];
                double y = pad.XyVertices[i * 2 + 1];
                xyz[i * 3] = x;
                xyz[i * 3 + 1] = y;
                xyz[i * 3 + 2] = pad.EvaluateZ(x, y);
            }

            polylines.Add(new OutputPolyline(xyz, pad.VertexCount, isClosed: true));
        }

        return polylines;
    }

    private static List<GradingPatch> BuildPadPatchSummaries(IReadOnlyList<PadBoundary> pads)
    {
        var patches = new List<GradingPatch>(pads.Count);
        for (int i = 0; i < pads.Count; i++)
        {
            var pad = pads[i];
            double priority = ComputePadOwnershipPriority(pad);
            patches.Add(new GradingPatch
            {
                OwnerKey = $"pad:{i}",
                Kind = GradingPatchKind.Pad,
                Priority = priority,
                OwnedRegionLoopXy = (double[])pad.XyVertices.Clone(),
                DaylightLoopXy = Array.Empty<double>(),
                StitchLoopXy = Array.Empty<double>(),
                DirtyBounds = GradingPatch.ComputeBounds(pad.XyVertices),
                UsesFallbackBand = false
            });
        }

        return patches;
    }

    private static GradingPatch BuildPadPatchSummary(PreparedPadSections prepared, double[] stitchLoopXy, int padIndex, double tolerance)
    {
        double priority = ComputePadOwnershipPriority(prepared.Pad);
        bool usesFallbackBand = !LoopsCoincide(prepared.ShoulderXy, stitchLoopXy, tolerance);
        return new GradingPatch
        {
            OwnerKey = $"pad:{padIndex}",
            Kind = GradingPatchKind.Pad,
            Priority = priority,
            OwnedRegionLoopXy = (double[])stitchLoopXy.Clone(),
            DaylightLoopXy = (double[])prepared.ShoulderXy.Clone(),
            StitchLoopXy = (double[])stitchLoopXy.Clone(),
            DirtyBounds = GradingPatch.ComputeBounds(stitchLoopXy),
            UsesFallbackBand = usesFallbackBand
        };
    }

    private static bool HasMeaningfulPadShoulderReach(PreparedPadSections prepared, double tolerance)
    {
        double toleranceSquared = tolerance * tolerance;
        for (int i = 0; i < prepared.BoundaryVertexCount; i++)
        {
            double dx = prepared.ShoulderXy[i * 2] - prepared.BoundaryLoopXy[i * 2];
            double dy = prepared.ShoulderXy[i * 2 + 1] - prepared.BoundaryLoopXy[i * 2 + 1];
            if ((dx * dx) + (dy * dy) > toleranceSquared)
                return true;
        }

        return false;
    }

    private static bool LoopsCoincide(double[] leftLoopXy, double[] rightLoopXy, double tolerance)
    {
        ComputeLoopDeviation(leftLoopXy, rightLoopXy, out double leftMax, out int leftMisses, tolerance);
        ComputeLoopDeviation(rightLoopXy, leftLoopXy, out double rightMax, out int rightMisses, tolerance);
        return leftMisses == 0 && rightMisses == 0 && leftMax <= tolerance && rightMax <= tolerance;
    }

    private static string[] BuildPadStitchDiagnostics(
        int padIndex,
        double[] shoulderLoopXy,
        double[] seamLoopXy,
        double[] patchLoopXy,
        double[] patchVertices,
        int[] patchFaces,
        int patchFaceCount,
        double[] outsideVertices,
        int[] outsideFaces,
        int outsideFaceCount,
        double tolerance)
    {
        var diagnostics = new List<string>();
        int seamVertexCount = seamLoopXy.Length / 2;
        int patchVertexCount = patchLoopXy.Length / 2;
        diagnostics.Add($"Grade Pad[{padIndex}] seam vertices: split={seamVertexCount}, patch={patchVertexCount}.");

        ComputeLoopDeviation(seamLoopXy, patchLoopXy, out double seamToPatchMax, out int seamMissCount, tolerance * 2.0);
        ComputeLoopDeviation(patchLoopXy, seamLoopXy, out double patchToSeamMax, out int patchMissCount, tolerance * 2.0);
        diagnostics.Add(
            $"Grade Pad[{padIndex}] seam deviation: split->patch max={seamToPatchMax:F6} ({seamMissCount} misses), patch->split max={patchToSeamMax:F6} ({patchMissCount} misses).");

        ComputeLoopDistanceStats(shoulderLoopXy, seamLoopXy, out double shoulderToSeamMin, out double shoulderToSeamMax);
        ComputeLoopDistanceStats(seamLoopXy, shoulderLoopXy, out double seamToShoulderMin, out double seamToShoulderMax);
        diagnostics.Add(
            $"Grade Pad[{padIndex}] topology band width: shoulder->seam min={shoulderToSeamMin:F6}, max={shoulderToSeamMax:F6}; seam->shoulder min={seamToShoulderMin:F6}, max={seamToShoulderMax:F6}.");

        SeamGraph seamGraph = SeamGraph.Build(
            seamLoopXy,
            patchVertices,
            patchFaces,
            patchFaceCount,
            outsideVertices,
            outsideFaces,
            outsideFaceCount,
            tolerance);
        diagnostics.Add($"Grade Pad[{padIndex}] patch boundary edges near seam: {seamGraph.PatchBoundaryEdgesNearSeam}.");
        diagnostics.Add($"Grade Pad[{padIndex}] outside-mesh naked edges near seam: {seamGraph.TerrainBoundaryEdgesNearSeam}.");
        diagnostics.Add($"Grade Pad[{padIndex}] seam segment matches: patch={seamGraph.PatchMatchedSegments}/{seamVertexCount}, outside={seamGraph.TerrainMatchedSegments}/{seamVertexCount}.");
        diagnostics.Add($"Grade Pad[{padIndex}] seam-near boundary segments: patch={seamGraph.PatchBoundarySegmentsNearSeam}, outside={seamGraph.TerrainBoundarySegmentsNearSeam}.");
        return diagnostics.ToArray();
    }

    private static string[] BuildPadSlopeDiagnostics(
        int padIndex,
        PreparedPadSections prepared,
        PatchMeshResult patch,
        double tolerance)
    {
        int measuredFaceCount = 0;
        double minSlopeDeg = double.MaxValue;
        double maxSlopeDeg = 0.0;
        double slopeSumDeg = 0.0;
        double targetSlopeDeg = prepared.Pad.SlopeAngleDeg;
        double toleranceSquared = tolerance * tolerance;
        double maxSlopeX = 0.0;
        double maxSlopeY = 0.0;

        for (int faceIndex = 0; faceIndex < patch.FaceCount; faceIndex++)
        {
            int a = patch.Faces[faceIndex * 3];
            int b = patch.Faces[faceIndex * 3 + 1];
            int c = patch.Faces[faceIndex * 3 + 2];
            double cx = (patch.Vertices[a * 3] + patch.Vertices[b * 3] + patch.Vertices[c * 3]) / 3.0;
            double cy = (patch.Vertices[a * 3 + 1] + patch.Vertices[b * 3 + 1] + patch.Vertices[c * 3 + 1]) / 3.0;

            if (PointInPolygon(cx, cy, prepared.BoundaryLoopXy, prepared.BoundaryVertexCount))
                continue;
            if (!PointInPolygon(cx, cy, prepared.ShoulderXy, prepared.BoundaryVertexCount))
                continue;
            if (DistToPolygon(cx, cy, prepared.BoundaryLoopXy, prepared.BoundaryVertexCount) <= tolerance * 2.0)
                continue;

            double ax = patch.Vertices[a * 3];
            double ay = patch.Vertices[a * 3 + 1];
            double az = patch.Vertices[a * 3 + 2];
            double bx = patch.Vertices[b * 3];
            double by = patch.Vertices[b * 3 + 1];
            double bz = patch.Vertices[b * 3 + 2];
            double dx = patch.Vertices[c * 3] - ax;
            double dy = patch.Vertices[c * 3 + 1] - ay;
            double dz = patch.Vertices[c * 3 + 2] - az;
            double ux = bx - ax;
            double uy = by - ay;
            double uz = bz - az;

            double nx = (uy * dz) - (uz * dy);
            double ny = (uz * dx) - (ux * dz);
            double nz = (ux * dy) - (uy * dx);
            double normalLengthSquared = (nx * nx) + (ny * ny) + (nz * nz);
            if (normalLengthSquared <= toleranceSquared * toleranceSquared)
                continue;

            double horizontal = Math.Sqrt((nx * nx) + (ny * ny));
            double slopeDeg = Math.Atan2(horizontal, Math.Abs(nz)) * 180.0 / Math.PI;
            minSlopeDeg = Math.Min(minSlopeDeg, slopeDeg);
            if (slopeDeg > maxSlopeDeg)
            {
                maxSlopeDeg = slopeDeg;
                maxSlopeX = cx;
                maxSlopeY = cy;
            }
            slopeSumDeg += slopeDeg;
            measuredFaceCount++;
        }

        if (measuredFaceCount == 0)
            return [$"Grade Pad[{padIndex}] batter slope check: no measurable batter faces inside the shoulder loop."];

        double avgSlopeDeg = slopeSumDeg / measuredFaceCount;
        double maxDeltaDeg = Math.Max(Math.Abs(minSlopeDeg - targetSlopeDeg), Math.Abs(maxSlopeDeg - targetSlopeDeg));
        var diagnostics = new List<string>
        {
            $"Grade Pad[{padIndex}] batter slope check: target={targetSlopeDeg:F2} deg, faces={measuredFaceCount}, min={minSlopeDeg:F2}, avg={avgSlopeDeg:F2}, max={maxSlopeDeg:F2}, max delta={maxDeltaDeg:F2} deg."
        };

        if (maxDeltaDeg > 5.0)
        {
            diagnostics.Add(
                $"Grade Pad[{padIndex}] batter slope warning: output deviates from target by up to {maxDeltaDeg:F2} deg near ({maxSlopeX:F3}, {maxSlopeY:F3}); inspect clipped daylight, nearby pads, or terrain-boundary stitching.");
        }

        return diagnostics.ToArray();
    }

    private static bool VerticesCoincident(double[] vertices, int firstIndex, int secondIndex, double tolerance)
    {
        double dx = vertices[firstIndex * 3] - vertices[secondIndex * 3];
        double dy = vertices[firstIndex * 3 + 1] - vertices[secondIndex * 3 + 1];
        return (dx * dx) + (dy * dy) <= tolerance * tolerance;
    }

    private static void AddTriangleIfNonDegenerate(double[] vertices, List<int> faces, int a, int b, int c, double tolerance)
    {
        if (a == b || b == c || c == a)
            return;

        double ax = vertices[a * 3];
        double ay = vertices[a * 3 + 1];
        double bx = vertices[b * 3];
        double by = vertices[b * 3 + 1];
        double cx = vertices[c * 3];
        double cy = vertices[c * 3 + 1];
        double area2 = ((bx - ax) * (cy - ay)) - ((by - ay) * (cx - ax));
        if (Math.Abs(area2) <= tolerance * tolerance)
            return;

        faces.Add(a);
        faces.Add(b);
        faces.Add(c);
    }

    private static void ComputeLoopDeviation(
        double[] sourceLoopXy,
        double[] targetLoopXy,
        out double maxDistance,
        out int missCount,
        double matchTolerance)
    {
        maxDistance = 0.0;
        missCount = 0;
        int sourceCount = sourceLoopXy.Length / 2;
        int targetCount = targetLoopXy.Length / 2;
        for (int i = 0; i < sourceCount; i++)
        {
            double px = sourceLoopXy[i * 2];
            double py = sourceLoopXy[i * 2 + 1];
            double bestDistance = double.MaxValue;
            if (TryFindClosestLoopLocation(targetLoopXy, targetCount, px, py, out ClosestLoopLocation closest))
                bestDistance = closest.Distance;

            if (bestDistance > maxDistance)
                maxDistance = bestDistance;
            if (bestDistance > matchTolerance)
                missCount++;
        }
    }

    private static void ComputeLoopDistanceStats(
        double[] sourceLoopXy,
        double[] targetLoopXy,
        out double minDistance,
        out double maxDistance)
    {
        minDistance = double.MaxValue;
        maxDistance = 0.0;
        int sourceCount = sourceLoopXy.Length / 2;
        int targetCount = targetLoopXy.Length / 2;
        for (int i = 0; i < sourceCount; i++)
        {
            double px = sourceLoopXy[i * 2];
            double py = sourceLoopXy[i * 2 + 1];
            double bestDistance = double.MaxValue;
            if (TryFindClosestLoopLocation(targetLoopXy, targetCount, px, py, out ClosestLoopLocation closest))
                bestDistance = closest.Distance;

            if (bestDistance < minDistance)
                minDistance = bestDistance;
            if (bestDistance > maxDistance)
                maxDistance = bestDistance;
        }

        if (minDistance == double.MaxValue)
            minDistance = 0.0;
    }

    private static int CountBoundaryEdgesNearLoop(
        double[] vertices,
        int[] faces,
        int faceCount,
        double[] loopXy,
        double distanceTolerance)
    {
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

        int boundaryNearLoop = 0;
        foreach (var pair in edgeFaceCount)
        {
            if (pair.Value != 1)
                continue;

            int a = (int)(pair.Key >> 32);
            int b = (int)(pair.Key & 0xFFFFFFFFL);
            double mx = (vertices[a * 3] + vertices[b * 3]) * 0.5;
            double my = (vertices[a * 3 + 1] + vertices[b * 3 + 1]) * 0.5;
            if (DistToPolygon(mx, my, loopXy, loopXy.Length / 2) <= distanceTolerance)
                boundaryNearLoop++;
        }

        return boundaryNearLoop;
    }

    private static int CountInteriorBoundaryEdgesNearLoop(
        double[] vertices,
        int[] faces,
        int faceCount,
        double[] seamLoopXy,
        double seamTolerance,
        double[]? terrainBoundaryLoop,
        double terrainBoundaryTolerance)
    {
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

        int interiorBoundaryNearLoop = 0;
        int seamVertexCount = seamLoopXy.Length / 2;
        int terrainBoundaryVertexCount = terrainBoundaryLoop?.Length / 2 ?? 0;
        foreach (var pair in edgeFaceCount)
        {
            if (pair.Value != 1)
                continue;

            int a = (int)(pair.Key >> 32);
            int b = (int)(pair.Key & 0xFFFFFFFFL);
            double mx = (vertices[a * 3] + vertices[b * 3]) * 0.5;
            double my = (vertices[a * 3 + 1] + vertices[b * 3 + 1]) * 0.5;
            if (DistToPolygon(mx, my, seamLoopXy, seamVertexCount) > seamTolerance)
                continue;

            if (terrainBoundaryLoop != null &&
                terrainBoundaryVertexCount >= 3 &&
                DistToPolygon(mx, my, terrainBoundaryLoop, terrainBoundaryVertexCount) <= terrainBoundaryTolerance)
            {
                continue;
            }

            interiorBoundaryNearLoop++;
        }

        return interiorBoundaryNearLoop;
    }

    private static int CountBoundaryEdgesAwayFromLoop(
        double[] vertices,
        int[] faces,
        int faceCount,
        double[] loopXy,
        double distanceTolerance)
    {
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

        int boundaryAwayFromLoop = 0;
        foreach (var pair in edgeFaceCount)
        {
            if (pair.Value != 1)
                continue;

            int a = (int)(pair.Key >> 32);
            int b = (int)(pair.Key & 0xFFFFFFFFL);
            double mx = (vertices[a * 3] + vertices[b * 3]) * 0.5;
            double my = (vertices[a * 3 + 1] + vertices[b * 3 + 1]) * 0.5;
            if (DistToPolygon(mx, my, loopXy, loopXy.Length / 2) > distanceTolerance)
                boundaryAwayFromLoop++;
        }

        return boundaryAwayFromLoop;
    }

    private static int CountBoundaryEdgesAwayFromReferenceBoundary(
        double[] vertices,
        int[] faces,
        int faceCount,
        double[] referenceVertices,
        int[] referenceFaces,
        int referenceFaceCount,
        double distanceTolerance)
    {
        var referenceBoundarySegments = BuildBoundarySegments(referenceVertices, referenceFaces, referenceFaceCount);
        if (referenceBoundarySegments.Count == 0)
            return 0;

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

        double toleranceSquared = distanceTolerance * distanceTolerance;
        int boundaryAway = 0;
        foreach (var pair in edgeFaceCount)
        {
            if (pair.Value != 1)
                continue;

            int a = (int)(pair.Key >> 32);
            int b = (int)(pair.Key & 0xFFFFFFFFL);
            double mx = (vertices[a * 3] + vertices[b * 3]) * 0.5;
            double my = (vertices[a * 3 + 1] + vertices[b * 3 + 1]) * 0.5;
            bool nearReferenceBoundary = false;
            for (int i = 0; i < referenceBoundarySegments.Count; i++)
            {
                var segment = referenceBoundarySegments[i];
                if (DistanceSquaredPointToSegment(mx, my, segment.Ax, segment.Ay, segment.Bx, segment.By) <= toleranceSquared)
                {
                    nearReferenceBoundary = true;
                    break;
                }
            }

            if (!nearReferenceBoundary)
                boundaryAway++;
        }

        return boundaryAway;
    }

    private static List<(double Ax, double Ay, double Bx, double By)> BuildBoundarySegments(
        double[] vertices,
        int[] faces,
        int faceCount)
    {
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

        var segments = new List<(double Ax, double Ay, double Bx, double By)>();
        foreach (var pair in edgeFaceCount)
        {
            if (pair.Value != 1)
                continue;

            int a = (int)(pair.Key >> 32);
            int b = (int)(pair.Key & 0xFFFFFFFFL);
            segments.Add((
                vertices[a * 3],
                vertices[a * 3 + 1],
                vertices[b * 3],
                vertices[b * 3 + 1]));
        }

        return segments;
    }

    private static double DistanceSquaredXY(double ax, double ay, double bx, double by)
    {
        double dx = ax - bx;
        double dy = ay - by;
        return (dx * dx) + (dy * dy);
    }

    private static double DistanceSquaredPointToSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax;
        double dy = by - ay;
        double lenSq = (dx * dx) + (dy * dy);
        if (lenSq <= 1e-16)
            return DistanceSquaredXY(px, py, ax, ay);

        double t = (((px - ax) * dx) + ((py - ay) * dy)) / lenSq;
        t = Math.Clamp(t, 0.0, 1.0);
        double qx = ax + (t * dx);
        double qy = ay + (t * dy);
        return DistanceSquaredXY(px, py, qx, qy);
    }

    private static double DistanceSquaredXY(double[] vertices, int firstIndex, int secondIndex)
    {
        double dx = vertices[firstIndex * 3] - vertices[secondIndex * 3];
        double dy = vertices[firstIndex * 3 + 1] - vertices[secondIndex * 3 + 1];
        return (dx * dx) + (dy * dy);
    }

    private static bool ShouldUseTopologyBand(double[] shoulderLoopXy, double[] seamLoopXy, double tolerance)
    {
        ComputeLoopDistanceStats(shoulderLoopXy, seamLoopXy, out _, out double shoulderToSeamMax);
        ComputeLoopDistanceStats(seamLoopXy, shoulderLoopXy, out _, out double seamToShoulderMax);
        double maxBandWidth = Math.Max(shoulderToSeamMax, seamToShoulderMax);
        double threshold = Math.Max(tolerance * 16.0, 0.05);
        return maxBandWidth > threshold;
    }

    private static void CountMatchedBoundarySegments(
        double[] seamLoopXy,
        double[] meshVertices,
        int[] meshFaces,
        int meshFaceCount,
        double tolerance,
        out int matchedSegments,
        out int boundarySegmentsNearSeam)
    {
        matchedSegments = 0;
        boundarySegmentsNearSeam = 0;
        int seamVertexCount = seamLoopXy.Length / 2;
        var edgeFaceCount = new Dictionary<long, int>(8, IndexedMeshTools.EdgeKeyComparer.Instance);
        for (int f = 0; f < meshFaceCount; f++)
        {
            int a = meshFaces[f * 3];
            int b = meshFaces[f * 3 + 1];
            int c = meshFaces[f * 3 + 2];
            IncrEdge(edgeFaceCount, a, b);
            IncrEdge(edgeFaceCount, b, c);
            IncrEdge(edgeFaceCount, c, a);
        }

        var boundarySegments = new List<(double Ax, double Ay, double Bx, double By)>();
        foreach (var pair in edgeFaceCount)
        {
            if (pair.Value != 1)
                continue;

            int a = (int)(pair.Key >> 32);
            int b = (int)(pair.Key & 0xFFFFFFFFL);
            double ax = meshVertices[a * 3];
            double ay = meshVertices[a * 3 + 1];
            double bx = meshVertices[b * 3];
            double by = meshVertices[b * 3 + 1];
            double mx = (ax + bx) * 0.5;
            double my = (ay + by) * 0.5;
            if (DistToPolygon(mx, my, seamLoopXy, seamVertexCount) <= tolerance * 4.0)
            {
                boundarySegmentsNearSeam++;
                boundarySegments.Add((ax, ay, bx, by));
            }
        }

        double tolSq = tolerance * tolerance;
        for (int i = 0; i < seamVertexCount; i++)
        {
            int next = (i + 1) % seamVertexCount;
            double sax = seamLoopXy[i * 2];
            double say = seamLoopXy[i * 2 + 1];
            double sbx = seamLoopXy[next * 2];
            double sby = seamLoopXy[next * 2 + 1];
            bool matched = false;
            for (int j = 0; j < boundarySegments.Count; j++)
            {
                var edge = boundarySegments[j];
                if ((DistanceSquaredXY(sax, say, edge.Ax, edge.Ay) <= tolSq &&
                     DistanceSquaredXY(sbx, sby, edge.Bx, edge.By) <= tolSq) ||
                    (DistanceSquaredXY(sax, say, edge.Bx, edge.By) <= tolSq &&
                     DistanceSquaredXY(sbx, sby, edge.Ax, edge.Ay) <= tolSq))
                {
                    matched = true;
                    break;
                }
            }

            if (matched)
                matchedSegments++;
        }
    }

    private static bool TryExtractAreaMesh(
        MeshAreaSplitter.SplitResult split,
        int areaIndex,
        out double[] vertices,
        out int vertexCount,
        out int[] faces,
        out int faceCount)
    {
        return TryExtractAreaMeshes(split, index => index == areaIndex, out vertices, out vertexCount, out faces, out faceCount);
    }

    private static bool TryExtractMeshesByLoopContainment(
        MeshAreaSplitter.SplitResult split,
        double[] loopXy,
        bool includeInside,
        double tolerance,
        out double[] vertices,
        out int vertexCount,
        out int[] faces,
        out int faceCount)
    {
        int loopVertexCount = loopXy.Length / 2;
        var selectedFaces = new List<int>();
        for (int faceIndex = 0; faceIndex < split.FaceCount; faceIndex++)
        {
            int i0 = split.Faces[faceIndex * 3];
            int i1 = split.Faces[faceIndex * 3 + 1];
            int i2 = split.Faces[faceIndex * 3 + 2];
            double cx = (split.Vertices[i0 * 3] + split.Vertices[i1 * 3] + split.Vertices[i2 * 3]) / 3.0;
            double cy = (split.Vertices[i0 * 3 + 1] + split.Vertices[i1 * 3 + 1] + split.Vertices[i2 * 3 + 1]) / 3.0;
            bool inside = PointInPolygon(cx, cy, loopXy, loopVertexCount) ||
                          DistToPolygon(cx, cy, loopXy, loopVertexCount) <= tolerance;
            if (includeInside ? inside : !inside)
                selectedFaces.Add(faceIndex);
        }

        return ExtractSelectedFaces(split, selectedFaces, out vertices, out vertexCount, out faces, out faceCount);
    }

    private static bool TryExtractAreaMeshes(
        MeshAreaSplitter.SplitResult split,
        Func<int, bool> includeArea,
        out double[] vertices,
        out int vertexCount,
        out int[] faces,
        out int faceCount)
    {
        var selectedFaces = new List<int>();
        for (int faceIndex = 0; faceIndex < split.FaceCount; faceIndex++)
        {
            if (includeArea(split.FaceAreaIndex[faceIndex]))
                selectedFaces.Add(faceIndex);
        }

        return ExtractSelectedFaces(split, selectedFaces, out vertices, out vertexCount, out faces, out faceCount);
    }

    private static bool ExtractSelectedFaces(
        MeshAreaSplitter.SplitResult split,
        List<int> selectedFaces,
        out double[] vertices,
        out int vertexCount,
        out int[] faces,
        out int faceCount)
    {
        if (selectedFaces.Count == 0)
        {
            vertices = Array.Empty<double>();
            vertexCount = 0;
            faces = Array.Empty<int>();
            faceCount = 0;
            return false;
        }

        var usedVertices = new HashSet<int>();
        foreach (int faceIndex in selectedFaces)
        {
            usedVertices.Add(split.Faces[faceIndex * 3]);
            usedVertices.Add(split.Faces[faceIndex * 3 + 1]);
            usedVertices.Add(split.Faces[faceIndex * 3 + 2]);
        }

        var remap = new Dictionary<int, int>(usedVertices.Count);
        vertices = new double[usedVertices.Count * 3];
        int nextVertex = 0;
        foreach (int originalVertex in usedVertices.OrderBy(static value => value))
        {
            remap[originalVertex] = nextVertex;
            vertices[nextVertex * 3] = split.Vertices[originalVertex * 3];
            vertices[nextVertex * 3 + 1] = split.Vertices[originalVertex * 3 + 1];
            vertices[nextVertex * 3 + 2] = split.Vertices[originalVertex * 3 + 2];
            nextVertex++;
        }

        faces = new int[selectedFaces.Count * 3];
        for (int i = 0; i < selectedFaces.Count; i++)
        {
            int faceIndex = selectedFaces[i];
            faces[i * 3] = remap[split.Faces[faceIndex * 3]];
            faces[i * 3 + 1] = remap[split.Faces[faceIndex * 3 + 1]];
            faces[i * 3 + 2] = remap[split.Faces[faceIndex * 3 + 2]];
        }

        vertexCount = nextVertex;
        faceCount = selectedFaces.Count;
        return true;
    }

    private static PatchMeshResult BuildSplitLocalPadPatchMesh(
        TerrainFaceGrid terrainFaceGrid,
        PreparedBarriers barriers,
        PreparedPadSections prepared,
        double[] seamLoopXy,
        double[] localVertices,
        int localVertexCount,
        int[] localFaces,
        int localFaceCount,
        double tolerance)
    {
        var gradedVertices = (double[])localVertices.Clone();
        ApplyPreparedPadGradingToVertices(
            gradedVertices,
            localVertices,
            localVertexCount,
            prepared,
            barriers,
            tolerance);

        int seamVertexCount = seamLoopXy.Length / 2;
        for (int i = 0; i < localVertexCount; i++)
        {
            double x = gradedVertices[i * 3];
            double y = gradedVertices[i * 3 + 1];
            if (DistToPolygon(x, y, seamLoopXy, seamVertexCount) <= tolerance * 2.0)
                gradedVertices[i * 3 + 2] = terrainFaceGrid.InterpolateZ(x, y);
        }

        return new PatchMeshResult
        {
            Vertices = gradedVertices,
            VertexCount = localVertexCount,
            Faces = (int[])localFaces.Clone(),
            FaceCount = localFaceCount,
            StitchLoopXy = (double[])seamLoopXy.Clone()
        };
    }

    private static void ApplyPreparedPadGradingToVertices(
        double[] gradedVertices,
        double[] originalVertices,
        int vertexCount,
        PreparedPadSections prepared,
        PreparedBarriers barriers,
        double tolerance)
    {
        var barrierScratch = new SpatialHashGrid2D.QueryScratch(Math.Max(barriers.Segments.Length, 1));
        var barrierCandidates = new List<int>(8);
        for (int i = 0; i < vertexCount; i++)
        {
            double px = gradedVertices[i * 3];
            double py = gradedVertices[i * 3 + 1];
            if (px < prepared.InfluenceMinX - tolerance ||
                px > prepared.InfluenceMaxX + tolerance ||
                py < prepared.InfluenceMinY - tolerance ||
                py > prepared.InfluenceMaxY + tolerance)
            {
                continue;
            }

            if (PointInPolygon(px, py, prepared.Pad.XyVertices, prepared.Pad.VertexCount) ||
                DistToPolygon(px, py, prepared.Pad.XyVertices, prepared.Pad.VertexCount) <= tolerance)
            {
                gradedVertices[i * 3 + 2] = prepared.Pad.EvaluateZ(px, py);
                continue;
            }

            if (!TryFindClosestLoopLocation(prepared.BoundaryLoopXy, prepared.BoundaryVertexCount, px, py, out ClosestLoopLocation closest) ||
                !TryInterpolatePadSection(
                    prepared,
                    closest,
                    out double boundaryX,
                    out double boundaryY,
                    out double boundaryZ,
                    out double shoulderX,
                    out double shoulderY,
                    out double shoulderZ))
            {
                continue;
            }

            if (barriers.Segments.Length > 0 &&
                GradingBarriers.IsCrossedByBarrier(
                    barriers,
                    px,
                    py,
                    boundaryX,
                    boundaryY,
                    barrierScratch,
                    barrierCandidates))
            {
                continue;
            }

            double sectionReach = Math.Sqrt(((shoulderX - boundaryX) * (shoulderX - boundaryX)) + ((shoulderY - boundaryY) * (shoulderY - boundaryY)));
            if (sectionReach <= 1e-9 || closest.Distance > sectionReach + tolerance)
                continue;

            double candidateZ = boundaryZ + ((shoulderZ - boundaryZ) * Math.Clamp(closest.Distance / sectionReach, 0.0, 1.0));
            if (Math.Abs(candidateZ - originalVertices[i * 3 + 2]) > GradingTolerances.VertexAdjustmentZTolerance(tolerance))
                gradedVertices[i * 3 + 2] = candidateZ;
        }
    }

    private static bool TryBuildSeamLoopFromOutsideMesh(
        double[] vertices,
        int[] faces,
        int faceCount,
        double[] referenceLoopXy,
        double tolerance,
        out double[] seamLoopXy)
    {
        seamLoopXy = Array.Empty<double>();
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

        var adjacency = new Dictionary<int, List<int>>();
        int segmentCount = 0;
        foreach (var pair in edgeFaceCount)
        {
            if (pair.Value != 1)
                continue;

            int a = (int)(pair.Key >> 32);
            int b = (int)(pair.Key & 0xFFFFFFFFL);
            double mx = (vertices[a * 3] + vertices[b * 3]) * 0.5;
            double my = (vertices[a * 3 + 1] + vertices[b * 3 + 1]) * 0.5;
            if (DistToPolygon(mx, my, referenceLoopXy, referenceLoopXy.Length / 2) > tolerance * 4.0)
                continue;

            AddBoundaryNeighbor(adjacency, a, b);
            AddBoundaryNeighbor(adjacency, b, a);
            segmentCount++;
        }

        if (segmentCount < 3 || adjacency.Count < 3)
            return false;

        foreach (var neighbors in adjacency.Values)
        {
            if (neighbors.Count != 2)
                return false;
        }

        int start = adjacency.Keys.Min();
        var order = new List<int>(adjacency.Count);
        int previous = -1;
        int current = start;
        while (true)
        {
            order.Add(current);
            var neighbors = adjacency[current];
            int next = neighbors[0] != previous ? neighbors[0] : neighbors[1];
            previous = current;
            current = next;

            if (current == start)
                break;

            if (order.Count > adjacency.Count)
                return false;
        }

        if (order.Count < 3 || order.Count != adjacency.Count)
            return false;

        seamLoopXy = new double[order.Count * 2];
        for (int i = 0; i < order.Count; i++)
        {
            int vertexIndex = order[i];
            seamLoopXy[i * 2] = vertices[vertexIndex * 3];
            seamLoopXy[i * 2 + 1] = vertices[vertexIndex * 3 + 1];
        }

        return true;
    }

    private static bool TryBuildSeamLoopByReferenceProjection(
        double[] vertices,
        int[] faces,
        int faceCount,
        double[] referenceLoopXy,
        double tolerance,
        out double[] seamLoopXy)
    {
        seamLoopXy = Array.Empty<double>();
        int referenceVertexCount = referenceLoopXy.Length / 2;
        if (referenceVertexCount < 3)
            return false;

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

        double nearTolerance = Math.Max(tolerance * 8.0, 1e-6);
        var candidates = new List<(double Station, double X, double Y)>();
        foreach (var pair in edgeFaceCount)
        {
            if (pair.Value != 1)
                continue;

            int a = (int)(pair.Key >> 32);
            int b = (int)(pair.Key & 0xFFFFFFFFL);
            double ax = vertices[a * 3];
            double ay = vertices[a * 3 + 1];
            double bx = vertices[b * 3];
            double by = vertices[b * 3 + 1];
            double mx = (ax + bx) * 0.5;
            double my = (ay + by) * 0.5;
            if (DistToPolygon(mx, my, referenceLoopXy, referenceVertexCount) > nearTolerance)
                continue;

            AddProjectedCandidate(ax, ay);
            AddProjectedCandidate(bx, by);
        }

        if (candidates.Count < 3)
            return false;

        candidates.Sort(static (left, right) => left.Station.CompareTo(right.Station));
        var ordered = new List<double>(candidates.Count * 2);
        foreach (var candidate in candidates)
        {
            if (ordered.Count >= 2 &&
                DistanceSquaredXY(ordered[^2], ordered[^1], candidate.X, candidate.Y) <= tolerance * tolerance)
            {
                continue;
            }

            ordered.Add(candidate.X);
            ordered.Add(candidate.Y);
        }

        if (ordered.Count >= 4 &&
            DistanceSquaredXY(ordered[0], ordered[1], ordered[^2], ordered[^1]) <= tolerance * tolerance)
        {
            ordered.RemoveRange(ordered.Count - 2, 2);
        }

        if (ordered.Count / 2 < 3)
            return false;

        seamLoopXy = ordered.ToArray();
        return true;

        void AddProjectedCandidate(double x, double y)
        {
            if (!TryFindClosestLoopLocation(referenceLoopXy, referenceVertexCount, x, y, out ClosestLoopLocation closest) ||
                closest.Distance > nearTolerance)
            {
                return;
            }

            double station = ComputeLoopStationFraction(referenceLoopXy, referenceVertexCount, closest);
            candidates.Add((station, x, y));
        }
    }

    private static bool TryMergePatchWithOutsideTerrain(
        double[] outsideVertices,
        int outsideVertexCount,
        int[] outsideFaces,
        int outsideFaceCount,
        PatchMeshResult patch,
        double[] seamLoopXy,
        double[]? terrainBoundaryLoop,
        double tolerance,
        bool rejectInteriorSeamBoundaryEdges,
        out double[] mergedVertices,
        out int mergedVertexCount,
        out int[] mergedFaces,
        out int mergedFaceCount,
        out string? failureReason)
    {
        failureReason = null;
        double mergeTolerance = tolerance * 4.0;
        MergeMeshes(
            patch.Vertices,
            patch.VertexCount,
            patch.Faces,
            patch.FaceCount,
            outsideVertices,
            outsideVertexCount,
            outsideFaces,
            outsideFaceCount,
            mergeTolerance,
            out mergedVertices,
            out mergedVertexCount,
            out mergedFaces,
            out mergedFaceCount);

        if (rejectInteriorSeamBoundaryEdges)
        {
            int interiorBoundaryEdgesNearSeam = CountInteriorBoundaryEdgesNearLoop(
                mergedVertices,
                mergedFaces,
                mergedFaceCount,
                seamLoopXy,
                tolerance * 4.0,
                terrainBoundaryLoop,
                tolerance * 8.0);
            if (interiorBoundaryEdgesNearSeam > 0)
            {
                failureReason = $"{interiorBoundaryEdgesNearSeam} interior seam-adjacent naked edge(s)";
                return false;
            }
        }

        if (terrainBoundaryLoop != null)
        {
            int interiorNakedEdges = CountBoundaryEdgesAwayFromLoop(mergedVertices, mergedFaces, mergedFaceCount, terrainBoundaryLoop, tolerance * 8.0);
            if (interiorNakedEdges > 200)
            {
                failureReason = $"{interiorNakedEdges} interior naked edge(s)";
                return false;
            }
        }

        return true;
    }

    private static void MergeMeshes(
        double[] firstVertices,
        int firstVertexCount,
        int[] firstFaces,
        int firstFaceCount,
        double[] secondVertices,
        int secondVertexCount,
        int[] secondFaces,
        int secondFaceCount,
        double tolerance,
        out double[] mergedVertices,
        out int mergedVertexCount,
        out int[] mergedFaces,
        out int mergedFaceCount)
    {
        MeshTopologyOperations.MergeMeshes(
            firstVertices,
            firstVertexCount,
            firstFaces,
            firstFaceCount,
            secondVertices,
            secondVertexCount,
            secondFaces,
            secondFaceCount,
            tolerance,
            CoincidentVertexZPolicy.KeepFirst,
            out mergedVertices,
            out mergedVertexCount,
            out mergedFaces,
            out mergedFaceCount);
    }


    public static bool TryTriangulateTopology(
        double[] vertices, int vertexCount,
        int[] faces, int faceCount,
        PadBoundary[] pads,
        LockCurve[]? lockCurves,
        double maxArea,
        double minAngle,
        out double[] topologyVertices,
        out int topologyVertexCount,
        out int[] topologyFaces,
        out int topologyFaceCount,
        out string? warningOrError)
    {
        topologyVertices = Array.Empty<double>();
        topologyVertexCount = 0;
        topologyFaces = Array.Empty<int>();
        topologyFaceCount = 0;
        warningOrError = null;

        if (!ValidatePads(pads, out warningOrError))
            return false;

        pads = OrderPadsForOwnership(pads);

        PadTopologyResult? topology = TryTriangulatePadTopology(
            vertices,
            vertexCount,
            faces,
            faceCount,
            pads,
            lockCurves,
            maxArea,
            minAngle,
            out warningOrError);

        if (topology == null)
            return false;

        topologyVertices = topology.Vertices;
        topologyVertexCount = topology.VertexCount;
        topologyFaces = topology.Faces;
        topologyFaceCount = topology.FaceCount;
        return true;
    }

    public static double[] ApplyGradingZ(
        double[] topologyVertices,
        int vertexCount,
        PadBoundary[] pads,
        LockCurve[]? lockCurves = null)
    {
        if (pads.Length == 0)
            return (double[])topologyVertices.Clone();

        pads = OrderPadsForOwnership(pads);

        PreparedBarriers barriers = lockCurves != null && lockCurves.Length > 0
            ? GradingBarriers.BuildFromLockCurves(lockCurves)
            : PreparedBarriers.Empty;

        var gradedVertices = (double[])topologyVertices.Clone();
        ApplyGradingToVertices(gradedVertices, topologyVertices, vertexCount, pads, barriers);
        return gradedVertices;
    }

    public static double[] ApplyGradingZ(
        double[] topologyVertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PadBoundary[] pads,
        LockCurve[]? lockCurves = null)
    {
        if (pads.Length == 0)
            return (double[])topologyVertices.Clone();

        pads = OrderPadsForOwnership(pads);

        PreparedBarriers barriers = lockCurves != null && lockCurves.Length > 0
            ? GradingBarriers.BuildFromLockCurves(lockCurves)
            : PreparedBarriers.Empty;
        if (pads.Any(static pad => PolygonHasConcaveVertex(pad.XyVertices, pad.VertexCount)))
        {
            var fallbackGradedVertices = (double[])topologyVertices.Clone();
            ApplyGradingToVertices(fallbackGradedVertices, topologyVertices, vertexCount, pads, barriers);
            return fallbackGradedVertices;
        }

        bool hasBoundaryLoop = TryBuildBoundaryLoop(topologyVertices, faces, faceCount, out double[] boundaryLoop, out int boundaryVertexCount);
        var TerrainFaceGrid = new TerrainFaceGrid(topologyVertices, vertexCount, faces, faceCount);
        var gradedVertices = (double[])topologyVertices.Clone();
        ApplyGradingToVerticesWithSections(
            gradedVertices,
            topologyVertices,
            vertexCount,
            pads,
            barriers,
            TerrainFaceGrid,
            hasBoundaryLoop,
            boundaryLoop,
            boundaryVertexCount,
            tolerance: 1e-3);
        return gradedVertices;
    }

    internal static double[] ApplyGradingZWithBarriers(
        double[] topologyVertices,
        int vertexCount,
        PadBoundary[] pads,
        PreparedBarriers barriers)
    {
        if (pads.Length == 0)
            return (double[])topologyVertices.Clone();

        pads = OrderPadsForOwnership(pads);

        var gradedVertices = (double[])topologyVertices.Clone();
        ApplyGradingToVertices(gradedVertices, topologyVertices, vertexCount, pads, barriers);
        return gradedVertices;
    }

    public static ConstraintSet CreateConstraints(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PadBoundary[] pads,
        LockCurve[]? lockCurves,
        double modelTolerance = GradingTolerances.DefaultModelTolerance)
    {
        double dedupTol = GradingTolerances.ModelToleranceOrDefault(modelTolerance);
        if (!ValidatePads(pads, out _))
        {
            return new ConstraintSet
            {
                Constraints = Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
                SuggestedEdgeLength = 0.0,
                Diagnostics = Array.Empty<string>(),
                StructuredDiagnostics = Array.Empty<GradingDiagnostic>()
            };
        }

        pads = OrderPadsForOwnership(pads);

        bool hasBoundaryLoop = TryBuildBoundaryLoop(vertices, faces, faceCount, out var boundaryLoop, out int boundaryVertexCount);
        var constraints = new List<SurfaceRemesher.ConstraintPolyline>(pads.Length * 3 + (lockCurves?.Length ?? 0));
        var diagnostics = new GradingDiagnosticCollector();
        double suggestedEdgeLength = double.MaxValue;
        var coincidenceSnapper = new ConstraintCoincidenceSnapper(
            vertices,
            vertexCount,
            faces,
            faceCount,
            Math.Max(dedupTol, GradingTolerances.ConstraintSnapTolerance(dedupTol)));

        var faceGridForConstraints = new TerrainFaceGrid(vertices, vertexCount, faces, faceCount);
        for (int padIndex = 0; padIndex < pads.Length; padIndex++)
        {
            PadBoundary pad = pads[padIndex];
            double shoulderDistance = ComputePadTransitionDistance(vertices, vertexCount, pad);
            double segmentLength = ComputePadConstraintSegmentLength(shoulderDistance);
            var padLoop = BuildClosedConstraintLoop(pad.XyVertices, pad.VertexCount, segmentLength, dedupTol);
            constraints.Add(new SurfaceRemesher.ConstraintPolyline(
                CreateConstraintPoints(padLoop.XyVertices, padLoop.VertexCount),
                padLoop.VertexCount,
                IsClosed: true,
                PreserveInputElevation: false));
            suggestedEdgeLength = UpdateSuggestedEdgeLength(suggestedEdgeLength, padLoop.XyVertices, padLoop.VertexCount, stride: 2, isClosed: true);

            double[] shoulderDistances = ComputePadBoundaryDistances(padLoop.XyVertices, padLoop.VertexCount, faceGridForConstraints, pad);
            if (TryBuildShoulderLoop(
                padLoop.XyVertices,
                padLoop.VertexCount,
                shoulderDistances,
                hasBoundaryLoop ? boundaryLoop : null,
                hasBoundaryLoop ? boundaryVertexCount : 0,
                dedupTol,
                out var shoulderXy,
                out string? skipReason))
            {
                int shoulderVertexCount = shoulderXy.Length / 2;
                constraints.Add(new SurfaceRemesher.ConstraintPolyline(
                    CreateConstraintPoints(shoulderXy, shoulderVertexCount),
                    shoulderVertexCount,
                    IsClosed: true,
                    PreserveInputElevation: false));
                suggestedEdgeLength = Math.Min(suggestedEdgeLength, segmentLength);
                suggestedEdgeLength = UpdateSuggestedEdgeLength(suggestedEdgeLength, shoulderXy, shoulderVertexCount, stride: 2, isClosed: true);

                if (TryBuildProtectedStitchLoop(
                        shoulderXy,
                        pad.StitchApronDistance,
                        hasBoundaryLoop ? boundaryLoop : null,
                        hasBoundaryLoop ? boundaryVertexCount : 0,
                        dedupTol,
                        out double[] stitchXy,
                        out string? stitchSkipReason))
                {
                    int stitchVertexCount = stitchXy.Length / 2;
                    constraints.Add(new SurfaceRemesher.ConstraintPolyline(
                        CreateConstraintPoints(stitchXy, stitchVertexCount),
                        stitchVertexCount,
                        IsClosed: true,
                        PreserveInputElevation: false));
                    suggestedEdgeLength = UpdateSuggestedEdgeLength(suggestedEdgeLength, stitchXy, stitchVertexCount, stride: 2, isClosed: true);
                }
                else if (!string.IsNullOrWhiteSpace(stitchSkipReason))
                {
                    diagnostics.AddWarning(
                        "grade_pad.stitch_loop.skipped",
                        stitchSkipReason!,
                        operation: "Grade Pad",
                        targetIndex: padIndex);
                }
            }
            else if (!string.IsNullOrWhiteSpace(skipReason))
            {
                diagnostics.AddWarning(
                    "grade_pad.shoulder_loop.skipped",
                    skipReason!,
                    operation: "Grade Pad",
                    targetIndex: padIndex);
            }
        }

        if (lockCurves != null)
        {
            foreach (var lockCurve in lockCurves)
            {
                if (lockCurve.VertexCount < 2 || lockCurve.XyVertices.Length < lockCurve.VertexCount * 2)
                    continue;

                var points = new double[lockCurve.VertexCount * 3];
                for (int i = 0; i < lockCurve.VertexCount; i++)
                {
                    points[i * 3] = lockCurve.XyVertices[i * 2];
                    points[i * 3 + 1] = lockCurve.XyVertices[i * 2 + 1];
                }

                constraints.Add(new SurfaceRemesher.ConstraintPolyline(
                    points,
                    lockCurve.VertexCount,
                    IsClosed: false,
                    PreserveInputElevation: false));
                suggestedEdgeLength = UpdateSuggestedEdgeLength(suggestedEdgeLength, points, lockCurve.VertexCount, stride: 3, isClosed: false);
            }
        }

        for (int i = 0; i < constraints.Count; i++)
            constraints[i] = coincidenceSnapper.SnapConstraintPolyline(constraints[i]);

        return new ConstraintSet
        {
            Constraints = constraints.ToArray(),
            SuggestedEdgeLength = suggestedEdgeLength < double.MaxValue ? suggestedEdgeLength : 0.0,
            Diagnostics = diagnostics.ToMessages(),
            StructuredDiagnostics = diagnostics.ToStructuredDiagnostics()
        };
    }

    private static bool ValidatePads(PadBoundary[] pads, out string? errorMessage)
    {
        errorMessage = null;

        if (pads.Length == 0)
        {
            errorMessage = "No pad boundaries provided.";
            return false;
        }

        foreach (var pad in pads)
        {
            if (pad.VertexCount < 3 ||
                pad.XyVertices.Length < pad.VertexCount * 2 ||
                pad.BoundaryVertices.Length < pad.VertexCount * 3)
            {
                errorMessage = "Each pad must have at least 3 valid vertices.";
                return false;
            }

            if (!double.IsFinite(pad.PlaneXCoeff) ||
                !double.IsFinite(pad.PlaneYCoeff) ||
                !double.IsFinite(pad.PlaneConstant))
            {
                errorMessage = "Each pad must define a valid finished plane.";
                return false;
            }
        }

        return true;
    }

    private static PadTopologyResult? TryTriangulatePadTopology(
        double[] vertices, int vertexCount,
        int[] faces, int faceCount,
        PadBoundary[] pads,
        LockCurve[]? lockCurves,
        double maxArea,
        double minAngle,
        out string? warningOrError)
    {
        warningOrError = null;
        const double dedupTol = 1e-3;

        static bool IsInsideControlledRegion(double x, double y, IReadOnlyList<double[]> controlLoops)
        {
            foreach (double[] loop in controlLoops)
            {
                int loopVertexCount = loop.Length / 2;
                if (loopVertexCount >= 3 && PointInPolygon(x, y, loop, loopVertexCount))
                    return true;
            }

            return false;
        }

        var xyList = new List<double>(vertexCount * 2);
        var zList = new List<double>(vertexCount);
        var segList = new List<(int a, int b)>();
        var coincidenceSnapper = new ConstraintCoincidenceSnapper(
            vertices,
            vertexCount,
            faces,
            faceCount,
            Math.Max(dedupTol, GradingTolerances.ConstraintSnapTolerance(dedupTol)));

        var vertHash = new SpatialVertexHash(dedupTol);

        var TerrainFaceGrid = new TerrainFaceGrid(vertices, vertexCount, faces, faceCount);
        bool hasBoundaryLoop = TryBuildBoundaryLoop(vertices, faces, faceCount, out var boundaryLoop, out int boundaryVertexCount);
        var controlledLoops = new List<double[]>(pads.Length * 2);

        foreach (var pad in pads)
        {
            double[] initialDistances = ComputePadBoundaryDistances(pad.XyVertices, pad.VertexCount, TerrainFaceGrid, pad);
            double maxDistance = 0.0;
            foreach (double distance in initialDistances)
                maxDistance = Math.Max(maxDistance, distance);

            double segmentLength = ComputePadConstraintSegmentLength(maxDistance);
            var padLoop = BuildClosedConstraintLoop(pad.XyVertices, pad.VertexCount, segmentLength, dedupTol);
            controlledLoops.Add(padLoop.XyVertices);

            double[] shoulderDistances = ComputePadBoundaryDistances(padLoop.XyVertices, padLoop.VertexCount, TerrainFaceGrid, pad);
            if (TryBuildShoulderLoop(
                padLoop.XyVertices,
                padLoop.VertexCount,
                shoulderDistances,
                hasBoundaryLoop ? boundaryLoop : null,
                hasBoundaryLoop ? boundaryVertexCount : 0,
                dedupTol,
                out var shoulderXy,
                out _))
            {
                controlledLoops.Add(shoulderXy);
                if (TryBuildProtectedStitchLoop(
                        shoulderXy,
                        pad.StitchApronDistance,
                        hasBoundaryLoop ? boundaryLoop : null,
                        hasBoundaryLoop ? boundaryVertexCount : 0,
                        dedupTol,
                        out double[] stitchXy,
                        out _))
                {
                    controlledLoops.Add(stitchXy);
                }
            }
        }

        var originalIndexMap = new Dictionary<int, int>(vertexCount);

        int AddOriginalVertex(int originalIndex, bool forceInclude = false)
        {
            if (originalIndexMap.TryGetValue(originalIndex, out int existing))
                return existing;

            double x = vertices[originalIndex * 3];
            double y = vertices[originalIndex * 3 + 1];
            if (!forceInclude && IsInsideControlledRegion(x, y, controlledLoops))
                return -1;

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

        for (int i = 0; i < vertexCount; i++)
            AddOriginalVertex(i);

        int AddVertex(double x, double y)
        {
            coincidenceSnapper.SnapPoint(x, y, out x, out y);
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

        // Add mesh boundary edges as constraints (keeps triangulation within original mesh).
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
            if (kvp.Value == 1)
            {
                int a = (int)(kvp.Key >> 32);
                int b = (int)(kvp.Key & 0xFFFFFFFFL);
                int mappedA = AddOriginalVertex(a, forceInclude: true);
                int mappedB = AddOriginalVertex(b, forceInclude: true);
                if (mappedA >= 0 && mappedB >= 0 && mappedA != mappedB)
                    segList.Add((mappedA, mappedB));
            }
        }

        // Build barriers from lock curves so shoulder rings are clipped at hard constraints.
        PreparedBarriers padBarriers = lockCurves != null && lockCurves.Length > 0
            ? GradingBarriers.BuildFromLockCurves(lockCurves)
            : PreparedBarriers.Empty;
        var padBarrierScratch = new SpatialHashGrid2D.QueryScratch(Math.Max(padBarriers.Segments.Length, 1));
        var padBarrierCandidates = new List<int>(8);

        var padPolylines = new List<OutputPolyline>(pads.Length);

        foreach (var pad in pads)
        {
            double[] shoulderDistances = ComputePadBoundaryDistances(pad.XyVertices, pad.VertexCount, TerrainFaceGrid, pad);
            double shoulderDistance = 0; foreach (double d in shoulderDistances) if (d > shoulderDistance) shoulderDistance = d;
            double segmentLength = ComputePadConstraintSegmentLength(shoulderDistance);
            var padLoop = BuildClosedConstraintLoop(pad.XyVertices, pad.VertexCount, segmentLength, dedupTol);
            // Re-sample distances at padLoop resolution (which may have more vertices than the original pad)
            shoulderDistances = ComputePadBoundaryDistances(padLoop.XyVertices, padLoop.VertexCount, TerrainFaceGrid, pad);
            AddClosedLoopSegments(padLoop.XyVertices, padLoop.VertexCount, AddVertex, segList);

            // Build pad boundary output polyline with graded Z (pad plane Z at each vertex)
            int loopN = padLoop.VertexCount;
            var padPolyXyz = new double[loopN * 3];
            for (int i = 0; i < loopN; i++)
            {
                double bx = padLoop.XyVertices[i * 2];
                double by = padLoop.XyVertices[i * 2 + 1];
                padPolyXyz[i * 3]     = bx;
                padPolyXyz[i * 3 + 1] = by;
                padPolyXyz[i * 3 + 2] = pad.EvaluateZ(bx, by);
            }
            padPolylines.Add(new OutputPolyline(padPolyXyz, loopN, isClosed: true));

            double[]? shoulderXy = AddPadShoulderConstraint(
                padLoop.XyVertices,
                padLoop.VertexCount,
                shoulderDistances,
                xyList,
                zList,
                vertHash,
                TerrainFaceGrid,
                segList,
                dedupTol,
                hasBoundaryLoop ? boundaryLoop : null,
                hasBoundaryLoop ? boundaryVertexCount : 0,
                padBarriers,
                padBarrierScratch,
                padBarrierCandidates);
            if (shoulderXy != null &&
                TryBuildProtectedStitchLoop(
                    shoulderXy,
                    pad.StitchApronDistance,
                    hasBoundaryLoop ? boundaryLoop : null,
                    hasBoundaryLoop ? boundaryVertexCount : 0,
                    dedupTol,
                    out double[] stitchXy,
                    out _))
            {
                AddClosedLoopSegments(stitchXy, stitchXy.Length / 2, AddVertex, segList);
            }
        }

        if (lockCurves != null)
        {
            foreach (var lc in lockCurves)
            {
                if (lc.VertexCount < 2 || lc.XyVertices.Length < lc.VertexCount * 2)
                    continue;

                var lcIndices = new int[lc.VertexCount];
                for (int i = 0; i < lc.VertexCount; i++)
                {
                    double lx = lc.XyVertices[i * 2];
                    double ly = lc.XyVertices[i * 2 + 1];
                    coincidenceSnapper.SnapPoint(lx, ly, out lx, out ly);

                    int near = vertHash.FindNearest(xyList, lx, ly, dedupTol);
                    if (near >= 0)
                    {
                        lcIndices[i] = near;
                    }
                    else
                    {
                        lcIndices[i] = zList.Count;
                        xyList.Add(lx);
                        xyList.Add(ly);
                        zList.Add(TerrainFaceGrid.InterpolateZ(lx, ly));
                        vertHash.Insert(lcIndices[i], lx, ly);
                    }
                }

                for (int i = 0; i < lc.VertexCount - 1; i++)
                {
                    if (lcIndices[i] != lcIndices[i + 1])
                        segList.Add((lcIndices[i], lcIndices[i + 1]));
                }
            }
        }

        int totalVerts = zList.Count;
        if (totalVerts < 3)
        {
            warningOrError = "Too few vertices for triangulation.";
            return null;
        }

        TriangulationOutcome triangulation = TriangulationHelper.Triangulate(
            xyList,
            totalVerts,
            segList,
            maxArea,
            minAngle,
            convex: false);

        if (triangulation.Mesh == null)
        {
            warningOrError = triangulation.WarningMessage ?? "Triangulation failed.";
            return null;
        }

        if (!string.IsNullOrWhiteSpace(triangulation.WarningMessage))
            warningOrError = triangulation.WarningMessage;

        var extracted = TriangleNetExtractor.Extract(triangulation.Mesh);
        int outVertCount = extracted.VertexCount;
        int outFaceCount = extracted.FaceCount;

        var topologyVertices = new double[outVertCount * 3];
        for (int i = 0; i < outVertCount; i++)
        {
            double x = extracted.Xy[i * 2];
            double y = extracted.Xy[i * 2 + 1];
            int sourceId = extracted.SourceIds[i];

            double originalZ = sourceId >= 0 && sourceId < totalVerts
                ? zList[sourceId]
                : TerrainFaceGrid.InterpolateZ(x, y);

            topologyVertices[i * 3] = x;
            topologyVertices[i * 3 + 1] = y;
            topologyVertices[i * 3 + 2] = originalZ;
        }

        var topologyFaces = extracted.Faces;
        var cullResult = TriangleBoundaryCuller.Cull(
            topologyVertices,
            outVertCount,
            topologyFaces,
            outFaceCount,
            xyList.ToArray(),
            IndexedMeshTools.FlattenSegments(segList),
            0);

        if (cullResult.Changed)
        {
            topologyVertices = IndexedMeshTools.CompactDoubleData(topologyVertices, 3, cullResult.NewToOld, cullResult.VertexCount);
            topologyFaces = cullResult.Faces;
            outVertCount = cullResult.VertexCount;
            outFaceCount = cullResult.FaceCount;
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

    private static void ApplyGradingToVerticesWithSections(
        double[] gradedVertices,
        double[] originalVertices,
        int vertexCount,
        PadBoundary[] pads,
        PreparedBarriers barriers,
        TerrainFaceGrid TerrainFaceGrid,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double tolerance,
        bool keepShoulderOnBatterPlane = false)
    {
        if (pads.Length == 0)
            return;

        var preparedPads = new PreparedPadSections[pads.Length];
        var interiorBounds = new Bounds2D[pads.Length];
        var influenceBounds = new Bounds2D[pads.Length];
        double globalMinX = double.MaxValue;
        double globalMaxX = double.MinValue;
        double globalMinY = double.MaxValue;
        double globalMaxY = double.MinValue;

        for (int i = 0; i < pads.Length; i++)
        {
            preparedPads[i] = BuildPreparedPadSections(
                pads[i],
                TerrainFaceGrid,
                barriers,
                hasBoundaryLoop,
                boundaryLoop,
                boundaryVertexCount,
                tolerance,
                keepShoulderOnBatterPlane);
            interiorBounds[i] = new Bounds2D(
                preparedPads[i].MinX,
                preparedPads[i].MaxX,
                preparedPads[i].MinY,
                preparedPads[i].MaxY);
            influenceBounds[i] = new Bounds2D(
                preparedPads[i].InfluenceMinX,
                preparedPads[i].InfluenceMaxX,
                preparedPads[i].InfluenceMinY,
                preparedPads[i].InfluenceMaxY);

            if (preparedPads[i].InfluenceMinX < globalMinX) globalMinX = preparedPads[i].InfluenceMinX;
            if (preparedPads[i].InfluenceMaxX > globalMaxX) globalMaxX = preparedPads[i].InfluenceMaxX;
            if (preparedPads[i].InfluenceMinY < globalMinY) globalMinY = preparedPads[i].InfluenceMinY;
            if (preparedPads[i].InfluenceMaxY > globalMaxY) globalMaxY = preparedPads[i].InfluenceMaxY;
        }

        var interiorIndex = SpatialHashGrid2D.Build(interiorBounds);
        var influenceIndex = SpatialHashGrid2D.Build(influenceBounds);
        int barrierCount = Math.Max(barriers.Segments.Length, 1);
        System.Threading.Tasks.Parallel.For(
            0,
            vertexCount,
            () => (
                InteriorScratch: new SpatialHashGrid2D.QueryScratch(pads.Length),
                InfluenceScratch: new SpatialHashGrid2D.QueryScratch(pads.Length),
                BarrierScratch: new SpatialHashGrid2D.QueryScratch(barrierCount),
                InteriorCandidates: new List<int>(8),
                InfluenceCandidates: new List<int>(8),
                BarrierCandidates: new List<int>(8)),
            (i, _, state) =>
        {
            double px = gradedVertices[i * 3];
            double py = gradedVertices[i * 3 + 1];
            if (px < globalMinX || px > globalMaxX || py < globalMinY || py > globalMaxY)
                return state;

            interiorIndex.GatherCandidates(Bounds2D.FromPoint(px, py), state.InteriorCandidates, state.InteriorScratch);
            bool insidePadTop = false;
            double padTopZ = double.NegativeInfinity;
            foreach (int padIndex in state.InteriorCandidates)
            {
                var prepared = preparedPads[padIndex];
                if (px < prepared.MinX || px > prepared.MaxX || py < prepared.MinY || py > prepared.MaxY)
                    continue;

                if (PointInPolygon(px, py, prepared.Pad.XyVertices, prepared.Pad.VertexCount))
                {
                    padTopZ = Math.Max(padTopZ, pads[padIndex].EvaluateZ(px, py));
                    insidePadTop = true;
                }
            }

            influenceIndex.GatherCandidates(Bounds2D.FromPoint(px, py), state.InfluenceCandidates, state.InfluenceScratch);
            double nearestDistance = double.MaxValue;
            int nearestPadIdx = -1;
            double nearestCandidateZ = insidePadTop ? padTopZ : 0.0;
            double highestCandidateZ = insidePadTop ? padTopZ : double.NegativeInfinity;
            double zTolerance = GradingTolerances.VertexAdjustmentZTolerance(tolerance);

            foreach (int padIndex in state.InfluenceCandidates)
            {
                var prepared = preparedPads[padIndex];
                if (px < prepared.InfluenceMinX || px > prepared.InfluenceMaxX || py < prepared.InfluenceMinY || py > prepared.InfluenceMaxY)
                    continue;
                if (!TryFindClosestLoopLocation(prepared.BoundaryLoopXy, prepared.BoundaryVertexCount, px, py, out ClosestLoopLocation closest))
                    continue;
                if (!TryInterpolatePadSection(
                        prepared,
                        closest,
                        out double boundaryX,
                        out double boundaryY,
                        out double boundaryZ,
                        out double shoulderX,
                        out double shoulderY,
                        out double shoulderZ))
                {
                    continue;
                }

                if (barriers.Segments.Length > 0 &&
                    GradingBarriers.IsCrossedByBarrier(
                        barriers,
                        px,
                        py,
                        boundaryX,
                        boundaryY,
                        state.BarrierScratch,
                        state.BarrierCandidates))
                {
                    continue;
                }

                double sectionReach = Math.Sqrt(((shoulderX - boundaryX) * (shoulderX - boundaryX)) + ((shoulderY - boundaryY) * (shoulderY - boundaryY)));
                if (sectionReach <= 1e-9 || closest.Distance > sectionReach + 1e-9)
                    continue;

                double normalizedDistance = Math.Clamp(closest.Distance / sectionReach, 0.0, 1.0);
                double candidateZ = boundaryZ + ((shoulderZ - boundaryZ) * normalizedDistance);
                candidateZ = ClampBetween(candidateZ, boundaryZ, shoulderZ);
                if (Math.Abs(candidateZ - originalVertices[i * 3 + 2]) <= GradingTolerances.VertexAdjustmentZTolerance(tolerance))
                    continue;
                if (insidePadTop && candidateZ <= padTopZ + zTolerance)
                    continue;

                highestCandidateZ = Math.Max(highestCandidateZ, candidateZ);

                if (closest.Distance < nearestDistance - 1e-12 ||
                    (Math.Abs(closest.Distance - nearestDistance) <= 1e-12 && padIndex > nearestPadIdx))
                {
                    nearestDistance = closest.Distance;
                    nearestPadIdx = padIndex;
                    nearestCandidateZ = candidateZ;
                }
            }

            if (nearestPadIdx >= 0)
                gradedVertices[i * 3 + 2] = highestCandidateZ;
            else if (insidePadTop)
                gradedVertices[i * 3 + 2] = padTopZ;

            return state;
        }, _ => { });
    }

    private static PreparedPadSections BuildPreparedPadSections(
        PadBoundary pad,
        TerrainFaceGrid TerrainFaceGrid,
        PreparedBarriers barriers,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double tolerance,
        bool keepShoulderOnBatterPlane = false)
    {
        double[] initialDistances = ComputePadBoundaryDistances(
            pad.XyVertices,
            pad.VertexCount,
            TerrainFaceGrid,
            pad);
        double shoulderDistance = 0.0;
        foreach (double distance in initialDistances)
            shoulderDistance = Math.Max(shoulderDistance, distance);

        double segmentLength = ComputePadConstraintSegmentLength(shoulderDistance);
        ConstraintLoop padLoop = BuildClosedConstraintLoop(pad.XyVertices, pad.VertexCount, segmentLength, tolerance);
        double[] shoulderDistances = ComputePadBoundaryDistances(
            padLoop.XyVertices,
            padLoop.VertexCount,
            TerrainFaceGrid,
            pad);
        double minX = double.MaxValue;
        double maxX = double.MinValue;
        double minY = double.MaxValue;
        double maxY = double.MinValue;
        double influenceMinX = double.MaxValue;
        double influenceMaxX = double.MinValue;
        double influenceMinY = double.MaxValue;
        double influenceMaxY = double.MinValue;

        int effectiveCornerFanSegments = pad.CornerFanSegments > 0
            ? pad.CornerFanSegments
            : (pad.StitchApronDistance > tolerance * 4.0 ? 6 : 0);
        double[] targetBoundaryXy = padLoop.XyVertices;
        double[] targetShoulderXy;
        bool hasShoulderLoop;
        if (effectiveCornerFanSegments > 0)
        {
            hasShoulderLoop = TryBuildExpandedOffsetPolygon(
                padLoop.XyVertices,
                padLoop.VertexCount,
                shoulderDistances,
                effectiveCornerFanSegments,
                TerrainFaceGrid,
                pad,
                out targetBoundaryXy,
                out targetShoulderXy,
                out _);
        }
        else
        {
            hasShoulderLoop = TryBuildOffsetPolygon(
                padLoop.XyVertices,
                padLoop.VertexCount,
                shoulderDistances,
                out targetShoulderXy,
                out _);
        }

        if (hasShoulderLoop && !ReferenceEquals(targetBoundaryXy, padLoop.XyVertices))
            padLoop = new ConstraintLoop(targetBoundaryXy, targetBoundaryXy.Length / 2);

        var shoulderXy = new double[padLoop.VertexCount * 2];
        var shoulderZ = new double[padLoop.VertexCount];

        for (int i = 0; i < padLoop.VertexCount; i++)
        {
            double boundaryX = padLoop.XyVertices[i * 2];
            double boundaryY = padLoop.XyVertices[i * 2 + 1];
            double resolvedShoulderX = boundaryX;
            double resolvedShoulderY = boundaryY;

            if (hasShoulderLoop)
            {
                ResolvePadShoulderEndpoint(
                    barriers,
                    hasBoundaryLoop,
                    boundaryLoop,
                    boundaryVertexCount,
                    tolerance,
                    boundaryX,
                    boundaryY,
                    targetShoulderXy[i * 2],
                    targetShoulderXy[i * 2 + 1],
                    out resolvedShoulderX,
                    out resolvedShoulderY);

            }

            shoulderXy[i * 2] = resolvedShoulderX;
            shoulderXy[i * 2 + 1] = resolvedShoulderY;
            double boundaryZ = pad.EvaluateZ(boundaryX, boundaryY);
            double actualReach = Math.Sqrt(((resolvedShoulderX - boundaryX) * (resolvedShoulderX - boundaryX)) + ((resolvedShoulderY - boundaryY) * (resolvedShoulderY - boundaryY)));
            if (actualReach <= tolerance)
            {
                shoulderZ[i] = boundaryZ;
            }
            else
            {
                double terrainZ = TerrainFaceGrid.InterpolateZ(resolvedShoulderX, resolvedShoulderY);
                if (!keepShoulderOnBatterPlane)
                {
                    shoulderZ[i] = terrainZ;
                }
                else
                {
                    double branchSign = Math.Sign(terrainZ - boundaryZ);
                    if (Math.Abs(branchSign) <= 1e-12)
                    {
                        shoulderZ[i] = boundaryZ;
                    }
                    else
                    {
                        double slopeRatio = Math.Tan(pad.SlopeAngleDeg * Math.PI / 180.0);
                        double batterReach = DistToPolygon(
                            resolvedShoulderX,
                            resolvedShoulderY,
                            padLoop.XyVertices,
                            padLoop.VertexCount);
                        if (batterReach <= tolerance)
                            batterReach = actualReach;
                        shoulderZ[i] = boundaryZ + (branchSign * slopeRatio * batterReach);
                    }
                }
            }

            if (boundaryX < minX) minX = boundaryX;
            if (boundaryX > maxX) maxX = boundaryX;
            if (boundaryY < minY) minY = boundaryY;
            if (boundaryY > maxY) maxY = boundaryY;

            influenceMinX = Math.Min(influenceMinX, Math.Min(boundaryX, resolvedShoulderX));
            influenceMaxX = Math.Max(influenceMaxX, Math.Max(boundaryX, resolvedShoulderX));
            influenceMinY = Math.Min(influenceMinY, Math.Min(boundaryY, resolvedShoulderY));
            influenceMaxY = Math.Max(influenceMaxY, Math.Max(boundaryY, resolvedShoulderY));
        }

        return new PreparedPadSections(
            pad,
            padLoop.XyVertices,
            padLoop.VertexCount,
            shoulderXy,
            shoulderZ,
            minX,
            maxX,
            minY,
            maxY,
            influenceMinX,
            influenceMaxX,
            influenceMinY,
            influenceMaxY);
    }

    private static bool TryFindClosestLoopLocation(
        double[] xyVertices,
        int vertexCount,
        double px,
        double py,
        out ClosestLoopLocation closest)
    {
        closest = default;
        double closestDistSq = double.MaxValue;
        if (vertexCount < 2)
            return false;

        for (int i = 0; i < vertexCount; i++)
        {
            int next = (i + 1) % vertexCount;
            double ax = xyVertices[i * 2];
            double ay = xyVertices[i * 2 + 1];
            double bx = xyVertices[next * 2];
            double by = xyVertices[next * 2 + 1];
            double dx = bx - ax;
            double dy = by - ay;
            double lenSq = (dx * dx) + (dy * dy);
            if (lenSq < 1e-20)
                continue;

            double t = Math.Clamp((((px - ax) * dx) + ((py - ay) * dy)) / lenSq, 0.0, 1.0);
            double projectedX = ax + (dx * t);
            double projectedY = ay + (dy * t);
            double offsetX = px - projectedX;
            double offsetY = py - projectedY;
            double distSq = (offsetX * offsetX) + (offsetY * offsetY);
            if (distSq >= closestDistSq)
                continue;

            closestDistSq = distSq;
            closest = new ClosestLoopLocation(i, t, Math.Sqrt(distSq));
        }

        return closestDistSq < double.MaxValue;
    }

    private static bool TryInterpolatePadSection(
        PreparedPadSections prepared,
        ClosestLoopLocation closest,
        out double boundaryX,
        out double boundaryY,
        out double boundaryZ,
        out double shoulderX,
        out double shoulderY,
        out double shoulderZ)
    {
        boundaryX = 0.0;
        boundaryY = 0.0;
        boundaryZ = 0.0;
        shoulderX = 0.0;
        shoulderY = 0.0;
        shoulderZ = 0.0;

        int segmentIndex = closest.SegmentIndex;
        if (segmentIndex < 0 || segmentIndex >= prepared.BoundaryVertexCount)
            return false;

        int next = (segmentIndex + 1) % prepared.BoundaryVertexCount;
        boundaryX = LerpValue(prepared.BoundaryLoopXy[segmentIndex * 2], prepared.BoundaryLoopXy[next * 2], closest.SegmentT);
        boundaryY = LerpValue(prepared.BoundaryLoopXy[(segmentIndex * 2) + 1], prepared.BoundaryLoopXy[(next * 2) + 1], closest.SegmentT);
        boundaryZ = prepared.Pad.EvaluateZ(boundaryX, boundaryY);
        shoulderX = LerpValue(prepared.ShoulderXy[segmentIndex * 2], prepared.ShoulderXy[next * 2], closest.SegmentT);
        shoulderY = LerpValue(prepared.ShoulderXy[(segmentIndex * 2) + 1], prepared.ShoulderXy[(next * 2) + 1], closest.SegmentT);
        shoulderZ = LerpValue(prepared.ShoulderZ[segmentIndex], prepared.ShoulderZ[next], closest.SegmentT);
        return double.IsFinite(shoulderZ);
    }

    private static void ResolvePadShoulderEndpoint(
        PreparedBarriers barriers,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double tolerance,
        double startX,
        double startY,
        double targetX,
        double targetY,
        out double resolvedX,
        out double resolvedY)
    {
        resolvedX = targetX;
        resolvedY = targetY;
        var barrierScratch = new SpatialHashGrid2D.QueryScratch(Math.Max(barriers.Segments.Length, 1));
        var barrierCandidates = new List<int>(8);

        if (barriers.Segments.Length > 0)
        {
            GradingBarriers.TryClipSegment(
                barriers,
                startX,
                startY,
                resolvedX,
                resolvedY,
                barrierScratch,
                barrierCandidates,
                out resolvedX,
                out resolvedY);
        }

        if (!hasBoundaryLoop)
            return;

        List<ClippedSegment> pieces = BoundaryClipper.ClipSegmentToBoundary(
            startX,
            startY,
            0.0,
            resolvedX,
            resolvedY,
            0.0,
            hasBoundaryLoop,
            boundaryLoop,
            boundaryVertexCount,
            tolerance);

        double furthestEndT = double.MinValue;
        bool foundPiece = false;
        foreach (ClippedSegment piece in pieces)
        {
            if (piece.StartT > 1e-9 || piece.EndT <= furthestEndT)
                continue;

            resolvedX = piece.EndX;
            resolvedY = piece.EndY;
            furthestEndT = piece.EndT;
            foundPiece = true;
        }

        if (foundPiece)
            return;

        resolvedX = startX;
        resolvedY = startY;
    }

    private static double LerpValue(double start, double end, double t)
    {
        return start + ((end - start) * t);
    }

    private static double ClampBetween(double value, double a, double b)
    {
        double min = Math.Min(a, b);
        double max = Math.Max(a, b);
        return Math.Max(min, Math.Min(max, value));
    }

    private static void ApplyGradingToVertices(
        double[] gradedVertices,
        double[] originalVertices,
        int vertexCount,
        PadBoundary[] pads,
        PreparedBarriers barriers)
    {
        if (pads.Length == 0)
            return;

        double globalMinX = double.MaxValue;
        double globalMaxX = double.MinValue;
        double globalMinY = double.MaxValue;
        double globalMaxY = double.MinValue;
        var padBounds = new PadInfluenceBounds[pads.Length];
        var interiorBounds = new Bounds2D[pads.Length];
        var influenceBounds = new Bounds2D[pads.Length];

        for (int p = 0; p < pads.Length; p++)
        {
            var pad = pads[p];
            double minX = double.MaxValue;
            double maxX = double.MinValue;
            double minY = double.MaxValue;
            double maxY = double.MinValue;

            for (int i = 0; i < pad.VertexCount; i++)
            {
                double vx = pad.XyVertices[i * 2];
                double vy = pad.XyVertices[i * 2 + 1];
                if (vx < minX) minX = vx;
                if (vx > maxX) maxX = vx;
                if (vy < minY) minY = vy;
                if (vy > maxY) maxY = vy;
            }

            double transitionDistance = ComputePadTransitionDistance(originalVertices, vertexCount, pad);
            double influenceMinX = minX - transitionDistance;
            double influenceMaxX = maxX + transitionDistance;
            double influenceMinY = minY - transitionDistance;
            double influenceMaxY = maxY + transitionDistance;
            padBounds[p] = new PadInfluenceBounds(
                pad,
                minX,
                maxX,
                minY,
                maxY,
                influenceMinX,
                influenceMaxX,
                influenceMinY,
                influenceMaxY);
            interiorBounds[p] = new Bounds2D(minX, maxX, minY, maxY);
            influenceBounds[p] = new Bounds2D(influenceMinX, influenceMaxX, influenceMinY, influenceMaxY);

            if (influenceMinX < globalMinX) globalMinX = influenceMinX;
            if (influenceMaxX > globalMaxX) globalMaxX = influenceMaxX;
            if (influenceMinY < globalMinY) globalMinY = influenceMinY;
            if (influenceMaxY > globalMaxY) globalMaxY = influenceMaxY;
        }

        var interiorIndex = SpatialHashGrid2D.Build(interiorBounds);
        var influenceIndex = SpatialHashGrid2D.Build(influenceBounds);

        int barrierCount = Math.Max(barriers.Segments.Length, 1);
        System.Threading.Tasks.Parallel.For(
            0,
            vertexCount,
            () => (
                InteriorScratch: new SpatialHashGrid2D.QueryScratch(pads.Length),
                InfluenceScratch: new SpatialHashGrid2D.QueryScratch(pads.Length),
                BarrierScratch: new SpatialHashGrid2D.QueryScratch(barrierCount),
                InteriorCandidates: new List<int>(8),
                InfluenceCandidates: new List<int>(8),
                BarrierCandidates: new List<int>(8)),
            (i, _, state) =>
        {
            double px = gradedVertices[i * 3];
            double py = gradedVertices[i * 3 + 1];

            if (px < globalMinX || px > globalMaxX || py < globalMinY || py > globalMaxY)
                return state;

            interiorIndex.GatherCandidates(
                Bounds2D.FromPoint(px, py),
                state.InteriorCandidates,
                state.InteriorScratch);

            int insidePadIdx = -1;
            foreach (int p in state.InteriorCandidates)
            {
                var bounds = padBounds[p];
                if (px < bounds.MinX || px > bounds.MaxX || py < bounds.MinY || py > bounds.MaxY)
                    continue;

                if (PointInPolygon(px, py, bounds.Pad.XyVertices, bounds.Pad.VertexCount))
                    insidePadIdx = Math.Max(insidePadIdx, p);
            }

            if (insidePadIdx >= 0)
            {
                gradedVertices[i * 3 + 2] = pads[insidePadIdx].EvaluateZ(px, py);
                return state;
            }

            influenceIndex.GatherCandidates(
                Bounds2D.FromPoint(px, py),
                state.InfluenceCandidates,
                state.InfluenceScratch);

            double nearestDist = double.MaxValue;
            int nearestPadIdx = -1;
            double nearestBoundaryZ = 0.0;
            double nearestBoundaryPx = px;
            double nearestBoundaryPy = py;
            foreach (int p in state.InfluenceCandidates)
            {
                var bounds = padBounds[p];
                if (px < bounds.InfluenceMinX || px > bounds.InfluenceMaxX || py < bounds.InfluenceMinY || py > bounds.InfluenceMaxY)
                    continue;

                double dist = DistToBoundaryWithZ(px, py, bounds.Pad.BoundaryVertices, bounds.Pad.VertexCount,
                    out double boundaryZ, out double bpx, out double bpy);
                if (dist < nearestDist - 1e-12 ||
                    (Math.Abs(dist - nearestDist) <= 1e-12 && (nearestPadIdx < 0 || p < nearestPadIdx)))
                {
                    nearestDist = dist;
                    nearestPadIdx = p;
                    nearestBoundaryZ = boundaryZ;
                    nearestBoundaryPx = bpx;
                    nearestBoundaryPy = bpy;
                }
            }

            if (nearestPadIdx < 0)
                return state;

            // Skip grading if a barrier lies between this vertex and its nearest pad boundary point.
            if (barriers.Segments.Length > 0 &&
                GradingBarriers.IsCrossedByBarrier(
                    barriers, px, py, nearestBoundaryPx, nearestBoundaryPy,
                    state.BarrierScratch, state.BarrierCandidates))
                return state;

            var pad = pads[nearestPadIdx];
            double slopeRatio = Math.Tan(pad.SlopeAngleDeg * Math.PI / 180.0);
            double dz = originalVertices[i * 3 + 2] - nearestBoundaryZ;
            double absDz = Math.Abs(dz);
            if (absDz <= GradingTolerances.VertexAdjustmentZTolerance(GradingTolerances.DefaultModelTolerance))
                return state;

            double neededDist = slopeRatio > 1e-12 ? absDz / slopeRatio : double.MaxValue;
            if (pad.MaxDistance > 0)
                neededDist = Math.Min(neededDist, pad.MaxDistance);

            if (nearestDist >= neededDist)
                return state;

            double rise = nearestDist * slopeRatio;
            if (rise < absDz)
            {
                double candidateZ = nearestBoundaryZ + Math.Sign(dz) * rise;
                if (Math.Abs(candidateZ - originalVertices[i * 3 + 2]) > GradingTolerances.VertexAdjustmentZTolerance(GradingTolerances.DefaultModelTolerance))
                    gradedVertices[i * 3 + 2] = candidateZ;
            }
            return state;
        }, _ => { });
    }

    private static double ComputePadTransitionDistance(double[] vertices, int vertexCount, PadBoundary pad)
    {
        double slopeRatio = Math.Tan(pad.SlopeAngleDeg * Math.PI / 180.0);
        double maxZDiff = 0;
        for (int i = 0; i < vertexCount; i++)
        {
            double dz = Math.Abs(vertices[i * 3 + 2] - pad.EvaluateZ(vertices[i * 3], vertices[i * 3 + 1]));
            if (dz > maxZDiff)
                maxZDiff = dz;
        }

        double transitionDistance = slopeRatio > 1e-12 ? maxZDiff / slopeRatio : 100.0;
        if (pad.MaxDistance > 0)
            transitionDistance = Math.Min(transitionDistance, pad.MaxDistance);
        return transitionDistance;
    }

    private static double[]? AddPadShoulderConstraint(
        double[] padLoopXy,
        int padLoopVertexCount,
        double[] shoulderDistances,
        List<double> xyList,
        List<double> zList,
        SpatialVertexHash vertHash,
        TerrainFaceGrid TerrainFaceGrid,
        List<(int a, int b)> segList,
        double dedupTol,
        double[]? boundaryLoop,
        int boundaryVertexCount,
        PreparedBarriers barriers,
        SpatialHashGrid2D.QueryScratch barrierScratch,
        List<int> barrierCandidates)
    {
        if (!TryBuildShoulderLoop(
            padLoopXy,
            padLoopVertexCount,
            shoulderDistances,
            boundaryLoop,
            boundaryVertexCount,
            dedupTol,
            out var shoulderXy,
            out _))
        {
            return null;
        }

        int shoulderVertexCount = shoulderXy.Length / 2;
        var shoulderIndices = new int[shoulderVertexCount];
        for (int i = 0; i < shoulderVertexCount; i++)
        {
            double px = shoulderXy[i * 2];
            double py = shoulderXy[i * 2 + 1];

            int near = vertHash.FindNearest(xyList, px, py, dedupTol);
            if (near >= 0)
            {
                shoulderIndices[i] = near;
            }
            else
            {
                shoulderIndices[i] = zList.Count;
                xyList.Add(px);
                xyList.Add(py);
                zList.Add(TerrainFaceGrid.InterpolateZ(px, py));
                vertHash.Insert(shoulderIndices[i], px, py);
            }
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

        // Add shoulder ring segments, clipping each at the first barrier hit.
        // When clipped, the arc terminates at the barrier intersection, producing
        // open support runs instead of a single closed ring.
        for (int i = 0; i < shoulderVertexCount; i++)
        {
            int next = (i + 1) % shoulderVertexCount;
            double ax = shoulderXy[i * 2],    ay = shoulderXy[i * 2 + 1];
            double bx = shoulderXy[next * 2], by = shoulderXy[next * 2 + 1];

            bool clipped = GradingBarriers.TryClipSegment(
                barriers, ax, ay, bx, by,
                barrierScratch, barrierCandidates,
                out double cbx, out double cby);

            int startIdx = shoulderIndices[i];
            int endIdx = clipped ? AddVertex(cbx, cby) : shoulderIndices[next];

            if (startIdx != endIdx)
                segList.Add((startIdx, endIdx));
        }

        return shoulderXy;
    }

    /// <summary>
    /// For each vertex of the pad boundary, interpolates the terrain Z and computes the
    /// horizontal distance the slope transition needs to travel to reach the terrain surface.
    /// Capped at MaxDistance when set.
    /// </summary>
    private static double[] ComputePadBoundaryDistances(
        double[] padLoopXy,
        int padLoopVertexCount,
        TerrainFaceGrid TerrainFaceGrid,
        PadBoundary pad)
    {
        double slopeRatio = Math.Tan(pad.SlopeAngleDeg * Math.PI / 180.0);
        var distances = new double[padLoopVertexCount];
        double signedArea = ClipperGeometry.SignedArea(padLoopXy);
        bool hasOrientation = Math.Abs(signedArea) > 1e-12;
        bool ccw = signedArea > 0.0;
        for (int i = 0; i < padLoopVertexCount; i++)
        {
            double bx = padLoopXy[i * 2];
            double by = padLoopXy[i * 2 + 1];
            double terrainZ = TerrainFaceGrid.InterpolateZ(bx, by);
            double padZ = pad.EvaluateZ(bx, by);
            double dz = Math.Abs(terrainZ - padZ);
            double d = slopeRatio > 1e-12 ? dz / slopeRatio : 100.0;
            if (d > 1e-9 && hasOrientation &&
                TryComputePadOutwardEdgeNormals(
                    padLoopXy,
                    padLoopVertexCount,
                    i,
                    ccw,
                    out double previousNormalX,
                    out double previousNormalY,
                    out double nextNormalX,
                    out double nextNormalY))
            {
                double branchSign = Math.Sign(terrainZ - padZ);
                double previousReach = ComputePadDaylightReach(
                    TerrainFaceGrid,
                    bx,
                    by,
                    padZ,
                    previousNormalX,
                    previousNormalY,
                    slopeRatio,
                    branchSign,
                    d,
                    pad.MaxDistance);
                double nextReach = ComputePadDaylightReach(
                    TerrainFaceGrid,
                    bx,
                    by,
                    padZ,
                    nextNormalX,
                    nextNormalY,
                    slopeRatio,
                    branchSign,
                    d,
                    pad.MaxDistance);

                d = Math.Max(previousReach, nextReach);
            }

            if (pad.MaxDistance > 0)
                d = Math.Min(d, pad.MaxDistance);
            distances[i] = d;
        }

        return distances;
    }

    private static bool TryComputePadOutwardEdgeNormals(
        double[] polygonXy,
        int vertexCount,
        int index,
        bool ccw,
        out double previousNormalX,
        out double previousNormalY,
        out double nextNormalX,
        out double nextNormalY)
    {
        previousNormalX = 0.0;
        previousNormalY = 0.0;
        nextNormalX = 0.0;
        nextNormalY = 0.0;
        if (vertexCount < 3)
            return false;

        int previous = (index + vertexCount - 1) % vertexCount;
        int next = (index + 1) % vertexCount;

        if (!TryComputePadOutwardEdgeNormal(
                polygonXy[previous * 2],
                polygonXy[previous * 2 + 1],
                polygonXy[index * 2],
                polygonXy[index * 2 + 1],
                ccw,
                out previousNormalX,
                out previousNormalY) ||
            !TryComputePadOutwardEdgeNormal(
                polygonXy[index * 2],
                polygonXy[index * 2 + 1],
                polygonXy[next * 2],
                polygonXy[next * 2 + 1],
                ccw,
                out nextNormalX,
                out nextNormalY))
        {
            return false;
        }

        return true;
    }

    private static bool TryComputePadOutwardEdgeNormal(
        double ax,
        double ay,
        double bx,
        double by,
        bool ccw,
        out double normalX,
        out double normalY)
    {
        normalX = 0.0;
        normalY = 0.0;
        double dx = bx - ax;
        double dy = by - ay;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length <= 1e-12)
            return false;

        normalX = ccw ? dy / length : -dy / length;
        normalY = ccw ? -dx / length : dx / length;
        return true;
    }

    private static double ComputePadDaylightReach(
        TerrainFaceGrid TerrainFaceGrid,
        double boundaryX,
        double boundaryY,
        double boundaryZ,
        double dirX,
        double dirY,
        double slopeRatio,
        double branchSign,
        double fallbackReach,
        double maxDistance)
    {
        if (slopeRatio <= 1e-12 ||
            Math.Abs(branchSign) <= 1e-12 ||
            !double.IsFinite(fallbackReach) ||
            fallbackReach <= 1e-9)
        {
            return Math.Max(0.0, fallbackReach);
        }

        double searchDistance = maxDistance > 0.0
            ? maxDistance
            : Math.Max(Math.Max(fallbackReach * 4.0, 1.0), TerrainFaceGrid.BoundsDiagonal);
        if (searchDistance <= 1e-9)
            return Math.Max(0.0, fallbackReach);

        if (TryFindPadDaylightReachByTriangleIntervals(
                TerrainFaceGrid,
                boundaryX,
                boundaryY,
                boundaryZ,
                dirX,
                dirY,
                slopeRatio,
                branchSign,
                searchDistance,
                out double daylightReach,
                out double bestApproachReach))
        {
            return daylightReach;
        }

        if (TryFindPadDaylightReach(
                TerrainFaceGrid,
                boundaryX,
                boundaryY,
                boundaryZ,
                dirX,
                dirY,
                slopeRatio,
                branchSign,
                searchDistance,
                out daylightReach,
                out bestApproachReach))
        {
            return daylightReach;
        }

        return Math.Min(fallbackReach, searchDistance);
    }

    private static bool TryFindPadDaylightReachByTriangleIntervals(
        TerrainFaceGrid TerrainFaceGrid,
        double edgeX,
        double edgeY,
        double edgeZ,
        double dirX,
        double dirY,
        double slopeRatio,
        double branchSign,
        double maxReach,
        out double daylightReach,
        out double bestApproachReach)
    {
        daylightReach = 0.0;
        bestApproachReach = 0.0;
        if (maxReach <= 1e-9)
            return false;

        return TerrainFaceGrid.TryFindRayDaylightReach(
            edgeX,
            edgeY,
            edgeZ,
            dirX,
            dirY,
            slopeRatio,
            branchSign,
            maxReach,
            out daylightReach,
            out bestApproachReach);
    }

    private static bool TryFindPadDaylightReach(
        TerrainFaceGrid TerrainFaceGrid,
        double edgeX,
        double edgeY,
        double edgeZ,
        double dirX,
        double dirY,
        double slopeRatio,
        double branchSign,
        double maxReach,
        out double daylightReach,
        out double bestApproachReach)
    {
        daylightReach = 0.0;
        bestApproachReach = 0.0;
        if (maxReach <= 1e-9)
            return false;

        const double diffTolerance = 1e-4;
        double step = Math.Clamp(maxReach / 48.0, 0.1, 5.0);
        int sampleCount = Math.Max(1, (int)Math.Ceiling(maxReach / step));
        double startDiff = EvaluatePadSectionDifference(TerrainFaceGrid, edgeX, edgeY, edgeZ, dirX, dirY, slopeRatio, branchSign, 0.0);
        double bestAbsDiff = Math.Abs(startDiff);
        double prevReach;
        double prevDiff;
        int sampleIndex;

        if (Math.Abs(startDiff) > diffTolerance)
        {
            prevReach = 0.0;
            prevDiff = startDiff;
            sampleIndex = 1;
        }
        else
        {
            double firstReach = Math.Min(maxReach, step);
            double firstDiff = EvaluatePadSectionDifference(TerrainFaceGrid, edgeX, edgeY, edgeZ, dirX, dirY, slopeRatio, branchSign, firstReach);
            if (Math.Abs(firstDiff) <= diffTolerance ||
                (branchSign > 0.0 && firstDiff < diffTolerance) ||
                (branchSign < 0.0 && firstDiff > -diffTolerance))
            {
                daylightReach = 0.0;
                return true;
            }

            prevReach = firstReach;
            prevDiff = firstDiff;
            sampleIndex = 2;
        }

        for (; sampleIndex <= sampleCount; sampleIndex++)
        {
            double currentReach = sampleIndex == sampleCount
                ? maxReach
                : Math.Min(maxReach, sampleIndex * step);
            double currentDiff = EvaluatePadSectionDifference(TerrainFaceGrid, edgeX, edgeY, edgeZ, dirX, dirY, slopeRatio, branchSign, currentReach);
            double currentAbsDiff = Math.Abs(currentDiff);
            if (currentAbsDiff < bestAbsDiff)
            {
                bestAbsDiff = currentAbsDiff;
                bestApproachReach = currentReach;
            }

            if (Math.Abs(currentDiff) <= diffTolerance)
            {
                daylightReach = currentReach;
                return true;
            }

            bool crossed = branchSign > 0.0
                ? prevDiff > diffTolerance && currentDiff < diffTolerance
                : prevDiff < -diffTolerance && currentDiff > -diffTolerance;
            if (crossed)
            {
                daylightReach = RefinePadDaylightReach(
                    TerrainFaceGrid,
                    edgeX,
                    edgeY,
                    edgeZ,
                    dirX,
                    dirY,
                    slopeRatio,
                    branchSign,
                    prevReach,
                    currentReach);
                return true;
            }

            prevReach = currentReach;
            prevDiff = currentDiff;
        }

        return false;
    }

    private static double RefinePadDaylightReach(
        TerrainFaceGrid TerrainFaceGrid,
        double edgeX,
        double edgeY,
        double edgeZ,
        double dirX,
        double dirY,
        double slopeRatio,
        double branchSign,
        double lowReach,
        double highReach)
    {
        const double diffTolerance = 1e-5;
        double low = lowReach;
        double high = highReach;

        for (int i = 0; i < 24; i++)
        {
            double mid = (low + high) * 0.5;
            double diff = EvaluatePadSectionDifference(TerrainFaceGrid, edgeX, edgeY, edgeZ, dirX, dirY, slopeRatio, branchSign, mid);
            if (Math.Abs(diff) <= diffTolerance || (high - low) <= 1e-4)
                return mid;

            if (branchSign > 0.0)
            {
                if (diff > 0.0) low = mid;
                else high = mid;
            }
            else
            {
                if (diff < 0.0) low = mid;
                else high = mid;
            }
        }

        return (low + high) * 0.5;
    }

    private static double EvaluatePadSectionDifference(
        TerrainFaceGrid TerrainFaceGrid,
        double edgeX,
        double edgeY,
        double edgeZ,
        double dirX,
        double dirY,
        double slopeRatio,
        double branchSign,
        double reach)
    {
        double terrainZ = TerrainFaceGrid.InterpolateZ(edgeX + (dirX * reach), edgeY + (dirY * reach));
        double gradeZ = edgeZ + (branchSign * slopeRatio * reach);
        return terrainZ - gradeZ;
    }

    private static bool TryBuildShoulderLoop(
        double[] padLoopXy,
        int padLoopVertexCount,
        double[] shoulderDistances,
        double[]? boundaryLoop,
        int boundaryVertexCount,
        double tolerance,
        out double[] shoulderXy,
        out string? skipReason)
    {
        shoulderXy = Array.Empty<double>();
        skipReason = null;
        double maxDist = 0;
        foreach (double d in shoulderDistances) if (d > maxDist) maxDist = d;
        if (maxDist <= tolerance)
            return false;

        if (!TryBuildShoulderLoopWithClipper(
            padLoopXy,
            padLoopVertexCount,
            shoulderDistances,
            boundaryLoop,
            boundaryVertexCount,
            tolerance,
            out shoulderXy,
            out string? offsetFailure))
        {
            skipReason = offsetFailure ?? "Grade Pad shoulder ring was skipped because the daylight offset could not be constructed cleanly.";
            return false;
        }

        return true;
    }

    private static bool TryBuildShoulderLoopFromSections(
        double[] padLoopXy,
        int padLoopVertexCount,
        double[] shoulderXy,
        double[]? boundaryLoop,
        int boundaryVertexCount,
        double tolerance,
        out double[] shoulderLoopXy,
        out string? failureReason,
        int cornerFanSegments = 0)
    {
        shoulderLoopXy = Array.Empty<double>();
        failureReason = null;

        if (!TryBuildPadTransitionQuadsFromSections(
                padLoopXy,
                padLoopVertexCount,
                shoulderXy,
                tolerance,
                out List<double[]> stripLoops))
        {
            failureReason = "Grade Pad shoulder ring was skipped because the resolved daylight strips degenerated.";
            return false;
        }

        if (!ClipperGeometry.TryUnionClosedLoops(stripLoops, tolerance, out List<double[]> unionLoops))
        {
            failureReason = "Grade Pad shoulder ring was skipped because the resolved daylight region could not be unioned cleanly.";
            return false;
        }

        if (boundaryLoop != null)
        {
            if (!ClipperGeometry.TryIntersectClosedLoops(unionLoops, boundaryLoop, tolerance, out unionLoops))
            {
                failureReason = "Grade Pad shoulder ring was skipped because the resolved daylight region exited the terrain boundary.";
                return false;
            }
        }

        if (!ClipperGeometry.TryPickLargestLoop(unionLoops, out shoulderLoopXy))
        {
            failureReason = "Grade Pad shoulder ring was skipped because the resolved daylight region produced no valid outer loop.";
            return false;
        }

        if (!ClipperGeometry.TrySimplifyClosedLoop(shoulderLoopXy, tolerance, out shoulderLoopXy) ||
            shoulderLoopXy.Length / 2 < 3)
        {
            shoulderLoopXy = Array.Empty<double>();
            failureReason = "Grade Pad shoulder ring was skipped because the resolved daylight region degenerated after simplification.";
            return false;
        }

        return true;
    }

    private static bool TryBuildOrderedShoulderLoopFromSections(
        double[] padLoopXy,
        int padLoopVertexCount,
        double[] shoulderXy,
        double tolerance,
        out double[] shoulderLoopXy,
        out string? failureReason)
    {
        shoulderLoopXy = Array.Empty<double>();
        failureReason = null;
        if (padLoopVertexCount < 3 || shoulderXy.Length < padLoopVertexCount * 2)
        {
            failureReason = "Grade Pad shoulder ring was skipped because the resolved daylight sections were invalid.";
            return false;
        }

        var points = new List<double>(padLoopVertexCount * 2);
        for (int i = 0; i < padLoopVertexCount; i++)
        {
            double bx = padLoopXy[i * 2];
            double by = padLoopXy[i * 2 + 1];
            double sx = shoulderXy[i * 2];
            double sy = shoulderXy[i * 2 + 1];
            if (DistanceSquaredXY(bx, by, sx, sy) <= tolerance * tolerance)
                continue;

            AddLoopPoint(points, sx, sy, tolerance);
        }

        if (points.Count >= 4 &&
            DistanceSquaredXY(points[0], points[1], points[^2], points[^1]) <= tolerance * tolerance)
        {
            points.RemoveRange(points.Count - 2, 2);
        }

        if (points.Count / 2 < 3)
        {
            failureReason = "Grade Pad shoulder ring was skipped because the resolved daylight sections collapsed.";
            return false;
        }

        shoulderLoopXy = points.ToArray();
        return true;
    }

    private static void AppendCornerFanStrips(
        double[] padLoopXy,
        int padLoopVertexCount,
        double[] shoulderXy,
        double tolerance,
        int cornerFanSegments,
        List<double[]> stripLoops)
    {
        if (padLoopVertexCount < 3) return;
        double signedArea = ClipperGeometry.SignedArea(padLoopXy);
        if (Math.Abs(signedArea) < 1e-12) return;
        bool ccw = signedArea > 0.0;
        int fanCount = cornerFanSegments + 1;

        for (int i = 0; i < padLoopVertexCount; i++)
        {
            int prev = (i + padLoopVertexCount - 1) % padLoopVertexCount;
            int next = (i + 1) % padLoopVertexCount;

            double x0 = padLoopXy[prev * 2];
            double y0 = padLoopXy[prev * 2 + 1];
            double x1 = padLoopXy[i * 2];
            double y1 = padLoopXy[i * 2 + 1];
            double x2 = padLoopXy[next * 2];
            double y2 = padLoopXy[next * 2 + 1];

            double dx0 = x1 - x0;
            double dy0 = y1 - y0;
            double dx1 = x2 - x1;
            double dy1 = y2 - y1;
            double len0 = Math.Sqrt(dx0 * dx0 + dy0 * dy0);
            double len1 = Math.Sqrt(dx1 * dx1 + dy1 * dy1);
            if (len0 < 1e-12 || len1 < 1e-12) continue;

            double n0x = ccw ? dy0 / len0 : -dy0 / len0;
            double n0y = ccw ? -dx0 / len0 : dx0 / len0;
            double n1x = ccw ? dy1 / len1 : -dy1 / len1;
            double n1y = ccw ? -dx1 / len1 : dx1 / len1;

            double turnCross = dx0 * dy1 - dy0 * dx1;
            bool isReentrant = ccw ? turnCross < -1e-12 : turnCross > 1e-12;
            if (isReentrant) continue;

            double prevAngle = Math.Atan2(n0y, n0x);
            double nextAngle = Math.Atan2(n1y, n1x);
            double sweep = ComputeOutwardAngleSweep(prevAngle, nextAngle, ccw);
            if (Math.Abs(sweep) <= 10.0 * Math.PI / 180.0) continue;

            double svx = shoulderXy[i * 2] - x1;
            double svy = shoulderXy[i * 2 + 1] - y1;
            double d = (svx * n0x + svy * n0y);
            if (d <= tolerance) continue;

            for (int f = 0; f < fanCount - 1; f++)
            {
                double theta0 = prevAngle + sweep * f / (fanCount - 1);
                double theta1 = prevAngle + sweep * (f + 1) / (fanCount - 1);
                double ax = x1 + Math.Cos(theta0) * d;
                double ay = y1 + Math.Sin(theta0) * d;
                double bx = x1 + Math.Cos(theta1) * d;
                double by = y1 + Math.Sin(theta1) * d;
                double[] tri = [x1, y1, ax, ay, bx, by];
                if (Math.Abs(ClipperGeometry.SignedArea(tri)) > tolerance * tolerance)
                    stripLoops.Add(tri);
            }
        }
    }

    private static bool TryBuildShoulderLoopWithClipper(
        double[] padLoopXy,
        int padLoopVertexCount,
        double[] shoulderDistances,
        double[]? boundaryLoop,
        int boundaryVertexCount,
        double tolerance,
        out double[] shoulderXy,
        out string? failureReason)
    {
        shoulderXy = Array.Empty<double>();
        failureReason = null;

        if (!TryBuildPadTransitionQuads(padLoopXy, padLoopVertexCount, shoulderDistances, tolerance, out List<double[]> stripLoops))
        {
            failureReason = "Grade Pad shoulder ring was skipped because the daylight strips degenerated.";
            return false;
        }

        if (!ClipperGeometry.TryUnionClosedLoops(stripLoops, tolerance, out List<double[]> unionLoops))
        {
            failureReason = "Grade Pad shoulder ring was skipped because the daylight region could not be unioned cleanly.";
            return false;
        }

        if (boundaryLoop != null)
        {
            if (!ClipperGeometry.TryIntersectClosedLoops(unionLoops, boundaryLoop, tolerance, out unionLoops))
            {
                failureReason = "Grade Pad shoulder ring was skipped because the daylight region exited the terrain boundary.";
                return false;
            }
        }

        if (!ClipperGeometry.TryPickLargestLoop(unionLoops, out shoulderXy))
        {
            failureReason = "Grade Pad shoulder ring was skipped because Clipper produced no valid outer loop.";
            return false;
        }

        if (!ClipperGeometry.TrySimplifyClosedLoop(shoulderXy, tolerance, out shoulderXy) ||
            shoulderXy.Length / 2 < 3)
        {
            shoulderXy = Array.Empty<double>();
            failureReason = "Grade Pad shoulder ring was skipped because the daylight region degenerated after simplification.";
            return false;
        }

        double maxDistance = 0.0;
        foreach (double distance in shoulderDistances)
            maxDistance = Math.Max(maxDistance, distance);

        ConstraintLoop resampledShoulder = BuildClosedConstraintLoop(
            shoulderXy,
            shoulderXy.Length / 2,
            ComputePadConstraintSegmentLength(maxDistance),
            tolerance);
        shoulderXy = resampledShoulder.XyVertices;

        return true;
    }

    private static bool TryBuildPadTransitionQuads(
        double[] padLoopXy,
        int padLoopVertexCount,
        double[] shoulderDistances,
        double tolerance,
        out List<double[]> stripLoops)
    {
        stripLoops = new List<double[]>(padLoopVertexCount);
        if (padLoopVertexCount < 3 || shoulderDistances.Length < padLoopVertexCount)
            return false;

        bool ccw = ClipperGeometry.SignedArea(padLoopXy) > 0.0;
        for (int i = 0; i < padLoopVertexCount; i++)
        {
            int next = (i + 1) % padLoopVertexCount;
            double ax = padLoopXy[i * 2];
            double ay = padLoopXy[i * 2 + 1];
            double bx = padLoopXy[next * 2];
            double by = padLoopXy[next * 2 + 1];
            double dx = bx - ax;
            double dy = by - ay;
            double length = Math.Sqrt((dx * dx) + (dy * dy));
            if (length <= tolerance)
                continue;

            double d0 = Math.Max(0.0, shoulderDistances[i]);
            double d1 = Math.Max(0.0, shoulderDistances[next]);
            if (d0 <= tolerance && d1 <= tolerance)
                continue;

            double nx = ccw ? dy / length : -dy / length;
            double ny = ccw ? -dx / length : dx / length;
            double sax = ax + (nx * d0);
            double say = ay + (ny * d0);
            double sbx = bx + (nx * d1);
            double sby = by + (ny * d1);
            double[] quad =
            [
                ax, ay,
                bx, by,
                sbx, sby,
                sax, say
            ];

            if (Math.Abs(ClipperGeometry.SignedArea(quad)) <= tolerance * tolerance)
                continue;

            stripLoops.Add(quad);
        }

        return stripLoops.Count > 0;
    }

    private static bool TryBuildPadTransitionQuadsFromSections(
        double[] padLoopXy,
        int padLoopVertexCount,
        double[] shoulderXy,
        double tolerance,
        out List<double[]> stripLoops)
    {
        stripLoops = new List<double[]>(padLoopVertexCount);
        if (padLoopVertexCount < 3 || shoulderXy.Length < padLoopVertexCount * 2)
            return false;

        for (int i = 0; i < padLoopVertexCount; i++)
        {
            int next = (i + 1) % padLoopVertexCount;
            double ax = padLoopXy[i * 2];
            double ay = padLoopXy[i * 2 + 1];
            double bx = padLoopXy[next * 2];
            double by = padLoopXy[next * 2 + 1];
            double sax = shoulderXy[i * 2];
            double say = shoulderXy[i * 2 + 1];
            double sbx = shoulderXy[next * 2];
            double sby = shoulderXy[next * 2 + 1];

            double edgeDx = bx - ax;
            double edgeDy = by - ay;
            if ((edgeDx * edgeDx) + (edgeDy * edgeDy) <= tolerance * tolerance)
                continue;

            double[] quad =
            [
                ax, ay,
                bx, by,
                sbx, sby,
                sax, say
            ];

            if (Math.Abs(ClipperGeometry.SignedArea(quad)) <= tolerance * tolerance)
                continue;

            stripLoops.Add(quad);
        }

        return stripLoops.Count > 0;
    }

    private static bool PolygonHasConcaveVertex(double[] polygonXy, int vertexCount)
    {
        if (vertexCount < 4)
            return false;

        double signedArea = 0.0;
        for (int i = 0; i < vertexCount; i++)
        {
            int next = (i + 1) % vertexCount;
            signedArea += (polygonXy[i * 2] * polygonXy[next * 2 + 1]) - (polygonXy[next * 2] * polygonXy[i * 2 + 1]);
        }

        if (Math.Abs(signedArea) <= 1e-12)
            return false;

        bool ccw = signedArea > 0.0;
        for (int i = 0; i < vertexCount; i++)
        {
            int prev = (i + vertexCount - 1) % vertexCount;
            int next = (i + 1) % vertexCount;

            double ax = polygonXy[i * 2] - polygonXy[prev * 2];
            double ay = polygonXy[i * 2 + 1] - polygonXy[prev * 2 + 1];
            double bx = polygonXy[next * 2] - polygonXy[i * 2];
            double by = polygonXy[next * 2 + 1] - polygonXy[i * 2 + 1];
            double cross = (ax * by) - (ay * bx);

            if (ccw ? cross < -1e-12 : cross > 1e-12)
                return true;
        }

        return false;
    }

    private static double UpdateSuggestedEdgeLength(
        double current,
        double[] points,
        int pointCount,
        int stride,
        bool isClosed)
    {
        if (pointCount < 2)
            return current;

        int segmentCount = isClosed ? pointCount : pointCount - 1;
        for (int i = 0; i < segmentCount; i++)
        {
            int next = (i + 1) % pointCount;
            double dx = points[next * stride] - points[i * stride];
            double dy = points[next * stride + 1] - points[i * stride + 1];
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length > 1e-9)
                current = Math.Min(current, length);
        }

        return current;
    }

    private static double ComputePadConstraintSegmentLength(double shoulderDistance)
    {
        if (shoulderDistance <= 1e-9)
            return 1.0;

        return Math.Clamp(shoulderDistance * 0.2, 0.5, 1.0);
    }

    private static double ComputeMinimumStitchSegmentLength(double modelTolerance, double terrainDetailSize)
    {
        double tolerance = GradingTolerances.ModelToleranceOrDefault(modelTolerance);
        double resolvedDetail = double.IsFinite(terrainDetailSize) && terrainDetailSize > 0.0
            ? terrainDetailSize
            : tolerance * 20.0;
        double targetStitchSpacing = Math.Max(tolerance * 8.0, resolvedDetail * 0.5);
        return Math.Max(tolerance * 4.0, targetStitchSpacing * 0.35);
    }

    private readonly record struct ConstraintLoop(double[] XyVertices, int VertexCount);

    private static ConstraintLoop BuildClosedConstraintLoop(double[] xyVertices, int vertexCount, double maxSegmentLength, double tolerance)
    {
        if (vertexCount < 3 || maxSegmentLength <= tolerance)
            return new ConstraintLoop((double[])xyVertices.Clone(), vertexCount);

        var points = new List<double>(vertexCount * 4);
        for (int i = 0; i < vertexCount; i++)
        {
            int next = (i + 1) % vertexCount;
            double ax = xyVertices[i * 2];
            double ay = xyVertices[i * 2 + 1];
            double bx = xyVertices[next * 2];
            double by = xyVertices[next * 2 + 1];
            double length = Math.Sqrt(((bx - ax) * (bx - ax)) + ((by - ay) * (by - ay)));
            int divisions = Math.Max(1, (int)Math.Ceiling(length / maxSegmentLength));

            for (int step = 0; step < divisions; step++)
            {
                double t = step / (double)divisions;
                AddLoopPoint(points, ax + ((bx - ax) * t), ay + ((by - ay) * t), tolerance);
            }
        }

        return new ConstraintLoop(points.ToArray(), points.Count / 2);
    }

    private static void AddLoopPoint(List<double> points, double x, double y, double tolerance)
    {
        if (points.Count >= 2)
        {
            double dx = x - points[^2];
            double dy = y - points[^1];
            if ((dx * dx) + (dy * dy) <= tolerance * tolerance)
                return;
        }

        points.Add(x);
        points.Add(y);
    }

    private static double[] CreateConstraintPoints(double[] xyVertices, int vertexCount)
    {
        var points = new double[vertexCount * 3];
        for (int i = 0; i < vertexCount; i++)
        {
            points[i * 3] = xyVertices[i * 2];
            points[i * 3 + 1] = xyVertices[i * 2 + 1];
        }

        return points;
    }

    private static void AddClosedLoopSegments(
        double[] xyVertices,
        int vertexCount,
        Func<double, double, int> addVertex,
        List<(int a, int b)> segList)
    {
        if (vertexCount < 3)
            return;

        int first = addVertex(xyVertices[0], xyVertices[1]);
        int previous = first;
        for (int i = 1; i < vertexCount; i++)
        {
            int current = addVertex(xyVertices[i * 2], xyVertices[i * 2 + 1]);
            if (previous != current)
                segList.Add((previous, current));
            previous = current;
        }

        if (previous != first)
            segList.Add((previous, first));
    }

    private static void AddClosedLoopVertices(
        double[] xyVertices,
        int vertexCount,
        Func<double, double, int> addVertex)
    {
        for (int i = 0; i < vertexCount; i++)
            addVertex(xyVertices[i * 2], xyVertices[i * 2 + 1]);
    }

    private static bool TryBuildExpandedOffsetPolygon(
        double[] polygonXy,
        int vertexCount,
        double[] distances,
        int cornerFanSegments,
        TerrainFaceGrid? TerrainFaceGrid,
        PadBoundary? pad,
        out double[] expandedPolygonXy,
        out double[] expandedOffsetXy,
        out string? failureReason)
    {
        failureReason = null;
        expandedPolygonXy = Array.Empty<double>();
        expandedOffsetXy = Array.Empty<double>();
        if (vertexCount < 3 || distances.Length < vertexCount)
        {
            failureReason = "Grade Pad shoulder ring was skipped because the daylight offset input was invalid.";
            return false;
        }
        bool anyPositive = false;
        for (int i = 0; i < vertexCount; i++) if (distances[i] > 1e-9) { anyPositive = true; break; }
        if (!anyPositive)
        {
            failureReason = "Grade Pad shoulder ring was skipped because the daylight offset did not extend beyond the pad boundary.";
            return false;
        }

        double signedArea = 0;
        for (int i = 0; i < vertexCount; i++)
        {
            int next = (i + 1) % vertexCount;
            double x0 = polygonXy[i * 2];
            double y0 = polygonXy[i * 2 + 1];
            double x1 = polygonXy[next * 2];
            double y1 = polygonXy[next * 2 + 1];
            signedArea += x0 * y1 - x1 * y0;
        }

        if (Math.Abs(signedArea) < 1e-12)
        {
            failureReason = "Grade Pad shoulder ring was skipped because the pad boundary is degenerate.";
            return false;
        }

        bool ccw = signedArea > 0;
        var boundaryList = new List<double>(vertexCount * 2);
        var offsetList = new List<double>(vertexCount * 2);

        for (int i = 0; i < vertexCount; i++)
        {
            int prev = (i + vertexCount - 1) % vertexCount;
            int next = (i + 1) % vertexCount;

            double x0 = polygonXy[prev * 2];
            double y0 = polygonXy[prev * 2 + 1];
            double x1 = polygonXy[i * 2];
            double y1 = polygonXy[i * 2 + 1];
            double x2 = polygonXy[next * 2];
            double y2 = polygonXy[next * 2 + 1];

            double dx0 = x1 - x0;
            double dy0 = y1 - y0;
            double dx1 = x2 - x1;
            double dy1 = y2 - y1;
            double len0 = Math.Sqrt(dx0 * dx0 + dy0 * dy0);
            double len1 = Math.Sqrt(dx1 * dx1 + dy1 * dy1);
            if (len0 < 1e-12 || len1 < 1e-12)
            {
                failureReason = "Grade Pad shoulder ring was skipped because the pad boundary contains repeated or zero-length edges.";
                return false;
            }

            double n0x = ccw ? dy0 / len0 : -dy0 / len0;
            double n0y = ccw ? -dx0 / len0 : dx0 / len0;
            double n1x = ccw ? dy1 / len1 : -dy1 / len1;
            double n1y = ccw ? -dx1 / len1 : dx1 / len1;
            double turnCross = dx0 * dy1 - dy0 * dx1;
            bool isReentrant = ccw ? turnCross < -1e-12 : turnCross > 1e-12;

            double d = distances[i];

            if (d <= 1e-9 || isReentrant)
            {
                boundaryList.Add(x1);
                boundaryList.Add(y1);
                offsetList.Add(x1);
                offsetList.Add(y1);
                continue;
            }

            if (cornerFanSegments >= 1 && !isReentrant)
            {
                double prevAngle = Math.Atan2(n0y, n0x);
                double nextAngle = Math.Atan2(n1y, n1x);
                double sweep = ComputeOutwardAngleSweep(prevAngle, nextAngle, ccw);
                if (Math.Abs(sweep) > 10.0 * Math.PI / 180.0)
                {
                    int fanCount = cornerFanSegments + 1;
                    for (int f = 0; f < fanCount; f++)
                    {
                        double theta = prevAngle + sweep * f / (fanCount - 1);
                        double rayX = Math.Cos(theta);
                        double rayY = Math.Sin(theta);
                        double rayDistance = ComputeCornerFanRayDistance(TerrainFaceGrid, pad, x1, y1, rayX, rayY, d);
                        boundaryList.Add(x1);
                        boundaryList.Add(y1);
                        offsetList.Add(x1 + rayX * rayDistance);
                        offsetList.Add(y1 + rayY * rayDistance);
                    }
                    continue;
                }
            }

            double line0x = x1 + n0x * d;
            double line0y = y1 + n0y * d;
            double line1x = x1 + n1x * d;
            double line1y = y1 + n1y * d;

            if (TryIntersectLines(line0x, line0y, dx0, dy0, line1x, line1y, dx1, dy1, out double ix, out double iy))
            {
                double offsetLen = Math.Sqrt((ix - x1) * (ix - x1) + (iy - y1) * (iy - y1));
                if (offsetLen <= d * 4.0 && !double.IsNaN(offsetLen) && !double.IsInfinity(offsetLen))
                {
                    boundaryList.Add(x1);
                    boundaryList.Add(y1);
                    offsetList.Add(ix);
                    offsetList.Add(iy);
                    continue;
                }
            }

            double point0x = x1 + (n0x * d);
            double point0y = y1 + (n0y * d);
            double point1x = x1 + (n1x * d);
            double point1y = y1 + (n1y * d);
            double option0Sq = ((point0x - x1) * (point0x - x1)) + ((point0y - y1) * (point0y - y1));
            double option1Sq = ((point1x - x1) * (point1x - x1)) + ((point1y - y1) * (point1y - y1));
            boundaryList.Add(x1);
            boundaryList.Add(y1);
            if (option0Sq <= option1Sq)
            {
                offsetList.Add(point0x);
                offsetList.Add(point0y);
            }
            else
            {
                offsetList.Add(point1x);
                offsetList.Add(point1y);
            }
        }

        expandedPolygonXy = boundaryList.ToArray();
        expandedOffsetXy = offsetList.ToArray();
        int expandedCount = expandedPolygonXy.Length / 2;

        if (cornerFanSegments == 0 && ClosedPolylineHasSelfIntersection(expandedOffsetXy, expandedCount))
        {
            failureReason = "Grade Pad shoulder ring was skipped because the daylight offset self-intersected.";
            expandedPolygonXy = Array.Empty<double>();
            expandedOffsetXy = Array.Empty<double>();
            return false;
        }

        return true;
    }

    private static double ComputeCornerFanRayDistance(
        TerrainFaceGrid? TerrainFaceGrid,
        PadBoundary? pad,
        double boundaryX,
        double boundaryY,
        double dirX,
        double dirY,
        double fallbackDistance)
    {
        double distance = Math.Max(0.0, fallbackDistance);
        if (TerrainFaceGrid == null || pad == null || distance <= 1e-9)
            return distance;

        double slopeRatio = Math.Tan(pad.SlopeAngleDeg * Math.PI / 180.0);
        if (slopeRatio <= 1e-12)
            return distance;

        double boundaryZ = pad.EvaluateZ(boundaryX, boundaryY);
        double terrainZ = TerrainFaceGrid.InterpolateZ(boundaryX, boundaryY);
        double branchSign = Math.Sign(terrainZ - boundaryZ);
        if (Math.Abs(branchSign) <= 1e-12)
            return distance;

        return ComputePadDaylightReach(
            TerrainFaceGrid,
            boundaryX,
            boundaryY,
            boundaryZ,
            dirX,
            dirY,
            slopeRatio,
            branchSign,
            distance,
            pad.MaxDistance);
    }

    private static double ComputeOutwardAngleSweep(double fromAngle, double toAngle, bool ccw)
    {
        double diff = toAngle - fromAngle;
        diff = ((diff % (2.0 * Math.PI)) + 2.0 * Math.PI) % (2.0 * Math.PI);
        if (!ccw && diff < Math.PI) diff = diff - 2.0 * Math.PI;
        if (ccw && diff > Math.PI) diff = diff - 2.0 * Math.PI;
        return diff;
    }

    private static bool TryBuildOffsetPolygon(
        double[] polygonXy,
        int vertexCount,
        double[] distances,
        out double[] offsetXy,
        out string? failureReason)
    {
        bool ok = TryBuildExpandedOffsetPolygon(
            polygonXy, vertexCount, distances, 0,
            null, null,
            out _, out offsetXy, out failureReason);
        return ok;
    }

    private static bool ClosedPolylineHasSelfIntersection(double[] xy, int vertexCount)
    {
        for (int i = 0; i < vertexCount; i++)
        {
            int iNext = (i + 1) % vertexCount;
            double ax = xy[i * 2];
            double ay = xy[i * 2 + 1];
            double bx = xy[iNext * 2];
            double by = xy[iNext * 2 + 1];
            if (((bx - ax) * (bx - ax)) + ((by - ay) * (by - ay)) <= 1e-12)
                continue;

            for (int j = i + 1; j < vertexCount; j++)
            {
                int jNext = (j + 1) % vertexCount;
                if (i == j || i == jNext || iNext == j || iNext == jNext)
                    continue;
                if (i == 0 && jNext == 0)
                    continue;

                double cx = xy[j * 2];
                double cy = xy[j * 2 + 1];
                double dx = xy[jNext * 2];
                double dy = xy[jNext * 2 + 1];
                if (((dx - cx) * (dx - cx)) + ((dy - cy) * (dy - cy)) <= 1e-12)
                    continue;

                if (SegmentsIntersect(ax, ay, bx, by, cx, cy, dx, dy))
                    return true;
            }
        }

        return false;
    }

    private static bool SegmentsIntersect(double ax, double ay, double bx, double by, double cx, double cy, double dx, double dy)
    {
        double o1 = Orientation(ax, ay, bx, by, cx, cy);
        double o2 = Orientation(ax, ay, bx, by, dx, dy);
        double o3 = Orientation(cx, cy, dx, dy, ax, ay);
        double o4 = Orientation(cx, cy, dx, dy, bx, by);

        if ((o1 > 0.0 && o2 < 0.0 || o1 < 0.0 && o2 > 0.0) &&
            (o3 > 0.0 && o4 < 0.0 || o3 < 0.0 && o4 > 0.0))
        {
            return true;
        }

        return Math.Abs(o1) <= 1e-12 && OnSegment(ax, ay, bx, by, cx, cy) ||
               Math.Abs(o2) <= 1e-12 && OnSegment(ax, ay, bx, by, dx, dy) ||
               Math.Abs(o3) <= 1e-12 && OnSegment(cx, cy, dx, dy, ax, ay) ||
               Math.Abs(o4) <= 1e-12 && OnSegment(cx, cy, dx, dy, bx, by);
    }

    private static double Orientation(double ax, double ay, double bx, double by, double cx, double cy)
    {
        return ((bx - ax) * (cy - ay)) - ((by - ay) * (cx - ax));
    }

    private static bool OnSegment(double ax, double ay, double bx, double by, double px, double py)
    {
        return px >= Math.Min(ax, bx) - 1e-12 &&
               px <= Math.Max(ax, bx) + 1e-12 &&
               py >= Math.Min(ay, by) - 1e-12 &&
               py <= Math.Max(ay, by) + 1e-12;
    }

    internal static bool TryBuildBoundaryLoop(double[] vertices, int[] faces, int faceCount, out double[] boundaryXy, out int boundaryVertexCount)
    {
        return MeshBoundaryLoopBuilder.TryBuildBoundaryLoop(vertices, faces, faceCount, out boundaryXy, out boundaryVertexCount);
    }

    private static void AddBoundaryNeighbor(Dictionary<int, List<int>> adjacency, int from, int to)
    {
        if (!adjacency.TryGetValue(from, out var list))
        {
            list = new List<int>(2);
            adjacency[from] = list;
        }

        list.Add(to);
    }

    internal static bool AllPointsInsideOrOnBoundary(double[] xy, int vertexCount, double[] boundaryLoop, int boundaryVertexCount, double tolerance)
    {
        return GradingGeometry2D.AllPointsInsideOrOnBoundary(xy, vertexCount, boundaryLoop, boundaryVertexCount, tolerance);
    }

    private static bool TryIntersectLines(
        double ax, double ay, double adx, double ady,
        double bx, double by, double bdx, double bdy,
        out double ix, out double iy)
    {
        double denom = adx * bdy - ady * bdx;
        if (Math.Abs(denom) < 1e-12)
        {
            ix = 0;
            iy = 0;
            return false;
        }

        double t = ((bx - ax) * bdy - (by - ay) * bdx) / denom;
        ix = ax + t * adx;
        iy = ay + t * ady;
        return true;
    }

    internal static double DistToBoundaryWithZ(
        double px,
        double py,
        double[] boundaryVertices,
        int boundaryVertexCount,
        out double boundaryZ,
        out double closestBx,
        out double closestBy)
    {
        boundaryZ = 0;
        closestBx = px;
        closestBy = py;
        double minDist = double.MaxValue;

        for (int i = 0; i < boundaryVertexCount; i++)
        {
            int next = (i + 1) % boundaryVertexCount;
            double ax = boundaryVertices[i * 3];
            double ay = boundaryVertices[i * 3 + 1];
            double az = boundaryVertices[i * 3 + 2];
            double bx = boundaryVertices[next * 3];
            double by = boundaryVertices[next * 3 + 1];
            double bz = boundaryVertices[next * 3 + 2];

            double dx = bx - ax;
            double dy = by - ay;
            double lenSq = dx * dx + dy * dy;
            double t = 0;
            double cx = ax;
            double cy = ay;
            if (lenSq > 1e-20)
            {
                t = Math.Clamp(((px - ax) * dx + (py - ay) * dy) / lenSq, 0.0, 1.0);
                cx = ax + t * dx;
                cy = ay + t * dy;
            }

            double dist = Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
            if (dist >= minDist)
                continue;

            minDist = dist;
            boundaryZ = az + (bz - az) * t;
            closestBx = cx;
            closestBy = cy;
        }

        return minDist;
    }

    private static GradingResult BuildResult(
        double[] originalVertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        double[] gradedVertices,
        IReadOnlyList<OutputPolyline>? outputPolylines = null,
        IReadOnlyList<string>? diagnostics = null,
        IReadOnlyList<GradingPatch>? patchSummaries = null)
    {
        return GradingResultBuilder.BuildFromXyz(
            originalVertices,
            gradedVertices,
            vertexCount,
            faces,
            faceCount,
            outputPolylines,
            diagnostics,
            patchSummaries,
            BuildStructuredPadDiagnostics(diagnostics));
    }

    private static IReadOnlyList<GradingDiagnostic>? BuildStructuredPadDiagnostics(IReadOnlyList<string>? diagnostics)
    {
        if (diagnostics == null || diagnostics.Count == 0)
            return null;

        var structured = new GradingDiagnostic[diagnostics.Count];
        for (int i = 0; i < diagnostics.Count; i++)
        {
            string message = diagnostics[i];
            structured[i] = new GradingDiagnostic(
                ClassifyPadDiagnosticSeverity(message),
                ClassifyPadDiagnosticCode(message),
                message,
                Operation: "grade_pad",
                TargetIndex: TryExtractPadDiagnosticIndex(message, out int padIndex) ? padIndex : null);
        }

        return structured;
    }

    private static GradingDiagnosticSeverity ClassifyPadDiagnosticSeverity(string message)
    {
        return message.Contains("warning", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("rejected", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("skipped", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("could not", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("collapsed", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("clipped", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("no measurable", StringComparison.OrdinalIgnoreCase)
            ? GradingDiagnosticSeverity.Warning
            : GradingDiagnosticSeverity.Information;
    }

    private static string ClassifyPadDiagnosticCode(string message)
    {
        if (message.Contains("batter slope warning", StringComparison.OrdinalIgnoreCase))
            return "grade_pad.slope.deviation";
        if (message.Contains("batter slope check", StringComparison.OrdinalIgnoreCase))
            return "grade_pad.slope.check";
        if (message.Contains("seam vertices", StringComparison.OrdinalIgnoreCase))
            return "grade_pad.stitch.seam_vertices";
        if (message.Contains("seam deviation", StringComparison.OrdinalIgnoreCase))
            return "grade_pad.stitch.seam_deviation";
        if (message.Contains("topology band width", StringComparison.OrdinalIgnoreCase))
            return "grade_pad.stitch.band_width";
        if (message.Contains("patch boundary edges near seam", StringComparison.OrdinalIgnoreCase))
            return "grade_pad.stitch.patch_boundary_edges";
        if (message.Contains("outside-mesh naked edges near seam", StringComparison.OrdinalIgnoreCase))
            return "grade_pad.stitch.outside_boundary_edges";
        if (message.Contains("seam segment matches", StringComparison.OrdinalIgnoreCase))
            return "grade_pad.stitch.segment_matches";
        if (message.Contains("seam-near boundary segments", StringComparison.OrdinalIgnoreCase))
            return "grade_pad.stitch.near_boundary_segments";
        if (message.Contains("protected stitch apron", StringComparison.OrdinalIgnoreCase))
            return "grade_pad.stitch.apron";
        if (message.Contains("terrain-side stitch loop", StringComparison.OrdinalIgnoreCase))
            return "grade_pad.stitch.terrain_side_loop";
        if (message.Contains("merged-mesh naked edges near seam", StringComparison.OrdinalIgnoreCase))
            return "grade_pad.stitch.merged_boundary_edges";
        if (message.Contains("daylight seam reached", StringComparison.OrdinalIgnoreCase))
            return "grade_pad.daylight.clipped_to_terrain";
        if (message.Contains("split local patch", StringComparison.OrdinalIgnoreCase))
            return "grade_pad.patch.split_local";
        if (message.Contains("corner constraints", StringComparison.OrdinalIgnoreCase))
            return "grade_pad.patch.corner_constraints";
        if (message.Contains("coupled protected patch", StringComparison.OrdinalIgnoreCase))
            return "grade_pad.coupled_patch";
        if (message.Contains("stitch", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("seam", StringComparison.OrdinalIgnoreCase))
            return "grade_pad.stitch";

        return "grade_pad.diagnostic";
    }

    private static bool TryExtractPadDiagnosticIndex(string message, out int padIndex)
    {
        padIndex = 0;
        const string prefix = "Grade Pad[";
        int start = message.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
            return false;

        start += prefix.Length;
        int end = message.IndexOf(']', start);
        return end > start &&
               int.TryParse(message.AsSpan(start, end - start), out padIndex);
    }

}
