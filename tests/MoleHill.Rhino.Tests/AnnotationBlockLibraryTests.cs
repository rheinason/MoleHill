using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class AnnotationBlockLibraryTests
{
    private static string Field(string key, string prompt = "", string fallback = "") =>
        $@"%<UserText(""block"",""{key}"",""{prompt}"",""{fallback}"")>%";

    [Fact]
    public void TryResolveFields_FillsEachFieldFromThePayload()
    {
        var values = new Dictionary<string, string> { ["Prefix"] = "EL ", ["VALUE"] = "12.3", ["Suffix"] = " m" };

        bool resolved = AnnotationBlockLibrary.TryResolveFields(
            Field("Prefix") + Field("VALUE") + Field("Suffix"), values, out string text);

        Assert.True(resolved);
        Assert.Equal("EL 12.3 m", text);
    }

    [Fact]
    public void TryResolveFields_KeepsFixedTextBesideFields()
    {
        var values = new Dictionary<string, string> { ["VALUE"] = "4.0" };

        bool resolved = AnnotationBlockLibrary.TryResolveFields("Slope: " + Field("VALUE") + " (design)", values, out string text);

        Assert.True(resolved);
        Assert.Equal("Slope: 4.0 (design)", text);
    }

    [Fact]
    public void TryResolveFields_TextWithoutFields_IsLeftAlone()
    {
        bool resolved = AnnotationBlockLibrary.TryResolveFields("N", new Dictionary<string, string>(), out string text);

        Assert.False(resolved);
        Assert.Equal("N", text);
    }

    [Fact]
    public void TryResolveFields_KeyMatchIsCaseInsensitive()
    {
        var values = new Dictionary<string, string> { ["value"] = "9" };

        Assert.True(AnnotationBlockLibrary.TryResolveFields(Field("VALUE"), values, out string text));
        Assert.Equal("9", text);
    }

    [Fact]
    public void TryResolveFields_MissingKey_UsesTheFieldDefault()
    {
        Assert.True(AnnotationBlockLibrary.TryResolveFields(
            Field("Name", "Label", "n/a"), new Dictionary<string, string>(), out string text));
        Assert.Equal("n/a", text);
    }

    [Fact]
    public void TryResolveFields_RichTextValue_IsEscaped()
    {
        var values = new Dictionary<string, string> { ["VALUE"] = @"a{b}\c" };

        Assert.True(AnnotationBlockLibrary.TryResolveFields(@"{\rtf1 " + Field("VALUE") + "}", values, out string text));
        Assert.Equal(@"{\rtf1 a\{b\}\\c}", text);
    }

    [Fact]
    public void TryResolveFields_ReadsTheFormulaTheDefaultBlockUses()
    {
        // FieldFormula is what the card tells users to type; it has to be something the preview reads back.
        var values = new Dictionary<string, string> { ["Distance"] = "7.50" };

        Assert.True(AnnotationBlockLibrary.TryResolveFields(
            AnnotationBlockLibrary.FieldFormula("Distance"), values, out string text));
        Assert.Equal("7.50", text);
    }

    [Fact]
    public void Classify_SplitsKnownFromUnknownKeys()
    {
        AnnotationBlockFieldReport report = AnnotationBlockLibrary.Classify(new[] { "value", "Prefix", "Colour", "VALUE" });

        Assert.Equal(new[] { "VALUE", "Prefix" }, report.Recognised);
        Assert.Equal(new[] { "Colour" }, report.Unrecognised);
        Assert.True(report.ShowsValue);
    }

    [Fact]
    public void Classify_PrefixAndSuffixAlone_ShowNoValue()
    {
        AnnotationBlockFieldReport report = AnnotationBlockLibrary.Classify(new[] { "Prefix", "Suffix" });

        Assert.False(report.ShowsValue);
        Assert.Contains("no number is drawn", AnnotationBlockLibrary.Describe(report, hasDefinition: true));
    }

    [Fact]
    public void Describe_DisplayFieldAlone_CountsAsShowingAValue()
    {
        Assert.True(AnnotationBlockLibrary.Classify(new[] { "Display" }).ShowsValue);
    }

    [Fact]
    public void Describe_BlockWithNoFields_SaysHowToAddOne()
    {
        string line = AnnotationBlockLibrary.Describe(AnnotationBlockFieldReport.None, hasDefinition: true);

        Assert.Contains("UserText", line);
        Assert.Contains("VALUE", line);
    }

    [Fact]
    public void Describe_MissingBlock_NamesItAndSaysBakeCreatesIt()
    {
        string line = AnnotationBlockLibrary.Describe(AnnotationBlockFieldReport.None, hasDefinition: false, "Spot Level");

        Assert.Contains("Spot Level", line);
        Assert.Contains("baking creates", line);
    }

    [Theory]
    [InlineData("MoleHill_AnalysisElevation", true)]
    [InlineData("MoleHill_AnalysisSlope", true)]
    [InlineData("MoleHill_ElevationMarker", true)]
    [InlineData("Elevation Label", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsBuiltInName_RecognisesTheManagedBlocks(string? name, bool expected)
    {
        Assert.Equal(expected, AnnotationBlockLibrary.IsBuiltInName(name));
    }

    [Fact]
    public void FieldHelp_ListsEveryKnownField()
    {
        string help = AnnotationBlockLibrary.FieldHelp();

        foreach (string key in AnnotationBlockLibrary.KnownFieldKeys)
            Assert.Contains($@"""{key}""", help);
    }

    [Fact]
    public void EveryBlockAnnotationCard_OffersABlockPickerForItsOwnBuiltInBlock()
    {
        var expected = new Dictionary<Type, MarkerBlockTemplate>
        {
            [typeof(CurveElevationLabelAnnotationDefinition)] = MarkerBlockTemplate.AnnotationElevation,
            [typeof(ProjectedElevationLabelAnnotationDefinition)] = MarkerBlockTemplate.AnnotationElevation,
            [typeof(CurveSlopeLabelAnnotationDefinition)] = MarkerBlockTemplate.AnnotationSlope,
            [typeof(PointSlopeLabelAnnotationDefinition)] = MarkerBlockTemplate.AnnotationSlope,
            [typeof(SlopeArrowAnnotationDefinition)] = MarkerBlockTemplate.AnnotationSlope,
        };

        foreach (var (definitionType, template) in expected)
        {
            AnnotationTypeDescriptor descriptor = AnnotationTypeRegistry.ForType(definitionType)!;
            var pickers = descriptor.Parameters.Where(p => p.Kind == ParameterKind.BlockPicker).ToList();

            var picker = Assert.Single(pickers);
            Assert.Equal("BlockDefinitionName", picker.Key);
            Assert.Equal(template, picker.BlockTemplate);

            // The picker edits the same field the build already reads.
            var definition = (BlockAttributeAnnotationDefinition)descriptor.Create();
            picker.SetText!(definition, "  My Label ");
            Assert.Equal("My Label", definition.BlockDefinitionName);
            picker.SetText!(definition, "  ");
            Assert.Null(definition.BlockDefinitionName);
        }
    }

    [RhinoNativeFact]
    public void CreateFromDefault_AddsAnEditableCopyThatDeclaresTheValueField()
    {
        using var doc = global::Rhino.RhinoDoc.CreateHeadless(null);

        var created = AnnotationBlockLibrary.CreateFromDefault(doc, MarkerBlockTemplate.AnnotationElevation, "Spot Level");

        Assert.NotNull(created);
        Assert.Equal("Spot Level", created!.Name);
        Assert.False(AnnotationBlockLibrary.IsBuiltInName(created.Name));
        AnnotationBlockFieldReport report = AnnotationBlockLibrary.Inspect(created);
        Assert.True(report.ShowsValue);
        Assert.Contains("Prefix", report.Recognised);
        Assert.Contains("Suffix", report.Recognised);
    }

    [RhinoNativeFact]
    public void UniqueName_AvoidsExistingBlocksAndBuiltIns()
    {
        using var doc = global::Rhino.RhinoDoc.CreateHeadless(null);
        AnnotationBlockLibrary.CreateFromDefault(doc, MarkerBlockTemplate.AnnotationSlope, "Slope Label");

        Assert.Equal("Slope Label 2", AnnotationBlockLibrary.UniqueName(doc, "Slope Label"));
        Assert.Equal("Fresh", AnnotationBlockLibrary.UniqueName(doc, "Fresh"));
        Assert.Equal("MoleHill_AnalysisSlope 2", AnnotationBlockLibrary.UniqueName(doc, "MoleHill_AnalysisSlope"));
    }

    [RhinoNativeFact]
    public void CreateFromDefault_SameNameTwice_GivesTwoSeparateBlocks()
    {
        using var doc = global::Rhino.RhinoDoc.CreateHeadless(null);

        var first = AnnotationBlockLibrary.CreateFromDefault(doc, MarkerBlockTemplate.AnnotationSlope, "Mine");
        var second = AnnotationBlockLibrary.CreateFromDefault(doc, MarkerBlockTemplate.AnnotationSlope, "Mine");

        Assert.Equal("Mine", first!.Name);
        Assert.Equal("Mine 2", second!.Name);
    }
}
