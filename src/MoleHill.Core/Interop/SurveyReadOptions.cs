namespace MoleHill.Core.Interop;

/// <summary>
/// How to read a survey point file. Everything here is a property of <i>this delivery</i>, which is why
/// none of it is stored: the next file from the same surveyor can differ.
/// </summary>
public sealed class SurveyReadOptions
{
    /// <summary>
    /// The field separator, or null to detect it from the file.
    ///
    /// Detection is offered because the user often does not know, and guessing wrong is visible
    /// immediately in a preview — unlike a wrong column map, which is not. See
    /// <see cref="SurveyPointFile.Delimiter"/> for what was actually used.
    /// </summary>
    public char? Delimiter { get; set; }

    /// <summary>Column mapping. Required — there is no safe default, since PNEZD and ENZ disagree.</summary>
    public SurveyColumnMap ColumnMap { get; set; } = new();

    /// <summary>Rows to skip before the first data row, for a file that carries a header.</summary>
    public int HeaderRowCount { get; set; }

    /// <summary>
    /// Line prefixes that mark a comment. Semicolon and hash are both in the wild; a line that begins
    /// with one is skipped silently rather than reported, because it is not malformed.
    /// </summary>
    public IReadOnlyList<string> CommentPrefixes { get; set; } = new[] { "#", ";", "//" };

    /// <summary>
    /// Stop after this many points, or 0 for all of them. The dialog preview reads a handful; the import
    /// reads everything. Diagnostics still cover only what was read.
    /// </summary>
    public int MaxPoints { get; set; }

    /// <summary>
    /// Multiplier from file units to model units, applied to X, Y and Z alike.
    ///
    /// A scale rather than a unit enum, because Core has no opinion about the document: the Rhino side
    /// resolves "US survey feet into a metre document" to a number and hands it over.
    /// </summary>
    public double UnitScale { get; set; } = 1.0;

    /// <summary>
    /// Added to Z after <see cref="UnitScale"/>, in model units, to reconcile a vertical datum.
    ///
    /// This is on the read and not on the document's project base for a reason worth repeating: a
    /// vertical datum belongs to the <i>delivery</i>, not to the site. Two surveys on two datums can
    /// perfectly well land in one document, which a document-level setting could not express.
    /// </summary>
    public double VerticalOffset { get; set; }

    /// <summary>The separators tried when <see cref="Delimiter"/> is null, in order of preference.</summary>
    public static IReadOnlyList<char> CandidateDelimiters { get; } = new[] { ',', '\t', ';', ' ' };
}
