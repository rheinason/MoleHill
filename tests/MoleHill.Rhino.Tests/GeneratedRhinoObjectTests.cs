using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino.Geometry;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class GeneratedRhinoObjectTests
{
    [Fact]
    public void TryGetPreviewDisplayText_RepeatedRead_ReusesCombinedText()
    {
        var generated = new GeneratedRhinoObject
        {
            Role = LayerRole.Auxiliary,
            Name = "Marker",
            InstanceUserStrings = new Dictionary<string, string>
            {
                [GeneratedBlockCatalog.PrefixToken] = "(",
                [GeneratedBlockCatalog.ValueToken] = "12.30",
                [GeneratedBlockCatalog.SuffixToken] = " m)"
            }
        };

        Assert.True(generated.TryGetPreviewDisplayText(out string first));
        Assert.True(generated.TryGetPreviewDisplayText(out string second));
        Assert.Equal("(12.30 m)", first);
        Assert.Same(first, second);
    }

    [RhinoNativeFact]
    public void GetPreviewTextEntity_SameSourceAndText_ReusesCachedDuplicate()
    {
        using var source = new TextEntity { RichText = "source" };
        var generated = new GeneratedRhinoObject { Role = LayerRole.Auxiliary, Name = "Marker" };

        TextEntity? first = generated.GetPreviewTextEntity(source, "12.30 m");
        TextEntity? second = generated.GetPreviewTextEntity(source, "12.30 m");

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.NotSame(source, first);
        Assert.Equal("12.30 m", first.RichText);
        Assert.Equal("source", source.RichText);
    }

    [RhinoNativeFact]
    public void GetPreviewTextEntity_DifferentDisplayText_CachesSeparateDuplicates()
    {
        using var source = new TextEntity { RichText = "source" };
        var generated = new GeneratedRhinoObject { Role = LayerRole.Auxiliary, Name = "Marker" };

        TextEntity? first = generated.GetPreviewTextEntity(source, "12.30 m");
        TextEntity? second = generated.GetPreviewTextEntity(source, "13.40 m");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.Equal("12.30 m", first.RichText);
        Assert.Equal("13.40 m", second.RichText);
    }

    [RhinoNativeFact]
    public void GetPreviewBrepMeshes_RepeatedRead_ReusesStableRenderMeshes()
    {
        using Brep brep = new Box(new BoundingBox(Point3d.Origin, new Point3d(10, 1, 3))).ToBrep();
        var generated = new GeneratedRhinoObject { Role = LayerRole.Auxiliary, Name = "Wall" };

        IReadOnlyList<Mesh> first = generated.GetPreviewBrepMeshes(brep);
        IReadOnlyList<Mesh> second = generated.GetPreviewBrepMeshes(brep);

        Assert.NotEmpty(first);
        Assert.Same(first, second);
        Assert.All(first, mesh => Assert.True(mesh.IsValid));
    }
}
