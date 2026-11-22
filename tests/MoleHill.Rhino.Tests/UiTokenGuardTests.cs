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

    private static readonly Regex NativeStepper = new(@"\bNumericStepper\b", RegexOptions.Compiled);

    /// <summary>
    /// Every number field is a <c>NumberField</c>. Rhino's <c>NumericStepper</c> reads typed text by culture,
    /// so "3,5" (or a numpad decimal key on a comma-decimal keyboard) became 35.
    /// </summary>
    [Fact]
    public void UiNumberFields_AreNumberFields()
    {
        var offenders = FindMatches(NativeStepper);
        Assert.True(
            offenders.Count == 0,
            "A NumericStepper in UI/. Use NumberField, which reads typed numbers through UserNumber:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    // A " | " inside a string: summary and result text written with the old separator.
    private static readonly Regex PipeSeparator = new(@"""[^""]*\s\|\s|\s\|\s[^""]*""", RegexOptions.Compiled);

    /// <summary>
    /// Card summaries and results join their parts with <c>SummaryText.Separator</c> (" · "), the design's
    /// "Type · n objects · n layers". They were written with " | " in about sixty places; this keeps that
    /// from coming back one string at a time. Descriptors live in Registry/, so it scans both folders.
    /// </summary>
    [Fact]
    public void CardText_JoinsPartsWithTheSummarySeparator()
    {
        var offenders = FindMatches(PipeSeparator, "src/MoleHill.Rhino/Registry/");
        Assert.True(
            offenders.Count == 0,
            "A \" | \" separator in card text. Join parts with SummaryText.Separator (\" · \") or SummaryText.Join:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    private static List<string> FindMatches(Regex pattern, string? alsoFolder = null)
    {
        string root = RepositoryPaths.FindRoot();
        var offenders = new List<string>();

        foreach (string file in RepositoryPaths.EnumerateShippedSources(root))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            bool inScope = relative.StartsWith(UiFolder, StringComparison.Ordinal) ||
                           (alsoFolder != null && relative.StartsWith(alsoFolder, StringComparison.Ordinal));
            if (!inScope || TokenFiles.Contains(relative))
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
