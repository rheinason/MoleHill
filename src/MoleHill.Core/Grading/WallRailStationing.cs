using MoleHill.Core.Geometry;

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

        if (closed)
            (first, second) = SeatSeamsOnLongestSegment(first, second);

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
            var stations = new List<Station> { Station.At(0, start.A, start.B, first, second) };
            for (int i = start.A + 1; i < end.A; i++)
            {
                double station = (aCum[i] - aCum[start.A]) / aSpan;
                stations.Add(new Station(station, i, -1, Vertex(first, i), Sample(second, bCum, bCum[start.B] + station * bSpan)));
            }
            for (int i = start.B + 1; i < end.B; i++)
            {
                double station = (bCum[i] - bCum[start.B]) / bSpan;
                stations.Add(new Station(station, -1, i, Sample(first, aCum, aCum[start.A] + station * aSpan), Vertex(second, i)));
            }
            if (ownsEnd)
                stations.Add(Station.At(1, end.A, end.B, first, second));
            stations.Sort((x, y) => x.Fraction.CompareTo(y.Fraction));

            // Merge stations closer than the tolerance on both rails into one, so the loft gets no
            // micrometre span where one rail's vertex lands just beside the other's. Each rail keeps its
            // own authored vertex. Two vertices of one rail merge only when they are the same point (a
            // doubled vertex); a vertical step stays a step.
            Station pending = stations[0];
            for (int i = 1; i < stations.Count; i++)
            {
                Station next = stations[i];
                if (CanMerge(pending, next, tolerance))
                {
                    pending = pending with
                    {
                        A = pending.A >= 0 ? pending.A : next.A,
                        B = pending.B >= 0 ? pending.B : next.B,
                        PointA = pending.A >= 0 ? pending.PointA : next.PointA,
                        PointB = pending.B >= 0 ? pending.PointB : next.PointB,
                    };
                    continue;
                }
                Emit(aResult, pending.PointA);
                Emit(bResult, pending.PointB);
                pending = next;
            }
            Emit(aResult, pending.PointA);
            Emit(bResult, pending.PointB);
        }
        return new Result(aResult.ToArray(), bResult.ToArray());
    }

    // A station: its fraction of the interval, the authored vertex it carries on each rail (or -1), and
    // its point on each rail - that vertex, or the plan-length sample.
    private readonly record struct Station(double Fraction, int A, int B, Point PointA, Point PointB)
    {
        public static Station At(double fraction, int a, int b, double[] first, double[] second) =>
            new(fraction, a, b, Vertex(first, a), Vertex(second, b));
    }

    private readonly record struct Point(double X, double Y, double Z);

    private static bool CanMerge(Station pending, Station next, double tolerance)
    {
        double a = Distance(pending.PointA, next.PointA), b = Distance(pending.PointB, next.PointB);
        if ((pending.A >= 0 && next.A >= 0 && a > DuplicateDistance) ||
            (pending.B >= 0 && next.B >= 0 && b > DuplicateDistance))
            return false;
        return a <= tolerance && b <= tolerance;
    }

    // Two authored vertices this close are one point drawn twice.
    private const double DuplicateDistance = 1e-9;

    private static void Emit(List<double> output, Point point)
    {
        output.Add(point.X);
        output.Add(point.Y);
        output.Add(point.Z);
    }

    private static Point Vertex(double[] points, int vertex)
    {
        int v = vertex * 3 % points.Length;
        return new Point(points[v], points[v + 1], points[v + 2]);
    }

    private static double Distance(Point p, Point q)
    {
        double dx = p.X - q.X, dy = p.Y - q.Y, dz = p.Z - q.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    // Closed rails: put the first rail's seam at the middle of its longest segment and the second rail's
    // at the nearest point to it, inserting both as vertices, so no bend straddles a seam. A run never wraps
    // a seam, so a fillet the seam cut in two became two partial bends on one ring and matched nothing on
    // the other; a seam on a corner vertex put the partner seam mid-fillet the same way.
    private static (double[] First, double[] Second) SeatSeamsOnLongestSegment(double[] first, double[] second)
    {
        int count = first.Length / 3, longestSegment = 0;
        double longest = -1;
        for (int i = 0; i < count; i++)
        {
            int next = (i + 1) % count;
            double length = Geometry2D.DistanceSquared(first[i * 3], first[i * 3 + 1], first[next * 3], first[next * 3 + 1]);
            if (length > longest)
            {
                longest = length;
                longestSegment = i;
            }
        }
        first = SplitAndRotate(first, longestSegment, 0.5);

        int partnerSegment = 0;
        double partnerT = 0, nearest = double.PositiveInfinity;
        int partnerCount = second.Length / 3;
        for (int i = 0; i < partnerCount; i++)
        {
            int p = i * 3, q = (i + 1) % partnerCount * 3;
            double t = Geometry2D.ParameterOnSegmentClamped(second[p], second[p + 1], second[q], second[q + 1], first[0], first[1]);
            double distance = Geometry2D.DistanceSquared(
                first[0], first[1], second[p] + t * (second[q] - second[p]), second[p + 1] + t * (second[q + 1] - second[p + 1]));
            if (distance < nearest)
            {
                nearest = distance;
                partnerSegment = i;
                partnerT = t;
            }
        }
        return (first, SplitAndRotate(second, partnerSegment, partnerT));
    }

    // The closed rail starting at parameter t on segment (segment, segment + 1); the start is inserted as a
    // vertex unless t lands on an end.
    private static double[] SplitAndRotate(double[] points, int segment, double t)
    {
        int count = points.Length / 3, next = (segment + 1) % count;
        if (t <= 1e-9)
            return Rotate(points, segment);
        if (t >= 1 - 1e-9)
            return Rotate(points, next);
        var result = new double[points.Length + 3];
        for (int axis = 0; axis < 3; axis++)
            result[axis] = points[segment * 3 + axis] + t * (points[next * 3 + axis] - points[segment * 3 + axis]);
        for (int i = 0; i < count; i++)
            Array.Copy(points, (next + i) % count * 3, result, (i + 1) * 3, 3);
        return result;
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
            List<(int A, int B)> matches = PairBends(a, first, b, second, closed, tolerance);
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

    private static List<(int A, int B)> PairBends(List<Bend> a, double[] first, List<Bend> b, double[] second, bool closed, double tolerance)
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
    // steps of a fillet. Runs do not wrap a closed rail's seam; SeatSeamsOnLongestSegment puts the seam
    // where no bend is.
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
            Point point = Sample(points, along, middle);
            Direction(points, (start + count - 1) % count, start, out double ix, out double iy);
            Direction(points, end, (end + 1) % count, out double ox, out double oy);

            // The arc proper runs from where the turn reaches 5% of the bend to where it reaches 95%, so a
            // stray small turn a wall width before or after the arc does not move its start or end anchor.
            int arcStart = start, arcEnd = end;
            double turned = 0;
            bool started = false;
            for (int i = start; i <= end; i++)
            {
                turned += turns[i];
                if (!started && Math.Abs(turned) >= ArcTrim * Math.Abs(total))
                {
                    arcStart = i;
                    started = true;
                }
                if (Math.Abs(turned) >= (1 - ArcTrim) * Math.Abs(total))
                {
                    arcEnd = i;
                    break;
                }
            }
            bends.Add(new Bend(start, end, along[arcStart], along[arcEnd], middle, point.X, point.Y, ix, iy, ox, oy));
        }
        return bends;
    }

    private const double ArcTrim = 0.05;

    // Splits each unmatched bend at a gap clearly wider than its others: two bends merged across a short
    // leg. An evenly stepped arc has no such gap and stays whole; peeling it a vertex at a time cost
    // O(n^2) and could whittle a correct fillet away. Returns whether any bend split.
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
            double widest = -1, secondWidest = 0;
            for (int v = bend.Start + 1; v <= bend.End; v++)
            {
                if (Math.Abs(turns[v]) < MinimumTurn)
                    continue;
                double gap = along[v] - along[previous];
                if (gap > widest)
                {
                    secondWidest = Math.Max(secondWidest, widest);
                    widest = gap;
                    cutAfter = previous;
                    cutBefore = v;
                }
                else
                {
                    secondWidest = Math.Max(secondWidest, gap);
                }
                previous = v;
            }
            if (widest <= SplitGapRatio * secondWidest)
                continue;
            int run = runs.IndexOf((bend.Start, bend.End));
            runs[run] = (bend.Start, cutAfter);
            runs.Insert(run + 1, (cutBefore, bend.End));
            split = true;
        }
        return split;
    }

    private const double SplitGapRatio = 1.5;

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
                Emit(output, Sample(points, along, positions[order[next++]]));
            }
            while (next < order.Length && positions[order[next]] <= along[i] + snap)
                indices[order[next++]] = output.Count / 3;
            output.Add(points[i * 3]); output.Add(points[i * 3 + 1]); output.Add(points[i * 3 + 2]);
        }
        while (next < order.Length)
        {
            // Past the last vertex: only on a closed rail, on its closing segment.
            indices[order[next]] = output.Count / 3;
            Emit(output, Sample(points, along, positions[order[next++]]));
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
            double t = Geometry2D.ParameterOnSegmentClamped(target[p], target[p + 1], target[q], target[q + 1], x, y);
            nearest = Math.Min(nearest, Geometry2D.DistanceSquared(
                x, y, target[p] + t * (target[q] - target[p]), target[p + 1] + t * (target[q + 1] - target[p + 1])));
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

    // The point at plan position along: a vertex when it lands on one, else interpolated on its segment.
    private static Point Sample(double[] points, double[] lengths, double along)
    {
        int found = Array.BinarySearch(lengths, along);
        if (found >= 0)
            return Vertex(points, found);
        int segment = Math.Clamp(~found - 1, 0, lengths.Length - 2);
        int p = segment * 3, q = (segment + 1) * 3 % points.Length;
        double t = (along - lengths[segment]) / (lengths[segment + 1] - lengths[segment]);
        return new Point(
            points[p] + t * (points[q] - points[p]),
            points[p + 1] + t * (points[q + 1] - points[p + 1]),
            points[p + 2] + t * (points[q + 2] - points[p + 2]));
    }
}
