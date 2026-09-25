using System.Globalization;

namespace MoleHill.Core.Interop;

/// <summary>
/// Reads a delimited survey point file into <see cref="SurveyPoint"/>s.
///
/// Deliberately does no interpretation of the description: the code, its figure number and its markers
/// are <c>FieldCodeParser</c>'s business. Keeping them apart means a file whose office conventions are
/// not yet in the code table still reads, and the user can see the coordinates before writing any rules.
///
/// Numbers are parsed invariant-culture. A survey file is an interchange format written by an instrument
/// or an office package, so its decimal point does not follow the reader's locale; parsing "1234,56" as
/// a comma decimal would also collide with the commonest delimiter.
///
/// <b>Thousands separators are not accepted either.</b> Allowing them turns a comma-decimal value such
/// as <c>512345,67</c> (the natural export from a semicolon-delimited, comma-decimal locale) into
/// <c>51234567</c> — a coordinate a hundred times too large that parses, draws, and looks like a survey.
/// A row that fails loudly with a line number is the better outcome; no survey exporter writes grouped
/// digits into a coordinate column.
/// </summary>
public static class SurveyPointFileReader
{
    public static SurveyPointFile Read(string content, SurveyReadOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.ColumnMap);
        content ??= string.Empty;

        // A UTF-8 BOM survives File.ReadAllText when the encoding is guessed, and would otherwise glue
        // itself to the first field — turning a perfectly good point number into a parse failure on
        // line 1 only. Cheap to strip, confusing to diagnose.
        if (content.Length > 0 && content[0] == '﻿')
            content = content[1..];

        string[] lines = content.Split('\n');
        SurveyColumnMap map = options.ColumnMap;
        var points = new List<SurveyPoint>();
        var diagnostics = new List<SurveyReadDiagnostic>();

        char delimiter = options.Delimiter ?? DetectDelimiter(lines, options, map.RequiredColumnCount);
        int dataLineCount = 0;
        int headerRowsSkipped = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            // Split on '\n' leaves the '\r' of a CRLF file on the end of every line.
            string line = lines[i].TrimEnd('\r');
            int lineNumber = i + 1;

            if (string.IsNullOrWhiteSpace(line) || IsComment(line, options))
                continue;

            if (headerRowsSkipped < options.HeaderRowCount)
            {
                headerRowsSkipped++;
                continue;
            }

            dataLineCount++;
            if (options.MaxPoints > 0 && points.Count >= options.MaxPoints)
                break;

            string[] fields = SplitRow(line, delimiter);
            if (fields.Length < map.RequiredColumnCount)
            {
                diagnostics.Add(new SurveyReadDiagnostic(
                    lineNumber,
                    $"Expected at least {map.RequiredColumnCount} columns but found {fields.Length}."));
                continue;
            }

            if (!TryParseCoordinate(fields[map.EastingColumn], out double easting))
            {
                diagnostics.Add(Unparseable(lineNumber, "easting", map.EastingColumn, fields));
                continue;
            }

            if (!TryParseCoordinate(fields[map.NorthingColumn], out double northing))
            {
                diagnostics.Add(Unparseable(lineNumber, "northing", map.NorthingColumn, fields));
                continue;
            }

            if (!TryParseCoordinate(fields[map.ElevationColumn], out double elevation))
            {
                diagnostics.Add(Unparseable(lineNumber, "elevation", map.ElevationColumn, fields));
                continue;
            }

            int? number = null;
            if (map.NumberColumn is { } numberColumn &&
                int.TryParse(fields[numberColumn].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedNumber))
            {
                number = parsedNumber;
            }

            points.Add(new SurveyPoint(
                number,
                easting * options.UnitScale,
                northing * options.UnitScale,
                (elevation * options.UnitScale) + options.VerticalOffset,
                ReadDescription(fields, map, delimiter),
                lineNumber));
        }

        return new SurveyPointFile(points, diagnostics, delimiter, dataLineCount);
    }

    /// <summary>
    /// Picks the separator that maps the most lines successfully, preferring the earlier candidate on a
    /// tie so a comma-delimited file is never read as space-delimited.
    ///
    /// Scoring by successful maps rather than by field count matters for space detection: a description
    /// with spaces in it inflates the field count without making the row any more readable.
    /// </summary>
    private static char DetectDelimiter(string[] lines, SurveyReadOptions options, int requiredColumns)
    {
        char best = ',';
        int bestScore = -1;

        foreach (char candidate in SurveyReadOptions.CandidateDelimiters)
        {
            int score = 0;
            int inspected = 0;
            foreach (string raw in lines)
            {
                if (inspected >= 50)
                    break;

                string line = raw.TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(line) || IsComment(line, options))
                    continue;

                inspected++;
                string[] fields = SplitRow(line, candidate);
                if (fields.Length >= requiredColumns)
                    score++;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// Splits one row, honouring RFC 4180 double quotes so a quoted description containing the delimiter
    /// stays one field. Runs of whitespace collapse when the delimiter is a space, because a
    /// column-aligned file pads with several.
    /// </summary>
    private static string[] SplitRow(string line, char delimiter)
    {
        if (delimiter == ' ')
            return line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == delimiter)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields.ToArray();
    }

    private static string ReadDescription(string[] fields, SurveyColumnMap map, char delimiter)
    {
        if (map.DescriptionColumn is not { } start || start >= fields.Length)
            return string.Empty;

        if (!map.DescriptionSpansRemainingColumns)
            return fields[start].Trim();

        // Rejoin with the delimiter rather than a space: the description was one field that happened to
        // contain the separator, and putting the original character back keeps "EP, START" intact for
        // whatever marker convention the code table uses.
        string joined = string.Join(delimiter, fields.Skip(start));
        return joined.Trim();
    }

    private static bool TryParseCoordinate(string field, out double value) =>
        double.TryParse(
            field.Trim(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value) && double.IsFinite(value);

    private static SurveyReadDiagnostic Unparseable(int lineNumber, string what, int column, string[] fields)
    {
        string text = column < fields.Length ? fields[column].Trim() : string.Empty;

        // Name the likely cause when the text is a number written with a decimal comma: the fix is in
        // the export settings, and "could not read" alone sends the user looking at the wrong thing.
        string hint = text.Contains(',') &&
                      double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out _)
            ? " It looks like a decimal comma; export the file with a decimal point."
            : string.Empty;

        return new SurveyReadDiagnostic(
            lineNumber,
            $"Could not read a {what} from column {column + 1} (\"{text}\").{hint}");
    }

    private static bool IsComment(string line, SurveyReadOptions options)
    {
        string trimmed = line.TrimStart();
        foreach (string prefix in options.CommentPrefixes)
        {
            if (prefix.Length > 0 && trimmed.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
