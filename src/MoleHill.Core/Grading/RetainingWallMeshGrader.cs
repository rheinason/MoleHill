using MoleHill.Core.Engine;

namespace MoleHill.Core.Grading;

/// <summary>
/// Grades a terrain mesh for one retaining-wall strip at a time.
/// Each call is isolated so callers can preserve/restore mesh state per wall on failure.
/// </summary>
public static class RetainingWallMeshGrader
{
    public sealed class WallStripDefinition
    {
        public double[] ToeXy { get; }
        public double[] ToeZ { get; }
        public double[] TopXy { get; }
        public double[] TopZ { get; }
        public int StationCount { get; }

        public WallStripDefinition(double[] toeXy, double[] toeZ, double[] topXy, double[] topZ, int stationCount)
        {
            ToeXy = toeXy;
            ToeZ = toeZ;
            TopXy = topXy;
            TopZ = topZ;
            StationCount = stationCount;
        }
    }

    public sealed class RetainingWallGradeOutcome
    {
        public bool GradeApplied { get; }
        public GradingResult? MeshResult { get; }
        public string? WarningOrError { get; }

        public RetainingWallGradeOutcome(bool gradeApplied, GradingResult? meshResult, string? warningOrError)
        {
            GradeApplied = gradeApplied;
            MeshResult = meshResult;
            WarningOrError = warningOrError;
        }
    }

    private readonly struct Point2
    {
        public readonly double X;
        public readonly double Y;

        public Point2(double x, double y)
        {
            X = x;
            Y = y;
        }
    }

    private readonly struct StripInterval
    {
        public readonly Point2 Toe0;
        public readonly Point2 Toe1;
        public readonly Point2 Top0;
        public readonly Point2 Top1;
        public readonly Point2 Mid0;
        public readonly Point2 Mid1;
        public readonly double ToeZ0;
        public readonly double ToeZ1;
        public readonly double TopZ0;
        public readonly double TopZ1;

        public StripInterval(
            Point2 toe0, Point2 toe1, Point2 top0, Point2 top1,
            double toeZ0, double toeZ1, double topZ0, double topZ1)
        {
            Toe0 = toe0;
            Toe1 = toe1;
            Top0 = top0;
            Top1 = top1;
            Mid0 = Lerp(toe0, top0, 0.5);
            Mid1 = Lerp(toe1, top1, 0.5);
            ToeZ0 = toeZ0;
            ToeZ1 = toeZ1;
            TopZ0 = topZ0;
            TopZ1 = topZ1;
        }
    }

    /// <summary>
    /// Grade a single wall strip against the current terrain mesh.
    /// </summary>
    public static RetainingWallGradeOutcome GradeSingleWall(
        double[] vertices, int vertexCount,
        int[] faces, int faceCount,
        WallStripDefinition strip,
        double sharpness,
        double shoulderWidth)
    {
        sharpness = Math.Clamp(sharpness, 0.0, 1.0);
        shoulderWidth = Math.Max(0.0, shoulderWidth);

        if (!ValidateStrip(strip, out string? stripError))
            return new RetainingWallGradeOutcome(false, null, stripError);

        const double dedupTol = 1e-3;
        var vertHash = new PadGrader.SpatialHash(dedupTol);
        var xyList = new List<double>(vertexCount * 2 + strip.StationCount * 4);
        var zList = new List<double>(vertexCount + strip.StationCount * 2);
        var segList = new List<(int a, int b)>(vertexCount + strip.StationCount * 4);

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
        AddBoundarySegments(segList, faces, faceCount);

        int AddVertex(double x, double y)
        {
            int near = vertHash.FindNearest(xyList, x, y, dedupTol);
            if (near >= 0) return near;

            int idx = zList.Count;
            xyList.Add(x);
            xyList.Add(y);
            zList.Add(faceGrid.InterpolateZ(x, y));
            vertHash.Insert(idx, x, y);
            return idx;
        }

        int n = strip.StationCount;
        var toeIdx = new int[n];
        var topIdx = new int[n];

        for (int i = 0; i < n; i++)
        {
            toeIdx[i] = AddVertex(strip.ToeXy[i * 2], strip.ToeXy[i * 2 + 1]);
            topIdx[i] = AddVertex(strip.TopXy[i * 2], strip.TopXy[i * 2 + 1]);
        }

        for (int i = 0; i < n - 1; i++)
        {
            AddSegment(segList, toeIdx[i], toeIdx[i + 1]);
            AddSegment(segList, topIdx[i], topIdx[i + 1]);
            AddSegment(segList, toeIdx[i], topIdx[i]);
            AddSegment(segList, toeIdx[i], topIdx[i + 1]); // deterministic diagonal
        }
        AddSegment(segList, toeIdx[n - 1], topIdx[n - 1]);

        double averageWidth = 0.0;
        for (int i = 0; i < n; i++)
        {
            double dx = strip.TopXy[i * 2] - strip.ToeXy[i * 2];
            double dy = strip.TopXy[i * 2 + 1] - strip.ToeXy[i * 2 + 1];
            averageWidth += Math.Sqrt(dx * dx + dy * dy);
        }
        averageWidth /= n;

        int totalVerts = zList.Count;
        if (totalVerts < 3)
            return new RetainingWallGradeOutcome(false, null, "Too few vertices for retaining wall triangulation.");

        var triMesh = TriangulationHelper.Triangulate(
            xyList, totalVerts, segList,
            0.0, 0.0,
            out string? triWarning,
            convex: false);

        if (triMesh == null)
            return new RetainingWallGradeOutcome(false, null, triWarning ?? "Retaining wall triangulation failed.");

        if (HasConstraintFailureWarning(triWarning))
            return new RetainingWallGradeOutcome(false, null, triWarning);

        var extracted = TriangleNetExtractor.Extract(triMesh);
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

        var intervals = new StripInterval[n - 1];
        for (int i = 0; i < n - 1; i++)
        {
            intervals[i] = new StripInterval(
                new Point2(strip.ToeXy[i * 2], strip.ToeXy[i * 2 + 1]),
                new Point2(strip.ToeXy[(i + 1) * 2], strip.ToeXy[(i + 1) * 2 + 1]),
                new Point2(strip.TopXy[i * 2], strip.TopXy[i * 2 + 1]),
                new Point2(strip.TopXy[(i + 1) * 2], strip.TopXy[(i + 1) * 2 + 1]),
                strip.ToeZ[i], strip.ToeZ[i + 1],
                strip.TopZ[i], strip.TopZ[i + 1]);
        }

        for (int i = 0; i < outVertCount; i++)
        {
            var p = new Point2(outXy[i * 2], outXy[i * 2 + 1]);
            bool inside = false;

            for (int s = 0; s < intervals.Length; s++)
            {
                if (!TrySampleInside(intervals[s], p, out double u, out double zToe, out double zTop))
                    continue;

                double smooth = SmoothStep3(u);
                double t = u + sharpness * (smooth - u);
                newZ[i] = zToe + t * (zTop - zToe);
                inside = true;
                break;
            }

            if (inside || shoulderWidth <= 0)
                continue;

            bool hasToe = ClosestPolylineSample(p, strip.ToeXy, strip.ToeZ, n, out double dToe, out double zToeRail);
            bool hasTop = ClosestPolylineSample(p, strip.TopXy, strip.TopZ, n, out double dTop, out double zTopRail);

            bool toeIn = hasToe && dToe <= shoulderWidth;
            bool topIn = hasTop && dTop <= shoulderWidth;
            if (!toeIn && !topIn)
                continue;

            bool useToe = toeIn && (!topIn || dToe <= dTop);
            double d = useToe ? dToe : dTop;
            double zRail = useToe ? zToeRail : zTopRail;
            double ratio = d / shoulderWidth;
            double falloff = 1.0 - ratio * ratio;
            newZ[i] = origZ[i] + falloff * (zRail - origZ[i]);
        }

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

        GradingResult result = BuildResult(outXy, origZ, newZ, finalVerts, outVertCount, finalFaces, outFaceCount);
        return new RetainingWallGradeOutcome(true, result, triWarning);
    }

    private static bool ValidateStrip(WallStripDefinition strip, out string? error)
    {
        error = null;
        if (strip.StationCount < 2)
        {
            error = "Retaining wall strip needs at least 2 synchronized stations.";
            return false;
        }

        int n = strip.StationCount;
        if (strip.ToeXy.Length != n * 2 || strip.TopXy.Length != n * 2 ||
            strip.ToeZ.Length != n || strip.TopZ.Length != n)
        {
            error = "Retaining wall strip arrays are not consistent with StationCount.";
            return false;
        }

        return true;
    }

    private static bool HasConstraintFailureWarning(string? warning)
    {
        if (warning == null) return false;
        return warning.Contains("Constraints could not be enforced", StringComparison.OrdinalIgnoreCase) ||
               warning.Contains("plain Delaunay", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddBoundarySegments(List<(int a, int b)> segments, int[] faces, int faceCount)
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

        foreach (var kvp in edgeFaceCount)
        {
            if (kvp.Value != 1) continue;
            int a = (int)(kvp.Key >> 32);
            int b = (int)(kvp.Key & 0xFFFFFFFFL);
            AddSegment(segments, a, b);
        }
    }

    private static void AddSegment(List<(int a, int b)> segments, int a, int b)
    {
        if (a == b) return;
        segments.Add((a, b));
    }

    private static bool TrySampleInside(StripInterval interval, Point2 p, out double u, out double zToe, out double zTop)
    {
        u = 0;
        zToe = 0;
        zTop = 0;

        bool inT0 = PointInTriangle(p, interval.Toe0, interval.Toe1, interval.Top1);
        bool inT1 = !inT0 && PointInTriangle(p, interval.Toe0, interval.Top1, interval.Top0);
        if (!inT0 && !inT1) return false;

        var midVec = Sub(interval.Mid1, interval.Mid0);
        double midLenSq = Dot(midVec, midVec);
        double s = 0;
        if (midLenSq > 1e-18)
            s = Math.Clamp(Dot(Sub(p, interval.Mid0), midVec) / midLenSq, 0.0, 1.0);

        var toePos = Lerp(interval.Toe0, interval.Toe1, s);
        var topPos = Lerp(interval.Top0, interval.Top1, s);
        var cross = Sub(topPos, toePos);
        double crossLenSq = Dot(cross, cross);
        if (crossLenSq < 1e-18) return false;

        u = Math.Clamp(Dot(Sub(p, toePos), cross) / crossLenSq, 0.0, 1.0);
        zToe = Lerp(interval.ToeZ0, interval.ToeZ1, s);
        zTop = Lerp(interval.TopZ0, interval.TopZ1, s);
        return true;
    }

    private static bool ClosestPolylineSample(
        Point2 p, double[] xy, double[] z, int count,
        out double minDist, out double zAtMin)
    {
        minDist = double.MaxValue;
        zAtMin = 0;
        if (count < 2) return false;

        for (int i = 0; i < count - 1; i++)
        {
            var a = new Point2(xy[i * 2], xy[i * 2 + 1]);
            var b = new Point2(xy[(i + 1) * 2], xy[(i + 1) * 2 + 1]);
            var ab = Sub(b, a);
            double lenSq = Dot(ab, ab);
            double t = 0;
            if (lenSq > 1e-18)
                t = Math.Clamp(Dot(Sub(p, a), ab) / lenSq, 0.0, 1.0);

            var c = Lerp(a, b, t);
            double dist = Length(Sub(p, c));
            if (dist >= minDist) continue;

            minDist = dist;
            zAtMin = Lerp(z[i], z[i + 1], t);
        }

        return minDist < double.MaxValue;
    }

    private static GradingResult BuildResult(
        double[] outXy, double[] origZ, double[] newZ,
        double[] finalVerts, int vertCount,
        int[] finalFaces, int faceCount)
    {
        double cutVol = 0, fillVol = 0;
        for (int f = 0; f < faceCount; f++)
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
            if (vol > 0) fillVol += vol;
            else cutVol += -vol;
        }

        var daylightPts = new List<double>();
        var processedEdges = new HashSet<long>();
        for (int f = 0; f < faceCount; f++)
        {
            int i0 = finalFaces[f * 3];
            int i1 = finalFaces[f * 3 + 1];
            int i2 = finalFaces[f * 3 + 2];
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

    private static bool PointInTriangle(Point2 p, Point2 a, Point2 b, Point2 c)
    {
        var v0 = Sub(c, a);
        var v1 = Sub(b, a);
        var v2 = Sub(p, a);

        double dot00 = Dot(v0, v0);
        double dot01 = Dot(v0, v1);
        double dot02 = Dot(v0, v2);
        double dot11 = Dot(v1, v1);
        double dot12 = Dot(v1, v2);

        double denom = dot00 * dot11 - dot01 * dot01;
        if (Math.Abs(denom) < 1e-18) return false;

        double inv = 1.0 / denom;
        double u = (dot11 * dot02 - dot01 * dot12) * inv;
        double v = (dot00 * dot12 - dot01 * dot02) * inv;
        const double eps = 1e-10;
        return u >= -eps && v >= -eps && (u + v) <= 1.0 + eps;
    }

    private static Point2 Lerp(Point2 a, Point2 b, double t) =>
        new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static Point2 Sub(Point2 a, Point2 b) => new(a.X - b.X, a.Y - b.Y);

    private static double Dot(Point2 a, Point2 b) => a.X * b.X + a.Y * b.Y;

    private static double Length(Point2 a) => Math.Sqrt(Dot(a, a));

    private static double SmoothStep3(double u) => u * u * (3.0 - 2.0 * u);

    private static void IncrEdge(Dictionary<long, int> dict, int a, int b)
    {
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        dict[key] = dict.GetValueOrDefault(key, 0) + 1;
    }
}
