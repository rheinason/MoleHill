using System.Text.RegularExpressions;
using MoleHill.Core.Tests;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// Registry guard. Analyses and annotations are dispatched through their descriptors
/// (<c>AnalysisTypeRegistry</c> / <c>AnnotationTypeRegistry</c>): what a type builds, what its card says
/// and how it is labelled are members of its descriptor, so adding a type means writing one class. A
/// <c>case XxxAnalysisDefinition</c> or <c>x is XxxAnnotationDefinition</c> anywhere else is the old
/// central switch growing back, and the next type then has to be remembered in every one of them.
///
/// Only <c>Registry/</c> and <c>Model/</c> may name a concrete type this way. Everything else listed
/// below is a known leftover with the reason it has not moved. The list is a ratchet: a file that stops
/// matching must be removed from it (the second test), so it can only shrink.
/// </summary>
public class ContentTypeSwitchGuardTests
{
    private const string StateStageReason = "Reads a type's own fields to decide how to colour or fingerprint it; moves with the preview/fingerprint descriptor hook.";

    /// <summary>file (repo-relative, forward slashes) → why a concrete-type test is still allowed there.</summary>
    private static readonly Dictionary<string, string> Exemptions = new()
    {
        // Being reshaped by the concurrent persistence work; exempt by name until that lands.
        ["src/MoleHill.Rhino/Services/Persistence/TerrainSerializer.cs"] = "JSON discriminator and legacy-shape handling; being moved behind the descriptor by the persistence work.",
        ["src/MoleHill.Rhino/Services/Persistence/TerrainUnitScaler.cs"] = "Per-type unit scaling; being moved onto the descriptors by the persistence work.",

        ["src/MoleHill.Rhino/Services/Output/LayerRoutingMigration.cs"] = "One-time migration of saved documents; names the historical shape of each type.",
        ["src/MoleHill.Rhino/Services/Display/TerrainAnalysisPreviewBuilder.cs"] = "Preview colouring switches on the analysis that colours the mesh; a preview hook on AnalysisTypeDescriptor is the next step.",
        ["src/MoleHill.Rhino/Services/Annotation/TerrainLegendBuilder.cs"] = "The legend keys whatever analysis colours the terrain; shares the preview-hook follow-up.",
        ["src/MoleHill.Rhino/Services/Sculpt/SculptAnalysisColorizer.cs"] = "Sculpt's live recolour reads the colouring analysis' own range fields; shares the preview-hook follow-up.",
        ["src/MoleHill.Rhino/Services/Build/TerrainBuildService.Fingerprints.cs"] = StateStageReason,
        ["src/MoleHill.Rhino/Services/Build/TerrainBuildService.Drainage.cs"] = "Reads the catchment merge setting inside a routing shared with the ponding card.",
        ["src/MoleHill.Rhino/Services/Build/TerrainBuildService.Analysis.cs"] = "The Legend is drawn by TerrainController.RefreshLegends with the preview colouring, never by the build; the skip stays explicit here.",
        ["src/MoleHill.Rhino/Services/Import/DocumentNorth.cs"] = "Asks whether any enabled Aspect card exists, before the document's north is read.",
        ["src/MoleHill.Rhino/UI/MoleHillPanel.Analysis.cs"] = "Bespoke Before-rows (slope unit selector, reference pickers) and per-type collapsed summaries still switch here.",
        ["src/MoleHill.Rhino/UI/MoleHillPanel.Annotations.cs"] = "Bespoke Before/After rows (insertion-origin pickers, section blocks) and per-type collapsed summaries still switch here.",
        ["src/MoleHill.Rhino/UI/MoleHillPanel.AnnotationStyle.cs"] = "Chooses which style rows apply to block-based annotations.",
        ["src/MoleHill.Rhino/UI/MoleHillPanel.ColorRamp.cs"] = "Slope's ramp editor works in the slope card's own unit.",
        ["src/MoleHill.Rhino/UI/MoleHillPanel.Schema.cs"] = "Two schema rows with type-specific editors (the going table; contour colour).",
    };

    // `case X`, `is X`, `is not X`, `or X`, `and not X` and switch-expression arms `X x =>`, where X is a
    // concrete or base content-definition type.
    private static readonly Regex Keyword = new(
        @"\b(?:case|is|or|and|not)\s+(?:\w+\.)*\w+(?:Analysis|Annotation)Definition\b|\w+(?:Analysis|Annotation)Definition(?:\s+\w+)?\s*=>",
        RegexOptions.Compiled);

    [Fact]
    public void ContentTypeSwitches_LiveInTheRegistry_OrAreExempted()
    {
        var offenders = FindMatches()
            .Where(pair => !Exemptions.ContainsKey(pair.Key))
            .Select(pair => $"{pair.Key}: {string.Join(" | ", pair.Value.Take(3))}")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "A concrete analysis/annotation type is named in a switch or type test outside Registry/ and Model/. " +
            "Put the behaviour on the type's descriptor instead:" + Environment.NewLine +
            string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void Exemptions_AreRemoved_OnceTheirFileNoLongerMatches()
    {
        Dictionary<string, List<string>> matches = FindMatches();

        var stale = Exemptions.Keys.Where(file => !matches.ContainsKey(file)).ToList();

        Assert.True(
            stale.Count == 0,
            "These files no longer name a concrete content type; delete their exemption: " + string.Join(", ", stale));
    }

    private static Dictionary<string, List<string>> FindMatches()
    {
        string root = RepositoryPaths.FindRoot();
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (string file in RepositoryPaths.EnumerateShippedSources(root))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.StartsWith("src/MoleHill.Rhino/Registry/", StringComparison.Ordinal) ||
                relative.StartsWith("src/MoleHill.Rhino/Model/", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (string line in File.ReadLines(file))
            {
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("*", StringComparison.Ordinal))
                    continue;

                if (!Keyword.IsMatch(line))
                    continue;

                if (!result.TryGetValue(relative, out List<string>? hits))
                    result[relative] = hits = new List<string>();
                hits.Add(trimmed.Length > 120 ? trimmed[..120] : trimmed);
            }
        }

        return result;
    }
}
