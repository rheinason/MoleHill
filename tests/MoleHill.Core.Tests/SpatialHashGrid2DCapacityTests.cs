using MoleHill.Core.Engine;
using Xunit;
using Xunit.Abstractions;

namespace MoleHill.Core.Tests;

/// <summary>
/// R04. The index registers an item into every cell its bounding box covers, so total *memberships* —
/// not item count — sizes the flattened CSR array. Long diagonals, a huge box among small ones, or
/// heavily overlapping bounds over a fine grid make that grow super-linearly, and it used to be summed
/// into an <c>int</c> and allocated unchecked.
///
/// These fixtures are the distributions that stress it. Each one asserts two things: the indexed answer
/// still matches brute force (the guard must not change what a query returns), and the membership count
/// stays inside the declared budget.
/// </summary>
public class SpatialHashGrid2DCapacityTests
{
    private readonly ITestOutputHelper output;

    public SpatialHashGrid2DCapacityTests(ITestOutputHelper output) => this.output = output;

    private static List<int> BruteForce(Bounds2D[] bounds, Bounds2D query, bool[]? valid = null)
    {
        var hits = new List<int>();
        for (int i = 0; i < bounds.Length; i++)
        {
            if (valid != null && !valid[i])
                continue;

            if (!double.IsFinite(bounds[i].MinX) || !double.IsFinite(bounds[i].MaxX) ||
                !double.IsFinite(bounds[i].MinY) || !double.IsFinite(bounds[i].MaxY) ||
                bounds[i].MinX > bounds[i].MaxX || bounds[i].MinY > bounds[i].MaxY)
            {
                continue;
            }

            if (bounds[i].Intersects(query))
                hits.Add(i);
        }

        return hits;
    }

    /// <summary>
    /// The candidates an indexed query yields, filtered by the exact bounds test every caller applies.
    /// Sorted, because a caller that depends on order sorts (the zone splitter does so explicitly) and
    /// coarsening legitimately changes the order candidates are first seen in.
    /// </summary>
    private static List<int> Indexed(SpatialHashGrid2D grid, Bounds2D[] bounds, Bounds2D query)
    {
        var candidates = new List<int>();
        grid.GatherCandidates(query, candidates, new SpatialHashGrid2D.QueryScratch(bounds.Length));
        var hits = new List<int>();
        foreach (int i in candidates)
        {
            if (bounds[i].Intersects(query))
                hits.Add(i);
        }

        hits.Sort();
        return hits;
    }

    private void AssertMatchesBruteForce(string label, Bounds2D[] bounds, Bounds2D[] queries, bool[]? valid = null)
    {
        SpatialHashGrid2D grid = SpatialHashGrid2D.Build(bounds, valid, null, out SpatialHashGrid2D.BuildStatistics stats);

        output.WriteLine(
            $"[{label}] items={stats.ItemCount:N0} indexed={stats.IndexedItemCount:N0} " +
            $"cells={stats.CellCount:N0} memberships={stats.Memberships:N0} " +
            $"({(stats.IndexedItemCount == 0 ? 0 : (double)stats.Memberships / stats.IndexedItemCount):0.00}/item) " +
            $"maxOccupancy={stats.MaxCellOccupancy:N0} cellSize={stats.CellSize:0.####} " +
            $"coarsenings={stats.CoarseningPasses}");

        long budget = Math.Max(
            (long)stats.IndexedItemCount * SpatialHashGrid2D.MembershipsPerItemBudget,
            SpatialHashGrid2D.MinimumMembershipBudget);
        Assert.True(
            stats.Memberships <= budget,
            $"[{label}] memberships {stats.Memberships:N0} exceeded the budget {budget:N0}.");

        foreach (Bounds2D query in queries)
        {
            List<int> expected = BruteForce(bounds, query, valid);
            List<int> actual = Indexed(grid, bounds, query);
            Assert.Equal(expected, actual);
        }
    }

    private static Bounds2D[] Queries(double extent)
    {
        return new[]
        {
            new Bounds2D(0, extent, 0, extent),
            new Bounds2D(extent * 0.25, extent * 0.35, extent * 0.25, extent * 0.35),
            new Bounds2D(-extent, extent * 2, -extent, extent * 2),
            new Bounds2D(extent * 0.5, extent * 0.5, extent * 0.5, extent * 0.5),
            new Bounds2D(extent * 10, extent * 11, extent * 10, extent * 11),
        };
    }

    [Fact]
    public void Build_UniformBounds_MatchesBruteForceAndStaysInBudget()
    {
        const int side = 100;
        var bounds = new Bounds2D[side * side];
        for (int j = 0; j < side; j++)
        {
            for (int i = 0; i < side; i++)
                bounds[(j * side) + i] = new Bounds2D(i, i + 0.9, j, j + 0.9);
        }

        AssertMatchesBruteForce("uniform", bounds, Queries(side));
    }

    [Fact]
    public void Build_ClusteredBounds_MatchesBruteForceAndStaysInBudget()
    {
        var random = new Random(20260919);
        var bounds = new Bounds2D[8000];
        for (int i = 0; i < bounds.Length; i++)
        {
            // Ten tight clusters in a large empty extent: most cells empty, a few very crowded.
            double cx = (i % 10) * 1000.0;
            double cy = (i % 7) * 900.0;
            double x = cx + (random.NextDouble() * 2.0);
            double y = cy + (random.NextDouble() * 2.0);
            bounds[i] = new Bounds2D(x, x + 0.1, y, y + 0.1);
        }

        AssertMatchesBruteForce("clustered", bounds, Queries(10000));
    }

    [Fact]
    public void Build_LongDiagonalBounds_MatchesBruteForceAndStaysInBudget()
    {
        // The classic membership blow-up: every box spans the whole extent in both axes, so on a fine
        // grid each one is registered into every cell.
        var bounds = new Bounds2D[4000];
        for (int i = 0; i < bounds.Length; i++)
        {
            double t = i / (double)bounds.Length;
            bounds[i] = new Bounds2D(t * 10.0, 4000.0, t * 10.0, 4000.0);
        }

        AssertMatchesBruteForce("long diagonal", bounds, Queries(4000));
    }

    [Fact]
    public void Build_MixedTinyAndHugeBounds_MatchesBruteForceAndStaysInBudget()
    {
        var bounds = new Bounds2D[6000];
        for (int i = 0; i < bounds.Length; i++)
        {
            if (i % 500 == 0)
            {
                // A handful of boxes covering everything, among thousands of specks.
                bounds[i] = new Bounds2D(0, 6000, 0, 6000);
                continue;
            }

            double x = i % 600;
            double y = i / 600.0;
            bounds[i] = new Bounds2D(x, x + 0.001, y, y + 0.001);
        }

        AssertMatchesBruteForce("mixed tiny/huge", bounds, Queries(6000));
    }

    [Fact]
    public void Build_DegenerateExtents_MatchesBruteForce()
    {
        // Zero-area boxes, a single repeated point, and entries the caller marked invalid.
        var bounds = new Bounds2D[200];
        var valid = new bool[bounds.Length];
        for (int i = 0; i < bounds.Length; i++)
        {
            bounds[i] = new Bounds2D(5, 5, 7, 7);
            valid[i] = i % 3 != 0;
        }

        AssertMatchesBruteForce("degenerate", bounds, Queries(10), valid);
    }

    [Fact]
    public void Build_NonFiniteAndInvertedBounds_AreSkippedNotIndexed()
    {
        var bounds = new[]
        {
            new Bounds2D(0, 1, 0, 1),
            new Bounds2D(double.NaN, 1, 0, 1),
            new Bounds2D(0, double.PositiveInfinity, 0, 1),
            new Bounds2D(5, 4, 0, 1),
            new Bounds2D(2, 3, 2, 3),
        };

        SpatialHashGrid2D grid = SpatialHashGrid2D.Build(bounds, null, null, out SpatialHashGrid2D.BuildStatistics stats);

        Assert.Equal(5, stats.ItemCount);
        Assert.Equal(2, stats.IndexedItemCount);
        Assert.Equal(new[] { 0, 4 }, Indexed(grid, bounds, new Bounds2D(-10, 10, -10, 10)));
    }

    [Fact]
    public void Build_EmptyAndAllInvalidInputs_ReportZeroedStatistics()
    {
        SpatialHashGrid2D empty = SpatialHashGrid2D.Build(
            Array.Empty<Bounds2D>(), null, null, out SpatialHashGrid2D.BuildStatistics emptyStats);
        Assert.Equal(0, emptyStats.ItemCount);
        Assert.Equal(0, emptyStats.Memberships);
        Assert.Empty(Indexed(empty, Array.Empty<Bounds2D>(), new Bounds2D(0, 1, 0, 1)));

        var bounds = new[] { new Bounds2D(0, 1, 0, 1) };
        SpatialHashGrid2D none = SpatialHashGrid2D.Build(
            bounds, new[] { false }, null, out SpatialHashGrid2D.BuildStatistics noneStats);
        Assert.Equal(1, noneStats.ItemCount);
        Assert.Equal(0, noneStats.IndexedItemCount);
        Assert.Empty(Indexed(none, bounds, new Bounds2D(0, 1, 0, 1)));
    }

    [Fact]
    public void Build_OrdinaryDistribution_IsNotCoarsened()
    {
        // The budget is a guard, not a tuning knob: a normal terrain-shaped distribution must take the
        // first pass untouched, or the guard has quietly become a behaviour change.
        const int side = 200;
        var bounds = new Bounds2D[side * side];
        for (int j = 0; j < side; j++)
        {
            for (int i = 0; i < side; i++)
                bounds[(j * side) + i] = new Bounds2D(i, i + 1.0, j, j + 1.0);
        }

        SpatialHashGrid2D.Build(bounds, null, null, out SpatialHashGrid2D.BuildStatistics stats);

        output.WriteLine($"ordinary: memberships/item={(double)stats.Memberships / stats.IndexedItemCount:0.00}");
        Assert.Equal(0, stats.CoarseningPasses);
        Assert.True(stats.Memberships < (long)stats.IndexedItemCount * 8, "Ordinary bounds should register a few cells each.");
    }

    [Fact]
    public void Build_MembershipsScaleLinearlyAcrossDoublingSizes()
    {
        // Records memberships and cell counts across doubling item counts, so growth is measured rather
        // than assumed. Asserted loosely: the point is to catch a super-linear regression, not to pin a
        // constant.
        double previousPerItem = 0;
        foreach (int side in new[] { 64, 128, 256 })
        {
            var bounds = new Bounds2D[side * side];
            for (int j = 0; j < side; j++)
            {
                for (int i = 0; i < side; i++)
                    bounds[(j * side) + i] = new Bounds2D(i, i + 1.0, j, j + 1.0);
            }

            SpatialHashGrid2D.Build(bounds, null, null, out SpatialHashGrid2D.BuildStatistics stats);
            double perItem = (double)stats.Memberships / stats.IndexedItemCount;
            output.WriteLine(
                $"side={side} items={stats.IndexedItemCount:N0} cells={stats.CellCount:N0} " +
                $"memberships={stats.Memberships:N0} perItem={perItem:0.00} " +
                $"bytes~={stats.Memberships * sizeof(int):N0}");

            Assert.True(perItem < 16, $"memberships/item {perItem:0.00} at side={side} is not linear-ish.");
            if (previousPerItem > 0)
                Assert.True(perItem < previousPerItem * 4, "memberships/item grew super-linearly across a doubling.");

            previousPerItem = perItem;
        }
    }
}
