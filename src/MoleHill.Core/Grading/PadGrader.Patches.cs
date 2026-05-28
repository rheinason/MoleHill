using TriangleNet;
using TriangleNet.Geometry;
using TriangleNet.Meshing;
using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

public static partial class PadGrader
{
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
        int maxIntermediateRows = vertexCount > 128 ? 2 : 4;
        int intermediateRows = Math.Clamp((int)Math.Ceiling(maxReach / targetSpacing) - 1, 0, maxIntermediateRows);
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
        LoopDeviationMetrics left = SeamValidator.ComputeLoopDeviation(leftLoopXy, rightLoopXy, tolerance);
        LoopDeviationMetrics right = SeamValidator.ComputeLoopDeviation(rightLoopXy, leftLoopXy, tolerance);
        return left.MissCount == 0 &&
            right.MissCount == 0 &&
            left.MaxDistance <= tolerance &&
            right.MaxDistance <= tolerance;
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

        LoopDeviationMetrics seamToPatch = SeamValidator.ComputeLoopDeviation(seamLoopXy, patchLoopXy, tolerance * 2.0);
        LoopDeviationMetrics patchToSeam = SeamValidator.ComputeLoopDeviation(patchLoopXy, seamLoopXy, tolerance * 2.0);
        diagnostics.Add(
            $"Grade Pad[{padIndex}] seam deviation: split->patch max={seamToPatch.MaxDistance:F6} ({seamToPatch.MissCount} misses), patch->split max={patchToSeam.MaxDistance:F6} ({patchToSeam.MissCount} misses).");

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
}
