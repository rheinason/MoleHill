using System.Text;

namespace MoleHill.Rhino.Model;

/// <summary>
/// The per-terrain layer root. A layer template writes its paths with <see cref="Token"/> where the
/// terrain's name goes (<c>MoleHill {terrain}::Terrain</c>), and the role table substitutes it when it
/// is built, so every terrain in a document gets a layer tree of its own instead of all of them
/// sharing one <c>MoleHill</c> root and fighting over it.
///
/// Pure string logic with no document access, so it is testable without Rhino.
/// </summary>
public static class TerrainLayerNaming
{
    public const string Token = "{terrain}";

    /// <summary>The root every shipped path hangs from.</summary>
    public const string DefaultRoot = "MoleHill " + Token;

    /// <summary>The literal root every document and template used before roots were per terrain.</summary>
    public const string LegacyRoot = "MoleHill";

    public const string FallbackName = "Terrain";

    private static readonly char[] DisallowedNameCharacters = "\\/:;,*?\"<>|{}[]()".ToCharArray();

    /// <summary>
    /// A layer-name-safe segment: anything a Rhino layer name cannot hold becomes a space and runs of
    /// whitespace collapse. Empty when nothing usable is left.
    /// </summary>
    public static string CleanSegment(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var cleaned = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (char.IsControl(c) || Array.IndexOf(DisallowedNameCharacters, c) >= 0)
                cleaned.Append(' ');
            else
                cleaned.Append(c);
        }

        return string.Join(" ", cleaned.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>The segment a terrain's name becomes in a layer path. Never empty.</summary>
    public static string Sanitize(string? terrainName)
    {
        string cleaned = CleanSegment(terrainName);
        return cleaned.Length == 0 ? FallbackName : cleaned;
    }

    public static bool ContainsToken(string? path) =>
        path != null && path.Contains(Token, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The path with the token replaced by the terrain's name. A null name leaves the token in place,
    /// which is what the template editor and the registry defaults want to show.
    /// </summary>
    public static string Resolve(string path, string? terrainName) =>
        terrainName == null || !ContainsToken(path)
            ? path
            : path.Replace(Token, Sanitize(terrainName), StringComparison.OrdinalIgnoreCase);

    /// <summary>The root layer a terrain's output lives under, for example <c>MoleHill Hillside</c>.</summary>
    public static string RootFor(string? terrainName) => Resolve(DefaultRoot, terrainName);

    /// <summary>
    /// The path a pre-token document would have used for a token path: the token and the space before
    /// it are dropped, <c>MoleHill {terrain}::Terrain</c> becoming <c>MoleHill::Terrain</c>.
    /// </summary>
    public static string ToLegacyLiteral(string path) =>
        path.Replace(" " + Token, string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(Token, string.Empty, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Rewrites a path whose first segment is the literal legacy root to the per-terrain root.
    /// False (and <paramref name="rewritten"/> unchanged) when the path hangs from anything else.
    /// </summary>
    public static bool TryRewriteLegacyRoot(string path, out string rewritten)
    {
        rewritten = path;
        string trimmed = path.Trim();
        int separator = trimmed.IndexOf("::", StringComparison.Ordinal);
        string first = separator < 0 ? trimmed : trimmed[..separator];
        if (!string.Equals(first.Trim(), LegacyRoot, StringComparison.OrdinalIgnoreCase))
            return false;

        rewritten = DefaultRoot + (separator < 0 ? string.Empty : trimmed[separator..]);
        return true;
    }

    /// <summary>
    /// True when <paramref name="layerPath"/> is the terrain's root or sits underneath it. Decides
    /// which source layers a terrain owns.
    /// </summary>
    public static bool IsUnderRoot(string? layerPath, string terrainName)
    {
        if (string.IsNullOrWhiteSpace(layerPath))
            return false;

        string root = RootFor(terrainName);
        return layerPath.Equals(root, StringComparison.OrdinalIgnoreCase)
            || layerPath.StartsWith(root + "::", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A layer path moved from one terrain root to another, or null when it is not under the old one.
    /// </summary>
    public static string? Rebase(string layerPath, string oldTerrainName, string newTerrainName)
    {
        string oldRoot = RootFor(oldTerrainName);
        string newRoot = RootFor(newTerrainName);
        if (layerPath.Equals(oldRoot, StringComparison.OrdinalIgnoreCase))
            return newRoot;
        if (layerPath.StartsWith(oldRoot + "::", StringComparison.OrdinalIgnoreCase))
            return newRoot + layerPath[oldRoot.Length..];
        return null;
    }

    /// <summary>
    /// The first terrain name whose layer root is free: <paramref name="desired"/>, then
    /// <c>desired 2</c>, <c>desired 3</c>. <paramref name="taken"/> is asked about a candidate name and
    /// compares by layer segment, so two names that clean to the same layer both count as taken.
    /// </summary>
    public static string NextFreeName(string desired, Func<string, bool> taken)
    {
        ArgumentNullException.ThrowIfNull(taken);
        string baseName = string.IsNullOrWhiteSpace(desired) ? FallbackName : desired.Trim();
        if (!taken(baseName))
            return baseName;

        for (int suffix = 2; ; suffix++)
        {
            string candidate = $"{baseName} {suffix}";
            if (!taken(candidate))
                return candidate;
        }
    }

    /// <summary>True when two terrain names would share a layer root.</summary>
    public static bool SameRoot(string? a, string? b) =>
        string.Equals(Sanitize(a), Sanitize(b), StringComparison.OrdinalIgnoreCase);
}
