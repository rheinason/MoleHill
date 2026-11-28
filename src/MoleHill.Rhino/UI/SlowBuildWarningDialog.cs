using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.UI;

namespace MoleHill.Rhino.UI;

internal readonly record struct SlowBuildWarningDialogResult(bool ContinueBuild, bool DisableFutureWarnings);

internal static class SlowBuildWarningDialog
{
    public static SlowBuildWarningDialogResult Show(RhinoDoc doc, string message, string title)
    {
        var choice = new SlowBuildWarningDialogResult(ContinueBuild: false, DisableFutureWarnings: false);
        var disableFutureWarningsCheck = new CheckBox
        {
            Text = "Don't show this warning again for this terrain"
        };

        var dialog = new Dialog
        {
            Owner = RhinoEtoApp.MainWindowForDocument(doc),
            ClientSize = new Size(440, 150)
        };
        UiDialogs.Frame(dialog, title, new Size(440, 150));

        var continueButton = UiDialogs.Button("Continue", (_, _) =>
        {
            choice = new SlowBuildWarningDialogResult(
                ContinueBuild: true,
                DisableFutureWarnings: disableFutureWarningsCheck.Checked == true);
            dialog.Close();
        });

        var cancelButton = UiDialogs.Button("Cancel", (_, _) =>
        {
            choice = new SlowBuildWarningDialogResult(
                ContinueBuild: false,
                DisableFutureWarnings: disableFutureWarningsCheck.Checked == true);
            dialog.Close();
        });

        dialog.Closed += (_, _) =>
        {
            if (choice.DisableFutureWarnings || choice.ContinueBuild)
                return;

            choice = new SlowBuildWarningDialogResult(
                ContinueBuild: false,
                DisableFutureWarnings: disableFutureWarningsCheck.Checked == true);
        };

        UiDialogs.SetContent(dialog, new DynamicLayout
        {
            DefaultSpacing = new Size(0, UiMetrics.SpaceXLarge),
            Rows =
            {
                UiDialogs.Intro(message),
                disableFutureWarningsCheck,
                UiDialogs.Actions(dialog, continueButton, cancelButton)
            }
        });

        dialog.ShowModal();
        return choice;
    }
}
