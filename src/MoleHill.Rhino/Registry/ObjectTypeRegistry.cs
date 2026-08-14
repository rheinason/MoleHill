using System.Reflection;
using MoleHill.Rhino.Services;
using MoleHill.Shared;

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

    public static Model.TerrainObjectDefinition? Create(string kind, ModelUnitContext unitContext)
    {
        if (!unitContext.IsSupported)
            return null;

        Model.TerrainObjectDefinition? definition = ForKind(kind)?.Create();
        if (definition != null)
            TerrainUnitScaler.Scale(definition, unitContext.FromMeters(1.0));
        return definition;
    }
}
