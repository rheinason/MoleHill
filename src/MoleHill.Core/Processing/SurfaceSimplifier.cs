using System.Diagnostics;
using MoleHill.Core.Analysis;
using MoleHill.Core.Engine;

namespace MoleHill.Core.Processing;

/// <summary>Deterministic tolerance- or vertex-count reduction of a constrained 2.5D terrain mesh.</summary>
public static class SurfaceSimplifier
{
    public enum SimplificationMode
    {
        MaximumDeviation,
        TargetVertexCount
    }

    public enum Phase
    {
        ConstraintPreparation,
        Triangulation,
        Verification,
        Refinement
    }

    public enum TerminationReason
    {
        ToleranceSatisfied,
        TargetCountSatisfied,
        MandatorySetExceedsTarget,
        NoReductionAchieved,
        UnsupportedInput,
        TriangulationFailed,
        VerificationFailed,
        NoProgress,
        ResourceLimit
    }

    public sealed class Options
    {
        public SimplificationMode Mode { get; init; } = SimplificationMode.MaximumDeviation;

        public double MaximumDeviation { get; init; }

        public int TargetVertexCount { get; init; }

        public double NumericalTolerance { get; init; } = 1e-8;

        public int MaximumRounds { get; init; } = 24;

        public Func<bool>? ShouldCancel { get; init; }

        /// <summary>Optional per-operation timing observer used by reproducible benchmarks.</summary>
        public Action<Phase, TimeSpan>? RecordPhase { get; init; }
    }

    public sealed class Result
    {
        public required double[] Vertices { get; init; }

        public required int[] Faces { get; init; }

        public required int InputVertexCount { get; init; }

        public required int InputFaceCount { get; init; }

        public int OutputVertexCount => Vertices.Length / 3;

        public int OutputFaceCount => Faces.Length / 3;

        public required int ProtectedVertexCount { get; init; }

        public required double MaximumDeviation { get; init; }

        public required bool HasEqualDomain { get; init; }

        public required int Rounds { get; init; }

        public required TerminationReason Termination { get; init; }

        public required string Diagnostic { get; init; }

        public bool Reduced => OutputVertexCount < InputVertexCount;
    }

    private readonly record struct CellKey(long X, long Y);

    private readonly record struct SourceError(int SourceIndex, double Error);

    private sealed class SelectedPoints
    {
        private readonly double[] _sourceVertices;
        private readonly double _tolerance;
        private readonly double _inverseTolerance;
        private readonly Dictionary<CellKey, List<int>> _cells = new();

        public SelectedPoints(double[] sourceVertices, double tolerance)
        {
            _sourceVertices = sourceVertices;
            _tolerance = Math.Max(tolerance, 1e-12);
            _inverseTolerance = 1.0 / _tolerance;
        }

        public List<double> Vertices { get; } = new();

        public int Count => Vertices.Count / 3;

        public bool TryAddSource(int sourceIndex, out int selectedIndex) => TryAdd(
            _sourceVertices[sourceIndex * 3],
            _sourceVertices[sourceIndex * 3 + 1],
            _sourceVertices[sourceIndex * 3 + 2],
            out selectedIndex);

        public bool TryAdd(double x, double y, double z, out int selectedIndex)
        {
            CellKey cell = ToCell(x, y);
            for (long cy = cell.Y - 1; cy <= cell.Y + 1; cy++)
            {
                for (long cx = cell.X - 1; cx <= cell.X + 1; cx++)
                {
                    if (!_cells.TryGetValue(new CellKey(cx, cy), out List<int>? candidates))
                        continue;
                    foreach (int candidate in candidates)
                    {
                        if (Math.Abs(Vertices[candidate * 3] - x) <= _tolerance &&
                            Math.Abs(Vertices[candidate * 3 + 1] - y) <= _tolerance)
                        {
                            selectedIndex = candidate;
                            return false;
                        }
                    }
                }
            }

            selectedIndex = Count;
            Vertices.Add(x);
            Vertices.Add(y);
            Vertices.Add(z);
            if (!_cells.TryGetValue(cell, out List<int>? list))
            {
                list = new List<int>(1);
                _cells[cell] = list;
            }
            list.Add(selectedIndex);
            return true;
        }

        private CellKey ToCell(double x, double y) => new(
            (long)Math.Floor(x * _inverseTolerance),
            (long)Math.Floor(y * _inverseTolerance));
    }

    public static Result Simplify(
        double[] vertices,
        int[] faces,
        int[] requiredSegments,
        Options options)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(faces);
        ArgumentNullException.ThrowIfNull(requiredSegments);
        ArgumentNullException.ThrowIfNull(options);
        var cancellation = new CancellationProbe(options.ShouldCancel);
        cancellation.ThrowIfCancelled();

        if (!Enum.IsDefined(options.Mode) ||
            (options.Mode == SimplificationMode.MaximumDeviation &&
             (!double.IsFinite(options.MaximumDeviation) || options.MaximumDeviation < 0.0)) ||
            (options.Mode == SimplificationMode.TargetVertexCount && options.TargetVertexCount < 3) ||
            !double.IsFinite(options.NumericalTolerance) || options.NumericalTolerance < 0.0 ||
            options.MaximumRounds < 1)
        {
            return Fallback(vertices, faces, 0, 0, TerminationReason.UnsupportedInput,
                "Simplification options must select a known mode, use finite nonnegative tolerances, " +
                "a target of at least three vertices in count mode, and at least one round.");
        }

        var evaluatorOptions = new SurfaceDeviationEvaluator.Options
        {
            NumericalTolerance = options.NumericalTolerance,
            ShouldCancel = options.ShouldCancel
        };
        long phaseStarted = Stopwatch.GetTimestamp();
        SurfaceDeviationEvaluator.Result self = SurfaceDeviationEvaluator.Evaluate(
            vertices, faces, vertices, faces, evaluatorOptions);
        options.RecordPhase?.Invoke(Phase.Verification, Stopwatch.GetElapsedTime(phaseStarted));
        if (!self.IsValid || !self.HasEqualDomain)
        {
            return Fallback(vertices, faces, 0, 0, TerminationReason.UnsupportedInput,
                self.FailureReason ?? "The input is not a valid single-valued 2.5D terrain domain.");
        }

        int inputVertexCount = vertices.Length / 3;
        int inputFaceCount = faces.Length / 3;
        double numericalVerticalTolerance = Math.Max(
            options.NumericalTolerance,
            VerticalScale(vertices) * 1e-12);
        if (requiredSegments.Length % 2 != 0)
        {
            return Fallback(vertices, faces, 0, 0, TerminationReason.UnsupportedInput,
                "Required segments must contain vertex-index pairs.");
        }

        phaseStarted = Stopwatch.GetTimestamp();
        bool protectedGeometryValid = TryBuildProtectedGeometry(
            faces, inputFaceCount, inputVertexCount, requiredSegments,
            out bool[] used, out bool[] protectedVertices, out List<(int A, int B)> protectedEdges,
            out int inputBoundaryEdgeCount,
            out string? topologyFailure);
        options.RecordPhase?.Invoke(Phase.ConstraintPreparation, Stopwatch.GetElapsedTime(phaseStarted));
        if (!protectedGeometryValid)
        {
            return Fallback(vertices, faces, 0, 0, TerminationReason.UnsupportedInput, topologyFailure!);
        }

        int usedVertexCount = used.Count(value => value);
        int protectedVertexCount = protectedVertices.Count(value => value);
        bool countMode = options.Mode == SimplificationMode.TargetVertexCount;
        if (countMode && options.TargetVertexCount >= usedVertexCount)
        {
            return AcceptedInputForCount(vertices, faces, usedVertexCount, inputFaceCount, protectedVertexCount,
                options.TargetVertexCount);
        }
        if (countMode && protectedVertexCount > options.TargetVertexCount)
        {
            return Fallback(vertices, faces, protectedVertexCount, 0, TerminationReason.MandatorySetExceedsTarget,
                $"The {protectedVertexCount:N0}-vertex mandatory boundary/constraint set exceeds the " +
                $"{options.TargetVertexCount:N0}-vertex target.");
        }
        var selected = new SelectedPoints(vertices, options.NumericalTolerance);
        var sourceToSelected = new int[inputVertexCount];
        Array.Fill(sourceToSelected, -1);
        for (int source = 0; source < inputVertexCount; source++)
        {
            if (!protectedVertices[source])
                continue;
            selected.TryAddSource(source, out int selectedIndex);
            sourceToSelected[source] = selectedIndex;
        }

        int seedTarget = Math.Min(usedVertexCount, Math.Max(protectedVertexCount, Math.Max(32, (int)Math.Ceiling(Math.Sqrt(usedVertexCount) * 2.0))));
        if (countMode)
            seedTarget = Math.Min(seedTarget, options.TargetVertexCount);
        AddSpatialExtrema(vertices, used, seedTarget, selected, sourceToSelected);
        FillDeterministically(vertices, used, seedTarget, selected, sourceToSelected);

        int[] selectedSegments = new int[protectedEdges.Count * 2];
        for (int edge = 0; edge < protectedEdges.Count; edge++)
        {
            (int a, int b) = protectedEdges[edge];
            selectedSegments[edge * 2] = sourceToSelected[a];
            selectedSegments[edge * 2 + 1] = sourceToSelected[b];
        }

        var referenceProjector = new MeshHeightProjector(vertices, inputVertexCount, faces, inputFaceCount);
        var referenceDomain = new SurfaceDeviationEvaluator.DomainCoverageIndex(
            vertices, faces, options.NumericalTolerance);
        Result? bestCountCandidate = null;
        Result StopCountOrFallback(int rounds, TerminationReason failureReason, string diagnostic) =>
            bestCountCandidate != null
                ? FinalizeCountResult(bestCountCandidate, rounds, diagnostic)
                : Fallback(vertices, faces, protectedVertexCount, rounds, failureReason, diagnostic);
        for (int round = 1; round <= options.MaximumRounds; round++)
        {
            cancellation.ThrowIfCancelled();
            phaseStarted = Stopwatch.GetTimestamp();
            bool candidateBuilt = TryBuildCandidate(
                selected, selectedSegments, referenceDomain,
                options.ShouldCancel,
                out double[] candidateVertices, out int[] candidateFaces, out string? buildFailure);
            options.RecordPhase?.Invoke(Phase.Triangulation, Stopwatch.GetElapsedTime(phaseStarted));
            if (!candidateBuilt)
            {
                return StopCountOrFallback(round, TerminationReason.TriangulationFailed, buildFailure!);
            }

            phaseStarted = Stopwatch.GetTimestamp();
            if (!RequiredEdgesExist(candidateFaces, selectedSegments, selected, candidateVertices, options.NumericalTolerance))
            {
                options.RecordPhase?.Invoke(Phase.Verification, Stopwatch.GetElapsedTime(phaseStarted));
                return StopCountOrFallback(round, TerminationReason.VerificationFailed,
                    "The candidate triangulation did not preserve every required segment.");
            }

            MeshTopologyValidator.BoundaryGraphAnalysis candidateTopology =
                MeshTopologyValidator.AnalyzeBoundaryGraph(candidateFaces, candidateFaces.Length / 3);
            if (candidateTopology.NonManifoldEdgeCount != 0 ||
                candidateTopology.HasOpenBoundaryChains ||
                candidateTopology.BoundaryEdgeCount != inputBoundaryEdgeCount)
            {
                options.RecordPhase?.Invoke(Phase.Verification, Stopwatch.GetElapsedTime(phaseStarted));
                return StopCountOrFallback(round, TerminationReason.VerificationFailed,
                    $"The candidate topology changed the terrain boundary or introduced a crack " +
                    $"(boundary edges {inputBoundaryEdgeCount:N0} -> {candidateTopology.BoundaryEdgeCount:N0}, " +
                    $"open chains {candidateTopology.HasOpenBoundaryChains}, " +
                    $"non-manifold edges {candidateTopology.NonManifoldEdgeCount:N0}).");
            }

            SurfaceDeviationEvaluator.Result measured = SurfaceDeviationEvaluator.Evaluate(
                vertices, faces, candidateVertices, candidateFaces, evaluatorOptions);
            options.RecordPhase?.Invoke(Phase.Verification, Stopwatch.GetElapsedTime(phaseStarted));
            if (!measured.IsValid || !measured.HasEqualDomain)
            {
                return StopCountOrFallback(round, TerminationReason.VerificationFailed,
                    measured.FailureReason ?? "Candidate surface verification failed.");
            }

            int candidateVertexCount = candidateVertices.Length / 3;
            if (countMode)
            {
                if (candidateVertexCount > options.TargetVertexCount)
                {
                    return StopCountOrFallback(
                        round, TerminationReason.VerificationFailed,
                        $"The triangulator produced {candidateVertexCount:N0} vertices, exceeding the " +
                        $"{options.TargetVertexCount:N0}-vertex target after mandatory geometry was inserted.");
                }

                if (candidateVertexCount < usedVertexCount &&
                    (bestCountCandidate == null || measured.MaximumDeviation < bestCountCandidate.MaximumDeviation))
                {
                    bestCountCandidate = new Result
                    {
                        Vertices = candidateVertices,
                        Faces = candidateFaces,
                        InputVertexCount = usedVertexCount,
                        InputFaceCount = inputFaceCount,
                        ProtectedVertexCount = protectedVertexCount,
                        MaximumDeviation = measured.MaximumDeviation,
                        HasEqualDomain = true,
                        Rounds = round,
                        Termination = TerminationReason.TargetCountSatisfied,
                        Diagnostic = $"Reduced {usedVertexCount:N0} to {candidateVertexCount:N0} vertices at or below " +
                            $"the {options.TargetVertexCount:N0}-vertex target; measured maximum deviation " +
                            $"{measured.MaximumDeviation:G6}."
                    };
                }

                if (selected.Count >= options.TargetVertexCount)
                {
                    return StopCountOrFallback(
                        round, TerminationReason.NoReductionAchieved,
                        "The target point set did not produce a smaller verified mesh.");
                }

                int beforeCountRefinement = selected.Count;
                phaseStarted = Stopwatch.GetTimestamp();
                if (double.IsFinite(measured.WorstX) && selected.Count < options.TargetVertexCount &&
                    referenceProjector.TryProjectZ(
                        measured.WorstX, measured.WorstY, 0.0, options.NumericalTolerance,
                        out double countWitnessZ, out MeshHeightProjector.ProjectionStatus countWitnessStatus) &&
                    countWitnessStatus == MeshHeightProjector.ProjectionStatus.Projected)
                {
                    selected.TryAdd(measured.WorstX, measured.WorstY, countWitnessZ, out _);
                }

                AddLargestSourceErrors(
                    vertices, used, sourceToSelected, selected, candidateVertices, candidateFaces,
                    maximumDeviation: -1.0, tolerance: options.NumericalTolerance, cancellation: cancellation,
                    maxAdditions: options.TargetVertexCount - selected.Count);
                options.RecordPhase?.Invoke(Phase.Refinement, Stopwatch.GetElapsedTime(phaseStarted));
                if (selected.Count == beforeCountRefinement)
                    return StopCountOrFallback(round, TerminationReason.NoProgress,
                        "Count-mode refinement found no additional surface samples.");
                continue;
            }

            if (measured.MaximumDeviation <= options.MaximumDeviation + numericalVerticalTolerance)
            {
                if (candidateVertices.Length / 3 >= usedVertexCount)
                {
                    return Fallback(vertices, faces, protectedVertexCount, round, TerminationReason.NoReductionAchieved,
                        "A verified candidate met the deviation bound but did not reduce the used vertex count.");
                }

                return new Result
                {
                    Vertices = candidateVertices,
                    Faces = candidateFaces,
                    InputVertexCount = usedVertexCount,
                    InputFaceCount = inputFaceCount,
                    ProtectedVertexCount = protectedVertexCount,
                    MaximumDeviation = measured.MaximumDeviation,
                    HasEqualDomain = true,
                    Rounds = round,
                    Termination = TerminationReason.ToleranceSatisfied,
                    Diagnostic = $"Reduced {usedVertexCount:N0} to {candidateVertices.Length / 3:N0} vertices within a measured maximum deviation of {measured.MaximumDeviation:G6} (design bound {options.MaximumDeviation:G6}, numerical tolerance {numericalVerticalTolerance:G6})."
                };
            }

            int before = selected.Count;
            phaseStarted = Stopwatch.GetTimestamp();
            if (double.IsFinite(measured.WorstX) &&
                referenceProjector.TryProjectZ(
                    measured.WorstX, measured.WorstY, 0.0, options.NumericalTolerance,
                    out double witnessZ, out MeshHeightProjector.ProjectionStatus witnessStatus) &&
                witnessStatus == MeshHeightProjector.ProjectionStatus.Projected)
            {
                selected.TryAdd(measured.WorstX, measured.WorstY, witnessZ, out _);
            }

            AddLargestSourceErrors(
                vertices, used, sourceToSelected, selected, candidateVertices, candidateFaces,
                options.MaximumDeviation, options.NumericalTolerance, cancellation, int.MaxValue);
            options.RecordPhase?.Invoke(Phase.Refinement, Stopwatch.GetElapsedTime(phaseStarted));
            if (selected.Count == before)
            {
                return Fallback(vertices, faces, protectedVertexCount, round, TerminationReason.NoProgress,
                    $"Refinement stalled at a measured maximum deviation of {measured.MaximumDeviation:G6}.");
            }

            if (selected.Count >= usedVertexCount)
            {
                return Fallback(vertices, faces, protectedVertexCount, round, TerminationReason.NoReductionAchieved,
                    "Meeting the requested deviation required all incoming used vertices.");
            }
        }

        return StopCountOrFallback(options.MaximumRounds, TerminationReason.ResourceLimit,
            $"The simplifier reached its {options.MaximumRounds}-round resource limit before satisfying the active mode.");
    }

    private static bool TryBuildProtectedGeometry(
        int[] faces,
        int faceCount,
        int vertexCount,
        int[] requiredSegments,
        out bool[] used,
        out bool[] protectedVertices,
        out List<(int A, int B)> protectedEdges,
        out int boundaryEdgeCount,
        out string? failure)
    {
        used = new bool[vertexCount];
        protectedVertices = new bool[vertexCount];
        protectedEdges = new List<(int A, int B)>();
        boundaryEdgeCount = 0;
        failure = null;
        var incidence = new Dictionary<long, int>(Math.Max(8, faceCount * 2), IndexedMeshTools.EdgeKeyComparer.Instance);
        for (int face = 0; face < faceCount; face++)
        {
            int a = faces[face * 3], b = faces[face * 3 + 1], c = faces[face * 3 + 2];
            used[a] = used[b] = used[c] = true;
            AddEdge(incidence, a, b); AddEdge(incidence, b, c); AddEdge(incidence, c, a);
        }

        var protectedKeys = new HashSet<long>(IndexedMeshTools.EdgeKeyComparer.Instance);
        foreach ((long key, int count) in incidence)
        {
            if (count > 2)
            {
                failure = "The incoming mesh contains a non-manifold edge.";
                return false;
            }
            if (count == 1)
            {
                protectedKeys.Add(key);
                boundaryEdgeCount++;
            }
        }

        MeshTopologyValidator.BoundaryGraphAnalysis inputTopology =
            MeshTopologyValidator.AnalyzeBoundaryGraph(faces, faceCount);
        if (inputTopology.HasOpenBoundaryChains)
        {
            failure = "The incoming mesh boundary contains open chains or internal cracks.";
            return false;
        }

        for (int segment = 0; segment < requiredSegments.Length / 2; segment++)
        {
            int a = requiredSegments[segment * 2], b = requiredSegments[segment * 2 + 1];
            if ((uint)a >= (uint)vertexCount || (uint)b >= (uint)vertexCount || a == b)
            {
                failure = $"Required segment {segment} has invalid endpoint indices.";
                return false;
            }
            long key = IndexedMeshTools.GetEdgeKey(a, b);
            if (!incidence.ContainsKey(key))
            {
                failure = $"Required segment {segment} is not an edge of the incoming mesh.";
                return false;
            }
            protectedKeys.Add(key);
        }

        foreach (long key in protectedKeys.OrderBy(value => value))
        {
            int a = (int)(key >> 32), b = (int)(key & 0xffffffffL);
            protectedVertices[a] = protectedVertices[b] = true;
            protectedEdges.Add((a, b));
        }
        return true;
    }

    private static void AddEdge(Dictionary<long, int> incidence, int a, int b)
    {
        long key = IndexedMeshTools.GetEdgeKey(a, b);
        incidence.TryGetValue(key, out int count);
        incidence[key] = count + 1;
    }

    private static double VerticalScale(double[] vertices)
    {
        double minZ = double.MaxValue, maxZ = double.MinValue;
        for (int i = 0; i < vertices.Length / 3; i++)
        {
            double z = vertices[i * 3 + 2];
            minZ = Math.Min(minZ, z);
            maxZ = Math.Max(maxZ, z);
        }
        return Math.Max(1.0, maxZ - minZ);
    }

    private static void AddSpatialExtrema(double[] vertices, bool[] used, int target, SelectedPoints selected, int[] sourceToSelected)
    {
        if (selected.Count >= target)
            return;
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        for (int i = 0; i < used.Length; i++)
        {
            if (!used[i]) continue;
            minX = Math.Min(minX, vertices[i * 3]); maxX = Math.Max(maxX, vertices[i * 3]);
            minY = Math.Min(minY, vertices[i * 3 + 1]); maxY = Math.Max(maxY, vertices[i * 3 + 1]);
        }
        int cellTarget = Math.Max(1, (target - selected.Count) / 2);
        double width = Math.Max(maxX - minX, 1e-12), height = Math.Max(maxY - minY, 1e-12);
        int xCells = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(cellTarget * width / height)));
        int yCells = Math.Max(1, (int)Math.Ceiling((double)cellTarget / xCells));
        int[] low = Enumerable.Repeat(-1, xCells * yCells).ToArray();
        int[] high = Enumerable.Repeat(-1, xCells * yCells).ToArray();
        for (int source = 0; source < used.Length; source++)
        {
            if (!used[source] || sourceToSelected[source] >= 0) continue;
            int x = Math.Min(xCells - 1, (int)((vertices[source * 3] - minX) / width * xCells));
            int y = Math.Min(yCells - 1, (int)((vertices[source * 3 + 1] - minY) / height * yCells));
            int cell = y * xCells + x;
            if (low[cell] < 0 || vertices[source * 3 + 2] < vertices[low[cell] * 3 + 2]) low[cell] = source;
            if (high[cell] < 0 || vertices[source * 3 + 2] > vertices[high[cell] * 3 + 2]) high[cell] = source;
        }
        for (int cell = 0; cell < low.Length && selected.Count < target; cell++)
        {
            Add(low[cell]);
            if (selected.Count < target) Add(high[cell]);
        }
        void Add(int source)
        {
            if (source < 0 || sourceToSelected[source] >= 0) return;
            selected.TryAddSource(source, out int selectedIndex);
            sourceToSelected[source] = selectedIndex;
        }
    }

    private static void FillDeterministically(double[] vertices, bool[] used, int target, SelectedPoints selected, int[] sourceToSelected)
    {
        for (int source = 0; source < used.Length && selected.Count < target; source++)
        {
            if (!used[source] || sourceToSelected[source] >= 0) continue;
            selected.TryAddSource(source, out int selectedIndex);
            sourceToSelected[source] = selectedIndex;
        }
    }

    private static bool TryBuildCandidate(
        SelectedPoints selected,
        int[] segments,
        SurfaceDeviationEvaluator.DomainCoverageIndex referenceDomain,
        Func<bool>? shouldCancel,
        out double[] vertices,
        out int[] faces,
        out string? failure)
    {
        double[] selectedVertices = selected.Vertices.ToArray();
        var xy = new double[selected.Count * 2];
        var z = new double[selected.Count];
        for (int i = 0; i < selected.Count; i++)
        {
            xy[i * 2] = selectedVertices[i * 3]; xy[i * 2 + 1] = selectedVertices[i * 3 + 1]; z[i] = selectedVertices[i * 3 + 2];
        }
        TinResult? tin = new TinEngine().Build(
            xy, z, segments, QualitySettings.None, out failure,
            useConvexHull: true, shouldCancel: shouldCancel, includeEdgeTopology: false);
        if (tin is null)
        {
            vertices = Array.Empty<double>(); faces = Array.Empty<int>();
            return false;
        }

        var keptFaces = new List<int>(tin.Faces.Length);
        for (int face = 0; face < tin.FaceCount; face++)
        {
            if ((face & 255) == 0 && shouldCancel?.Invoke() == true) throw new OperationCanceledException("Simplification cancelled.");
            int a = tin.Faces[face * 3], b = tin.Faces[face * 3 + 1], c = tin.Faces[face * 3 + 2];
            if (referenceDomain.CoversTriangle(tin.Vertices, a, b, c))
            {
                keptFaces.Add(a); keptFaces.Add(b); keptFaces.Add(c);
            }
        }
        if (keptFaces.Count == 0)
        {
            vertices = Array.Empty<double>(); faces = Array.Empty<int>(); failure = "Triangulation produced no faces inside the incoming terrain domain.";
            return false;
        }
        Compact(tin.Vertices, keptFaces, out vertices, out faces);
        failure = null;
        return true;
    }

    private static void Compact(double[] inputVertices, List<int> inputFaces, out double[] vertices, out int[] faces)
    {
        var oldToNew = new int[inputVertices.Length / 3];
        Array.Fill(oldToNew, -1);
        int count = 0;
        foreach (int old in inputFaces)
            if (oldToNew[old] < 0) oldToNew[old] = count++;
        vertices = new double[count * 3];
        for (int old = 0; old < oldToNew.Length; old++)
            if (oldToNew[old] >= 0) Array.Copy(inputVertices, old * 3, vertices, oldToNew[old] * 3, 3);
        faces = new int[inputFaces.Count];
        for (int i = 0; i < faces.Length; i++) faces[i] = oldToNew[inputFaces[i]];
    }

    private static bool RequiredEdgesExist(int[] faces, int[] segments, SelectedPoints selected, double[] candidateVertices, double tolerance)
    {
        var candidateByCell = new SelectedPoints(candidateVertices, tolerance);
        for (int i = 0; i < candidateVertices.Length / 3; i++) candidateByCell.TryAddSource(i, out _);
        var candidateEdges = new HashSet<long>(IndexedMeshTools.EdgeKeyComparer.Instance);
        for (int face = 0; face < faces.Length / 3; face++)
        {
            int a = faces[face * 3], b = faces[face * 3 + 1], c = faces[face * 3 + 2];
            candidateEdges.Add(IndexedMeshTools.GetEdgeKey(a, b)); candidateEdges.Add(IndexedMeshTools.GetEdgeKey(b, c)); candidateEdges.Add(IndexedMeshTools.GetEdgeKey(c, a));
        }
        for (int segment = 0; segment < segments.Length / 2; segment++)
        {
            int sa = segments[segment * 2], sb = segments[segment * 2 + 1];
            if (!candidateByCell.TryAdd(selected.Vertices[sa * 3], selected.Vertices[sa * 3 + 1], selected.Vertices[sa * 3 + 2], out int a) &&
                !candidateByCell.TryAdd(selected.Vertices[sb * 3], selected.Vertices[sb * 3 + 1], selected.Vertices[sb * 3 + 2], out int b) &&
                candidateEdges.Contains(IndexedMeshTools.GetEdgeKey(a, b)))
                continue;
            return false;
        }
        return true;
    }

    private static void AddLargestSourceErrors(
        double[] sourceVertices,
        bool[] used,
        int[] sourceToSelected,
        SelectedPoints selected,
        double[] candidateVertices,
        int[] candidateFaces,
        double maximumDeviation,
        double tolerance,
        CancellationProbe cancellation,
        int maxAdditions)
    {
        var projector = new MeshHeightProjector(candidateVertices, candidateVertices.Length / 3, candidateFaces, candidateFaces.Length / 3);
        var errors = new List<SourceError>();
        for (int source = 0; source < used.Length; source++)
        {
            cancellation.ThrowIfCancelledOften();
            if (!used[source] || sourceToSelected[source] >= 0) continue;
            double x = sourceVertices[source * 3], y = sourceVertices[source * 3 + 1], z = sourceVertices[source * 3 + 2];
            if (!projector.TryProjectZ(x, y, z, tolerance, out double projected, out MeshHeightProjector.ProjectionStatus status) ||
                status != MeshHeightProjector.ProjectionStatus.Projected) continue;
            double error = Math.Abs(z - projected);
            if (error > maximumDeviation) errors.Add(new SourceError(source, error));
        }
        // Grow geometrically so release-scale meshes do not spend most of the round budget
        // making tiny refinements. Ranking and tie order remain deterministic.
        int addCount = Math.Min(maxAdditions, Math.Max(32, selected.Count / 2));
        if (addCount <= 0)
            return;
        foreach (SourceError error in errors.OrderByDescending(value => value.Error).ThenBy(value => value.SourceIndex).Take(addCount))
        {
            selected.TryAddSource(error.SourceIndex, out int selectedIndex);
            sourceToSelected[error.SourceIndex] = selectedIndex;
        }
    }

    private static Result AcceptedInputForCount(
        double[] vertices,
        int[] faces,
        int usedVertexCount,
        int faceCount,
        int protectedVertexCount,
        int targetVertexCount) => new()
    {
        Vertices = vertices,
        Faces = faces,
        InputVertexCount = usedVertexCount,
        InputFaceCount = faceCount,
        ProtectedVertexCount = protectedVertexCount,
        MaximumDeviation = 0.0,
        HasEqualDomain = true,
        Rounds = 0,
        Termination = TerminationReason.TargetCountSatisfied,
        Diagnostic = $"The incoming {usedVertexCount:N0}-vertex mesh already satisfies the " +
            $"{targetVertexCount:N0}-vertex target and was kept unchanged."
    };

    private static Result FinalizeCountResult(Result best, int rounds, string stoppingDiagnostic) => new()
    {
        Vertices = best.Vertices,
        Faces = best.Faces,
        InputVertexCount = best.InputVertexCount,
        InputFaceCount = best.InputFaceCount,
        ProtectedVertexCount = best.ProtectedVertexCount,
        MaximumDeviation = best.MaximumDeviation,
        HasEqualDomain = best.HasEqualDomain,
        Rounds = rounds,
        Termination = TerminationReason.TargetCountSatisfied,
        Diagnostic = $"{best.Diagnostic} Search stopped after {rounds:N0} rounds: {stoppingDiagnostic}"
    };

    private static Result Fallback(
        double[] vertices,
        int[] faces,
        int protectedVertexCount,
        int rounds,
        TerminationReason termination,
        string diagnostic) => new()
    {
        Vertices = vertices,
        Faces = faces,
        InputVertexCount = vertices.Length / 3,
        InputFaceCount = faces.Length / 3,
        ProtectedVertexCount = protectedVertexCount,
        MaximumDeviation = 0.0,
        HasEqualDomain = true,
        Rounds = rounds,
        Termination = termination,
        Diagnostic = diagnostic
    };
}
