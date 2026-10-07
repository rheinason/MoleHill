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

    /// <summary>A slope stored as degrees, shown in the user's chosen slope unit.</summary>
    public static string FormatSlopeDegrees(double degrees) =>
        SlopeInput.FormatWithUnit(Math.Tan(degrees * Math.PI / 180.0), SlopeUnitPreference.Current);
}
