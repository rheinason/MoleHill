namespace MoleHill.Core.Interop;

/// <summary>
/// One surveyed point as it was written in the file, after column mapping and unit-free numeric parsing.
///
/// <see cref="X"/> and <see cref="Y"/> are already in the document's axis sense — the reader resolves
/// easting to X and northing to Y, so nothing downstream has to remember which format was read. See
/// <see cref="SurveyColumnMap"/> for why that matters.
///
/// <see cref="RawDescription"/> is the untouched description field. Splitting it into a code, a figure
/// number and markers is <c>FieldCodeParser</c>'s job, not the reader's: the reader must stay usable for
/// a file whose conventions the code table does not yet know.
/// </summary>
/// <param name="Number">The point number, when the format carries one. Null is normal, not an error.</param>
/// <param name="LineNumber">1-based line in the source file, for diagnostics the user can act on.</param>
public readonly record struct SurveyPoint(
    int? Number,
    double X,
    double Y,
    double Z,
    string RawDescription,
    int LineNumber);
