namespace MoleHill.Core.Interop;

/// <summary>
/// A description field split into its parts: the code itself, the figure number that separates two runs
/// of the same code, and whatever markers the crew appended.
/// </summary>
/// <param name="Code">The bare code, uppercased. Empty when the description carried none.</param>
/// <param name="FigureNumber">
/// The trailing integer that makes <c>EP1</c> and <c>EP2</c> two different runs, or null for a bare code.
/// Null and 0 are different keys: a file mixing <c>EP</c> and <c>EP0</c> keeps two runs.
/// </param>
/// <param name="IsStart">A start marker was present — force a new run even if one is open.</param>
/// <param name="IsEnd">An end marker was present — close the run after adding this point.</param>
/// <param name="IsArc">This point lies on an arc through its neighbours.</param>
/// <param name="IsClose">Close the run into a loop.</param>
/// <param name="IsContinuation">The continuation suffix was present.</param>
public readonly record struct ParsedFieldCode(
    string Code,
    int? FigureNumber,
    bool IsStart,
    bool IsEnd,
    bool IsArc,
    bool IsClose,
    bool IsContinuation)
{
    public bool HasCode => !string.IsNullOrEmpty(Code);

    /// <summary>The key that decides which open run a point joins.</summary>
    public string RunKey => FigureNumber is { } figure
        ? string.Concat(Code, "#", figure.ToString(System.Globalization.CultureInfo.InvariantCulture))
        : Code;
}
