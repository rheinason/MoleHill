using MoleHill.Rhino.Model;

namespace MoleHill.Rhino.Services;

internal static class TerrainStageKey
{
    public static string CreateModifier(int modifierIndex, ModifierDefinition modifier)
    {
        return $"modifier:{modifierIndex}:{modifier.GetType().Name}:{modifier.Id:N}";
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

    public static bool TryParseModifierIndex(string stageKey, out int modifierIndex)
    {
        modifierIndex = -1;
        const string token = "modifier:";
        int modifierOffset = stageKey.IndexOf(token, StringComparison.Ordinal);
        if (modifierOffset < 0)
            return false;

        int start = modifierOffset + token.Length;
        int end = stageKey.IndexOf(':', start);
        if (end <= start)
            return false;

        return int.TryParse(stageKey[start..end], out modifierIndex);
    }
}
