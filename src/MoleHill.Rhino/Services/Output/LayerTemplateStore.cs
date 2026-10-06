using System.Text.Json;
using System.Text.Json.Serialization;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;

namespace MoleHill.Rhino.Services;

internal sealed class LayerTemplateStore
{
    private const string TemplatesFileName = "layer-templates.json";

    /// <summary>Current template schema. 0 is a pre-role file — see <see cref="UpgradeTemplate"/>.</summary>
    private const int CurrentTemplateVersion = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // Inherited appearance is the common case, and writing a null for every field a template
        // does not set would triple the size of the file and bury the values that matter.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public IReadOnlyList<LayerTemplateDefinition> LoadTemplates()
    {
        string path = GetStorePath();
        if (!File.Exists(path))
        {
            var defaults = CreateDefaultTemplates();
            SaveTemplates(defaults);
            return defaults;
        }

        try
        {
            string json = File.ReadAllText(path);
            var templates = JsonSerializer.Deserialize<List<LayerTemplateDefinition>>(json, JsonOptions);
            if (templates is { Count: > 0 })
                return NormalizeTemplates(templates);
        }
        catch
        {
            // Fall back to defaults if the local settings file is invalid.
        }

        var fallback = CreateDefaultTemplates();
        SaveTemplates(fallback);
        return fallback;
    }

    public void SaveTemplates(IEnumerable<LayerTemplateDefinition> templates)
    {
        string path = GetStorePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var normalized = NormalizeTemplates(templates.ToList());
        string json = JsonSerializer.Serialize(normalized, JsonOptions);
        File.WriteAllText(path, json);

        // Routing and appearance both come from the template, so every open document is now
        // resolving against a stale table.
        LayerRoleService.Invalidate();
    }

    public string GetStorePath()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "MoleHill", TemplatesFileName);
    }

    /// <summary>Returns a fresh copy of the built-in factory templates without touching the saved file.</summary>
    public IReadOnlyList<LayerTemplateDefinition> GetDefaultTemplates() => CreateDefaultTemplates();

    internal static List<LayerTemplateDefinition> NormalizeTemplates(List<LayerTemplateDefinition> templates)
    {
        foreach (var template in templates)
        {
            template.Name = string.IsNullOrWhiteSpace(template.Name) ? "Template" : template.Name.Trim();
            template.Entries ??= new List<LayerTemplateEntry>();
            template.Entries = template.Entries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Path))
                .Select(entry =>
                {
                    entry.Path = entry.Path.Trim();
                    return entry;
                })
                .ToList();

            UpgradeTemplate(template);
            DropDuplicateRoleBindings(template);
        }

        return templates
            .Where(template => template.Entries.Count > 0)
            .GroupBy(template => template.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    /// <summary>
    /// Brings a pre-role template up to the current schema, in place.
    ///
    /// A version 0 file predates role bindings, and its entries are keyed only by layer path. Since
    /// those paths are exactly the ones the plugin used to hardcode, the roles can be recovered by
    /// matching each path against the role's default — so a user's existing template, and any copy
    /// or export of it, acquires its bindings on first load with nothing for them to do.
    ///
    /// A print width of zero also changes meaning: it used to be the unset default that the old
    /// serializer wrote for every entry, and now means a deliberate hairline, so it is cleared back
    /// to "inherit".
    /// </summary>
    private static void UpgradeTemplate(LayerTemplateDefinition template)
    {
        if (template.Version >= CurrentTemplateVersion)
            return;

        if (template.Version < 2)
            RecoverRoleBindings(template);

        if (template.Version < 3)
            MakeRootPerTerrain(template);

        template.Version = CurrentTemplateVersion;
    }

    /// <summary>
    /// Version 3: the layer root is per terrain. A template whose role-bound layers all hang from the
    /// literal <c>MoleHill</c> root (the shipped layout, and every file from before this) has that
    /// segment rewritten to <c>MoleHill {terrain}</c>, so each terrain gets a tree of its own.
    ///
    /// A template that routes anywhere else, or only partly under <c>MoleHill</c>, is the user's own
    /// layout and is left exactly as written: rewriting half of it would split a structure they chose,
    /// and a literal root keeps every terrain sharing it, which is what they asked for.
    /// </summary>
    private static void MakeRootPerTerrain(LayerTemplateDefinition template)
    {
        var bound = template.Entries.Where(entry => entry.Roles.Count > 0).ToList();
        if (bound.Count == 0 || bound.Any(entry => TerrainLayerNaming.ContainsToken(entry.Path)))
            return;

        if (!bound.All(entry => TerrainLayerNaming.TryRewriteLegacyRoot(entry.Path, out _)))
            return;

        foreach (var entry in template.Entries)
        {
            if (TerrainLayerNaming.TryRewriteLegacyRoot(entry.Path, out string rewritten))
                entry.Path = rewritten;
        }
    }

    private static void RecoverRoleBindings(LayerTemplateDefinition template)
    {

        // Several roles can default to one layer — retaining walls and grading output both sit on
        // the auxiliary layer until someone splits them out. The layer belongs to the role that
        // names it, so recovering a binding from a path must pick that one and not a role that is
        // merely sharing it.
        var rolesByDefaultPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in LayerRoleRegistry.All)
        {
            if (descriptor.Parent.HasValue && descriptor.RelativeSuffix.Length == 0)
                continue;

            string defaultPath = LayerRoleRegistry.DefaultPath(descriptor.Role);
            rolesByDefaultPath[defaultPath] = descriptor.Id;
            // A file from before roots were per terrain spells the same default without the token.
            rolesByDefaultPath[TerrainLayerNaming.ToLegacyLiteral(defaultPath)] = descriptor.Id;
        }

        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in template.Entries)
        {
            if (template.Version < 1 && entry.PlotWeight is 0.0)
                entry.PlotWeight = null;

            // A layer used to carry at most one role; fold that into the list it has now.
            if (!string.IsNullOrWhiteSpace(entry.LegacyRole) && !entry.Roles.Contains(entry.LegacyRole!))
                entry.Roles.Insert(0, entry.LegacyRole!);
            entry.LegacyRole = null;

            if (entry.Roles.Count > 0)
            {
                foreach (string existing in entry.Roles)
                    claimed.Add(existing);
                continue;
            }

            if (rolesByDefaultPath.TryGetValue(entry.Path, out string? roleId) && claimed.Add(roleId))
                entry.Roles.Add(roleId);
        }
    }

    /// <summary>
    /// Keeps the first binding when a role appears on more than one layer. Several roles may share a
    /// layer, but one role landing on two layers would duplicate its output, so an imported or
    /// hand-edited file that does it is resolved rather than honoured.
    /// </summary>
    private static void DropDuplicateRoleBindings(LayerTemplateDefinition template)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in template.Entries)
            entry.Roles.RemoveAll(roleId => string.IsNullOrWhiteSpace(roleId) || !seen.Add(roleId));
    }

    /// <summary>
    /// The shipped template: every role at its registry default, plus the plain layers a user draws
    /// their own inputs and feature curves on, which nothing routes to.
    ///
    /// Generated from <see cref="LayerRoleRegistry"/> rather than hand-written. The two used to be
    /// separate lists of the same layer paths and print widths, kept in agreement by hand and by
    /// tests whose only job was to catch them drifting — and they had in fact drifted.
    /// </summary>
    private static List<LayerTemplateDefinition> CreateDefaultTemplates()
    {
        const string R = TerrainLayerNaming.DefaultRoot;
        var entries = new List<LayerTemplateEntry>
        {
            CreateEntry(R, unchecked((int)0xFF000000), unchecked((int)0xFF000000), 0.25),
            CreateEntry(R + "::Inputs", unchecked((int)0xFF808080), unchecked((int)0xFF808080), 0.25),
            CreateEntry(R + "::Inputs::Spots", unchecked((int)0xFF008900), unchecked((int)0xFF008900), 0.18),
            CreateEntry(R + "::Inputs::Contours", unchecked((int)0xFF8C8C8C), unchecked((int)0xFF8C8C8C), 0.13),
            CreateEntry(R + "::Inputs::Breaklines", unchecked((int)0xFFFFC000), unchecked((int)0xFFFFC000), 0.25),
            CreateEntry(R + "::Inputs::Boundary", unchecked((int)0xFF1E64FF), unchecked((int)0xFF1E64FF), 0.35),
            CreateEntry(R + "::Features", unchecked((int)0xFF7D26CD), unchecked((int)0xFF7D26CD), 0.25),
            CreateEntry(R + "::Features::Walls", unchecked((int)0xFFC00000), unchecked((int)0xFFC00000), 0.25),
            CreateEntry(R + "::Features::Pads", unchecked((int)0xFF00B0F0), unchecked((int)0xFF00B0F0), 0.25),
            CreateEntry(R + "::Features::Paths", unchecked((int)0xFFFFBF00), unchecked((int)0xFFFFBF00), 0.25)
        };

        entries.AddRange(CreateRoleEntries());

        return new List<LayerTemplateDefinition>
        {
            new()
            {
                Version = CurrentTemplateVersion,
                Name = "MoleHill Terrain",
                Entries = entries
            }
        };
    }

    private static IEnumerable<LayerTemplateEntry> CreateRoleEntries()
    {
        var table = LayerRoleTable.Default;
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var descriptor in LayerRoleRegistry.All)
        {
            string path = table.Path(descriptor.Role);

            // Roles that deliberately share a layer with their parent contribute no second entry;
            // a template cannot bind two roles to one layer.
            if (!seenPaths.Add(path))
                continue;

            var appearance = table.Appearance(descriptor.Role);
            yield return new LayerTemplateEntry
            {
                Path = path,
                Roles = { descriptor.Id },
                ColorArgb = appearance.ColorArgb,
                PrintColorArgb = appearance.PrintColorArgb,
                PlotWeight = appearance.PlotWeight,
                LinetypeName = appearance.LinetypeName,
                AnnotationStyleName = appearance.AnnotationStyleName,
                HatchPatternName = appearance.HatchPatternName,
                // Only carried when it is not what the print width would derive, so the file shows
                // the handful of deliberate exceptions rather than restating every default.
                PreviewWidthPx =
                    appearance.PreviewWidthPx == LayerRoleRegistry.DerivePreviewWidthPx(appearance.PlotWeight)
                        ? null
                        : appearance.PreviewWidthPx
            };
        }
    }

    private static LayerTemplateEntry CreateEntry(string path, int colorArgb, int printColorArgb, double plotWeight)
    {
        return new LayerTemplateEntry
        {
            Path = path,
            ColorArgb = colorArgb,
            PrintColorArgb = printColorArgb,
            PlotWeight = plotWeight
        };
    }
}
