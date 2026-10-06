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
        // The role converter comes first so it, not the general enum converter, reads roles: a role name
        // written by a newer build must cost that one rule, not the whole table.
        Converters = { new TolerantRoleConverter(), new JsonStringEnumConverter() }
    };

    /// <summary>Stands in for a role name this build does not know, until the rule is dropped on read.</summary>
    private const FieldCodeRole UnknownRole = (FieldCodeRole)(-1);

    /// <summary>
    /// The user's table, creating the shipped default on first run.
    ///
    /// An unreadable file falls back to the default <i>in memory</i> and is never overwritten here. The
    /// table is a convenience, so refusing to import a survey over it would be the wrong trade — but the
    /// file is the user's work, and "unreadable" is as often a Dropbox lock or a table written by a newer
    /// build as it is corruption. Rewriting it with the defaults would destroy it for a transient reason.
    /// <paramref name="warning"/> says what happened so the caller can tell the user.
    /// </summary>
    public FieldCodeTable Load(out string? warning) => LoadFrom(GetStorePath(), out warning);

    /// <summary>Writes the table, first backing up an existing file this build could not fully read.</summary>
    public void Save(FieldCodeTable table) => SaveTo(GetStorePath(), table);

    internal static FieldCodeTable LoadFrom(string path, out string? warning)
    {
        warning = null;
        if (!File.Exists(path))
        {
            FieldCodeTable defaults = FieldCodeTable.CreateDefault();

            // Writing the first-run file is a courtesy (it gives the user something to find and edit),
            // so failing to write it must not fail the load.
            try
            {
                SaveTo(path, defaults);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
            }

            return defaults;
        }

        if (TryRead(path, out FieldCodeTable? table, out string? readWarning) && table != null)
        {
            warning = readWarning;
            return table;
        }

        warning = $"MoleHill: could not read the field code table at {path}; using the shipped codes for now. " +
                  "The file was left untouched — fix or remove it, or save from mhEditFieldCodes (the old file is backed up first).";
        return FieldCodeTable.CreateDefault();
    }

    internal static void SaveTo(string path, FieldCodeTable table)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        BackUpIfNotFullyReadable(path);
        WriteTo(path, table);
    }

    /// <summary>
    /// Copies an existing file aside before it is overwritten, when this build could not read all of it.
    /// That is the one moment the user's unreadable table would otherwise be lost for good.
    /// </summary>
    private static void BackUpIfNotFullyReadable(string path)
    {
        if (!File.Exists(path) || (TryRead(path, out _, out string? warning) && warning == null))
            return;

        string backup = $"{path}.bak-{DateTime.Now:yyyyMMdd-HHmmss}";
        File.Copy(path, backup, overwrite: true);
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

    internal static bool TryRead(string path, out FieldCodeTable? table) => TryRead(path, out table, out _);

    /// <summary>
    /// Reads a table. A rule whose role this build does not know is dropped rather than failing the
    /// file, and <paramref name="warning"/> names it: its points then arrive as unmatched and are
    /// reported, which is honest, where guessing a role for them would not be.
    /// </summary>
    internal static bool TryRead(string path, out FieldCodeTable? table, out string? warning)
    {
        table = null;
        warning = null;
        try
        {
            string json = File.ReadAllText(path);
            FieldCodeTable? parsed = JsonSerializer.Deserialize<FieldCodeTable>(json, JsonOptions);
            if (parsed == null)
                return false;

            parsed.Rules ??= new List<FieldCodeRule>();
            List<string> unknown = parsed.Rules
                .Where(static rule => rule != null && rule.Role == UnknownRole)
                .Select(static rule => rule.Code)
                .ToList();
            if (unknown.Count > 0)
            {
                parsed.Rules.RemoveAll(static rule => rule != null && rule.Role == UnknownRole);
                warning = $"MoleHill: {unknown.Count} field code rule(s) in {Path.GetFileName(path)} use a role this " +
                          $"version does not know and were skipped: {string.Join(", ", unknown)}.";
            }

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
    /// Codes that more than one rule claims, compared the way <see cref="Normalize"/> dedupes them.
    ///
    /// The editor checks this before saving because <see cref="Normalize"/> keeps the first rule and
    /// silently drops the rest — and the dropped one is usually the row the user just edited.
    /// Normalize keeps doing that for loads, where a hand-edited file has no one to ask.
    /// </summary>
    internal static List<string> FindDuplicateCodes(IEnumerable<FieldCodeRule> rules)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicates = new List<string>();
        foreach (FieldCodeRule rule in rules)
        {
            if (rule == null || string.IsNullOrWhiteSpace(rule.Code))
                continue;

            string code = rule.Code.Trim().ToUpperInvariant();
            if (!seen.Add(code) && !duplicates.Contains(code))
                duplicates.Add(code);
        }

        return duplicates;
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

        table.UnmatchedLayer = FieldCodeTable.StripLegacyLayerPrefix(table.UnmatchedLayer);
        if (string.IsNullOrWhiteSpace(table.UnmatchedLayer))
            table.UnmatchedLayer = FieldCodeTable.DefaultUnmatchedLayer;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rules = new List<FieldCodeRule>(table.Rules.Count);
        foreach (FieldCodeRule rule in table.Rules)
        {
            if (rule == null || string.IsNullOrWhiteSpace(rule.Code))
                continue;

            rule.Code = rule.Code.Trim().ToUpperInvariant();
            rule.Layer = FieldCodeTable.StripLegacyLayerPrefix(rule.Layer);
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

    /// <summary>Reads a role name or number; a name this build does not know becomes <see cref="UnknownRole"/>.</summary>
    private sealed class TolerantRoleConverter : JsonConverter<FieldCodeRole>
    {
        public override FieldCodeRole Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int number))
                return (FieldCodeRole)number;

            if (reader.TokenType == JsonTokenType.String &&
                Enum.TryParse(reader.GetString(), ignoreCase: true, out FieldCodeRole role) &&
                Enum.IsDefined(role))
            {
                return role;
            }

            if (reader.TokenType is JsonTokenType.String or JsonTokenType.Number)
                return UnknownRole;

            throw new JsonException($"Expected a field code role, found {reader.TokenType}.");
        }

        public override void Write(Utf8JsonWriter writer, FieldCodeRole value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
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
