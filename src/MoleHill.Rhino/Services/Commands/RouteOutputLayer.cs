namespace MoleHill.Rhino.Services;

/// <summary>
/// Where mhSuggestRoute puts the curves it keeps: the current layer, or one the user picked. A pick is
/// remembered by layer id for the document it was made in (for the session, like every command option),
/// so renaming the layer keeps it and another document never inherits it; a remembered layer that has
/// since been deleted falls back to the current layer.
/// </summary>
internal static class RouteOutputLayer
{
    private const string KeyPrefix = "MoleHill.SuggestRoute.LayerId.";

    /// <summary>The layer remembered for a document, or <see cref="Guid.Empty"/> for the current layer.</summary>
    public static Guid Remembered(uint documentSerial) =>
        CommandOptionCache.GetValue(KeyPrefix + documentSerial, Guid.Empty);

    /// <summary>Remembers a layer for a document; <see cref="Guid.Empty"/> goes back to the current layer.</summary>
    public static void Remember(uint documentSerial, Guid layerId) =>
        CommandOptionCache.SetValue(KeyPrefix + documentSerial, layerId);

    /// <summary>
    /// The layer to use: the remembered one while it still exists, otherwise the current layer
    /// (<see cref="Guid.Empty"/>). <paramref name="fellBack"/> is true when a remembered layer was lost,
    /// so the command can say so instead of quietly changing where curves land.
    /// </summary>
    public static Guid Resolve(Guid remembered, Func<Guid, bool> layerExists, out bool fellBack)
    {
        fellBack = false;
        if (remembered == Guid.Empty)
            return Guid.Empty;
        if (layerExists(remembered))
            return remembered;

        fellBack = true;
        return Guid.Empty;
    }

    /// <summary>
    /// A layer name as a command-line option value, which must be one word: anything but letters and
    /// digits becomes an underscore. The full path is printed when the layer is chosen.
    /// </summary>
    public static string OptionValue(string? layerName)
    {
        if (string.IsNullOrWhiteSpace(layerName))
            return "Current";

        var characters = layerName.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        string value = new string(characters).Trim('_');
        return value.Length == 0 ? "Layer" : value;
    }
}
