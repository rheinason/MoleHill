using System.Runtime.CompilerServices;
using Rhino.Geometry;

namespace MoleHill.Rhino.Services;

internal sealed class GeneratedRhinoObject
{
    private readonly Dictionary<(int SourceIdentity, string DisplayText), TextEntity> _previewTextCache = new();
    private bool _previewDisplayTextInitialized;
    private string? _previewDisplayText;

    public GeometryBase? Geometry { get; init; }

    public required string Name { get; init; }

    public GeneratedObjectKind Kind { get; init; }

    public Guid? AnalysisId { get; init; }

    public int? ColorArgb { get; init; }

    public string? LayerPath { get; init; }

    public string? SourceLayerPath { get; init; }

    public string? MaterialName { get; init; }

    public string? InstanceDefinitionName { get; init; }

    public MarkerBlockTemplate MarkerBlockTemplate { get; init; }

    public IReadOnlyDictionary<string, string>? InstanceUserStrings { get; init; }

    public Transform InstanceTransform { get; init; } = Transform.Identity;

    public double? PlotWeight { get; init; }

    /// <summary>When set, this is a scatter instance owned by the given scatter definition; the display
    /// conduit uses the definition's preview mode/cap to decide how to draw it. Bake ignores this.</summary>
    public Guid? ScatterDefinitionId { get; init; }

    internal TextEntity? GetPreviewTextEntity(TextEntity source, string displayText)
    {
        var key = (RuntimeHelpers.GetHashCode(source), displayText);
        if (_previewTextCache.TryGetValue(key, out TextEntity? cached))
            return cached;

        if (source.Duplicate() is not TextEntity clone)
            return null;

        clone.RichText = displayText;
        _previewTextCache[key] = clone;
        return clone;
    }

    internal bool TryGetPreviewDisplayText(out string displayText)
    {
        if (!_previewDisplayTextInitialized)
        {
            _previewDisplayTextInitialized = true;
            if (InstanceUserStrings is { Count: > 0 } userStrings)
            {
                string prefix = GetUserString(userStrings, GeneratedBlockCatalog.PrefixToken);
                string value = GetUserString(userStrings, GeneratedBlockCatalog.ValueToken);
                string suffix = GetUserString(userStrings, GeneratedBlockCatalog.SuffixToken);
                string combined = string.Concat(prefix, value, suffix);
                _previewDisplayText = !string.IsNullOrWhiteSpace(combined)
                    ? combined
                    : GetUserString(userStrings, GeneratedBlockCatalog.DisplayToken);
            }
        }

        displayText = _previewDisplayText ?? string.Empty;
        return !string.IsNullOrWhiteSpace(displayText);
    }

    private static string GetUserString(IReadOnlyDictionary<string, string> userStrings, string key)
    {
        return userStrings.TryGetValue(key, out string? value) && value != null
            ? value
            : string.Empty;
    }
}
