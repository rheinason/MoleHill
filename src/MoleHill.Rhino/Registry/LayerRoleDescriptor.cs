using MoleHill.Rhino.Model;

namespace MoleHill.Rhino.Registry;

/// <summary>
/// Everything the plugin knows about one <see cref="LayerRole"/> independently of any document: its
/// persisted id, where it sits in the role hierarchy, the path it resolves to when no template binds
/// it, and how its output looks.
/// </summary>
/// <param name="Role">The enum member this describes.</param>
/// <param name="Id">Stable key persisted in <see cref="LayerTemplateEntry.Role"/>. Never rename one:
/// every saved template and every embedded document copy refers to roles by this string.</param>
/// <param name="DisplayName">Human label for the template editor, e.g. "Contours (Major)".</param>
/// <param name="Parent">The role this one hangs off, or null for a root. Both the path and every
/// appearance field fall back up this chain.</param>
/// <param name="RelativeSuffix">Appended to the parent's resolved path. Empty means "the same layer
/// as the parent" — used where a role wants its own appearance but not its own layer.</param>
/// <param name="AbsoluteDefaultPath">Roots only: where the chain terminates. Exactly one of this and
/// <paramref name="Parent"/> is set, which is what guarantees resolution always produces a path.</param>
/// <param name="Appearance">Built-in appearance, the bottom of the inheritance chain.</param>
/// <param name="Facets">Which appearance fields are meaningful for this role.</param>
/// <param name="ColorFromObject">True when the colour carries information the layer cannot express —
/// a colour ramp, a zone identity, a per-terrain colour — so output stays ByObject. False for
/// drawing output, which stays ByLayer so the office layer table governs the print. Declared here
/// once rather than hand-set at each of the producers, so preview and bake cannot drift apart.</param>
internal sealed record LayerRoleDescriptor(
    LayerRole Role,
    string Id,
    string DisplayName,
    LayerRole? Parent,
    string RelativeSuffix,
    string? AbsoluteDefaultPath,
    LayerAppearanceDefaults Appearance,
    LayerRoleFacets Facets,
    bool ColorFromObject = false);
