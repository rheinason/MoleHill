using MoleHill.Core.Engine;
using TriangleNet.Geometry;
using TriangleNet.Meshing;

namespace MoleHill.Core.Grading;

/// <summary>
/// Grades a terrain mesh along path curves (roads, sidewalks, etc.).
/// Adds road edges (path offset by half-width) as constrained edges,
/// re-triangulates, then grades Z: inside road = path Z, outside = slope transition.
/// Falls back to Z-only modification if triangulation fails.
/// </summary>
public static partial class PathGrader
{
    /// <summary>
    /// Apply path grading to a terrain mesh.
    /// Overlapping paths are blended by proximity so junction behavior is stable
    /// regardless of the input order.
    /// </summary>
    public static GradingResult? Grade(
        double[] vertices, int vertexCount,
        int[] faces, int faceCount,
        PathDefinition[] paths,
        out string? errorMessage,
        double modelTolerance = GradingTolerances.DefaultModelTolerance)
    {
        return Grade(vertices, vertexCount, faces, faceCount, paths, Array.Empty<SurfaceRemesher.ConstraintPolyline>(), out errorMessage, modelTolerance);
    }

    public static GradingResult? Grade(
        double[] vertices, int vertexCount,
        int[] faces, int faceCount,
        PathDefinition[] paths,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> hardConstraints,
        out string? errorMessage,
        double modelTolerance = GradingTolerances.DefaultModelTolerance)
    {
        errorMessage = null;
        hardConstraints ??= Array.Empty<SurfaceRemesher.ConstraintPolyline>();

        if (!GradingInputValidator.ValidateTerrainMesh(vertices, vertexCount, faces, faceCount, out errorMessage))
            return null;

        if (!GradingInputValidator.ValidateConstraintPolylines(hardConstraints, "Hard", out errorMessage))
            return null;

        if (paths == null)
        {
            errorMessage = "No path definitions provided.";
            return null;
        }

        if (paths.Length == 0)
        {
            errorMessage = "No path definitions provided.";
            return null;
        }

        foreach (var path in paths)
        {
            if (path == null)
            {
                errorMessage = "Each path must have valid XY and Z vertices.";
                return null;
            }

            if (path.VertexCount < 2)
            {
                errorMessage = "Each path must have at least 2 vertices.";
                return null;
            }

            long requiredPathXyValues = (long)path.VertexCount * 2;
            if (requiredPathXyValues > int.MaxValue ||
                !GradingInputValidator.ValidateFiniteValues(
                    path.XyVertices,
                    (int)requiredPathXyValues,
                    "Each path must have valid XY and Z vertices.",
                    "Path coordinates must contain only finite values.",
                    out errorMessage) ||
                !GradingInputValidator.ValidateFiniteValues(
                    path.ZValues,
                    path.VertexCount,
                    "Each path must have valid XY and Z vertices.",
                    "Path elevations must contain only finite values.",
                    out errorMessage))
            {
                return null;
            }

            if (!double.IsFinite(path.Width) || path.Width <= 0)
            {
                errorMessage = "Path width must be positive.";
                return null;
            }

            if (!double.IsFinite(path.SlopeAngleDeg) ||
                !double.IsFinite(path.MaxDistance) ||
                path.MaxDistance < 0.0)
            {
                errorMessage = "Each path must define finite grading parameters.";
                return null;
            }
        }

        // Grade Path must own and rebuild topology. Do not silently fall back to Z-only grading.
        var result = GradeWithEdges(vertices, vertexCount, faces, faceCount, paths, hardConstraints, modelTolerance, out string? topologyError, out _);
        if (result != null)
        {
            errorMessage = null;
            return result;
        }

        errorMessage = string.IsNullOrWhiteSpace(topologyError)
            ? "Grade Path topology rebuild failed."
            : $"Grade Path topology rebuild failed: {topologyError}";
        return null;
    }

    public static double[] ApplyGradingZ(double[] topologyVertices, int vertexCount, PathDefinition[] paths)
    {
        return ApplyGradingZ(topologyVertices, vertexCount, paths, out _);
    }

    public static GradingResult? TryRepairRejectedStitchedResult(GradingResult result, out string? diagnostic)
    {
        diagnostic = null;
        if (result.VertexCount <= 0 || result.FaceCount <= 0)
            return null;

        MeshArtifactCleaner.CleanupResult cleanup = MeshArtifactCleaner.Clean(
            result.Vertices,
            result.VertexCount,
            result.Faces,
            result.FaceCount,
            new MeshArtifactCleaner.Options(
                MinComponentFaceCount: 1,
                MinComponentAreaRatio: 0.0,
                MinFaceAngleDegrees: 1.0,
                MaxAspectRatio: 100.0,
                KeepLargestComponentOnly: true));

        if (cleanup.RemovedFaceCount <= 0)
        {
            diagnostic = $"Grade Path stitched repair found no removable detached components (components {cleanup.Before.ComponentCount}, boundary edges {cleanup.Before.BoundaryEdgeCount}).";
            return null;
        }

        diagnostic =
            $"Grade Path stitched repair kept the largest connected component only (components {cleanup.Before.ComponentCount}->{cleanup.After.ComponentCount}, removed components={cleanup.RemovedComponentCount}, removed faces={cleanup.RemovedFaceCount}, boundary edges {cleanup.Before.BoundaryEdgeCount}->{cleanup.After.BoundaryEdgeCount}).";

        string[] diagnostics = result.Diagnostics.Count == 0
            ? new[] { diagnostic }
            : result.Diagnostics.Concat(new[] { diagnostic }).ToArray();
        GradingDiagnostic[] structuredDiagnostics = result.StructuredDiagnostics
            .Concat(new[]
            {
                GradingDiagnostic.Warning(
                    "grade_path.stitched_repair.detached_components_removed",
                    diagnostic,
                    operation: "Grade Path")
            })
            .ToArray();

        return new GradingResult(
            cleanup.Vertices,
            cleanup.VertexCount,
            cleanup.Faces,
            cleanup.FaceCount,
            result.CutVolume,
            result.FillVolume,
            result.DaylightVertices,
            result.DaylightVertexCount,
            result.OutputPolylines,
            diagnostics,
            result.PatchSummaries,
            structuredDiagnostics);
    }

    public static double[] ApplyGradingZ(double[] topologyVertices, int vertexCount, PathDefinition[] paths, out int changedVertexCount)
    {
        return ApplyGradingZ(topologyVertices, vertexCount, paths, Array.Empty<SurfaceRemesher.ConstraintPolyline>(), out changedVertexCount);
    }

    public static double[] ApplyGradingZ(
        double[] topologyVertices,
        int vertexCount,
        PathDefinition[] paths,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> barrierConstraints,
        out int changedVertexCount)
    {
        var outXy = new double[vertexCount * 2];
        var origZ = new double[vertexCount];
        var newZ = new double[vertexCount];

        for (int i = 0; i < vertexCount; i++)
        {
            outXy[i * 2] = topologyVertices[i * 3];
            outXy[i * 2 + 1] = topologyVertices[i * 3 + 1];
            origZ[i] = topologyVertices[i * 3 + 2];
            newZ[i] = topologyVertices[i * 3 + 2];
        }

        ApplyPathGrading(paths, barrierConstraints, outXy, origZ, newZ, vertexCount);

        var gradedVertices = new double[vertexCount * 3];
        changedVertexCount = 0;
        for (int i = 0; i < vertexCount; i++)
        {
            gradedVertices[i * 3] = topologyVertices[i * 3];
            gradedVertices[i * 3 + 1] = topologyVertices[i * 3 + 1];
            gradedVertices[i * 3 + 2] = newZ[i];
            if (Math.Abs(newZ[i] - origZ[i]) > 1e-9)
                changedVertexCount++;
        }

        return gradedVertices;
    }

    public static double[] ApplyGradingZ(
        double[] topologyVertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PathDefinition[] paths,
        out int changedVertexCount)
    {
        return ApplyGradingZ(
            topologyVertices,
            vertexCount,
            faces,
            faceCount,
            paths,
            Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
            out changedVertexCount);
    }

    public static double[] ApplyGradingZ(
        double[] topologyVertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        PathDefinition[] paths,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> barrierConstraints,
        out int changedVertexCount)
    {
        var outXy = new double[vertexCount * 2];
        var origZ = new double[vertexCount];
        var newZ = new double[vertexCount];
        bool hasBoundaryLoop = MeshBoundaryLoopBuilder.TryBuildBoundaryLoop(topologyVertices, faces, faceCount, out double[] boundaryLoop, out int boundaryVertexCount);
        var faceGrid = new TerrainFaceGrid(topologyVertices, vertexCount, faces, faceCount);

        for (int i = 0; i < vertexCount; i++)
        {
            outXy[i * 2] = topologyVertices[i * 3];
            outXy[i * 2 + 1] = topologyVertices[i * 3 + 1];
            origZ[i] = topologyVertices[i * 3 + 2];
            newZ[i] = topologyVertices[i * 3 + 2];
        }

        ApplyPathGrading(
            paths,
            barrierConstraints,
            outXy,
            origZ,
            newZ,
            vertexCount,
            faceGrid.InterpolateZ,
            hasBoundaryLoop,
            boundaryLoop,
            boundaryVertexCount,
            boundaryTolerance: 1e-3);

        var gradedVertices = new double[vertexCount * 3];
        changedVertexCount = 0;
        for (int i = 0; i < vertexCount; i++)
        {
            gradedVertices[i * 3] = topologyVertices[i * 3];
            gradedVertices[i * 3 + 1] = topologyVertices[i * 3 + 1];
            gradedVertices[i * 3 + 2] = newZ[i];
            if (Math.Abs(newZ[i] - origZ[i]) > 1e-9)
                changedVertexCount++;
        }

        return gradedVertices;
    }

    private static double ComputeConstraintSegmentLength(PathDefinition path, double shoulderDistance)
    {
        double baseSpacing = shoulderDistance > 1e-6 ? shoulderDistance * 0.5 : path.Width;
        double minSpacing = Math.Max(path.Width * 0.5, 1.0);
        double maxSpacing = Math.Max(minSpacing, path.Width * 2.0);
        return Math.Clamp(baseSpacing, minSpacing, maxSpacing);
    }

    private static ConstraintPath MakeGeometryConstraintPath(double[] xy, double[] z, int count)
        => new ConstraintPath(xy, z, count, Array.Empty<double>(), Array.Empty<double>());

    private static ConstraintPath BuildConstraintPolyline(PathDefinition path, double maxSegmentLength, double dedupTol)
    {
        if (path.VertexCount < 2 || maxSegmentLength <= dedupTol)
        {
            double[] xyOut = (double[])path.XyVertices.Clone();
            int n = path.VertexCount;
            ComputeSmoothedTangents(xyOut, n, out double[] tx, out double[] ty);
            return new ConstraintPath(xyOut, (double[])path.ZValues.Clone(), n, tx, ty);
        }

        var xy = new List<double>(path.VertexCount * 4);
        var z = new List<double>(path.VertexCount * 2);
        for (int segmentIndex = 0; segmentIndex < path.VertexCount - 1; segmentIndex++)
        {
            double ax = path.XyVertices[segmentIndex * 2];
            double ay = path.XyVertices[segmentIndex * 2 + 1];
            double bx = path.XyVertices[(segmentIndex + 1) * 2];
            double by = path.XyVertices[(segmentIndex + 1) * 2 + 1];
            double az = path.ZValues[segmentIndex];
            double bz = path.ZValues[segmentIndex + 1];

            double segLen = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
            int divisions = Math.Max(1, (int)Math.Ceiling(segLen / maxSegmentLength));

            for (int step = 0; step < divisions; step++)
            {
                double t = (double)step / divisions;
                AddConstraintSample(xy, z, ax + (bx - ax) * t, ay + (by - ay) * t, az + ((bz - az) * t), dedupTol);
            }
        }

        AddConstraintSample(
            xy,
            z,
            path.XyVertices[(path.VertexCount - 1) * 2],
            path.XyVertices[(path.VertexCount - 1) * 2 + 1],
            path.ZValues[path.VertexCount - 1],
            dedupTol);

        double[] xyArr = xy.ToArray();
        int count = xy.Count / 2;
        ComputeSmoothedTangents(xyArr, count, out double[] tangentX, out double[] tangentY);
        return new ConstraintPath(xyArr, z.ToArray(), count, tangentX, tangentY);
    }

    private static ConstraintPath ResampleConstraintPath(ConstraintPath path, double maxSegmentLength, double dedupTol)
    {
        if (path.VertexCount < 3 || maxSegmentLength <= dedupTol)
            return path;

        var cumulativeLengths = BuildConstraintCumulativeLengths(path.XyVertices, path.VertexCount);
        double totalLength = cumulativeLengths[^1];
        if (totalLength <= maxSegmentLength + dedupTol)
            return path;

        var xy = new List<double>(path.VertexCount * 2);
        var z = new List<double>(path.VertexCount);
        int sampleCount = Math.Max(2, (int)Math.Ceiling(totalLength / maxSegmentLength) + 1);
        for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            double targetDistance = sampleIndex == sampleCount - 1
                ? totalLength
                : Math.Min(sampleIndex * maxSegmentLength, totalLength);
            SampleConstraintPathAtDistance(path, cumulativeLengths, targetDistance, out double x, out double y, out double elevation);
            AddConstraintSample(xy, z, x, y, elevation, dedupTol);
        }

        double[] xyArr = xy.ToArray();
        int count = xyArr.Length / 2;
        ComputeSmoothedTangents(xyArr, count, out double[] tangentX, out double[] tangentY);
        return new ConstraintPath(xyArr, z.ToArray(), count, tangentX, tangentY);
    }

    private static double[] ResampleConstraintRow(
        double[] rowXy,
        double[] referenceXy,
        double maxSegmentLength,
        double dedupTol)
    {
        int vertexCount = Math.Min(rowXy.Length, referenceXy.Length) / 2;
        if (vertexCount < 3 || maxSegmentLength <= dedupTol)
            return rowXy;

        double[] cumulativeLengths = BuildConstraintCumulativeLengths(referenceXy, vertexCount);
        double totalLength = cumulativeLengths[^1];
        if (totalLength <= maxSegmentLength + dedupTol)
            return rowXy;

        var resampled = new List<double>(rowXy.Length);
        int sampleCount = Math.Max(2, (int)Math.Ceiling(totalLength / maxSegmentLength) + 1);
        for (int sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            double targetDistance = sampleIndex == sampleCount - 1
                ? totalLength
                : Math.Min(sampleIndex * maxSegmentLength, totalLength);
            SampleConstraintRowAtDistance(rowXy, cumulativeLengths, vertexCount, targetDistance, out double x, out double y);
            if (resampled.Count >= 2)
            {
                double dx = x - resampled[^2];
                double dy = y - resampled[^1];
                if ((dx * dx) + (dy * dy) <= dedupTol * dedupTol)
                    continue;
            }

            resampled.Add(x);
            resampled.Add(y);
        }

        return resampled.ToArray();
    }

    private static double[] BuildConstraintCumulativeLengths(double[] xyVertices, int vertexCount)
    {
        var cumulativeLengths = new double[vertexCount];
        for (int i = 1; i < vertexCount; i++)
        {
            double dx = xyVertices[i * 2] - xyVertices[(i - 1) * 2];
            double dy = xyVertices[i * 2 + 1] - xyVertices[(i - 1) * 2 + 1];
            cumulativeLengths[i] = cumulativeLengths[i - 1] + Math.Sqrt((dx * dx) + (dy * dy));
        }

        return cumulativeLengths;
    }

    private static void SampleConstraintPathAtDistance(
        ConstraintPath path,
        double[] cumulativeLengths,
        double targetDistance,
        out double x,
        out double y,
        out double elevation)
    {
        int segmentIndex = FindConstraintDistanceSegment(cumulativeLengths, targetDistance);
        double segmentStartDistance = cumulativeLengths[segmentIndex - 1];
        double segmentEndDistance = cumulativeLengths[segmentIndex];
        double blend = segmentEndDistance <= segmentStartDistance + 1e-12
            ? 0.0
            : (targetDistance - segmentStartDistance) / (segmentEndDistance - segmentStartDistance);

        double ax = path.XyVertices[(segmentIndex - 1) * 2];
        double ay = path.XyVertices[(segmentIndex - 1) * 2 + 1];
        double bx = path.XyVertices[segmentIndex * 2];
        double by = path.XyVertices[segmentIndex * 2 + 1];
        double az = path.ZValues[segmentIndex - 1];
        double bz = path.ZValues[segmentIndex];
        x = ax + ((bx - ax) * blend);
        y = ay + ((by - ay) * blend);
        elevation = az + ((bz - az) * blend);
    }

    private static void SampleConstraintRowAtDistance(
        double[] rowXy,
        double[] cumulativeLengths,
        int vertexCount,
        double targetDistance,
        out double x,
        out double y)
    {
        int segmentIndex = FindConstraintDistanceSegment(cumulativeLengths, targetDistance);
        double segmentStartDistance = cumulativeLengths[segmentIndex - 1];
        double segmentEndDistance = cumulativeLengths[segmentIndex];
        double blend = segmentEndDistance <= segmentStartDistance + 1e-12
            ? 0.0
            : (targetDistance - segmentStartDistance) / (segmentEndDistance - segmentStartDistance);

        double ax = rowXy[(segmentIndex - 1) * 2];
        double ay = rowXy[(segmentIndex - 1) * 2 + 1];
        double bx = rowXy[segmentIndex * 2];
        double by = rowXy[segmentIndex * 2 + 1];
        x = ax + ((bx - ax) * blend);
        y = ay + ((by - ay) * blend);
    }

    private static int FindConstraintDistanceSegment(double[] cumulativeLengths, double targetDistance)
    {
        if (cumulativeLengths.Length < 2)
            return 1;

        int segmentIndex = 1;
        while (segmentIndex < cumulativeLengths.Length && cumulativeLengths[segmentIndex] < targetDistance)
            segmentIndex++;

        return Math.Min(segmentIndex, cumulativeLengths.Length - 1);
    }

    /// <summary>
    /// Computes a smooth unit tangent at each station of a polyline.
    /// Uses length-weighted averaging of adjacent segment directions.
    /// Zero-length segments are skipped. If adjacent segments are nearly
    /// anti-parallel (dot &lt; -0.7, i.e. ≥ 135° reversal), only the longer
    /// segment contributes to avoid spurious bisector normals at tight U-turns.
    /// Falls back to the longer adjacent segment if the weighted sum is degenerate.
    /// </summary>
    private static void ComputeSmoothedTangents(
        double[] xyVertices,
        int vertexCount,
        out double[] tangentX,
        out double[] tangentY)
    {
        tangentX = new double[vertexCount];
        tangentY = new double[vertexCount];

        if (vertexCount < 2)
            return;

        for (int i = 0; i < vertexCount; i++)
        {
            double sumX = 0.0, sumY = 0.0;

            // Previous segment: from i-1 to i
            double prevDx = 0, prevDy = 0, prevLen = 0;
            if (i > 0)
            {
                prevDx = xyVertices[i * 2]     - xyVertices[(i - 1) * 2];
                prevDy = xyVertices[i * 2 + 1] - xyVertices[(i - 1) * 2 + 1];
                prevLen = Math.Sqrt(prevDx * prevDx + prevDy * prevDy);
                if (prevLen < 1e-9) prevLen = 0; // zero-length — skip
            }

            // Next segment: from i to i+1
            double nextDx = 0, nextDy = 0, nextLen = 0;
            if (i < vertexCount - 1)
            {
                nextDx = xyVertices[(i + 1) * 2]     - xyVertices[i * 2];
                nextDy = xyVertices[(i + 1) * 2 + 1] - xyVertices[i * 2 + 1];
                nextLen = Math.Sqrt(nextDx * nextDx + nextDy * nextDy);
                if (nextLen < 1e-9) nextLen = 0;
            }

            bool hasPrev = prevLen > 1e-9;
            bool hasNext = nextLen > 1e-9;

            if (hasPrev && hasNext)
            {
                // Normalized directions
                double pnx = prevDx / prevLen, pny = prevDy / prevLen;
                double nnx = nextDx / nextLen, nny = nextDy / nextLen;

                // Check for near-U-turn (dot < -0.7, roughly ≥ 135°)
                double dot = pnx * nnx + pny * nny;
                if (dot < -0.7)
                {
                    // Use only the longer segment to avoid bisector artifacts
                    if (prevLen >= nextLen) { sumX = pnx * prevLen; sumY = pny * prevLen; }
                    else                    { sumX = nnx * nextLen; sumY = nny * nextLen; }
                }
                else
                {
                    // Length-weighted blend of both segments
                    sumX = pnx * prevLen + nnx * nextLen;
                    sumY = pny * prevLen + nny * nextLen;
                }
            }
            else if (hasPrev)
            {
                sumX = prevDx; sumY = prevDy;
            }
            else if (hasNext)
            {
                sumX = nextDx; sumY = nextDy;
            }

            double len = Math.Sqrt(sumX * sumX + sumY * sumY);
            if (len < 1e-9)
            {
                // Fully degenerate — fall back to whichever segment is longer
                if (prevLen >= nextLen && prevLen > 1e-9)      { len = prevLen; sumX = prevDx; sumY = prevDy; }
                else if (nextLen > 1e-9)                        { len = nextLen; sumX = nextDx; sumY = nextDy; }
                else                                            { tangentX[i] = 1.0; tangentY[i] = 0.0; continue; }
                len = Math.Sqrt(sumX * sumX + sumY * sumY);
            }

            tangentX[i] = sumX / len;
            tangentY[i] = sumY / len;
        }
    }

    private static void ComputeInsideCornerGuideSuppression(
        double[] xyVertices,
        int vertexCount,
        out bool[] suppressLeftGuides,
        out bool[] suppressRightGuides)
    {
        suppressLeftGuides = new bool[vertexCount];
        suppressRightGuides = new bool[vertexCount];

        if (vertexCount < 3)
            return;

        const double minTurnAngleDeg = 30.0;
        double dotThreshold = Math.Cos(minTurnAngleDeg * Math.PI / 180.0);

        for (int i = 1; i < vertexCount - 1; i++)
        {
            double prevDx = xyVertices[i * 2] - xyVertices[(i - 1) * 2];
            double prevDy = xyVertices[i * 2 + 1] - xyVertices[(i - 1) * 2 + 1];
            double nextDx = xyVertices[(i + 1) * 2] - xyVertices[i * 2];
            double nextDy = xyVertices[(i + 1) * 2 + 1] - xyVertices[i * 2 + 1];

            double prevLen = Math.Sqrt(prevDx * prevDx + prevDy * prevDy);
            double nextLen = Math.Sqrt(nextDx * nextDx + nextDy * nextDy);
            if (prevLen <= 1e-9 || nextLen <= 1e-9)
                continue;

            prevDx /= prevLen;
            prevDy /= prevLen;
            nextDx /= nextLen;
            nextDy /= nextLen;

            double dot = Math.Clamp((prevDx * nextDx) + (prevDy * nextDy), -1.0, 1.0);
            if (dot >= dotThreshold)
                continue;

            double turn = (prevDx * nextDy) - (prevDy * nextDx);
            if (Math.Abs(turn) <= 1e-9)
                continue;

            bool[] target = turn > 0.0 ? suppressLeftGuides : suppressRightGuides;
            int window = dot <= 0.5 ? 2 : 1;
            int start = Math.Max(0, i - window);
            int end = Math.Min(vertexCount - 1, i + window);
            for (int j = start; j <= end; j++)
                target[j] = true;
        }
    }

    private static double ComputeGuideSpacing(double width, double shoulderDistance)
    {
        double baseSpacing = Math.Max(width * 2.0, shoulderDistance * 2.0);
        return Math.Clamp(baseSpacing, 4.0, 15.0);
    }

    private static bool[] ComputeGuideSelection(
        double[] xyVertices,
        int vertexCount,
        double targetSpacing,
        bool[] suppressGuides)
    {
        var keepGuides = new bool[vertexCount];
        if (vertexCount == 0)
            return keepGuides;

        keepGuides[0] = true;
        if (vertexCount == 1)
            return keepGuides;

        int last = vertexCount - 1;
        keepGuides[last] = true;
        if (vertexCount == 2)
            return keepGuides;

        var cumulativeLength = new double[vertexCount];
        for (int i = 1; i < vertexCount; i++)
        {
            double dx = xyVertices[i * 2] - xyVertices[(i - 1) * 2];
            double dy = xyVertices[i * 2 + 1] - xyVertices[(i - 1) * 2 + 1];
            cumulativeLength[i] = cumulativeLength[i - 1] + Math.Sqrt((dx * dx) + (dy * dy));
        }

        double lastKeptLength = cumulativeLength[0];
        int keptInteriorCount = 0;
        for (int i = 1; i < last; i++)
        {
            if (suppressGuides[i])
                continue;

            if (cumulativeLength[i] - lastKeptLength < targetSpacing - 1e-9)
                continue;

            keepGuides[i] = true;
            lastKeptLength = cumulativeLength[i];
            keptInteriorCount++;
        }

        double totalLength = cumulativeLength[last];
        if ((totalLength - lastKeptLength) > targetSpacing * 1.5)
        {
            double targetLength = totalLength - targetSpacing;
            int bestIndex = -1;
            double bestError = double.MaxValue;
            for (int i = 1; i < last; i++)
            {
                if (suppressGuides[i] || keepGuides[i])
                    continue;

                double fromPrevious = cumulativeLength[i] - lastKeptLength;
                double toEnd = totalLength - cumulativeLength[i];
                if (fromPrevious < targetSpacing * 0.5 || toEnd < targetSpacing * 0.5)
                    continue;

                double error = Math.Abs(cumulativeLength[i] - targetLength);
                if (error < bestError)
                {
                    bestError = error;
                    bestIndex = i;
                }
            }

            if (bestIndex >= 0)
                keepGuides[bestIndex] = true;
        }

        if (keptInteriorCount == 0)
        {
            int bestIndex = -1;
            double bestError = double.MaxValue;
            double targetLength = totalLength * 0.5;
            for (int i = 1; i < last; i++)
            {
                if (suppressGuides[i])
                    continue;

                double error = Math.Abs(cumulativeLength[i] - targetLength);
                if (error < bestError)
                {
                    bestError = error;
                    bestIndex = i;
                }
            }

            if (bestIndex >= 0)
                keepGuides[bestIndex] = true;
        }

        return keepGuides;
    }

    private static bool[] BuildStationConstraintSelection(
        ConstraintPath constraintPath,
        PathSectionResolutionStatus[] leftStatuses,
        PathSectionResolutionStatus[] rightStatuses,
        double targetSpacing)
    {
        int vertexCount = constraintPath.VertexCount;
        var keepStations = new bool[vertexCount];
        if (vertexCount == 0)
            return keepStations;

        ComputeInsideCornerGuideSuppression(
            constraintPath.XyVertices,
            vertexCount,
            out bool[] suppressLeftGuides,
            out bool[] suppressRightGuides);

        var suppressStations = new bool[vertexCount];
        for (int i = 0; i < vertexCount; i++)
            suppressStations[i] = suppressLeftGuides[i] || suppressRightGuides[i];

        keepStations = ComputeGuideSelection(
            constraintPath.XyVertices,
            vertexCount,
            Math.Max(targetSpacing, 1e-6),
            suppressStations);

        keepStations[0] = true;
        keepStations[vertexCount - 1] = true;
        int statusCount = Math.Min(vertexCount, Math.Min(leftStatuses.Length, rightStatuses.Length));
        for (int i = 1; i < statusCount; i++)
        {
            if (leftStatuses[i] == leftStatuses[i - 1] &&
                rightStatuses[i] == rightStatuses[i - 1])
            {
                continue;
            }

            keepStations[i - 1] = true;
            keepStations[i] = true;
        }

        return keepStations;
    }

    private static void AddConstraintSample(List<double> xy, List<double> z, double x, double y, double elevation, double dedupTol)
    {
        if (xy.Count >= 2)
        {
            double dx = x - xy[^2];
            double dy = y - xy[^1];
            if (dx * dx + dy * dy <= dedupTol * dedupTol)
                return;
        }

        xy.Add(x);
        xy.Add(y);
        z.Add(elevation);
    }

    private static void ComputeDirection(double[] xyVertices, int vertexCount, int index, out double dx, out double dy)
    {
        if (index == 0)
        {
            dx = xyVertices[2] - xyVertices[0];
            dy = xyVertices[3] - xyVertices[1];
        }
        else if (index == vertexCount - 1)
        {
            dx = xyVertices[index * 2] - xyVertices[(index - 1) * 2];
            dy = xyVertices[index * 2 + 1] - xyVertices[(index - 1) * 2 + 1];
        }
        else
        {
            dx = xyVertices[(index + 1) * 2] - xyVertices[(index - 1) * 2];
            dy = xyVertices[(index + 1) * 2 + 1] - xyVertices[(index - 1) * 2 + 1];
        }

        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-12)
            len = 1.0;

        dx /= len;
        dy /= len;
    }

    private static void GetConstraintPathTangent(ConstraintPath path, int index, out double dx, out double dy)
    {
        if (path.TangentX.Length == path.VertexCount &&
            path.TangentY.Length == path.VertexCount &&
            index >= 0 &&
            index < path.VertexCount)
        {
            dx = path.TangentX[index];
            dy = path.TangentY[index];
            return;
        }

        ComputeDirection(path.XyVertices, path.VertexCount, index, out dx, out dy);
    }

    private static IEnumerable<ConstraintPath> CreateBoundaryClippedRuns(
        double[] xyVertices,
        double[] zValues,
        int vertexCount,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double tolerance)
    {
        return CreateClippedRuns(
            xyVertices,
            zValues,
            vertexCount,
            hasBoundaryLoop,
            boundaryLoop,
            boundaryVertexCount,
            tolerance,
            PreparedBarriers.Empty,
            new SpatialHashGrid2D.QueryScratch(1),
            new List<int>(8));
    }

    private static IEnumerable<ConstraintPath> CreateClippedRuns(
        double[] xyVertices,
        double[] zValues,
        int vertexCount,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double tolerance,
        PreparedBarriers barriers,
        SpatialHashGrid2D.QueryScratch barrierScratch,
        List<int> barrierCandidates)
    {
        if (!hasBoundaryLoop)
        {
            yield return MakeGeometryConstraintPath(CopyLeadingDoubles(xyVertices, vertexCount * 2), CopyLeadingDoubles(zValues, vertexCount), vertexCount);
            yield break;
        }

        var runXy = new List<double>(vertexCount * 2);
        var runZ = new List<double>(vertexCount);

        for (int i = 0; i < vertexCount - 1; i++)
        {
            double startX = xyVertices[i * 2];
            double startY = xyVertices[i * 2 + 1];
            double startZ = zValues[i];
            double endX = xyVertices[(i + 1) * 2];
            double endY = xyVertices[(i + 1) * 2 + 1];
            double endZ = zValues[i + 1];

            if (barriers.Segments.Length > 0 &&
                GradingBarriers.TryClipSegment(
                    barriers,
                    startX,
                    startY,
                    endX,
                    endY,
                    barrierScratch,
                    barrierCandidates,
                    out double clippedEndX,
                    out double clippedEndY))
            {
                double segmentDx = endX - startX;
                double segmentDy = endY - startY;
                double segmentLengthSquared = (segmentDx * segmentDx) + (segmentDy * segmentDy);
                double clipT = segmentLengthSquared <= 1e-12
                    ? 0.0
                    : (((clippedEndX - startX) * segmentDx) + ((clippedEndY - startY) * segmentDy)) / segmentLengthSquared;
                endX = clippedEndX;
                endY = clippedEndY;
                endZ = InterpolateSectionValue(startZ, endZ, Math.Clamp(clipT, 0.0, 1.0));
            }

            List<ClippedSegment> pieces = BoundaryClipper.ClipSegmentToBoundary(
                startX,
                startY,
                startZ,
                endX,
                endY,
                endZ,
                hasBoundaryLoop,
                boundaryLoop,
                boundaryVertexCount,
                tolerance);

            if (pieces.Count == 0)
            {
                    if (runZ.Count >= 2)
                        yield return SimplifyConstraintPathWithClipper(
                            MakeGeometryConstraintPath(runXy.ToArray(), runZ.ToArray(), runZ.Count),
                            tolerance);

                    runXy.Clear();
                    runZ.Clear();
                    continue;
            }

            foreach (ClippedSegment piece in pieces)
            {
                if (runZ.Count > 0)
                {
                    double dx = runXy[^2] - piece.StartX;
                    double dy = runXy[^1] - piece.StartY;
                    if ((dx * dx) + (dy * dy) > tolerance * tolerance)
                    {
                        yield return SimplifyConstraintPathWithClipper(
                            MakeGeometryConstraintPath(runXy.ToArray(), runZ.ToArray(), runZ.Count),
                            tolerance);
                        runXy.Clear();
                        runZ.Clear();
                    }
                }

                AppendRunPoint(runXy, runZ, piece.StartX, piece.StartY, piece.StartZ, tolerance);
                AppendRunPoint(runXy, runZ, piece.EndX, piece.EndY, piece.EndZ, tolerance);
            }
        }

        if (runZ.Count >= 2)
            yield return SimplifyConstraintPathWithClipper(
                MakeGeometryConstraintPath(runXy.ToArray(), runZ.ToArray(), runZ.Count),
                tolerance);
    }

    private static void AppendRunPoint(
        List<double> runXy,
        List<double> runZ,
        double x,
        double y,
        double z,
        double tolerance)
    {
        if (runZ.Count > 0)
        {
            double dx = runXy[^2] - x;
            double dy = runXy[^1] - y;
            if ((dx * dx) + (dy * dy) <= tolerance * tolerance)
            {
                runXy[^2] = x;
                runXy[^1] = y;
                runZ[^1] = z;
                return;
            }
        }

        runXy.Add(x);
        runXy.Add(y);
        runZ.Add(z);
    }

    private static void AddConstraintPolyline(
        List<SurfaceRemesher.ConstraintPolyline> constraints,
        double[] xyVertices,
        double[] zValues,
        int vertexCount)
    {
        if (vertexCount < 2)
            return;

        var points = new double[vertexCount * 3];
        for (int i = 0; i < vertexCount; i++)
        {
            points[i * 3] = xyVertices[i * 2];
            points[i * 3 + 1] = xyVertices[i * 2 + 1];
            points[i * 3 + 2] = zValues[i];
        }

        constraints.Add(new SurfaceRemesher.ConstraintPolyline(points, vertexCount, IsClosed: false, PreserveInputElevation: false));
    }

    private static void AddBoundaryClippedConstraintRuns(
        List<SurfaceRemesher.ConstraintPolyline> constraints,
        double[] xyVertices,
        double[] zValues,
        int vertexCount,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double tolerance)
    {
        foreach (ConstraintPath run in CreateBoundaryClippedRuns(
                     xyVertices,
                     zValues,
                     vertexCount,
                     hasBoundaryLoop,
                     boundaryLoop,
                     boundaryVertexCount,
                     tolerance))
        {
            AddConstraintPolyline(constraints, run.XyVertices, run.ZValues, run.VertexCount);
        }
    }

    private static void AddPathStationConstraints(
        List<SurfaceRemesher.ConstraintPolyline> constraints,
        ConstraintPath centerPath,
        double[] leftRoadXy,
        double[] rightRoadXy,
        double[] leftShoulderXy,
        double[] rightShoulderXy,
        double[] leftShoulderZ,
        double[] rightShoulderZ,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        bool[]? keepStations,
        int vertexCount,
        double tolerance)
    {
        for (int i = 0; i < vertexCount; i++)
        {
            if (keepStations != null && (i >= keepStations.Length || !keepStations[i]))
                continue;

            var points = new List<double>(15);
            double centerX = centerPath.XyVertices[i * 2];
            double centerY = centerPath.XyVertices[i * 2 + 1];
            double centerZ = centerPath.ZValues[i];

            AppendConstraintPoint(points, leftShoulderXy[i * 2], leftShoulderXy[i * 2 + 1], leftShoulderZ[i], tolerance);
            AppendConstraintPoint(points, leftRoadXy[i * 2], leftRoadXy[i * 2 + 1], centerZ, tolerance);
            AppendConstraintPoint(points, centerX, centerY, centerZ, tolerance);
            AppendConstraintPoint(points, rightRoadXy[i * 2], rightRoadXy[i * 2 + 1], centerZ, tolerance);
            AppendConstraintPoint(points, rightShoulderXy[i * 2], rightShoulderXy[i * 2 + 1], rightShoulderZ[i], tolerance);

            int pointCount = points.Count / 3;
            if (pointCount < 2)
                continue;

            var stationXy = new double[pointCount * 2];
            var stationZ = new double[pointCount];
            for (int pointIndex = 0; pointIndex < pointCount; pointIndex++)
            {
                stationXy[pointIndex * 2] = points[pointIndex * 3];
                stationXy[(pointIndex * 2) + 1] = points[(pointIndex * 3) + 1];
                stationZ[pointIndex] = points[(pointIndex * 3) + 2];
            }

            AddBoundaryClippedConstraintRuns(
                constraints,
                stationXy,
                stationZ,
                pointCount,
                hasBoundaryLoop,
                boundaryLoop,
                boundaryVertexCount,
                tolerance);
        }
    }

    private static void AppendConstraintPoint(List<double> points, double x, double y, double z, double tolerance)
    {
        if (points.Count >= 3)
        {
            double dx = x - points[^3];
            double dy = y - points[^2];
            if ((dx * dx) + (dy * dy) <= tolerance * tolerance)
            {
                points[^3] = x;
                points[^2] = y;
                points[^1] = z;
                return;
            }
        }

        points.Add(x);
        points.Add(y);
        points.Add(z);
    }

    private static ConstraintPath SimplifyConstraintPathWithClipper(ConstraintPath path, double tolerance)
    {
        if (path.VertexCount < 3 ||
            IsApproximatelyStraight(path.XyVertices, path.VertexCount, tolerance) ||
            !ClipperGeometry.TrySimplifyOpenPolyline(path.XyVertices, tolerance, out double[] simplifiedXy))
        {
            return path;
        }

        int simplifiedCount = simplifiedXy.Length / 2;
        if (simplifiedCount < 2)
            return path;

        var simplifiedZ = new double[simplifiedCount];
        for (int i = 0; i < simplifiedCount; i++)
        {
            if (!TrySampleConstraintPathZ(
                path.XyVertices,
                path.ZValues,
                path.VertexCount,
                simplifiedXy[i * 2],
                simplifiedXy[i * 2 + 1],
                out simplifiedZ[i]))
            {
                return path;
            }
        }

        return MakeGeometryConstraintPath(simplifiedXy, simplifiedZ, simplifiedCount);
    }

    private static bool IsApproximatelyStraight(double[] xyVertices, int vertexCount, double tolerance)
    {
        if (vertexCount < 3)
            return true;

        double ax = xyVertices[0];
        double ay = xyVertices[1];
        double bx = xyVertices[(vertexCount - 1) * 2];
        double by = xyVertices[((vertexCount - 1) * 2) + 1];
        double dx = bx - ax;
        double dy = by - ay;
        double lengthSq = (dx * dx) + (dy * dy);
        if (lengthSq <= tolerance * tolerance)
            return true;

        double toleranceSq = tolerance * tolerance;
        for (int i = 1; i < vertexCount - 1; i++)
        {
            double px = xyVertices[i * 2];
            double py = xyVertices[i * 2 + 1];
            double t = Math.Clamp((((px - ax) * dx) + ((py - ay) * dy)) / lengthSq, 0.0, 1.0);
            double projectedX = ax + (dx * t);
            double projectedY = ay + (dy * t);
            double offsetX = px - projectedX;
            double offsetY = py - projectedY;
            if (((offsetX * offsetX) + (offsetY * offsetY)) > toleranceSq)
                return false;
        }

        return true;
    }

    private static bool TrySampleConstraintPathZ(
        double[] xyVertices,
        double[] zValues,
        int vertexCount,
        double px,
        double py,
        out double sampledZ)
    {
        sampledZ = 0.0;
        double bestDistanceSq = double.MaxValue;
        if (vertexCount < 2)
            return false;

        for (int i = 0; i < vertexCount - 1; i++)
        {
            double ax = xyVertices[i * 2];
            double ay = xyVertices[i * 2 + 1];
            double bx = xyVertices[(i + 1) * 2];
            double by = xyVertices[(i + 1) * 2 + 1];
            double dx = bx - ax;
            double dy = by - ay;
            double segmentLengthSq = (dx * dx) + (dy * dy);
            double t = 0.0;
            if (segmentLengthSq > 1e-18)
                t = Math.Clamp((((px - ax) * dx) + ((py - ay) * dy)) / segmentLengthSq, 0.0, 1.0);

            double projectedX = ax + (dx * t);
            double projectedY = ay + (dy * t);
            double offsetX = px - projectedX;
            double offsetY = py - projectedY;
            double distanceSq = (offsetX * offsetX) + (offsetY * offsetY);
            if (distanceSq >= bestDistanceSq)
                continue;

            bestDistanceSq = distanceSq;
            sampledZ = InterpolateSectionValue(zValues[i], zValues[i + 1], t);
        }

        return bestDistanceSq < double.MaxValue;
    }

    private static int NormalizeConstraintPointCount(SurfaceRemesher.ConstraintPolyline constraint, double tolerance)
    {
        if (!constraint.IsClosed || constraint.PointCount < 3)
            return constraint.PointCount;

        int last = constraint.PointCount - 1;
        double dx = constraint.Points[last * 3] - constraint.Points[0];
        double dy = constraint.Points[last * 3 + 1] - constraint.Points[1];
        return dx * dx + dy * dy <= tolerance * tolerance
            ? last
            : constraint.PointCount;
    }

    private static bool IsBlockedByBarrier(
        PreparedBarriers preparedBarriers,
        double halfWidth,
        ClosestPathLocation closest,
        double px,
        double py,
        SpatialHashGrid2D.QueryScratch barrierScratch,
        List<int> barrierCandidates)
    {
        if (preparedBarriers.Segments.Length == 0)
            return false;

        double side = closest.SideSign >= 0.0 ? 1.0 : -1.0;
        double normalX = -closest.DirectionY;
        double normalY = closest.DirectionX;
        double edgeX = closest.ProjectedX + (normalX * halfWidth * side);
        double edgeY = closest.ProjectedY + (normalY * halfWidth * side);

        return GradingBarriers.IsCrossedByBarrier(
            preparedBarriers, edgeX, edgeY, px, py, barrierScratch, barrierCandidates);
    }

    private static double[] CopyLeadingDoubles(double[] values, int length)
    {
        var copy = new double[length];
        Array.Copy(values, copy, length);
        return copy;
    }

    private static bool HasDistinctShoulderSamples(double[] edgeXy, double[] shoulderXy, int vertexCount, double tolerance)
    {
        double tolSq = tolerance * tolerance;
        for (int i = 0; i < vertexCount; i++)
        {
            double dx = shoulderXy[i * 2] - edgeXy[i * 2];
            double dy = shoulderXy[(i * 2) + 1] - edgeXy[(i * 2) + 1];
            if ((dx * dx) + (dy * dy) > tolSq)
                return true;
        }

        return false;
    }

    private static int[]? AddShoulderConstraint(
        double[] shoulderXy,
        int vertexCount,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double tolerance,
        Func<double, double, int> addVertex,
        List<(int a, int b)> segList,
        PreparedBarriers barriers,
        SpatialHashGrid2D.QueryScratch barrierScratch,
        List<int> barrierCandidates)
    {
        var indices = new int[vertexCount];
        Array.Fill(indices, -1);
        bool hasSegment = false;

        for (int i = 0; i < vertexCount - 1; i++)
        {
            double startX = shoulderXy[i * 2];
            double startY = shoulderXy[i * 2 + 1];
            double endX = shoulderXy[(i + 1) * 2];
            double endY = shoulderXy[(i + 1) * 2 + 1];

            bool clippedByBarrier = GradingBarriers.TryClipSegment(
                barriers, startX, startY, endX, endY,
                barrierScratch, barrierCandidates,
                out endX, out endY);

            List<ClippedSegment> pieces = BoundaryClipper.ClipSegmentToBoundary(
                startX, startY, 0.0,
                endX, endY, 0.0,
                hasBoundaryLoop,
                boundaryLoop,
                boundaryVertexCount,
                tolerance);

            foreach (ClippedSegment piece in pieces)
            {
                int startIndex = piece.StartT <= 1e-9
                    ? EnsureShoulderSampleIndex(indices, i, shoulderXy, addVertex)
                    : addVertex(piece.StartX, piece.StartY);
                int endIndex = (!clippedByBarrier && piece.EndT >= 1.0 - 1e-9)
                    ? EnsureShoulderSampleIndex(indices, i + 1, shoulderXy, addVertex)
                    : addVertex(piece.EndX, piece.EndY);

                if (startIndex == endIndex)
                    continue;

                segList.Add((startIndex, endIndex));
                hasSegment = true;
            }
        }

        return hasSegment ? indices : null;
    }

    private static void AddShoulderGuideConstraints(
        List<SurfaceRemesher.ConstraintPolyline> constraints,
        double[] roadXy,
        double[] shoulderXy,
        double[] roadZValues,
        double[] shoulderZValues,
        int vertexCount,
        bool[]? keepGuides,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double tolerance)
    {
        AddShoulderGuideConstraints(
            constraints,
            roadXy,
            shoulderXy,
            roadZValues,
            shoulderZValues,
            vertexCount,
            keepGuides,
            hasBoundaryLoop,
            boundaryLoop,
            boundaryVertexCount,
            tolerance,
            PreparedBarriers.Empty,
            new SpatialHashGrid2D.QueryScratch(1),
            new List<int>(8));
    }

    private static void AddShoulderGuideConstraints(
        List<SurfaceRemesher.ConstraintPolyline> constraints,
        double[] roadXy,
        double[] shoulderXy,
        double[] roadZValues,
        double[] shoulderZValues,
        int vertexCount,
        bool[]? keepGuides,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double tolerance,
        PreparedBarriers barriers,
        SpatialHashGrid2D.QueryScratch barrierScratch,
        List<int> barrierCandidates)
    {
        for (int i = 0; i < vertexCount; i++)
        {
            if (keepGuides != null && !keepGuides[i])
                continue;

            double roadX = roadXy[i * 2];
            double roadY = roadXy[i * 2 + 1];
            double shoulderX = shoulderXy[i * 2];
            double shoulderY = shoulderXy[i * 2 + 1];
            double shoulderZ = shoulderZValues[i];

            if (barriers.Segments.Length > 0)
            {
                double originalShoulderX = shoulderX;
                double originalShoulderY = shoulderY;
                GradingBarriers.TryClipSegment(
                    barriers,
                    roadX,
                    roadY,
                    shoulderX,
                    shoulderY,
                    barrierScratch,
                    barrierCandidates,
                    out shoulderX,
                    out shoulderY);

                double dx = originalShoulderX - roadX;
                double dy = originalShoulderY - roadY;
                double lengthSquared = (dx * dx) + (dy * dy);
                if (lengthSquared > 1e-12)
                {
                    double clipT = (((shoulderX - roadX) * dx) + ((shoulderY - roadY) * dy)) / lengthSquared;
                    shoulderZ = InterpolateSectionValue(roadZValues[i], shoulderZValues[i], Math.Clamp(clipT, 0.0, 1.0));
                }
            }

            List<ClippedSegment> pieces = BoundaryClipper.ClipSegmentToBoundary(
                roadX,
                roadY,
                roadZValues[i],
                shoulderX,
                shoulderY,
                shoulderZ,
                hasBoundaryLoop,
                boundaryLoop,
                boundaryVertexCount,
                tolerance);

            foreach (ClippedSegment piece in pieces)
            {
                double dx = piece.EndX - piece.StartX;
                double dy = piece.EndY - piece.StartY;
                if ((dx * dx) + (dy * dy) <= tolerance * tolerance)
                    continue;

                constraints.Add(new SurfaceRemesher.ConstraintPolyline(
                    new[]
                    {
                        piece.StartX, piece.StartY, piece.StartZ,
                        piece.EndX, piece.EndY, piece.EndZ
                    },
                    PointCount: 2,
                    IsClosed: false,
                    PreserveInputElevation: false));
            }
        }
    }

    private static void AddShoulderGuideSegments(
        List<double> xyList,
        int[] roadIndices,
        double[]? shoulderXy,
        int[]? shoulderIndices,
        int vertexCount,
        bool[]? keepGuides,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double tolerance,
        Func<double, double, int> addVertex,
        List<(int a, int b)> segList,
        PreparedBarriers barriers,
        SpatialHashGrid2D.QueryScratch barrierScratch,
        List<int> barrierCandidates)
    {
        if (shoulderXy == null)
            return;

        for (int i = 0; i < vertexCount; i++)
        {
            if (keepGuides != null && !keepGuides[i])
                continue;

            double roadX = xyList[roadIndices[i] * 2];
            double roadY = xyList[roadIndices[i] * 2 + 1];
            double shoulderX = shoulderXy[i * 2];
            double shoulderY = shoulderXy[i * 2 + 1];

            bool clippedByBarrier = GradingBarriers.TryClipSegment(
                barriers, roadX, roadY, shoulderX, shoulderY,
                barrierScratch, barrierCandidates,
                out shoulderX, out shoulderY);

            List<ClippedSegment> pieces = BoundaryClipper.ClipSegmentToBoundary(
                roadX, roadY, 0.0,
                shoulderX, shoulderY, 0.0,
                hasBoundaryLoop,
                boundaryLoop,
                boundaryVertexCount,
                tolerance);

            foreach (ClippedSegment piece in pieces)
            {
                int startIndex = piece.StartT <= 1e-9
                    ? roadIndices[i]
                    : addVertex(piece.StartX, piece.StartY);
                int endIndex = (!clippedByBarrier && piece.EndT >= 1.0 - 1e-9 && shoulderIndices != null && shoulderIndices[i] >= 0)
                    ? shoulderIndices[i]
                    : addVertex(piece.EndX, piece.EndY);

                if (startIndex != endIndex)
                    segList.Add((startIndex, endIndex));
            }
        }
    }

    private static int EnsureShoulderSampleIndex(
        int[] indices,
        int pointIndex,
        double[] shoulderXy,
        Func<double, double, int> addVertex)
    {
        if (indices[pointIndex] >= 0)
            return indices[pointIndex];

        indices[pointIndex] = addVertex(shoulderXy[pointIndex * 2], shoulderXy[pointIndex * 2 + 1]);
        return indices[pointIndex];
    }

    private static bool IsInsideOrOnBoundary(
        double x,
        double y,
        bool hasBoundaryLoop,
        double[] boundaryLoop,
        int boundaryVertexCount,
        double tolerance)
    {
        return BoundaryClipper.IsInsideOrOnBoundary(x, y, hasBoundaryLoop, boundaryLoop, boundaryVertexCount, tolerance);
    }

    private static bool TryFindClosestPathSample(PathDefinition path, double px, double py, out double closestDist, out double closestPathZ)
    {
        if (!TryFindClosestPathLocation(path.XyVertices, path.ZValues, path.VertexCount, px, py, out ClosestPathLocation closest))
        {
            closestDist = double.MaxValue;
            closestPathZ = 0.0;
            return false;
        }

        closestDist = closest.Distance;
        closestPathZ = closest.PathZ;
        return true;
    }

    private static bool TryFindClosestPathLocation(
        ConstraintPath path,
        double px,
        double py,
        out ClosestPathLocation closest)
    {
        return TryFindClosestPathLocation(
            path.XyVertices, path.ZValues, path.VertexCount,
            path.TangentX, path.TangentY,
            px, py, out closest);
    }

    private static bool TryFindClosestPathLocation(
        double[] xyVertices,
        double[] zValues,
        int vertexCount,
        double px,
        double py,
        out ClosestPathLocation closest)
    {
        return TryFindClosestPathLocation(
            xyVertices, zValues, vertexCount,
            null, null,
            px, py, out closest);
    }

    /// <summary>
    /// Finds the closest point on the path polyline and returns a
    /// <see cref="ClosestPathLocation"/>. When smooth tangent arrays are
    /// provided, <c>SideSign</c> and <c>DirectionX/Y</c> are computed from
    /// the smoothly interpolated tangent at the closest position rather than
    /// the raw segment direction. This eliminates the discrete SideSign flip
    /// that occurs at segment-ownership (Voronoi) boundaries near path bends,
    /// which was the primary cause of cut/fill polarity inversions in the
    /// shoulder reference-profile lookup.
    /// </summary>
    private static bool TryFindClosestPathLocation(
        double[] xyVertices,
        double[] zValues,
        int vertexCount,
        double[]? tangentX,
        double[]? tangentY,
        double px,
        double py,
        out ClosestPathLocation closest)
    {
        double closestDistSq = double.MaxValue;
        closest = default;

        for (int s = 0; s < vertexCount - 1; s++)
        {
            double ax = xyVertices[s * 2];
            double ay = xyVertices[s * 2 + 1];
            double bx = xyVertices[(s + 1) * 2];
            double by = xyVertices[(s + 1) * 2 + 1];

            double sdx = bx - ax;
            double sdy = by - ay;
            double segLen = sdx * sdx + sdy * sdy;
            if (segLen < 1e-20)
                continue;

            double t = ((px - ax) * sdx + (py - ay) * sdy) / segLen;
            t = Math.Clamp(t, 0.0, 1.0);

            double projX = ax + t * sdx;
            double projY = ay + t * sdy;
            double dx = px - projX;
            double dy = py - projY;
            double distSq = (dx * dx) + (dy * dy);

            if (distSq < closestDistSq)
            {
                double segLength = Math.Sqrt(segLen);

                // Compute direction and SideSign from smooth tangent when available.
                // Linearly interpolate station tangents at parameter t, then normalize.
                // Falls back to raw segment direction when no tangents are provided.
                double dirX, dirY, sideSign;
                if (tangentX != null && tangentY != null &&
                    s < tangentX.Length && s + 1 < tangentX.Length)
                {
                    double blendX = tangentX[s] + t * (tangentX[s + 1] - tangentX[s]);
                    double blendY = tangentY[s] + t * (tangentY[s + 1] - tangentY[s]);
                    double blendLen = Math.Sqrt(blendX * blendX + blendY * blendY);
                    if (blendLen > 1e-9)
                    {
                        dirX = blendX / blendLen;
                        dirY = blendY / blendLen;
                    }
                    else
                    {
                        // Interpolated tangent degenerate — fall back to segment direction
                        dirX = sdx / segLength;
                        dirY = sdy / segLength;
                    }
                    // SideSign: cross product of smooth tangent with (query − projected)
                    sideSign = dirX * (py - projY) - dirY * (px - projX);
                }
                else
                {
                    dirX = sdx / segLength;
                    dirY = sdy / segLength;
                    sideSign = (sdx * (py - ay)) - (sdy * (px - ax));
                }

                closestDistSq = distSq;
                closest = new ClosestPathLocation(
                    s,
                    t,
                    Math.Sqrt(distSq),
                    zValues[s] + t * (zValues[s + 1] - zValues[s]),
                    sideSign,
                    projX,
                    projY,
                    dirX,
                    dirY);
            }
        }

        return closestDistSq < double.MaxValue;
    }

    /// <summary>
    /// Build GradingResult with volumes and daylight line.
    /// </summary>
    private static GradingResult BuildResult(
        double[] outXy, double[] origZ, double[] newZ,
        double[] finalVerts, int vertCount,
        int[] finalFaces, int faceCount,
        IReadOnlyList<OutputPolyline>? outputPolylines = null,
        IReadOnlyList<GradingPatch>? patchSummaries = null,
        IReadOnlyList<string>? diagnostics = null,
        IReadOnlyList<GradingDiagnostic>? structuredDiagnostics = null)
    {
        return GradingResultBuilder.BuildFromComponents(
            outXy,
            origZ,
            newZ,
            finalVerts, vertCount,
            finalFaces, faceCount,
            outputPolylines,
            diagnostics,
            patchSummaries: patchSummaries,
            structuredDiagnostics: structuredDiagnostics);
    }

    private static void IncrEdge(Dictionary<long, int> dict, int a, int b)
    {
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        dict[key] = dict.GetValueOrDefault(key, 0) + 1;
    }

}
