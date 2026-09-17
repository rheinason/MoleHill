using System.Text.Json;
using System.Text.Json.Serialization;
using MoleHill.Core.Interop;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Reads and writes the per-user field code table.
///
/// <b>There is no document-embedded twin, and that is the deliberate difference from
/// <see cref="LayerTemplateStore"/>.</b> A layer template has one because a document must keep
/// <i>drawing</i> consistently for whoever opens it next — routing and appearance are read on every
/// build. A code table is consumed once, at import, and what it leaves behind is ordinary curves on
/// ordinary layers. The document has nothing left to remember, so embedding a copy would add a second
/// resolution order to explain and buy nothing. Sharing between machines is <see cref="Export"/> and
/// <see cref="Import"/>.
/// </summary>
internal sealed class FieldCodeTableStore
{
    private const string TableFileName = "field-codes.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// The user's table, creating the shipped default on first run.
    ///
    /// A corrupt or unreadable file falls back to the default and rewrites it rather than failing. The
    /// table is a convenience, not a document: refusing to import a survey because a settings file was
    /// truncated would be the wrong trade.
    /// </summary>
    public FieldCodeTable Load()
    {
        string path = GetStorePath();
        if (!File.Exists(path))
        {
            FieldCodeTable defaults = FieldCodeTable.CreateDefault();
            Save(defaults);
            return defaults;
        }

        if (TryRead(path, out FieldCodeTable? table) && table != null)
            return table;

        FieldCodeTable fallback = FieldCodeTable.CreateDefault();
        Save(fallback);
        return fallback;
    }

    public void Save(FieldCodeTable table)
    {
        string path = GetStorePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteTo(path, table);
    }

    public string GetStorePath()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "MoleHill", TableFileName);
    }

    /// <summary>A fresh copy of the shipped table, without touching the saved file.</summary>
    public FieldCodeTable GetDefaultTable() => FieldCodeTable.CreateDefault();

    /// <summary>Writes the table to a path the user chose, for sharing with another machine or office.</summary>
    public static void Export(string path, FieldCodeTable table) => WriteTo(path, table);

    /// <summary>Reads a table from a path the user chose. False when the file is not one.</summary>
    public static bool Import(string path, out FieldCodeTable? table) => TryRead(path, out table);

    internal static bool TryRead(string path, out FieldCodeTable? table)
    {
        table = null;
        try
        {
            string json = File.ReadAllText(path);
            FieldCodeTable? parsed = JsonSerializer.Deserialize<FieldCodeTable>(json, JsonOptions);
            if (parsed == null)
                return false;

            table = Normalize(parsed);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static void WriteTo(string path, FieldCodeTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        FieldCodeTable normalized = Normalize(table.Clone());
        File.WriteAllText(path, JsonSerializer.Serialize(normalized, JsonOptions));
    }

    /// <summary>
    /// Trims the table into a shape the parser can rely on: codes uppercased and deduplicated, blank
    /// rules and blank marker tokens dropped.
    ///
    /// Two rules for one code would make the winner depend on list order, which is invisible in the
    /// editor — so the first is kept and the rest discarded at the point the file is read, not left to
    /// surprise someone mid-import.
    /// </summary>
    internal static FieldCodeTable Normalize(FieldCodeTable table)
    {
        table.Rules ??= new List<FieldCodeRule>();
        table.StartTokens = CleanTokens(table.StartTokens);
        table.EndTokens = CleanTokens(table.EndTokens);
        table.ArcTokens = CleanTokens(table.ArcTokens);
        table.CloseTokens = CleanTokens(table.CloseTokens);
        table.ContinuationSuffix = table.ContinuationSuffix?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(table.UnmatchedLayer))
            table.UnmatchedLayer = FieldCodeTable.DefaultUnmatchedLayer;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rules = new List<FieldCodeRule>(table.Rules.Count);
        foreach (FieldCodeRule rule in table.Rules)
        {
            if (rule == null || string.IsNullOrWhiteSpace(rule.Code))
                continue;

            rule.Code = rule.Code.Trim().ToUpperInvariant();
            rule.Layer = rule.Layer?.Trim() ?? string.Empty;
            rule.Description = rule.Description?.Trim() ?? string.Empty;
            if (!Enum.IsDefined(rule.Role))
                rule.Role = FieldCodeRole.Breakline;

            if (seen.Add(rule.Code))
                rules.Add(rule);
        }

        table.Rules = rules;
        table.Version = FieldCodeTable.CurrentVersion;
        return table;
    }

    private static List<string> CleanTokens(List<string>? tokens)
    {
        var cleaned = new List<string>();
        if (tokens == null)
            return cleaned;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string token in tokens)
        {
            if (string.IsNullOrWhiteSpace(token))
                continue;

            string trimmed = token.Trim().ToUpperInvariant();
            if (seen.Add(trimmed))
                cleaned.Add(trimmed);
        }

        return cleaned;
    }
}
