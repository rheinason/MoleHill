using System.Collections.Specialized;

namespace MoleHill.Rhino.Services;

internal readonly record struct BlockAttributeFieldDefinition(string Key, string? Prompt, string? DefaultValue);

internal static class BlockAttributePayload
{
    public static readonly string[] KnownPayloadKeys =
    {
        GeneratedBlockCatalog.DisplayToken,
        GeneratedBlockCatalog.ValueToken,
        GeneratedBlockCatalog.PrefixToken,
        GeneratedBlockCatalog.SuffixToken,
        GeneratedBlockCatalog.UnitToken,
        GeneratedBlockCatalog.NameToken,
        GeneratedBlockCatalog.IndexToken,
        GeneratedBlockCatalog.DistanceToken
    };

    public static IReadOnlyDictionary<string, string> BuildValues(
        IReadOnlyDictionary<string, string>? payload,
        IEnumerable<BlockAttributeFieldDefinition> fields)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (payload != null)
        {
            foreach (var pair in payload)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key))
                    values[pair.Key] = pair.Value ?? string.Empty;
            }
        }

        foreach (var field in fields)
        {
            if (string.IsNullOrWhiteSpace(field.Key) || values.ContainsKey(field.Key))
                continue;

            values[field.Key] = field.DefaultValue ?? string.Empty;
        }

        return values;
    }

    public static IReadOnlyList<string> FindMissingFieldKeys(
        NameValueCollection? strings,
        IEnumerable<BlockAttributeFieldDefinition> fields)
    {
        var missing = new List<string>();
        foreach (var field in fields)
        {
            if (string.IsNullOrWhiteSpace(field.Key))
                continue;

            if (strings?[field.Key] == null)
                missing.Add(field.Key);
        }

        return missing;
    }
}
