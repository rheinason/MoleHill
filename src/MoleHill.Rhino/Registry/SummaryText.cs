using System.Globalization;

namespace MoleHill.Rhino.Registry;

/// <summary>
/// The one shape of a card's summary and result text: parts joined by <see cref="Separator"/> ("Type ·
/// 2 objects · Fill 64.9%"), numbers written with a point as Rhino writes them.
///
/// <para>Summaries are dozens of interpolated strings across the descriptors and panel (<c>$"{x:G4}"</c>),
/// and interpolation formats with the thread's culture, so on a comma-decimal Windows they read "0,2"
/// beside fields that read "0.2". Rather than rewrite every site, the panel evaluates every summary and
/// result producer through <see cref="Invariant{T}"/>, which formats them all with a point at once.</para>
/// </summary>
internal static class SummaryText
{
    /// <summary>Between the parts of a summary line (design review: "Type · n objects · n layers").</summary>
    public const string Separator = " · ";

    /// <summary>
    /// A source-based card's counts, "2 objects · 1 layer" (design review UI-20): picked objects and
    /// watched layers, with the right singular or plural.
    /// </summary>
    public static string Sources(MoleHill.Rhino.Model.SourceReferenceSet sources) => Join(
        Count(sources.ObjectIds.Count, "object", "objects"),
        Count(sources.LayerPaths.Count, "layer", "layers"));

    private static string Count(int count, string one, string many) => $"{count} {(count == 1 ? one : many)}";

    /// <summary>Joins the non-empty parts with <see cref="Separator"/>.</summary>
    public static string Join(params string?[] parts) =>
        string.Join(Separator, parts.Where(part => !string.IsNullOrWhiteSpace(part)));

    /// <summary>
    /// Runs a summary or result producer with point-decimal number formatting, restoring the culture after.
    /// The panel calls producers on the UI thread, synchronously, so the change is confined to the call.
    /// </summary>
    public static T Invariant<T>(Func<T> produce)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            return produce();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
