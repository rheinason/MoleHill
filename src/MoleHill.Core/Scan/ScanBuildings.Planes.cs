using MoleHill.Core.Engine;

namespace MoleHill.Core.Scan;

/// <summary>Upward-face adjacency, robust plane seeds, connected region growth and coplanar merging.</summary>
public static partial class ScanBuildings
{
    private static List<RoofSample> CaptureRoofSamples(IndexedTriMesh scan, Candidate candidate)
    {
        var samples = new List<RoofSample>(candidate.Faces.Count);
        var canonical = new Dictionary<(double X, double Y, double Z), int>();
        var edges = IndexedMeshTools.CreateEdgeKeyMap<int>(candidate.Faces.Count * 2);
        foreach (int face in candidate.Faces)
        {
            var vertices = new int[3];
            for (int k = 0; k < 3; k++)
            {
                int index = scan.Faces[face * 3 + k] * 3;
                var key = (scan.Vertices[index], scan.Vertices[index + 1], scan.Vertices[index + 2]);
                if (!canonical.TryGetValue(key, out int id))
                    canonical.Add(key, id = canonical.Count);
                vertices[k] = id;
            }
            int a = scan.Faces[face * 3] * 3, b = scan.Faces[face * 3 + 1] * 3, c = scan.Faces[face * 3 + 2] * 3;
            double ux = scan.Vertices[b] - scan.Vertices[a], uy = scan.Vertices[b + 1] - scan.Vertices[a + 1], uz = scan.Vertices[b + 2] - scan.Vertices[a + 2];
            double vx = scan.Vertices[c] - scan.Vertices[a], vy = scan.Vertices[c + 1] - scan.Vertices[a + 1], vz = scan.Vertices[c + 2] - scan.Vertices[a + 2];
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            double x = (scan.Vertices[a] + scan.Vertices[b] + scan.Vertices[c]) / 3;
            double y = (scan.Vertices[a + 1] + scan.Vertices[b + 1] + scan.Vertices[c + 1]) / 3;
            double z = (scan.Vertices[a + 2] + scan.Vertices[b + 2] + scan.Vertices[c + 2]) / 3;
            var plane = new RoofPlane(-nx / nz, -ny / nz, z + nx / nz * x + ny / nz * y);
            int current = samples.Count;
            // For a uniform triangle E[(p-centroid)(q-centroid)] is the sum over its three
            // vertices divided by twelve. Centroids alone lose all support along long triangles.
            double Moment(int p, double meanP, int q, double meanQ) =>
                ((scan.Vertices[a + p] - meanP) * (scan.Vertices[a + q] - meanQ) +
                 (scan.Vertices[b + p] - meanP) * (scan.Vertices[b + q] - meanQ) +
                 (scan.Vertices[c + p] - meanP) * (scan.Vertices[c + q] - meanQ)) / 12;
            samples.Add(new RoofSample
            {
                X = x, Y = y, Z = z, MaximumZ = Math.Max(scan.Vertices[a + 2], Math.Max(scan.Vertices[b + 2], scan.Vertices[c + 2])),
                Weight = Math.Abs(nz) * 0.5, FacePlane = plane,
                XX = Moment(0, x, 0, x), XY = Moment(0, x, 1, y), YY = Moment(1, y, 1, y),
                XZ = Moment(0, x, 2, z), YZ = Moment(1, y, 2, z),
                Vertices = vertices, Neighbours = new[] { -1, -1, -1 },
            });
            for (int k = 0; k < 3; k++)
            {
                int p = vertices[k], q = vertices[(k + 1) % 3];
                long key = ((long)Math.Min(p, q) << 32) | (uint)Math.Max(p, q);
                if (edges.TryGetValue(key, out int owner))
                {
                    samples[current].Neighbours[k] = owner / 3;
                    samples[owner / 3].Neighbours[owner % 3] = current;
                }
                else
                    edges.Add(key, current * 3 + k);
            }
        }
        return samples;
    }

    private static List<RoofSample> LocalSamples(List<RoofSample> samples, double originX, double originY) =>
        samples.Select(s => new RoofSample
        {
            X = s.X - originX, Y = s.Y - originY, Z = s.Z, MaximumZ = s.MaximumZ, Weight = s.Weight,
            XX = s.XX, XY = s.XY, YY = s.YY, XZ = s.XZ, YZ = s.YZ,
            FacePlane = new RoofPlane(s.FacePlane.X, s.FacePlane.Y, s.FacePlane.Height(originX, originY)),
            Vertices = s.Vertices, Neighbours = s.Neighbours,
        }).ToList();

    private static List<PlaneRegion> FindRoofPlanes(List<RoofSample> samples, Options options)
    {
        var remaining = Enumerable.Repeat(true, samples.Count).ToArray();
        var smoothed = samples.Select(sample => sample.FacePlane).ToArray();
        double cosine = Math.Cos(options.PlaneAngleDegrees * Math.PI / 180);
        // Normals are averaged over about the footprint tolerance, however finely the scan is triangulated: a
        // full-detail export's 10 cm triangles each tilt with the scan's centimetre noise (±20 degrees on a
        // corrugated roof), and over one ring a third of a plain roof failed the plane angle, so its houses were
        // rejected as unplanar. Neighbours that noisy differ by more than the plane angle too, so the guard that
        // keeps a ridge out of the average is twice it: a ridge between two pitched planes is sharper still.
        double crease = Math.Cos(Math.Min(options.PlaneAngleDegrees * 2, 60) * Math.PI / 180);
        double edge = samples.Select(sample => Math.Sqrt(sample.Weight * 2)).Order().ElementAtOrDefault(samples.Count / 2);
        int rings = edge > 0 ? Math.Clamp((int)Math.Round(options.FootprintTolerance / edge), 1, 8) : 1;
        for (int ring = 0; ring < rings; ring++)
        {
            var next = new RoofPlane[samples.Count];
            for (int i = 0; i < samples.Count; i++)
            {
                double x = smoothed[i].X * samples[i].Weight, y = smoothed[i].Y * samples[i].Weight, w = samples[i].Weight;
                foreach (int neighbour in samples[i].Neighbours)
                    // Normal smoothing must not erase a ridge before plane detection sees it.
                    if (neighbour >= 0 && NormalDot(smoothed[i], smoothed[neighbour]) >= crease)
                    {
                        RoofSample s = samples[neighbour];
                        x += smoothed[neighbour].X * s.Weight; y += smoothed[neighbour].Y * s.Weight; w += s.Weight;
                    }
                next[i] = new RoofPlane(x / w, y / w, 0);
            }
            smoothed = next;
        }
        var random = new Random(1729);
        var regions = new List<PlaneRegion>();
        double minimumArea = options.MinimumArea * 0.03;
        for (int round = 0; round < options.MaximumPlanes; round++)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            int[] active = Enumerable.Range(0, samples.Count).Where(i => remaining[i]).ToArray();
            if (active.Length == 0)
                break;
            RoofPlane? best = null;
            double bestArea = 0;
            // Triangle planes are useful seeds on clean roofs; distant centroid triples remain stable
            // when photogrammetry noise makes individual tiny triangles' normals unreliable.
            for (int trial = 0; trial < 192; trial++)
            {
                if ((trial & 31) == 0)
                    options.CancellationToken.ThrowIfCancellationRequested();
                RoofPlane? seed = trial < 64 ? samples[active[(int)((long)trial * active.Length / 64)]].FacePlane
                    : PlaneThrough(samples[active[random.Next(active.Length)]], samples[active[random.Next(active.Length)]], samples[active[random.Next(active.Length)]]);
                if (seed is not RoofPlane plane || Math.Sqrt(plane.X * plane.X + plane.Y * plane.Y) > 4)
                    continue;
                double area = 0;
                foreach (int i in active)
                    if (Matches(i, plane))
                        area += samples[i].Weight;
                if (area > bestArea)
                {
                    bestArea = area;
                    best = plane;
                }
            }
            if (best == null || bestArea < minimumArea)
                break;
            RoofPlane fitted = best.Value;
            List<int> inliers = new();
            for (int refine = 0; refine < 3; refine++)
            {
                inliers = active.Where(i => Matches(i, fitted)).ToList();
                if (FitPlane(samples, inliers) is RoofPlane next)
                    fitted = next;
            }
            // Grow only connected patches with enough measured area; tiny isolated leaf triangles
            // matching a plane are not a roof facet. UV seams were welded by coordinates above.
            var eligible = new bool[samples.Count];
            foreach (int i in inliers)
                eligible[i] = true;
            var grown = new List<int>();
            var queue = new Queue<int>();
            foreach (int seed in inliers)
            {
                if (!eligible[seed])
                    continue;
                var patch = new List<int>();
                double area = 0;
                eligible[seed] = false;
                queue.Enqueue(seed);
                while (queue.TryDequeue(out int i))
                {
                    patch.Add(i); area += samples[i].Weight;
                    foreach (int neighbour in samples[i].Neighbours)
                        if (neighbour >= 0 && eligible[neighbour])
                        {
                            eligible[neighbour] = false;
                            queue.Enqueue(neighbour);
                        }
                }
                if (area >= minimumArea)
                    grown.AddRange(patch);
            }
            if (grown.Count == 0)
                break;
            fitted = FitPlane(samples, grown) ?? fitted;
            double grownArea = grown.Sum(i => samples[i].Weight);
            double rms = Math.Sqrt(grown.Sum(i => samples[i].Weight * SampleErrorSquared(samples[i], fitted)) / grownArea);
            regions.Add(new PlaneRegion(fitted, grown, grownArea, rms));
            foreach (int i in grown)
                remaining[i] = false;
        }
        // Separate patches can be one physical plane (two wings, or a patch split by a chimney).
        for (int i = 0; i < regions.Count; i++)
            for (int j = regions.Count - 1; j > i; j--)
            {
                if (NormalDot(regions[i].Plane, regions[j].Plane) < cosine)
                    continue;
                List<int> merged = regions[i].Samples.Concat(regions[j].Samples).ToList();
                if (FitPlane(samples, merged) is not RoofPlane fit)
                    continue;
                double area = regions[i].Area + regions[j].Area;
                double rms = Math.Sqrt(merged.Sum(k => samples[k].Weight * SampleErrorSquared(samples[k], fit)) / area);
                if (rms > options.RoofTolerance * 0.5 || merged.Any(k => SampleErrorSquared(samples[k], fit) > Square(options.RoofTolerance)))
                    continue;
                regions[i] = new PlaneRegion(fit, merged, area, rms);
                regions.RemoveAt(j);
            }
        return regions.OrderByDescending(region => region.Area).ToList();

        bool Matches(int i, RoofPlane plane) => SampleErrorSquared(samples[i], plane) <= Square(options.RoofTolerance) &&
            NormalDot(smoothed[i], plane) >= cosine;
    }

    private static double SampleErrorSquared(RoofSample sample, RoofPlane plane)
    {
        double x = plane.X - sample.FacePlane.X, y = plane.Y - sample.FacePlane.Y;
        return Square(sample.Z - plane.Height(sample.X, sample.Y)) +
            Math.Max(0, x * x * sample.XX + 2 * x * y * sample.XY + y * y * sample.YY);
    }

    private static RoofPlane? FitPlane(IReadOnlyList<RoofSample> samples, IReadOnlyList<int> indices)
    {
        double weight = 0, x = 0, y = 0, z = 0;
        foreach (int i in indices)
        {
            RoofSample s = samples[i]; weight += s.Weight; x += s.Weight * s.X; y += s.Weight * s.Y; z += s.Weight * s.Z;
        }
        if (weight <= 0)
            return null;
        x /= weight; y /= weight; z /= weight;
        double xx = 0, xy = 0, yy = 0, xz = 0, yz = 0;
        foreach (int i in indices)
        {
            RoofSample s = samples[i]; double dx = s.X - x, dy = s.Y - y, dz = s.Z - z;
            xx += s.Weight * (dx * dx + s.XX); xy += s.Weight * (dx * dy + s.XY); yy += s.Weight * (dy * dy + s.YY);
            xz += s.Weight * (dx * dz + s.XZ); yz += s.Weight * (dy * dz + s.YZ);
        }
        double determinant = xx * yy - xy * xy;
        if (determinant <= Math.Max(xx * yy, 1e-30) * 1e-12)
            return null;
        double a = (xz * yy - yz * xy) / determinant, b = (yz * xx - xz * xy) / determinant;
        return new RoofPlane(a, b, z - a * x - b * y);
    }

    private static RoofPlane? PlaneThrough(RoofSample a, RoofSample b, RoofSample c)
    {
        double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z;
        double vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
        double determinant = ux * vy - uy * vx;
        if (Math.Abs(determinant) < 1e-10)
            return null;
        double x = (uz * vy - uy * vz) / determinant, y = (ux * vz - uz * vx) / determinant;
        return new RoofPlane(x, y, a.Z - x * a.X - y * a.Y);
    }

    private static double NormalDot(RoofPlane a, RoofPlane b) =>
        (a.X * b.X + a.Y * b.Y + 1) / Math.Sqrt((a.X * a.X + a.Y * a.Y + 1) * (b.X * b.X + b.Y * b.Y + 1));
    private static double Square(double value) => value * value;
}
