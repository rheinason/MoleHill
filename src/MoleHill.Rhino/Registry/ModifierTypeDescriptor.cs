using MoleHill.Rhino.Model;
using Rhino;

namespace MoleHill.Rhino.Registry;

/// <summary>
/// Self-registering descriptor for one terrain modifier type — the Blender-style "register a type and
/// it appears everywhere" unit. Discovered by reflection in <see cref="TerrainTypeRegistry"/>; adding a
/// modifier should mean dropping one descriptor (plus its definition) rather than editing switches in
/// the build service, controller, panel, and serializer. This is the foundation: it currently supplies
/// the kind/CLR-type/factory; build-handler and parameter-schema members will be added as the migration
/// proceeds (see docs/cleanup-plan / the modular-refactor plan).
/// </summary>
internal abstract class ModifierTypeDescriptor
{
    /// <summary>Stable discriminator string (must match the historical JSON `$type` value).</summary>
    public abstract string Kind { get; }

    public abstract Type DefinitionType { get; }

    /// <summary>Human label for menus/headers.</summary>
    public abstract string DisplayName { get; }

    /// <summary>Creates a new definition with unit-aware defaults.</summary>
    public abstract ModifierDefinition Create(UnitSystem unitSystem);
}
