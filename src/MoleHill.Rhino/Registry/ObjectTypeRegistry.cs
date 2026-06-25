using System.Reflection;

namespace MoleHill.Rhino.Registry;

/// <summary>Auto-discovered registry of <see cref="ObjectTypeDescriptor"/>s (Objects tab).</summary>
internal static class ObjectTypeRegistry
{
    private static readonly IReadOnlyList<ObjectTypeDescriptor> ObjectDescriptors;
    private static readonly Dictionary<Type, ObjectTypeDescriptor> ByType;
    private static readonly Dictionary<string, ObjectTypeDescriptor> ByKind;

    static ObjectTypeRegistry()
    {
        var descriptors = Assembly.GetExecutingAssembly().GetTypes()
            .Where(type => !type.IsAbstract && typeof(ObjectTypeDescriptor).IsAssignableFrom(type))
            .Select(type => (ObjectTypeDescriptor)Activator.CreateInstance(type)!)
            .OrderBy(descriptor => descriptor.SortOrder)
            .ToList();

        ObjectDescriptors = descriptors;
        ByType = descriptors.ToDictionary(descriptor => descriptor.DefinitionType);
        ByKind = descriptors.ToDictionary(descriptor => descriptor.Kind, StringComparer.Ordinal);
    }

    public static IReadOnlyList<ObjectTypeDescriptor> Objects => ObjectDescriptors;

    public static ObjectTypeDescriptor? ForType(Type definitionType) => ByType.GetValueOrDefault(definitionType);

    public static ObjectTypeDescriptor? ForKind(string kind) => ByKind.GetValueOrDefault(kind);

    public static Model.TerrainObjectDefinition? Create(string kind) => ForKind(kind)?.Create();
}
