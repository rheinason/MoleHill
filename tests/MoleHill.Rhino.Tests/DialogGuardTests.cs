using System.Text.RegularExpressions;
using MoleHill.Core.Tests;
using Xunit;

namespace MoleHill.Rhino.Tests;

/// <summary>
/// Every modal dialog is built in <c>UiDialogs</c>' frame. Before it, each dialog assembled its own: the
/// primary button came first in some and last in others, headings were bold in one and muted bold in the
/// next, labels ended in a colon in a few, and none of the inputs had the panel's input look. These checks
/// are what keep a new dialog from starting that drift again.
/// </summary>
public class DialogGuardTests
{
    private const string RhinoProject = "src/MoleHill.Rhino/";
    private const string DialogsFile = RhinoProject + "UI/UiDialogs.cs";

    private static readonly Regex DialogButtons = new(@"\b(?:DefaultButton|AbortButton)\s*=", RegexOptions.Compiled);

    // A label written with a trailing colon: Text = "Tolerance:" or Label("Color:").
    private static readonly Regex ColonLabel = new(@"(?:\bText\s*=\s*|\bLabel\()\$?""[^""]*:""", RegexOptions.Compiled);

    private static readonly Regex CreatesDialog = new(@"\bnew\s+(?:[\w.]+\.)?Dialog\b|:\s*(?:[\w.]+\.)?Dialog\b", RegexOptions.Compiled);

    [Fact]
    public void DialogButtons_AreSetByUiDialogsActions()
    {
        var offenders = Scan(DialogButtons, RhinoProject, except: DialogsFile);
        Assert.True(
            offenders.Count == 0,
            "A dialog sets its Default/Abort button itself. Build the button row with UiDialogs.Actions, which " +
            "puts the primary action and Cancel in Rhino's order and wires Enter and Escape:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void Labels_HaveNoTrailingColon()
    {
        var offenders = Scan(ColonLabel, RhinoProject + "UI/");
        Assert.True(
            offenders.Count == 0,
            "A label ending in a colon. Labels are Title Case with no colon, on cards and in dialogs alike " +
            "(UiDialogs.Field):" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void EveryDialog_IsFramedByUiDialogs()
    {
        string root = RepositoryPaths.FindRoot();
        var offenders = new List<string>();
        foreach (string file in RepositoryPaths.EnumerateShippedSources(root))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (!relative.StartsWith(RhinoProject, StringComparison.Ordinal) || relative == DialogsFile)
                continue;

            string text = File.ReadAllText(file);
            if (CreatesDialog.IsMatch(text) && !text.Contains("UiDialogs.Frame(", StringComparison.Ordinal))
                offenders.Add(relative);
        }

        Assert.True(
            offenders.Count == 0,
            "A file creates a dialog without UiDialogs.Frame (title, padding, Rhino styling):" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>Proves the patterns match what they claim to, so a regex typo cannot make a check vacuous.</summary>
    [Theory]
    [InlineData("        DefaultButton = ok;", true)]
    [InlineData("        dialog.AbortButton = cancel;", true)]
    [InlineData("        new Label { Text = \"Tolerance:\" },", true)]
    [InlineData("        UiControls.Label(\"Color:\")", true)]
    [InlineData("        new Label { Text = \"Tolerance\" },", false)]
    [InlineData("        _status.Text = $\"Could not save: {ex.Message}\";", false)]
    public void Patterns_MatchWhatTheyGuard(string line, bool flagged)
    {
        bool matched = DialogButtons.IsMatch(line) || ColonLabel.IsMatch(line);
        Assert.Equal(flagged, matched);
    }

    private static List<string> Scan(Regex pattern, string folder, string? except = null)
    {
        string root = RepositoryPaths.FindRoot();
        var offenders = new List<string>();
        foreach (string file in RepositoryPaths.EnumerateShippedSources(root))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (!relative.StartsWith(folder, StringComparison.Ordinal) || relative == except)
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
