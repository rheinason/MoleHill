namespace MoleHill.Core.Interop;

/// <summary>
/// Splits a raw description into a <see cref="ParsedFieldCode"/>.
///
/// The grammar is the intersection of what field crews actually type: a code, optionally carrying a
/// figure number as trailing digits and/or a continuation suffix, followed by any number of marker tokens
/// separated by spaces, commas or full stops. <c>EP1</c>, <c>EP 1</c>, <c>EP.ST</c>, <c>EP2 AR</c> and
/// <c>EP-</c> all read the way the person typing them expects.
///
/// Marker spellings come from the <see cref="FieldCodeTable"/> rather than from here, so an office whose
/// crews write <c>BEG</c> instead of <c>ST</c> configures rather than recompiles.
/// </summary>
public static class FieldCodeParser
{
    private static readonly char[] TokenSeparators = { ' ', '\t', '.', ',', '/' };

    public static ParsedFieldCode Parse(string? rawDescription, FieldCodeTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (string.IsNullOrWhiteSpace(rawDescription))
            return new ParsedFieldCode(string.Empty, null, false, false, false, false, false);

        string[] tokens = rawDescription.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return new ParsedFieldCode(string.Empty, null, false, false, false, false, false);

        string head = tokens[0];
        bool isContinuation = false;
        if (table.ContinuationSuffix.Length > 0 &&
            head.Length > table.ContinuationSuffix.Length &&
            head.EndsWith(table.ContinuationSuffix, StringComparison.Ordinal))
        {
            isContinuation = true;
            head = head[..^table.ContinuationSuffix.Length];
        }

        SplitTrailingDigits(head, out string code, out int? figureNumber);

        bool isStart = false;
        bool isEnd = false;
        bool isArc = false;
        bool isClose = false;

        for (int i = 1; i < tokens.Length; i++)
        {
            string token = tokens[i];

            // A bare integer after the code is the figure number written with a separator ("EP 1"), but
            // only when the code did not already carry one — "EP1 2" is a code with a figure number
            // followed by something this table does not know, and inventing a second meaning for it
            // would be a guess.
            if (figureNumber is null &&
                int.TryParse(token, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int separated))
            {
                figureNumber = separated;
                continue;
            }

            if (table.IsStartToken(token))
                isStart = true;
            else if (table.IsEndToken(token))
                isEnd = true;
            else if (table.IsArcToken(token))
                isArc = true;
            else if (table.IsCloseToken(token))
                isClose = true;
        }

        return new ParsedFieldCode(
            code.ToUpperInvariant(),
            figureNumber,
            isStart,
            isEnd,
            isArc,
            isClose,
            isContinuation);
    }

    /// <summary>
    /// Peels a trailing run of digits off a code. <c>EP12</c> is edge-of-pavement figure 12.
    ///
    /// A code that is entirely digits keeps its digits and gets no figure number — a numeric code is a
    /// code, and reading "101" as "figure 101 of the empty code" would merge every numerically-coded
    /// point in the file into one run.
    /// </summary>
    private static void SplitTrailingDigits(string head, out string code, out int? figureNumber)
    {
        int end = head.Length;
        while (end > 0 && char.IsAsciiDigit(head[end - 1]))
            end--;

        if (end == 0 || end == head.Length)
        {
            code = head;
            figureNumber = null;
            return;
        }

        code = head[..end];
        figureNumber = int.TryParse(
            head[end..],
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out int parsed)
            ? parsed
            : null;
    }
}
