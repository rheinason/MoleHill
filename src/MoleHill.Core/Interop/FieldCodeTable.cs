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

    /// <summary>Layer that points whose code has no rule are created on, so nothing is ever dropped.</summary>
    public string UnmatchedLayer { get; set; } = "Survey::Unmatched";

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
        StartTokens = new List<string> { "ST", "BEG", "START" },
        EndTokens = new List<string> { "END", "FIN" },
        ArcTokens = new List<string> { "AR", "ARC", "CRV" },
        CloseTokens = new List<string> { "CL", "CLOSE" },
        ContinuationSuffix = "-",
        UnmatchedLayer = "Survey::Unmatched",
        Rules = new List<FieldCodeRule>
        {
            new() { Code = "EP", Role = FieldCodeRole.Breakline, Layer = "Survey::Edge of Pavement", Description = "Edge of pavement" },
            new() { Code = "TC", Role = FieldCodeRole.Breakline, Layer = "Survey::Top of Kerb", Description = "Top of kerb" },
            new() { Code = "TOE", Role = FieldCodeRole.Breakline, Layer = "Survey::Toe", Description = "Toe of slope" },
            new() { Code = "TOP", Role = FieldCodeRole.Breakline, Layer = "Survey::Crest", Description = "Top of slope" },
            new() { Code = "CL", Role = FieldCodeRole.Breakline, Layer = "Survey::Centreline", Description = "Centreline" },
            new() { Code = "BDY", Role = FieldCodeRole.Boundary, Layer = "Survey::Boundary", Description = "Site boundary", ClosedByDefault = true },
            new() { Code = "BLD", Role = FieldCodeRole.Boundary, Layer = "Survey::Buildings", Description = "Building footprint", ClosedByDefault = true },
            new() { Code = "SPOT", Role = FieldCodeRole.Spot, Layer = "Survey::Spot Levels", Description = "Spot level" },
            new() { Code = "GND", Role = FieldCodeRole.Spot, Layer = "Survey::Spot Levels", Description = "Ground shot" }
        }
    };

    public FieldCodeTable Clone() => new()
    {
        Rules = Rules.Select(static rule => rule.Clone()).ToList(),
        StartTokens = new List<string>(StartTokens),
        EndTokens = new List<string>(EndTokens),
        ArcTokens = new List<string>(ArcTokens),
        CloseTokens = new List<string>(CloseTokens),
        ContinuationSuffix = ContinuationSuffix,
        UnmatchedLayer = UnmatchedLayer
    };
}
