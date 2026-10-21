using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;

namespace MoleHill.Rhino.Registry;

/// <summary>
/// Helpers shared by the modifier descriptors' <see cref="ModifierTypeDescriptor.Summarize"/> overrides.
/// No Eto dependency, so the Rhino test project can compile and exercise the summaries.
/// </summary>
internal static class ModifierSummaryFormatting
{
    public static int CountSources(SourceReferenceSet sources) =>
        sources.ObjectIds.Count + sources.LayerPaths.Count;

    /// <summary>"1 boundary", "2 boundaries": a summary reads as words, so the noun agrees with its count.</summary>
    public static string Count(int count, string singular, string plural) =>
        count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + (count == 1 ? singular : plural);
}
