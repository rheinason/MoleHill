using MoleHill.Core.Processing;
using Xunit;

namespace MoleHill.Core.Tests;

public sealed class ZonePriorityResolverTests
{
    [Fact]
    public void SortInPlace_ElevationRun_OrdersByElevationBeforeStack()
    {
        var entries = new List<ZonePriorityResolver.BoundaryPriority>
        {
            new(0, 0, 12, true),
            new(2, 0, 5, true)
        };

        ZonePriorityResolver.SortInPlace(entries, item => item);

        Assert.Equal(new[] { 2, 0 }, entries.Select(item => item.ZoneOrder));
    }

    [Fact]
    public void SortInPlace_MixedModes_PreservesStackBoundaryAndTransitiveOrder()
    {
        var entries = new List<ZonePriorityResolver.BoundaryPriority>
        {
            new(2, 0, 0, true),
            new(0, 0, 100, true),
            new(1, 0, 50, false)
        };

        ZonePriorityResolver.SortInPlace(entries, item => item);

        Assert.Equal(new[] { 0, 1, 2 }, entries.Select(item => item.ZoneOrder));
    }

    [Fact]
    public void SortInPlace_EqualElevation_UsesSourceThenStack()
    {
        var entries = new List<ZonePriorityResolver.BoundaryPriority>
        {
            new(1, 1, 5, true),
            new(2, 0, 5, true),
            new(0, 0, 5, true)
        };

        ZonePriorityResolver.SortInPlace(entries, item => item);

        Assert.Equal(new[] { 0, 2, 1 }, entries.Select(item => item.ZoneOrder));
    }
}
