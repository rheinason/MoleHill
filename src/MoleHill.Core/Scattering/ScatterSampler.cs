using MoleHill.Core.Grading;
using MoleHill.Core.Engine;

namespace MoleHill.Core.Scattering;

/// <summary>
/// Deterministic 2D point distribution inside one or more boundary polygons. Pure geometry (no Rhino):
/// the caller resolves boundary curves to flat XY loops, calls <see cref="Sample"/>, then projects the
/// returned points onto the terrain. All density modes are reduced to a single effective cell size, so
/// every pattern responds consistently to count / per-area / spacing. Identical seeds yield identical
/// points; the RNG is a self-contained SplitMix64 (not <see cref="System.Random"/>) so results are
/// stable across runtimes and rebuilds.
/// </summary>
public static class ScatterSampler
{
    /// <summary>Safety cap on generated points when the request doesn't specify one; guards against a
    /// runaway density (tiny spacing / huge count) hanging the build.</summary>
    public const int DefaultMaxSamples = 200_000;

    /// <summary>Curve placements are substantially more expensive than region samples because every
    /// point is projected onto the terrain and turned into instance preview geometry. Keep their
    /// implicit safety ceiling lower so a tiny spacing cannot stall the interactive rebuild.</summary>
    public const int DefaultMaxCurveSamples = 20_000;

    public static List<(double X, double Y)> Sample(ScatterRequest request)
    {
        var result = new List<(double X, double Y)>();
        if (request is null)
            return result;

        int cap = request.MaxSamples > 0 ? request.MaxSamples : DefaultMaxSamples;
        Func<bool> cancelled = request.ShouldCancel ?? (static () => false);

        if (request.Source == ScatterSourceMode.Curve)
        {
            foreach (ScatterCurvePoint point in SampleCurve(request, itemExtent: null))
                result.Add((point.X, point.Y));
            return result;
        }

        var loops = new List<double[]>();
        foreach (double[] loop in request.Boundaries)
        {
            if (loop is { Length: >= 6 } && loop.Length % 2 == 0)
                loops.Add(loop);
        }

        if (loops.Count == 0)
            return result;

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        double area = 0.0;
        foreach (double[] loop in loops)
        {
            int count = loop.Length / 2;
            for (int i = 0; i < count; i++)
            {
                double x = loop[i * 2], y = loop[i * 2 + 1];
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }

            area += Math.Abs(SignedArea(loop, count));
        }

        if (area <= 0.0 || maxX <= minX || maxY <= minY)
            return result;

        // Per-loop bounds, computed once. A candidate outside a loop's bounding box is outside that
        // loop, so its edges never need scanning - which matters because every candidate sample pays
        // this predicate, and a rejected candidate otherwise walked every edge of every loop.
        var loopBounds = new (double MinX, double MaxX, double MinY, double MaxY)[loops.Count];
        for (int i = 0; i < loops.Count; i++)
        {
            double[] loop = loops[i];
            int count = loop.Length / 2;
            double loopMinX = double.MaxValue, loopMaxX = double.MinValue;
            double loopMinY = double.MaxValue, loopMaxY = double.MinValue;
            for (int v = 0; v < count; v++)
            {
                double x = loop[v * 2], y = loop[(v * 2) + 1];
                if (x < loopMinX) loopMinX = x;
                if (x > loopMaxX) loopMaxX = x;
                if (y < loopMinY) loopMinY = y;
                if (y > loopMaxY) loopMaxY = y;
            }

            loopBounds[i] = (loopMinX, loopMaxX, loopMinY, loopMaxY);
        }

        bool Inside(double x, double y)
        {
            for (int i = 0; i < loops.Count; i++)
            {
                (double loopMinX, double loopMaxX, double loopMinY, double loopMaxY) = loopBounds[i];
                if (x < loopMinX || x > loopMaxX || y < loopMinY || y > loopMaxY)
                    continue;

                double[] loop = loops[i];
                if (GradingGeometry2D.PointInPolygon(x, y, loop, loop.Length / 2))
                    return true;
            }

            return false;
        }

        // Reduce every density mode to one effective cell size; patterns key off it uniformly.
        double cellSize = request.DensityMode switch
        {
            ScatterDensityMode.Spacing => request.Spacing,
            ScatterDensityMode.PerArea => request.PerAreaDensity > 0.0 ? 1.0 / Math.Sqrt(request.PerAreaDensity) : 0.0,
            _ => request.Count > 0.0 ? Math.Sqrt(area / request.Count) : 0.0
        };

        if (!(cellSize > 0.0) || !double.IsFinite(cellSize))
            return result;

        int targetCount = request.DensityMode switch
        {
            ScatterDensityMode.Count => (int)Math.Round(request.Count),
            ScatterDensityMode.PerArea => (int)Math.Round(request.PerAreaDensity * area),
            _ => (int)Math.Round(area / (cellSize * cellSize))
        };
        if (targetCount < 0)
            targetCount = 0;
        if (targetCount > cap)
            targetCount = cap;

        var rng = new SplitMix64(Mix(request.Seed));

        switch (request.Pattern)
        {
            case ScatterPattern.Grid:
                SampleGrid(result, Inside, minX, minY, maxX, maxY, cellSize, jitter: false, cap, cancelled, ref rng);
                break;
            case ScatterPattern.JitteredGrid:
                SampleGrid(result, Inside, minX, minY, maxX, maxY, cellSize, jitter: true, cap, cancelled, ref rng);
                break;
            case ScatterPattern.PoissonDisk:
                SamplePoisson(result, Inside, minX, minY, maxX, maxY, cellSize, cap, cancelled, ref rng);
                break;
            default:
                SampleRandom(result, Inside, minX, minY, maxX, maxY, targetCount, cancelled, ref rng);
                break;
        }

        return result;
    }

    private static void SampleRandom(
        List<(double X, double Y)> result,
        Func<double, double, bool> inside,
        double minX, double minY, double maxX, double maxY,
        int targetCount,
        Func<bool> cancelled,
        ref SplitMix64 rng)
    {
        if (targetCount <= 0)
            return;

        double width = maxX - minX;
        double height = maxY - minY;
        long maxAttempts = (long)targetCount * 40 + 1000;
        long attempts = 0;
        while (result.Count < targetCount && attempts < maxAttempts)
        {
            attempts++;
            if ((attempts & 8191) == 0 && cancelled())
                return;

            double x = minX + rng.NextUnit() * width;
            double y = minY + rng.NextUnit() * height;
            if (inside(x, y))
                result.Add((x, y));
        }
    }

    private static void SampleGrid(
        List<(double X, double Y)> result,
        Func<double, double, bool> inside,
        double minX, double minY, double maxX, double maxY,
        double cellSize,
        bool jitter,
        int cap,
        Func<bool> cancelled,
        ref SplitMix64 rng)
    {
        // A huge extent over a tiny cell overflows an int cast, and the old code silently produced
        // nothing when it wrapped negative. Count in doubles, then clamp: the cap stops the loop long
        // before the clamp is reached, so a clamped dimension changes no emitted point.
        double requestedColumns = Math.Ceiling((maxX - minX) / cellSize);
        double requestedRows = Math.Ceiling((maxY - minY) / cellSize);
        if (!(requestedColumns > 0.0) || !(requestedRows > 0.0))
            return;

        long columns = (long)Math.Min(requestedColumns, int.MaxValue);
        long rows = (long)Math.Min(requestedRows, int.MaxValue);

        for (long row = 0; row < rows; row++)
        {
            if (result.Count >= cap || cancelled())
                return;

            for (long column = 0; column < columns; column++)
            {
                if (result.Count >= cap)
                    return;

                double x = minX + (column + 0.5) * cellSize;
                double y = minY + (row + 0.5) * cellSize;
                if (jitter)
                {
                    x += (rng.NextUnit() - 0.5) * cellSize;
                    y += (rng.NextUnit() - 0.5) * cellSize;
                }
                else
                {
                    // Advance the stream so jittered/non-jittered seeds stay distinct and reproducible.
                    rng.NextUnit();
                    rng.NextUnit();
                }

                if (inside(x, y))
                    result.Add((x, y));
            }
        }
    }

    // Bridson's blue-noise sampling: every accepted point is at least <paramref name="radius"/> from
    // all others. Backing grid cell = radius/sqrt(2) so each cell holds at most one sample.
    private static void SamplePoisson(
        List<(double X, double Y)> result,
        Func<double, double, bool> inside,
        double minX, double minY, double maxX, double maxY,
        double radius,
        int cap,
        Func<bool> cancelled,
        ref SplitMix64 rng)
    {
        double cell = radius / Math.Sqrt(2.0);

        // The backing grid is SPARSE. Sizing it densely from the bounding box made the allocation a
        // function of extent / radius squared, independent of the requested cap: a large region with a
        // small spacing exhausted memory before the cap could help, and the int product overflowed
        // outright. Occupancy is now keyed by cell, so it costs one dictionary entry per accepted
        // sample - at most one per cell by construction, and no object per entry.
        double requestedWidth = Math.Ceiling((maxX - minX) / cell);
        double requestedHeight = Math.Ceiling((maxY - minY) / cell);
        if (!(requestedWidth > 0.0) || !(requestedHeight > 0.0))
            return;

        // Clamped to int range so the packed cell key below stays injective (a colliding key would be a
        // correctness bug, not just a slow bucket). The cap stops sampling long before either limit.
        long gridWidth = (long)Math.Min(requestedWidth, int.MaxValue);
        long gridHeight = (long)Math.Min(requestedHeight, int.MaxValue);

        var occupancy = new Dictionary<long, int>();
        var samples = new List<(double X, double Y)>();
        var active = new List<int>();
        double radiusSq = radius * radius;

        long CellX(double x) => Math.Clamp((long)((x - minX) / cell), 0L, gridWidth - 1);

        long CellY(double y) => Math.Clamp((long)((y - minY) / cell), 0L, gridHeight - 1);

        static long CellKey(long cellX, long cellY) => (cellX << 32) | (uint)cellY;

        bool FarEnough(double x, double y)
        {
            long gx = CellX(x);
            long gy = CellY(y);
            for (long oy = -2; oy <= 2; oy++)
            {
                long ny = gy + oy;
                if (ny < 0 || ny >= gridHeight)
                    continue;

                for (long ox = -2; ox <= 2; ox++)
                {
                    long nx = gx + ox;
                    if (nx < 0 || nx >= gridWidth)
                        continue;

                    if (!occupancy.TryGetValue(CellKey(nx, ny), out int sampleIndex))
                        continue;

                    double dx = samples[sampleIndex].X - x;
                    double dy = samples[sampleIndex].Y - y;
                    if (dx * dx + dy * dy < radiusSq)
                        return false;
                }
            }

            return true;
        }

        void AddSample(double x, double y)
        {
            int index = samples.Count;
            samples.Add((x, y));
            occupancy[CellKey(CellX(x), CellY(y))] = index;
            active.Add(index);
            if (inside(x, y))
                result.Add((x, y));
        }

        // Seed the first point inside the region.
        double width = maxX - minX;
        double height = maxY - minY;
        bool seeded = false;
        for (int attempt = 0; attempt < 4000 && !seeded; attempt++)
        {
            double x = minX + rng.NextUnit() * width;
            double y = minY + rng.NextUnit() * height;
            if (inside(x, y))
            {
                AddSample(x, y);
                seeded = true;
            }
        }

        if (!seeded)
            return;

        const int candidatesPerPoint = 30;
        while (active.Count > 0)
        {
            if (result.Count >= cap || cancelled())
                return;

            int activeIndex = (int)(rng.NextUnit() * active.Count);
            if (activeIndex >= active.Count)
                activeIndex = active.Count - 1;

            (double X, double Y) origin = samples[active[activeIndex]];
            bool found = false;
            for (int k = 0; k < candidatesPerPoint; k++)
            {
                double angle = rng.NextUnit() * Math.PI * 2.0;
                double distance = radius * (1.0 + rng.NextUnit());
                double x = origin.X + Math.Cos(angle) * distance;
                double y = origin.Y + Math.Sin(angle) * distance;
                if (x < minX || x > maxX || y < minY || y > maxY)
                    continue;
                if (!FarEnough(x, y))
                    continue;

                AddSample(x, y);
                found = true;
                break;
            }

            if (!found)
                active.RemoveAt(activeIndex);
        }
    }

    /// <summary>
    /// Curve mode: distribute points ALONG open polylines, returning each point's curve tangent (for
    /// align-to-tangent). Density is center-to-center Spacing, Count→spacing, or — when
    /// <paramref name="itemExtent"/> is supplied and the mode is <see cref="ScatterDensityMode.EdgeToEdge"/>
    /// — edge-to-edge by per-item footprint (extent supplied by the caller, which knows block sizes) plus
    /// <see cref="ScatterRequest.EdgeGap"/>. Spacing/count modes place points evenly, then nudge each along
    /// the arc by <see cref="ScatterRequest.AlongJitter"/> (fraction of the step). A perpendicular disk XY
    /// jitter (<see cref="ScatterRequest.JitterXy"/>) is applied last. <paramref name="itemExtent"/> receives
    /// the global item index and returns that item's along-curve footprint; the caller must select the block
    /// for the same index deterministically so extents match.
    /// </summary>
    public static List<ScatterCurvePoint> SampleCurve(ScatterRequest request, Func<int, double>? itemExtent)
    {
        var result = new List<ScatterCurvePoint>();
        if (request is null)
            return result;

        int cap = request.MaxSamples > 0 ? request.MaxSamples : DefaultMaxCurveSamples;
        Func<bool> cancelled = request.ShouldCancel ?? (static () => false);

        var paths = new List<(double[] Xy, int Count, double[] Cumulative, double Length)>();
        double totalLength = 0.0;
        foreach (double[] path in request.Paths)
        {
            if (cancelled())
                return result;

            if (path is null || path.Length < 4 || path.Length % 2 != 0)
                continue;

            int count = path.Length / 2;
            var cumulative = new double[count];
            double length = 0.0;
            for (int i = 1; i < count; i++)
            {
                double dx = path[i * 2] - path[(i - 1) * 2];
                double dy = path[i * 2 + 1] - path[(i - 1) * 2 + 1];
                length += Math.Sqrt(dx * dx + dy * dy);
                cumulative[i] = length;
            }

            if (length > 0.0)
            {
                paths.Add((path, count, cumulative, length));
                totalLength += length;
            }
        }

        if (paths.Count == 0 || !(totalLength > 0.0))
            return result;

        var rng = new SplitMix64(Mix(request.Seed));
        double jitter = Math.Max(0.0, request.JitterXy);

        (double X, double Y, double Tangent) PointOn((double[] Xy, int Count, double[] Cumulative, double Length) path, double s)
        {
            s = Math.Clamp(s, 0.0, path.Length);
            int i = Array.BinarySearch(path.Cumulative, s);
            if (i < 0)
                i = ~i;
            i = Math.Max(1, i);
            if (i >= path.Count)
                i = path.Count - 1;

            double dxSeg = path.Xy[i * 2] - path.Xy[(i - 1) * 2];
            double dySeg = path.Xy[i * 2 + 1] - path.Xy[(i - 1) * 2 + 1];
            double segment = path.Cumulative[i] - path.Cumulative[i - 1];
            double t = segment > 1e-12 ? (s - path.Cumulative[i - 1]) / segment : 0.0;
            double x = path.Xy[(i - 1) * 2] + dxSeg * t;
            double y = path.Xy[(i - 1) * 2 + 1] + dySeg * t;
            return (x, y, Math.Atan2(dySeg, dxSeg));
        }

        void Emit(double x, double y, double tangent)
        {
            if (jitter > 0.0)
            {
                double radius = jitter * Math.Sqrt(rng.NextUnit());
                double angle = rng.NextUnit() * Math.PI * 2.0;
                x += Math.Cos(angle) * radius;
                y += Math.Sin(angle) * radius;
            }

            result.Add(new ScatterCurvePoint(x, y, tangent));
        }

        // Edge-to-edge: a sequential walk advancing by each item's footprint + gap. Needs per-item
        // extents from the caller; without them, fall through to the spacing/count modes below.
        if (request.DensityMode == ScatterDensityMode.EdgeToEdge && itemExtent != null)
        {
            double gap = Math.Max(0.0, request.EdgeGap);
            int index = 0;
            foreach (var path in paths)
            {
                if (result.Count >= cap || cancelled())
                    return result;

                double s = 0.0;
                double prevHalf = 0.0;
                bool first = true;
                while (true)
                {
                    if (result.Count >= cap || cancelled())
                        return result;

                    double extent = itemExtent(index);
                    if (!(extent > 0.0) || !double.IsFinite(extent))
                        extent = Math.Max(gap, ScaleAwareTolerance.LengthFloor(gap));
                    double half = extent * 0.5;

                    s = first ? half : s + prevHalf + gap + half;
                    first = false;
                    if (s + half > path.Length + 1e-9)
                        break;

                    (double x, double y, double tangent) = PointOn(path, s);
                    Emit(x, y, tangent);
                    index++;
                    prevHalf = half;
                }
            }

            return result;
        }

        // Center-to-center spacing along the arc (or a target count mapped to one). Points are placed
        // evenly, then nudged along the arc by an optional randomness fraction of the step (0 = even,
        // 1 = up to ±half a step). A "pattern" has no meaning along a 1-D line — order/randomness do.
        double step = request.DensityMode == ScatterDensityMode.Spacing
            ? request.Spacing
            : request.Count >= 1.0 ? totalLength / request.Count : 0.0;
        if (!(step > 0.0) || !double.IsFinite(step))
            return result;

        double alongJitter = Math.Clamp(request.AlongJitter, 0.0, 1.0);

        foreach (var path in paths)
        {
            if (result.Count >= cap || cancelled())
                return result;

            for (double s = 0.0; s <= path.Length + 1e-9; s += step)
            {
                if (result.Count >= cap || cancelled())
                    return result;

                double arc = alongJitter > 0.0
                    ? Math.Clamp(s + (rng.NextUnit() - 0.5) * step * alongJitter, 0.0, path.Length)
                    : s;
                (double x, double y, double tangent) = PointOn(path, arc);
                Emit(x, y, tangent);
            }
        }

        return result;
    }

    private static double SignedArea(double[] loop, int count)
    {
        double sum = 0.0;
        for (int i = 0; i < count; i++)
        {
            int j = (i + 1) % count;
            sum += loop[i * 2] * loop[j * 2 + 1] - loop[j * 2] * loop[i * 2 + 1];
        }

        return sum * 0.5;
    }

    private static ulong Mix(int seed)
    {
        // Spread a small int seed across the full 64-bit state so nearby seeds diverge immediately.
        ulong z = unchecked((ulong)seed * 0x9E3779B97F4A7C15UL + 0xD1B54A32D192ED03UL);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z ^= z >> 27;
        return z;
    }

    private struct SplitMix64
    {
        private ulong _state;

        public SplitMix64(ulong seed) => _state = seed;

        public double NextUnit()
        {
            _state = unchecked(_state + 0x9E3779B97F4A7C15UL);
            ulong z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            const double divisor = 1UL << 53;
            return (z >> 11) / divisor;
        }
    }
}
