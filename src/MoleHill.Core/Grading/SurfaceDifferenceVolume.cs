// Exact cut/fill between a mesh and a reference surface: the two triangulations are overlaid in plan.

namespace MoleHill.Core.Grading;

/// <summary>Cut (below the reference), fill (above it) and the largest height difference measured.</summary>
public readonly record struct SurfaceDifference(double Cut, double Fill, double AbsMax, int FoldFaceCount)
{
    public double Net => Cut - Fill;
}

/// <summary>
/// The volume between a mesh and the reference surface it is compared with, computed exactly.
///
/// <para>Each mesh triangle is clipped in plan against every reference triangle beneath it. On each piece
/// both surfaces are planes, so their difference is linear and integrates exactly, including where it
/// changes sign. The result depends only on the two surfaces, never on how either is triangulated. Every
/// volume MoleHill reports (Earthworks, Cut / Fill, zones, the graders' own figures, Balance Pad) goes
/// through here. The per-face sampling it replaced read the reference at one point (or three) per face. It
/// was exact only where the reference was flat across the face: a pad top spanning a coarse terrain's
/// creases read hundreds of cubic metres off, and the figure jumped whenever a diagonal flipped.</para>
///
/// <para>Ground the reference does not cover counts as unchanged. Where reference triangles overlap in plan
/// (an overhang) the face is measured by sampling instead, against the reference sheet nearest the face;
/// <see cref="SurfaceDifference.FoldFaceCount"/> says how many faces needed that.</para>
/// </summary>
public static class SurfaceDifferenceVolume
{
    private const int ChunkSize = 2048;
    private const int SampleDepth = 7;
    private const int MaxPolygon = 16;

    private sealed class Scratch
    {
        public readonly List<int> Candidates = new();
    }

    private readonly record struct FaceVolume(double Cut, double Fill, double AbsMax, bool Fold);

    /// <summary>
    /// Measures <paramref name="faceCount"/> triangles of <paramref name="vertices"/>/<paramref name="faces"/>
    /// against <paramref name="reference"/>. <paramref name="includeFace"/> limits the faces measured;
    /// <paramref name="faceCut"/>/<paramref name="faceFill"/>, when given, receive each face's own share.
    /// Chunks run in parallel and are summed in order, so the result does not depend on scheduling.
    /// </summary>
    public static SurfaceDifference Measure(
        double[] vertices,
        int[] faces,
        int faceCount,
        ReferenceSurface reference,
        double tolerance,
        bool[]? includeFace = null,
        double[]? faceCut = null,
        double[]? faceFill = null,
        Func<bool>? shouldCancel = null)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(faces);
        ArgumentNullException.ThrowIfNull(reference);

        int chunks = (faceCount + ChunkSize - 1) / ChunkSize;
        var partial = new (double Cut, double Fill, double AbsMax, int Folds)[chunks];
        bool cancelled = false;

        void RunChunk(int chunk, Scratch scratch)
        {
            double cut = 0.0, fill = 0.0, absMax = 0.0;
            int folds = 0;
            int end = Math.Min(faceCount, (chunk + 1) * ChunkSize);
            for (int face = chunk * ChunkSize; face < end; face++)
            {
                if (includeFace != null && !includeFace[face])
                    continue;
                FaceVolume volume = MeasureFace(vertices, faces, face, reference, tolerance, scratch);
                cut += volume.Cut;
                fill += volume.Fill;
                absMax = Math.Max(absMax, volume.AbsMax);
                if (volume.Fold)
                    folds++;
                if (faceCut != null)
                    faceCut[face] = volume.Cut;
                if (faceFill != null)
                    faceFill[face] = volume.Fill;
            }

            partial[chunk] = (cut, fill, absMax, folds);
        }

        if (chunks <= 1)
        {
            if (shouldCancel?.Invoke() == true)
                throw new OperationCanceledException("Volume measurement cancelled.");
            if (chunks == 1)
                RunChunk(0, new Scratch());
        }
        else
        {
            Parallel.For(0, chunks, () => new Scratch(), (chunk, state, scratch) =>
            {
                if (shouldCancel?.Invoke() == true)
                {
                    cancelled = true;
                    state.Stop();
                    return scratch;
                }

                RunChunk(chunk, scratch);
                return scratch;
            }, static _ => { });
            if (cancelled)
                throw new OperationCanceledException("Volume measurement cancelled.");
        }

        double totalCut = 0.0, totalFill = 0.0, totalAbsMax = 0.0;
        int totalFolds = 0;
        foreach ((double cut, double fill, double absMax, int folds) in partial)
        {
            totalCut += cut;
            totalFill += fill;
            totalAbsMax = Math.Max(totalAbsMax, absMax);
            totalFolds += folds;
        }

        return new SurfaceDifference(totalCut, totalFill, totalAbsMax, totalFolds);
    }

    private static FaceVolume MeasureFace(double[] v, int[] faces, int face, ReferenceSurface reference, double tolerance, Scratch scratch)
    {
        int ia = faces[face * 3], ib = faces[face * 3 + 1], ic = faces[face * 3 + 2];
        double ax = v[ia * 3], ay = v[ia * 3 + 1], az = v[ia * 3 + 2];
        double bx = v[ib * 3], by = v[ib * 3 + 1], bz = v[ib * 3 + 2];
        double cx = v[ic * 3], cy = v[ic * 3 + 1], cz = v[ic * 3 + 2];
        double ux = bx - ax, uy = by - ay, uz = bz - az;
        double wx = cx - ax, wy = cy - ay, wz = cz - az;
        double det = ux * wy - wx * uy;
        double area = Math.Abs(det) * 0.5;
        // A face standing on edge covers no ground.
        if (!(area > reference.Scale * reference.Scale * 1e-18))
            return default;

        double gx = (uz * wy - wz * uy) / det;
        double gy = (ux * wz - wx * uz) / det;

        List<int> candidates = scratch.Candidates;
        reference.CollectCandidates(
            Math.Min(ax, Math.Min(bx, cx)), Math.Min(ay, Math.Min(by, cy)),
            Math.Max(ax, Math.Max(bx, cx)), Math.Max(ay, Math.Max(by, cy)),
            candidates);

        // A face copied unchanged from the reference differs from it nowhere.
        foreach (int r in candidates)
        {
            if (IsSameTriangle(reference, r, ax, ay, az, bx, by, bz, cx, cy, cz))
                return default;
        }

        Span<double> px = stackalloc double[MaxPolygon];
        Span<double> py = stackalloc double[MaxPolygon];
        Span<double> qx = stackalloc double[MaxPolygon];
        Span<double> qy = stackalloc double[MaxPolygon];

        double cut = 0.0, fill = 0.0, absMax = 0.0, covered = 0.0;
        foreach (int r in candidates)
        {
            // The face's corners, anticlockwise.
            int n = 3;
            px[0] = ax; py[0] = ay;
            if (det > 0)
            {
                px[1] = bx; py[1] = by; px[2] = cx; py[2] = cy;
            }
            else
            {
                px[1] = cx; py[1] = cy; px[2] = bx; py[2] = by;
            }

            double orient = reference.SignedDoubleArea(r) > 0 ? 1.0 : -1.0;
            reference.Corner(r, 0, out double r0x, out double r0y, out _);
            reference.Corner(r, 1, out double r1x, out double r1y, out _);
            reference.Corner(r, 2, out double r2x, out double r2y, out _);
            n = ClipByEdge(px, py, n, r0x, r0y, r1x, r1y, orient, qx, qy);
            if (n >= 3)
                n = ClipByEdge(qx, qy, n, r1x, r1y, r2x, r2y, orient, px, py);
            if (n >= 3)
                n = ClipByEdge(px, py, n, r2x, r2y, r0x, r0y, orient, qx, qy);
            if (n < 3)
                continue;

            // The clipped piece is convex and anticlockwise; fan it from its first corner.
            double d0 = az + gx * (qx[0] - ax) + gy * (qy[0] - ay) - reference.HeightAt(r, qx[0], qy[0]);
            double dPrevious = az + gx * (qx[1] - ax) + gy * (qy[1] - ay) - reference.HeightAt(r, qx[1], qy[1]);
            absMax = Math.Max(absMax, Math.Max(Math.Abs(d0), Math.Abs(dPrevious)));
            for (int i = 2; i < n; i++)
            {
                double dNext = az + gx * (qx[i] - ax) + gy * (qy[i] - ay) - reference.HeightAt(r, qx[i], qy[i]);
                absMax = Math.Max(absMax, Math.Abs(dNext));
                double pieceArea = ((qx[i - 1] - qx[0]) * (qy[i] - qy[0]) - (qx[i] - qx[0]) * (qy[i - 1] - qy[0])) * 0.5;
                if (pieceArea > 0.0)
                {
                    AccumulateLinear(pieceArea, d0, dPrevious, dNext, ref cut, ref fill);
                    covered += pieceArea;
                }

                dPrevious = dNext;
            }
        }

        // More reference than face beneath it means reference triangles overlap here (an overhang): the
        // overlay would count the ground twice, so sample against the nearest sheet instead.
        if (covered > area * (1.0 + 1e-6) + reference.Scale * reference.Scale * 1e-15)
        {
            (double sampledCut, double sampledFill, double sampledMax) = SampleFace(
                ax, ay, az, bx, by, bz, cx, cy, cz, reference, tolerance);
            return new FaceVolume(sampledCut, sampledFill, sampledMax, Fold: true);
        }

        return new FaceVolume(cut, fill, absMax, Fold: false);
    }

    private static bool IsSameTriangle(
        ReferenceSurface reference, int r,
        double ax, double ay, double az, double bx, double by, double bz, double cx, double cy, double cz)
    {
        reference.Corner(r, 0, out double x0, out double y0, out double z0);
        reference.Corner(r, 1, out double x1, out double y1, out double z1);
        reference.Corner(r, 2, out double x2, out double y2, out double z2);

        static bool Matches(double x, double y, double z, double x0, double y0, double z0, double x1, double y1, double z1, double x2, double y2, double z2) =>
            (x == x0 && y == y0 && z == z0) || (x == x1 && y == y1 && z == z1) || (x == x2 && y == y2 && z == z2);

        return Matches(ax, ay, az, x0, y0, z0, x1, y1, z1, x2, y2, z2) &&
               Matches(bx, by, bz, x0, y0, z0, x1, y1, z1, x2, y2, z2) &&
               Matches(cx, cy, cz, x0, y0, z0, x1, y1, z1, x2, y2, z2);
    }

    /// <summary>Sutherland–Hodgman: keeps the part of the polygon on the inner side of one reference edge.</summary>
    private static int ClipByEdge(
        Span<double> inX, Span<double> inY, int n,
        double ex0, double ey0, double ex1, double ey1, double orient,
        Span<double> outX, Span<double> outY)
    {
        double dx = ex1 - ex0, dy = ey1 - ey0;
        int m = 0;
        for (int i = 0; i < n; i++)
        {
            int j = i + 1 == n ? 0 : i + 1;
            double si = orient * (dx * (inY[i] - ey0) - dy * (inX[i] - ex0));
            double sj = orient * (dx * (inY[j] - ey0) - dy * (inX[j] - ex0));
            if (si >= 0.0 && m < MaxPolygon)
            {
                outX[m] = inX[i];
                outY[m] = inY[i];
                m++;
            }

            if ((si >= 0.0) != (sj >= 0.0) && m < MaxPolygon)
            {
                double t = si / (si - sj);
                outX[m] = inX[i] + t * (inX[j] - inX[i]);
                outY[m] = inY[i] + t * (inY[j] - inY[i]);
                m++;
            }
        }

        return m;
    }

    /// <summary>
    /// Fallback for a face over overlapping reference triangles: splits the face until the reference
    /// (read through the projector, which picks the sheet nearest the face) is linear across each piece.
    /// </summary>
    private static (double Cut, double Fill, double AbsMax) SampleFace(
        double ax, double ay, double az, double bx, double by, double bz, double cx, double cy, double cz,
        ReferenceSurface reference, double tolerance)
    {
        Analysis.MeshHeightProjector projector = reference.Projector;
        double deltaTolerance = Math.Max(tolerance, 1e-9) * 0.1;
        double cut = 0.0, fill = 0.0, absMax = 0.0;

        double DeltaAt(double x, double y, double z) =>
            projector.TryProjectZ(x, y, z, tolerance, out double baseZ, out _) ? z - baseZ : 0.0;

        void Piece(
            double x0, double y0, double z0, double d0,
            double x1, double y1, double z1, double d1,
            double x2, double y2, double z2, double d2,
            int depth)
        {
            double pieceArea = Math.Abs((x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0)) * 0.5;
            if (pieceArea <= 0.0)
                return;
            absMax = Math.Max(absMax, Math.Max(Math.Abs(d0), Math.Max(Math.Abs(d1), Math.Abs(d2))));
            if (depth < SampleDepth)
            {
                double mx01 = (x0 + x1) * 0.5, my01 = (y0 + y1) * 0.5, mz01 = (z0 + z1) * 0.5;
                double mx12 = (x1 + x2) * 0.5, my12 = (y1 + y2) * 0.5, mz12 = (z1 + z2) * 0.5;
                double mx20 = (x2 + x0) * 0.5, my20 = (y2 + y0) * 0.5, mz20 = (z2 + z0) * 0.5;
                double m01 = DeltaAt(mx01, my01, mz01), m12 = DeltaAt(mx12, my12, mz12), m20 = DeltaAt(mx20, my20, mz20);
                double centre = DeltaAt((x0 + x1 + x2) / 3.0, (y0 + y1 + y2) / 3.0, (z0 + z1 + z2) / 3.0);
                double bend = Math.Max(
                    Math.Max(Math.Abs(m01 - (d0 + d1) * 0.5), Math.Abs(m12 - (d1 + d2) * 0.5)),
                    Math.Max(Math.Abs(m20 - (d2 + d0) * 0.5), Math.Abs(centre - (d0 + d1 + d2) / 3.0)));
                if (bend > deltaTolerance)
                {
                    Piece(x0, y0, z0, d0, mx01, my01, mz01, m01, mx20, my20, mz20, m20, depth + 1);
                    Piece(mx01, my01, mz01, m01, x1, y1, z1, d1, mx12, my12, mz12, m12, depth + 1);
                    Piece(mx20, my20, mz20, m20, mx12, my12, mz12, m12, x2, y2, z2, d2, depth + 1);
                    Piece(mx01, my01, mz01, m01, mx12, my12, mz12, m12, mx20, my20, mz20, m20, depth + 1);
                    return;
                }
            }

            AccumulateLinear(pieceArea, d0, d1, d2, ref cut, ref fill);
        }

        Piece(ax, ay, az, DeltaAt(ax, ay, az), bx, by, bz, DeltaAt(bx, by, bz), cx, cy, cz, DeltaAt(cx, cy, cz), 0);
        return (cut, fill, absMax);
    }

    /// <summary>Exact cut and fill of a difference that varies linearly over a triangle of the given area.</summary>
    internal static void AccumulateLinear(double area, double d0, double d1, double d2, ref double cut, ref double fill)
    {
        double total = area * (d0 + d1 + d2) / 3.0;
        bool anyPositive = d0 > 0 || d1 > 0 || d2 > 0;
        bool anyNegative = d0 < 0 || d1 < 0 || d2 < 0;
        if (!(anyPositive && anyNegative))
        {
            if (total >= 0)
                fill += total;
            else
                cut -= total;
            return;
        }

        // The lone corner of one sign cuts off a similar triangle at the zero line; the rest is the other sign.
        // A corner exactly on zero joins the non-positive side; the cut-off triangle's formula holds for it.
        bool p0 = d0 > 0, p1 = d1 > 0, p2 = d2 > 0;
        double lone, other1, other2;
        if (p0 != p1 && p0 != p2)
            (lone, other1, other2) = (d0, d1, d2);
        else if (p1 != p0 && p1 != p2)
            (lone, other1, other2) = (d1, d0, d2);
        else
            (lone, other1, other2) = (d2, d0, d1);

        double denominator = (lone - other1) * (lone - other2);
        double loneVolume = denominator == 0.0 ? 0.0 : area * lone * lone * lone / (3.0 * denominator);
        double otherVolume = total - loneVolume;
        if (loneVolume >= 0)
            fill += loneVolume;
        else
            cut -= loneVolume;
        if (otherVolume >= 0)
            fill += otherVolume;
        else
            cut -= otherVolume;
    }
}
