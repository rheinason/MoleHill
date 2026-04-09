using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

public static class SurfaceStripGrader
{
    public sealed class SurfaceDefinition
    {
        public double[] FootprintXy { get; }

        public int FootprintVertexCount { get; }

        public double[] BoundaryVertices { get; }

        public int BoundaryVertexCount { get; }

        public double PlaneXCoeff { get; }

        public double PlaneYCoeff { get; }

        public double PlaneConstant { get; }

        public double SlopeAngleDeg { get; }

        public double MaxDistance { get; }

        public SurfaceDefinition(
            double[] footprintXy,
            int footprintVertexCount,
            double[] boundaryVertices,
            int boundaryVertexCount,
            double planeXCoeff,
            double planeYCoeff,
            double planeConstant,
            double slopeAngleDeg = 33.0,
            double maxDistance = 0.0)
        {
            FootprintXy = footprintXy;
            FootprintVertexCount = footprintVertexCount;
            BoundaryVertices = boundaryVertices;
            BoundaryVertexCount = boundaryVertexCount;
            PlaneXCoeff = planeXCoeff;
            PlaneYCoeff = planeYCoeff;
            PlaneConstant = planeConstant;
            SlopeAngleDeg = Math.Max(0.1, Math.Min(89.9, slopeAngleDeg));
            MaxDistance = maxDistance;
        }

        public double EvaluateZ(double x, double y) => PlaneXCoeff * x + PlaneYCoeff * y + PlaneConstant;
    }

    public static GradingResult? Grade(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        SurfaceDefinition surface,
        out string? errorMessage)
    {
        return Grade(
            vertices,
            vertexCount,
            faces,
            faceCount,
            surface,
            Array.Empty<SurfaceRemesher.ConstraintPolyline>(),
            out errorMessage);
    }

    public static GradingResult? Grade(
        double[] vertices,
        int vertexCount,
        int[] faces,
        int faceCount,
        SurfaceDefinition surface,
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> barrierConstraints,
        out string? errorMessage)
    {
        errorMessage = null;
        const double dedupTol = 1e-3;

        if (surface.FootprintVertexCount < 3)
        {
            errorMessage = "The graded surface footprint must contain at least 3 vertices.";
            return null;
        }

        if (surface.BoundaryVertexCount < 2)
        {
            errorMessage = "The graded surface boundary must contain at least 2 vertices.";
            return null;
        }

        var xyList = new List<double>(vertexCount * 2);
        var zList = new List<double>(vertexCount);
        var segList = new List<(int a, int b)>();
        var vertHash = new PadGrader.SpatialHash(dedupTol);

        for (int i = 0; i < vertexCount; i++)
        {
            double x = vertices[i * 3];
            double y = vertices[i * 3 + 1];
            xyList.Add(x);
            xyList.Add(y);
            zList.Add(vertices[i * 3 + 2]);
            vertHash.Insert(i, x, y);
        }

        var faceGrid = new PadGrader.FaceGrid(vertices, vertexCount, faces, faceCount);
        PreparedBarriers preparedBarriers = GradingBarriers.Build(barrierConstraints);
        bool hasBoundaryLoop = PadGrader.TryBuildBoundaryLoop(vertices, faces, faceCount, out var boundaryLoop, out int boundaryVertexCount);
        if (hasBoundaryLoop &&
            !BoundaryClipper.IsPolylineInsideBoundary(
                surface.FootprintXy,
                surface.FootprintVertexCount,
                isClosed: true,
                hasBoundaryLoop,
                boundaryLoop,
                boundaryVertexCount,
                dedupTol))
        {
            errorMessage = "The graded surface footprint must lie within the terrain boundary.";
            return null;
        }

        AddBoundarySegments(faces, faceCount, segList);
        AddBarrierConstraints(barrierConstraints, xyList, zList, vertHash, segList, dedupTol);
        AddPolygonConstraint(surface.FootprintXy, surface.FootprintVertexCount, xyList, zList, vertHash, faceGrid, segList, dedupTol);

        int totalVerts = zList.Count;
        if (totalVerts < 3)
        {
            errorMessage = "Too few vertices for triangulation.";
            return null;
        }

        TriangulationOutcome triangulation = TriangulationHelper.Triangulate(
            xyList,
            totalVerts,
            segList,
            0,
            0,
            convex: false);

        if (triangulation.Mesh == null)
        {
            errorMessage = triangulation.WarningMessage ?? "Triangulation failed.";
            return null;
        }

        if (!string.IsNullOrWhiteSpace(triangulation.WarningMessage))
            errorMessage = triangulation.WarningMessage;

        var extracted = TriangleNetExtractor.Extract(triangulation.Mesh);
        int outVertCount = extracted.VertexCount;
        int outFaceCount = extracted.FaceCount;

        var outXy = new double[outVertCount * 2];
        var origZ = new double[outVertCount];
        var newZ = new double[outVertCount];

        for (int i = 0; i < outVertCount; i++)
        {
            double x = extracted.Xy[i * 2];
            double y = extracted.Xy[i * 2 + 1];
            int sourceId = extracted.SourceIds[i];
            outXy[i * 2] = x;
            outXy[i * 2 + 1] = y;

            if (sourceId >= 0 && sourceId < totalVerts)
            {
                origZ[i] = zList[sourceId];
                newZ[i] = zList[sourceId];
            }
            else
            {
                double iz = faceGrid.InterpolateZ(x, y);
                origZ[i] = iz;
                newZ[i] = iz;
            }
        }

        ApplySurfaceHeights(surface, outXy, origZ, newZ, outVertCount, preparedBarriers);

        var finalVerts = new double[outVertCount * 3];
        for (int i = 0; i < outVertCount; i++)
        {
            finalVerts[i * 3] = outXy[i * 2];
            finalVerts[i * 3 + 1] = outXy[i * 2 + 1];
            finalVerts[i * 3 + 2] = newZ[i];
        }

        var finalFaces = extracted.Faces;
        var cullResult = TriangleBoundaryCuller.Cull(
            finalVerts,
            outVertCount,
            finalFaces,
            outFaceCount,
            xyList.ToArray(),
            IndexedMeshTools.FlattenSegments(segList),
            0);

        if (cullResult.Changed)
        {
            outXy = IndexedMeshTools.CompactDoubleData(outXy, 2, cullResult.NewToOld, cullResult.VertexCount);
            origZ = IndexedMeshTools.CompactDoubleData(origZ, 1, cullResult.NewToOld, cullResult.VertexCount);
            newZ = IndexedMeshTools.CompactDoubleData(newZ, 1, cullResult.NewToOld, cullResult.VertexCount);
            finalVerts = IndexedMeshTools.CompactDoubleData(finalVerts, 3, cullResult.NewToOld, cullResult.VertexCount);
            finalFaces = cullResult.Faces;
            outVertCount = cullResult.VertexCount;
            outFaceCount = cullResult.FaceCount;
        }

        double cutVol = 0;
        double fillVol = 0;
        for (int f = 0; f < outFaceCount; f++)
        {
            int i0 = finalFaces[f * 3];
            int i1 = finalFaces[f * 3 + 1];
            int i2 = finalFaces[f * 3 + 2];

            double area2d = Math.Abs(
                (outXy[i1 * 2] - outXy[i0 * 2]) * (outXy[i2 * 2 + 1] - outXy[i0 * 2 + 1]) -
                (outXy[i2 * 2] - outXy[i0 * 2]) * (outXy[i1 * 2 + 1] - outXy[i0 * 2 + 1])) * 0.5;

            double dz0 = newZ[i0] - origZ[i0];
            double dz1 = newZ[i1] - origZ[i1];
            double dz2 = newZ[i2] - origZ[i2];
            double avgDz = (dz0 + dz1 + dz2) / 3.0;

            double vol = area2d * avgDz;
            if (vol > 0)
                fillVol += vol;
            else
                cutVol += -vol;
        }

        var daylightPts = new List<double>();
        var processedEdges = new HashSet<long>();
        for (int f = 0; f < outFaceCount; f++)
        {
            int i0 = finalFaces[f * 3];
            int i1 = finalFaces[f * 3 + 1];
            int i2 = finalFaces[f * 3 + 2];
            CheckDaylightEdge(i0, i1, outXy, newZ, origZ, processedEdges, daylightPts);
            CheckDaylightEdge(i1, i2, outXy, newZ, origZ, processedEdges, daylightPts);
            CheckDaylightEdge(i2, i0, outXy, newZ, origZ, processedEdges, daylightPts);
        }

        return new GradingResult(
            finalVerts,
            outVertCount,
            finalFaces,
            outFaceCount,
            cutVol,
            fillVol,
            daylightPts.ToArray(),
            daylightPts.Count / 3);
    }

    private static void ApplySurfaceHeights(
        SurfaceDefinition surface,
        double[] outXy,
        double[] origZ,
        double[] newZ,
        int outVertCount,
        PreparedBarriers barriers)
    {
        double slopeRatio = Math.Tan(surface.SlopeAngleDeg * Math.PI / 180.0);
        var barrierScratch = barriers.Segments.Length > 0 ? new SpatialHashGrid2D.QueryScratch(Math.Max(barriers.Segments.Length, 1)) : null;
        var barrierCandidates = barriers.Segments.Length > 0 ? new List<int>(8) : null;

        for (int i = 0; i < outVertCount; i++)
        {
            double px = outXy[i * 2];
            double py = outXy[i * 2 + 1];

            if (PadGrader.PointInPolygon(px, py, surface.FootprintXy, surface.FootprintVertexCount))
            {
                newZ[i] = surface.EvaluateZ(px, py);
                continue;
            }

            if (!TryFindNearestVisibleBoundaryLocation(
                    px,
                    py,
                    surface.BoundaryVertices,
                    surface.BoundaryVertexCount,
                    barriers,
                    barrierScratch,
                    barrierCandidates,
                    out double boundaryDistance,
                    out double boundaryZ))
            {
                continue;
            }

            double dz = origZ[i] - boundaryZ;
            double absDz = Math.Abs(dz);
            double neededDist = slopeRatio > 1e-12 ? absDz / slopeRatio : double.MaxValue;
            if (surface.MaxDistance > 0)
                neededDist = Math.Min(neededDist, surface.MaxDistance);

            if (boundaryDistance < neededDist)
            {
                double rise = boundaryDistance * slopeRatio;
                if (rise < absDz)
                    newZ[i] = boundaryZ + Math.Sign(dz) * rise;
            }
        }
    }

    private static bool TryFindNearestVisibleBoundaryLocation(
        double px,
        double py,
        double[] boundaryVertices,
        int boundaryVertexCount,
        PreparedBarriers barriers,
        SpatialHashGrid2D.QueryScratch? barrierScratch,
        List<int>? barrierCandidates,
        out double boundaryDistance,
        out double boundaryZ)
    {
        boundaryDistance = double.MaxValue;
        boundaryZ = 0.0;

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
            double t = 0.0;
            double cx = ax;
            double cy = ay;
            if (lenSq > 1e-20)
            {
                t = Math.Clamp(((px - ax) * dx + (py - ay) * dy) / lenSq, 0.0, 1.0);
                cx = ax + t * dx;
                cy = ay + t * dy;
            }

            if (barriers.Segments.Length > 0 &&
                barrierScratch != null &&
                barrierCandidates != null &&
                GradingBarriers.IsCrossedByBarrier(
                    barriers,
                    px,
                    py,
                    cx,
                    cy,
                    barrierScratch,
                    barrierCandidates))
            {
                continue;
            }

            double dist = Math.Sqrt(((px - cx) * (px - cx)) + ((py - cy) * (py - cy)));
            if (dist >= boundaryDistance)
                continue;

            boundaryDistance = dist;
            boundaryZ = az + ((bz - az) * t);
        }

        return boundaryDistance < double.MaxValue;
    }

    private static void AddBoundarySegments(int[] faces, int faceCount, List<(int a, int b)> segList)
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

        foreach (var pair in edgeFaceCount)
        {
            if (pair.Value != 1)
                continue;

            int a = (int)(pair.Key >> 32);
            int b = (int)(pair.Key & 0xFFFFFFFFL);
            segList.Add((a, b));
        }
    }

    private static void AddPolygonConstraint(
        double[] polygonXy,
        int vertexCount,
        List<double> xyList,
        List<double> zList,
        PadGrader.SpatialHash vertHash,
        PadGrader.FaceGrid faceGrid,
        List<(int a, int b)> segList,
        double dedupTol)
    {
        var polygonIndices = new int[vertexCount];
        for (int i = 0; i < vertexCount; i++)
        {
            double px = polygonXy[i * 2];
            double py = polygonXy[i * 2 + 1];
            int near = vertHash.FindNearest(xyList, px, py, dedupTol);
            if (near >= 0)
            {
                polygonIndices[i] = near;
            }
            else
            {
                polygonIndices[i] = zList.Count;
                xyList.Add(px);
                xyList.Add(py);
                zList.Add(faceGrid.InterpolateZ(px, py));
                vertHash.Insert(polygonIndices[i], px, py);
            }
        }

        for (int i = 0; i < vertexCount; i++)
        {
            int a = polygonIndices[i];
            int b = polygonIndices[(i + 1) % vertexCount];
            if (a != b)
                segList.Add((a, b));
        }
    }

    private static void AddBarrierConstraints(
        IReadOnlyList<SurfaceRemesher.ConstraintPolyline> barrierConstraints,
        List<double> xyList,
        List<double> zList,
        PadGrader.SpatialHash vertHash,
        List<(int a, int b)> segList,
        double dedupTol)
    {
        foreach (var constraint in barrierConstraints)
        {
            if (!constraint.PreserveInputElevation)
                continue;

            int pointCount = NormalizeConstraintPointCount(constraint, dedupTol);
            if (pointCount < 2)
                continue;

            var pointIndices = new int[pointCount];
            for (int i = 0; i < pointCount; i++)
            {
                double px = constraint.Points[i * 3];
                double py = constraint.Points[i * 3 + 1];
                double pz = constraint.Points[i * 3 + 2];
                int near = vertHash.FindNearest(xyList, px, py, dedupTol);
                if (near >= 0)
                {
                    pointIndices[i] = near;
                }
                else
                {
                    pointIndices[i] = zList.Count;
                    xyList.Add(px);
                    xyList.Add(py);
                    zList.Add(pz);
                    vertHash.Insert(pointIndices[i], px, py);
                }
            }

            for (int i = 0; i < pointCount - 1; i++)
            {
                int a = pointIndices[i];
                int b = pointIndices[i + 1];
                if (a != b)
                    segList.Add((a, b));
            }

            if (!constraint.IsClosed)
                continue;

            int last = pointIndices[pointCount - 1];
            int first = pointIndices[0];
            if (last != first)
                segList.Add((last, first));
        }
    }

    private static int NormalizeConstraintPointCount(SurfaceRemesher.ConstraintPolyline constraint, double tolerance)
    {
        int count = constraint.PointCount;
        if (!constraint.IsClosed || count < 2)
            return count;

        double lastX = constraint.Points[(count - 1) * 3];
        double lastY = constraint.Points[(count - 1) * 3 + 1];
        double firstX = constraint.Points[0];
        double firstY = constraint.Points[1];
        double dx = lastX - firstX;
        double dy = lastY - firstY;
        if (((dx * dx) + (dy * dy)) <= tolerance * tolerance)
            return count - 1;

        return count;
    }

    private static void CheckDaylightEdge(
        int a,
        int b,
        double[] xy,
        double[] newZ,
        double[] origZ,
        HashSet<long> processed,
        List<double> pts)
    {
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        if (!processed.Add(key))
            return;

        double dzA = newZ[a] - origZ[a];
        double dzB = newZ[b] - origZ[b];
        const double threshold = 0.001;

        if ((dzA > threshold && dzB < -threshold) || (dzA < -threshold && dzB > threshold))
        {
            double t = dzA / (dzA - dzB);
            pts.Add(xy[a * 2] + t * (xy[b * 2] - xy[a * 2]));
            pts.Add(xy[a * 2 + 1] + t * (xy[b * 2 + 1] - xy[a * 2 + 1]));
            pts.Add(newZ[a] + t * (newZ[b] - newZ[a]));
        }
        else if (Math.Abs(dzA) <= threshold && Math.Abs(dzB) > threshold)
        {
            pts.Add(xy[a * 2]);
            pts.Add(xy[a * 2 + 1]);
            pts.Add(newZ[a]);
        }
        else if (Math.Abs(dzB) <= threshold && Math.Abs(dzA) > threshold)
        {
            pts.Add(xy[b * 2]);
            pts.Add(xy[b * 2 + 1]);
            pts.Add(newZ[b]);
        }
    }

    private static void IncrEdge(Dictionary<long, int> dict, int a, int b)
    {
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        dict[key] = dict.GetValueOrDefault(key, 0) + 1;
    }
}
