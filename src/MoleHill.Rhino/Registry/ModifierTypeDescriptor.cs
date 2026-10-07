using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;

namespace MoleHill.Rhino.Registry;

/// <summary>
/// Self-registering descriptor for one terrain modifier type — the Blender-style "register a type and
/// it appears everywhere" unit. Discovered by reflection in <see cref="TerrainTypeRegistry"/>; adding a
/// modifier should mean dropping one descriptor (plus its definition) rather than editing switches in
/// the build service, controller, panel, and serializer. It supplies the kind/CLR-type/factory, the build
/// handler (<see cref="RunBuildStage"/>), the menu and card chrome, and the parameter schema — so a
/// modifier's serialization, menu entry, build dispatch and card all follow from this one file.
/// </summary>
internal abstract class ModifierTypeDescriptor
{
    /// <summary>Stable discriminator string (must match the historical JSON `$type` value).</summary>
    public abstract string Kind { get; }

    public abstract Type DefinitionType { get; }

    /// <summary>Human label for menus/headers.</summary>
    public abstract string DisplayName { get; }

    /// <summary>Panel icon resource name (loaded via <c>PanelIcons.Load</c>).</summary>
    public abstract string IconName { get; }

    /// <summary>Ordering within the Add-Modifier menu.</summary>
    public virtual int SortOrder => 0;

    /// <summary>Whether this type appears in the Add-Modifier menu (the base triangulate does not).</summary>
    public virtual bool CanCreateFromMenu => true;

    /// <summary>Short card subtitle. Defaults to the display name.</summary>
    public virtual string Subtitle => DisplayName;

    /// <summary>Creates a new definition with unit-aware defaults.</summary>
    public abstract ModifierDefinition Create(UnitSystem unitSystem);

    /// <summary>
    /// Declarative parameter schema for this type. The panel generates the card from this list, and the
    /// same schema is the contract for Grasshopper-component generation. Empty means "no schema yet" —
    /// the panel falls back to its hand-written card body for that type.
    /// </summary>
    public virtual IReadOnlyList<ParameterDescriptor<ModifierDefinition>> Parameters => Array.Empty<ParameterDescriptor<ModifierDefinition>>();

    /// <summary>
    /// Runs this type's build step, reading the incoming mesh from <paramref name="context"/> and writing
    /// back the outgoing mesh/fingerprint. Replaces the central modifier switch in the build service.
    /// </summary>
    public abstract void RunBuildStage(ModifierBuildContext context);

    /// <summary>
    /// Why this instance cannot change the terrain yet (a Grade Pad with no boundaries), or null when it can.
    /// An inert stage is skipped outright: the terrain and its fingerprint pass through untouched, nothing is
    /// cached, and the card shows the reason. Judged on resolved sources, so a set whose objects were
    /// deleted counts as empty. Types that act on the whole mesh without sources are never inert.
    /// </summary>
    public virtual string? InertReason(ModifierDefinition modifier, TerrainBuildSnapshot snapshot) => null;

    /// <summary>
    /// The one-line text shown on the collapsed card (source counts, key values). Empty when a type has none.
    /// </summary>
    public virtual string Summarize(ModifierDefinition modifier) => string.Empty;

    /// <summary>True when every one of <paramref name="sets"/> resolves to no objects.</summary>
    protected static bool NoneResolve(TerrainBuildSnapshot snapshot, params SourceReferenceSet[] sets) =>
        sets.All(set => TerrainBuildSnapshotResolver.ResolveObjects(snapshot, set).Count == 0);
}
