using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Builds <see cref="ModifierDefinition"/> JSON polymorphism from the type registry instead of a
/// hand-maintained <c>[JsonDerivedType]</c> list — so registering a modifier descriptor is all that's
/// needed for it to serialize/deserialize. Discriminator strings are unchanged (they come from each
/// descriptor's <see cref="ModifierTypeDescriptor.Kind"/>), so saved .3dm terrains still load. Every
/// other polymorphic type (analyses, markers, objects) keeps its attribute-driven contract via the base
/// resolver.
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

    public override JsonTypeInfo GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        JsonTypeInfo info = base.GetTypeInfo(type, options);

        if (type == typeof(ModifierDefinition))
        {
            var polymorphism = new JsonPolymorphismOptions
            {
                TypeDiscriminatorPropertyName = "$type",
            };

            foreach (var descriptor in TerrainTypeRegistry.Modifiers)
                polymorphism.DerivedTypes.Add(new JsonDerivedType(descriptor.DefinitionType, descriptor.Kind));

            foreach (var (legacyType, legacyKind) in LegacyModifierKinds)
                polymorphism.DerivedTypes.Add(new JsonDerivedType(legacyType, legacyKind));

            info.PolymorphismOptions = polymorphism;
        }

        return info;
    }
}
