using System.Text.Json;
using MoleHill.Rhino.Model;

namespace MoleHill.Rhino.Services;

internal sealed class LayerTemplateStore
{
    private const string TemplatesFileName = "layer-templates.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

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
    }

    public string GetStorePath()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "MoleHill", TemplatesFileName);
    }

    /// <summary>Returns a fresh copy of the built-in factory templates without touching the saved file.</summary>
    public IReadOnlyList<LayerTemplateDefinition> GetDefaultTemplates() => CreateDefaultTemplates();

    private static List<LayerTemplateDefinition> NormalizeTemplates(List<LayerTemplateDefinition> templates)
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
        }

        return templates
            .Where(template => template.Entries.Count > 0)
            .GroupBy(template => template.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static List<LayerTemplateDefinition> CreateDefaultTemplates()
    {
        // Mirrors the layers MoleHill actually uses: inputs the user draws or imports (spots,
        // contours, breaklines, boundary), grading feature curves (walls, pads, paths), plus the
        // output layers the plugin bakes to (Terrain / Auxiliary / Annotation). Output paths and the
        // terrain color reuse TerrainDefinition so the template stays aligned with the plugin's defaults.
        return new List<LayerTemplateDefinition>
        {
            new()
            {
                Name = "MoleHill Terrain",
                Entries = new List<LayerTemplateEntry>
                {
                    CreateEntry("MoleHill", unchecked((int)0xFF000000), unchecked((int)0xFF000000), 0.25),
                    CreateEntry("MoleHill::Inputs", unchecked((int)0xFF808080), unchecked((int)0xFF808080), 0.25),
                    CreateEntry("MoleHill::Inputs::Spots", unchecked((int)0xFF008900), unchecked((int)0xFF008900), 0.18),
                    CreateEntry("MoleHill::Inputs::Contours", unchecked((int)0xFF8C8C8C), unchecked((int)0xFF8C8C8C), 0.13),
                    CreateEntry("MoleHill::Inputs::Breaklines", unchecked((int)0xFFFFC000), unchecked((int)0xFFFFC000), 0.25),
                    CreateEntry("MoleHill::Inputs::Boundary", unchecked((int)0xFF1E64FF), unchecked((int)0xFF1E64FF), 0.35),
                    CreateEntry("MoleHill::Features", unchecked((int)0xFF7D26CD), unchecked((int)0xFF7D26CD), 0.25),
                    CreateEntry("MoleHill::Features::Walls", unchecked((int)0xFFC00000), unchecked((int)0xFFC00000), 0.25),
                    CreateEntry("MoleHill::Features::Pads", unchecked((int)0xFF00B0F0), unchecked((int)0xFF00B0F0), 0.25),
                    CreateEntry("MoleHill::Features::Paths", unchecked((int)0xFFFFBF00), unchecked((int)0xFFFFBF00), 0.25),
                    CreateEntry(TerrainDefinition.DefaultTerrainLayerPath, TerrainDefinition.DefaultTerrainColorArgb, TerrainDefinition.DefaultTerrainColorArgb, 0.18),
                    CreateEntry(TerrainDefinition.DefaultAuxiliaryLayerPath, unchecked((int)0xFFAAAAAA), unchecked((int)0xFFAAAAAA), 0.13),
                    CreateEntry(TerrainDefinition.DefaultAnnotationLayerPath, unchecked((int)0xFF000000), unchecked((int)0xFF000000), 0.13),
                    // Drawing sublayers. Print width and print colour live here rather than on each
                    // generated object, so the office standard is edited in Rhino's Layers panel and
                    // per-detail overrides work. Weights match GeneratedLayerDefaults.
                    CreateEntry($"{TerrainDefinition.DefaultAnnotationLayerPath}::Contours::Major", unchecked((int)0xFF6E4B1F), unchecked((int)0xFF000000), 0.35),
                    CreateEntry($"{TerrainDefinition.DefaultAnnotationLayerPath}::Contours::Minor", unchecked((int)0xFFA98A5C), unchecked((int)0xFF000000), 0.13),
                    CreateEntry($"{TerrainDefinition.DefaultAnnotationLayerPath}::Sections", unchecked((int)0xFF000000), unchecked((int)0xFF000000), 0.50),
                    CreateEntry($"{TerrainDefinition.DefaultAnnotationLayerPath}::Sections::Cuts", unchecked((int)0xFF000000), unchecked((int)0xFF000000), 0.50),
                    CreateEntry($"{TerrainDefinition.DefaultAnnotationLayerPath}::Sections::Grid", unchecked((int)0xFFB4B4B4), unchecked((int)0xFF808080), 0.13),
                    CreateEntry($"{TerrainDefinition.DefaultAnnotationLayerPath}::Sections::Ticks", unchecked((int)0xFF808080), unchecked((int)0xFF000000), 0.18),
                    CreateEntry($"{TerrainDefinition.DefaultAnnotationLayerPath}::Sections::Labels", unchecked((int)0xFF000000), unchecked((int)0xFF000000), 0.13),
                    CreateEntry($"{TerrainDefinition.DefaultAnnotationLayerPath}::Sections::CutFill::Cut", TerrainSectionAnalysisDefinitionBase.DefaultCutColorArgb, unchecked((int)0xFFEB462D), 0.13),
                    CreateEntry($"{TerrainDefinition.DefaultAnnotationLayerPath}::Sections::CutFill::Fill", TerrainSectionAnalysisDefinitionBase.DefaultFillColorArgb, unchecked((int)0xFF4C849E), 0.13)
                }
            }
        };
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
