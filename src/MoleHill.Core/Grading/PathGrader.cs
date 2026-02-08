using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

/// <summary>
/// Grades a terrain mesh along path curves (roads, sidewalks, etc.).
/// Adds road edges (path offset by half-width) as constrained edges,
/// re-triangulates, then grades Z: inside road = path Z, outside = slope transition.
/// Falls back to Z-only modification if triangulation fails.
/// </summary>
public static class PathGrader
{
    public sealed class PathDefinition
    {
        public double[] XyVertices { get; }
        public double[] ZValues { get; }
        public int VertexCount { get; }
        public double Width { get; }
        public double SlopeAngleDeg { get; }
        public double MaxDistance { get; }

        public PathDefinition(double[] xyVertices, double[] zValues, int vertexCount,
                              double width, double slopeAngleDeg = 33.0, double maxDistance = 0.0)
        {
            XyVertices = xyVertices;
            ZValues = zValues;
            VertexCount = vertexCount;
            Width = width;
            SlopeAngleDeg = Math.Max(0.1, Math.Min(89.9, slopeAngleDeg));
            MaxDistance = maxDistance;
        }
    }

    /// <summary>
    /// Apply path grading to a terrain mesh.
    /// Later paths in the array override earlier ones in overlapping zones.
    /// </summary>
    public static GradingResult? Grade(
        double[] vertices, int vertexCount,
        int[] faces, int faceCount,
        PathDefinition[] paths,
        out string? errorMessage)
    {
        errorMessage = null;

        if (paths.Length == 0)
        {
            errorMessage = "No path definitions provided.";
            return null;
        }

        foreach (var path in paths)
        {
            if (path.VertexCount < 2)
            {
                errorMessage = "Each path must have at least 2 vertices.";
                return null;
            }
            if (path.Width <= 0)
            {
                errorMessage = "Path width must be positive.";
                return null;
            }
        }

        // Try re-triangulation with road edge constraints
        var result = GradeWithEdges(vertices, vertexCount, faces, faceCount, paths, out errorMessage);
        if (result != null) return result;

        // Fallback: just modify Z of existing mesh
        errorMessage = null;
        return GradeZOnly(vertices, vertexCount, faces, faceCount, paths, out errorMessage);
    }

    /// <summary>
    /// Re-triangulate with road edges as constrained segments, then grade Z.
    /// </summary>
    private static GradingResult? GradeWithEdges(
        double[] vertices, int vertexCount,
        int[] faces, int faceCount,
        PathDefinition[] paths,
        out string? errorMessage)
    {
        errorMessage = null;
        const double dedupTol = 1e-3;

        var vertHash = new PadGrader.SpatialHash(dedupTol);
        var xyList = new List<double>(vertexCount * 2);
        var zList = new List<double>(vertexCount);
        var segList = new List<(int a, int b)>();

        // Add terrain vertices
        for (int i = 0; i < vertexCount; i++)
        {
            double x = vertices[i * 3], y = vertices[i * 3 + 1];
            xyList.Add(x);
            xyList.Add(y);
            zList.Add(vertices[i * 3 + 2]);
            vertHash.Insert(i, x, y);
        }

        var faceGrid = new PadGrader.FaceGrid(vertices, vertexCount, faces, faceCount);

        // Add mesh boundary edges as constraints (keeps triangulation within original mesh)
        var edgeFaceCount = new Dictionary<long, int>();
        for (int f = 0; f < faceCount; f++)
        {
            int a = faces[f * 3], b = faces[f * 3 + 1], c = faces[f * 3 + 2];
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

        int AddVertex(double x, double y)
        {
            int near = vertHash.FindNearest(xyList, x, y, dedupTol);
            if (near >= 0) return near;

            int idx = zList.Count;
            xyList.Add(x);
            xyList.Add(y);
            zList.Add(faceGrid.InterpolateZ(x, y)); // always terrain Z
            vertHash.Insert(idx, x, y);
            return idx;
        }

        // For each path, compute road edges and add as open constrained polylines
        foreach (var path in paths)
        {
            double halfWidth = path.Width * 0.5;
            int n = path.VertexCount;

            var centerIdx = new int[n];
            var leftIdx = new int[n];
            var rightIdx = new int[n];

            for (int i = 0; i < n; i++)
            {
                double cx = path.XyVertices[i * 2], cy = path.XyVertices[i * 2 + 1];

                // Average direction at vertex
                double dx, dy;
                if (i == 0)
                {
                    dx = path.XyVertices[2] - path.XyVertices[0];
                    dy = path.XyVertices[3] - path.XyVertices[1];
                }
                else if (i == n - 1)
                {
                    dx = path.XyVertices[i * 2] - path.XyVertices[(i - 1) * 2];
                    dy = path.XyVertices[i * 2 + 1] - path.XyVertices[(i - 1) * 2 + 1];
                }
                else
                {
                    dx = path.XyVertices[(i + 1) * 2] - path.XyVertices[(i - 1) * 2];
                    dy = path.XyVertices[(i + 1) * 2 + 1] - path.XyVertices[(i - 1) * 2 + 1];
                }

                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1e-12) len = 1;
                dx /= len; dy /= len;

                // Perpendicular offset
                double px = -dy * halfWidth, py = dx * halfWidth;

                centerIdx[i] = AddVertex(cx, cy);
                leftIdx[i] = AddVertex(cx + px, cy + py);
                rightIdx[i] = AddVertex(cx - px, cy - py);
            }

            // Add constrained segments (open polylines, NOT closed)
            for (int i = 0; i < n - 1; i++)
            {
                if (centerIdx[i] != centerIdx[i + 1])
                    segList.Add((centerIdx[i], centerIdx[i + 1]));
                if (leftIdx[i] != leftIdx[i + 1])
                    segList.Add((leftIdx[i], leftIdx[i + 1]));
                if (rightIdx[i] != rightIdx[i + 1])
                    segList.Add((rightIdx[i], rightIdx[i + 1]));
            }
        }

        // Triangulate (NO quality constraints)
        int totalVerts = zList.Count;
        if (totalVerts < 3)
        {
            errorMessage = "Too few vertices for triangulation.";
            return null;
        }

        var triMesh = TriangulationHelper.Triangulate(
            xyList, totalVerts, segList,
            0, 0, // no quality refinement
            out string? triWarning,
            convex: false);

        if (triMesh == null)
        {
            errorMessage = triWarning ?? "Triangulation failed.";
            return null;
        }

        if (triWarning != null)
            errorMessage = triWarning;

        // Map output vertices
        var outVerts = triMesh.Vertices.ToList();
        var outTris = triMesh.Triangles.ToList();
        int outVertCount = outVerts.Count;
        int outFaceCount = outTris.Count;

        var outXy = new double[outVertCount * 2];
        var origZ = new double[outVertCount];
        var newZ = new double[outVertCount];
        var idToIdx = new Dictionary<int, int>(outVertCount);

        for (int i = 0; i < outVertCount; i++)
        {
            var mv = outVerts[i];
            idToIdx[mv.ID] = i;
            outXy[i * 2] = mv.X;
            outXy[i * 2 + 1] = mv.Y;

            if (mv.ID >= 0 && mv.ID < totalVerts)
            {
                origZ[i] = zList[mv.ID];
                newZ[i] = zList[mv.ID];
            }
            else
            {
                double iz = faceGrid.InterpolateZ(mv.X, mv.Y);
                origZ[i] = iz;
                newZ[i] = iz;
            }
        }

        // Grade Z
        ApplyPathGrading(paths, outXy, origZ, newZ, outVertCount);

        // Build output
        var finalVerts = new double[outVertCount * 3];
        for (int i = 0; i < outVertCount; i++)
        {
            finalVerts[i * 3] = outXy[i * 2];
            finalVerts[i * 3 + 1] = outXy[i * 2 + 1];
            finalVerts[i * 3 + 2] = newZ[i];
        }

        var finalFaces = new int[outFaceCount * 3];
        for (int i = 0; i < outFaceCount; i++)
        {
            var tri = outTris[i];
            finalFaces[i * 3] = idToIdx.GetValueOrDefault(tri.GetVertex(0).ID, 0);
            finalFaces[i * 3 + 1] = idToIdx.GetValueOrDefault(tri.GetVertex(1).ID, 0);
            finalFaces[i * 3 + 2] = idToIdx.GetValueOrDefault(tri.GetVertex(2).ID, 0);
        }

        return BuildResult(outXy, origZ, newZ, finalVerts, outVertCount, finalFaces, outFaceCount);
    }

    /// <summary>
    /// Fallback: modify Z values of existing mesh without re-triangulation.
    /// </summary>
    private static GradingResult? GradeZOnly(
        double[] vertices, int vertexCount,
        int[] faces, int faceCount,
        PathDefinition[] paths,
        out string? errorMessage)
    {
        errorMessage = "Using Z-only grading (road edges may not be sharp).";

        var outXy = new double[vertexCount * 2];
        var origZ = new double[vertexCount];
        var newZ = new double[vertexCount];

        for (int i = 0; i < vertexCount; i++)
        {
            outXy[i * 2] = vertices[i * 3];
            outXy[i * 2 + 1] = vertices[i * 3 + 1];
            origZ[i] = vertices[i * 3 + 2];
            newZ[i] = vertices[i * 3 + 2];
        }

        ApplyPathGrading(paths, outXy, origZ, newZ, vertexCount);

        var finalVerts = new double[vertexCount * 3];
        for (int i = 0; i < vertexCount; i++)
        {
            finalVerts[i * 3] = vertices[i * 3];
            finalVerts[i * 3 + 1] = vertices[i * 3 + 1];
            finalVerts[i * 3 + 2] = newZ[i];
        }

        return BuildResult(outXy, origZ, newZ, finalVerts, vertexCount, (int[])faces.Clone(), faceCount);
    }

    /// <summary>
    /// Shared grading logic: for each vertex, find nearest path and assign Z.
    /// </summary>
    private static void ApplyPathGrading(
        PathDefinition[] paths,
        double[] outXy, double[] origZ, double[] newZ, int vertCount)
    {
        foreach (var path in paths)
        {
            double halfWidth = path.Width * 0.5;
            double slopeRatio = Math.Tan(path.SlopeAngleDeg * Math.PI / 180.0);

            // Compute bbox for fast filtering
            double mnX = double.MaxValue, mxX = double.MinValue;
            double mnY = double.MaxValue, mxY = double.MinValue;
            for (int i = 0; i < path.VertexCount; i++)
            {
                double x = path.XyVertices[i * 2], y = path.XyVertices[i * 2 + 1];
                if (x < mnX) mnX = x; if (x > mxX) mxX = x;
                if (y < mnY) mnY = y; if (y > mxY) mxY = y;
            }

            double maxZDiff = 0;
            for (int i = 0; i < vertCount; i++)
            {
                double vx = outXy[i * 2], vy = outXy[i * 2 + 1];
                if (vx < mnX - 200 || vx > mxX + 200 || vy < mnY - 200 || vy > mxY + 200) continue;
                for (int p = 0; p < path.VertexCount; p++)
                {
                    double dz = Math.Abs(origZ[i] - path.ZValues[p]);
                    if (dz > maxZDiff) maxZDiff = dz;
                }
            }
            double maxInfluence = halfWidth + (slopeRatio > 1e-12 ? maxZDiff / slopeRatio : 100.0);
            if (path.MaxDistance > 0) maxInfluence = halfWidth + path.MaxDistance;

            mnX -= maxInfluence; mxX += maxInfluence;
            mnY -= maxInfluence; mxY += maxInfluence;

            for (int i = 0; i < vertCount; i++)
            {
                double px = outXy[i * 2], py = outXy[i * 2 + 1];

                if (px < mnX || px > mxX || py < mnY || py > mxY) continue;

                // Find closest point on path centerline
                double closestDist = double.MaxValue;
                double closestPathZ = 0;

                for (int s = 0; s < path.VertexCount - 1; s++)
                {
                    double ax = path.XyVertices[s * 2], ay = path.XyVertices[s * 2 + 1];
                    double bx = path.XyVertices[(s + 1) * 2], by = path.XyVertices[(s + 1) * 2 + 1];

                    double sdx = bx - ax, sdy = by - ay;
                    double segLen = sdx * sdx + sdy * sdy;
                    if (segLen < 1e-20) continue;

                    double t = ((px - ax) * sdx + (py - ay) * sdy) / segLen;
                    t = Math.Max(0, Math.Min(1, t));

                    double projX = ax + t * sdx, projY = ay + t * sdy;
                    double dist = Math.Sqrt((px - projX) * (px - projX) + (py - projY) * (py - projY));

                    if (dist < closestDist)
                    {
                        closestDist = dist;
                        closestPathZ = path.ZValues[s] + t * (path.ZValues[s + 1] - path.ZValues[s]);
                    }
                }

                if (closestDist <= halfWidth + 1e-6)
                {
                    // Inside road — use path Z
                    newZ[i] = closestPathZ;
                }
                else
                {
                    // Transition zone (same as pad transition)
                    double distFromEdge = closestDist - halfWidth;
                    double dz = origZ[i] - closestPathZ;
                    double absDz = Math.Abs(dz);
                    double neededDist = slopeRatio > 1e-12 ? absDz / slopeRatio : double.MaxValue;
                    if (path.MaxDistance > 0) neededDist = Math.Min(neededDist, path.MaxDistance);

                    if (distFromEdge < neededDist)
                    {
                        double rise = distFromEdge * slopeRatio;
                        if (rise < absDz)
                            newZ[i] = closestPathZ + Math.Sign(dz) * rise;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Build GradingResult with volumes and daylight line.
    /// </summary>
    private static GradingResult BuildResult(
        double[] outXy, double[] origZ, double[] newZ,
        double[] finalVerts, int vertCount,
        int[] finalFaces, int faceCount)
    {
        double cutVol = 0, fillVol = 0;
        for (int f = 0; f < faceCount; f++)
        {
            int i0 = finalFaces[f * 3], i1 = finalFaces[f * 3 + 1], i2 = finalFaces[f * 3 + 2];

            double area2d = Math.Abs(
                (outXy[i1 * 2] - outXy[i0 * 2]) * (outXy[i2 * 2 + 1] - outXy[i0 * 2 + 1])
              - (outXy[i2 * 2] - outXy[i0 * 2]) * (outXy[i1 * 2 + 1] - outXy[i0 * 2 + 1])
            ) * 0.5;

            double dz0 = newZ[i0] - origZ[i0];
            double dz1 = newZ[i1] - origZ[i1];
            double dz2 = newZ[i2] - origZ[i2];
            double avgDz = (dz0 + dz1 + dz2) / 3.0;

            double vol = area2d * avgDz;
            if (vol > 0) fillVol += vol;
            else cutVol += -vol;
        }

        var daylightPts = new List<double>();
        var processedEdges = new HashSet<long>();

        for (int f = 0; f < faceCount; f++)
        {
            int i0 = finalFaces[f * 3], i1 = finalFaces[f * 3 + 1], i2 = finalFaces[f * 3 + 2];
            CheckDaylightEdge(i0, i1, outXy, newZ, origZ, processedEdges, daylightPts);
            CheckDaylightEdge(i1, i2, outXy, newZ, origZ, processedEdges, daylightPts);
            CheckDaylightEdge(i2, i0, outXy, newZ, origZ, processedEdges, daylightPts);
        }

        return new GradingResult(
            finalVerts, vertCount,
            finalFaces, faceCount,
            cutVol, fillVol,
            daylightPts.ToArray(), daylightPts.Count / 3);
    }

    private static void IncrEdge(Dictionary<long, int> dict, int a, int b)
    {
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        dict[key] = dict.GetValueOrDefault(key, 0) + 1;
    }

    private static void CheckDaylightEdge(int a, int b,
        double[] xy, double[] newZ, double[] origZ,
        HashSet<long> processed, List<double> pts)
    {
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        if (!processed.Add(key)) return;

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
            pts.Add(xy[a * 2]); pts.Add(xy[a * 2 + 1]); pts.Add(newZ[a]);
        }
        else if (Math.Abs(dzB) <= threshold && Math.Abs(dzA) > threshold)
        {
            pts.Add(xy[b * 2]); pts.Add(xy[b * 2 + 1]); pts.Add(newZ[b]);
        }
    }
}
