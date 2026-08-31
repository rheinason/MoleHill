using System.Reflection;
using MoleHill.Rhino.Services;
using MoleHill.Shared;

namespace MoleHill.Rhino.Registry;

/// <summary>Auto-discovered registry of <see cref="AnnotationTypeDescriptor"/>s (Annotations tab).</summary>
internal static class AnnotationTypeRegistry
{
    private static readonly IReadOnlyList<AnnotationTypeDescriptor> AnnotationDescriptors;
    private static readonly Dictionary<Type, AnnotationTypeDescriptor> ByType;
    private static readonly Dictionary<string, AnnotationTypeDescriptor> ByKind;

    static AnnotationTypeRegistry()
    {
        var descriptors = Assembly.GetExecutingAssembly().GetTypes()
            .Where(type => !type.IsAbstract && typeof(AnnotationTypeDescriptor).IsAssignableFrom(type))
            .Select(type => (AnnotationTypeDescriptor)Activator.CreateInstance(type)!)
            .OrderBy(descriptor => descriptor.SortOrder)
            .ToList();

        AnnotationDescriptors = descriptors;
        ByType = descriptors.ToDictionary(descriptor => descriptor.DefinitionType);
        ByKind = descriptors.ToDictionary(descriptor => descriptor.Kind, StringComparer.Ordinal);
    }

    public static IReadOnlyList<AnnotationTypeDescriptor> Annotations => AnnotationDescriptors;

    public static AnnotationTypeDescriptor? ForType(Type definitionType) => ByType.GetValueOrDefault(definitionType);

    public static AnnotationTypeDescriptor? ForKind(string kind) => ByKind.GetValueOrDefault(kind);

    public static Model.AnnotationDefinition? Create(string kind) => ForKind(kind)?.Create();

    public static Model.AnnotationDefinition? Create(string kind, ModelUnitContext unitContext)
    {
        if (!unitContext.IsSupported)
            return null;

        Model.AnnotationDefinition? definition = ForKind(kind)?.Create();
        if (definition != null)
            TerrainUnitScaler.Scale(definition, unitContext.FromMeters(1.0));
        return definition;
    }
}
