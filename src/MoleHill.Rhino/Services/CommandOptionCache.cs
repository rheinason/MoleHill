using System.Collections.Concurrent;

namespace MoleHill.Rhino.Services;

internal static class CommandOptionCache
{
    private static readonly ConcurrentDictionary<string, object?> Values = new(StringComparer.Ordinal);

    public static T GetValue<T>(string key, T defaultValue)
    {
        if (Values.TryGetValue(key, out object? value) && value is T typed)
            return typed;

        Values[key] = defaultValue;
        return defaultValue;
    }

    public static void SetValue<T>(string key, T value)
    {
        Values[key] = value;
    }
}
