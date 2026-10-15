using System.Linq;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// The document contract for Smooth's Round Creases mode. A Smooth saved before the mode existed carries
/// no <c>Mode</c> key and must still read back as plain smoothing.
/// </summary>
public class SmoothRoundCreasesSerializationTests
{
    private static SmoothModifierDefinition RoundTrip(SmoothModifierDefinition modifier)
    {
        var terrain = new TerrainDefinition();
        terrain.Modifiers.Add(modifier);
        string json = TerrainSerializer.Serialize(new[] { terrain });
        return Assert.IsType<SmoothModifierDefinition>(Assert.Single(TerrainSerializer.Deserialize(json)).Modifiers.Last());
    }

    [Fact]
    public void RoundMode_RoundTripsLengthAndAngle()
    {
        var restored = RoundTrip(new SmoothModifierDefinition
        {
            Mode = SmoothModes.Round,
            RoundingLength = 3.5,
            CreaseAngle = 14.0
        });

        Assert.True(SmoothModes.IsRound(restored));
        Assert.Equal(3.5, restored.RoundingLength);
        Assert.Equal(14.0, restored.CreaseAngle);
    }

    [Fact]
    public void LegacySmooth_WithoutModeKey_ReadsAsSmooth()
    {
        var terrain = new TerrainDefinition();
        terrain.Modifiers.Add(new SmoothModifierDefinition { Iterations = 4 });
        string json = TerrainSerializer.Serialize(new[] { terrain });
        string legacy = System.Text.RegularExpressions.Regex.Replace(
            json,
            "\"(?:Mode|mode)\"\\s*:\\s*\"smooth\"\\s*,?",
            string.Empty);
        Assert.True(legacy.Length < json.Length, "the Mode key was not found to strip");

        var restored = Assert.IsType<SmoothModifierDefinition>(Assert.Single(TerrainSerializer.Deserialize(legacy)).Modifiers.Last());

        Assert.False(SmoothModes.IsRound(restored));
        Assert.Equal(4, restored.Iterations);
    }

    [Fact]
    public void Create_SetsAMetricRoundingLength_InTheDocumentUnit()
    {
        var metres = Assert.IsType<SmoothModifierDefinition>(
            TerrainTypeRegistry.ForModifierKind("smooth")!.Create(global::Rhino.UnitSystem.Meters));
        var millimetres = Assert.IsType<SmoothModifierDefinition>(
            TerrainTypeRegistry.ForModifierKind("smooth")!.Create(global::Rhino.UnitSystem.Millimeters));

        Assert.False(SmoothModes.IsRound(metres));
        Assert.Equal(2.0, metres.RoundingLength, 9);
        Assert.Equal(2000.0, millimetres.RoundingLength, 6);
    }

    [Fact]
    public void RoundMode_WithoutLength_IsInert()
    {
        ModifierTypeDescriptor descriptor = TerrainTypeRegistry.ForModifierKind("smooth")!;

        Assert.NotNull(descriptor.InertReason(new SmoothModifierDefinition { Mode = SmoothModes.Round, RoundingLength = 0.0 }, null!));
        Assert.Null(descriptor.InertReason(new SmoothModifierDefinition { Mode = SmoothModes.Round, RoundingLength = 1.0 }, null!));
        Assert.Null(descriptor.InertReason(new SmoothModifierDefinition { RoundingLength = 0.0 }, null!));
    }
}
