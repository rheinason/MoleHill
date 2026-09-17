using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

public sealed class TerrainReferenceResolverTests
{
    [Fact]
    public void Resolve_DuplicateName_ReportsCandidatesInsteadOfChoosingOne()
    {
        var first = new TerrainDefinition { Name = "Site B" };
        var second = new TerrainDefinition { Name = "site b" };

        TerrainDefinition? resolved = TerrainReferenceResolver.Resolve(
            new[] { first, second }, "SITE B", out string? message);

        Assert.Null(resolved);
        Assert.Contains("ambiguous", message);
        Assert.Contains(first.TerrainId.ToString("D"), message);
        Assert.Contains(second.TerrainId.ToString("D"), message);
    }

    [Fact]
    public void Resolve_Guid_KeepsBindingAfterRename()
    {
        var first = new TerrainDefinition { Name = "Site B" };
        var second = new TerrainDefinition { Name = "Site B" };
        first.Name = "Renamed";

        TerrainDefinition? resolved = TerrainReferenceResolver.Resolve(
            new[] { first, second }, first.TerrainId.ToString("D"), out string? message);

        Assert.Same(first, resolved);
        Assert.Null(message);
    }
}
