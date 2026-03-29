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
        AddBoundarySegments(faces, faceCount, segList);
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

        ApplySurfaceHeights(surface, outXy, origZ, newZ, outVertCount);

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
        int outVertCount)
    {
        double slopeRatio = Math.Tan(surface.SlopeAngleDeg * Math.PI / 180.0);

        for (int i = 0; i < outVertCount; i++)
        {
            double px = outXy[i * 2];
            double py = outXy[i * 2 + 1];

            if (PadGrader.PointInPolygon(px, py, surface.FootprintXy, surface.FootprintVertexCount))
            {
                newZ[i] = surface.EvaluateZ(px, py);
                continue;
            }

            double boundaryDistance = PadGrader.DistToBoundaryWithZ(px, py, surface.BoundaryVertices, surface.BoundaryVertexCount, out double boundaryZ);
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

    private static void AddBoundarySegments(int[] faces, int faceCount, List<(int a, int b)> segList)
    {
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
