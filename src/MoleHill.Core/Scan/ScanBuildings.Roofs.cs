namespace MoleHill.Core.Scan;

/// <summary>Measured-plane templates, complexity-aware selection and the general arrangement fallback.</summary>
public static partial class ScanBuildings
{
    private sealed class RoofModel
    {
        public required ScanRoofKind Kind { get; init; }
        public required IReadOnlyList<RoofPlane> Planes { get; init; }
        public required Func<double, double, int> Select { get; init; }
        public double Rms { get; set; }
        public double WithinShare { get; set; }
        public double Score { get; set; }
        public string Diagnostic { get; set; } = "";
        public double CoveredShare { get; set; } = 1;
        public IReadOnlyList<Cut> ExtraCuts { get; set; } = Array.Empty<Cut>();
        /// <summary>The plane pairs whose meeting can bound a roof cell; null means every pair.</summary>
        public Func<int, int, bool>? Meets { get; init; }
        public bool Approximate { get; init; }
        public bool Bounded { get; set; }
        public double Height(double x, double y) => Planes[Select(x, y)].Height(x, y);
    }

    private static List<RoofModel> ChooseRoofs(List<RoofSample> samples, List<PlaneRegion> regions, List<List<P2>> footprints,
        double facadeAngle, double baseZ, Options options)
    {
        var choices = new List<RoofModel>();
        int[] all = Enumerable.Range(0, samples.Count).ToArray();
        if (FitPlane(samples, all) is RoofPlane plane)
        {
            AddMinimum(Math.Sqrt(plane.X * plane.X + plane.Y * plane.Y) <= .03 ? ScanRoofKind.Flat : ScanRoofKind.Shed, new[] { plane });
            double mean = samples.Sum(s => s.Weight * s.Z) / samples.Sum(s => s.Weight);
            AddMinimum(ScanRoofKind.Flat, new[] { new RoofPlane(0, 0, mean) });
        }
        RoofPlane[] planes = regions.Select(region => region.Plane).ToArray();
        var pairs = new List<(int A, int B)>();
        for (int i = 0; i < planes.Length; i++)
            for (int j = i + 1; j < planes.Length; j++)
            {
                double magnitude = Math.Sqrt((Square(planes[i].X) + Square(planes[i].Y)) * (Square(planes[j].X) + Square(planes[j].Y)));
                if (magnitude < 0.01 || (planes[i].X * planes[j].X + planes[i].Y * planes[j].Y) / magnitude > -0.85)
                    continue;
                pairs.Add((i, j));
                AddMinimum(ScanRoofKind.Gable, new[] { planes[i], planes[j] });
            }
        // A hip is the lower envelope of measured roof facets. Half hips have a raised triangular
        // end above a gable wall: its footprint-edge heights rise above the surrounding eaves.
        foreach ((int a, int b) in pairs)
            for (int c = 0; c < planes.Length; c++)
            {
                if (c == a || c == b || !Independent(planes[a], planes[c]))
                    continue;
                AddMinimum(ScanRoofKind.HalfHip, new[] { planes[a], planes[b], planes[c] });
                for (int d = c + 1; d < planes.Length; d++)
                {
                    if (d == a || d == b || !Independent(planes[a], planes[d]))
                        continue;
                    RoofPlane[] hip = { planes[a], planes[b], planes[c], planes[d] };
                    RoofModel model = Minimum(ScanRoofKind.Hip, hip);
                    var edgeHeights = new List<double>();
                    foreach (List<P2> loop in footprints)
                        for (int i = 0; i < loop.Count; i++)
                        {
                            P2 p = loop[i], q = loop[(i + 1) % loop.Count];
                            edgeHeights.Add(model.Height(p.X, p.Y));
                            edgeHeights.Add(model.Height((p.X + q.X) * 0.5, (p.Y + q.Y) * 0.5));
                        }
                    ScanRoofKind kind = edgeHeights.Max() - edgeHeights.Min() > options.RoofTolerance * 3 ? ScanRoofKind.HalfHip : ScanRoofKind.Hip;
                    AddMinimum(kind, hip);
                }
            }
        for (int i = 0; i < pairs.Count; i++)
            for (int j = i + 1; j < pairs.Count; j++)
            {
                (int a, int b) = pairs[i]; (int c, int d) = pairs[j];
                if (a == c || a == d || b == c || b == d || !Independent(planes[a], planes[c]))
                    continue;
                RoofPlane[] cross = { planes[a], planes[b], planes[c], planes[d] };
                var model = new RoofModel
                {
                    Kind = ScanRoofKind.CrossGable, Planes = cross,
                    Select = (x, y) =>
                    {
                        int first = cross[0].Height(x, y) < cross[1].Height(x, y) ? 0 : 1;
                        int second = cross[2].Height(x, y) < cross[3].Height(x, y) ? 2 : 3;
                        return cross[first].Height(x, y) >= cross[second].Height(x, y) ? first : second;
                    },
                };
                Evaluate(model);
                choices.Add(model);
            }
        List<RoofModel> models = choices.Where(Accept).OrderBy(model => model.Score).ThenBy(model => model.Kind).ToList();

        // Convex roof arrangements can use more than four facets without becoming a generic box.
        RoofModel envelope = Minimum(ScanRoofKind.PlaneArrangement, planes);
        Evaluate(envelope);
        if (Accept(envelope))
            models.Add(envelope);
        // A continuous non-convex roof is a lattice of its planes: the upper envelope of lower
        // envelopes. Build the observed ordering cells rather than forcing every roof into one
        // convex lower envelope (which would turn valleys and flat dormer wings into boxes).
        var groups = new HashSet<int>();
        for (int i = 0; i < regions.Count; i++)
            foreach (int index in regions[i].Samples)
            {
                RoofSample sample = samples[index];
                double own = planes[i].Height(sample.X, sample.Y);
                int group = 1 << i;
                for (int j = 0; j < planes.Length; j++)
                    if (planes[j].Height(sample.X, sample.Y) >= own - options.RoofTolerance * 0.05)
                        group |= 1 << j;
                groups.Add(group);
            }
        int[] terms = groups.OrderBy(group => group).ToArray();
        var lattice = new RoofModel
        {
            Kind = ScanRoofKind.PlaneArrangement, Planes = planes,
            Select = (x, y) =>
            {
                int selected = 0; double highest = double.NegativeInfinity;
                foreach (int term in terms)
                {
                    int lower = -1; double lowest = double.PositiveInfinity;
                    for (int i = 0; i < planes.Length; i++)
                        if ((term & (1 << i)) != 0 && planes[i].Height(x, y) < lowest)
                        {
                            lowest = planes[i].Height(x, y); lower = i;
                        }
                    if (lowest > highest)
                    {
                        highest = lowest; selected = lower;
                    }
                }
                return selected;
            },
        };
        Evaluate(lattice);
        if (Accept(lattice))
            models.Add(lattice);
        // Parts under simple roofs come before the spatial arrangement: fewer faces for the same measured fit.
        List<RoofModel> composites = CompositeRoofs(samples, regions, footprints, facadeAngle, baseZ, options);
        foreach (RoofModel composite in composites)
        {
            Evaluate(composite);
            models.Add(composite);
        }
        // Non-convex arrangements assign cells to the spatially nearest measured plane patch. Every
        // pairwise plane intersection splits the shell; genuine roof steps receive vertical faces.
        P2[][] representatives = regions.Select(region => region.Samples
            .Where((_, i) => i % Math.Max(1, region.Samples.Count / 64) == 0)
            .Select(i => new P2(samples[i].X, samples[i].Y)).ToArray()).ToArray();
        P2[][] boundaries = regions.Select(region =>
        {
            var members = region.Samples.ToHashSet();
            return region.Samples.Where(i => samples[i].Neighbours.Any(neighbour => neighbour < 0 || !members.Contains(neighbour)))
                .Select(i => new P2(samples[i].X, samples[i].Y)).ToArray();
        }).ToArray();
        var general = new RoofModel
        {
            Kind = ScanRoofKind.PlaneArrangement, Planes = planes,
            ExtraCuts = SpatialCuts(boundaries, options),
            Select = (x, y) =>
            {
                double best = double.PositiveInfinity; int selected = 0;
                for (int i = 0; i < representatives.Length; i++)
                    foreach (P2 p in representatives[i])
                    {
                        double distance = Square(x - p.X) + Square(y - p.Y);
                        if (distance < best)
                        {
                            best = distance;
                            selected = i;
                        }
                    }
                return selected;
            },
        };
        Evaluate(general);
        if (Accept(general))
            models.Add(general);

        if (options.MinimumRoofDetail > 0 || options.MaximumRoofFaces > 0)
            foreach (RoofModel simple in choices.Where(model => model.Planes.Count <= 4)
                .GroupBy(model => model.Kind).SelectMany(group => group.OrderBy(model => model.Rms).ThenBy(model => model.Planes.Count).Take(2)))
                models.Add(new RoofModel
                {
                    Kind = simple.Kind, Planes = simple.Planes, Select = simple.Select, Approximate = true,
                    Diagnostic = $"Simplified {simple.Kind} fallback: small roof details/complex arrangements omitted; fit is approximate.",
                });
        foreach (RoofModel composite in composites)
            models.Add(new RoofModel
            {
                Kind = composite.Kind, Planes = composite.Planes, Select = composite.Select, ExtraCuts = composite.ExtraCuts, Meets = composite.Meets, Approximate = true,
                Diagnostic = composite.Diagnostic + " Fit is approximate.",
            });
        RoofModel fallback = ExtrusionFallback(samples);
        Evaluate(fallback);
        RoofModel closest = choices.Concat(new[] { envelope, lattice, general }).OrderBy(model => model.Score).First();
        fallback.Diagnostic = FormattableString.Invariant($"Best attempted {closest.Kind}: {closest.Rms:F3} RMS, {closest.WithinShare:P1} within tolerance; {planes.Length} measured planes.");
        models.Add(fallback);
        return models;

        void AddMinimum(ScanRoofKind kind, RoofPlane[] candidate)
        {
            var model = Minimum(kind, candidate);
            Evaluate(model);
            choices.Add(model);
        }
        bool Accept(RoofModel model) => model.WithinShare >= 0.9;
        void Evaluate(RoofModel model)
        {
            var residuals = samples.Select(sample => (Error: Math.Abs(sample.Z - model.Height(sample.X, sample.Y)), sample.Weight)).OrderBy(pair => pair.Error).ToArray();
            double totalWeight = residuals.Sum(pair => pair.Weight), sum = 0, within = 0, robust = 0;
            foreach (var pair in residuals)
            {
                sum += pair.Weight * Square(pair.Error);
                if (pair.Error <= options.RoofTolerance)
                    within += pair.Weight;
                robust += pair.Weight * Square(Math.Min(pair.Error, options.RoofTolerance * 3));
            }
            model.Rms = Math.Sqrt(sum / totalWeight);
            model.WithinShare = within / totalWeight;
            model.Score = Math.Sqrt(robust / totalWeight) + model.Planes.Count * options.RoofTolerance * 0.025;
        }
    }

    private static RoofModel Minimum(ScanRoofKind kind, IReadOnlyList<RoofPlane> planes) => new()
    {
        Kind = kind, Planes = planes,
        Select = (x, y) =>
        {
            int selected = 0;
            for (int i = 1; i < planes.Count; i++)
                if (planes[i].Height(x, y) < planes[selected].Height(x, y))
                    selected = i;
            return selected;
        },
    };

    private static RoofModel ExtrusionFallback(List<RoofSample> samples)
    {
        double total = samples.Sum(sample => sample.Weight), accumulated = 0, median = samples[0].Z;
        foreach (RoofSample sample in samples.OrderBy(sample => sample.Z))
        {
            accumulated += sample.Weight;
            median = sample.Z;
            if (accumulated >= total * 0.5)
                break;
        }
        return Minimum(ScanRoofKind.ExtrusionFallback, new[] { new RoofPlane(0, 0, median) });
    }

    private static bool Independent(RoofPlane a, RoofPlane b)
    {
        double magnitude = Math.Sqrt((Square(a.X) + Square(a.Y)) * (Square(b.X) + Square(b.Y)));
        return magnitude > 0.01 && Math.Abs(a.X * b.X + a.Y * b.Y) / magnitude < 0.35;
    }

    private static List<Cut> SpatialCuts(P2[][] patches, Options options)
    {
        var cuts = new List<Cut>();
        for (int i = 0; i < patches.Length; i++)
            for (int j = i + 1; j < patches.Length; j++)
            {
                var pairs = new List<(P2 A, P2 B, double Distance)>();
                foreach (P2 a in patches[i])
                {
                    P2 b = patches[j].MinBy(b => Square(a.X - b.X) + Square(a.Y - b.Y));
                    pairs.Add((a, b, Math.Sqrt(Square(a.X - b.X) + Square(a.Y - b.Y))));
                }
                foreach (P2 b in patches[j])
                {
                    P2 a = patches[i].MinBy(a => Square(a.X - b.X) + Square(a.Y - b.Y));
                    pairs.Add((a, b, Math.Sqrt(Square(a.X - b.X) + Square(a.Y - b.Y))));
                }
                double nearest = pairs.Min(pair => pair.Distance);
                if (nearest > options.FootprintTolerance * 2 + options.RoofTolerance * 2)
                    continue;
                var near = pairs.Where(pair => pair.Distance <= nearest * 3 + options.FootprintTolerance).ToArray();
                double weight = near.Sum(pair => 1 / Math.Max(pair.Distance, options.FootprintTolerance * .01));
                double x = near.Sum(pair => (pair.A.X + pair.B.X) * .5 / Math.Max(pair.Distance, options.FootprintTolerance * .01)) / weight;
                double y = near.Sum(pair => (pair.A.Y + pair.B.Y) * .5 / Math.Max(pair.Distance, options.FootprintTolerance * .01)) / weight;
                double xx = 0, xy = 0, yy = 0;
                foreach (var pair in near)
                {
                    double dx = (pair.A.X + pair.B.X) * .5 - x, dy = (pair.A.Y + pair.B.Y) * .5 - y;
                    double w = 1 / Math.Max(pair.Distance, options.FootprintTolerance * .01);
                    xx += dx * dx * w; xy += dx * dy * w; yy += dy * dy * w;
                }
                double angle = .5 * Math.Atan2(2 * xy, xx - yy);
                double aNormal = -Math.Sin(angle), bNormal = Math.Cos(angle);
                cuts.Add(new Cut(aNormal, bNormal, -aNormal * x - bNormal * y));
            }
        return cuts;
    }
}
