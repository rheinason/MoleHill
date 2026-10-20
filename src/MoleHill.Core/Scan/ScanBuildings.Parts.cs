namespace MoleHill.Core.Scan;

/// <summary>Composite roofs: the plan split along the facades into parts, each under its own simple roof.</summary>
public static partial class ScanBuildings
{
    private sealed record PartFit(RoofPlane[] Planes, double Cost, double Within);
    /// <summary>A box in the facade frame (u along, v across) with the samples over it and its roof. Parts
    /// are compared by reference: a tree of them is searched, split and joined again.</summary>
    private sealed class Part(double u0, double u1, double v0, double v1, int[] samples, PartFit fit)
    {
        public double U0 { get; } = u0;
        public double U1 { get; } = u1;
        public double V0 { get; } = v0;
        public double V1 { get; } = v1;
        public int[] Samples { get; } = samples;
        public PartFit Fit { get; } = fit;
        public (bool AlongU, double At, Part Low, Part High, double Gain)? Split { get; set; }
        /// <summary>Set once the split is taken: the two halves and the line between them.</summary>
        public (Part Low, Part High, Cut Line)? Children { get; set; }
        public double[]? Envelope { get; set; }
    }
    private sealed record Overlap(double[][] Boxes, List<Cut> Edges, double Cost);

    /// <summary>A main house with a lower annex, porch or second wing is one roof per part, not one roof
    /// stretched over all of them. Splits run parallel to the facades, which is where such parts meet,
    /// and are taken greedily while one pays for itself in roof error; each part keeps the cheapest lower
    /// envelope of up to four measured planes (or a local flat or pitched plane) that explains it. Parts
    /// then overlap where that explains the roof better (a wing's roof reaching into the main house, the
    /// higher roof winning), and halves the overlaps made redundant are joined again.
    /// Returns composites from most parts to two: the face budget is only known once a shell is built,
    /// and the best composite with one part fewer is the next to try.</summary>
    private static List<RoofModel> CompositeRoofs(List<RoofSample> samples, List<PlaneRegion> regions, List<List<P2>> footprints,
        double angle, double baseZ, Options options)
    {
        var composites = new List<RoofModel>();
        double cos = Math.Cos(angle), sin = Math.Sin(angle), tolerance = options.RoofTolerance;
        double[] u = samples.Select(s => s.X * cos + s.Y * sin).ToArray(), v = samples.Select(s => -s.X * sin + s.Y * cos).ToArray();
        int[] byU = Enumerable.Range(0, samples.Count).OrderBy(i => u[i]).ToArray(), byV = Enumerable.Range(0, samples.Count).OrderBy(i => v[i]).ToArray();
        double[] sortedU = byU.Select(i => u[i]).ToArray(), sortedV = byV.Select(i => v[i]).ToArray();
        var member = Enumerable.Repeat(-1, samples.Count).ToArray();
        for (int region = 0; region < regions.Count; region++)
            foreach (int i in regions[region].Samples)
                member[i] = region;
        P2[] corners = footprints.SelectMany(loop => loop).Select(p => new P2(p.X * cos + p.Y * sin, -p.X * sin + p.Y * cos)).ToArray();
        if (corners.Length < 3 || !double.IsFinite(baseZ))
            return composites;
        // Plan points over the footprint: a part's roof planes extend over all of it, not only its samples.
        double[][] containment = footprints.Select(loop => loop.SelectMany(p => new[] { p.X, p.Y }).ToArray()).ToArray();
        var interior = new List<(double U, double V, double X, double Y)>();
        double spacing = Math.Max(options.FootprintTolerance, Math.Sqrt(options.MinimumArea) * .05);
        for (double pu = corners.Min(p => p.X) + spacing * .5; pu < corners.Max(p => p.X); pu += spacing)
            for (double pv = corners.Min(p => p.Y) + spacing * .5; pv < corners.Max(p => p.Y); pv += spacing)
            {
                P2 point = new(pu * cos - pv * sin, pu * sin + pv * cos);
                if (InsideLoops(point, containment))
                    interior.Add((pu, pv, point.X, point.Y));
            }
        // And along the outline, where a part's edge meets the footprint between its corners.
        foreach (List<P2> loop in footprints)
            for (int i = 0; i < loop.Count; i++)
            {
                P2 a = loop[i], b = loop[(i + 1) % loop.Count];
                int steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(Square(b.X - a.X) + Square(b.Y - a.Y)) / spacing * 2));
                for (int k = 0; k < steps; k++)
                {
                    double x = a.X + (b.X - a.X) * k / steps, y = a.Y + (b.Y - a.Y) * k / steps;
                    interior.Add((x * cos + y * sin, -x * sin + y * cos, x, y));
                }
            }
        double floor = baseZ + options.MinimumHeight * .25 + tolerance;
        double totalWeight = samples.Sum(s => s.Weight);
        // A part narrower than the roof detail is a detail; a sliver of roof data is no part at all.
        double minimumWidth = Math.Max(options.MinimumRoofDetail, options.FootprintTolerance * 2);
        double minimumWeight = options.MinimumArea * .1, step = Math.Max(options.FootprintTolerance, minimumWidth * .1);
        // A part must buy back a tenth of the tolerance over the whole roof, so measurement noise never splits.
        double penalty = totalWeight * Square(tolerance * .1);
        double minimumU = corners.Min(p => p.X), maximumU = corners.Max(p => p.X), minimumV = corners.Min(p => p.Y), maximumV = corners.Max(p => p.Y);
        int[] all = Enumerable.Range(0, samples.Count).ToArray();
        if (FitPart(all, minimumU, maximumU, minimumV, maximumV) is not PartFit whole)
            return composites;
        var root = new Part(minimumU, maximumU, minimumV, maximumV, all, whole);
        var leaves = new List<Part> { root };
        FindSplit(root);
        while (leaves.Count < 8)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            Part? best = leaves.Where(part => part.Split is { } split && split.Gain > penalty &&
                    leaves.Where(other => other != part).SelectMany(other => other.Fit.Planes)
                        .Concat(split.Low.Fit.Planes).Concat(split.High.Fit.Planes).Distinct().Count() <= options.MaximumPlanes)
                .MaxBy(part => part.Split!.Value.Gain);
            if (best == null)
                break;
            var (alongU, at, low, high, _) = best.Split!.Value;
            // The line u = at (or v = at) in local plan coordinates.
            best.Children = (low, high, alongU ? new Cut(cos, sin, -at) : new Cut(-sin, cos, -at));
            leaves.Remove(best);
            leaves.Add(low); leaves.Add(high);
            FindSplit(low); FindSplit(high);
        }
        if (leaves.Count < 2)
            return composites;
        Overlap overlap = Overlapping(leaves);
        bool simplified = false;
        while (leaves.Count > 2)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            // Join the two halves whose joining costs least, once overlaps have had their say.
            (Part Node, Overlap Overlap, List<Part> Leaves)? cheapest = null;
            foreach (Part node in Nodes(root, leaves).Where(node => node.Children is { } children && leaves.Contains(children.Low) && leaves.Contains(children.High)))
            {
                var joined = leaves.Where(part => part != node.Children!.Value.Low && part != node.Children.Value.High).Append(node).ToList();
                Overlap next = Overlapping(joined);
                if (cheapest == null || next.Cost < cheapest.Value.Overlap.Cost)
                    cheapest = (node, next, joined);
            }
            if (cheapest == null)
                break;
            if (!simplified && cheapest.Value.Overlap.Cost - overlap.Cost > penalty)
            {
                composites.Add(Model(leaves, overlap));
                simplified = true;
            }
            leaves = cheapest.Value.Leaves;
            overlap = cheapest.Value.Overlap;
            if (simplified)
                composites.Add(Model(leaves, overlap));
        }
        if (!simplified)
            composites.Add(Model(leaves, overlap));
        return composites;

        // The tree down to the given leaves: joined halves are no longer visited.
        IEnumerable<Part> Nodes(Part node, List<Part> stop) => node.Children is { } children && !stop.Contains(node)
            ? new[] { node }.Concat(Nodes(children.Low, stop)).Concat(Nodes(children.High, stop)) : new[] { node };

        double[] Envelope(Part part) => part.Envelope ??= samples.Select(sample =>
            part.Fit.Planes.Min(plane => plane.Height(sample.X, sample.Y))).ToArray();

        Overlap Overlapping(List<Part> parts)
        {
            // Wings overlap: a part's box may reach into its neighbour, where the higher roof wins. An L
            // of two hipped wings then meets in valleys, not in a vertical wall standing on an eave.
            var boxes = parts.Select(part => new[] { part.U0, part.U1, part.V0, part.V1 }).ToArray();
            var current = new double[samples.Count];
            for (int part = 0; part < parts.Count; part++)
                foreach (int i in parts[part].Samples)
                    current[i] = Envelope(parts[part])[i];
            var edges = new List<Cut>();
            RoofPlane[] planes = parts.SelectMany(part => part.Fit.Planes).Distinct().ToArray();
            var lines = new List<(bool AlongU, double At)>();
            for (int i = 0; i < planes.Length; i++)
                for (int j = i + 1; j < planes.Length; j++)
                {
                    // The planes' difference A u + B v + C is zero on a line of constant u when B vanishes.
                    double dx = planes[i].X - planes[j].X, dy = planes[i].Y - planes[j].Y, dz = planes[i].Z - planes[j].Z;
                    double a = dx * cos + dy * sin, b = -dx * sin + dy * cos;
                    if (Math.Abs(b) <= Math.Abs(a) * 1e-3)
                        lines.Add((true, -dz / a));
                    else if (Math.Abs(a) <= Math.Abs(b) * 1e-3)
                        lines.Add((false, -dz / b));
                }
            for (int round = 0; round < parts.Count * 4; round++)
            {
                // An overlap adds lines and faces as a split does, and must pay for itself as one.
                double bestGain = penalty, bestEdge = 0; int bestPart = -1, bestSide = -1;
                for (int part = 0; part < parts.Count; part++)
                {
                    double[] envelope = Envelope(parts[part]);
                    for (int side = 0; side < 4; side++)
                    {
                        // Sides 0..3 move U0 down, U1 up, V0 down, V1 up.
                        bool alongU = side < 2, outward = side % 2 == 1;
                        double[] axial = alongU ? u : v, lateral = alongU ? v : u;
                        double edge = boxes[part][side], from = boxes[part][alongU ? 2 : 0], to = boxes[part][alongU ? 3 : 1];
                        // Walk the samples in order away from the edge, keeping those level with the part.
                        int[] sorted = alongU ? byU : byV;
                        int first = Array.BinarySearch(alongU ? sortedU : sortedV, edge);
                        first = first < 0 ? ~first : first;
                        var reached = new List<int>();
                        for (int k = outward ? first : first - 1; k >= 0 && k < sorted.Length; k += outward ? 1 : -1)
                        {
                            int i = sorted[k];
                            if ((outward ? axial[i] > edge : axial[i] < edge) && lateral[i] >= from && lateral[i] <= to)
                                reached.Add(i);
                        }
                        double gain = 0, sideGain = double.NegativeInfinity, sideEdge = 0, previous = edge;
                        for (int k = 0; k < reached.Count;)
                        {
                            // Samples level with one another are claimed together: the edge can only pass between them.
                            double row = axial[reached[k]];
                            for (; k < reached.Count && Math.Abs(axial[reached[k]] - row) <= options.FootprintTolerance * 1e-3; k++)
                            {
                                int i = reached[k];
                                double higher = Math.Max(current[i], envelope[i]);
                                gain += samples[i].Weight * (Square(Math.Min(Math.Abs(samples[i].Z - current[i]), tolerance * 3)) -
                                    Square(Math.Min(Math.Abs(samples[i].Z - higher), tolerance * 3)));
                            }
                            // Reach on while it costs nothing: where the overlap's roof is the lower one it changes no
                            // height, and stopping short of where the two roofs cross would leave a step between them.
                            if (gain >= sideGain - 1e-12 * totalWeight)
                            {
                                sideGain = Math.Max(gain, sideGain);
                                // The edge goes in the gap before the next row, which the overlap does not claim, or on a
                                // ridge or valley running along it within a row of there: that is where the two roofs
                                // cross, and a row straddling the ridge may go either way.
                                sideEdge = k < reached.Count ? Crossing(alongU, previous, row, axial[reached[k]]) : row;
                            }
                            previous = row;
                        }
                        if (sideGain > bestGain)
                        {
                            bestGain = sideGain; bestPart = part; bestSide = side; bestEdge = sideEdge;
                        }
                    }
                }
                if (bestPart < 0)
                    break;
                boxes[bestPart][bestSide] = bestEdge;
                double[] box = boxes[bestPart], reach = Envelope(parts[bestPart]);
                for (int i = 0; i < samples.Count; i++)
                    if (u[i] >= box[0] && u[i] <= box[1] && v[i] >= box[2] && v[i] <= box[3])
                        current[i] = Math.Max(current[i], reach[i]);
                edges.Add(bestSide < 2 ? new Cut(cos, sin, -bestEdge) : new Cut(-sin, cos, -bestEdge));
            }
            double cost = 0;
            for (int i = 0; i < samples.Count; i++)
                cost += samples[i].Weight * Square(Math.Min(Math.Abs(samples[i].Z - current[i]), tolerance * 3));
            return new Overlap(boxes, edges, cost);

            double Crossing(bool alongU, double previous, double last, double next)
            {
                double low = Math.Min(previous, next), high = Math.Max(previous, next), middle = (last + next) * .5;
                double[] inside = lines.Where(line => line.AlongU == alongU && line.At > low && line.At < high).Select(line => line.At).ToArray();
                return inside.Length == 0 ? middle : inside.MinBy(at => Math.Abs(at - middle));
            }
        }

        RoofModel Model(List<Part> chosen, Overlap overlap)
        {
            Part[] parts = chosen.ToArray();
            double[][] boxes = overlap.Boxes;
            var planes = new List<RoofPlane>();
            int[][] owned = parts.Select(part => part.Fit.Planes.Select(plane =>
            {
                int index = planes.IndexOf(plane);
                if (index < 0) { index = planes.Count; planes.Add(plane); }
                return index;
            }).ToArray()).ToArray();
            List<Cut> cuts = Nodes(root, chosen).Where(node => !chosen.Contains(node)).Select(node => node.Children!.Value.Line).Concat(overlap.Edges).ToList();
            // Planes meet within a part, or across parts that overlap or touch: where two roofs cross on the edge
            // between them the step there turns over, and must be cut where it does. Apart, they never meet.
            var meets = new bool[planes.Count, planes.Count];
            double touch = options.FootprintTolerance * 1e-3;
            for (int p = 0; p < parts.Length; p++)
                for (int q = 0; q < parts.Length; q++)
                    if (Math.Min(boxes[p][1], boxes[q][1]) >= Math.Max(boxes[p][0], boxes[q][0]) - touch &&
                        Math.Min(boxes[p][3], boxes[q][3]) >= Math.Max(boxes[p][2], boxes[q][2]) - touch)
                        foreach (int i in owned[p])
                            foreach (int j in owned[q])
                                meets[i, j] = meets[j, i] = true;
            return new RoofModel
            {
                Kind = ScanRoofKind.Composite, Planes = planes, ExtraCuts = cuts, Meets = (i, j) => meets[i, j],
                Diagnostic = FormattableString.Invariant($"Composite of {parts.Length} parts with {string.Join(", ", parts.Select(p => p.Fit.Planes.Length))} roof planes{(overlap.Edges.Count > 0 ? $" and {overlap.Edges.Count} overlap(s)" : "")}."),
                Select = (x, y) =>
                {
                    double pu = x * cos + y * sin, pv = -x * sin + y * cos;
                    int home = 0; double nearest = double.PositiveInfinity;
                    for (int i = 0; i < parts.Length; i++)
                    {
                        // Parts tile the footprint's box; outside it the nearest part continues.
                        double distance = Math.Max(Math.Max(parts[i].U0 - pu, pu - parts[i].U1), 0) + Math.Max(Math.Max(parts[i].V0 - pv, pv - parts[i].V1), 0);
                        if (distance < nearest) { nearest = distance; home = i; }
                    }
                    int selected = -1; double highest = double.NegativeInfinity;
                    for (int part = 0; part < parts.Length; part++)
                    {
                        if (part != home && !(pu >= boxes[part][0] && pu <= boxes[part][1] && pv >= boxes[part][2] && pv <= boxes[part][3]))
                            continue;
                        int lowest = owned[part][0];
                        foreach (int plane in owned[part])
                            if (planes[plane].Height(x, y) < planes[lowest].Height(x, y))
                                lowest = plane;
                        if (planes[lowest].Height(x, y) > highest)
                        {
                            highest = planes[lowest].Height(x, y); selected = lowest;
                        }
                    }
                    return selected;
                },
            };
        }

        void FindSplit(Part part)
        {
            part.Split = null;
            if (part.Fit.Within >= .95)
                return;
            // Ranking every gap between the samples by the parent's own candidate roofs is a sum over each side,
            // so prefix sums price all of them at once; only the most promising few are fitted properly.
            int[] members = part.Samples;
            List<RoofPlane[]> roofs = Candidates(members);
            var errors = new double[roofs.Count][];
            for (int c = 0; c < roofs.Count; c++)
            {
                errors[c] = new double[members.Length];
                for (int k = 0; k < members.Length; k++)
                {
                    RoofSample sample = samples[members[k]];
                    double height = double.PositiveInfinity;
                    foreach (RoofPlane plane in roofs[c])
                        height = Math.Min(height, plane.Height(sample.X, sample.Y));
                    errors[c][k] = sample.Weight * (Square(Math.Min(Math.Abs(sample.Z - height), tolerance * 3)) + roofs[c].Length * Square(tolerance * .05));
                }
            }
            foreach (bool alongU in new[] { true, false })
            {
                double low = alongU ? part.U0 : part.V0, high = alongU ? part.U1 : part.V1;
                double[] coordinate = alongU ? u : v;
                int[] order = Enumerable.Range(0, members.Length).OrderBy(k => coordinate[members[k]]).ToArray();
                var below = new double[roofs.Count];
                double[] total = errors.Select(row => row.Sum()).ToArray();
                double weightBelow = 0, weightTotal = members.Sum(i => samples[i].Weight);
                var ranked = new List<(double At, double Cost)>();
                for (int k = 0; k + 1 < order.Length; k++)
                {
                    for (int c = 0; c < roofs.Count; c++)
                        below[c] += errors[c][order[k]];
                    weightBelow += samples[members[order[k]]].Weight;
                    double here = coordinate[members[order[k]]], next = coordinate[members[order[k + 1]]];
                    double at = (here + next) * .5;
                    if (next - here <= options.FootprintTolerance * 1e-3 || at < low + minimumWidth || at > high - minimumWidth ||
                        weightBelow < minimumWeight || weightTotal - weightBelow < minimumWeight)
                        continue;
                    double first = double.PositiveInfinity, second = double.PositiveInfinity;
                    for (int c = 0; c < roofs.Count; c++)
                    {
                        first = Math.Min(first, below[c]); second = Math.Min(second, total[c] - below[c]);
                    }
                    ranked.Add((at, first + second));
                }
                var tried = new List<double>();
                foreach (var (at, _) in ranked.OrderBy(position => position.Cost))
                {
                    if (tried.Count == 2)
                        break;
                    if (tried.Any(other => Math.Abs(other - at) < step))
                        continue;
                    tried.Add(at);
                    Try(alongU, at);
                }
            }

            void Try(bool alongU, double at)
            {
                double low = alongU ? part.U0 : part.V0, high = alongU ? part.U1 : part.V1;
                if (at < low + minimumWidth || at > high - minimumWidth)
                    return;
                double[] coordinate = alongU ? u : v;
                int[] below = part.Samples.Where(i => coordinate[i] < at).ToArray(), above = part.Samples.Where(i => coordinate[i] >= at).ToArray();
                if (below.Sum(i => samples[i].Weight) < minimumWeight || above.Sum(i => samples[i].Weight) < minimumWeight)
                    return;
                PartFit? first = alongU ? FitPart(below, part.U0, at, part.V0, part.V1) : FitPart(below, part.U0, part.U1, part.V0, at);
                PartFit? second = alongU ? FitPart(above, at, part.U1, part.V0, part.V1) : FitPart(above, part.U0, part.U1, at, part.V1);
                if (first == null || second == null)
                    return;
                double gain = part.Fit.Cost - first.Cost - second.Cost;
                if (part.Split is { } current && gain <= current.Gain)
                    return;
                part.Split = (alongU, at,
                    alongU ? new Part(part.U0, at, part.V0, part.V1, below, first) : new Part(part.U0, part.U1, part.V0, at, below, first),
                    alongU ? new Part(at, part.U1, part.V0, part.V1, above, second) : new Part(part.U0, part.U1, at, part.V1, above, second), gain);
            }
        }

        // A local flat and pitched plane, and lower envelopes of up to four of the measured planes the part holds.
        List<RoofPlane[]> Candidates(int[] indices)
        {
            double weight = indices.Sum(i => samples[i].Weight);
            var support = new double[regions.Count];
            foreach (int i in indices)
                if (member[i] >= 0)
                    support[member[i]] += samples[i].Weight;
            RoofPlane[] measured = Enumerable.Range(0, regions.Count).Where(r => support[r] >= weight * .05)
                .OrderByDescending(r => support[r]).Take(5).Select(r => regions[r].Plane).ToArray();
            var candidates = new List<RoofPlane[]>();
            double accumulated = 0, median = 0;
            foreach (int i in indices.OrderBy(i => samples[i].Z))
            {
                accumulated += samples[i].Weight; median = samples[i].Z;
                if (accumulated >= weight * .5) break;
            }
            candidates.Add(new[] { new RoofPlane(0, 0, median) });
            if (FitPlane(samples, indices) is RoofPlane local && Math.Sqrt(local.X * local.X + local.Y * local.Y) <= 4)
                candidates.Add(new[] { local });
            for (int a = 0; a < measured.Length; a++)
            {
                candidates.Add(new[] { measured[a] });
                for (int b = a + 1; b < measured.Length; b++)
                {
                    candidates.Add(new[] { measured[a], measured[b] });
                    for (int c = b + 1; c < measured.Length; c++)
                    {
                        candidates.Add(new[] { measured[a], measured[b], measured[c] });
                        for (int d = c + 1; d < measured.Length; d++)
                            candidates.Add(new[] { measured[a], measured[b], measured[c], measured[d] });
                    }
                }
            }
            return candidates;
        }

        PartFit? FitPart(int[] indices, double u0, double u1, double v0, double v1)
        {
            var area = interior.Where(p => p.U >= u0 && p.U <= u1 && p.V >= v0 && p.V <= v1).ToArray();
            double weight = indices.Sum(i => samples[i].Weight);
            PartFit? best = null;
            foreach (RoofPlane[] planes in Candidates(indices))
            {
                double cost = weight * planes.Length * Square(tolerance * .05), within = 0;
                foreach (int i in indices)
                {
                    double height = double.PositiveInfinity;
                    foreach (RoofPlane plane in planes)
                        height = Math.Min(height, plane.Height(samples[i].X, samples[i].Y));
                    double error = Math.Abs(samples[i].Z - height);
                    cost += samples[i].Weight * Square(Math.Min(error, tolerance * 3));
                    if (error <= tolerance)
                        within += samples[i].Weight;
                }
                // A roof that dips to the ground somewhere in its part is no roof for it.
                if ((best == null || cost < best.Cost) && !area.Any(p => planes.Min(plane => plane.Height(p.X, p.Y)) < floor))
                    best = new PartFit(planes, cost, weight > 0 ? within / weight : 0);
            }
            return best;
        }
    }
}
