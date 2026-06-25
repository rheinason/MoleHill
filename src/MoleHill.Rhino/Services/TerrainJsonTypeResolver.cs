using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Builds JSON polymorphism for the registry-driven definition families (modifiers, terrain objects,
/// markers) from their type registries instead of hand-maintained <c>[JsonDerivedType]</c> lists — so
/// registering a descriptor is all that's needed for a type to serialize/deserialize. Discriminator
/// strings are unchanged (they come from each descriptor's <c>Kind</c>), so saved .3dm terrains still
/// load. Types not in a registry (analyses) keep their attribute-driven contract via the base resolver.
/// </summary>
internal sealed class TerrainJsonTypeResolver : DefaultJsonTypeInfoResolver
{
    // Legacy modifier kinds with no descriptor: deserialize-only shims the serializer migrates away
    // (TerrainSerializer.MigrateZones turns them into zones). Their discriminators must stay registered
    // so old documents still deserialize before migration runs.
    private static readonly (Type Type, string Kind)[] LegacyModifierKinds =
    {
        (typeof(MeshAreasModifierDefinition), "mesh-areas"),
        (typeof(MeshCollageModifierDefinition), "mesh-collage"),
    };

    private static readonly Dictionary<Type, IReadOnlyList<(Type Type, string Kind)>> Families = BuildFamilies();

    private static Dictionary<Type, IReadOnlyList<(Type, string)>> BuildFamilies()
    {
        var modifiers = TerrainTypeRegistry.Modifiers
            .Select(descriptor => (descriptor.DefinitionType, descriptor.Kind))
            .Concat(LegacyModifierKinds)
            .ToList();

        var objects = ObjectTypeRegistry.Objects
            .Select(descriptor => (descriptor.DefinitionType, descriptor.Kind))
            .ToList();

        var markers = MarkerTypeRegistry.Markers
            .Select(descriptor => (descriptor.DefinitionType, descriptor.Kind))
            .ToList();

        return new Dictionary<Type, IReadOnlyList<(Type, string)>>
        {
            [typeof(ModifierDefinition)] = modifiers,
            [typeof(TerrainObjectDefinition)] = objects,
            [typeof(MarkerDefinition)] = markers,
        };
    }

    public override JsonTypeInfo GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        JsonTypeInfo info = base.GetTypeInfo(type, options);

        if (Families.TryGetValue(type, out var derivedTypes))
        {
            var polymorphism = new JsonPolymorphismOptions
            {
                TypeDiscriminatorPropertyName = "$type",
            };

            foreach (var (derivedType, kind) in derivedTypes)
                polymorphism.DerivedTypes.Add(new JsonDerivedType(derivedType, kind));

            info.PolymorphismOptions = polymorphism;
        }

        return info;
    }
}
