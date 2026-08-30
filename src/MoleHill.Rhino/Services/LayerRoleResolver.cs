using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Fully resolved appearance for one role: the template entry's values, whatever it inherits from
/// its ancestors, and the role's built-in defaults, collapsed into a single immutable record.
///
/// One record feeds both the viewport conduit and the bake path, which is what makes a baked object
/// look exactly like its preview. Before this, preview read a pixel column of one table and bake
/// read a millimetre column of another, and the two were kept in agreement by hand.
/// </summary>
internal sealed record LayerAppearance(
    int ColorArgb,
    int PrintColorArgb,
    double? PlotWeight,
    string? LinetypeName,
    string? AnnotationStyleName,
    string? HatchPatternName,
    double HatchScale,
    double HatchRotationDegrees,
    int PreviewWidthPx,
    bool ColorFromObject)
{
    /// <summary>
    /// Applies the terrain's display-only preview multiplier to this role's thickness. Clamped to
    /// what Rhino's display pipeline accepts, and never thinner than one pixel — a line the user
    /// turned down should get lighter, not disappear. Never reaches baked geometry.
    /// </summary>
    public int ScalePreviewWidth(double previewLineWeight)
    {
        double weight = double.IsFinite(previewLineWeight) && previewLineWeight > 0.0 ? previewLineWeight : 1.0;
        return (int)Math.Clamp(Math.Round(PreviewWidthPx * weight), 1, 32);
    }
}

/// <summary>A role together with the layer it lands on and how it looks.</summary>
internal sealed record ResolvedLayerRole(LayerRole Role, string LayerPath, LayerAppearance Appearance);

/// <summary>
/// One layer template flattened to role → (path, appearance).
///
/// Built once on the document thread and then read freely from the background build thread, the same
/// way <c>AnnotationStyleSnapshot</c> and <c>HatchPatternSnapshot</c> already are — the build has no
/// document access, so every layer decision must be resolved before it starts.
/// </summary>
internal sealed class LayerRoleTable
{
    private readonly Dictionary<LayerRole, ResolvedLayerRole> _resolved;
    private readonly (string Path, LayerAppearance Appearance)[] _allLayers;
    private readonly Dictionary<string, LayerAppearance> _byPath;

    private LayerRoleTable(
        string templateName,
        Dictionary<LayerRole, ResolvedLayerRole> resolved,
        (string Path, LayerAppearance Appearance)[] allLayers,
        ulong fingerprint)
    {
        TemplateName = templateName;
        _resolved = resolved;
        _allLayers = allLayers;
        _byPath = allLayers.ToDictionary(
            layer => layer.Path, layer => layer.Appearance, StringComparer.OrdinalIgnoreCase);
        Fingerprint = fingerprint;
    }

    public string TemplateName { get; }

    /// <summary>Changes whenever routing or appearance changes, so the build cache can key on it.</summary>
    public ulong Fingerprint { get; }

    public ResolvedLayerRole this[LayerRole role] => _resolved[role];

    /// <summary>The layer a role's output lands on. Never null and never empty.</summary>
    public string Path(LayerRole role) => _resolved[role].LayerPath;

    /// <summary>
    /// A path underneath a prefix role. Zones use this to mirror the input layer they were read
    /// from: <c>MoleHill::Zones::Site::Lawn</c> for a zone bounded by <c>Site::Lawn</c>.
    /// </summary>
    public string Path(LayerRole role, string? relativeSuffix)
    {
        string root = Path(role);
        if (string.IsNullOrWhiteSpace(relativeSuffix))
            return root;

        var segments = relativeSuffix
            .Split(new[] { "::" }, StringSplitOptions.None)
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .Select(segment => segment.Trim());

        string tail = string.Join("::", segments);
        return tail.Length == 0 ? root : $"{root}::{tail}";
    }

    public LayerAppearance Appearance(LayerRole role) => _resolved[role].Appearance;

    /// <summary>
    /// The role whose layer contains <paramref name="fullPath"/>, longest match first. Used for
    /// output that landed on a user-picked layer with no role of its own, and to seed intermediate
    /// layers from their own entry rather than from a descendant's.
    /// </summary>
    public ResolvedLayerRole? FindByLayerPath(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            return null;

        ResolvedLayerRole? best = null;
        foreach (var candidate in _resolved.Values)
        {
            if (!fullPath.Equals(candidate.LayerPath, StringComparison.OrdinalIgnoreCase) &&
                !fullPath.StartsWith(candidate.LayerPath + "::", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (best == null || candidate.LayerPath.Length > best.LayerPath.Length)
                best = candidate;
        }

        return best;
    }

    /// <summary>Every layer the template declares, role-bound or not, for layer creation.</summary>
    public IReadOnlyList<(string Path, LayerAppearance Appearance)> AllLayers => _allLayers;

    /// <summary>
    /// The appearance declared for this exact layer, or null when the template says nothing about
    /// it. Layer creation walks a path segment by segment and asks per segment, so an intermediate
    /// layer nobody styled is created bare rather than borrowing a descendant's look.
    /// </summary>
    public LayerAppearance? TryGetLayerAppearance(string? fullPath) =>
        fullPath != null && _byPath.TryGetValue(fullPath, out var appearance) ? appearance : null;

    /// <summary>Registry defaults only — the table a document gets before it has a template.</summary>
    public static LayerRoleTable Default { get; } = Build(null);

    public static LayerRoleTable Build(LayerTemplateDefinition? template)
    {
        var entriesByRole = new Dictionary<LayerRole, LayerTemplateEntry>();
        if (template?.Entries != null)
        {
            foreach (var entry in template.Entries)
            {
                var descriptor = LayerRoleRegistry.ForId(entry.Role);
                // First binding wins: a template with a role bound twice is rejected by the editor,
                // but a hand-edited or imported file can still carry one.
                if (descriptor != null && !entriesByRole.ContainsKey(descriptor.Role))
                    entriesByRole[descriptor.Role] = entry;
            }
        }

        var resolved = new Dictionary<LayerRole, ResolvedLayerRole>();
        foreach (var descriptor in LayerRoleRegistry.All)
            Resolve(descriptor.Role, entriesByRole, resolved);

        var allLayers = BuildAllLayers(template, resolved);
        return new LayerRoleTable(
            template?.Name ?? "MoleHill defaults",
            resolved,
            allLayers,
            ComputeFingerprint(resolved));
    }

    private static ResolvedLayerRole Resolve(
        LayerRole role,
        Dictionary<LayerRole, LayerTemplateEntry> entriesByRole,
        Dictionary<LayerRole, ResolvedLayerRole> resolved)
    {
        if (resolved.TryGetValue(role, out var existing))
            return existing;

        var descriptor = LayerRoleRegistry.For(role);
        entriesByRole.TryGetValue(role, out LayerTemplateEntry? entry);

        ResolvedLayerRole? parent = descriptor.Parent.HasValue
            ? Resolve(descriptor.Parent.Value, entriesByRole, resolved)
            : null;

        // An explicit binding is an absolute path and wins outright. Otherwise the path is built
        // from the parent, so rebinding a root moves its whole family with it.
        string path = !string.IsNullOrWhiteSpace(entry?.Path)
            ? entry!.Path.Trim()
            : parent != null
                ? parent.LayerPath + descriptor.RelativeSuffix
                : descriptor.AbsoluteDefaultPath!;

        var appearance = ResolveAppearance(descriptor, entry, parent?.Appearance);
        var result = new ResolvedLayerRole(role, path, appearance);
        resolved[role] = result;
        return result;
    }

    /// <summary>
    /// Field by field: this entry, then whatever the parent role resolved to, then the role's own
    /// built-in default. Inheriting per field rather than per record is what lets a template
    /// override one print width without having to restate the annotation style beside it.
    /// </summary>
    private static LayerAppearance ResolveAppearance(
        LayerRoleDescriptor descriptor,
        LayerTemplateEntry? entry,
        LayerAppearance? parent)
    {
        var defaults = descriptor.Appearance;

        int? color = entry?.ColorArgb ?? defaults.ColorArgb ?? parent?.ColorArgb;
        int? printColor = entry?.PrintColorArgb ?? defaults.PrintColorArgb ?? parent?.PrintColorArgb;
        double? plotWeight = entry?.PlotWeight
            ?? defaults.PlotWeight
            ?? (defaults.SuppressInheritedPlotWeight ? null : parent?.PlotWeight);

        int previewWidth = entry?.PreviewWidthPx
            ?? defaults.PreviewWidthPx
            ?? LayerRoleRegistry.DerivePreviewWidthPx(plotWeight);

        return new LayerAppearance(
            ColorArgb: color ?? unchecked((int)0xFF000000),
            // Print colour follows the display colour unless it is set apart deliberately, which is
            // what a drawing usually wants: a brown contour that prints black.
            PrintColorArgb: printColor ?? color ?? unchecked((int)0xFF000000),
            PlotWeight: plotWeight,
            LinetypeName: entry?.LinetypeName ?? defaults.LinetypeName ?? parent?.LinetypeName,
            AnnotationStyleName: entry?.AnnotationStyleName
                ?? defaults.AnnotationStyleName
                ?? parent?.AnnotationStyleName,
            HatchPatternName: entry?.HatchPatternName ?? defaults.HatchPatternName ?? parent?.HatchPatternName,
            HatchScale: entry?.HatchScale ?? defaults.HatchScale ?? parent?.HatchScale ?? 1.0,
            HatchRotationDegrees: entry?.HatchRotationDegrees
                ?? defaults.HatchRotationDegrees
                ?? parent?.HatchRotationDegrees
                ?? 0.0,
            PreviewWidthPx: previewWidth,
            ColorFromObject: descriptor.ColorFromObject);
    }

    /// <summary>
    /// Every layer the template asks for: the role-bound ones plus the plain layers a template
    /// creates for the user's own inputs (Inputs::Spots, Features::Walls) that nothing routes to.
    /// </summary>
    private static (string Path, LayerAppearance Appearance)[] BuildAllLayers(
        LayerTemplateDefinition? template,
        Dictionary<LayerRole, ResolvedLayerRole> resolved)
    {
        var byPath = new Dictionary<string, LayerAppearance>(StringComparer.OrdinalIgnoreCase);

        foreach (var role in resolved.Values)
            byPath[role.LayerPath] = role.Appearance;

        if (template?.Entries != null)
        {
            foreach (var entry in template.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Path) || LayerRoleRegistry.ForId(entry.Role) != null)
                    continue;

                string path = entry.Path.Trim();
                if (byPath.ContainsKey(path))
                    continue;

                double? plotWeight = entry.PlotWeight;
                byPath[path] = new LayerAppearance(
                    ColorArgb: entry.ColorArgb ?? unchecked((int)0xFF000000),
                    PrintColorArgb: entry.PrintColorArgb ?? entry.ColorArgb ?? unchecked((int)0xFF000000),
                    PlotWeight: plotWeight,
                    LinetypeName: entry.LinetypeName,
                    AnnotationStyleName: entry.AnnotationStyleName,
                    HatchPatternName: entry.HatchPatternName,
                    HatchScale: entry.HatchScale ?? 1.0,
                    HatchRotationDegrees: entry.HatchRotationDegrees ?? 0.0,
                    PreviewWidthPx: entry.PreviewWidthPx
                        ?? LayerRoleRegistry.DerivePreviewWidthPx(plotWeight),
                    ColorFromObject: false);
            }
        }

        return byPath
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => (pair.Key, pair.Value))
            .ToArray();
    }

    private static ulong ComputeFingerprint(Dictionary<LayerRole, ResolvedLayerRole> resolved)
    {
        var builder = new FingerprintBuilder();
        foreach (var descriptor in LayerRoleRegistry.All)
        {
            var role = resolved[descriptor.Role];
            var appearance = role.Appearance;
            builder.Add(descriptor.Id);
            builder.Add(role.LayerPath);
            builder.Add(appearance.ColorArgb);
            builder.Add(appearance.PrintColorArgb);
            builder.Add(appearance.PlotWeight ?? double.NaN);
            builder.Add(appearance.LinetypeName);
            builder.Add(appearance.AnnotationStyleName);
            builder.Add(appearance.HatchPatternName);
            builder.Add(appearance.HatchScale);
            builder.Add(appearance.HatchRotationDegrees);
            builder.Add(appearance.PreviewWidthPx);
        }

        return builder.ToUInt64();
    }
}
