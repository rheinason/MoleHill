using System.Reflection;
using MoleHill.Rhino.Services;
using MoleHill.Shared;

namespace MoleHill.Rhino.Registry;

/// <summary>Auto-discovered registry of <see cref="AnalysisTypeDescriptor"/>s (Analysis tab).</summary>
internal static class AnalysisTypeRegistry
{
    private static readonly IReadOnlyList<AnalysisTypeDescriptor> AnalysisDescriptors;
    private static readonly Dictionary<Type, AnalysisTypeDescriptor> ByType;
    private static readonly Dictionary<string, AnalysisTypeDescriptor> ByKind;

    static AnalysisTypeRegistry()
    {
        var descriptors = Assembly.GetExecutingAssembly().GetTypes()
            .Where(type => !type.IsAbstract && typeof(AnalysisTypeDescriptor).IsAssignableFrom(type))
            .Select(type => (AnalysisTypeDescriptor)Activator.CreateInstance(type)!)
            .OrderBy(descriptor => descriptor.SortOrder)
            .ToList();

        AnalysisDescriptors = descriptors;
        ByType = descriptors.ToDictionary(descriptor => descriptor.DefinitionType);
        ByKind = descriptors.ToDictionary(descriptor => descriptor.Kind, StringComparer.Ordinal);
    }

    public static IReadOnlyList<AnalysisTypeDescriptor> Analyses => AnalysisDescriptors;

    public static AnalysisTypeDescriptor? ForType(Type definitionType) => ByType.GetValueOrDefault(definitionType);

    public static AnalysisTypeDescriptor? ForKind(string kind) => ByKind.GetValueOrDefault(kind);

    public static Model.AnalysisDefinition? Create(string kind) => ForKind(kind)?.Create();

    public static Model.AnalysisDefinition? Create(string kind, ModelUnitContext unitContext)
    {
        if (!unitContext.IsSupported)
            return null;

        Model.AnalysisDefinition? definition = ForKind(kind)?.Create();
        if (definition != null)
            TerrainUnitScaler.Scale(definition, unitContext.FromMeters(1.0));
        return definition;
    }
}
