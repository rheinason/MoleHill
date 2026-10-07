using System.Text.RegularExpressions;
using Xunit;

namespace MoleHill.Core.Tests;

/// <summary>
/// Guard for the modifier type registry. A modifier's behaviour belongs on its
/// <c>ModifierTypeDescriptor</c> (<c>Registry/</c>) or on its definition (<c>Model/</c>). A
/// <c>case XxxModifierDefinition</c> or <c>is XxxModifierDefinition</c> anywhere else is a type switch
/// that a new modifier would have to be added to by hand, which is the shape the registry exists to
/// remove: the collapsed-card summary, the stage dispatch and the Add menu all used to be such switches.
///
/// <para>The remaining uses are listed below, with the number of occurrences each file may hold and the
/// reason it is legitimate. A new use fails the test; either route it through the descriptor, or add the
/// file here with a reason. Only modifier definitions are guarded — analyses and annotations have their
/// own descriptors and their own sweep.</para>
/// </summary>
public class ModifierTypeSwitchGuardTests
{
    private const string BasePinned = "The pinned base Triangulate is a document-structure rule (always first, cannot be moved or deleted), not per-type behaviour.";
    private const string BespokeUi = "Bespoke card UI beyond the parameter schema (the AppendBespokeModifierRows escape hatch).";

    /// <summary>file (repo-relative, forward slashes) → (occurrences allowed, reason).</summary>
    private static readonly Dictionary<string, (int Max, string Reason)> Exemptions = new()
    {
        ["src/MoleHill.Rhino/Services/Build/TerrainBuildService.Tin.cs"] = (1, "Grade Path look-ahead while assembling the TIN: a cross-stage rule between wall insertion and the paths above it. A descriptor hook would be the natural home."),
        ["src/MoleHill.Rhino/Services/Build/TerrainBuildService.cs"] = (3, "Stage input collection: Add Geometry's boundary owner and Project To's target-terrain dependency are cross-terrain rules, not per-stage behaviour."),
        ["src/MoleHill.Rhino/Services/Commands/DocumentCommandService.cs"] = (2, BasePinned),
        ["src/MoleHill.Rhino/Services/Controller/TerrainController.Build.cs"] = (8, "Five are the 'expensive stage' set for the slow-build warning (a descriptor flag would replace them); two pick Smooth/Sculpt for protect-curve checks; one copies In-Situ Stair results between terrains."),
        ["src/MoleHill.Rhino/Services/Controller/TerrainController.Diagnostics.cs"] = (3, "Smooth/Sculpt protect-curve diagnostics and a Remesh-present test; per-type diagnostics that have no descriptor hook yet."),
        ["src/MoleHill.Rhino/Services/Controller/TerrainController.Edits.cs"] = (3, BasePinned),
        ["src/MoleHill.Rhino/Services/Controller/TerrainController.Terrains.cs"] = (3, "Seeding and updating the pinned base Triangulate from the selection when a terrain is created."),
        ["src/MoleHill.Rhino/Services/Output/LayerRoutingMigration.cs"] = (1, "Load-time migration of legacy Retaining Wall layers; it must name the type it migrates."),
        ["src/MoleHill.Rhino/Services/Persistence/TerrainUnitScaler.cs"] = (1, "The encoded sculpt field is not a plain multiply, so Sculpt keeps explicit scaling code."),
        ["src/MoleHill.Rhino/Services/Sculpt/SculptConstraintMaskBuilder.cs"] = (1, "Sculpt reads the Grade Path stages stacked above it to build its protect mask: a deliberate cross-type dependency."),
        ["src/MoleHill.Rhino/Services/Sculpt/SculptSessionController.cs"] = (2, "The interactive sculpt session is Sculpt-specific by definition."),
        ["src/MoleHill.Rhino/UI/MoleHillPanel.Modifiers.cs"] = (11, BasePinned + " Also " + BespokeUi),
        ["src/MoleHill.Rhino/UI/MoleHillPanel.Schema.cs"] = (4, "IsBespokePositionedModifierParameter: rows the schema declares but the card positions by hand (" + "Triangulate, Grade Path, Project To, geometry-input)."),
        ["src/MoleHill.Rhino/UI/MoleHillPanel.Tabs.cs"] = (1, BasePinned),
    };

    private static readonly Regex TypePattern = new(
        @"\b(?:case|is|as|or|not)\s+\(?\s*\w+ModifierDefinition\b(?!\.)",
        RegexOptions.Compiled);

    [Fact]
    public void ModifierTypeSwitches_LiveOnlyInRegistryAndModel_OrAreDeclared()
    {
        string root = RepositoryPaths.FindRoot();
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);

        foreach (string file in RepositoryPaths.EnumerateShippedSources(root))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.StartsWith("src/MoleHill.Rhino/Registry/", StringComparison.Ordinal) ||
                relative.StartsWith("src/MoleHill.Rhino/Model/", StringComparison.Ordinal))
            {
                continue;
            }

            int count = 0;
            foreach (string line in File.ReadLines(file))
            {
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                    continue;
                count += TypePattern.Matches(line).Count;
            }

            if (count > 0)
                counts[relative] = count;
        }

        var problems = new List<string>();
        foreach ((string file, int count) in counts)
        {
            if (!Exemptions.TryGetValue(file, out var allowed))
                problems.Add($"{file}: {count} modifier type pattern(s), not exempt");
            else if (count > allowed.Max)
                problems.Add($"{file}: {count} modifier type pattern(s), exemption allows {allowed.Max}");
        }

        foreach ((string file, var allowed) in Exemptions)
        {
            if (!counts.ContainsKey(file))
                problems.Add($"{file}: exempt ({allowed.Reason}) but no longer has any; remove the exemption");
        }

        Assert.True(problems.Count == 0, Environment.NewLine + string.Join(Environment.NewLine, problems));
    }
}
