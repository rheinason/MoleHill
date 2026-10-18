using MoleHill.Core.Engine;
using MoleHill.Core.Analysis;
using MoleHill.Core.Geometry;

namespace MoleHill.Core.Scan;

/// <summary>Building context from raised regions and measured roof planes. Footprints, planes, roofs and
/// closed shells and manifold junctions are implemented in the corresponding partial files; no host geometry is retained.</summary>
public static partial class ScanBuildings
{
    public sealed class Options
    {
        public required double MinimumArea { get; init; }
        public required double MinimumHeight { get; init; }
        public required double FootprintTolerance { get; init; }
        public required double RoofTolerance { get; init; }
        /// <summary>Suppress arrangement patches narrower than this physical size; zero retains measured details.</summary>
        public double MinimumRoofDetail { get; init; }
        /// <summary>Maximum connected roof facets, including roof steps; zero leaves the face count unbounded.</summary>
        public int MaximumRoofFaces { get; init; }
        public double PlaneAngleDegrees { get; init; } = 18;
        public double MinimumPlanarShare { get; init; } = 0.7;
        public double MaximumGreenShare { get; init; } = 0.55;
        public int MaximumPlanes { get; init; } = 12;
        public CancellationToken CancellationToken { get; init; }
    }

    public sealed class Building
    {
        /// <summary>Closed plan loops without repeated endpoints, flat XYZ at GroundHeight.</summary>
        public required IReadOnlyList<double[]> Footprints { get; init; }
        public required IndexedTriMesh Solid { get; init; }
        public required IReadOnlyList<ScanBuildingFaces.Face> PlanarFaces { get; init; }
        public required ScanRoofKind RoofKind { get; init; }
        public required double GroundHeight { get; init; }
        public required double EaveHeight { get; init; }
        public required double RidgeHeight { get; init; }
        public required double RoofRms { get; init; }
        public required double RoofWithinToleranceShare { get; init; }
        public required double RoofSampleCoverage { get; init; }
        public required double PlanarShare { get; init; }
        public required int RoofPlaneCount { get; init; }
        public required double FootprintArea { get; init; }
        public required string Diagnostic { get; init; }
        public bool IsApproximateRoof { get; init; }
        public bool IsFallback => RoofKind == ScanRoofKind.ExtrusionFallback || IsApproximateRoof;
    }

    public sealed class Result
    {
        public required IReadOnlyList<Building> Buildings { get; init; }
        public required int CandidateCount { get; init; }
        public required int RejectedVegetation { get; init; }
        public required int RejectedGeometry { get; init; }
    }

    private sealed class Candidate
    {
        public required int Label { get; init; }
        public List<int> Nodes { get; } = new();
        public List<int> Faces { get; } = new();
    }

    private readonly record struct P2(double X, double Y);
    private readonly record struct RoofPlane(double X, double Y, double Z)
    {
        public double Height(double x, double y) => X * x + Y * y + Z;
    }

    private sealed class RoofSample
    {
        public required double X { get; init; }
        public required double Y { get; init; }
        public required double Z { get; init; }
        public required double Weight { get; init; }
        public required RoofPlane FacePlane { get; init; }
        public required int[] Vertices { get; init; }
        public required int[] Neighbours { get; init; }
    }

    private sealed record PlaneRegion(RoofPlane Plane, List<int> Samples, double Area, double Rms);

    public static Result Extract(IndexedTriMesh scan, ScanGround.Result ground, int[]? surfaceColours, Options options, byte[]? surfaceClasses = null)
    {
        ArgumentNullException.ThrowIfNull(ground);
        ArgumentNullException.ThrowIfNull(options);
        if (!Positive(options.MinimumArea) || !Positive(options.MinimumHeight) || !Positive(options.FootprintTolerance) ||
            !Positive(options.RoofTolerance) || !double.IsFinite(options.MinimumRoofDetail) || options.MinimumRoofDetail < 0 ||
            options.MaximumRoofFaces < 0 || options.MaximumRoofFaces > 10000 || !Positive(options.PlaneAngleDegrees) || options.PlaneAngleDegrees >= 45 ||
            !Positive(options.MinimumPlanarShare) || options.MinimumPlanarShare > 1 ||
            !double.IsFinite(options.MaximumGreenShare) || options.MaximumGreenShare < 0 || options.MaximumGreenShare > 1 ||
            options.MaximumPlanes < 1 || options.MaximumPlanes > 16)
            throw new ArgumentOutOfRangeException(nameof(options));
        ScanGrid surface = ground.Surface;
        if (surfaceColours != null && surfaceColours.Length != surface.NodeCount)
            throw new ArgumentException("Colours must align with the scan surface.", nameof(surfaceColours));
        if (surfaceClasses != null && surfaceClasses.Length != surface.NodeCount)
            throw new ArgumentException("Classes must align with the scan surface.", nameof(surfaceClasses));
        options.CancellationToken.ThrowIfCancellationRequested();
        var raised = new bool[surface.NodeCount];
        for (int node = 0; node < raised.Length; node++)
            raised[node] = ground.Domain[node] && !ground.IsGround[node] && double.IsFinite(surface.Values[node]) &&
                surface.Values[node] - ground.Ground.Values[node] >= options.MinimumHeight;
        int[] labels = GridMorphology.Label(raised, surface.Columns, surface.Rows, out List<int> sizes, out _);
        var candidates = new Dictionary<int, Candidate>();
        for (int label = 0; label < sizes.Count; label++)
            if (sizes[label] * surface.NodeArea >= options.MinimumArea)
                candidates.Add(label, new Candidate { Label = label });
        for (int node = 0; node < labels.Length; node++)
            if (candidates.TryGetValue(labels[node], out Candidate? candidate))
                candidate.Nodes.Add(node);

        // Capture upward triangles once. The roof fit sees the original mesh, not harmonic ground fill.
        for (int face = 0; face < scan.FaceCount; face++)
        {
            if ((face & 4095) == 0)
                options.CancellationToken.ThrowIfCancellationRequested();
            int a = scan.Faces[face * 3] * 3, b = scan.Faces[face * 3 + 1] * 3, c = scan.Faces[face * 3 + 2] * 3;
            double x = (scan.Vertices[a] + scan.Vertices[b] + scan.Vertices[c]) / 3;
            double y = (scan.Vertices[a + 1] + scan.Vertices[b + 1] + scan.Vertices[c + 1]) / 3;
            int column = (int)Math.Round((x - surface.OriginX) / surface.CellSize);
            int row = (int)Math.Round((y - surface.OriginY) / surface.CellSize);
            if (column < 0 || row < 0 || column >= surface.Columns || row >= surface.Rows ||
                !candidates.TryGetValue(labels[row * surface.Columns + column], out Candidate? candidate))
                continue;
            double ux = scan.Vertices[b] - scan.Vertices[a], uy = scan.Vertices[b + 1] - scan.Vertices[a + 1], uz = scan.Vertices[b + 2] - scan.Vertices[a + 2];
            double vx = scan.Vertices[c] - scan.Vertices[a], vy = scan.Vertices[c + 1] - scan.Vertices[a + 1], vz = scan.Vertices[c + 2] - scan.Vertices[a + 2];
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            if (Math.Abs(nz) < Math.Sqrt(nx * nx + ny * ny + nz * nz) * 0.2 || Math.Abs(nz) < 1e-20)
                continue;
            double z = (scan.Vertices[a + 2] + scan.Vertices[b + 2] + scan.Vertices[c + 2]) / 3;
            if (Math.Abs(z - surface.Values[row * surface.Columns + column]) > options.RoofTolerance + surface.CellSize * 0.8)
                continue;
            candidate.Faces.Add(face);
        }

        var buildings = new List<Building>();
        int vegetation = 0, geometry = 0;
        foreach (Candidate candidate in candidates.Values)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            if (surfaceClasses != null && candidate.Nodes.Count(node => surfaceClasses[node] is >= 3 and <= 5) > candidate.Nodes.Count * .5)
            {
                vegetation++;
                continue;
            }
            int coloured = 0, green = 0;
            foreach (int node in candidate.Nodes)
            {
                int rgb = surfaceColours?[node] ?? -1;
                if (rgb < 0)
                    continue;
                coloured++;
                int r = rgb >> 16 & 255, g = rgb >> 8 & 255, b = rgb & 255;
                if (g > r && g > b && (2.0 * g - r - b) / Math.Max(1, r + g + b) > 0.08)
                    green++;
            }
            bool greenCandidate = coloured > candidate.Nodes.Count * 0.5 && green > coloured * options.MaximumGreenShare;
            List<RoofSample> samples = CaptureRoofSamples(scan, candidate);
            if (samples.Count < 3)
            {
                if (greenCandidate) vegetation++; else geometry++;
                continue;
            }
            // Work about a local origin throughout fitting and arrangement, including survey coordinates.
            double originX = candidate.Nodes.Average(n => surface.NodeX(n % surface.Columns));
            double originY = candidate.Nodes.Average(n => surface.NodeY(n / surface.Columns));
            samples = LocalSamples(samples, originX, originY);
            List<PlaneRegion> planes = FindRoofPlanes(samples, options);
            double totalArea = samples.Sum(sample => sample.Weight);
            double planarShare = planes.Sum(plane => plane.Area) / totalArea;
            double minimumDominantShare = coloured > candidate.Nodes.Count * .5 ? .18 : .25;
            if (planes.Count == 0 || planarShare < options.MinimumPlanarShare || planes.Max(plane => plane.Area) < totalArea * minimumDominantShare)
            {
                if (greenCandidate) vegetation++; else geometry++;
                continue;
            }
            // Green is supporting evidence, not a veto on turf roofs. A green candidate needs stronger
            // coherent plane evidence than an ordinary roof before it is retained as a building.
            if (greenCandidate && (planarShare < .9 || planes.Max(plane => plane.Area) < totalArea * .3 || planes.Count > 6))
            {
                vegetation++;
                continue;
            }
            List<List<P2>> footprints = BuildFootprints(candidate, labels, surface, options, originX, originY);
            if (footprints.Count == 0)
            {
                geometry++;
                continue;
            }
            double nearestWithin = samples.Where(sample => planes.Min(region => Math.Abs(sample.Z - region.Plane.Height(sample.X, sample.Y))) <= options.RoofTolerance).Sum(sample => sample.Weight) / totalArea;
            double baseZ = GroundAlongFootprint(footprints, ground.Ground, originX, originY);
            RoofModel? model = null;
            IndexedTriMesh solid = IndexedTriMesh.Empty;
            var failed = new List<string>();
            var approximations = new List<(RoofModel Model, IndexedTriMesh Solid)>();
            foreach (RoofModel attempted in ChooseRoofs(samples, planes, footprints, options))
            {
                IndexedTriMesh candidateSolid = BuildSolid(footprints, attempted, baseZ, options);
                bool fits = !candidateSolid.IsEmpty && MeasureSolid(candidateSolid, samples, attempted, options);
                bool tooComplex = options.MaximumRoofFaces > 0 && !candidateSolid.IsEmpty &&
                    CountRoofFaces(ScanBuildingFaces.Coalesce(candidateSolid, options.RoofTolerance * 1e-4,
                        options.CancellationToken), footprints, options.RoofTolerance * 1e-4) > options.MaximumRoofFaces;
                bool fallback = attempted.Kind == ScanRoofKind.ExtrusionFallback || attempted.Approximate;
                if (!candidateSolid.IsEmpty && !tooComplex && fallback && attempted.Bounded && attempted.CoveredShare >= .85 &&
                    (attempted.Kind == ScanRoofKind.ExtrusionFallback || attempted.WithinShare >= .5))
                {
                    approximations.Add((attempted, candidateSolid));
                    continue;
                }
                if (!candidateSolid.IsEmpty && !tooComplex && fits && !fallback)
                {
                    solid = candidateSolid;
                    model = attempted;
                    break;
                }
                failed.Add(FormattableString.Invariant($"Attempted {attempted.Kind}: {(tooComplex ? "roof face budget exceeded" : candidateSolid.IsEmpty ? attempted.Diagnostic : $"shell RMS {attempted.Rms:F3}, {attempted.WithinShare:P1} within tolerance, {attempted.CoveredShare:P1} coverage")}."));
            }
            if (model == null && approximations.Count > 0)
            {
                // The robust pre-fit score deliberately caps outliers. It must not choose an
                // approximate shell with metre-scale errors over a better measured alternative.
                var best = approximations.OrderBy(candidate => candidate.Model.Rms)
                    .ThenByDescending(candidate => candidate.Model.CoveredShare)
                    .ThenByDescending(candidate => candidate.Model.WithinShare).First();
                model = best.Model; solid = best.Solid;
                model.Diagnostic += FormattableString.Invariant($" Selected shell: {model.Rms:F3} RMS, {model.WithinShare:P1} within tolerance, {model.CoveredShare:P1} coverage. ") +
                    string.Join(" ", failed.Take(4));
            }
            if (solid.IsEmpty || model == null)
            {
                geometry++;
                continue;
            }
            double eave = double.PositiveInfinity, ridge = double.NegativeInfinity;
            for (int i = 2; i < solid.VertexCount * 3; i += 3)
            {
                if (solid.Vertices[i] <= baseZ + options.RoofTolerance * 1e-6)
                    continue;
                eave = Math.Min(eave, solid.Vertices[i]);
                ridge = Math.Max(ridge, solid.Vertices[i]);
            }
            if (!double.IsFinite(baseZ + eave + ridge) || eave < baseZ + options.MinimumHeight * 0.25)
            {
                geometry++;
                continue;
            }
            for (int i = 0; i < solid.VertexCount; i++)
            {
                solid.Vertices[i * 3] += originX;
                solid.Vertices[i * 3 + 1] += originY;
            }
            buildings.Add(new Building
            {
                Footprints = footprints.Select(loop => loop.SelectMany(p => new[] { p.X + originX, p.Y + originY, baseZ }).ToArray()).ToArray(),
                Solid = solid, RoofKind = model.Kind, IsApproximateRoof = model.Approximate, GroundHeight = baseZ, EaveHeight = eave, RidgeHeight = ridge,
                PlanarFaces = ScanBuildingFaces.Coalesce(solid, options.RoofTolerance * 1e-4, options.CancellationToken),
                RoofRms = model.Rms, RoofWithinToleranceShare = model.WithinShare, PlanarShare = planarShare,
                RoofSampleCoverage = model.CoveredShare,
                RoofPlaneCount = model.Planes.Count, FootprintArea = Math.Abs(footprints.Sum(SignedArea)),
                Diagnostic = model.Kind == ScanRoofKind.ExtrusionFallback
                    ? FormattableString.Invariant($"Median-height extrusion fallback. {model.Diagnostic} Nearest plane explains {nearestWithin:P1}.")
                    : model.Approximate ? model.Diagnostic
                    : $"{model.Kind} fitted to upward scan triangles; accuracy describes observed roof samples.",
            });
        }
        return new Result { Buildings = buildings, CandidateCount = candidates.Count, RejectedVegetation = vegetation, RejectedGeometry = geometry };
    }

    private static bool Positive(double value) => double.IsFinite(value) && value > 0;

    private static int CountRoofFaces(IReadOnlyList<ScanBuildingFaces.Face> faces, List<List<P2>> footprints, double tolerance)
    {
        int count = 0;
        double[][] perimeter = footprints.Select(loop => loop.SelectMany(p => new[] { p.X, p.Y }).ToArray()).ToArray();
        foreach (ScanBuildingFaces.Face face in faces)
        {
            double twiceArea = 0;
            foreach (double[] loop in face.Loops)
                for (int i = 0; i < loop.Length; i += 3)
                {
                    int next = (i + 3) % loop.Length;
                    twiceArea += (loop[i] - loop[0]) * (loop[next + 1] - loop[1]) -
                        (loop[next] - loop[0]) * (loop[i + 1] - loop[1]);
                }
            // Include interior vertical roof steps: upward faces alone can hide hundreds of
            // little step walls. Only the floor and actual exterior facades are exempt.
            if (twiceArea > tolerance * tolerance || Math.Abs(twiceArea) <= tolerance * tolerance &&
                !face.Loops.All(loop => Enumerable.Range(0, loop.Length / 3).All(i =>
                {
                    int a = i * 3, b = (a + 3) % loop.Length;
                    return OnPerimeter(loop[a], loop[a + 1]) &&
                        OnPerimeter((loop[a] + loop[b]) * .5, (loop[a + 1] + loop[b + 1]) * .5);
                }))) count++;
        }
        return count;

        bool OnPerimeter(double x, double y)
        {
            foreach (double[] loop in perimeter)
                if (Geometry2D.DistanceToPolygon(x, y, loop, loop.Length / 2) <= tolerance) return true;
            return false;
        }
    }

    private static bool MeasureSolid(IndexedTriMesh solid, List<RoofSample> samples, RoofModel model, Options options)
    {
        var projector = new MeshHeightProjector(solid.Vertices, solid.VertexCount, solid.Faces, solid.FaceCount);
        double weight = 0, covered = 0, sum = 0, within = 0;
        foreach (RoofSample sample in samples)
        {
            weight += sample.Weight;
            if (!projector.TryGetHeightExtremes(sample.X, sample.Y, options.RoofTolerance * 1e-6, out _, out double high))
                continue;
            double error = Math.Abs(sample.Z - high);
            covered += sample.Weight; sum += sample.Weight * error * error;
            if (error <= options.RoofTolerance)
                within += sample.Weight;
        }
        model.Rms = covered > 0 ? Math.Sqrt(sum / covered) : double.PositiveInfinity;
        model.WithinShare = covered > 0 ? within / covered : 0;
        model.CoveredShare = covered / weight;
        double highest = solid.Vertices.Where((_, i) => i % 3 == 2).Max();
        bool bounded = highest <= samples.Max(sample => sample.Z) + options.RoofTolerance * 2;
        model.Bounded = bounded;
        return model.WithinShare >= 0.9 && model.CoveredShare >= 0.85 && bounded;
    }
    private static double SignedArea(IReadOnlyList<P2> loop) =>
        Geometry2D.SignedArea(loop.SelectMany(p => new[] { p.X, p.Y }).ToArray(), loop.Count);
}
