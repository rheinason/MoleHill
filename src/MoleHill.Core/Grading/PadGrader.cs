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
        lockCurves ??= Array.Empty<LockCurve>();

        if (!GradingInputValidator.ValidateTerrainMesh(vertices, vertexCount, faces, faceCount, out errorMessage))
            return null;

        if (!GradingInputValidator.ValidateLockCurves(lockCurves, out errorMessage))
            return null;

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
            bool isProtectedPad = pads[padIndex].StitchApronDistance > dedupTol * 4.0;
            double[]? protectedStitchLoopCandidateXy = null;
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
                protectedStitchLoopCandidateXy = (double[])seamLoopXy.Clone();
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
            double[]? exactTerrainSideStitchLoopXy = null;
            if (!hasOutsideMesh)
            {
                diagnostics.Add($"Grade Pad[{padIndex}] daylight seam reached or crossed the terrain boundary; grading was clipped to the available terrain and no outside stitch mesh was available.");
            }
            else if (TryBuildSeamLoopFromOutsideMesh(outsideVertices, outsideFaces, outsideFaceCount, seamLoopXy, dedupTol, out double[] terrainStitchLoopXy) &&
                     LoopsCoincide(terrainStitchLoopXy, seamLoopXy, dedupTol * 8.0))
            {
                exactTerrainSideStitchLoopXy = AlignClosedLoopToReference(terrainStitchLoopXy, seamLoopXy, dedupTol);
                if (!isProtectedPad)
                    seamLoopXy = exactTerrainSideStitchLoopXy;
                if (!isProtectedPad)
                    daylightLoopXy = (double[])seamLoopXy.Clone();

                seamCoincidesWithPad = LoopsCoincide(seamLoopXy, prepared.BoundaryLoopXy, dedupTol * 4.0);
                diagnostics.Add($"Grade Pad[{padIndex}] using terrain-side stitch loop with {exactTerrainSideStitchLoopXy.Length / 2} vertices.");
            }

            PatchMeshResult? patch = null;
            string? patchError = null;
            bool allowSplitLocalFallback = prepared.Pad.StitchApronDistance <= dedupTol * 4.0;

            bool TryApplyProtectedWholeMeshRemesh(string retryDiagnostic, string fallbackFailure, out string? remeshFailureMessage)
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
                        diagnostics.Add(retryDiagnostic);
                        diagnostics.AddRange(remeshResult.Diagnostics);
                        outputPolylines.AddRange(remeshResult.OutputPolylines);
                        patchSummaries.Add(BuildPadPatchSummary(prepared, seamLoopXy, padIndex, dedupTol));
                        currentVertices = remeshResult.Vertices;
                        currentVertexCount = remeshResult.VertexCount;
                        currentFaces = remeshResult.Faces;
                        currentFaceCount = remeshResult.FaceCount;
                        remeshFailureMessage = null;
                        return true;
                    }

                    remeshFailureMessage = remeshFailure ?? fallbackFailure;
                    return false;
                }

                remeshFailureMessage = fallbackFailure;
                return false;
            }

            List<ProtectedPadStitchCandidate> BuildProtectedStitchCandidates()
            {
                var candidates = new List<ProtectedPadStitchCandidate>(3);

                void AddCandidate(ProtectedPadStitchCandidateKind kind, double[] loop, string label)
                {
                    if (loop.Length < 6)
                        return;

                    for (int i = 0; i < candidates.Count; i++)
                    {
                        if (candidates[i].StitchLoopXy.Length == loop.Length &&
                            LoopsCoincide(candidates[i].StitchLoopXy, loop, dedupTol * 4.0))
                        {
                            return;
                        }
                    }

                    candidates.Add(new ProtectedPadStitchCandidate(kind, (double[])loop.Clone(), label));
                }

                if (protectedStitchLoopCandidateXy != null)
                    AddCandidate(ProtectedPadStitchCandidateKind.ProtectedStitch, protectedStitchLoopCandidateXy, "protected stitch loop");

                if (exactTerrainSideStitchLoopXy != null)
                {
                    double budgetedSpacing = Math.Max(minStitchSegmentLength * 4.0, dedupTol * 16.0);
                    double[] budgetedLoop = ResampleClosedLoopBySpacing(exactTerrainSideStitchLoopXy, budgetedSpacing, dedupTol);
                    if ((budgetedLoop.Length / 2) < (exactTerrainSideStitchLoopXy.Length / 2) &&
                        LoopsCoincide(budgetedLoop, exactTerrainSideStitchLoopXy, Math.Max(dedupTol * 16.0, budgetedSpacing)))
                    {
                        AddCandidate(ProtectedPadStitchCandidateKind.BudgetedTerrainSide, budgetedLoop, "budgeted terrain-side stitch loop");
                    }

                    AddCandidate(ProtectedPadStitchCandidateKind.ExactTerrainSide, exactTerrainSideStitchLoopXy, "exact terrain-side stitch loop");
                }

                if (candidates.Count == 0)
                    AddCandidate(ProtectedPadStitchCandidateKind.ProtectedStitch, seamLoopXy, "protected stitch loop");

                return candidates;
            }

            bool TryApplyProtectedPatchCandidate(
                ProtectedPadStitchCandidate candidate,
                int softVertexBudget,
                int hardVertexBudget,
                out string failure)
            {
                failure = string.Empty;
                double[] candidateSeamLoopXy = candidate.StitchLoopXy;
                bool candidateSeamCoincidesWithPad = LoopsCoincide(candidateSeamLoopXy, prepared.BoundaryLoopXy, dedupTol * 4.0);
                if (!hasOutsideMesh || candidateSeamCoincidesWithPad)
                {
                    failure = $"{candidate.Label} could not be tested because no outside stitch mesh was available.";
                    return false;
                }

                PatchMeshResult? candidatePatch = TryBuildPadPatchMesh(
                    currentFaceGrid,
                    barriers,
                    hasTerrainBoundary,
                    terrainBoundaryLoop,
                    terrainBoundaryVertexCount,
                    prepared,
                    daylightLoopXy,
                    candidateSeamLoopXy,
                    out string? candidatePatchError);
                if (candidatePatch == null)
                {
                    failure = $"{candidate.Label} patch build failed: {candidatePatchError ?? "no patch was produced"}.";
                    return false;
                }

                if (candidatePatch.VertexCount > hardVertexBudget)
                {
                    failure = $"{candidate.Label} patch exceeded hard density budget ({candidatePatch.VertexCount:N0} vertices > {hardVertexBudget:N0}).";
                    return false;
                }

                if (!TryBuildBoundaryLoop(
                        candidatePatch.Vertices,
                        candidatePatch.Faces,
                        candidatePatch.FaceCount,
                        out double[] candidatePatchBoundaryLoopXy,
                        out _))
                {
                    failure = $"{candidate.Label} patch did not produce a single closed stitch boundary.";
                    return false;
                }

                SeamValidationResult candidateSeamValidation = SeamValidator.ValidatePatchSegmentMatch(
                    candidatePatchBoundaryLoopXy,
                    candidatePatch.Vertices,
                    candidatePatch.Faces,
                    candidatePatch.FaceCount,
                    outsideVertices,
                    outsideFaces,
                    outsideFaceCount,
                    dedupTol);
                SeamGraph candidateSeamGraph = candidateSeamValidation.SeamGraph!;
                if (!candidateSeamValidation.IsValid)
                {
                    failure =
                        $"{candidate.Label} seam integrity check failed ({candidateSeamValidation.FailureReason}; patch={candidateSeamGraph.PatchMatchedSegments}/{candidateSeamGraph.SeamVertexCount}, outside={candidateSeamGraph.TerrainMatchedSegments}/{candidateSeamGraph.SeamVertexCount}).";
                    return false;
                }

                if (!TryMergePatchWithOutsideTerrain(
                        outsideVertices,
                        outsideVertexCount,
                        outsideFaces,
                        outsideFaceCount,
                        candidatePatch,
                        candidateSeamLoopXy,
                        hasTerrainBoundary ? terrainBoundaryLoop : null,
                        dedupTol,
                        rejectInteriorSeamBoundaryEdges: true,
                        out double[] mergedVertices,
                        out int mergedVertexCount,
                        out int[] mergedFaces,
                        out int mergedFaceCount,
                        out string? mergeFailure))
                {
                    failure = $"{candidate.Label} stitched merge rejected: {mergeFailure}.";
                    return false;
                }

                diagnostics.Add(
                    $"Grade Pad[{padIndex}] selected {candidate.Label}: stitch vertices={candidateSeamLoopXy.Length / 2:N0}, patch vertices={candidatePatch.VertexCount:N0}, patch faces={candidatePatch.FaceCount:N0}, soft cap={softVertexBudget:N0}, hard cap={hardVertexBudget:N0}.");
                if (candidatePatch.VertexCount > softVertexBudget)
                    diagnostics.Add($"Grade Pad[{padIndex}] explicit protected patch density warning: {candidatePatch.VertexCount:N0} vertices exceeds soft budget {softVertexBudget:N0}.");

                seamLoopXy = candidateSeamLoopXy;
                seamCoincidesWithPad = candidateSeamCoincidesWithPad;
                diagnostics.AddRange(BuildPadSlopeDiagnostics(padIndex, prepared, candidatePatch, dedupTol));
                if (candidatePatch.CornerConstraintsRejected)
                    diagnostics.Add($"Grade Pad[{padIndex}] explicit patch corner constraints were rejected by the triangulator; retried without them.");
                if (candidatePatch.CornerConstraintCount > 0)
                    diagnostics.Add($"Grade Pad[{padIndex}] explicit patch corner constraints: {candidatePatch.CornerConstraintCount}.");
                diagnostics.AddRange(BuildPadStitchDiagnostics(
                    padIndex,
                    daylightLoopXy,
                    candidateSeamLoopXy,
                    candidatePatchBoundaryLoopXy,
                    candidatePatch.Vertices,
                    candidatePatch.Faces,
                    candidatePatch.FaceCount,
                    outsideVertices,
                    outsideFaces,
                    outsideFaceCount,
                    dedupTol));

                currentVertices = mergedVertices;
                currentVertexCount = mergedVertexCount;
                currentFaces = mergedFaces;
                currentFaceCount = mergedFaceCount;
                diagnostics.Add(
                    $"Grade Pad[{padIndex}] merged-mesh naked edges near seam: {CountBoundaryEdgesNearLoop(currentVertices, currentFaces, currentFaceCount, candidateSeamLoopXy, dedupTol * 4.0)}.");
                outputPolylines.Add(BuildPadBoundaryPolyline(prepared));
                patchSummaries.Add(BuildPadPatchSummary(prepared, candidateSeamLoopXy, padIndex, dedupTol));
                return true;
            }

            if (!allowSplitLocalFallback)
            {
                int localInputVertexCount = Math.Max(localVertexCount, 1);
                int softVertexBudget = Math.Max(2000, Math.Max(localInputVertexCount * 8, currentVertexCount * 2));
                int hardVertexBudget = Math.Max(10000, Math.Max(localInputVertexCount * 25, currentVertexCount * 4));
                var candidateFailures = new List<string>();
                bool appliedCandidate = false;
                foreach (ProtectedPadStitchCandidate candidate in BuildProtectedStitchCandidates())
                {
                    if (TryApplyProtectedPatchCandidate(candidate, softVertexBudget, hardVertexBudget, out string candidateFailure))
                    {
                        appliedCandidate = true;
                        break;
                    }

                    candidateFailures.Add(candidateFailure);
                }

                if (appliedCandidate)
                    continue;

                foreach (string candidateFailure in candidateFailures)
                    diagnostics.Add($"Grade Pad[{padIndex}] protected stitch candidate rejected: {candidateFailure}");

                if (exactTerrainSideStitchLoopXy != null)
                    seamLoopXy = exactTerrainSideStitchLoopXy;
                else if (protectedStitchLoopCandidateXy != null)
                    seamLoopXy = protectedStitchLoopCandidateXy;

                string fallbackFailure = candidateFailures.Count > 0
                    ? candidateFailures[^1]
                    : $"Grade Pad[{padIndex}] explicit protected patch could not be built.";
                if (TryApplyProtectedWholeMeshRemesh(
                        $"Grade Pad[{padIndex}] explicit protected patch candidates were rejected; retrying protected whole-mesh remesh.",
                        fallbackFailure,
                        out string? remeshFailure))
                {
                    continue;
                }

                diagnostics.AddRange(BuildProtectedPadInteractionDiagnostics(padIndex, seamLoopXy, patchSummaries, dedupTol));
                failureOutputPolylines = BuildProtectedPadFailurePolylines(prepared, daylightLoopXy, seamLoopXy, currentFaceGrid);
                errorMessage = BuildProtectedPadFailureMessage(
                    remeshFailure ?? fallbackFailure,
                    diagnostics);
                return null;
            }

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
                SeamValidationResult seamValidation = SeamValidator.ValidatePatchSegmentMatch(
                    patchBoundaryLoopXy,
                    patch.Vertices,
                    patch.Faces,
                    patch.FaceCount,
                    outsideVertices,
                    outsideFaces,
                    outsideFaceCount,
                    dedupTol);
                SeamGraph seamGraph = seamValidation.SeamGraph!;
                if (!seamValidation.IsValid)
                {
                    diagnostics.Add($"Grade Pad[{padIndex}] patch seam integrity check failed ({seamValidation.FailureReason}); using split local patch.");
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

                    seamValidation = SeamValidator.ValidatePatchSegmentMatch(
                        patchBoundaryLoopXy,
                        patch.Vertices,
                        patch.Faces,
                        patch.FaceCount,
                        outsideVertices,
                        outsideFaces,
                        outsideFaceCount,
                        dedupTol);
                    seamGraph = seamValidation.SeamGraph!;
                    if (!seamValidation.IsValid)
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
                    rejectInteriorSeamBoundaryEdges: false,
                    out currentVertices,
                    out currentVertexCount,
                    out currentFaces,
                    out currentFaceCount,
                    out string? mergeFailure);
                if (!merged)
                {
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
                            rejectInteriorSeamBoundaryEdges: false,
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

        diagnostics.Add(GradingTopologyDiagnostics.BuildMeshSummaryMessage(
            "Grade Pad",
            vertexCount,
            faceCount,
            currentVertexCount,
            currentFaceCount,
            currentFaces));

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
}
