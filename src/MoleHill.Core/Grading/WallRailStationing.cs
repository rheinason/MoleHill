namespace MoleHill.Core.Grading;

/// <summary>Synchronizes paired wall rails in plan, anchoring matching bends before sampling between them.</summary>
public static class WallRailStationing
{
    public readonly record struct Result(double[] First, double[] Second);
    private readonly record struct Corner(int Index, double InX, double InY, double OutX, double OutY);
    private const double DirectionAgreement = 0.9659258262890683; // 15 degrees

    /// <summary>
    /// Inputs are ordered XYZ triples with matching direction. Closed inputs omit the repeated endpoint.
    /// Every authored vertex survives; Z is interpolated on its own rail, never used as station distance.
    /// </summary>
    public static Result Synchronize(double[] first, double[] second, bool closed, double tolerance)
    {
        if (first.Length < 6 || second.Length < 6 || first.Length % 3 != 0 || second.Length % 3 != 0)
            return new Result(Array.Empty<double>(), Array.Empty<double>());

        List<(int A, int B)> matches = MatchCorners(first, second, closed, tolerance);
        if (closed && matches.Count > 0)
        {
            // Put the seam on corresponding bends. Closest-point seam alignment alone can put one
            // seam partway along the other rail's segment, cutting across the corner on the wrap.
            first = Rotate(first, matches[0].A);
            second = Rotate(second, matches[0].B);
            matches = MatchCorners(first, second, closed, tolerance);
        }

        double[] aCum = PlanLengths(first, closed);
        double[] bCum = PlanLengths(second, closed);
        if (aCum[^1] <= 1e-12 || bCum[^1] <= 1e-12)
            return new Result(Array.Empty<double>(), Array.Empty<double>());

        var anchors = new List<(int A, int B)> { (0, 0) };
        foreach (var match in matches)
            if (match.A > anchors[^1].A && match.B > anchors[^1].B)
                anchors.Add(match);
        anchors.Add((aCum.Length - 1, bCum.Length - 1));

        var aResult = new List<double>();
        var bResult = new List<double>();
        for (int k = 1; k < anchors.Count; k++)
        {
            var start = anchors[k - 1];
            var end = anchors[k];
            double aSpan = aCum[end.A] - aCum[start.A];
            double bSpan = bCum[end.B] - bCum[start.B];
            if (aSpan <= 1e-12 || bSpan <= 1e-12)
                continue;
            var stations = new SortedSet<double> { 0, 1 };
            for (int i = start.A + 1; i < end.A; i++)
                stations.Add((aCum[i] - aCum[start.A]) / aSpan);
            for (int i = start.B + 1; i < end.B; i++)
                stations.Add((bCum[i] - bCum[start.B]) / bSpan);
            double previous = double.NegativeInfinity;
            foreach (double station in stations)
            {
                // Each interval owns its start; the last open interval also owns the endpoint.
                if (station == 1 && (closed || k < anchors.Count - 1))
                    continue;
                if (station - previous < 1e-10)
                    continue;
                previous = station;
                AppendSample(aResult, first, aCum, aCum[start.A] + station * aSpan);
                AppendSample(bResult, second, bCum, bCum[start.B] + station * bSpan);
            }
        }
        return new Result(aResult.ToArray(), bResult.ToArray());
    }

    private static List<(int A, int B)> MatchCorners(double[] a, double[] b, bool closed, double tolerance)
    {
        var ac = Corners(a, closed);
        var bc = Corners(b, closed);
        var matches = new List<(int A, int B)>();
        if (ac.Count == 0 || bc.Count == 0)
            return matches;
        // A compatible bend must also be near the partner rail. This prevents a missing bend from
        // acquiring an unrelated, similarly oriented corner elsewhere on a long winding wall.
        double reach = Math.Max(tolerance, Math.Max(MaxNearestDistance(ac, a, b, closed), MaxNearestDistance(bc, b, a, closed))) * 4;
        foreach (var corner in ac)
        {
            int j = NearestCompatible(corner, a, bc, b, reach);
            if (j < 0)
                continue;
            int reciprocal = NearestCompatible(bc[j], b, ac, a, reach);
            if (reciprocal >= 0 && ac[reciprocal].Index == corner.Index)
                matches.Add((corner.Index, bc[j].Index));
        }
        return matches;
    }

    private static int NearestCompatible(Corner corner, double[] source, List<Corner> candidates, double[] target, double reach)
    {
        int best = -1;
        double bestSquared = reach * reach;
        for (int i = 0; i < candidates.Count; i++)
        {
            Corner candidate = candidates[i];
            if (corner.InX * candidate.InX + corner.InY * candidate.InY < DirectionAgreement ||
                corner.OutX * candidate.OutX + corner.OutY * candidate.OutY < DirectionAgreement)
                continue;
            double dx = source[corner.Index * 3] - target[candidate.Index * 3];
            double dy = source[corner.Index * 3 + 1] - target[candidate.Index * 3 + 1];
            double squared = dx * dx + dy * dy;
            if (squared < bestSquared)
            {
                bestSquared = squared;
                best = i;
            }
        }
        return best;
    }

    private static List<Corner> Corners(double[] points, bool closed)
    {
        int count = points.Length / 3;
        var result = new List<Corner>();
        for (int i = closed ? 0 : 1; i < (closed ? count : count - 1); i++)
        {
            int before = (i + count - 1) % count;
            int after = (i + 1) % count;
            double ix = points[i * 3] - points[before * 3];
            double iy = points[i * 3 + 1] - points[before * 3 + 1];
            double ox = points[after * 3] - points[i * 3];
            double oy = points[after * 3 + 1] - points[i * 3 + 1];
            double il = Math.Sqrt(ix * ix + iy * iy);
            double ol = Math.Sqrt(ox * ox + oy * oy);
            if (il <= 1e-12 || ol <= 1e-12)
                continue;
            ix /= il; iy /= il; ox /= ol; oy /= ol;
            if (ix * ox + iy * oy < DirectionAgreement)
                result.Add(new Corner(i, ix, iy, ox, oy));
        }
        return result;
    }

    private static double MaxNearestDistance(List<Corner> corners, double[] source, double[] target, bool closed)
    {
        double maximum = 0;
        int segments = target.Length / 3 - (closed ? 0 : 1);
        foreach (var corner in corners)
        {
            int i = corner.Index * 3;
            double nearest = double.PositiveInfinity;
            for (int j = 0; j < segments; j++)
            {
                int p = j * 3, q = (j + 1) * 3 % target.Length;
                double dx = target[q] - target[p], dy = target[q + 1] - target[p + 1];
                double lengthSquared = dx * dx + dy * dy;
                double t = lengthSquared <= 1e-24 ? 0 : Math.Clamp(((source[i] - target[p]) * dx + (source[i + 1] - target[p + 1]) * dy) / lengthSquared, 0, 1);
                double ex = source[i] - target[p] - t * dx, ey = source[i + 1] - target[p + 1] - t * dy;
                nearest = Math.Min(nearest, ex * ex + ey * ey);
            }
            maximum = Math.Max(maximum, nearest);
        }
        return Math.Sqrt(maximum);
    }

    private static double[] Rotate(double[] points, int start)
    {
        var result = new double[points.Length];
        for (int i = 0; i < points.Length / 3; i++)
            Array.Copy(points, (start * 3 + i * 3) % points.Length, result, i * 3, 3);
        return result;
    }

    // A vertical step (two vertices at one plan position) still needs a span of its own, or both ends share
    // one station and the step becomes a ramp. A thousandth of its rise is far below any sloped segment's
    // plan length, so stationing stays plan-based everywhere else.
    private const double VerticalStepWeight = 1e-3;

    private static double[] PlanLengths(double[] points, bool closed)
    {
        int count = points.Length / 3;
        var lengths = new double[count + (closed ? 1 : 0)];
        for (int i = 1; i < lengths.Length; i++)
        {
            int p = (i - 1) * 3, q = i * 3 % points.Length;
            double dx = points[q] - points[p], dy = points[q + 1] - points[p + 1];
            double rise = Math.Abs(points[q + 2] - points[p + 2]);
            lengths[i] = lengths[i - 1] + Math.Max(Math.Sqrt(dx * dx + dy * dy), VerticalStepWeight * rise);
        }
        return lengths;
    }

    private static void AppendSample(List<double> output, double[] points, double[] lengths, double along)
    {
        int found = Array.BinarySearch(lengths, along);
        if (found >= 0)
        {
            int vertex = found * 3 % points.Length;
            output.Add(points[vertex]); output.Add(points[vertex + 1]); output.Add(points[vertex + 2]);
            return;
        }
        int segment = Math.Clamp(~found - 1, 0, lengths.Length - 2);
        int p = segment * 3, q = (segment + 1) * 3 % points.Length;
        double t = (along - lengths[segment]) / (lengths[segment + 1] - lengths[segment]);
        for (int axis = 0; axis < 3; axis++)
            output.Add(points[p + axis] + t * (points[q + axis] - points[p + axis]));
    }
}
