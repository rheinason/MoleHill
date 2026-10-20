namespace MoleHill.Core.Scan;

/// <summary>
/// Splits soft ground at its necks: lawns and gardens joined only by strips narrower than a width are outlined
/// as separate areas. On the reference scan one soft outline ran 4.2 km around nearly every garden, joined by
/// verges, as a single curve of 3,241 controls. Cores are what an opening by a disc of the width leaves; every
/// other soft node joins the core its soft ground reaches first, so a strip divides where two fronts meet.
/// The disc matters: a square window measures a diagonal strip 1.4 times wider than it is, and the reference
/// streets run diagonally.
/// </summary>
internal static class ScanSoftNecks
{
    /// <summary>A region per soft node (-1 elsewhere); soft ground reaching no core keeps a region per piece.</summary>
    public static int[] Regions(bool[] soft, int columns, int rows, double radius, int minimumNodes, CancellationToken token)
    {
        var regions = new int[soft.Length];
        Array.Fill(regions, -1);
        bool[] opened = GridMorphology.OpenMaskDisc(soft, columns, rows, radius, token);
        int[] cores = GridMorphology.Label(opened, columns, rows, out List<int> sizes, out _);
        var queue = new int[soft.Length];
        int head = 0, tail = 0;
        for (int node = 0; node < soft.Length; node++)
            if (cores[node] >= 0 && sizes[cores[node]] >= minimumNodes)
            {
                regions[node] = cores[node];
                queue[tail++] = node;
            }
        int next = sizes.Count;
        Spread();
        for (int node = 0; node < soft.Length; node++)
            if (soft[node] && regions[node] < 0)
            {
                regions[node] = next++;
                queue[tail++] = node;
                Spread();
            }
        return regions;

        void Spread()
        {
            while (head < tail)
            {
                if ((head & 0xFFFF) == 0) token.ThrowIfCancellationRequested();
                int node = queue[head++], column = node % columns, row = node / columns;
                if (column > 0) Visit(node - 1, regions[node]);
                if (column < columns - 1) Visit(node + 1, regions[node]);
                if (row > 0) Visit(node - columns, regions[node]);
                if (row < rows - 1) Visit(node + columns, regions[node]);
            }
        }
        void Visit(int node, int region)
        {
            if (!soft[node] || regions[node] >= 0) return;
            regions[node] = region;
            queue[tail++] = node;
        }
    }
}
