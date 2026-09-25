using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class DocumentNorthTests
{
    [Fact]
    public void TerrainsToRefreshAfterChange_LiveTerrainWithEnabledAspect_IsRefreshed()
    {
        var aspect = new TerrainDefinition();
        aspect.Analyses.Add(new AspectAnalysisDefinition());

        IReadOnlyList<Guid> ids = DocumentNorth.TerrainsToRefreshAfterChange(new[] { aspect });

        Assert.Equal(new[] { aspect.TerrainId }, ids);
    }

    [Fact]
    public void TerrainsToRefreshAfterChange_NoAspectDisabledAspectOrManualTerrain_AreLeftAlone()
    {
        var plain = new TerrainDefinition();
        plain.Analyses.Add(new SlopeAnalysisDefinition());

        var disabled = new TerrainDefinition();
        disabled.Analyses.Add(new AspectAnalysisDefinition { IsEnabled = false });

        var manual = new TerrainDefinition { LiveUpdateEnabled = false };
        manual.Analyses.Add(new AspectAnalysisDefinition());

        Assert.Empty(DocumentNorth.TerrainsToRefreshAfterChange(new[] { plain, disabled, manual }));
    }
}
