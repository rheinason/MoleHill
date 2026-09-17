namespace MoleHill.Core.Interop;

/// <summary>
/// What the field code table made of a survey file: the figures, the loose points, and a full account of
/// everything the table did not recognise.
///
/// <b>Nothing is dropped.</b> A code with no rule is the normal first-run state, not an error — the user
/// has not written that rule yet — so its points are kept, counted by code in
/// <see cref="UnmatchedCodes"/>, and created on the table's unmatched layer. A reader that binned them
/// would produce a terrain missing linework for a reason nobody could see.
/// </summary>
public sealed class SurveyImportResult
{
    public SurveyImportResult(
        IReadOnlyList<SurveyFigure> figures,
        IReadOnlyList<int> spotPointIndices,
        IReadOnlyList<int> unmatchedPointIndices,
        IReadOnlyDictionary<string, int> unmatchedCodes,
        IReadOnlyList<SurveyReadDiagnostic> diagnostics,
        int ignoredPointCount)
    {
        Figures = figures;
        SpotPointIndices = spotPointIndices;
        UnmatchedPointIndices = unmatchedPointIndices;
        UnmatchedCodes = unmatchedCodes;
        Diagnostics = diagnostics;
        IgnoredPointCount = ignoredPointCount;
    }

    public IReadOnlyList<SurveyFigure> Figures { get; }

    /// <summary>Points whose rule is <see cref="FieldCodeRole.Spot"/>, plus runs too short to be lines.</summary>
    public IReadOnlyList<int> SpotPointIndices { get; }

    /// <summary>Points whose code has no rule. Created, never discarded.</summary>
    public IReadOnlyList<int> UnmatchedPointIndices { get; }

    /// <summary>Unrecognised code to how many points carried it, so the user can see what to add.</summary>
    public IReadOnlyDictionary<string, int> UnmatchedCodes { get; }

    public IReadOnlyList<SurveyReadDiagnostic> Diagnostics { get; }

    /// <summary>Points matched to an <see cref="FieldCodeRole.Ignore"/> rule: recognised and deliberately not drawn.</summary>
    public int IgnoredPointCount { get; }

    /// <summary>Label used in <see cref="UnmatchedCodes"/> for a point whose description was blank.</summary>
    public const string BlankCodeLabel = "(no code)";
}
