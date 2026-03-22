using TriangleNet;
using TriangleNet.Geometry;
using TriangleNet.Meshing;
using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

/// <summary>
/// Grades a terrain mesh by flattening areas inside boundary curves
/// to a target elevation, with controlled slope transitions.
/// Re-triangulates the entire mesh with pad boundaries as constrained edges.
/// </summary>
public static class PadGrader
{
    public sealed class PadBoundary
    {
        public double[] XyVertices { get; }
        public int VertexCount { get; }
        public double TargetZ { get; }
        public double SlopeAngleDeg { get; }
        public double MaxDistance { get; }

        public PadBoundary(double[] xyVertices, int vertexCount, double targetZ,
                           double slopeAngleDeg = 33.0, double maxDistance = 0.0)
        {
            XyVertices = xyVertices;
            VertexCount = vertexCount;
            TargetZ = targetZ;
            SlopeAngleDeg = Math.Max(0.1, Math.Min(89.9, slopeAngleDeg));
            MaxDistance = maxDistance;
        }
    }

    public sealed class LockCurve
    {
        public double[] XyVertices { get; }
        public int VertexCount { get; }

        public LockCurve(double[] xyVertices, int vertexCount)
        {
            XyVertices = xyVertices;
            VertexCount = vertexCount;
        }
    }

    private sealed class PadTopologyResult
    {
        public required double[] Vertices { get; init; }
        public required int VertexCount { get; init; }
        public required int[] Faces { get; init; }
        public required int FaceCount { get; init; }
    }

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
        double maxArea,
        double minAngle,
        out string? errorMessage)
    {
        errorMessage = null;

        if (!ValidatePads(pads, out errorMessage))
            return null;

        PadTopologyResult? topology = TryTriangulatePadTopology(
            vertices,
            vertexCount,
            faces,
            faceCount,
            pads,
            lockCurves,
            maxArea,
            minAngle,
            out string? topologyMessage);

        if (topology == null)
        {
            errorMessage = topologyMessage ?? "Triangulation failed.";
            return null;
        }

        if (!string.IsNullOrWhiteSpace(topologyMessage))
            errorMessage = topologyMessage;

        double[] gradedVertices = ApplyGradingZ(topology.Vertices, topology.VertexCount, pads);
        return BuildResult(topology.Vertices, topology.VertexCount, topology.Faces, topology.FaceCount, gradedVertices);
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
        PadBoundary[] pads)
    {
        if (pads.Length == 0)
            return (double[])topologyVertices.Clone();

        var gradedVertices = (double[])topologyVertices.Clone();
        ApplyGradingToVertices(gradedVertices, topologyVertices, vertexCount, pads);
        return gradedVertices;
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
            if (pad.VertexCount < 3 || pad.XyVertices.Length < pad.VertexCount * 2)
            {
                errorMessage = "Each pad must have at least 3 valid vertices.";
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

        var xyList = new List<double>(vertexCount * 2);
        var zList = new List<double>(vertexCount);
        var segList = new List<(int a, int b)>();

        var vertHash = new SpatialHash(dedupTol);

        for (int i = 0; i < vertexCount; i++)
        {
            double x = vertices[i * 3];
            double y = vertices[i * 3 + 1];
            xyList.Add(x);
            xyList.Add(y);
            zList.Add(vertices[i * 3 + 2]);
            vertHash.Insert(i, x, y);
        }

        var faceGrid = new FaceGrid(vertices, vertexCount, faces, faceCount);
        bool hasBoundaryLoop = TryBuildBoundaryLoop(vertices, faces, faceCount, out var boundaryLoop, out int boundaryVertexCount);

        // Add mesh boundary edges as constraints (keeps triangulation within original mesh).
        var edgeFaceCount = new Dictionary<long, int>();
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
                segList.Add((a, b));
            }
        }

        foreach (var pad in pads)
        {
            var padIndices = new int[pad.VertexCount];
            for (int i = 0; i < pad.VertexCount; i++)
            {
                double px = pad.XyVertices[i * 2];
                double py = pad.XyVertices[i * 2 + 1];

                int near = vertHash.FindNearest(xyList, px, py, dedupTol);
                if (near >= 0)
                {
                    padIndices[i] = near;
                }
                else
                {
                    padIndices[i] = zList.Count;
                    xyList.Add(px);
                    xyList.Add(py);
                    zList.Add(faceGrid.InterpolateZ(px, py));
                    vertHash.Insert(padIndices[i], px, py);
                }
            }

            for (int i = 0; i < pad.VertexCount; i++)
            {
                int a = padIndices[i];
                int b = padIndices[(i + 1) % pad.VertexCount];
                if (a != b)
                    segList.Add((a, b));
            }

            double shoulderDistance = ComputePadTransitionDistance(vertices, vertexCount, pad);
            AddPadShoulderConstraint(
                pad,
                shoulderDistance,
                xyList,
                zList,
                vertHash,
                faceGrid,
                segList,
                dedupTol,
                hasBoundaryLoop ? boundaryLoop : null,
                hasBoundaryLoop ? boundaryVertexCount : 0);
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
                        zList.Add(faceGrid.InterpolateZ(lx, ly));
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

        IMesh? triMesh = TriangulationHelper.Triangulate(
            xyList,
            totalVerts,
            segList,
            maxArea,
            minAngle,
            out string? triWarning,
            convex: false);

        if (triMesh == null)
        {
            warningOrError = triWarning ?? "Triangulation failed.";
            return null;
        }

        if (!string.IsNullOrWhiteSpace(triWarning))
            warningOrError = triWarning;

        var extracted = TriangleNetExtractor.Extract(triMesh);
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
                : faceGrid.InterpolateZ(x, y);

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
            FaceCount = outFaceCount
        };
    }

    private static void ApplyGradingToVertices(
        double[] gradedVertices,
        double[] originalVertices,
        int vertexCount,
        PadBoundary[] pads)
    {
        double globalMinX = double.MaxValue;
        double globalMaxX = double.MinValue;
        double globalMinY = double.MaxValue;
        double globalMaxY = double.MinValue;
        double globalMaxTrans = 0;

        for (int p = 0; p < pads.Length; p++)
        {
            var pad = pads[p];
            double slopeRatio = Math.Tan(pad.SlopeAngleDeg * Math.PI / 180.0);

            for (int i = 0; i < pad.VertexCount; i++)
            {
                double vx = pad.XyVertices[i * 2];
                double vy = pad.XyVertices[i * 2 + 1];
                if (vx < globalMinX) globalMinX = vx;
                if (vx > globalMaxX) globalMaxX = vx;
                if (vy < globalMinY) globalMinY = vy;
                if (vy > globalMaxY) globalMaxY = vy;
            }

            double maxZDiff = 0;
            for (int i = 0; i < vertexCount; i++)
            {
                double dz = Math.Abs(originalVertices[i * 3 + 2] - pad.TargetZ);
                if (dz > maxZDiff)
                    maxZDiff = dz;
            }

            double transitionDistance = slopeRatio > 1e-12 ? maxZDiff / slopeRatio : 100.0;
            if (pad.MaxDistance > 0)
                transitionDistance = Math.Min(transitionDistance, pad.MaxDistance);
            if (transitionDistance > globalMaxTrans)
                globalMaxTrans = transitionDistance;
        }

        globalMinX -= globalMaxTrans;
        globalMaxX += globalMaxTrans;
        globalMinY -= globalMaxTrans;
        globalMaxY += globalMaxTrans;

        for (int i = 0; i < vertexCount; i++)
        {
            double px = gradedVertices[i * 3];
            double py = gradedVertices[i * 3 + 1];

            if (px < globalMinX || px > globalMaxX || py < globalMinY || py > globalMaxY)
                continue;

            int insidePadIdx = -1;
            for (int p = pads.Length - 1; p >= 0; p--)
            {
                if (PointInPolygon(px, py, pads[p].XyVertices, pads[p].VertexCount))
                {
                    insidePadIdx = p;
                    break;
                }
            }

            if (insidePadIdx >= 0)
            {
                gradedVertices[i * 3 + 2] = pads[insidePadIdx].TargetZ;
                continue;
            }

            double nearestDist = double.MaxValue;
            int nearestPadIdx = -1;
            for (int p = 0; p < pads.Length; p++)
            {
                double dist = DistToPolygon(px, py, pads[p].XyVertices, pads[p].VertexCount);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearestPadIdx = p;
                }
            }

            if (nearestPadIdx < 0)
                continue;

            var pad = pads[nearestPadIdx];
            double slopeRatio = Math.Tan(pad.SlopeAngleDeg * Math.PI / 180.0);
            double dz = originalVertices[i * 3 + 2] - pad.TargetZ;
            double absDz = Math.Abs(dz);

            double neededDist = slopeRatio > 1e-12 ? absDz / slopeRatio : double.MaxValue;
            if (pad.MaxDistance > 0)
                neededDist = Math.Min(neededDist, pad.MaxDistance);

            if (nearestDist >= neededDist)
                continue;

            double rise = nearestDist * slopeRatio;
            if (rise < absDz)
                gradedVertices[i * 3 + 2] = pad.TargetZ + Math.Sign(dz) * rise;
        }
    }

    private static double ComputePadTransitionDistance(double[] vertices, int vertexCount, PadBoundary pad)
    {
        double slopeRatio = Math.Tan(pad.SlopeAngleDeg * Math.PI / 180.0);
        double maxZDiff = 0;
        for (int i = 0; i < vertexCount; i++)
        {
            double dz = Math.Abs(vertices[i * 3 + 2] - pad.TargetZ);
            if (dz > maxZDiff)
                maxZDiff = dz;
        }

        double transitionDistance = slopeRatio > 1e-12 ? maxZDiff / slopeRatio : 100.0;
        if (pad.MaxDistance > 0)
            transitionDistance = Math.Min(transitionDistance, pad.MaxDistance);
        return transitionDistance;
    }

    private static void AddPadShoulderConstraint(
        PadBoundary pad,
        double shoulderDistance,
        List<double> xyList,
        List<double> zList,
        SpatialHash vertHash,
        FaceGrid faceGrid,
        List<(int a, int b)> segList,
        double dedupTol,
        double[]? boundaryLoop,
        int boundaryVertexCount)
    {
        if (shoulderDistance <= dedupTol)
            return;

        if (!TryBuildOffsetPolygon(pad.XyVertices, pad.VertexCount, shoulderDistance, out var shoulderXy))
            return;

        if (boundaryLoop != null && !AllPointsInsideOrOnBoundary(shoulderXy, pad.VertexCount, boundaryLoop, boundaryVertexCount, dedupTol))
            return;

        var shoulderIndices = new int[pad.VertexCount];
        for (int i = 0; i < pad.VertexCount; i++)
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
                zList.Add(faceGrid.InterpolateZ(px, py));
                vertHash.Insert(shoulderIndices[i], px, py);
            }
        }

        for (int i = 0; i < pad.VertexCount; i++)
        {
            int a = shoulderIndices[i];
            int b = shoulderIndices[(i + 1) % pad.VertexCount];
            if (a != b)
                segList.Add((a, b));
        }
    }

    private static bool TryBuildOffsetPolygon(double[] polygonXy, int vertexCount, double distance, out double[] offsetXy)
    {
        offsetXy = Array.Empty<double>();
        if (vertexCount < 3 || distance <= 1e-9)
            return false;

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
            return false;

        bool ccw = signedArea > 0;
        offsetXy = new double[vertexCount * 2];

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
                return false;

            double n0x = ccw ? dy0 / len0 : -dy0 / len0;
            double n0y = ccw ? -dx0 / len0 : dx0 / len0;
            double n1x = ccw ? dy1 / len1 : -dy1 / len1;
            double n1y = ccw ? -dx1 / len1 : dx1 / len1;

            double line0x = x1 + n0x * distance;
            double line0y = y1 + n0y * distance;
            double line1x = x1 + n1x * distance;
            double line1y = y1 + n1y * distance;

            if (TryIntersectLines(line0x, line0y, dx0, dy0, line1x, line1y, dx1, dy1, out double ix, out double iy))
            {
                double offsetLen = Math.Sqrt((ix - x1) * (ix - x1) + (iy - y1) * (iy - y1));
                if (offsetLen <= distance * 4.0 && !double.IsNaN(offsetLen) && !double.IsInfinity(offsetLen))
                {
                    offsetXy[i * 2] = ix;
                    offsetXy[i * 2 + 1] = iy;
                    continue;
                }
            }

            double bisX = n0x + n1x;
            double bisY = n0y + n1y;
            double bisLen = Math.Sqrt(bisX * bisX + bisY * bisY);
            if (bisLen < 1e-12)
            {
                bisX = n0x;
                bisY = n0y;
                bisLen = Math.Sqrt(bisX * bisX + bisY * bisY);
            }

            offsetXy[i * 2] = x1 + bisX / bisLen * distance;
            offsetXy[i * 2 + 1] = y1 + bisY / bisLen * distance;
        }

        return true;
    }

    internal static bool TryBuildBoundaryLoop(double[] vertices, int[] faces, int faceCount, out double[] boundaryXy, out int boundaryVertexCount)
    {
        boundaryXy = Array.Empty<double>();
        boundaryVertexCount = 0;

        var edgeFaceCount = new Dictionary<long, int>();
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
            AddBoundaryNeighbor(adjacency, a, b);
            AddBoundaryNeighbor(adjacency, b, a);
            segmentCount++;
        }

        if (segmentCount < 3 || adjacency.Count == 0)
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

        boundaryVertexCount = order.Count;
        boundaryXy = new double[boundaryVertexCount * 2];
        for (int i = 0; i < boundaryVertexCount; i++)
        {
            int vertexIndex = order[i];
            boundaryXy[i * 2] = vertices[vertexIndex * 3];
            boundaryXy[i * 2 + 1] = vertices[vertexIndex * 3 + 1];
        }

        return true;
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
        for (int i = 0; i < vertexCount; i++)
        {
            double px = xy[i * 2];
            double py = xy[i * 2 + 1];
            if (PointInPolygon(px, py, boundaryLoop, boundaryVertexCount))
                continue;

            if (DistToPolygon(px, py, boundaryLoop, boundaryVertexCount) <= tolerance)
                continue;

            return false;
        }

        return true;
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

    private static GradingResult BuildResult(
        double[] originalVertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        double[] gradedVertices)
    {
        double cutVol = 0;
        double fillVol = 0;
        for (int f = 0; f < faceCount; f++)
        {
            int i0 = faces[f * 3];
            int i1 = faces[f * 3 + 1];
            int i2 = faces[f * 3 + 2];

            double area2d = Math.Abs(
                (gradedVertices[i1 * 3] - gradedVertices[i0 * 3]) * (gradedVertices[i2 * 3 + 1] - gradedVertices[i0 * 3 + 1])
              - (gradedVertices[i2 * 3] - gradedVertices[i0 * 3]) * (gradedVertices[i1 * 3 + 1] - gradedVertices[i0 * 3 + 1]))
                * 0.5;

            double dz0 = gradedVertices[i0 * 3 + 2] - originalVertices[i0 * 3 + 2];
            double dz1 = gradedVertices[i1 * 3 + 2] - originalVertices[i1 * 3 + 2];
            double dz2 = gradedVertices[i2 * 3 + 2] - originalVertices[i2 * 3 + 2];
            double avgDz = (dz0 + dz1 + dz2) / 3.0;

            double vol = area2d * avgDz;
            if (vol > 0)
                fillVol += vol;
            else
                cutVol += -vol;
        }

        var daylightPts = new List<double>();
        var processedEdges = new HashSet<long>();

        for (int f = 0; f < faceCount; f++)
        {
            int i0 = faces[f * 3];
            int i1 = faces[f * 3 + 1];
            int i2 = faces[f * 3 + 2];
            CheckDaylightEdge(i0, i1, originalVertices, gradedVertices, processedEdges, daylightPts);
            CheckDaylightEdge(i1, i2, originalVertices, gradedVertices, processedEdges, daylightPts);
            CheckDaylightEdge(i2, i0, originalVertices, gradedVertices, processedEdges, daylightPts);
        }

        return new GradingResult(
            gradedVertices,
            vertexCount,
            (int[])faces.Clone(),
            faceCount,
            cutVol,
            fillVol,
            daylightPts.ToArray(),
            daylightPts.Count / 3);
    }

    private static void CheckDaylightEdge(
        int a,
        int b,
        double[] originalVertices,
        double[] gradedVertices,
        HashSet<long> processed,
        List<double> pts)
    {
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        if (!processed.Add(key))
            return;

        double dzA = gradedVertices[a * 3 + 2] - originalVertices[a * 3 + 2];
        double dzB = gradedVertices[b * 3 + 2] - originalVertices[b * 3 + 2];
        const double threshold = 0.001;

        if ((dzA > threshold && dzB < -threshold) || (dzA < -threshold && dzB > threshold))
        {
            double t = dzA / (dzA - dzB);
            pts.Add(gradedVertices[a * 3] + t * (gradedVertices[b * 3] - gradedVertices[a * 3]));
            pts.Add(gradedVertices[a * 3 + 1] + t * (gradedVertices[b * 3 + 1] - gradedVertices[a * 3 + 1]));
            pts.Add(gradedVertices[a * 3 + 2] + t * (gradedVertices[b * 3 + 2] - gradedVertices[a * 3 + 2]));
        }
        else if (Math.Abs(dzA) <= threshold && Math.Abs(dzB) > threshold)
        {
            pts.Add(gradedVertices[a * 3]);
            pts.Add(gradedVertices[a * 3 + 1]);
            pts.Add(gradedVertices[a * 3 + 2]);
        }
        else if (Math.Abs(dzB) <= threshold && Math.Abs(dzA) > threshold)
        {
            pts.Add(gradedVertices[b * 3]);
            pts.Add(gradedVertices[b * 3 + 1]);
            pts.Add(gradedVertices[b * 3 + 2]);
        }
    }

    // Spatial data structures.

    internal sealed class SpatialHash
    {
        private readonly double _cellSize;
        private readonly double _invCell;
        private readonly Dictionary<long, List<int>> _grid = new();

        public SpatialHash(double tolerance)
        {
            _cellSize = Math.Max(tolerance * 2, 1e-10);
            _invCell = 1.0 / _cellSize;
        }

        public void Insert(int index, double x, double y)
        {
            long key = CellKey(x, y);
            if (!_grid.TryGetValue(key, out var list))
            {
                list = new List<int>();
                _grid[key] = list;
            }

            list.Add(index);
        }

        public int FindNearest(List<double> xyList, double px, double py, double tolerance)
        {
            double tolSq = tolerance * tolerance;
            long cx = (long)Math.Floor(px * _invCell);
            long cy = (long)Math.Floor(py * _invCell);

            for (long dx = -1; dx <= 1; dx++)
            {
                for (long dy = -1; dy <= 1; dy++)
                {
                    long key = PackKey(cx + dx, cy + dy);
                    if (!_grid.TryGetValue(key, out var indices))
                        continue;

                    foreach (int idx in indices)
                    {
                        double ex = xyList[idx * 2];
                        double ey = xyList[idx * 2 + 1];
                        double d2 = (px - ex) * (px - ex) + (py - ey) * (py - ey);
                        if (d2 < tolSq)
                            return idx;
                    }
                }
            }

            return -1;
        }

        private long CellKey(double x, double y) =>
            PackKey((long)Math.Floor(x * _invCell), (long)Math.Floor(y * _invCell));

        private static long PackKey(long cx, long cy) =>
            (cx * 0x100000001L) ^ (cy * 0x27d4eb2dL);
    }

    internal sealed class FaceGrid
    {
        private readonly double[] _verts;
        private readonly int[] _faces;
        private readonly Dictionary<long, List<int>> _grid;
        private readonly double _invCell;

        public FaceGrid(double[] vertices, int vertexCount, int[] faces, int faceCount)
        {
            _verts = vertices;
            _faces = faces;

            double minX = double.MaxValue;
            double maxX = double.MinValue;
            double minY = double.MaxValue;
            double maxY = double.MinValue;
            for (int i = 0; i < vertexCount; i++)
            {
                double x = vertices[i * 3];
                double y = vertices[i * 3 + 1];
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }

            double span = Math.Max(maxX - minX, maxY - minY);
            int gridRes = Math.Max(1, (int)Math.Sqrt(faceCount / 4.0));
            double cellSize = Math.Max(span / gridRes, 1e-6);
            _invCell = 1.0 / cellSize;

            _grid = new Dictionary<long, List<int>>(faceCount);
            for (int f = 0; f < faceCount; f++)
            {
                int i0 = faces[f * 3];
                int i1 = faces[f * 3 + 1];
                int i2 = faces[f * 3 + 2];
                double x0 = vertices[i0 * 3];
                double y0 = vertices[i0 * 3 + 1];
                double x1 = vertices[i1 * 3];
                double y1 = vertices[i1 * 3 + 1];
                double x2 = vertices[i2 * 3];
                double y2 = vertices[i2 * 3 + 1];

                long cMinX = (long)Math.Floor(Math.Min(x0, Math.Min(x1, x2)) * _invCell);
                long cMaxX = (long)Math.Floor(Math.Max(x0, Math.Max(x1, x2)) * _invCell);
                long cMinY = (long)Math.Floor(Math.Min(y0, Math.Min(y1, y2)) * _invCell);
                long cMaxY = (long)Math.Floor(Math.Max(y0, Math.Max(y1, y2)) * _invCell);

                for (long cy = cMinY; cy <= cMaxY; cy++)
                {
                    for (long cx = cMinX; cx <= cMaxX; cx++)
                    {
                        long key = (cx * 0x100000001L) ^ (cy * 0x27d4eb2dL);
                        if (!_grid.TryGetValue(key, out var list))
                        {
                            list = new List<int>();
                            _grid[key] = list;
                        }

                        list.Add(f);
                    }
                }
            }
        }

        public double InterpolateZ(double px, double py)
        {
            const double tol = 1e-4;
            long cx = (long)Math.Floor(px * _invCell);
            long cy = (long)Math.Floor(py * _invCell);

            for (long dx = -1; dx <= 1; dx++)
            {
                for (long dy = -1; dy <= 1; dy++)
                {
                    long key = ((cx + dx) * 0x100000001L) ^ ((cy + dy) * 0x27d4eb2dL);
                    if (!_grid.TryGetValue(key, out var faceIndices))
                        continue;

                    foreach (int f in faceIndices)
                    {
                        int i0 = _faces[f * 3];
                        int i1 = _faces[f * 3 + 1];
                        int i2 = _faces[f * 3 + 2];
                        double x0 = _verts[i0 * 3];
                        double y0 = _verts[i0 * 3 + 1];
                        double z0 = _verts[i0 * 3 + 2];
                        double x1 = _verts[i1 * 3];
                        double y1 = _verts[i1 * 3 + 1];
                        double z1 = _verts[i1 * 3 + 2];
                        double x2 = _verts[i2 * 3];
                        double y2 = _verts[i2 * 3 + 1];
                        double z2 = _verts[i2 * 3 + 2];

                        double denom = (y1 - y2) * (x0 - x2) + (x2 - x1) * (y0 - y2);
                        if (Math.Abs(denom) < 1e-12)
                            continue;

                        double w0 = ((y1 - y2) * (px - x2) + (x2 - x1) * (py - y2)) / denom;
                        double w1 = ((y2 - y0) * (px - x2) + (x0 - x2) * (py - y2)) / denom;
                        double w2 = 1.0 - w0 - w1;

                        if (w0 >= -tol && w1 >= -tol && w2 >= -tol)
                            return w0 * z0 + w1 * z1 + w2 * z2;
                    }
                }
            }

            return NearestVertexZ(px, py);
        }

        private double NearestVertexZ(double px, double py)
        {
            double nearestZ = 0;
            double nearestDistSq = double.MaxValue;
            int vCount = _verts.Length / 3;
            for (int i = 0; i < vCount; i++)
            {
                double dx = _verts[i * 3] - px;
                double dy = _verts[i * 3 + 1] - py;
                double distSq = dx * dx + dy * dy;
                if (distSq < nearestDistSq)
                {
                    nearestDistSq = distSq;
                    nearestZ = _verts[i * 3 + 2];
                }
            }

            return nearestZ;
        }
    }

    // Geometry helpers (public for cross-assembly use).

    public static bool PointInPolygon(double px, double py, double[] polyXy, int polyVertCount)
    {
        bool inside = false;
        for (int i = 0, j = polyVertCount - 1; i < polyVertCount; j = i++)
        {
            double xi = polyXy[i * 2];
            double yi = polyXy[i * 2 + 1];
            double xj = polyXy[j * 2];
            double yj = polyXy[j * 2 + 1];

            if (((yi > py) != (yj > py)) &&
                (px < (xj - xi) * (py - yi) / (yj - yi) + xi))
            {
                inside = !inside;
            }
        }

        return inside;
    }

    public static double DistToPolygon(double px, double py, double[] polyXy, int polyVertCount)
    {
        double minDist = double.MaxValue;
        for (int i = 0, j = polyVertCount - 1; i < polyVertCount; j = i++)
        {
            double dist = DistToSegment(
                px,
                py,
                polyXy[j * 2],
                polyXy[j * 2 + 1],
                polyXy[i * 2],
                polyXy[i * 2 + 1]);
            if (dist < minDist)
                minDist = dist;
        }

        return minDist;
    }

    private static double DistToSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        double dx = bx - ax;
        double dy = by - ay;
        double lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-20)
            return Math.Sqrt((px - ax) * (px - ax) + (py - ay) * (py - ay));

        double t = Math.Max(0, Math.Min(1, ((px - ax) * dx + (py - ay) * dy) / lenSq));
        double cx = ax + t * dx;
        double cy = ay + t * dy;
        return Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
    }

    public static int FindNearVertex(List<double> xyList, double px, double py, double tolerance)
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

    private static void IncrEdge(Dictionary<long, int> dict, int a, int b)
    {
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        dict[key] = dict.GetValueOrDefault(key, 0) + 1;
    }

    public static double InterpolateZ(double[] vertices, int[] faces, int faceCount, double px, double py)
    {
        const double tol = 1e-4;
        for (int f = 0; f < faceCount; f++)
        {
            int i0 = faces[f * 3];
            int i1 = faces[f * 3 + 1];
            int i2 = faces[f * 3 + 2];
            double x0 = vertices[i0 * 3];
            double y0 = vertices[i0 * 3 + 1];
            double z0 = vertices[i0 * 3 + 2];
            double x1 = vertices[i1 * 3];
            double y1 = vertices[i1 * 3 + 1];
            double z1 = vertices[i1 * 3 + 2];
            double x2 = vertices[i2 * 3];
            double y2 = vertices[i2 * 3 + 1];
            double z2 = vertices[i2 * 3 + 2];

            double denom = (y1 - y2) * (x0 - x2) + (x2 - x1) * (y0 - y2);
            if (Math.Abs(denom) < 1e-12)
                continue;

            double w0 = ((y1 - y2) * (px - x2) + (x2 - x1) * (py - y2)) / denom;
            double w1 = ((y2 - y0) * (px - x2) + (x0 - x2) * (py - y2)) / denom;
            double w2 = 1.0 - w0 - w1;

            if (w0 >= -tol && w1 >= -tol && w2 >= -tol)
                return w0 * z0 + w1 * z1 + w2 * z2;
        }

        double nearestZ = 0;
        double nearestDistSq = double.MaxValue;
        int vCount = vertices.Length / 3;
        for (int i = 0; i < vCount; i++)
        {
            double dx = vertices[i * 3] - px;
            double dy = vertices[i * 3 + 1] - py;
            double distSq = dx * dx + dy * dy;
            if (distSq < nearestDistSq)
            {
                nearestDistSq = distSq;
                nearestZ = vertices[i * 3 + 2];
            }
        }

        return nearestZ;
    }
}
