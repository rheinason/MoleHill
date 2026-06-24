using System.Reflection;
using MoleHill.Rhino.Model;
using Rhino;

namespace MoleHill.Rhino.Registry;

/// <summary>
/// The central registry of terrain type descriptors, auto-discovered by reflection at first use. One
/// place to ask "what modifier types exist / how do I create one", replacing the per-type switches
/// scattered across the controller, panel, and build service. Adding a modifier = drop a
/// <see cref="ModifierTypeDescriptor"/> subclass; it registers itself here automatically.
/// </summary>
internal static class TerrainTypeRegistry
{
    private static readonly IReadOnlyList<ModifierTypeDescriptor> ModifierDescriptors;
    private static readonly Dictionary<Type, ModifierTypeDescriptor> ModifiersByType;
    private static readonly Dictionary<string, ModifierTypeDescriptor> ModifiersByKind;

    static TerrainTypeRegistry()
    {
        var descriptors = Assembly.GetExecutingAssembly().GetTypes()
            .Where(type => !type.IsAbstract && typeof(ModifierTypeDescriptor).IsAssignableFrom(type))
            .Select(type => (ModifierTypeDescriptor)Activator.CreateInstance(type)!)
            .OrderBy(descriptor => descriptor.DisplayName, StringComparer.Ordinal)
            .ToList();

        ModifierDescriptors = descriptors;
        ModifiersByType = descriptors.ToDictionary(descriptor => descriptor.DefinitionType);
        ModifiersByKind = descriptors.ToDictionary(descriptor => descriptor.Kind, StringComparer.Ordinal);
    }

    public static IReadOnlyList<ModifierTypeDescriptor> Modifiers => ModifierDescriptors;

    public static ModifierTypeDescriptor? ForModifierType(Type definitionType) =>
        ModifiersByType.GetValueOrDefault(definitionType);

    public static ModifierTypeDescriptor? ForModifierKind(string kind) =>
        ModifiersByKind.GetValueOrDefault(kind);

    public static ModifierDefinition? CreateModifier(string kind, UnitSystem unitSystem) =>
        ForModifierKind(kind)?.Create(unitSystem);
}
