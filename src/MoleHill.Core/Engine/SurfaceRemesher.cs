using System.Diagnostics;
using System.Text;
using MoleHill.Core.Grading;
using TriangleNet.Geometry;
using TriangleNet.Meshing;
using static MoleHill.Core.Engine.MeshFlipGeometry;

namespace MoleHill.Core.Engine;

/// <summary>
/// Shared remesh pipeline for Rhino and Grasshopper.
/// Preserves mesh boundaries and constraint curves while limiting edge refinement along sharp features.
/// </summary>
public static class SurfaceRemesher
{
    public readonly record struct ConstraintPolyline(double[] Points, int PointCount, bool IsClosed, bool PreserveInputElevation = false);

    public readonly record struct TimingEntry(string Name, TimeSpan Elapsed);

    public sealed class Options
    {
        public double Tolerance { get; init; }

        public double RequestedEdgeLength { get; init; }

        public double MaxArea { get; init; }

        public double MinAngle { get; init; }

        public bool ProtectSharpEdges { get; init; } = true;

        /// <summary>
        /// When true, <see cref="RequestedEdgeLength"/> controls only constraint pre-densification
        /// spacing; it does NOT drive quality interior refinement (Steiner insertions).
        /// Use this for topology-insertion passes (Grade Path, Grade Pad) where the goal is to
        /// embed constraint edges into the mesh without re-triangulating the entire terrain.
        /// </summary>
        public bool ConstraintInsertionOnly { get; init; }

        /// <summary>
        /// When true, prefer the reduced interior seed pass (boundary + constraints + coarse guides)
        /// over the full upstream-mesh seed whenever the reduced pass succeeds.
        /// This is useful for corridor-style topology rebuilds where inherited mesh structure should
        /// not dominate the remeshed interior.
        /// </summary>
        public bool PreferReducedInteriorSeed { get; init; }

        /// <summary>
        /// When false, the reduced-seed fallback will not add guide seeds sampled from the original
        /// terrain interior. Use this when constraint corridor seeds are expected to fully define
        /// the rebuilt region and inherited terrain structure should not leak back in.
        /// </summary>
        public bool AddReducedInteriorGuideSeeds { get; init; } = true;

        /// <summary>
        /// When false, paired open constraints will not inject extra seed rows across the corridor
        /// between them. Use this when the supplied constraints are already sparsified enough for
        /// the remesh pass and additional corridor seeding would over-refine the interior.
        /// </summary>
        public bool AddConstraintCorridorSeeds { get; init; } = true;

        /// <summary>
        /// Optional soft polylines that seed vertices into the triangulation without adding
        /// constrained segments. Use these to preserve local grading detail where hard
        /// constraints would cross or over-constrain the network.
        /// </summary>
        public IReadOnlyList<ConstraintPolyline> GuidePolylines { get; init; } = Array.Empty<ConstraintPolyline>();

        /// <summary>
        /// When &gt; 0, near-coincident input vertices (and constraint vertices) within this distance are
        /// merged before triangulation — a "merge by distance" / short-edge collapse. Grading often leaves
        /// near-duplicate vertices at batter toes; without this the remesh keeps both and *protects* the
        /// tiny edge between them, which refinement then fans into a pinched cone. This is an explicit
        /// decimation threshold (larger than the model tolerance), so it overrides the constraint-chain
        /// dedup cap. 0 leaves the default tolerance-only dedup.
        /// </summary>
        public double VertexMergeTolerance { get; init; }

        /// <summary>
        /// When &gt; 0, interior edges whose adjacent faces meet at a dihedral angle of at least this many
        /// degrees (a crease — e.g. a batter TOE where the slope meets terrain) are constrained for this
        /// remesh so the triangulation can't flip across them and spike the feature. These crease
        /// constraints are detected from the input geometry every pass and are NOT persisted, so stacking
        /// grades does not accumulate breaklines. 0 disables it.
        /// </summary>
        public double PreserveCreaseAngleDeg { get; init; }
    }

    public sealed class TimingProfile
    {
        private readonly List<TimingEntry> _entries = new();

        public IReadOnlyList<TimingEntry> Entries => _entries;

        public bool Success { get; private set; }

        public bool ReturnedInputMesh { get; private set; }

        public bool UsedBoundaryAndGuideSeedFallback { get; private set; }

        public string SelectedAttempt { get; private set; } = "none";

        internal void AddPhase(string name, TimeSpan elapsed)
        {
            _entries.Add(new TimingEntry(name, elapsed));
        }

        internal void SetOutcome(bool success, bool returnedInputMesh, bool usedBoundaryAndGuideSeedFallback, string selectedAttempt)
        {
            Success = success;
            ReturnedInputMesh = returnedInputMesh;
            UsedBoundaryAndGuideSeedFallback = usedBoundaryAndGuideSeedFallback;
            SelectedAttempt = selectedAttempt;
        }

        public string FormatReport(string? header = null)
        {
            var builder = new StringBuilder();
            builder.AppendLine(header ?? "Surface remesh timing");
            builder.Append("outcome: ");
            builder.Append(Success ? "success" : "failure");
            builder.AppendLine();
            builder.Append("selected_attempt: ");
            builder.Append(SelectedAttempt);
            builder.AppendLine();
            builder.Append("returned_input_mesh: ");
            builder.Append(ReturnedInputMesh ? "yes" : "no");
            builder.AppendLine();
            builder.Append("used_fallback: ");
            builder.Append(UsedBoundaryAndGuideSeedFallback ? "yes" : "no");

            for (int i = 0; i < _entries.Count; i++)
            {
                builder.AppendLine();
                builder.Append(_entries[i].Name);
                builder.Append(": ");
                builder.Append(_entries[i].Elapsed.TotalMilliseconds.ToString("0.###"));
                builder.Append(" ms");
            }

            return builder.ToString();
        }
    }

    public sealed class Result
    {
        public bool Success { get; init; }

        public double[] Vertices { get; init; } = Array.Empty<double>();

        public int[] Faces { get; init; } = Array.Empty<int>();

        public string? Warning { get; init; }

        public bool UsedBoundaryAndGuideSeedFallback { get; init; }

        public bool ReturnedInputMesh { get; init; }

        public int AddedProtectedVertices { get; init; }

        public TimingProfile? Profile { get; init; }
    }

    private sealed class PreparedInput
    {
        public required List<double> XY { get; init; }

        public required List<double> Z { get; init; }

        public required List<(int a, int b)> Segments { get; init; }

        public required int AddedProtectedVertices { get; init; }

        public required bool UsesFullOriginalVertexSeed { get; init; }
    }

    private readonly record struct TriangulationAttempt(IMesh? Mesh, string? Warning, TriangulationWarningFlags Flags);
    private readonly record struct Segment2D(double Ax, double Ay, double Bx, double By);

    private sealed class SegmentSpatialIndex
    {
        private readonly IReadOnlyList<Segment2D> _segments;
        private readonly SpatialHashGrid2D _grid;
        private readonly List<int> _candidates = new(16);
        private readonly SpatialHashGrid2D.QueryScratch _scratch;

        private SegmentSpatialIndex(IReadOnlyList<Segment2D> segments, SpatialHashGrid2D grid)
        {
            _segments = segments;
            _grid = grid;
            _scratch = new SpatialHashGrid2D.QueryScratch(segments.Count);
        }

        public int Count => _segments.Count;

        public static SegmentSpatialIndex Build(IReadOnlyList<Segment2D> segments, double padding)
        {
            var bounds = new Bounds2D[segments.Count];
            for (int i = 0; i < segments.Count; i++)
                bounds[i] = GetSegmentBounds(segments[i], padding);

            return new SegmentSpatialIndex(segments, SpatialHashGrid2D.Build(bounds));
        }

        public double DistanceSquaredToAnySegment(double x, double y)
        {
            if (_segments.Count == 0)
                return double.PositiveInfinity;

            _grid.GatherCandidates(Bounds2D.FromPoint(x, y), _candidates, _scratch);
            double best = double.PositiveInfinity;
            for (int i = 0; i < _candidates.Count; i++)
            {
                best = Math.Min(best, DistanceSquaredToSegment(x, y, _segments[_candidates[i]]));
                if (best <= 0.0)
                    return 0.0;
            }

            return best;
        }

        private static Bounds2D GetSegmentBounds(Segment2D segment, double padding)
        {
            return new Bounds2D(
                Math.Min(segment.Ax, segment.Bx) - padding,
                Math.Max(segment.Ax, segment.Bx) + padding,
                Math.Min(segment.Ay, segment.By) - padding,
                Math.Max(segment.Ay, segment.By) + padding);
        }
    }

    private sealed class NearVertexIndex
    {
        private readonly List<double> _xyList;
        private readonly Dictionary<long, List<int>> _cells = new(IndexedMeshTools.CellKeyComparer.Instance);
        private readonly double _cellSize;
        private readonly double _inverseCellSize;

        public NearVertexIndex(List<double> xyList, double cellSize)
        {
            _xyList = xyList;
            _cellSize = Math.Max(cellSize, 1e-9);
            _inverseCellSize = 1.0 / _cellSize;
        }

        public void Add(int index)
        {
            long key = CellKey(_xyList[index * 2], _xyList[index * 2 + 1]);
            if (!_cells.TryGetValue(key, out List<int>? indexes))
            {
                indexes = new List<int>(2);
                _cells.Add(key, indexes);
            }

            indexes.Add(index);
        }

        public int Find(double x, double y, double tolerance)
        {
            if (_cells.Count == 0)
                return -1;

            double toleranceSquared = tolerance * tolerance;
            double bestDistanceSquared = toleranceSquared;
            int bestIndex = -1;
            long centerX = (long)Math.Floor(x * _inverseCellSize);
            long centerY = (long)Math.Floor(y * _inverseCellSize);
            long radius = Math.Max(1, (long)Math.Ceiling(tolerance / _cellSize));

            for (long cellY = centerY - radius; cellY <= centerY + radius; cellY++)
            {
                for (long cellX = centerX - radius; cellX <= centerX + radius; cellX++)
                {
                    if (!_cells.TryGetValue(PackCellKey(cellX, cellY), out List<int>? indexes))
                        continue;

                    foreach (int index in indexes)
                    {
                        double dx = _xyList[index * 2] - x;
                        double dy = _xyList[index * 2 + 1] - y;
                        double distanceSquared = (dx * dx) + (dy * dy);
                        if (distanceSquared < bestDistanceSquared ||
                            (distanceSquared == bestDistanceSquared && (bestIndex < 0 || index < bestIndex)))
                        {
                            bestDistanceSquared = distanceSquared;
                            bestIndex = index;
                        }
                    }
                }
            }

            return bestIndex;
        }

        private long CellKey(double x, double y)
        {
            return PackCellKey(
                (long)Math.Floor(x * _inverseCellSize),
                (long)Math.Floor(y * _inverseCellSize));
        }
    }

    private sealed class AttemptEvaluation
    {
        public required PreparedInput Prepared { get; init; }

        public double[] Vertices { get; init; } = Array.Empty<double>();

        public int[] Faces { get; init; } = Array.Empty<int>();

        public string? Warning { get; init; }

        public bool ConstraintsDropped { get; init; }

        public bool QualityDropped { get; init; }

        public bool TopologyInvalid { get; init; }

        public bool ConstraintApronInvalid { get; init; }

        public bool PerimeterApronInvalid { get; init; }

        public double MaxConstraintTriangleArea { get; init; }

        public double MaxPerimeterTriangleArea { get; init; }

        public bool Accepted { get; init; }
    }

    private readonly record struct PreservedConstraintSegment(
        double Ax,
        double Ay,
        double Az,
        double Bx,
        double By,
        double Bz);

    private sealed class PreservedConstraintSegmentIndex
    {
        private readonly List<PreservedConstraintSegment> _segments;
        private readonly Dictionary<long, List<int>> _cells;
        private readonly double _cellSize;
        private readonly double _inverseCellSize;

        public PreservedConstraintSegmentIndex(List<PreservedConstraintSegment> segments, Dictionary<long, List<int>> cells, double cellSize)
        {
            _segments = segments;
            _cells = cells;
            _cellSize = cellSize;
            _inverseCellSize = 1.0 / cellSize;
        }

        public bool TryGetElevation(double x, double y, double maxDistanceSquared, out double z)
        {
            z = 0.0;
            long key = PackCellKey(
                (long)Math.Floor(x * _inverseCellSize),
                (long)Math.Floor(y * _inverseCellSize));
            if (!_cells.TryGetValue(key, out List<int>? segmentIndexes))
                return false;

            bool found = false;
            double bestDistanceSquared = maxDistanceSquared;
            foreach (int segmentIndex in segmentIndexes)
            {
                PreservedConstraintSegment segment = _segments[segmentIndex];
                if (!TryProjectToConstraintSegment(
                        segment,
                        x,
                        y,
                        bestDistanceSquared,
                        out double candidateZ,
                        out double distanceSquared))
                {
                    continue;
                }

                bestDistanceSquared = distanceSquared;
                z = candidateZ;
                found = true;
            }

            return found;
        }

        public static PreservedConstraintSegmentIndex? Build(IReadOnlyList<ConstraintPolyline> constraints, double tolerance)
        {
            var segments = new List<PreservedConstraintSegment>();
            double minX = double.MaxValue;
            double minY = double.MaxValue;
            double maxX = double.MinValue;
            double maxY = double.MinValue;

            foreach (var constraint in constraints)
            {
                if (!constraint.PreserveInputElevation)
                    continue;

                int pointCount = NormalizePointCount(constraint, tolerance);
                if (pointCount < 2)
                    continue;

                for (int pointIndex = 1; pointIndex < pointCount; pointIndex++)
                    AddSegment(constraint, pointIndex - 1, pointIndex, segments, ref minX, ref minY, ref maxX, ref maxY);

                if (constraint.IsClosed)
                    AddSegment(constraint, pointCount - 1, 0, segments, ref minX, ref minY, ref maxX, ref maxY);
            }

            if (segments.Count == 0)
                return null;

            double span = Math.Max(maxX - minX, maxY - minY);
            double minCellSize = tolerance * 16.0;
            double cellSize = Math.Clamp(span / 256.0, minCellSize, Math.Max(minCellSize, 4.0));
            var cells = new Dictionary<long, List<int>>(segments.Count * 2, IndexedMeshTools.CellKeyComparer.Instance);
            for (int i = 0; i < segments.Count; i++)
                AddSegmentToCells(cells, segments[i], i, tolerance, cellSize);

            return new PreservedConstraintSegmentIndex(segments, cells, cellSize);
        }

        private static void AddSegment(
            ConstraintPolyline constraint,
            int startPointIndex,
            int endPointIndex,
            List<PreservedConstraintSegment> segments,
            ref double minX,
            ref double minY,
            ref double maxX,
            ref double maxY)
        {
            var segment = new PreservedConstraintSegment(
                constraint.Points[startPointIndex * 3],
                constraint.Points[startPointIndex * 3 + 1],
                constraint.Points[startPointIndex * 3 + 2],
                constraint.Points[endPointIndex * 3],
                constraint.Points[endPointIndex * 3 + 1],
                constraint.Points[endPointIndex * 3 + 2]);

            double lengthSquared = DistanceSquared(segment.Ax, segment.Ay, segment.Bx, segment.By);
            if (lengthSquared <= 1e-18)
                return;

            segments.Add(segment);
            minX = Math.Min(minX, Math.Min(segment.Ax, segment.Bx));
            minY = Math.Min(minY, Math.Min(segment.Ay, segment.By));
            maxX = Math.Max(maxX, Math.Max(segment.Ax, segment.Bx));
            maxY = Math.Max(maxY, Math.Max(segment.Ay, segment.By));
        }

        private static void AddSegmentToCells(
            Dictionary<long, List<int>> cells,
            PreservedConstraintSegment segment,
            int segmentIndex,
            double tolerance,
            double cellSize)
        {
            double invCellSize = 1.0 / cellSize;
            long minCellX = (long)Math.Floor((Math.Min(segment.Ax, segment.Bx) - tolerance) * invCellSize);
            long minCellY = (long)Math.Floor((Math.Min(segment.Ay, segment.By) - tolerance) * invCellSize);
            long maxCellX = (long)Math.Floor((Math.Max(segment.Ax, segment.Bx) + tolerance) * invCellSize);
            long maxCellY = (long)Math.Floor((Math.Max(segment.Ay, segment.By) + tolerance) * invCellSize);

            for (long cy = minCellY; cy <= maxCellY; cy++)
            {
                for (long cx = minCellX; cx <= maxCellX; cx++)
                {
                    long key = PackCellKey(cx, cy);
                    if (!cells.TryGetValue(key, out List<int>? indexes))
                    {
                        indexes = new List<int>();
                        cells.Add(key, indexes);
                    }

                    indexes.Add(segmentIndex);
                }
            }
        }
    }

    public static Result Remesh(
        double[] originalVertices,
        int[] originalFaces,
        IReadOnlyList<ConstraintPolyline> constraints,
        Options options)
    {
        var profile = new TimingProfile();
        long totalStart = Stopwatch.GetTimestamp();

        try
        {
            int originalVertexCount = originalVertices.Length / 3;
            int faceCount = originalFaces.Length / 3;

            if (originalVertexCount == 0 || faceCount == 0)
            {
                profile.SetOutcome(success: false, returnedInputMesh: false, usedBoundaryAndGuideSeedFallback: false, selectedAttempt: "none");
                return new Result
                {
                    Success = false,
                    Warning = "Input mesh has no usable triangles.",
                    Profile = profile
                };
            }

            AttemptEvaluation? fallbackAttempt = null;
            var fallbackOptions = CreateBoundaryAndGuideSeedFallbackOptions(options);
            long fallbackPrepareStart = Stopwatch.GetTimestamp();
            var preparedFallback = PrepareInput(originalVertices, originalFaces, constraints, fallbackOptions, seedInteriorVertices: false);
            profile.AddPhase("fallback.prepare_input", Stopwatch.GetElapsedTime(fallbackPrepareStart));
            if (!preparedFallback.UsesFullOriginalVertexSeed)
            {
                fallbackAttempt = EvaluateAttempt(
                    originalVertices,
                    originalFaces,
                    faceCount,
                    constraints,
                    fallbackOptions,
                    seedInteriorVertices: false,
                    profile,
                    "fallback",
                    precomputedPrepared: preparedFallback);

                if (options.PreferReducedInteriorSeed && fallbackAttempt.Accepted)
                {
                    profile.SetOutcome(success: true, returnedInputMesh: false, usedBoundaryAndGuideSeedFallback: true, selectedAttempt: "fallback");
                    return BuildAcceptedResult(fallbackAttempt, usedBoundaryAndGuideSeedFallback: true, profile);
                }
            }

            var firstAttempt = EvaluateAttempt(
                originalVertices,
                originalFaces,
                faceCount,
                constraints,
                options,
                seedInteriorVertices: true,
                profile,
                "initial");

            if (!firstAttempt.Accepted && fallbackAttempt?.Accepted == true)
            {
                profile.SetOutcome(success: true, returnedInputMesh: false, usedBoundaryAndGuideSeedFallback: true, selectedAttempt: "fallback");
                return BuildAcceptedResult(fallbackAttempt, usedBoundaryAndGuideSeedFallback: true, profile);
            }

            if (firstAttempt.Accepted)
            {
                if (fallbackAttempt is not null &&
                    fallbackAttempt.Accepted &&
                    (options.PreferReducedInteriorSeed || ShouldPreferBoundaryAndGuideSeedFallback(firstAttempt, fallbackAttempt, GetEffectiveMaxArea(options))))
                {
                    profile.SetOutcome(success: true, returnedInputMesh: false, usedBoundaryAndGuideSeedFallback: true, selectedAttempt: "fallback");
                    return BuildAcceptedResult(fallbackAttempt, usedBoundaryAndGuideSeedFallback: true, profile);
                }

                profile.SetOutcome(success: true, returnedInputMesh: false, usedBoundaryAndGuideSeedFallback: false, selectedAttempt: "initial");
                return BuildAcceptedResult(firstAttempt, usedBoundaryAndGuideSeedFallback: false, profile);
            }

            profile.SetOutcome(success: false, returnedInputMesh: true, usedBoundaryAndGuideSeedFallback: !preparedFallback.UsesFullOriginalVertexSeed, selectedAttempt: "preserved_input");
            return BuildPreservedInputResult(
                originalVertices,
                originalFaces,
                firstAttempt,
                fallbackAttempt,
                !preparedFallback.UsesFullOriginalVertexSeed,
                profile);
        }
        finally
        {
            profile.AddPhase("total", Stopwatch.GetElapsedTime(totalStart));
        }
    }

    private static PreparedInput PrepareInput(
        double[] originalVertices,
        int[] originalFaces,
        IReadOnlyList<ConstraintPolyline> constraints,
        Options options,
        bool seedInteriorVertices)
    {
        int originalVertexCount = originalVertices.Length / 3;
        int faceCount = originalFaces.Length / 3;

        var segments = new List<(int a, int b)>();
        var segmentKeys = IndexedMeshTools.CreateEdgeKeySet();
        var boundarySegments = new List<(int a, int b)>();
        MeshConstraintTools.AddBoundarySegments(boundarySegments, IndexedMeshTools.CreateEdgeKeySet(), originalFaces, faceCount);

        bool usesFullOriginalVertexSeed = seedInteriorVertices || boundarySegments.Count == 0;
        var xyList = new List<double>(usesFullOriginalVertexSeed ? originalVertexCount * 2 : Math.Max(boundarySegments.Count * 4, 8));
        var zList = new List<double>(usesFullOriginalVertexSeed ? originalVertexCount : Math.Max(boundarySegments.Count * 2, 4));
        var originalIndexMap = new Dictionary<int, int>(usesFullOriginalVertexSeed
            ? originalVertexCount
            : Math.Min(originalVertexCount, Math.Max(boundarySegments.Count * 2, 8)));
        double targetLength = GetProtectedEdgeLength(options);
        double floorLength = Math.Max(options.Tolerance * 4.0, 1e-6);
        double seedReuseTolerance = Math.Max(options.Tolerance, 1e-6);
        if (targetLength > 0)
            seedReuseTolerance = Math.Min(seedReuseTolerance, targetLength * 0.1);
        double dedupTolerance = Math.Max(options.Tolerance, 1e-6);
        // Cap dedup radius to a fraction of the target edge length so that tolerance values
        // large relative to edge length (e.g. 0.5 m tolerance + 1 m edge length) do not snap
        // adjacent constraint vertices together, which would collapse constraint chains and
        // cause "Constraints could not be enforced" failures.
        if (targetLength > 0)
            dedupTolerance = Math.Min(dedupTolerance, targetLength * 0.25);
        // Merge-by-distance: an explicit decimation threshold collapses near-duplicate input/constraint
        // vertices (e.g. batter-toe pinches) that the model-tolerance dedup leaves distinct. It overrides
        // the chain-protection caps above because it is a deliberate user request, not identity dedup.
        if (options.VertexMergeTolerance > 0)
        {
            seedReuseTolerance = Math.Max(seedReuseTolerance, options.VertexMergeTolerance);
            dedupTolerance = Math.Max(dedupTolerance, options.VertexMergeTolerance);
        }

        double exactReuseTolerance = Math.Max(Math.Min(dedupTolerance * 0.01, 1e-6), 1e-9);
        var nearVertices = new NearVertexIndex(xyList, Math.Max(seedReuseTolerance, dedupTolerance));

        bool requiresInterpolatedConstraintZ = options.AddConstraintCorridorSeeds || options.GuidePolylines.Count > 0;
        foreach (var constraint in constraints)
        {
            if (constraint.PreserveInputElevation)
                continue;

            requiresInterpolatedConstraintZ = true;
            break;
        }

        TerrainFaceGrid? faceGrid = requiresInterpolatedConstraintZ
            ? new TerrainFaceGrid(originalVertices, originalVertexCount, originalFaces, faceCount)
            : null;

        int EnsureSeedVertex(int originalIndex)
        {
            if (originalIndexMap.TryGetValue(originalIndex, out int existing))
                return existing;

            double x = originalVertices[originalIndex * 3];
            double y = originalVertices[originalIndex * 3 + 1];
            double z = originalVertices[originalIndex * 3 + 2];

            int nearIndex = nearVertices.Find(x, y, seedReuseTolerance);
            if (nearIndex >= 0)
            {
                originalIndexMap.Add(originalIndex, nearIndex);
                return nearIndex;
            }

            int newIndex = zList.Count;
            xyList.Add(x);
            xyList.Add(y);
            zList.Add(z);
            nearVertices.Add(newIndex);
            originalIndexMap.Add(originalIndex, newIndex);
            return newIndex;
        }

        if (usesFullOriginalVertexSeed)
        {
            for (int i = 0; i < originalVertexCount; i++)
                EnsureSeedVertex(i);
        }

        int addedProtectedVertices = 0;

        foreach (var (a, b) in boundarySegments)
        {
            int startIndex = EnsureSeedVertex(a);
            int endIndex = EnsureSeedVertex(b);
            if (options.ProtectSharpEdges && targetLength > 0)
            {
                AddBoundarySegmentChain(
                    xyList,
                    zList,
                    segments,
                    segmentKeys,
                    nearVertices,
                    startIndex,
                    endIndex,
                    targetLength,
                    floorLength,
                    ref addedProtectedVertices);
            }
            else
            {
                MeshConstraintTools.TryAddSegment(segments, segmentKeys, startIndex, endIndex);
            }
        }

        foreach (var constraint in constraints)
        {
            int normalizedCount = NormalizePointCount(constraint, options.Tolerance);
            if (normalizedCount < 2)
                continue;

            int lastIndex = ResolveConstraintVertex(
                xyList,
                zList,
                originalVertices,
                originalFaces,
                faceCount,
                faceGrid,
                constraint,
                0,
                dedupTolerance,
                exactReuseTolerance,
                nearVertices,
                allowBroadReuse: true);

            for (int pointIndex = 1; pointIndex < normalizedCount; pointIndex++)
            {
                int nextIndex = ResolveConstraintVertex(
                    xyList,
                    zList,
                    originalVertices,
                    originalFaces,
                    faceCount,
                    faceGrid,
                    constraint,
                    pointIndex,
                    dedupTolerance,
                    exactReuseTolerance,
                    nearVertices,
                    allowBroadReuse: true);

                AddConstraintSegmentChain(
                    xyList,
                    zList,
                    segments,
                    segmentKeys,
                    nearVertices,
                    originalVertices,
                    originalFaces,
                    faceCount,
                    faceGrid,
                    constraint,
                    pointIndex - 1,
                    pointIndex,
                    lastIndex,
                    nextIndex,
                    targetLength,
                    floorLength,
                    exactReuseTolerance,
                    options.ProtectSharpEdges,
                    ref addedProtectedVertices);

                lastIndex = nextIndex;
            }

            if (constraint.IsClosed)
            {
                int closingIndex = ResolveConstraintVertex(
                    xyList,
                    zList,
                    originalVertices,
                    originalFaces,
                    faceCount,
                    faceGrid,
                    constraint,
                    0,
                    dedupTolerance,
                    exactReuseTolerance,
                    nearVertices,
                    allowBroadReuse: true);

                AddConstraintSegmentChain(
                    xyList,
                    zList,
                    segments,
                    segmentKeys,
                    nearVertices,
                    originalVertices,
                    originalFaces,
                    faceCount,
                    faceGrid,
                    constraint,
                    normalizedCount - 1,
                    0,
                    lastIndex,
                    closingIndex,
                    targetLength,
                    floorLength,
                    exactReuseTolerance,
                    options.ProtectSharpEdges,
                    ref addedProtectedVertices);
            }
        }

        // Transient crease preservation: pin sharp feature edges (batter toes, slope breaks) so the
        // re-triangulation keeps them smooth instead of flipping across them. Detected from geometry each
        // pass and never persisted — stacking grades does not accumulate breaklines. Runs in BOTH the full
        // and reduced-seed passes (EnsureSeedVertex adds any crease endpoint the reduced seed dropped), so
        // enabling MaxArea — which often makes the remesh prefer the reduced-seed pass — keeps the creases.
        if (options.PreserveCreaseAngleDeg > 0)
        {
            double cosThreshold = Math.Cos(Math.Clamp(options.PreserveCreaseAngleDeg, 1.0, 179.0) * Math.PI / 180.0);
            foreach ((int a, int b) in DetectCreaseEdges(originalVertices, originalFaces, faceCount, cosThreshold))
                MeshConstraintTools.TryAddSegment(segments, segmentKeys, EnsureSeedVertex(a), EnsureSeedVertex(b));
        }

        if (options.GuidePolylines.Count > 0)
        {
            AddGuidePolylineSeeds(
                xyList,
                zList,
                originalVertices,
                originalFaces,
                faceCount,
                faceGrid,
                options.GuidePolylines,
                targetLength,
                floorLength,
                nearVertices,
                dedupTolerance);
        }

        if (options.AddConstraintCorridorSeeds)
        {
            AddConstraintCorridorSeeds(
                xyList,
                zList,
                originalVertices,
                originalFaces,
                faceCount,
                faceGrid,
                constraints,
                targetLength,
                floorLength,
                nearVertices,
                dedupTolerance);
        }

        if (!usesFullOriginalVertexSeed && options.AddReducedInteriorGuideSeeds)
        {
            AddReducedInteriorGuideSeeds(
                xyList,
                zList,
                originalVertices,
                originalFaces,
                faceCount,
                targetLength,
                options.Tolerance);
        }

        return new PreparedInput
        {
            XY = xyList,
            Z = zList,
            Segments = segments,
            AddedProtectedVertices = addedProtectedVertices,
            UsesFullOriginalVertexSeed = usesFullOriginalVertexSeed
        };
    }

    private static TriangulationAttempt TriangulatePrepared(
        PreparedInput prepared,
        double effectiveMaxArea,
        Options options)
    {
        // segmentSplitting=2 when ProtectSharpEdges: pre-subdivision already placed vertices
        // at target spacing so Triangle.NET doesn't need to split them further. This prevents
        // the cascade that occurs with free splitting near narrow corridors (retaining walls).
        var triangulation = TriangulationHelper.Triangulate(
            prepared.XY,
            prepared.Z.Count,
            prepared.Segments,
            effectiveMaxArea,
            options.MinAngle,
            convex: false,
            segmentSplitting: options.ProtectSharpEdges ? 2 : 0);

        // If ProtectSharpEdges=2 drops constraints (tight parallel breaklines like retaining
        // walls), fall back to free splitting. The reference-equality vertex collection handles
        // Triangle.NET's split-vertex bug; the SteinerPoints cap in TriangulationHelper
        // prevents cascade freezing in the fallback path.
        if (options.ProtectSharpEdges &&
            (triangulation.Mesh == null || MeshConstraintTools.ConstraintsWereDropped(triangulation.Flags)))
        {
            triangulation = TriangulationHelper.Triangulate(
                prepared.XY,
                prepared.Z.Count,
                prepared.Segments,
                effectiveMaxArea,
                options.MinAngle,
                convex: false,
                segmentSplitting: 0);
        }

        return new TriangulationAttempt(triangulation.Mesh, triangulation.WarningMessage, triangulation.Flags);
    }

    private static Result BuildAcceptedResult(AttemptEvaluation attempt, bool usedBoundaryAndGuideSeedFallback, TimingProfile profile)
    {
        return new Result
        {
            Success = true,
            Vertices = attempt.Vertices,
            Faces = attempt.Faces,
            Warning = attempt.Warning,
            UsedBoundaryAndGuideSeedFallback = usedBoundaryAndGuideSeedFallback,
            AddedProtectedVertices = attempt.Prepared.AddedProtectedVertices,
            Profile = profile
        };
    }

    private static Result BuildPreservedInputResult(
        double[] originalVertices,
        int[] originalFaces,
        AttemptEvaluation firstAttempt,
        AttemptEvaluation? fallbackAttempt,
        bool attemptedBoundaryAndGuideSeedFallback,
        TimingProfile profile)
    {
        return new Result
        {
            Success = false,
            Vertices = originalVertices.ToArray(),
            Faces = originalFaces.ToArray(),
            Warning = BuildPreservedInputWarning(firstAttempt, fallbackAttempt, attemptedBoundaryAndGuideSeedFallback),
            UsedBoundaryAndGuideSeedFallback = attemptedBoundaryAndGuideSeedFallback,
            ReturnedInputMesh = true,
            AddedProtectedVertices = fallbackAttempt is null
                ? firstAttempt.Prepared.AddedProtectedVertices
                : fallbackAttempt.Prepared.AddedProtectedVertices,
            Profile = profile
        };
    }

    /// <summary>
    /// Interior edges whose two faces meet at a dihedral angle ≥ the threshold (cos ≤
    /// <paramref name="cosThreshold"/>) — the mesh's crease/feature lines (batter toes, slope breaks).
    /// Returned as original-vertex index pairs. Boundary edges (one face) are excluded; they are already
    /// constrained as the mesh outline.
    /// </summary>
    internal static List<(int a, int b)> DetectCreaseEdges(double[] vertices, int[] faces, int faceCount, double cosThreshold)
    {
        // Edge keys are (min << 32) | max, so the default long hash (lo ^ hi) collapses adjacent
        // mesh indices into a handful of buckets and degrades this to a quadratic scan. Every
        // edge-keyed table must use EdgeKeyComparer.
        var edgeFaces = new Dictionary<long, (int f0, int f1, int count)>(faceCount * 2, IndexedMeshTools.EdgeKeyComparer.Instance);
        for (int f = 0; f < faceCount; f++)
        {
            int v0 = faces[f * 3], v1 = faces[f * 3 + 1], v2 = faces[f * 3 + 2];
            Accumulate(v0, v1, f);
            Accumulate(v1, v2, f);
            Accumulate(v2, v0, f);
        }

        var creases = new List<(int, int)>();
        foreach (KeyValuePair<long, (int f0, int f1, int count)> entry in edgeFaces)
        {
            if (entry.Value.count != 2)
                continue;

            FaceNormal(vertices, faces, entry.Value.f0, out double n0x, out double n0y, out double n0z);
            FaceNormal(vertices, faces, entry.Value.f1, out double n1x, out double n1y, out double n1z);
            double l0 = Math.Sqrt((n0x * n0x) + (n0y * n0y) + (n0z * n0z));
            double l1 = Math.Sqrt((n1x * n1x) + (n1y * n1y) + (n1z * n1z));
            if (l0 <= 1e-18 || l1 <= 1e-18)
                continue;

            double cos = ((n0x * n1x) + (n0y * n1y) + (n0z * n1z)) / (l0 * l1);
            if (cos <= cosThreshold)
                creases.Add(((int)(entry.Key >> 32), (int)(entry.Key & 0xFFFFFFFFL)));
        }

        return creases;

        void Accumulate(int a, int b, int face)
        {
            long key = ((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b);
            if (!edgeFaces.TryGetValue(key, out (int f0, int f1, int count) e))
                edgeFaces[key] = (face, -1, 1);
            else if (e.count == 1)
                edgeFaces[key] = (e.f0, face, 2);
            else
                edgeFaces[key] = (e.f0, e.f1, e.count + 1);
        }
    }

    private static void FaceNormal(double[] v, int[] faces, int face, out double nx, out double ny, out double nz)
    {
        int a = faces[face * 3], b = faces[face * 3 + 1], c = faces[face * 3 + 2];
        double ux = v[b * 3] - v[a * 3], uy = v[b * 3 + 1] - v[a * 3 + 1], uz = v[b * 3 + 2] - v[a * 3 + 2];
        double wx = v[c * 3] - v[a * 3], wy = v[c * 3 + 1] - v[a * 3 + 1], wz = v[c * 3 + 2] - v[a * 3 + 2];
        nx = (uy * wz) - (uz * wy);
        ny = (uz * wx) - (ux * wz);
        nz = (ux * wy) - (uy * wx);
    }

    private static AttemptEvaluation EvaluateAttempt(
        double[] originalVertices,
        int[] originalFaces,
        int faceCount,
        IReadOnlyList<ConstraintPolyline> constraints,
        Options options,
        bool seedInteriorVertices,
        TimingProfile profile,
        string attemptName,
        PreparedInput? precomputedPrepared = null)
    {
        long attemptStart = Stopwatch.GetTimestamp();

        try
        {
            double effectiveMaxArea = GetEffectiveMaxArea(options);
            bool requiresQuality = effectiveMaxArea > 0 || options.MinAngle > 0;

            PreparedInput prepared;
            if (precomputedPrepared is not null)
            {
                prepared = precomputedPrepared;
            }
            else
            {
                long prepareStart = Stopwatch.GetTimestamp();
                prepared = PrepareInput(originalVertices, originalFaces, constraints, options, seedInteriorVertices);
                profile.AddPhase($"{attemptName}.prepare_input", Stopwatch.GetElapsedTime(prepareStart));
            }

            long triangulateStart = Stopwatch.GetTimestamp();
            var triangulation = TriangulatePrepared(prepared, effectiveMaxArea, options);
            profile.AddPhase($"{attemptName}.triangulate", Stopwatch.GetElapsedTime(triangulateStart));

            if (triangulation.Mesh == null)
            {
                return new AttemptEvaluation
                {
                    Prepared = prepared,
                    Warning = triangulation.Warning ?? "Triangulation failed."
                };
            }

            if (MeshConstraintTools.ConstraintsWereDropped(triangulation.Flags))
            {
                return new AttemptEvaluation
                {
                    Prepared = prepared,
                    Warning = triangulation.Warning ?? "Constraints could not be preserved.",
                    ConstraintsDropped = true
                };
            }

            if (requiresQuality && MeshConstraintTools.QualityWasDropped(triangulation.Flags))
            {
                return new AttemptEvaluation
                {
                    Prepared = prepared,
                    Warning = triangulation.Warning ?? "Requested remesh refinement could not be satisfied.",
                    QualityDropped = true
                };
            }

            var mesh = triangulation.Mesh;
            if (mesh.Triangles.Count == 0)
            {
                return new AttemptEvaluation
                {
                    Prepared = prepared,
                    Warning = "Triangulation produced 0 triangles."
                };
            }

            long extractStart = Stopwatch.GetTimestamp();
            var extracted = TriangleNetExtractor.Extract(mesh);
            profile.AddPhase($"{attemptName}.extract_mesh", Stopwatch.GetElapsedTime(extractStart));

            long interpolateStart = Stopwatch.GetTimestamp();
            var outputVertices = new double[extracted.VertexCount * 3];
            TerrainFaceGrid? interpolationGrid = null;
            for (int i = 0; i < extracted.VertexCount; i++)
            {
                double x = extracted.Xy[i * 2];
                double y = extracted.Xy[i * 2 + 1];
                int sourceId = extracted.SourceIds[i];
                outputVertices[i * 3] = x;
                outputVertices[i * 3 + 1] = y;
                if (sourceId >= 0 && sourceId < prepared.Z.Count)
                {
                    outputVertices[i * 3 + 2] = prepared.Z[sourceId];
                }
                else
                {
                    interpolationGrid ??= new TerrainFaceGrid(originalVertices, originalVertices.Length / 3, originalFaces, faceCount);
                    outputVertices[i * 3 + 2] = interpolationGrid.InterpolateZ(x, y);
                }
            }
            profile.AddPhase($"{attemptName}.interpolate_output_z", Stopwatch.GetElapsedTime(interpolateStart));

            long preserveConstraintZStart = Stopwatch.GetTimestamp();
            ApplyPreservedConstraintElevations(outputVertices, constraints, options.Tolerance);
            profile.AddPhase($"{attemptName}.apply_preserved_constraint_z", Stopwatch.GetElapsedTime(preserveConstraintZStart));

            long topologyStart = Stopwatch.GetTimestamp();
            var topology = MeshTopologyValidator.AnalyzeBoundaryGraph(extracted.Faces, extracted.FaceCount);
            profile.AddPhase($"{attemptName}.topology_validation", Stopwatch.GetElapsedTime(topologyStart));

            bool topologyInvalid = !topology.HasSingleClosedBoundaryLoop;
            bool constraintApronInvalid = false;
            bool perimeterApronInvalid = false;
            double maxConstraintTriangleArea = 0.0;
            double maxPerimeterTriangleArea = 0.0;
            double qualityLength = GetProtectedEdgeLength(options);

            if (qualityLength > 0 && effectiveMaxArea > 0)
            {
                long buildAreaCheckInputsStart = Stopwatch.GetTimestamp();
                var perimeterSegments = BuildPerimeterSegments(originalVertices, originalFaces, faceCount);
                var perimeterMatchIndex = SegmentSpatialIndex.Build(perimeterSegments, Math.Max(options.Tolerance, 1e-9));
                var constraintSegments = BuildNonPerimeterConstraintSegments(constraints, perimeterMatchIndex, options.Tolerance);
                profile.AddPhase($"{attemptName}.build_area_check_inputs", Stopwatch.GetElapsedTime(buildAreaCheckInputsStart));

                long areaChecksStart = Stopwatch.GetTimestamp();
                EvaluateTriangleAreaChecks(
                    outputVertices,
                    extracted.Faces,
                    qualityLength,
                    effectiveMaxArea,
                    SegmentSpatialIndex.Build(perimeterSegments, qualityLength * 3.0),
                    SegmentSpatialIndex.Build(constraintSegments, qualityLength * 3.0),
                    ref maxPerimeterTriangleArea,
                    ref maxConstraintTriangleArea,
                    ref perimeterApronInvalid,
                    ref constraintApronInvalid);
                profile.AddPhase($"{attemptName}.area_checks", Stopwatch.GetElapsedTime(areaChecksStart));
            }

            bool accepted = !topologyInvalid && !constraintApronInvalid && !perimeterApronInvalid;

            return new AttemptEvaluation
            {
                Prepared = prepared,
                Vertices = outputVertices,
                Faces = extracted.Faces,
                Warning = accepted ? triangulation.Warning : BuildAttemptFailureWarning(triangulation.Warning, topologyInvalid, constraintApronInvalid, perimeterApronInvalid),
                TopologyInvalid = topologyInvalid,
                ConstraintApronInvalid = constraintApronInvalid,
                PerimeterApronInvalid = perimeterApronInvalid,
                MaxConstraintTriangleArea = maxConstraintTriangleArea,
                MaxPerimeterTriangleArea = maxPerimeterTriangleArea,
                Accepted = accepted
            };
        }
        finally
        {
            profile.AddPhase($"{attemptName}.attempt_total", Stopwatch.GetElapsedTime(attemptStart));
        }
    }

    private static Options CreateBoundaryAndGuideSeedFallbackOptions(Options options)
    {
        double requestedEdgeLength = options.RequestedEdgeLength;
        double maxArea = options.MaxArea;

        if (requestedEdgeLength > 0)
        {
            requestedEdgeLength *= 1.5;
            if (maxArea > 0)
                maxArea *= 2.25;
        }
        else if (maxArea > 0)
        {
            maxArea *= 2.25;
        }

        return new Options
        {
            Tolerance = options.Tolerance,
            RequestedEdgeLength = requestedEdgeLength,
            ConstraintInsertionOnly = options.ConstraintInsertionOnly,
            PreferReducedInteriorSeed = options.PreferReducedInteriorSeed,
            AddReducedInteriorGuideSeeds = options.AddReducedInteriorGuideSeeds,
            AddConstraintCorridorSeeds = options.AddConstraintCorridorSeeds,
            GuidePolylines = options.GuidePolylines,
            MaxArea = maxArea,
            MinAngle = 0.0,
            ProtectSharpEdges = options.ProtectSharpEdges,
            // Carry the feature-preserving knobs so the reduced-seed fallback (which a MaxArea pass often
            // prefers) keeps creases merged/pinned too — otherwise enabling MaxArea silently dropped them.
            VertexMergeTolerance = options.VertexMergeTolerance,
            PreserveCreaseAngleDeg = options.PreserveCreaseAngleDeg
        };
    }

    private static string BuildPreservedInputWarning(
        AttemptEvaluation firstAttempt,
        AttemptEvaluation? fallbackAttempt,
        bool attemptedBoundaryAndGuideSeedFallback)
    {
        var parts = new List<string>
        {
            "Remesh kept the upstream mesh unchanged."
        };

        parts.Add($"Initial pass failed: {DescribeAttemptFailure(firstAttempt)}.");
        if (attemptedBoundaryAndGuideSeedFallback)
        {
            parts.Add(fallbackAttempt is null
                ? "Boundary-and-guide seed fallback could not be prepared."
                : $"Boundary-and-guide seed fallback failed: {DescribeAttemptFailure(fallbackAttempt)}.");
        }

        return string.Join(" ", parts);
    }

    private static string DescribeAttemptFailure(AttemptEvaluation attempt)
    {
        var reasons = new List<string>();

        if (attempt.ConstraintsDropped)
            reasons.Add("constraints could not be preserved");

        if (attempt.QualityDropped)
            reasons.Add("requested remesh refinement could not be satisfied");

        if (attempt.TopologyInvalid)
            reasons.Add("output would create extra boundary loops, open naked-edge chains, or non-manifold edges");

        if (attempt.ConstraintApronInvalid)
            reasons.Add("oversized triangles remained near remesh constraints");

        if (attempt.PerimeterApronInvalid)
            reasons.Add("oversized triangles remained near the terrain perimeter");

        if (reasons.Count == 0)
            reasons.Add(attempt.Warning ?? "triangulation failed");
        else if (!string.IsNullOrWhiteSpace(attempt.Warning))
            reasons.Add(attempt.Warning);

        return string.Join("; ", reasons);
    }

    private static string BuildAttemptFailureWarning(
        string? warning,
        bool topologyInvalid,
        bool constraintApronInvalid,
        bool perimeterApronInvalid)
    {
        var reasons = new List<string>();

        if (topologyInvalid)
            reasons.Add("Remesh output would create extra boundary loops, open naked-edge chains, or non-manifold edges.");

        if (constraintApronInvalid)
            reasons.Add("Remesh output left oversized triangles near remesh constraints.");

        if (perimeterApronInvalid)
            reasons.Add("Remesh output left oversized triangles near the terrain perimeter.");

        if (!string.IsNullOrWhiteSpace(warning))
            reasons.Add(warning);

        return reasons.Count == 0
            ? "Triangulation failed."
            : string.Join(" ", reasons);
    }

    private static void EvaluateTriangleAreaChecks(
        double[] vertices,
        int[] faces,
        double qualityLength,
        double targetArea,
        SegmentSpatialIndex perimeterSegments,
        SegmentSpatialIndex constraintSegments,
        ref double maxPerimeterTriangleArea,
        ref double maxConstraintTriangleArea,
        ref bool perimeterApronInvalid,
        ref bool constraintApronInvalid)
    {
        double perimeterDistanceSquared = qualityLength * qualityLength * 9.0;
        double constraintAreaLimit = targetArea * 12.0;
        double perimeterAreaLimit = targetArea * 16.0;

        for (int faceIndex = 0; faceIndex < faces.Length / 3; faceIndex++)
        {
            int a = faces[faceIndex * 3];
            int b = faces[faceIndex * 3 + 1];
            int c = faces[faceIndex * 3 + 2];
            double area = TriangleArea(vertices, a, b, c);
            double centroidX = (vertices[a * 3] + vertices[b * 3] + vertices[c * 3]) / 3.0;
            double centroidY = (vertices[a * 3 + 1] + vertices[b * 3 + 1] + vertices[c * 3 + 1]) / 3.0;

            if (constraintSegments.Count > 0 &&
                constraintSegments.DistanceSquaredToAnySegment(centroidX, centroidY) <= perimeterDistanceSquared)
            {
                if (area > maxConstraintTriangleArea)
                    maxConstraintTriangleArea = area;

                if (!constraintApronInvalid && area > constraintAreaLimit)
                    constraintApronInvalid = true;
            }

            if (perimeterSegments.Count > 0 &&
                perimeterSegments.DistanceSquaredToAnySegment(centroidX, centroidY) <= perimeterDistanceSquared)
            {
                if (area > maxPerimeterTriangleArea)
                    maxPerimeterTriangleArea = area;

                if (!perimeterApronInvalid && area > perimeterAreaLimit)
                    perimeterApronInvalid = true;
            }

            if (constraintApronInvalid && perimeterApronInvalid)
                return;
        }
    }

    private static bool ShouldPreferBoundaryAndGuideSeedFallback(
        AttemptEvaluation initialAttempt, AttemptEvaluation fallbackAttempt, double targetMaxArea)
    {
        double initialScore = Math.Max(initialAttempt.MaxConstraintTriangleArea, initialAttempt.MaxPerimeterTriangleArea);
        double fallbackScore = Math.Max(fallbackAttempt.MaxConstraintTriangleArea, fallbackAttempt.MaxPerimeterTriangleArea);

        if (initialScore <= 0.0 || fallbackScore <= 0.0)
            return false;

        // Keep the full-seed result — it preserves the input's structured topology (batter rows, graded
        // corridors) — whenever it already satisfies the area target near constraints. The coarse
        // boundary/guide re-seed is only worth its loss of structure when the full-seed pass genuinely
        // could not refine there (carried/constraint vertices blocked it), i.e. it overshoots the target.
        if (targetMaxArea > 0.0 && initialScore <= targetMaxArea)
            return false;

        return fallbackScore < initialScore * 0.85;
    }

    private static List<Segment2D> BuildPerimeterSegments(double[] originalVertices, int[] originalFaces, int faceCount)
    {
        var boundaryEdges = new List<(int a, int b)>();
        MeshConstraintTools.AddBoundarySegments(boundaryEdges, IndexedMeshTools.CreateEdgeKeySet(), originalFaces, faceCount);
        var segments = new List<Segment2D>(boundaryEdges.Count);

        foreach (var (a, b) in boundaryEdges)
        {
            segments.Add(new Segment2D(
                originalVertices[a * 3],
                originalVertices[a * 3 + 1],
                originalVertices[b * 3],
                originalVertices[b * 3 + 1]));
        }

        return segments;
    }

    private static List<Segment2D> BuildNonPerimeterConstraintSegments(
        IReadOnlyList<ConstraintPolyline> constraints,
        SegmentSpatialIndex perimeterSegments,
        double tolerance)
    {
        var result = new List<Segment2D>();

        foreach (var constraint in constraints)
        {
            int pointCount = NormalizePointCount(constraint, tolerance);
            if (pointCount < 2)
                continue;

            if (IsPerimeterConstraint(constraint, pointCount, perimeterSegments, tolerance))
                continue;

            for (int pointIndex = 1; pointIndex < pointCount; pointIndex++)
                result.Add(ToSegment(constraint, pointIndex - 1, pointIndex));

            if (constraint.IsClosed)
                result.Add(ToSegment(constraint, pointCount - 1, 0));
        }

        return result;
    }

    private static bool IsPerimeterConstraint(
        ConstraintPolyline constraint,
        int pointCount,
        SegmentSpatialIndex perimeterSegments,
        double tolerance)
    {
        if (perimeterSegments.Count == 0)
            return false;

        double distanceToleranceSquared = Math.Max(tolerance, 1e-9);
        distanceToleranceSquared *= distanceToleranceSquared;

        for (int pointIndex = 1; pointIndex < pointCount; pointIndex++)
        {
            if (!SegmentLiesOnPerimeter(constraint, pointIndex - 1, pointIndex, perimeterSegments, distanceToleranceSquared))
                return false;
        }

        return !constraint.IsClosed || SegmentLiesOnPerimeter(constraint, pointCount - 1, 0, perimeterSegments, distanceToleranceSquared);
    }

    private static bool SegmentLiesOnPerimeter(
        ConstraintPolyline constraint,
        int startPointIndex,
        int endPointIndex,
        SegmentSpatialIndex perimeterSegments,
        double distanceToleranceSquared)
    {
        double ax = constraint.Points[startPointIndex * 3];
        double ay = constraint.Points[startPointIndex * 3 + 1];
        double bx = constraint.Points[endPointIndex * 3];
        double by = constraint.Points[endPointIndex * 3 + 1];
        double mx = (ax + bx) * 0.5;
        double my = (ay + by) * 0.5;

        return perimeterSegments.DistanceSquaredToAnySegment(ax, ay) <= distanceToleranceSquared &&
               perimeterSegments.DistanceSquaredToAnySegment(mx, my) <= distanceToleranceSquared &&
               perimeterSegments.DistanceSquaredToAnySegment(bx, by) <= distanceToleranceSquared;
    }

    private static Segment2D ToSegment(ConstraintPolyline constraint, int startPointIndex, int endPointIndex)
    {
        return new Segment2D(
            constraint.Points[startPointIndex * 3],
            constraint.Points[startPointIndex * 3 + 1],
            constraint.Points[endPointIndex * 3],
            constraint.Points[endPointIndex * 3 + 1]);
    }

    private static double DistanceSquaredToSegment(double x, double y, Segment2D segment)
    {
        double dx = segment.Bx - segment.Ax;
        double dy = segment.By - segment.Ay;
        double lengthSquared = (dx * dx) + (dy * dy);
        double t = lengthSquared <= 1e-12
            ? 0.0
            : Math.Clamp((((x - segment.Ax) * dx) + ((y - segment.Ay) * dy)) / lengthSquared, 0.0, 1.0);
        double closestX = segment.Ax + (dx * t);
        double closestY = segment.Ay + (dy * t);
        double offsetX = x - closestX;
        double offsetY = y - closestY;
        return (offsetX * offsetX) + (offsetY * offsetY);
    }

    private static void AddConstraintCorridorSeeds(
        List<double> xyList,
        List<double> zList,
        double[] originalVertices,
        int[] originalFaces,
        int faceCount,
        TerrainFaceGrid? faceGrid,
        IReadOnlyList<ConstraintPolyline> constraints,
        double targetLength,
        double floorLength,
        NearVertexIndex nearVertices,
        double reuseTolerance)
    {
        if (constraints.Count < 2 || targetLength <= 0)
            return;

        double effectiveTarget = Math.Max(targetLength, floorLength);
        bool[] paired = new bool[constraints.Count];

        for (int i = 0; i < constraints.Count; i++)
        {
            if (paired[i] || constraints[i].IsClosed)
                continue;

            int countA = NormalizePointCount(constraints[i], reuseTolerance);
            if (countA < 2)
                continue;

            int bestMatch = -1;
            bool bestMatchReversed = false;
            double bestScore = double.PositiveInfinity;

            for (int j = i + 1; j < constraints.Count; j++)
            {
                if (paired[j] || constraints[j].IsClosed)
                    continue;

                if (!TryMatchOpenConstraintPair(constraints[i], countA, constraints[j], reuseTolerance, out bool reverseB, out double score))
                    continue;

                if (score < bestScore)
                {
                    bestScore = score;
                    bestMatch = j;
                    bestMatchReversed = reverseB;
                }
            }

            if (bestMatch < 0)
                continue;

            AddCorridorSeedPoints(
                xyList,
                zList,
                originalVertices,
                originalFaces,
                faceCount,
                faceGrid,
                constraints[i],
                countA,
                constraints[bestMatch],
                NormalizePointCount(constraints[bestMatch], reuseTolerance),
                bestMatchReversed,
                effectiveTarget,
                nearVertices,
                reuseTolerance);

            paired[i] = true;
            paired[bestMatch] = true;
        }
    }

    private static void AddGuidePolylineSeeds(
        List<double> xyList,
        List<double> zList,
        double[] originalVertices,
        int[] originalFaces,
        int faceCount,
        TerrainFaceGrid? faceGrid,
        IReadOnlyList<ConstraintPolyline> guidePolylines,
        double targetLength,
        double floorLength,
        NearVertexIndex nearVertices,
        double reuseTolerance)
    {
        if (guidePolylines.Count == 0)
            return;

        double spacing = targetLength > 0.0
            ? Math.Max(targetLength, floorLength)
            : Math.Max(reuseTolerance * 16.0, 1e-6);

        foreach (var guide in guidePolylines)
        {
            int pointCount = NormalizePointCount(guide, reuseTolerance);
            if (pointCount < 2)
                continue;

            for (int pointIndex = 1; pointIndex < pointCount; pointIndex++)
            {
                AddGuideSegmentSeeds(
                    xyList,
                    zList,
                    originalVertices,
                    originalFaces,
                    faceCount,
                    faceGrid,
                    guide,
                    pointIndex - 1,
                    pointIndex,
                    spacing,
                    nearVertices,
                    reuseTolerance);
            }

            if (guide.IsClosed)
            {
                AddGuideSegmentSeeds(
                    xyList,
                    zList,
                    originalVertices,
                    originalFaces,
                    faceCount,
                    faceGrid,
                    guide,
                    pointCount - 1,
                    0,
                    spacing,
                    nearVertices,
                    reuseTolerance);
            }
        }
    }

    private static void AddGuideSegmentSeeds(
        List<double> xyList,
        List<double> zList,
        double[] originalVertices,
        int[] originalFaces,
        int faceCount,
        TerrainFaceGrid? faceGrid,
        ConstraintPolyline guide,
        int startPointIndex,
        int endPointIndex,
        double spacing,
        NearVertexIndex nearVertices,
        double reuseTolerance)
    {
        double ax = guide.Points[startPointIndex * 3];
        double ay = guide.Points[startPointIndex * 3 + 1];
        double bx = guide.Points[endPointIndex * 3];
        double by = guide.Points[endPointIndex * 3 + 1];
        double length = Math.Sqrt(DistanceSquared(ax, ay, bx, by));
        if (length <= reuseTolerance)
            return;

        int stepCount = Math.Max(1, (int)Math.Ceiling(length / spacing));
        for (int step = 0; step <= stepCount; step++)
        {
            double t = step / (double)stepCount;
            double x = Lerp(ax, bx, t);
            double y = Lerp(ay, by, t);
            if (nearVertices.Find(x, y, reuseTolerance) >= 0)
                continue;

            double z = InterpolateOriginalZ(faceGrid, originalVertices, originalFaces, faceCount, x, y);
            int newIndex = zList.Count;
            xyList.Add(x);
            xyList.Add(y);
            zList.Add(z);
            nearVertices.Add(newIndex);
        }
    }

    private static void AddReducedInteriorGuideSeeds(
        List<double> xyList,
        List<double> zList,
        double[] originalVertices,
        int[] originalFaces,
        int faceCount,
        double targetLength,
        double modelTolerance)
    {
        int originalVertexCount = originalVertices.Length / 3;
        if (originalVertexCount == 0)
            return;

        double maxTerrainSpan = GetMaxTerrainSpan(originalVertices);
        double reuseTolerance = ScaleAwareTolerance.ResolveLength(modelTolerance, maxTerrainSpan);
        double spacing = targetLength > 0
            ? Math.Max(targetLength, reuseTolerance * 8.0)
            : Math.Max(reuseTolerance * 64.0, maxTerrainSpan / 128.0);
        double minSpacing = Math.Max(spacing * 0.6, reuseTolerance * 4.0);
        double minSpacingSq = minSpacing * minSpacing;
        double invCell = 1.0 / spacing;
        var grid = new Dictionary<long, List<int>>(IndexedMeshTools.CellKeyComparer.Instance);

        void InsertSeed(int index)
        {
            long key = PackCellKey(
                (long)Math.Floor(xyList[index * 2] * invCell),
                (long)Math.Floor(xyList[index * 2 + 1] * invCell));
            if (!grid.TryGetValue(key, out var indices))
            {
                indices = new List<int>();
                grid[key] = indices;
            }

            indices.Add(index);
        }

        bool HasNearbySeed(double x, double y)
        {
            long cx = (long)Math.Floor(x * invCell);
            long cy = (long)Math.Floor(y * invCell);
            for (long dx = -1; dx <= 1; dx++)
            {
                for (long dy = -1; dy <= 1; dy++)
                {
                    long key = PackCellKey(cx + dx, cy + dy);
                    if (!grid.TryGetValue(key, out var indices))
                        continue;

                    foreach (int index in indices)
                    {
                        double seedX = xyList[index * 2];
                        double seedY = xyList[index * 2 + 1];
                        double offsetX = seedX - x;
                        double offsetY = seedY - y;
                        if ((offsetX * offsetX) + (offsetY * offsetY) < minSpacingSq)
                            return true;
                    }
                }
            }

            return false;
        }

        int existingSeedCount = xyList.Count / 2;
        for (int i = 0; i < existingSeedCount; i++)
            InsertSeed(i);

        for (int originalIndex = 0; originalIndex < originalVertexCount; originalIndex++)
        {
            double x = originalVertices[originalIndex * 3];
            double y = originalVertices[originalIndex * 3 + 1];
            if (HasNearbySeed(x, y))
                continue;

            int newIndex = zList.Count;
            xyList.Add(x);
            xyList.Add(y);
            zList.Add(originalVertices[originalIndex * 3 + 2]);
            InsertSeed(newIndex);
        }

        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            int a = originalFaces[faceIndex * 3];
            int b = originalFaces[faceIndex * 3 + 1];
            int c = originalFaces[faceIndex * 3 + 2];

            double ax = originalVertices[a * 3];
            double ay = originalVertices[a * 3 + 1];
            double az = originalVertices[a * 3 + 2];
            double bx = originalVertices[b * 3];
            double by = originalVertices[b * 3 + 1];
            double bz = originalVertices[b * 3 + 2];
            double cx = originalVertices[c * 3];
            double cy = originalVertices[c * 3 + 1];
            double cz = originalVertices[c * 3 + 2];

            double minX = Math.Min(ax, Math.Min(bx, cx));
            double maxX = Math.Max(ax, Math.Max(bx, cx));
            double minY = Math.Min(ay, Math.Min(by, cy));
            double maxY = Math.Max(ay, Math.Max(by, cy));
            long minCellX = (long)Math.Floor(minX * invCell);
            long maxCellX = (long)Math.Floor(maxX * invCell);
            long minCellY = (long)Math.Floor(minY * invCell);
            long maxCellY = (long)Math.Floor(maxY * invCell);
            bool addedInteriorSeed = false;

            for (long cellY = minCellY; cellY <= maxCellY; cellY++)
            {
                for (long cellX = minCellX; cellX <= maxCellX; cellX++)
                {
                    double sampleX = (cellX + 0.5) / invCell;
                    double sampleY = (cellY + 0.5) / invCell;
                    if (!PointInTriangle(sampleX, sampleY, ax, ay, bx, by, cx, cy))
                        continue;

                    if (HasNearbySeed(sampleX, sampleY))
                        continue;

                    int newIndex = zList.Count;
                    xyList.Add(sampleX);
                    xyList.Add(sampleY);
                    zList.Add(InterpolateTriangleZ(sampleX, sampleY, ax, ay, az, bx, by, bz, cx, cy, cz));
                    InsertSeed(newIndex);
                    addedInteriorSeed = true;
                }
            }

            if (addedInteriorSeed)
                continue;

            double centroidX = (ax + bx + cx) / 3.0;
            double centroidY = (ay + by + cy) / 3.0;
            if (HasNearbySeed(centroidX, centroidY))
                continue;

            int centroidIndex = zList.Count;
            xyList.Add(centroidX);
            xyList.Add(centroidY);
            zList.Add((az + bz + cz) / 3.0);
            InsertSeed(centroidIndex);
        }
    }

    private static double GetMaxTerrainSpan(double[] originalVertices)
    {
        if (originalVertices.Length < 3)
            return 0.0;

        double minX = originalVertices[0];
        double maxX = originalVertices[0];
        double minY = originalVertices[1];
        double maxY = originalVertices[1];

        for (int i = 1; i < originalVertices.Length / 3; i++)
        {
            double x = originalVertices[i * 3];
            double y = originalVertices[i * 3 + 1];
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        return Math.Max(maxX - minX, maxY - minY);
    }

    private static double[] BuildCumulativeLengths(ConstraintPolyline constraint, int pointCount)
    {
        var cumulative = new double[pointCount];
        for (int i = 1; i < pointCount; i++)
        {
            double x0 = constraint.Points[(i - 1) * 3];
            double y0 = constraint.Points[(i - 1) * 3 + 1];
            double x1 = constraint.Points[i * 3];
            double y1 = constraint.Points[i * 3 + 1];
            cumulative[i] = cumulative[i - 1] + Math.Sqrt(DistanceSquared(x0, y0, x1, y1));
        }

        return cumulative;
    }

    private static double SamplePairDistance(
        ConstraintPolyline constraintA,
        int pointCountA,
        double[] cumulativeA,
        ConstraintPolyline constraintB,
        int pointCountB,
        double[] cumulativeB,
        bool reverseB,
        double fraction)
    {
        SampleConstraintPoint(constraintA, pointCountA, cumulativeA, fraction, reverse: false, out double ax, out double ay);
        SampleConstraintPoint(constraintB, pointCountB, cumulativeB, fraction, reverseB, out double bx, out double by);
        return Math.Sqrt(DistanceSquared(ax, ay, bx, by));
    }

    private static void SampleConstraintPoint(
        ConstraintPolyline constraint,
        int pointCount,
        double[] cumulativeLengths,
        double fraction,
        bool reverse,
        out double x,
        out double y)
    {
        double totalLength = cumulativeLengths[^1];
        if (totalLength <= 1e-12)
        {
            int index = reverse ? pointCount - 1 : 0;
            x = constraint.Points[index * 3];
            y = constraint.Points[index * 3 + 1];
            return;
        }

        double along = Math.Clamp(fraction, 0.0, 1.0) * totalLength;
        double sourceAlong = reverse ? totalLength - along : along;

        int segmentIndex = 1;
        while (segmentIndex < pointCount && cumulativeLengths[segmentIndex] < sourceAlong)
            segmentIndex++;

        if (segmentIndex >= pointCount)
            segmentIndex = pointCount - 1;

        double segmentStart = cumulativeLengths[segmentIndex - 1];
        double segmentEnd = cumulativeLengths[segmentIndex];
        double t = segmentEnd <= segmentStart + 1e-12
            ? 0.0
            : (sourceAlong - segmentStart) / (segmentEnd - segmentStart);

        double ax = constraint.Points[(segmentIndex - 1) * 3];
        double ay = constraint.Points[(segmentIndex - 1) * 3 + 1];
        double bx = constraint.Points[segmentIndex * 3];
        double by = constraint.Points[segmentIndex * 3 + 1];
        x = Lerp(ax, bx, t);
        y = Lerp(ay, by, t);
    }

    private static bool TryMatchOpenConstraintPair(
        ConstraintPolyline constraintA,
        int pointCountA,
        ConstraintPolyline constraintB,
        double tolerance,
        out bool reverseB,
        out double score)
    {
        reverseB = false;
        score = double.PositiveInfinity;

        int pointCountB = NormalizePointCount(constraintB, tolerance);
        if (pointCountA < 2 || pointCountB < 2 || constraintA.IsClosed || constraintB.IsClosed)
            return false;

        var cumulativeA = BuildCumulativeLengths(constraintA, pointCountA);
        var cumulativeB = BuildCumulativeLengths(constraintB, pointCountB);
        double totalA = cumulativeA[^1];
        double totalB = cumulativeB[^1];
        if (totalA <= tolerance || totalB <= tolerance)
            return false;

        // Determine relative orientation by comparing which end of B is nearer to A's start.
        double startStartDist = DistanceSquared(
            constraintA.Points[0], constraintA.Points[1],
            constraintB.Points[0], constraintB.Points[1]);
        double startEndDist = DistanceSquared(
            constraintA.Points[0], constraintA.Points[1],
            constraintB.Points[(pointCountB - 1) * 3],
            constraintB.Points[(pointCountB - 1) * 3 + 1]);
        reverseB = startEndDist < startStartDist;

        double distance0 = SamplePairDistance(constraintA, pointCountA, cumulativeA, constraintB, pointCountB, cumulativeB, reverseB, 0.25);
        double distance1 = SamplePairDistance(constraintA, pointCountA, cumulativeA, constraintB, pointCountB, cumulativeB, reverseB, 0.5);
        double distance2 = SamplePairDistance(constraintA, pointCountA, cumulativeA, constraintB, pointCountB, cumulativeB, reverseB, 0.75);
        double maxDistance = Math.Max(distance0, Math.Max(distance1, distance2));

        // Lines must be meaningfully separated (not the same or near-coincident line).
        if (maxDistance <= tolerance * 2.0)
            return false;

        score = (distance0 + distance1 + distance2) / 3.0;
        return true;
    }

    private static void AddCorridorSeedPoints(
        List<double> xyList,
        List<double> zList,
        double[] originalVertices,
        int[] originalFaces,
        int faceCount,
        TerrainFaceGrid? faceGrid,
        ConstraintPolyline constraintA,
        int pointCountA,
        ConstraintPolyline constraintB,
        int pointCountB,
        bool reverseB,
        double targetLength,
        NearVertexIndex nearVertices,
        double reuseTolerance)
    {
        var cumulativeA = BuildCumulativeLengths(constraintA, pointCountA);
        var cumulativeB = BuildCumulativeLengths(constraintB, pointCountB);
        double totalA = cumulativeA[^1];
        double totalB = cumulativeB[^1];
        double maxLength = Math.Max(totalA, totalB);
        if (maxLength <= reuseTolerance)
            return;

        int stationCount = Math.Max(2, (int)Math.Ceiling(maxLength / targetLength));
        for (int station = 1; station < stationCount; station++)
        {
            double fraction = station / (double)stationCount;
            SampleConstraintPoint(constraintA, pointCountA, cumulativeA, fraction, reverse: false, out double ax, out double ay);
            SampleConstraintPoint(constraintB, pointCountB, cumulativeB, fraction, reverseB, out double bx, out double by);

            double width = Math.Sqrt(DistanceSquared(ax, ay, bx, by));
            if (width <= reuseTolerance * 2.0)
                continue;

            int rowCount = Math.Max(1, (int)Math.Ceiling(width / targetLength) - 1);
            for (int row = 1; row <= rowCount; row++)
            {
                double blend = row / (double)(rowCount + 1);
                double x = Lerp(ax, bx, blend);
                double y = Lerp(ay, by, blend);
                if (nearVertices.Find(x, y, reuseTolerance) >= 0)
                    continue;

                double z = InterpolateOriginalZ(faceGrid, originalVertices, originalFaces, faceCount, x, y);
                int newIndex = zList.Count;
                xyList.Add(x);
                xyList.Add(y);
                zList.Add(z);
                nearVertices.Add(newIndex);
            }
        }
    }

    private static void AddBoundarySegmentChain(
        List<double> xyList,
        List<double> zList,
        List<(int a, int b)> segments,
        HashSet<long> segmentKeys,
        NearVertexIndex nearVertices,
        int startIndex,
        int endIndex,
        double targetLength,
        double floorLength,
        ref int addedProtectedVertices)
    {
        int segmentCount = GetProtectedSubdivisionCount(
            xyList[startIndex * 2],
            xyList[startIndex * 2 + 1],
            xyList[endIndex * 2],
            xyList[endIndex * 2 + 1],
            targetLength,
            floorLength);

        int previousIndex = startIndex;
        for (int step = 1; step < segmentCount; step++)
        {
            double t = step / (double)segmentCount;
            double x = Lerp(xyList[startIndex * 2], xyList[endIndex * 2], t);
            double y = Lerp(xyList[startIndex * 2 + 1], xyList[endIndex * 2 + 1], t);
            double z = Lerp(zList[startIndex], zList[endIndex], t);

            int newIndex = zList.Count;
            xyList.Add(x);
            xyList.Add(y);
            zList.Add(z);
            nearVertices.Add(newIndex);
            MeshConstraintTools.TryAddSegment(segments, segmentKeys, previousIndex, newIndex);
            previousIndex = newIndex;
            addedProtectedVertices++;
        }

        MeshConstraintTools.TryAddSegment(segments, segmentKeys, previousIndex, endIndex);
    }

    private static void AddConstraintSegmentChain(
        List<double> xyList,
        List<double> zList,
        List<(int a, int b)> segments,
        HashSet<long> segmentKeys,
        NearVertexIndex nearVertices,
        double[] originalVertices,
        int[] originalFaces,
        int faceCount,
        TerrainFaceGrid? faceGrid,
        ConstraintPolyline constraint,
        int startPointIndex,
        int endPointIndex,
        int startVertexIndex,
        int endVertexIndex,
        double targetLength,
        double floorLength,
        double exactReuseTolerance,
        bool protectSharpEdges,
        ref int addedProtectedVertices)
    {
        if (!protectSharpEdges || targetLength <= 0)
        {
            MeshConstraintTools.TryAddSegment(segments, segmentKeys, startVertexIndex, endVertexIndex);
            return;
        }

        double startX = constraint.Points[startPointIndex * 3];
        double startY = constraint.Points[startPointIndex * 3 + 1];
        double endX = constraint.Points[endPointIndex * 3];
        double endY = constraint.Points[endPointIndex * 3 + 1];

        int segmentCount = GetProtectedSubdivisionCount(startX, startY, endX, endY, targetLength, floorLength);
        int previousIndex = startVertexIndex;

        for (int step = 1; step < segmentCount; step++)
        {
            double t = step / (double)segmentCount;
            double x = Lerp(startX, endX, t);
            double y = Lerp(startY, endY, t);
            double z = constraint.PreserveInputElevation
                ? Lerp(constraint.Points[startPointIndex * 3 + 2], constraint.Points[endPointIndex * 3 + 2], t)
                : InterpolateOriginalZ(faceGrid, originalVertices, originalFaces, faceCount, x, y);

            int newIndex = nearVertices.Find(x, y, exactReuseTolerance);
            if (newIndex < 0)
            {
                newIndex = zList.Count;
                xyList.Add(x);
                xyList.Add(y);
                zList.Add(z);
                nearVertices.Add(newIndex);
                addedProtectedVertices++;
            }
            else if (constraint.PreserveInputElevation)
            {
                zList[newIndex] = z;
            }

            MeshConstraintTools.TryAddSegment(segments, segmentKeys, previousIndex, newIndex);
            previousIndex = newIndex;
        }

        MeshConstraintTools.TryAddSegment(segments, segmentKeys, previousIndex, endVertexIndex);
    }

    private static int ResolveConstraintVertex(
        List<double> xyList,
        List<double> zList,
        double[] originalVertices,
        int[] originalFaces,
        int faceCount,
        TerrainFaceGrid? faceGrid,
        ConstraintPolyline constraint,
        int pointIndex,
        double broadReuseTolerance,
        double exactReuseTolerance,
        NearVertexIndex nearVertices,
        bool allowBroadReuse)
    {
        double x = constraint.Points[pointIndex * 3];
        double y = constraint.Points[pointIndex * 3 + 1];
        double z = constraint.PreserveInputElevation
            ? constraint.Points[pointIndex * 3 + 2]
            : InterpolateOriginalZ(faceGrid, originalVertices, originalFaces, faceCount, x, y);

        int existing = allowBroadReuse
            ? nearVertices.Find(x, y, broadReuseTolerance)
            : nearVertices.Find(x, y, exactReuseTolerance);
        if (existing >= 0)
        {
            if (constraint.PreserveInputElevation)
                zList[existing] = z;
            return existing;
        }

        int newIndex = zList.Count;
        xyList.Add(x);
        xyList.Add(y);
        zList.Add(z);
        nearVertices.Add(newIndex);
        return newIndex;
    }

    private static double InterpolateOriginalZ(
        TerrainFaceGrid? faceGrid,
        double[] originalVertices,
        int[] originalFaces,
        int faceCount,
        double x,
        double y)
    {
        return faceGrid?.InterpolateZ(x, y) ?? GradingGeometry2D.InterpolateZ(originalVertices, originalFaces, faceCount, x, y);
    }

    private static double GetProtectedEdgeLength(Options options)
    {
        if (options.RequestedEdgeLength > 0)
            return options.RequestedEdgeLength;

        if (options.MaxArea > 0)
            return Math.Sqrt(options.MaxArea * 4.0 / Math.Sqrt(3.0));

        return 0.0;
    }

    private static double GetEffectiveMaxArea(Options options)
    {
        if (options.ConstraintInsertionOnly)
            return 0.0;

        if (options.MaxArea > 0)
            return options.MaxArea;

        if (options.RequestedEdgeLength > 0)
            return options.RequestedEdgeLength * options.RequestedEdgeLength * Math.Sqrt(3.0) / 4.0;

        return 0.0;
    }

    private static void ApplyPreservedConstraintElevations(
        double[] outputVertices,
        IReadOnlyList<ConstraintPolyline> constraints,
        double tolerance)
    {
        if (constraints.Count == 0)
            return;

        double matchTolerance = Math.Max(tolerance, 1e-6);
        PreservedConstraintSegmentIndex? index = PreservedConstraintSegmentIndex.Build(constraints, matchTolerance);
        if (index == null)
            return;

        double matchToleranceSquared = matchTolerance * matchTolerance;
        for (int vertexIndex = 0; vertexIndex < outputVertices.Length / 3; vertexIndex++)
        {
            double x = outputVertices[vertexIndex * 3];
            double y = outputVertices[vertexIndex * 3 + 1];
            if (index.TryGetElevation(x, y, matchToleranceSquared, out double z))
                outputVertices[vertexIndex * 3 + 2] = z;
        }
    }

    private static bool TryProjectToConstraintSegment(
        PreservedConstraintSegment segment,
        double x,
        double y,
        double maxDistanceSquared,
        out double z,
        out double distanceSquared)
    {
        double dx = segment.Bx - segment.Ax;
        double dy = segment.By - segment.Ay;
        double lengthSquared = (dx * dx) + (dy * dy);
        double t;
        if (lengthSquared <= 1e-12)
        {
            t = 0.0;
        }
        else
        {
            t = (((x - segment.Ax) * dx) + ((y - segment.Ay) * dy)) / lengthSquared;
            if (t < 0.0 || t > 1.0)
            {
                z = 0.0;
                distanceSquared = double.PositiveInfinity;
                return false;
            }
        }

        double closestX = segment.Ax + (dx * t);
        double closestY = segment.Ay + (dy * t);
        double offsetX = x - closestX;
        double offsetY = y - closestY;
        distanceSquared = (offsetX * offsetX) + (offsetY * offsetY);
        if (distanceSquared > maxDistanceSquared)
        {
            z = 0.0;
            return false;
        }

        z = Lerp(segment.Az, segment.Bz, t);
        return true;
    }

    private static int NormalizePointCount(ConstraintPolyline constraint, double tolerance)
    {
        if (!constraint.IsClosed || constraint.PointCount < 3)
            return constraint.PointCount;

        int last = constraint.PointCount - 1;
        double dx = constraint.Points[last * 3] - constraint.Points[0];
        double dy = constraint.Points[last * 3 + 1] - constraint.Points[1];
        double tolSq = tolerance * tolerance;
        return dx * dx + dy * dy <= tolSq
            ? last
            : constraint.PointCount;
    }

    private static int GetProtectedSubdivisionCount(
        double ax,
        double ay,
        double bx,
        double by,
        double targetLength,
        double floorLength)
    {
        if (targetLength <= 0)
            return 1;

        double length = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
        if (length <= targetLength)
            return 1;

        double effectiveTarget = Math.Max(targetLength, floorLength);
        int segmentCount = Math.Max(1, (int)Math.Ceiling(length / effectiveTarget));
        while (segmentCount > 1 && (length / segmentCount) < floorLength)
            segmentCount--;

        return Math.Max(1, segmentCount);
    }

    private static double DistanceSquared(double ax, double ay, double bx, double by)
    {
        double dx = ax - bx;
        double dy = ay - by;
        return dx * dx + dy * dy;
    }

    private static bool PointInTriangle(
        double px,
        double py,
        double ax,
        double ay,
        double bx,
        double by,
        double cx,
        double cy)
    {
        double v0x = cx - ax;
        double v0y = cy - ay;
        double v1x = bx - ax;
        double v1y = by - ay;
        double v2x = px - ax;
        double v2y = py - ay;

        double dot00 = (v0x * v0x) + (v0y * v0y);
        double dot01 = (v0x * v1x) + (v0y * v1y);
        double dot02 = (v0x * v2x) + (v0y * v2y);
        double dot11 = (v1x * v1x) + (v1y * v1y);
        double dot12 = (v1x * v2x) + (v1y * v2y);
        double denominator = (dot00 * dot11) - (dot01 * dot01);
        if (Math.Abs(denominator) <= 1e-20)
            return false;

        double invDenominator = 1.0 / denominator;
        double u = ((dot11 * dot02) - (dot01 * dot12)) * invDenominator;
        double v = ((dot00 * dot12) - (dot01 * dot02)) * invDenominator;
        return u >= -1e-9 && v >= -1e-9 && (u + v) <= 1.0 + 1e-9;
    }

    private static double InterpolateTriangleZ(
        double px,
        double py,
        double ax,
        double ay,
        double az,
        double bx,
        double by,
        double bz,
        double cx,
        double cy,
        double cz)
    {
        double denominator = ((by - cy) * (ax - cx)) + ((cx - bx) * (ay - cy));
        if (Math.Abs(denominator) <= 1e-20)
            return (az + bz + cz) / 3.0;

        double w0 = (((by - cy) * (px - cx)) + ((cx - bx) * (py - cy))) / denominator;
        double w1 = (((cy - ay) * (px - cx)) + ((ax - cx) * (py - cy))) / denominator;
        double w2 = 1.0 - w0 - w1;
        return (w0 * az) + (w1 * bz) + (w2 * cz);
    }

    private static double TriangleArea(double[] vertices, int a, int b, int c)
    {
        double ax = vertices[a * 3];
        double ay = vertices[a * 3 + 1];
        double bx = vertices[b * 3];
        double by = vertices[b * 3 + 1];
        double cx = vertices[c * 3];
        double cy = vertices[c * 3 + 1];
        return Math.Abs(((bx - ax) * (cy - ay)) - ((by - ay) * (cx - ax))) * 0.5;
    }

    private static long PackCellKey(long cx, long cy) =>
        (cx * 0x100000001L) ^ (cy * 0x27d4eb2dL);

    private static double Lerp(double a, double b, double t) => a + ((b - a) * t);
}
