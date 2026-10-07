namespace MoleHill.Core.Grading;

/// <summary>Synchronizes paired wall rails in plan, anchoring matching bends before sampling between them.</summary>
public static class WallRailStationing
{
    public readonly record struct Result(double[] First, double[] Second);

    // A bend: the run of turning vertices Start..End (plan positions StartAlong..EndAlong), anchored at its
    // turn-weighted middle (plan position Along, point X, Y), with the directions entering and leaving it.
    private readonly record struct Bend(
        int Start, int End, double StartAlong, double EndAlong, double Along, double X, double Y,
        double InX, double InY, double OutX, double OutY);
    private const double DirectionAgreement = 0.9659258262890683; // 15 degrees

    /// <summary>
    /// Inputs are ordered XYZ triples with matching direction. Closed inputs omit the repeated endpoint.
    /// Every authored vertex survives; Z is interpolated on its own rail, never used as station distance.
    /// </summary>
    public static Result Synchronize(double[] first, double[] second, bool closed, double tolerance)
    {
        if (first.Length < 6 || second.Length < 6 || first.Length % 3 != 0 || second.Length % 3 != 0)
            return new Result(Array.Empty<double>(), Array.Empty<double>());

        List<(double A, double B)> positions = AnchorPositions(MatchBends(first, second, closed, tolerance), tolerance);
        int[] firstAnchors = InsertAnchors(ref first, closed, positions.Select(position => position.A).ToArray(), tolerance);
        int[] secondAnchors = InsertAnchors(ref second, closed, positions.Select(position => position.B).ToArray(), tolerance);
        var matches = firstAnchors.Zip(secondAnchors, (a, b) => (A: a, B: b)).OrderBy(match => match.A).ToList();
        if (closed && matches.Count > 0)
        {
            // Put the seam on corresponding bends. Closest-point seam alignment alone can put one
            // seam partway along the other rail's segment, cutting across the corner on the wrap.
            int firstShift = matches[0].A, secondShift = matches[0].B;
            int firstCount = first.Length / 3, secondCount = second.Length / 3;
            first = Rotate(first, firstShift);
            second = Rotate(second, secondShift);
            matches = matches
                .Select(match => (A: (match.A - firstShift + firstCount) % firstCount, B: (match.B - secondShift + secondCount) % secondCount))
                .OrderBy(match => match.A)
                .ToList();
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
            // Each interval owns its start; the last open interval also owns the endpoint.
            bool ownsEnd = !closed && k == anchors.Count - 1;
            var stations = new List<(double Station, int A, int B)> { (0, start.A, start.B) };
            for (int i = start.A + 1; i < end.A; i++)
                stations.Add(((aCum[i] - aCum[start.A]) / aSpan, i, -1));
            for (int i = start.B + 1; i < end.B; i++)
                stations.Add(((bCum[i] - bCum[start.B]) / bSpan, -1, i));
            if (ownsEnd)
                stations.Add((1, end.A, end.B));
            stations.Sort((x, y) => x.Station.CompareTo(y.Station));

            // Merge stations closer than the tolerance on both rails into one, so the loft gets no
            // micrometre span where one rail's vertex lands just beside the other's. Each rail keeps its
            // own authored vertex; two vertices of one rail never merge (a vertical step stays a step).
            int count = 0;
            (double Station, int A, int B) pending = default;
            foreach (var station in stations)
            {
                if (count > 0 && CanMerge(pending, station, first, second, aCum, bCum, start, aSpan, bSpan, tolerance))
                {
                    pending = (pending.Station, pending.A >= 0 ? pending.A : station.A, pending.B >= 0 ? pending.B : station.B);
                    continue;
                }
                if (count++ > 0)
                    EmitStation(aResult, bResult, first, second, aCum, bCum, start, aSpan, bSpan, pending);
                pending = station;
            }
            EmitStation(aResult, bResult, first, second, aCum, bCum, start, aSpan, bSpan, pending);
        }
        return new Result(aResult.ToArray(), bResult.ToArray());
    }

    private static bool CanMerge(
        (double Station, int A, int B) pending,
        (double Station, int A, int B) next,
        double[] first, double[] second, double[] aCum, double[] bCum,
        (int A, int B) start, double aSpan, double bSpan, double tolerance)
    {
        if ((pending.A >= 0 && next.A >= 0) || (pending.B >= 0 && next.B >= 0))
            return false;
        return Distance(StationPoint(first, aCum, start.A, aSpan, pending.Station, pending.A),
                        StationPoint(first, aCum, start.A, aSpan, next.Station, next.A)) <= tolerance &&
               Distance(StationPoint(second, bCum, start.B, bSpan, pending.Station, pending.B),
                        StationPoint(second, bCum, start.B, bSpan, next.Station, next.B)) <= tolerance;
    }

    private static void EmitStation(
        List<double> aResult, List<double> bResult, double[] first, double[] second, double[] aCum, double[] bCum,
        (int A, int B) start, double aSpan, double bSpan, (double Station, int A, int B) station)
    {
        aResult.AddRange(StationPoint(first, aCum, start.A, aSpan, station.Station, station.A));
        bResult.AddRange(StationPoint(second, bCum, start.B, bSpan, station.Station, station.B));
    }

    // A station's point on one rail: the authored vertex when it has one, else the plan-length sample.
    private static double[] StationPoint(double[] points, double[] cum, int startIndex, double span, double station, int vertex)
    {
        if (vertex >= 0)
        {
            int v = vertex * 3 % points.Length;
            return new[] { points[v], points[v + 1], points[v + 2] };
        }
        var sample = new List<double>(3);
        AppendSample(sample, points, cum, cum[startIndex] + station * span);
        return sample.ToArray();
    }

    private static double Distance(double[] p, double[] q)
    {
        double dx = p[0] - q[0], dy = p[1] - q[1], dz = p[2] - q[2];
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>
    /// Pairs the bends of the two rails. A bend is a run of consecutive same-direction turns, so a sharp
    /// corner, a chamfer and a tessellated fillet are all one bend: Rhino's Offset with round corners gives
    /// one rail a 90-degree vertex and the other a 5-degrees-per-step arc. Matching single vertices found
    /// no bend on the arc, so the sharp corner paired with a point before the arc and the wall cells
    /// crossed. Turns are joined generously; where that merges two bends on one rail but not on the other
    /// (the outside rail of a bend is longer), the merged bend matches nothing and is split at its widest
    /// gap until every bend that can match has.
    /// </summary>
    private static List<(Bend A, Bend B)> MatchBends(double[] first, double[] second, bool closed, double tolerance)
    {
        double width = EstimateWidth(first, second, closed);
        double[] firstTurns = Turns(first, closed), secondTurns = Turns(second, closed);
        double[] firstAlong = PlanLengths(first, closed), secondAlong = PlanLengths(second, closed);
        List<(int Start, int End)> firstRuns = Runs(firstTurns, firstAlong, width);
        List<(int Start, int End)> secondRuns = Runs(secondTurns, secondAlong, width);
        while (true)
        {
            List<Bend> a = Bends(first, firstTurns, firstAlong, firstRuns);
            List<Bend> b = Bends(second, secondTurns, secondAlong, secondRuns);
            List<(int A, int B)> matches = MatchBends(a, first, b, second, closed, tolerance);
            bool split = SplitUnmatched(firstRuns, a, matches.Select(match => match.A), firstTurns, firstAlong);
            split |= SplitUnmatched(secondRuns, b, matches.Select(match => match.B), secondTurns, secondAlong);
            if (!split)
                return matches.Select(match => (A: a[match.A], B: b[match.B])).OrderBy(match => match.A.Along).ToList();
        }
    }

    // Every matched bend pairs its middles. When both rails turn along an arc, their starts and ends pair
    // too: the arcs then map onto each other and the legs onto legs. With the middle alone, a tight fillet
    // paired "rest of the leg plus half the arc" on one rail with "half a longer arc" on the other and
    // sheared until the cells folded. Against a sharp corner only the middle anchors, or the corner would
    // stand still while the arc swept round it.
    private static List<(double A, double B)> AnchorPositions(List<(Bend A, Bend B)> bends, double tolerance)
    {
        var positions = new List<(double A, double B)>(bends.Count * 3);
        foreach (var (a, b) in bends)
        {
            bool arcs = a.EndAlong - a.StartAlong > tolerance && b.EndAlong - b.StartAlong > tolerance;
            if (arcs)
                positions.Add((a.StartAlong, b.StartAlong));
            positions.Add((a.Along, b.Along));
            if (arcs)
                positions.Add((a.EndAlong, b.EndAlong));
        }
        return positions;
    }

    private static List<(int A, int B)> MatchBends(List<Bend> a, double[] first, List<Bend> b, double[] second, bool closed, double tolerance)
    {
        var matches = new List<(int A, int B)>();
        if (a.Count == 0 || b.Count == 0)
            return matches;
        // A compatible bend must also be near the partner rail. This prevents a missing bend from
        // acquiring an unrelated, similarly oriented corner elsewhere on a long winding wall.
        double nearest = 0;
        foreach (Bend bend in a)
            nearest = Math.Max(nearest, NearestDistance(bend.X, bend.Y, second, closed));
        foreach (Bend bend in b)
            nearest = Math.Max(nearest, NearestDistance(bend.X, bend.Y, first, closed));
        double reach = Math.Max(tolerance, nearest) * 4;
        for (int i = 0; i < a.Count; i++)
        {
            int j = NearestCompatible(a[i], b, reach);
            if (j >= 0 && NearestCompatible(b[j], a, reach) == i)
                matches.Add((i, j));
        }
        return matches;
    }

    private static int NearestCompatible(Bend bend, List<Bend> candidates, double reach)
    {
        int best = -1;
        double bestSquared = reach * reach;
        for (int i = 0; i < candidates.Count; i++)
        {
            Bend candidate = candidates[i];
            if (bend.InX * candidate.InX + bend.InY * candidate.InY < DirectionAgreement ||
                bend.OutX * candidate.OutX + bend.OutY * candidate.OutY < DirectionAgreement)
                continue;
            double dx = bend.X - candidate.X, dy = bend.Y - candidate.Y;
            double squared = dx * dx + dy * dy;
            if (squared < bestSquared)
            {
                bestSquared = squared;
                best = i;
            }
        }
        return best;
    }

    // Signed turn at each vertex; zero at an open rail's ends.
    private static double[] Turns(double[] points, bool closed)
    {
        int count = points.Length / 3;
        var turns = new double[count];
        for (int i = closed ? 0 : 1; i < (closed ? count : count - 1); i++)
        {
            if (Direction(points, (i + count - 1) % count, i, out double ix, out double iy) &&
                Direction(points, i, (i + 1) % count, out double ox, out double oy))
                turns[i] = Math.Atan2(ix * oy - iy * ox, ix * ox + iy * oy);
        }
        return turns;
    }

    // Consecutive turns join one run when they turn the same way within two wall widths: a chamfer, or the
    // steps of a fillet. Runs do not wrap a closed rail's seam; a bend split there anchors as two smaller
    // bends or not at all, which is no worse than an unmatched corner.
    private static List<(int Start, int End)> Runs(double[] turns, double[] along, double width)
    {
        var runs = new List<(int Start, int End)>();
        for (int i = 0; i < turns.Length; i++)
        {
            if (Math.Abs(turns[i]) < MinimumTurn)
                continue;
            if (runs.Count > 0 && Math.Sign(turns[runs[^1].End]) == Math.Sign(turns[i]) &&
                along[i] - along[runs[^1].End] <= 2 * width)
                runs[^1] = (runs[^1].Start, i);
            else
                runs.Add((i, i));
        }
        return runs;
    }

    private static List<Bend> Bends(double[] points, double[] turns, double[] along, List<(int Start, int End)> runs)
    {
        int count = points.Length / 3;
        var bends = new List<Bend>(runs.Count);
        foreach (var (start, end) in runs)
        {
            double total = 0, weighted = 0;
            for (int i = start; i <= end; i++)
            {
                total += turns[i];
                weighted += turns[i] * along[i];
            }
            if (Math.Abs(total) < CornerTurn)
                continue;
            double middle = weighted / total;
            var point = new List<double>(3);
            AppendSample(point, points, along, middle);
            Direction(points, (start + count - 1) % count, start, out double ix, out double iy);
            Direction(points, end, (end + 1) % count, out double ox, out double oy);
            bends.Add(new Bend(start, end, along[start], along[end], middle, point[0], point[1], ix, iy, ox, oy));
        }
        return bends;
    }

    // Splits each unmatched multi-vertex bend at its widest gap between turns. Returns whether any split.
    private static bool SplitUnmatched(
        List<(int Start, int End)> runs, List<Bend> bends, IEnumerable<int> matched, double[] turns, double[] along)
    {
        var isMatched = new HashSet<int>(matched);
        bool split = false;
        for (int i = 0; i < bends.Count; i++)
        {
            Bend bend = bends[i];
            if (isMatched.Contains(i) || bend.Start == bend.End)
                continue;
            int previous = bend.Start, cutAfter = bend.Start, cutBefore = bend.End;
            double widest = -1;
            for (int v = bend.Start + 1; v <= bend.End; v++)
            {
                if (Math.Abs(turns[v]) < MinimumTurn)
                    continue;
                if (along[v] - along[previous] > widest)
                {
                    widest = along[v] - along[previous];
                    cutAfter = previous;
                    cutBefore = v;
                }
                previous = v;
            }
            int run = runs.IndexOf((bend.Start, bend.End));
            runs[run] = (bend.Start, cutAfter);
            runs.Insert(run + 1, (cutBefore, bend.End));
            split = true;
        }
        return split;
    }

    // Inserts a vertex at each plan position (in any order), or reuses a vertex within the tolerance of
    // it, and returns each position's vertex index in the input order.
    private static int[] InsertAnchors(ref double[] points, bool closed, double[] positions, double tolerance)
    {
        var indices = new int[positions.Length];
        if (positions.Length == 0)
            return indices;

        int count = points.Length / 3;
        double[] along = PlanLengths(points, closed);
        int[] order = Enumerable.Range(0, positions.Length).OrderBy(i => positions[i]).ToArray();
        double snap = Math.Max(tolerance, 1e-9);
        var output = new List<double>(points.Length + positions.Length * 3);
        int next = 0;
        for (int i = 0; i < count; i++)
        {
            while (next < order.Length && positions[order[next]] < along[i] - snap)
            {
                indices[order[next]] = output.Count / 3;
                AppendSample(output, points, along, positions[order[next++]]);
            }
            while (next < order.Length && positions[order[next]] <= along[i] + snap)
                indices[order[next++]] = output.Count / 3;
            output.Add(points[i * 3]); output.Add(points[i * 3 + 1]); output.Add(points[i * 3 + 2]);
        }
        while (next < order.Length)
        {
            // Past the last vertex: only on a closed rail, on its closing segment.
            indices[order[next]] = output.Count / 3;
            AppendSample(output, points, along, positions[order[next++]]);
        }
        points = output.ToArray();
        return indices;
    }

    // A turn this small is a collinear height vertex or noise, not part of a bend.
    private const double MinimumTurn = 0.5 * Math.PI / 180;
    // A bend turns at least this much in total, whatever it is made of.
    private const double CornerTurn = 15 * Math.PI / 180;

    private static bool Direction(double[] points, int from, int to, out double x, out double y)
    {
        x = points[to * 3] - points[from * 3];
        y = points[to * 3 + 1] - points[from * 3 + 1];
        double length = Math.Sqrt(x * x + y * y);
        if (length <= 1e-12)
        {
            x = y = 0;
            return false;
        }
        x /= length;
        y /= length;
        return true;
    }

    // Typical plan distance between the rails: the median over both rails' vertices. Each probe scans the
    // whole partner rail, so a dense ring samples at most WidthSamples vertices per rail.
    private const int WidthSamples = 64;

    private static double EstimateWidth(double[] a, double[] b, bool closed)
    {
        var distances = new List<double>(2 * WidthSamples);
        AddWidthSamples(distances, a, b, closed);
        AddWidthSamples(distances, b, a, closed);
        distances.Sort();
        return distances[distances.Count / 2];
    }

    private static void AddWidthSamples(List<double> distances, double[] source, double[] target, bool closed)
    {
        int count = source.Length / 3;
        int stride = Math.Max(1, (count + WidthSamples - 1) / WidthSamples);
        for (int i = 0; i < count; i += stride)
            distances.Add(NearestDistance(i, source, target, closed));
    }

    private static double NearestDistance(int index, double[] source, double[] target, bool closed) =>
        NearestDistance(source[index * 3], source[index * 3 + 1], target, closed);

    private static double NearestDistance(double x, double y, double[] target, bool closed)
    {
        int segments = target.Length / 3 - (closed ? 0 : 1);
        double nearest = double.PositiveInfinity;
        for (int j = 0; j < segments; j++)
        {
            int p = j * 3, q = (j + 1) * 3 % target.Length;
            double dx = target[q] - target[p], dy = target[q + 1] - target[p + 1];
            double lengthSquared = dx * dx + dy * dy;
            double t = lengthSquared <= 1e-24 ? 0 : Math.Clamp(((x - target[p]) * dx + (y - target[p + 1]) * dy) / lengthSquared, 0, 1);
            double ex = x - target[p] - t * dx, ey = y - target[p + 1] - t * dy;
            nearest = Math.Min(nearest, ex * ex + ey * ey);
        }
        return Math.Sqrt(nearest);
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
