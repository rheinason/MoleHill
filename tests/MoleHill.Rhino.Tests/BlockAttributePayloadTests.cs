using System.Collections.Specialized;
using MoleHill.Rhino.Services;
using Xunit;

namespace MoleHill.Rhino.Tests;

public class BlockAttributePayloadTests
{
    [Fact]
    public void BuildValues_PreservesGeneratedEmptyValues()
    {
        var payload = new Dictionary<string, string>
        {
            [GeneratedBlockCatalog.PrefixToken] = string.Empty,
            [GeneratedBlockCatalog.ValueToken] = "12.30",
            [GeneratedBlockCatalog.SuffixToken] = " m"
        };
        var fields = new[]
        {
            new BlockAttributeFieldDefinition(GeneratedBlockCatalog.PrefixToken, "Prefix", "default prefix"),
            new BlockAttributeFieldDefinition(GeneratedBlockCatalog.ValueToken, "Value", "0"),
            new BlockAttributeFieldDefinition(GeneratedBlockCatalog.SuffixToken, "Suffix", string.Empty)
        };

        var values = BlockAttributePayload.BuildValues(payload, fields);

        Assert.Equal(string.Empty, values[GeneratedBlockCatalog.PrefixToken]);
        Assert.Equal("12.30", values[GeneratedBlockCatalog.ValueToken]);
        Assert.Equal(" m", values[GeneratedBlockCatalog.SuffixToken]);
    }

    [Fact]
    public void BuildValues_AddsDefinitionFieldsMissingFromPayload()
    {
        var payload = new Dictionary<string, string>
        {
            [GeneratedBlockCatalog.ValueToken] = "7.5"
        };
        var fields = new[]
        {
            new BlockAttributeFieldDefinition(GeneratedBlockCatalog.ValueToken, "Value", "0"),
            new BlockAttributeFieldDefinition("Custom", "Custom", "fallback")
        };

        var values = BlockAttributePayload.BuildValues(payload, fields);

        Assert.Equal("7.5", values[GeneratedBlockCatalog.ValueToken]);
        Assert.Equal("fallback", values["Custom"]);
    }

    [Fact]
    public void FindMissingFieldKeys_OnlyReportsDefinitionFields()
    {
        var strings = new NameValueCollection
        {
            [GeneratedBlockCatalog.DisplayToken] = "12.3 m",
            [GeneratedBlockCatalog.ValueToken] = "12.3"
        };
        var fields = new[]
        {
            new BlockAttributeFieldDefinition(GeneratedBlockCatalog.ValueToken, "Value", "0"),
            new BlockAttributeFieldDefinition(GeneratedBlockCatalog.PrefixToken, "Prefix", string.Empty)
        };

        var missing = BlockAttributePayload.FindMissingFieldKeys(strings, fields);

        Assert.Equal(new[] { GeneratedBlockCatalog.PrefixToken }, missing);
    }
}
