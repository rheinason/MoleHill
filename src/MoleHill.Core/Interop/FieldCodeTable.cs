namespace MoleHill.Core.Interop;

/// <summary>
/// The office's field code conventions: what each code means, and how a crew marks the start, end, arc
/// and closure of a run.
///
/// <b>The marker spellings are data, not constants.</b> Hard-coding "ST" and "END" would make the feature
/// work for one office and fail silently for the next — the codes would still parse, the runs would
/// simply never close where they should, which reads as the crew not having used the convention. Every
/// token list below is therefore editable and saved with the table.
/// </summary>
public sealed class FieldCodeTable
{
    /// <summary>Schema version of the saved file, so a later shape can migrate rather than reset.</summary>
    public int Version { get; set; }

    public List<FieldCodeRule> Rules { get; set; } = new();

    /// <summary>Tokens that open a run, e.g. ST, BEG, START.</summary>
    public List<string> StartTokens { get; set; } = new();

    /// <summary>Tokens that close a run, e.g. END, FIN.</summary>
    public List<string> EndTokens { get; set; } = new();

    /// <summary>Tokens marking a point as lying on an arc through its neighbours.</summary>
    public List<string> ArcTokens { get; set; } = new();

    /// <summary>Tokens that close a run into a loop.</summary>
    public List<string> CloseTokens { get; set; } = new();

    /// <summary>
    /// Suffix meaning "this point continues the open run of this code", conventionally a hyphen.
    ///
    /// Empty disables the convention, which matters for an office whose codes legitimately end in the
    /// character.
    /// </summary>
    public string ContinuationSuffix { get; set; } = "-";

    /// <summary>
    /// Layer that points whose code has no rule are created on, so nothing is ever dropped.
    ///
    /// Deliberately not one of the role layers below. An unmatched point's meaning is unknown, and
    /// dropping it onto the breakline or spot layer would feed it to the terrain as though it had been
    /// understood — which is worse than losing it, because it is then wrong rather than missing.
    /// </summary>
    public string UnmatchedLayer { get; set; } = DefaultUnmatchedLayer;

    public const string DefaultUnmatchedLayer = "MoleHill::Inputs::Unmatched Codes";

    /// <summary>
    /// Where a role's output goes when a rule does not name its own layer.
    ///
    /// These are the input layers the layer template already ships — the ones it describes as "the plain
    /// layers a user draws their own inputs and feature curves on, which nothing routes to". Survey
    /// linework is exactly that, so it belongs there rather than in a parallel tree of its own.
    ///
    /// <b>One layer per role, not per code, and that is forced rather than chosen.</b> A layer source
    /// resolves objects whose layer index matches exactly — sublayers of an assigned layer are not
    /// collected — so codes nested under a role layer would each need assigning by hand. Defaulting all
    /// breakline codes to one layer means a single "Layers" assignment picks up the whole survey. A user
    /// who wants EP drawn separately from TC just types a layer on that rule.
    /// </summary>
    public static string DefaultLayerFor(FieldCodeRole role) => role switch
    {
        FieldCodeRole.Breakline => "MoleHill::Inputs::Breaklines",
        FieldCodeRole.Contour => "MoleHill::Inputs::Contours",
        FieldCodeRole.Boundary => "MoleHill::Inputs::Boundary",
        FieldCodeRole.Spot => "MoleHill::Inputs::Spots",
        _ => string.Empty
    };

    /// <summary>The layer a rule's output lands on: its own when set, otherwise the role's default.</summary>
    public static string ResolveLayer(FieldCodeRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return string.IsNullOrWhiteSpace(rule.Layer) ? DefaultLayerFor(rule.Role) : rule.Layer.Trim();
    }

    /// <summary>
    /// Whether a typed layer path has a shape a document can hold: <c>::</c>-separated segments, none
    /// blank, none padded with whitespace, none carrying control characters.
    ///
    /// Structural only — the host adds its own naming rules on top. It exists because an unusable path
    /// does not fail when the layer is created: it quietly lands the output on whatever layer happens
    /// to be current, which is the one place this command must never put anything.
    /// </summary>
    public static bool IsValidLayerPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        foreach (string segment in path.Split("::"))
        {
            if (segment.Length == 0 ||
                segment.Trim().Length != segment.Length ||
                segment.Any(char.IsControl) ||
                segment.Contains(':'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Finds the rule for a code, case-insensitively, or null when the table does not know it.</summary>
    public FieldCodeRule? Find(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;

        foreach (FieldCodeRule rule in Rules)
        {
            if (string.Equals(rule.Code, code, StringComparison.OrdinalIgnoreCase))
                return rule;
        }

        return null;
    }

    public bool IsStartToken(string token) => Contains(StartTokens, token);

    public bool IsEndToken(string token) => Contains(EndTokens, token);

    public bool IsArcToken(string token) => Contains(ArcTokens, token);

    public bool IsCloseToken(string token) => Contains(CloseTokens, token);

    private static bool Contains(List<string> tokens, string token)
    {
        foreach (string candidate in tokens)
        {
            if (!string.IsNullOrWhiteSpace(candidate) &&
                string.Equals(candidate, token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A starting table covering the codes almost every survey carries. Deliberately small: a table
    /// pre-filled with fifty guesses would be edited by deletion, and a wrong rule is worse than a
    /// missing one because a missing one is reported.
    /// </summary>
    public static FieldCodeTable CreateDefault() => new()
    {
        Version = CurrentVersion,
        StartTokens = new List<string> { "ST", "BEG", "START" },
        EndTokens = new List<string> { "END", "FIN" },
        ArcTokens = new List<string> { "AR", "ARC", "CRV" },
        CloseTokens = new List<string> { "CL", "CLOSE" },
        ContinuationSuffix = "-",
        UnmatchedLayer = DefaultUnmatchedLayer,

        // Layers are left empty so every rule follows its role. That is what makes one "Layers"
        // assignment pick up the whole survey, and it keeps the shipped table honest about the fact
        // that the code's meaning, not its name, decides where it goes.
        Rules = new List<FieldCodeRule>
        {
            new() { Code = "EP", Role = FieldCodeRole.Breakline, Description = "Edge of pavement" },
            new() { Code = "TC", Role = FieldCodeRole.Breakline, Description = "Top of kerb" },
            new() { Code = "TOE", Role = FieldCodeRole.Breakline, Description = "Toe of slope" },
            new() { Code = "TOP", Role = FieldCodeRole.Breakline, Description = "Top of slope" },
            new() { Code = "CL", Role = FieldCodeRole.Breakline, Description = "Centreline" },
            new() { Code = "BDY", Role = FieldCodeRole.Boundary, Description = "Site boundary", ClosedByDefault = true },
            new() { Code = "BLD", Role = FieldCodeRole.Boundary, Description = "Building footprint", ClosedByDefault = true },
            new() { Code = "SPOT", Role = FieldCodeRole.Spot, Description = "Spot level" },
            new() { Code = "GND", Role = FieldCodeRole.Spot, Description = "Ground shot" }
        }
    };

    /// <summary>Current schema version written by <see cref="CreateDefault"/> and the store.</summary>
    public const int CurrentVersion = 1;

    public FieldCodeTable Clone() => new()
    {
        Version = Version,
        Rules = Rules.Select(static rule => rule.Clone()).ToList(),
        StartTokens = new List<string>(StartTokens),
        EndTokens = new List<string>(EndTokens),
        ArcTokens = new List<string>(ArcTokens),
        CloseTokens = new List<string>(CloseTokens),
        ContinuationSuffix = ContinuationSuffix,
        UnmatchedLayer = UnmatchedLayer
    };
}
