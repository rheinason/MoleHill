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

        var continueButton = new Button { Text = "Continue", Width = 96 };
        var cancelButton = new Button { Text = "Cancel", Width = 96 };

        var dialog = new Dialog
        {
            Title = title,
            Owner = RhinoEtoApp.MainWindowForDocument(doc),
            Resizable = false,
            Maximizable = false,
            Minimizable = false,
            Padding = new Padding(12),
            ClientSize = new Size(440, 150),
            MinimumSize = new Size(440, 150),
            DefaultButton = continueButton,
            AbortButton = cancelButton
        };
        dialog.UseRhinoStyle();

        continueButton.Click += (_, _) =>
        {
            choice = new SlowBuildWarningDialogResult(
                ContinueBuild: true,
                DisableFutureWarnings: disableFutureWarningsCheck.Checked == true);
            dialog.Close();
        };

        cancelButton.Click += (_, _) =>
        {
            choice = new SlowBuildWarningDialogResult(
                ContinueBuild: false,
                DisableFutureWarnings: disableFutureWarningsCheck.Checked == true);
            dialog.Close();
        };

        dialog.Closed += (_, _) =>
        {
            if (choice.DisableFutureWarnings || choice.ContinueBuild)
                return;

            choice = new SlowBuildWarningDialogResult(
                ContinueBuild: false,
                DisableFutureWarnings: disableFutureWarningsCheck.Checked == true);
        };

        dialog.Content = new DynamicLayout
        {
            DefaultSpacing = new Size(0, 10),
            Rows =
            {
                new Label
                {
                    Text = message,
                    Wrap = WrapMode.Word
                },
                disableFutureWarningsCheck,
                new Panel { Height = 2 },
                new StackLayout
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalContentAlignment = HorizontalAlignment.Right,
                    Items =
                    {
                        new StackLayoutItem(new Panel(), expand: true),
                        cancelButton,
                        continueButton
                    }
                }
            }
        };

        dialog.ShowModal();
        return choice;
    }
}
