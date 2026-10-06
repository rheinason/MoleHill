using MoleHill.Rhino.Model;

namespace MoleHill.Rhino.Services;

internal static class TerrainStageKey
{
    /// <summary>
    /// A modifier stage's cache key: its type and id, never its position. Position used to be part of it, so
    /// inserting or moving a card changed the key of every card below and they all rebuilt on unchanged
    /// input (92 s at the 1 m park for an empty card). Order needs no place in the key: it already reaches
    /// every stage through its upstream fingerprint.
    /// </summary>
    public static string CreateModifier(ModifierDefinition modifier)
    {
        return $"modifier:{modifier.GetType().Name}:{modifier.Id:N}";
    }

    public static string ForMode(TerrainBuildMode mode, string stageKey)
    {
        return TerrainRuntimeCache.GetStagePrefix(mode) + stageKey;
    }

    public static string CreateGradingTopology(string stageKey, string graderKind)
    {
        return $"{stageKey}:topology:{graderKind}";
    }

    public static string CreateSmoothPrepared(string stageKey)
    {
        return $"{stageKey}:prepared";
    }

    public static string GetBase(string stageKey)
    {
        int topologyIndex = stageKey.IndexOf(":topology:", StringComparison.Ordinal);
        if (topologyIndex >= 0)
            return stageKey[..topologyIndex];

        int preparedIndex = stageKey.IndexOf(":prepared", StringComparison.Ordinal);
        if (preparedIndex >= 0)
            return stageKey[..preparedIndex];

        return stageKey;
    }

    /// <summary>The id of the modifier a (possibly suffixed) modifier stage key belongs to.</summary>
    public static bool TryParseModifierId(string stageKey, out Guid modifierId)
    {
        modifierId = Guid.Empty;
        const string token = "modifier:";
        int modifierOffset = stageKey.IndexOf(token, StringComparison.Ordinal);
        if (modifierOffset < 0)
            return false;

        int typeEnd = stageKey.IndexOf(':', modifierOffset + token.Length);
        if (typeEnd < 0)
            return false;

        int idStart = typeEnd + 1;
        int idEnd = stageKey.IndexOf(':', idStart);
        string id = idEnd < 0 ? stageKey[idStart..] : stageKey[idStart..idEnd];
        return Guid.TryParseExact(id, "N", out modifierId);
    }
}
