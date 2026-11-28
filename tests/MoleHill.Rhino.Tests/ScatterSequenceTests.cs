using System.Linq;
using MoleHill.Core.Scattering;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// Scatter's Sequence view (design review Step 61): along a curve in Sequence, the checkbox alone decides
/// whether a block is in the repeating cycle, rows drag to reorder, and weights stay stored for Random.
/// </summary>
public class ScatterSequenceTests
{
    private static ScatterObjectDefinition Curve(ScatterBlockOrder order, params (string Name, double Weight, bool Enabled)[] blocks) => new()
    {
        SourceMode = ScatterSourceMode.Curve,
        BlockOrder = order,
        Blocks = blocks.Select(b => new ScatterBlockEntry { BlockDefinitionName = b.Name, Weight = b.Weight, IsEnabled = b.Enabled }).ToList(),
    };

    [Fact]
    public void Sequence_IgnoresWeight_TheCheckboxDecides()
    {
        var scatter = Curve(ScatterBlockOrder.Sequence, ("A", 5, true), ("B", 0, true), ("C", 2, false));

        Assert.Equal(new[] { 1.0, 1.0, 0.0 }, scatter.Blocks.Select(scatter.EffectiveWeight));
    }

    [Fact]
    public void Random_UsesTheWeights_AndZeroStillLeavesABlockOut()
    {
        var scatter = Curve(ScatterBlockOrder.Random, ("A", 5, true), ("B", 0, true), ("C", 2, false));

        Assert.Equal(new[] { 5.0, 0.0, 0.0 }, scatter.Blocks.Select(scatter.EffectiveWeight));
    }

    [Fact]
    public void Sequence_AppliesOnlyAlongACurve()
    {
        var region = Curve(ScatterBlockOrder.Sequence, ("A", 3, true));
        region.SourceMode = ScatterSourceMode.Region;

        Assert.False(region.CyclesInSequence);
        Assert.Equal(3.0, region.EffectiveWeight(region.Blocks[0]));
    }

    [Theory]
    [InlineData(0, 2, "BACD")] // down: lands before the row it was dropped on
    [InlineData(3, 1, "ADBC")] // up
    [InlineData(1, 4, "ACDB")] // onto the tail: to the end
    [InlineData(2, 2, "ABCD")] // onto itself
    [InlineData(2, 3, "ABCD")] // just below itself: already there
    [InlineData(5, 0, "ABCD")] // out of range
    public void MoveToIndex_PutsTheBlockBeforeTheDropRow(int from, int before, string expected)
    {
        var items = "ABCD".ToList();

        StackReorder.MoveToIndex(items, from, before);

        Assert.Equal(expected, new string(items.ToArray()));
    }

    private const string Schema33 = """
        {
          "schemaVersion": 33,
          "terrains": [
            {
              "schemaVersion": 33,
              "terrainId": "22222222-2222-2222-2222-222222222222",
              "name": "Site",
              "objects": [
                {
                  "$type": "scatter",
                  "id": "30000000-0000-0000-0000-000000000001",
                  "name": "Hedge",
                  "sourceMode": 1,
                  "blockOrder": 1,
                  "blocks": [
                    { "blockDefinitionName": "Beech", "weight": 2 },
                    { "blockDefinitionName": "Holly", "weight": 0 },
                    { "blockDefinitionName": "Yew", "weight": 1 }
                  ]
                },
                {
                  "$type": "scatter",
                  "id": "30000000-0000-0000-0000-000000000002",
                  "name": "Verge",
                  "sourceMode": 1,
                  "blockOrder": 0,
                  "blocks": [
                    { "blockDefinitionName": "Grass", "weight": 0 },
                    { "blockDefinitionName": "Clover", "weight": 1 }
                  ]
                },
                {
                  "$type": "scatter",
                  "id": "30000000-0000-0000-0000-000000000003",
                  "name": "Meadow",
                  "sourceMode": 0,
                  "blockOrder": 1,
                  "blocks": [
                    { "blockDefinitionName": "Daisy", "weight": 0 }
                  ]
                }
              ]
            }
          ]
        }
        """;

    private static ScatterObjectDefinition Load(int index) =>
        Assert.IsType<ScatterObjectDefinition>(TerrainSerializer.Deserialize(Schema33).Single().Objects[index]);

    /// <summary>
    /// A sequence saved before schema 34 left Holly out with weight 0. Weight no longer counts in a
    /// sequence, so Holly is unticked instead - the cycle stays Beech, Yew - and every weight is kept.
    /// </summary>
    [Fact]
    public void SavedSequence_ZeroWeightBlock_IsUntickedAndTheCycleIsUnchanged()
    {
        var hedge = Load(0);

        Assert.Equal(new[] { true, false, true }, hedge.Blocks.Select(b => b.IsEnabled));
        Assert.Equal(new[] { 2.0, 0.0, 1.0 }, hedge.Blocks.Select(b => b.Weight));
        Assert.Equal(
            new[] { "Beech", "Yew" },
            hedge.Blocks.Where(b => hedge.EffectiveWeight(b) > 0).Select(b => b.BlockDefinitionName));
    }

    [Fact]
    public void SavedRandomMix_AndRegionScatter_AreLeftAlone()
    {
        Assert.All(Load(1).Blocks, block => Assert.True(block.IsEnabled));
        Assert.True(Assert.Single(Load(2).Blocks).IsEnabled);
    }

    [Fact]
    public void ComputedMembers_AreNotSaved()
    {
        var terrain = new TerrainDefinition();
        terrain.Objects.Add(Curve(ScatterBlockOrder.Sequence, ("A", 1, true)));
        terrain.Annotations.Add(new SpotHeightsAnnotationDefinition());

        string json = TerrainSerializer.Serialize(new[] { terrain });

        Assert.DoesNotContain("cyclesInSequence", json);
        Assert.DoesNotContain("activeSources", json);
    }
}
