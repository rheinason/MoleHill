using MoleHill.Core.Grading;

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
    public static List<(double X, double Y)> Sample(ScatterRequest request)
    {
        var result = new List<(double X, double Y)>();
        if (request is null)
            return result;

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

        bool Inside(double x, double y)
        {
            foreach (double[] loop in loops)
            {
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

        var rng = new SplitMix64(Mix(request.Seed));

        switch (request.Pattern)
        {
            case ScatterPattern.Grid:
                SampleGrid(result, Inside, minX, minY, maxX, maxY, cellSize, jitter: false, ref rng);
                break;
            case ScatterPattern.JitteredGrid:
                SampleGrid(result, Inside, minX, minY, maxX, maxY, cellSize, jitter: true, ref rng);
                break;
            case ScatterPattern.PoissonDisk:
                SamplePoisson(result, Inside, minX, minY, maxX, maxY, cellSize, ref rng);
                break;
            default:
                SampleRandom(result, Inside, minX, minY, maxX, maxY, targetCount, ref rng);
                break;
        }

        return result;
    }

    private static void SampleRandom(
        List<(double X, double Y)> result,
        Func<double, double, bool> inside,
        double minX, double minY, double maxX, double maxY,
        int targetCount,
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
        ref SplitMix64 rng)
    {
        int columns = (int)Math.Ceiling((maxX - minX) / cellSize);
        int rows = (int)Math.Ceiling((maxY - minY) / cellSize);
        if (columns <= 0 || rows <= 0)
            return;

        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
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
        ref SplitMix64 rng)
    {
        double cell = radius / Math.Sqrt(2.0);
        int gridWidth = (int)Math.Ceiling((maxX - minX) / cell);
        int gridHeight = (int)Math.Ceiling((maxY - minY) / cell);
        if (gridWidth <= 0 || gridHeight <= 0)
            return;

        var grid = new int[gridWidth * gridHeight];
        Array.Fill(grid, -1);
        var samples = new List<(double X, double Y)>();
        var active = new List<int>();
        double radiusSq = radius * radius;

        int GridIndex(double x, double y)
        {
            int gx = Math.Clamp((int)((x - minX) / cell), 0, gridWidth - 1);
            int gy = Math.Clamp((int)((y - minY) / cell), 0, gridHeight - 1);
            return gy * gridWidth + gx;
        }

        bool FarEnough(double x, double y)
        {
            int gx = Math.Clamp((int)((x - minX) / cell), 0, gridWidth - 1);
            int gy = Math.Clamp((int)((y - minY) / cell), 0, gridHeight - 1);
            for (int oy = -2; oy <= 2; oy++)
            {
                int ny = gy + oy;
                if (ny < 0 || ny >= gridHeight)
                    continue;

                for (int ox = -2; ox <= 2; ox++)
                {
                    int nx = gx + ox;
                    if (nx < 0 || nx >= gridWidth)
                        continue;

                    int sampleIndex = grid[ny * gridWidth + nx];
                    if (sampleIndex < 0)
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
            grid[GridIndex(x, y)] = index;
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
