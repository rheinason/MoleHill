using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class DrainagePreviewCacheTests
{
    private static readonly DrainagePreviewKey Ponding = new("ponding", 0.005, 0.0, 0.05);

    [Fact]
    public void GetOrCompute_SameMeshAndSettings_SolvesOnce()
    {
        var cache = new DrainagePreviewCache();
        var mesh = new object();

        string first = cache.GetOrCompute(mesh, Ponding, () => "solved");
        string second = cache.GetOrCompute(mesh, Ponding, () => "solved again");

        Assert.Same(first, second);
        Assert.Equal(1, cache.ComputeCount);
    }

    [Fact]
    public void GetOrCompute_NewMeshInstance_Resolves()
    {
        // A rebuild publishes a new mesh; an equal-looking one is still a different terrain.
        var cache = new DrainagePreviewCache();
        cache.GetOrCompute(new object(), Ponding, () => "a");
        string next = cache.GetOrCompute(new object(), Ponding, () => "b");

        Assert.Equal("b", next);
        Assert.Equal(2, cache.ComputeCount);
    }

    [Theory]
    [InlineData("catchments", 0.005, 0.0, 0.05)]
    [InlineData("ponding", 0.01, 0.0, 0.05)]
    [InlineData("ponding", 0.005, 0.01, 0.05)]
    [InlineData("ponding", 0.005, 0.0, 0.10)]
    public void GetOrCompute_AnySolveSettingChanged_Resolves(string kind, double flat, double merge, double depth)
    {
        var cache = new DrainagePreviewCache();
        var mesh = new object();
        cache.GetOrCompute(mesh, Ponding, () => "a");
        cache.GetOrCompute(mesh, new DrainagePreviewKey(kind, flat, merge, depth), () => "b");

        Assert.Equal(2, cache.ComputeCount);
    }

    [Fact]
    public void Clear_SameMeshMutatedInPlace_Resolves()
    {
        // A sculpt stroke moves vertices of the same instance and clears the cache.
        var cache = new DrainagePreviewCache();
        var mesh = new object();
        cache.GetOrCompute(mesh, Ponding, () => "before");
        cache.Clear();
        string after = cache.GetOrCompute(mesh, Ponding, () => "after");

        Assert.Equal("after", after);
        Assert.Equal(2, cache.ComputeCount);
    }
}
