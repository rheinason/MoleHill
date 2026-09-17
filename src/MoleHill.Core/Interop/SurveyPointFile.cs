namespace MoleHill.Core.Interop;

/// <summary>
/// The result of reading a survey point file: the points that mapped, and an account of everything that
/// did not.
///
/// The diagnostics are not an error channel — a file that yields points <i>and</i> diagnostics is the
/// normal case, and both halves are reported. A reader that returned only the points would be hiding the
/// rows it could not read, which in a survey is exactly the linework somebody then re-draws by hand
/// without knowing why it is missing.
/// </summary>
public sealed class SurveyPointFile
{
    public SurveyPointFile(
        IReadOnlyList<SurveyPoint> points,
        IReadOnlyList<SurveyReadDiagnostic> diagnostics,
        char delimiter,
        int dataLineCount)
    {
        Points = points;
        Diagnostics = diagnostics;
        Delimiter = delimiter;
        DataLineCount = dataLineCount;
    }

    public IReadOnlyList<SurveyPoint> Points { get; }

    public IReadOnlyList<SurveyReadDiagnostic> Diagnostics { get; }

    /// <summary>The separator actually used, whether it was stated or detected.</summary>
    public char Delimiter { get; }

    /// <summary>Non-blank, non-comment lines seen — so a caller can say "812 of 815 rows read".</summary>
    public int DataLineCount { get; }

    public bool HasPoints => Points.Count > 0;
}
