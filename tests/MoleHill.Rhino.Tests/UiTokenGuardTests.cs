using System.Text.RegularExpressions;
using MoleHill.Core.Tests;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// Style guard for the Rhino UI. Every colour the panel and its dialogs draw is a named token on
/// <c>UiTheme</c>, and every padding and spacing value is a step of the <c>UiMetrics</c> scale. That is
/// what lets a design decision (quieter borders, a tighter rhythm, a dark-theme fix) land as one edit to
/// a token instead of a sweep through every card, and it is what stops the number of individual styles
/// creeping back up one "just this once" literal at a time.
///
/// Flagged: a colour built from literal numbers (<c>Color.FromArgb(80, 80, 80)</c>,
/// <c>new Color(0f, 0f, 0f, 0.5f)</c>), a named Eto colour other than <c>Colors.Transparent</c>, and a
/// non-zero literal padding or spacing. <c>SystemColors</c> is flagged too: Rhino themes its panels apart
/// from the system palette, so a system colour (black control ink in a dark Rhino) is as foreign as a
/// literal. Colours converted from data (a layer, a zone, a ramp stop) are
/// not literals and pass. Only <c>UiTheme.cs</c> and <c>UiMetrics.cs</c> may hold the numbers.
/// </summary>
public class UiTokenGuardTests
{
    private const string UiFolder = "src/MoleHill.Rhino/UI/";

    private static readonly HashSet<string> TokenFiles = new(StringComparer.Ordinal)
    {
        UiFolder + "UiTheme.cs",
        UiFolder + "UiMetrics.cs",
    };

    private static readonly Regex LiteralColour = new(
        @"Color\.From(?:Argb|Rgb)\(\s*\d+(?:\s*,\s*\d+)*\s*\)" +
        @"|new\s+Color\(\s*[\d.]+f?(?:\s*,\s*[\d.]+f?)+\s*\)" +
        @"|\bColors\.(?!Transparent\b)\w+" +
        @"|\bSystemColors\.\w+",
        RegexOptions.Compiled);

    private static readonly Regex LiteralSpacing = new(
        @"new\s+Padding\(\s*(?:0\s*,\s*)*[1-9]" +
        @"|new\s+Padding\([^)]*,\s*[1-9]\d*\s*[,)]" +
        @"|\b(?:Spacing|DefaultSpacing|Padding)\s*=\s*[1-9]" +
        @"|\b(?:Spacing|DefaultSpacing)\s*=\s*new\s+Size\([^)]*\b[1-9]",
        RegexOptions.Compiled);

    [Fact]
    public void UiColours_AreThemeTokens()
    {
        var offenders = FindMatches(LiteralColour);
        Assert.True(
            offenders.Count == 0,
            "A colour literal outside UiTheme. Name it as a token on UiTheme (with its light/dark values) and use that:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void UiSpacing_ComesFromTheMetricScale()
    {
        var offenders = FindMatches(LiteralSpacing);
        Assert.True(
            offenders.Count == 0,
            "A literal padding/spacing outside UiMetrics. Use a scale step (SpaceSmall, SpaceLarge, ...) or a named " +
            "alias (StackGap, DialogPadding, ...); do not add an off-scale number:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    private static readonly Regex LocaleNumber = new(@"CultureInfo\.CurrentCulture", RegexOptions.Compiled);

    /// <summary>
    /// Numbers in the panel are written with a point, as Rhino writes them, and read through
    /// <c>UserNumber</c>. The panel mixed the Windows locale ("64,941") with the steppers' point ("0.000"),
    /// and a stepper read a typed "3.5" as 35 or 0 on a comma-decimal machine.
    /// </summary>
    [Fact]
    public void UiNumbers_GoThroughUserNumber()
    {
        var offenders = FindMatches(LocaleNumber);
        Assert.True(
            offenders.Count == 0,
            "A number formatted or parsed with the Windows locale in UI/. Use MoleHill.Core.Analysis.UserNumber " +
            "(point decimal, tolerant read):" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    private static List<string> FindMatches(Regex pattern)
    {
        string root = RepositoryPaths.FindRoot();
        var offenders = new List<string>();

        foreach (string file in RepositoryPaths.EnumerateShippedSources(root))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (!relative.StartsWith(UiFolder, StringComparison.Ordinal) || TokenFiles.Contains(relative))
                continue;

            int lineNumber = 0;
            foreach (string line in File.ReadLines(file))
            {
                lineNumber++;
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("*", StringComparison.Ordinal))
                    continue;

                if (pattern.IsMatch(line))
                    offenders.Add($"{relative}:{lineNumber}: {(trimmed.Length > 120 ? trimmed[..120] : trimmed)}");
            }
        }

        return offenders;
    }
}
