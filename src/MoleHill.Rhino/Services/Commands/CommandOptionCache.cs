using System.Collections.Concurrent;
using MoleHill.Shared;

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

    public static double GetLength(string key, ModelUnitContext unitContext, double defaultMeters)
    {
        double meters = GetValue(key + ".Meters", defaultMeters);
        return unitContext.FromMeters(meters);
    }

    public static double GetLengthFromModelDefault(
        string key,
        ModelUnitContext unitContext,
        double defaultModelLength)
    {
        return GetLength(key, unitContext, unitContext.ToMeters(defaultModelLength));
    }

    public static void SetLength(string key, ModelUnitContext unitContext, double modelLength)
    {
        SetValue(key + ".Meters", unitContext.ToMeters(modelLength));
    }
}
