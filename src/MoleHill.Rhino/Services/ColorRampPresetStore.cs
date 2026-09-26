using System.Text.Json;
using MoleHill.Core.Analysis;

namespace MoleHill.Rhino.Services;

/// <summary>
/// The user's own saved colour ramps.
///
/// Built-ins live in <see cref="ColorRampPresets"/> and are shared by every install; these are per-user and
/// follow the person, not the document — a ramp you tuned for one site is usually the one you want on the
/// next. That is why they sit beside the layer templates in %APPDATA%/MoleHill rather than in the .3dm:
/// a ramp saved inside a document would be invisible from every other document.
///
/// Nothing here is load-bearing: a missing, unreadable, or malformed file simply means no saved ramps, and
/// the built-ins carry on. A colour preference must never be able to stop a terrain from drawing.
/// </summary>
internal static class ColorRampPresetStore
{
    private const string FileName = "color-ramps.json";

    private const int MaximumPresets = 64;

    private static readonly object Gate = new();

    private static List<ColorRampPreset>? _cache;

    /// <summary>The saved ramps, newest last. Empty when nothing has been saved or the file is unusable.</summary>
    public static IReadOnlyList<ColorRampPreset> All
    {
        get
        {
            lock (Gate)
            {
                _cache ??= Load();
                return _cache;
            }
        }
    }

    /// <summary>
    /// Saves a ramp under a name, replacing any saved ramp with the same name. Names that collide with a
    /// built-in are still allowed — the menu shows the two groups separately — because refusing a name the
    /// user typed is more annoying than a duplicate label.
    /// </summary>
    public static void Save(string name, ColorRamp ramp)
    {
        string trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            return;

        lock (Gate)
        {
            var presets = _cache ??= Load();
            presets.RemoveAll(preset => string.Equals(preset.Label, trimmed, StringComparison.OrdinalIgnoreCase));
            presets.Add(new ColorRampPreset
            {
                Key = "user:" + trimmed,
                Label = trimmed,
                Ramp = ramp
            });

            if (presets.Count > MaximumPresets)
                presets.RemoveRange(0, presets.Count - MaximumPresets);

            Persist(presets);
        }
    }

    public static void Delete(string name)
    {
        lock (Gate)
        {
            var presets = _cache ??= Load();
            if (presets.RemoveAll(preset => string.Equals(preset.Label, name, StringComparison.OrdinalIgnoreCase)) > 0)
                Persist(presets);
        }
    }

    /// <summary>Drops the in-memory copy so the next read picks the file up again.</summary>
    public static void Invalidate()
    {
        lock (Gate)
            _cache = null;
    }

    /// <summary>Beside the layer templates, so everything MoleHill keeps per user lives in one folder.</summary>
    private static string ResolvePath()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "MoleHill", FileName);
    }

    private static List<ColorRampPreset> Load()
    {
        string path = ResolvePath();
        if (!File.Exists(path))
            return new List<ColorRampPreset>();

        try
        {
            var records = JsonSerializer.Deserialize<List<StoredRamp>>(File.ReadAllText(path));
            if (records == null)
                return new List<ColorRampPreset>();

            var presets = new List<ColorRampPreset>(records.Count);
            foreach (var record in records)
            {
                if (string.IsNullOrWhiteSpace(record?.Name) || record.Stops == null)
                    continue;

                var stops = record.Stops
                    .Where(stop => stop != null && double.IsFinite(stop.Position))
                    .Select(stop => new SlopeAnalyzer.ColorStop(
                        stop!.Position,
                        (byte)((stop.ColorArgb >> 16) & 0xFF),
                        (byte)((stop.ColorArgb >> 8) & 0xFF),
                        (byte)(stop.ColorArgb & 0xFF)))
                    .ToList();

                if (stops.Count < ColorRamp.MinimumStops)
                    continue;

                presets.Add(new ColorRampPreset
                {
                    Key = "user:" + record.Name,
                    Label = record.Name,
                    Ramp = new ColorRamp(stops)
                });
            }

            return presets;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new List<ColorRampPreset>();
        }
    }

    private static void Persist(List<ColorRampPreset> presets)
    {
        try
        {
            string path = ResolvePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var records = presets.Select(preset => new StoredRamp
            {
                Name = preset.Label,
                Stops = preset.Ramp.Stops.Select(stop => new StoredStop
                {
                    Position = stop.Position,
                    ColorArgb = unchecked((int)0xFF000000) | (stop.R << 16) | (stop.G << 8) | stop.B
                }).ToList()
            }).ToList();

            File.WriteAllText(path, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The ramp stays usable in this session; it just will not survive a restart.
        }
    }

    private sealed class StoredRamp
    {
        public string? Name { get; set; }

        public List<StoredStop>? Stops { get; set; }
    }

    private sealed class StoredStop
    {
        public double Position { get; set; }

        public int ColorArgb { get; set; }
    }
}
