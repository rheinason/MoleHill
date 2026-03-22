using MoleHill.Core.Grading;
using TriangleNet.Geometry;
using TriangleNet.Meshing;

namespace MoleHill.Core.Engine;

/// <summary>
/// Shared remesh pipeline for Rhino and Grasshopper.
/// Preserves mesh boundaries and constraint curves while limiting edge refinement along sharp features.
/// </summary>
public static class SurfaceRemesher
{
    public readonly record struct ConstraintPolyline(double[] Points, int PointCount, bool IsClosed, bool PreserveInputElevation = false);

    public sealed class Options
    {
        public double Tolerance { get; init; }

        public double RequestedEdgeLength { get; init; }

        public double MaxArea { get; init; }

        public double MinAngle { get; init; }

        public bool ProtectSharpEdges { get; init; } = true;
    }

    public sealed class Result
    {
        public bool Success { get; init; }

        public double[] Vertices { get; init; } = Array.Empty<double>();

        public int[] Faces { get; init; } = Array.Empty<int>();

        public string? Warning { get; init; }

        public bool UsedReducedSeedFallback { get; init; }

        public int AddedProtectedVertices { get; init; }
    }

    private sealed class PreparedInput
    {
        public required List<double> XY { get; init; }

        public required List<double> Z { get; init; }

        public required List<(int a, int b)> Segments { get; init; }

        public required int AddedProtectedVertices { get; init; }

        public required bool UsesFullOriginalVertexSeed { get; init; }
    }

    private readonly record struct TriangulationAttempt(IMesh? Mesh, string? Warning);

    public static Result Remesh(
        double[] originalVertices,
        int[] originalFaces,
        IReadOnlyList<ConstraintPolyline> constraints,
        Options options)
    {
        int originalVertexCount = originalVertices.Length / 3;
        int faceCount = originalFaces.Length / 3;

        if (originalVertexCount == 0 || faceCount == 0)
        {
            return new Result
            {
                Success = false,
                Warning = "Input mesh has no usable triangles."
            };
        }

        double effectiveMaxArea = GetEffectiveMaxArea(options);
        bool requiresQuality = effectiveMaxArea > 0 || options.MinAngle > 0;
        var prepared = PrepareInput(originalVertices, originalFaces, constraints, options, seedInteriorVertices: true);
        var triangulation = TriangulatePrepared(prepared, effectiveMaxArea, options);
        bool usedReducedSeedFallback = false;

        if (requiresQuality && !IsAcceptableTriangulation(triangulation, requiresQuality))
        {
            var reducedPrepared = PrepareInput(originalVertices, originalFaces, constraints, options, seedInteriorVertices: false);
            if (!reducedPrepared.UsesFullOriginalVertexSeed)
            {
                var reducedTriangulation = TriangulatePrepared(reducedPrepared, effectiveMaxArea, options);
                if (IsAcceptableTriangulation(reducedTriangulation, requiresQuality))
                {
                    prepared = reducedPrepared;
                    triangulation = reducedTriangulation;
                    usedReducedSeedFallback = true;
                }
            }
        }

        if (triangulation.Mesh == null)
        {
            return new Result
            {
                Success = false,
                Warning = triangulation.Warning ?? "Triangulation failed.",
                UsedReducedSeedFallback = usedReducedSeedFallback,
                AddedProtectedVertices = prepared.AddedProtectedVertices
            };
        }

        if (MeshConstraintTools.ConstraintsWereDropped(triangulation.Warning))
        {
            return new Result
            {
                Success = false,
                Warning = triangulation.Warning ?? "Constraints could not be preserved.",
                UsedReducedSeedFallback = usedReducedSeedFallback,
                AddedProtectedVertices = prepared.AddedProtectedVertices
            };
        }

        if (requiresQuality && MeshConstraintTools.QualityWasDropped(triangulation.Warning))
        {
            return new Result
            {
                Success = false,
                Warning = triangulation.Warning ?? "Requested remesh refinement could not be satisfied.",
                UsedReducedSeedFallback = usedReducedSeedFallback,
                AddedProtectedVertices = prepared.AddedProtectedVertices
            };
        }

        var mesh = triangulation.Mesh;
        if (mesh.Triangles.Count == 0)
        {
            return new Result
            {
                Success = false,
                Warning = "Triangulation produced 0 triangles.",
                UsedReducedSeedFallback = usedReducedSeedFallback,
                AddedProtectedVertices = prepared.AddedProtectedVertices
            };
        }

        var extracted = TriangleNetExtractor.Extract(mesh);
        var outputVertices = new double[extracted.VertexCount * 3];

        for (int i = 0; i < extracted.VertexCount; i++)
        {
            double x = extracted.Xy[i * 2];
            double y = extracted.Xy[i * 2 + 1];
            int sourceId = extracted.SourceIds[i];
            outputVertices[i * 3] = x;
            outputVertices[i * 3 + 1] = y;
            outputVertices[i * 3 + 2] = sourceId >= 0 && sourceId < prepared.Z.Count
                ? prepared.Z[sourceId]
                : PadGrader.InterpolateZ(originalVertices, originalFaces, faceCount, x, y);
        }

        ApplyPreservedConstraintElevations(outputVertices, constraints, options.Tolerance);

        return new Result
        {
            Success = true,
            Vertices = outputVertices,
            Faces = extracted.Faces,
            Warning = triangulation.Warning,
            UsedReducedSeedFallback = usedReducedSeedFallback,
            AddedProtectedVertices = prepared.AddedProtectedVertices
        };
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
        var segmentKeys = new HashSet<long>();
        var boundarySegments = new List<(int a, int b)>();
        MeshConstraintTools.AddBoundarySegments(boundarySegments, new HashSet<long>(), originalFaces, faceCount);

        bool usesFullOriginalVertexSeed = seedInteriorVertices || boundarySegments.Count == 0;
        var xyList = new List<double>(usesFullOriginalVertexSeed ? originalVertexCount * 2 : Math.Max(boundarySegments.Count * 4, 8));
        var zList = new List<double>(usesFullOriginalVertexSeed ? originalVertexCount : Math.Max(boundarySegments.Count * 2, 4));
        Dictionary<int, int>? originalIndexMap = usesFullOriginalVertexSeed ? null : new Dictionary<int, int>();

        if (usesFullOriginalVertexSeed)
        {
            for (int i = 0; i < originalVertexCount; i++)
            {
                xyList.Add(originalVertices[i * 3]);
                xyList.Add(originalVertices[i * 3 + 1]);
                zList.Add(originalVertices[i * 3 + 2]);
            }
        }

        int EnsureSeedVertex(int originalIndex)
        {
            if (usesFullOriginalVertexSeed)
                return originalIndex;

            if (originalIndexMap!.TryGetValue(originalIndex, out int existing))
                return existing;

            int newIndex = zList.Count;
            xyList.Add(originalVertices[originalIndex * 3]);
            xyList.Add(originalVertices[originalIndex * 3 + 1]);
            zList.Add(originalVertices[originalIndex * 3 + 2]);
            originalIndexMap.Add(originalIndex, newIndex);
            return newIndex;
        }

        double targetLength = GetProtectedEdgeLength(options);
        double floorLength = Math.Max(options.Tolerance * 4.0, 1e-6);
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

        double dedupTolerance = Math.Max(options.Tolerance, 1e-6);
        // Cap dedup radius to a fraction of the target edge length so that tolerance values
        // large relative to edge length (e.g. 0.5 m tolerance + 1 m edge length) do not snap
        // adjacent constraint vertices together, which would collapse constraint chains and
        // cause "Constraints could not be enforced" failures.
        if (targetLength > 0)
            dedupTolerance = Math.Min(dedupTolerance, targetLength * 0.25);
        double exactReuseTolerance = Math.Max(Math.Min(dedupTolerance * 0.01, 1e-6), 1e-9);

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
                constraint,
                0,
                dedupTolerance,
                exactReuseTolerance,
                allowBroadReuse: true);

            for (int pointIndex = 1; pointIndex < normalizedCount; pointIndex++)
            {
                int nextIndex = ResolveConstraintVertex(
                    xyList,
                    zList,
                    originalVertices,
                    originalFaces,
                    faceCount,
                    constraint,
                    pointIndex,
                    dedupTolerance,
                    exactReuseTolerance,
                    allowBroadReuse: true);

                AddConstraintSegmentChain(
                    xyList,
                    zList,
                    segments,
                    segmentKeys,
                    originalVertices,
                    originalFaces,
                    faceCount,
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
                    constraint,
                    0,
                    dedupTolerance,
                    exactReuseTolerance,
                    allowBroadReuse: true);

                AddConstraintSegmentChain(
                    xyList,
                    zList,
                    segments,
                    segmentKeys,
                    originalVertices,
                    originalFaces,
                    faceCount,
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

        AddConstraintCorridorSeeds(
            xyList,
            zList,
            originalVertices,
            originalFaces,
            faceCount,
            constraints,
            targetLength,
            floorLength,
            dedupTolerance);

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
        string? warning;
        // segmentSplitting=2 when ProtectSharpEdges: pre-subdivision already placed vertices
        // at target spacing so Triangle.NET doesn't need to split them further. This prevents
        // the cascade that occurs with free splitting near narrow corridors (retaining walls).
        var mesh = TriangulationHelper.Triangulate(
            prepared.XY,
            prepared.Z.Count,
            prepared.Segments,
            effectiveMaxArea,
            options.MinAngle,
            out warning,
            convex: false,
            segmentSplitting: options.ProtectSharpEdges ? 2 : 0);

        // If ProtectSharpEdges=2 drops constraints (tight parallel breaklines like retaining
        // walls), fall back to free splitting. The reference-equality vertex collection handles
        // Triangle.NET's split-vertex bug; the SteinerPoints cap in TriangulationHelper
        // prevents cascade freezing in the fallback path.
        if (options.ProtectSharpEdges &&
            (mesh == null || MeshConstraintTools.ConstraintsWereDropped(warning)))
        {
            mesh = TriangulationHelper.Triangulate(
                prepared.XY,
                prepared.Z.Count,
                prepared.Segments,
                effectiveMaxArea,
                options.MinAngle,
                out warning,
                convex: false,
                segmentSplitting: 0);
        }

        return new TriangulationAttempt(mesh, warning);
    }

    private static bool IsAcceptableTriangulation(TriangulationAttempt triangulation, bool requiresQuality)
    {
        return triangulation.Mesh != null &&
               !MeshConstraintTools.ConstraintsWereDropped(triangulation.Warning) &&
               (!requiresQuality || !MeshConstraintTools.QualityWasDropped(triangulation.Warning));
    }

    private static void AddConstraintCorridorSeeds(
        List<double> xyList,
        List<double> zList,
        double[] originalVertices,
        int[] originalFaces,
        int faceCount,
        IReadOnlyList<ConstraintPolyline> constraints,
        double targetLength,
        double floorLength,
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
                constraints[i],
                countA,
                constraints[bestMatch],
                NormalizePointCount(constraints[bestMatch], reuseTolerance),
                bestMatchReversed,
                effectiveTarget,
                reuseTolerance);

            paired[i] = true;
            paired[bestMatch] = true;
        }
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
        ConstraintPolyline constraintA,
        int pointCountA,
        ConstraintPolyline constraintB,
        int pointCountB,
        bool reverseB,
        double targetLength,
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
                if (FindNearVertex(xyList, x, y, reuseTolerance) >= 0)
                    continue;

                double z = PadGrader.InterpolateZ(originalVertices, originalFaces, faceCount, x, y);
                xyList.Add(x);
                xyList.Add(y);
                zList.Add(z);
            }
        }
    }

    private static void AddBoundarySegmentChain(
        List<double> xyList,
        List<double> zList,
        List<(int a, int b)> segments,
        HashSet<long> segmentKeys,
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
        double[] originalVertices,
        int[] originalFaces,
        int faceCount,
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
                : PadGrader.InterpolateZ(originalVertices, originalFaces, faceCount, x, y);

            int newIndex = FindNearVertex(xyList, x, y, exactReuseTolerance);
            if (newIndex < 0)
            {
                newIndex = zList.Count;
                xyList.Add(x);
                xyList.Add(y);
                zList.Add(z);
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
        ConstraintPolyline constraint,
        int pointIndex,
        double broadReuseTolerance,
        double exactReuseTolerance,
        bool allowBroadReuse)
    {
        double x = constraint.Points[pointIndex * 3];
        double y = constraint.Points[pointIndex * 3 + 1];
        double z = constraint.PreserveInputElevation
            ? constraint.Points[pointIndex * 3 + 2]
            : PadGrader.InterpolateZ(originalVertices, originalFaces, faceCount, x, y);

        int existing = allowBroadReuse
            ? FindNearVertex(xyList, x, y, broadReuseTolerance)
            : FindNearVertex(xyList, x, y, exactReuseTolerance);
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
        return newIndex;
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
        for (int vertexIndex = 0; vertexIndex < outputVertices.Length / 3; vertexIndex++)
        {
            double x = outputVertices[vertexIndex * 3];
            double y = outputVertices[vertexIndex * 3 + 1];
            if (TryGetPreservedConstraintElevation(x, y, constraints, matchTolerance, out double z))
                outputVertices[vertexIndex * 3 + 2] = z;
        }
    }

    private static bool TryGetPreservedConstraintElevation(
        double x,
        double y,
        IReadOnlyList<ConstraintPolyline> constraints,
        double tolerance,
        out double z)
    {
        double bestDistanceSquared = tolerance * tolerance;
        z = 0.0;
        bool found = false;

        foreach (var constraint in constraints)
        {
            if (!constraint.PreserveInputElevation)
                continue;

            int pointCount = NormalizePointCount(constraint, tolerance);
            if (pointCount < 2)
                continue;

            for (int pointIndex = 1; pointIndex < pointCount; pointIndex++)
            {
                if (TryProjectToConstraintSegment(constraint, pointIndex - 1, pointIndex, x, y, bestDistanceSquared, out double candidateZ, out double distanceSquared))
                {
                    bestDistanceSquared = distanceSquared;
                    z = candidateZ;
                    found = true;
                }
            }

            if (constraint.IsClosed &&
                TryProjectToConstraintSegment(constraint, pointCount - 1, 0, x, y, bestDistanceSquared, out double closingZ, out double closingDistanceSquared))
            {
                bestDistanceSquared = closingDistanceSquared;
                z = closingZ;
                found = true;
            }
        }

        return found;
    }

    private static bool TryProjectToConstraintSegment(
        ConstraintPolyline constraint,
        int startPointIndex,
        int endPointIndex,
        double x,
        double y,
        double maxDistanceSquared,
        out double z,
        out double distanceSquared)
    {
        double ax = constraint.Points[startPointIndex * 3];
        double ay = constraint.Points[startPointIndex * 3 + 1];
        double az = constraint.Points[startPointIndex * 3 + 2];
        double bx = constraint.Points[endPointIndex * 3];
        double by = constraint.Points[endPointIndex * 3 + 1];
        double bz = constraint.Points[endPointIndex * 3 + 2];

        double dx = bx - ax;
        double dy = by - ay;
        double lengthSquared = (dx * dx) + (dy * dy);
        double t;
        if (lengthSquared <= 1e-12)
        {
            t = 0.0;
        }
        else
        {
            t = (((x - ax) * dx) + ((y - ay) * dy)) / lengthSquared;
            if (t < 0.0 || t > 1.0)
            {
                z = 0.0;
                distanceSquared = double.PositiveInfinity;
                return false;
            }
        }

        double closestX = ax + (dx * t);
        double closestY = ay + (dy * t);
        double offsetX = x - closestX;
        double offsetY = y - closestY;
        distanceSquared = (offsetX * offsetX) + (offsetY * offsetY);
        if (distanceSquared > maxDistanceSquared)
        {
            z = 0.0;
            return false;
        }

        z = Lerp(az, bz, t);
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

    private static int FindNearVertex(List<double> xyList, double px, double py, double tolerance)
    {
        double tolSq = tolerance * tolerance;
        int count = xyList.Count / 2;
        for (int i = 0; i < count; i++)
        {
            double dx = xyList[i * 2] - px;
            double dy = xyList[i * 2 + 1] - py;
            if (dx * dx + dy * dy < tolSq)
                return i;
        }

        return -1;
    }

    private static double DistanceSquared(double ax, double ay, double bx, double by)
    {
        double dx = ax - bx;
        double dy = ay - by;
        return dx * dx + dy * dy;
    }

    private static double Lerp(double a, double b, double t) => a + ((b - a) * t);
}
