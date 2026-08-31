using System.Reflection;

namespace MoleHill.Rhino.Registry;

/// <summary>Auto-discovered registry of <see cref="MarkerTypeDescriptor"/>s. Feeds JSON type resolution
/// and the marker build stage; the Markers tab it was written for no longer exists.</summary>
internal static class MarkerTypeRegistry
{
    private static readonly IReadOnlyList<MarkerTypeDescriptor> MarkerDescriptors;
    private static readonly Dictionary<Type, MarkerTypeDescriptor> ByType;
    private static readonly Dictionary<string, MarkerTypeDescriptor> ByKind;

    static MarkerTypeRegistry()
    {
        var descriptors = Assembly.GetExecutingAssembly().GetTypes()
            .Where(type => !type.IsAbstract && typeof(MarkerTypeDescriptor).IsAssignableFrom(type))
            .Select(type => (MarkerTypeDescriptor)Activator.CreateInstance(type)!)
            .OrderBy(descriptor => descriptor.SortOrder)
            .ToList();

        MarkerDescriptors = descriptors;
        ByType = descriptors.ToDictionary(descriptor => descriptor.DefinitionType);
        ByKind = descriptors.ToDictionary(descriptor => descriptor.Kind, StringComparer.Ordinal);
    }

    public static IReadOnlyList<MarkerTypeDescriptor> Markers => MarkerDescriptors;

    public static MarkerTypeDescriptor? ForType(Type definitionType) => ByType.GetValueOrDefault(definitionType);

    public static MarkerTypeDescriptor? ForKind(string kind) => ByKind.GetValueOrDefault(kind);

    public static Model.MarkerDefinition? Create(string kind) => ForKind(kind)?.Create();
}
