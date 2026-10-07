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
}
