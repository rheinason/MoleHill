using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// Keeps the modifier cards speaking one language. A curve that the mesh keeps is a "breakline" on every
/// card, a closed curve that scopes a region is a "boundary", the mesh's outer edge is its "border", and
/// the stage input is the "incoming mesh". The vocabulary drifted once (Sculpt, Remesh and Retopo called
/// their curves "Constraints" while Smooth called the same idea "Breaklines"), so the retired words are
/// pinned here. Storage keys keep their old names for saved documents; only user-facing text is checked.
/// </summary>
public class ModifierVocabularyGuardTests
{
    private static readonly Regex RetiredTerms = new(
        @"\b(constraints?|features?|pinned|fixity|upstream|TIN boundary)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static TheoryData<string, string, string> AllModifierText()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var descriptor in TerrainTypeRegistry.Modifiers)
        {
            data.Add(descriptor.Kind, "Subtitle", descriptor.Subtitle);
            foreach (var parameter in descriptor.Parameters)
            {
                data.Add(descriptor.Kind, $"{parameter.Key} label", parameter.Label);
                if (!string.IsNullOrWhiteSpace(parameter.Help))
                    data.Add(descriptor.Kind, $"{parameter.Key} help", parameter.Help!);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllModifierText))]
    public void ModifierText_AvoidsRetiredTerms(string kind, string field, string text)
    {
        Match match = RetiredTerms.Match(text);
        Assert.False(match.Success, $"{kind} {field} uses the retired term '{match.Value}': \"{text}\"");
    }

    [Theory]
    [InlineData("remesh", "Constraints")]
    [InlineData("retopo", "Constraints")]
    public void CurvesTheMeshKeeps_AreLabelledBreaklines(string kind, string key)
    {
        var descriptor = TerrainTypeRegistry.Modifiers.Single(
            item => string.Equals(item.Kind, kind, StringComparison.OrdinalIgnoreCase));
        var parameter = descriptor.Parameters.Single(item => item.Key == key);

        Assert.Equal("Breaklines", parameter.Label);
    }

    [Theory]
    [InlineData("sculpt", "Constraints")]
    [InlineData("smooth", "Breaklines")]
    public void CurvesThatShieldTerrain_AreLabelledProtect(string kind, string key)
    {
        var descriptor = TerrainTypeRegistry.Modifiers.Single(
            item => string.Equals(item.Kind, kind, StringComparison.OrdinalIgnoreCase));

        Assert.Equal("Protect", descriptor.Parameters.Single(item => item.Key == key).Label);
    }
}
