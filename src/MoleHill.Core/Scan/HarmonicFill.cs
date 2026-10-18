namespace MoleHill.Core.Scan;

/// <summary>
/// Fills the unknown nodes of a domain with the harmonic (Laplace) interpolant of the known ones: each
/// unknown node ends as the mean of its domain neighbours, so a removed house leaves a smooth patch that
/// meets the ground around it, not the lump nearest-neighbour filling left on the reference scan. A node
/// outside the domain is no neighbour at all (the edge is free, not held at zero).
/// <para>Plain relaxation needs about as many sweeps as a gap is nodes wide (a 20 m house at 0.25 m: tens of
/// thousands); this solves on a lattice halved again and again and starts each finer level from the
/// coarser answer, so a few hundred sweeps per level suffice.</para>
/// </summary>
internal static class HarmonicFill
{
    private const int SweepsPerLevel = 400;
    private const int CoarsestSweeps = 5000;
    private const int CoarsestNodeCount = 4096;
    private const double Overrelaxation = 1.8;

    /// <summary>
    /// Returns <paramref name="values"/> with every domain node that is not <paramref name="known"/> filled.
    /// With no known node at all nothing is filled.
    /// </summary>
    public static double[] Fill(double[] values, bool[] known, bool[] domain, int columns, int rows, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new double[values.Length];
        for (int i = 0; i < values.Length; i++)
            result[i] = domain[i] && known[i] ? values[i] : double.NaN;
        Solve(result, known, domain, columns, rows, cancellationToken);
        return result;
    }

    private static void Solve(double[] values, bool[] known, bool[] domain, int columns, int rows, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool anyUnknown = false;
        for (int i = 0; i < values.Length && !anyUnknown; i++)
            anyUnknown = domain[i] && !known[i];
        if (!anyUnknown)
            return;

        int sweeps = SweepsPerLevel;
        if ((long)columns * rows <= CoarsestNodeCount || columns < 4 || rows < 4)
        {
            SeedWithMean(values, known, domain);
            sweeps = CoarsestSweeps;
        }
        else
        {
            // Coarse node (I, J) stands for fine nodes (2I..2I+1, 2J..2J+1): known where any of them is.
            int coarseColumns = (columns + 1) / 2, coarseRows = (rows + 1) / 2;
            var coarseValues = new double[coarseColumns * coarseRows];
            var coarseKnown = new bool[coarseValues.Length];
            var coarseDomain = new bool[coarseValues.Length];
            for (int cj = 0; cj < coarseRows; cj++)
            {
                for (int ci = 0; ci < coarseColumns; ci++)
                {
                    double sum = 0.0;
                    int count = 0;
                    bool inDomain = false;
                    for (int dj = 0; dj < 2; dj++)
                    {
                        for (int di = 0; di < 2; di++)
                        {
                            int i = (2 * ci) + di, j = (2 * cj) + dj;
                            if (i >= columns || j >= rows)
                                continue;
                            int node = (j * columns) + i;
                            inDomain |= domain[node];
                            if (domain[node] && known[node])
                            {
                                sum += values[node];
                                count++;
                            }
                        }
                    }

                    int coarse = (cj * coarseColumns) + ci;
                    coarseDomain[coarse] = inDomain;
                    coarseKnown[coarse] = count > 0;
                    coarseValues[coarse] = count > 0 ? sum / count : double.NaN;
                }
            }

            Solve(coarseValues, coarseKnown, coarseDomain, coarseColumns, coarseRows, cancellationToken);

            // Start from the coarse answer, bilinear between the coarse nodes that have one. A coarse node
            // stands for a 2 x 2 block, so its centre is half a fine node in from the block's first.
            for (int j = 0; j < rows; j++)
            {
                double y = Math.Clamp((j - 0.5) / 2.0, 0.0, coarseRows - 1);
                int j0 = Math.Min((int)y, coarseRows - 1), j1 = Math.Min(j0 + 1, coarseRows - 1);
                double ty = y - j0;
                for (int i = 0; i < columns; i++)
                {
                    int node = (j * columns) + i;
                    if (!domain[node] || known[node])
                        continue;
                    double x = Math.Clamp((i - 0.5) / 2.0, 0.0, coarseColumns - 1);
                    int i0 = Math.Min((int)x, coarseColumns - 1), i1 = Math.Min(i0 + 1, coarseColumns - 1);
                    double tx = x - i0;
                    double sum = 0.0, weight = 0.0;
                    Blend(i0, j0, (1 - tx) * (1 - ty));
                    Blend(i1, j0, tx * (1 - ty));
                    Blend(i0, j1, (1 - tx) * ty);
                    Blend(i1, j1, tx * ty);
                    values[node] = weight > 0.0 ? sum / weight : coarseValues[((j / 2) * coarseColumns) + (i / 2)];

                    void Blend(int ci, int cj, double w)
                    {
                        double value = coarseValues[(cj * coarseColumns) + ci];
                        if (w <= 0.0 || double.IsNaN(value))
                            return;
                        sum += w * value;
                        weight += w;
                    }
                }
            }
        }

        Relax(values, known, domain, columns, rows, sweeps, cancellationToken);
    }

    /// <summary>
    /// Over-relaxed Gauss-Seidel over the unknown nodes alone (a house is a tenth of a site, so sweeping the
    /// whole lattice would be ten times the work), until no node moves by more than a ten-millionth of the
    /// known heights' range.
    /// </summary>
    private static void Relax(double[] values, bool[] known, bool[] domain, int columns, int rows, int sweeps, CancellationToken cancellationToken)
    {
        var unknown = new List<int>();
        double low = double.PositiveInfinity, high = double.NegativeInfinity;
        for (int node = 0; node < values.Length; node++)
        {
            if (!domain[node])
                continue;
            if (!known[node])
                unknown.Add(node);
            else if (!double.IsNaN(values[node]))
            {
                low = Math.Min(low, values[node]);
                high = Math.Max(high, values[node]);
            }
        }

        var neighbours = new int[unknown.Count * 4];
        for (int k = 0; k < unknown.Count; k++)
        {
            int node = unknown[k], i = node % columns, j = node / columns;
            neighbours[(k * 4) + 0] = i > 0 && domain[node - 1] ? node - 1 : -1;
            neighbours[(k * 4) + 1] = i < columns - 1 && domain[node + 1] ? node + 1 : -1;
            neighbours[(k * 4) + 2] = j > 0 && domain[node - columns] ? node - columns : -1;
            neighbours[(k * 4) + 3] = j < rows - 1 && domain[node + columns] ? node + columns : -1;
        }

        double tolerance = 1e-7 * Math.Max(1.0, high - low);
        for (int sweep = 0; sweep < sweeps; sweep++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double largestChange = 0.0;
            for (int k = 0; k < unknown.Count; k++)
            {
                double sum = 0.0;
                int count = 0;
                for (int n = k * 4; n < (k * 4) + 4; n++)
                {
                    int neighbour = neighbours[n];
                    if (neighbour < 0 || double.IsNaN(values[neighbour]))
                        continue;
                    sum += values[neighbour];
                    count++;
                }

                if (count == 0)
                    continue;
                int node = unknown[k];
                double mean = sum / count;
                double current = values[node];
                if (double.IsNaN(current))
                {
                    values[node] = mean;
                    largestChange = double.PositiveInfinity;
                    continue;
                }

                double updated = current + (Overrelaxation * (mean - current));
                largestChange = Math.Max(largestChange, Math.Abs(updated - current));
                values[node] = updated;
            }

            if (largestChange < tolerance)
                break;
        }
    }

    private static void SeedWithMean(double[] values, bool[] known, bool[] domain)
    {
        double sum = 0.0;
        int count = 0;
        for (int i = 0; i < values.Length; i++)
        {
            if (domain[i] && known[i])
            {
                sum += values[i];
                count++;
            }
        }

        if (count == 0)
            return;
        double mean = sum / count;
        for (int i = 0; i < values.Length; i++)
        {
            if (domain[i] && !known[i])
                values[i] = mean;
        }
    }
}
