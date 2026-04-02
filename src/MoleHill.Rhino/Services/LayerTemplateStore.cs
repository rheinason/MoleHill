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
        return new List<LayerTemplateDefinition>
        {
            new()
            {
                Name = "Landscape Starter",
                Entries = new List<LayerTemplateEntry>
                {
                    CreateEntry("Topography", unchecked((int)0xFF000000), unchecked((int)0xFF000000), 0.25),
                    CreateEntry("Topography::Contours", unchecked((int)0xFFAAAAAA), unchecked((int)0xFFAAAAAA), 0.18),
                    CreateEntry("Topography::Spots", unchecked((int)0xFF008900), unchecked((int)0xFF008900), 0.18),
                    CreateEntry("Topography::Breaklines", unchecked((int)0xFFFFC000), unchecked((int)0xFFFFC000), 0.25),
                    CreateEntry("Topography::Other_Geo", unchecked((int)0xFFE19032), unchecked((int)0xFFE19032), 0.18),
                    CreateEntry("Topography::Mend", unchecked((int)0xFF73C878), unchecked((int)0xFF73C878), 0.18),
                    CreateEntry("Topography::Boundaries", unchecked((int)0xFF5F24F8), unchecked((int)0xFF5F24F8), 0.25),
                    CreateEntry("Features", unchecked((int)0xFF7D26CD), unchecked((int)0xFF7D26CD), 0.25),
                    CreateEntry("Features::Wall_Curves", unchecked((int)0xFF00FF00), unchecked((int)0xFF00FF00), 0.25),
                    CreateEntry("Features::Wall_Curves::Wall_Spots", unchecked((int)0xFF00FF00), unchecked((int)0xFF00FF00), 0.18),
                    CreateEntry("Features::Curb_Curves", unchecked((int)0xFFBF3FFF), unchecked((int)0xFFBF3FFF), 0.25),
                    CreateEntry("Features::Curb_Curves::Curb_Spots", unchecked((int)0xFFBF3FFF), unchecked((int)0xFFBF3FFF), 0.18),
                    CreateEntry("Features::Stair_Surface", unchecked((int)0xFF000000), unchecked((int)0xFF000000), 0.25),
                    CreateEntry("Features::Path_Curve", unchecked((int)0xFFFFBF00), unchecked((int)0xFFFFBF00), 0.25)
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
