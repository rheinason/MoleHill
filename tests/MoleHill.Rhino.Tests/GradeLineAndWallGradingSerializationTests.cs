using System.Linq;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// The document contract for grading from a line, and for the retaining wall's new graded mode. The
/// one that matters most is the last: a wall saved before grading existed carries no <c>mode</c> key,
/// and it must still read back as breaklines only.
/// </summary>
public class GradeLineAndWallGradingSerializationTests
{
    private static T RoundTrip<T>(T modifier) where T : ModifierDefinition
    {
        var terrain = new TerrainDefinition();
        terrain.Modifiers.Add(modifier);
        string json = TerrainSerializer.Serialize(new[] { terrain });
        return Restore<T>(json);
    }

    /// <summary>Deserializing seeds a Triangulate modifier, so take the one that was added.</summary>
    private static T Restore<T>(string json) where T : ModifierDefinition =>
        Assert.IsType<T>(Assert.Single(TerrainSerializer.Deserialize(json)).Modifiers.Last());

    [Fact]
    public void Registry_KnowsTheGradeLineKind()
    {
        ModifierTypeDescriptor? descriptor = TerrainTypeRegistry.ForModifierKind("grade-line");

        Assert.NotNull(descriptor);
        Assert.Equal(typeof(GradeLineModifierDefinition), descriptor!.DefinitionType);
        Assert.Same(descriptor, TerrainTypeRegistry.ForModifierType(typeof(GradeLineModifierDefinition)));
    }

    [Fact]
    public void GradeLine_RoundTripsItsSlopesAndSides()
    {
        var restored = RoundTrip(new GradeLineModifierDefinition
        {
            SlopeAngle = 26.0,
            CutSlopeAngle = 40.0,
            UseAsymmetricSides = true,
            LeftCutSlopeAngle = 60.0,
            LeftFillSlopeAngle = 18.0,
            RightCutSlopeAngle = 22.0,
            RightFillSlopeAngle = 12.0,
            MaxDistance = 7.5
        });

        Assert.Equal(26.0, restored.SlopeAngle);
        Assert.Equal(40.0, restored.CutSlopeAngle);
        Assert.True(restored.UseAsymmetricSides);
        Assert.Equal(60.0, restored.LeftCutSlopeAngle);
        Assert.Equal(18.0, restored.LeftFillSlopeAngle);
        Assert.Equal(22.0, restored.RightCutSlopeAngle);
        Assert.Equal(12.0, restored.RightFillSlopeAngle);
        Assert.Equal(7.5, restored.MaxDistance);
    }

    [Fact]
    public void GradeLine_DefaultsToASymmetricSection()
    {
        var fresh = Assert.IsType<GradeLineModifierDefinition>(
            TerrainTypeRegistry.ForModifierKind("grade-line")!.Create(global::Rhino.UnitSystem.Meters));

        Assert.False(fresh.UseAsymmetricSides);
        Assert.Equal(0.0, fresh.LeftCutSlopeAngle);
        Assert.Equal(0.0, fresh.RightCutSlopeAngle);
    }

    [Fact]
    public void RetainingWall_DefaultsToBreaklinesOnly()
    {
        var fresh = new RetainingWallModifierDefinition();

        Assert.Equal(RetainingWallModifierDefinition.BreaklineOnlyMode, fresh.Mode);
        Assert.False(fresh.GradesTerrain);
    }

    /// <summary>
    /// The compatibility case. A wall written before the graded mode existed has no "mode" property at
    /// all; deserializing it must leave the modifier on the breakline-only path rather than silently
    /// starting to move terrain in an existing document.
    /// </summary>
    [Fact]
    public void RetainingWall_JsonWithoutAMode_StaysBreaklinesOnly()
    {
        var terrain = new TerrainDefinition();
        terrain.Modifiers.Add(new RetainingWallModifierDefinition { MaxWallWidth = 1.25 });
        string json = TerrainSerializer.Serialize(new[] { terrain });

        // Strip the mode the current writer emits, reproducing an older document exactly.
        string legacy = json.Replace($"\"mode\":\"{RetainingWallModifierDefinition.BreaklineOnlyMode}\",", string.Empty)
                            .Replace($",\"mode\":\"{RetainingWallModifierDefinition.BreaklineOnlyMode}\"", string.Empty);

        RetainingWallModifierDefinition restored = Restore<RetainingWallModifierDefinition>(legacy);

        Assert.Equal(RetainingWallModifierDefinition.BreaklineOnlyMode, restored.Mode);
        Assert.False(restored.GradesTerrain);
        Assert.Equal(1.25, restored.MaxWallWidth);
    }

    [Fact]
    public void RetainingWall_RoundTripsTheGradedMode()
    {
        var restored = RoundTrip(new RetainingWallModifierDefinition
        {
            MaxWallWidth = 2.0,
            Mode = RetainingWallModifierDefinition.GradeMode,
            SlopeAngle = 30.0,
            CutSlopeAngle = 45.0,
            UseAsymmetricSides = true,
            ToeCutSlopeAngle = 50.0,
            ToeFillSlopeAngle = 20.0,
            TopCutSlopeAngle = 70.0,
            TopFillSlopeAngle = 25.0,
            MaxDistance = 12.0
        });

        Assert.True(restored.GradesTerrain);
        Assert.Equal(30.0, restored.SlopeAngle);
        Assert.Equal(50.0, restored.ToeCutSlopeAngle);
        Assert.Equal(70.0, restored.TopCutSlopeAngle);
        Assert.Equal(12.0, restored.MaxDistance);
    }

    /// <summary>
    /// Slope angles are angles; only the reach is a model length. Scaling a document must not re-slope
    /// the batters.
    /// </summary>
    [Fact]
    public void UnitScaling_MovesTheReachAndLeavesTheAnglesAlone()
    {
        var line = new GradeLineModifierDefinition { SlopeAngle = 33.0, LeftCutSlopeAngle = 60.0, MaxDistance = 4.0 };
        var wall = new RetainingWallModifierDefinition { MaxWallWidth = 1.0, SlopeAngle = 33.0, MaxDistance = 4.0 };

        TerrainUnitScaler.Scale(line, 1000.0);
        TerrainUnitScaler.Scale(wall, 1000.0);

        Assert.Equal(33.0, line.SlopeAngle);
        Assert.Equal(60.0, line.LeftCutSlopeAngle);
        Assert.Equal(4000.0, line.MaxDistance);
        Assert.Equal(33.0, wall.SlopeAngle);
        Assert.Equal(1000.0, wall.MaxWallWidth);
        Assert.Equal(4000.0, wall.MaxDistance);
    }
}
