using Eto.Drawing;
using Eto.Forms;
using MoleHill.Rhino.Registry;
using Rhino;
using Rhino.UI;

namespace MoleHill.Rhino.UI;

// Colour helpers: the optional-colour editor row, ARGB and opacity packing, and swatch/label text for
// layer and terrain colours.
public sealed partial class MoleHillPanel : Panel
{
    private Control CreateOptionalColorEditor(
        string label,
        int? colorArgb,
        Action<int?> onCommit,
        string help,
        int? fallbackColorArgb = null,
        string defaultText = "(by layer)")
    {
        var swatch = new Panel
        {
            Width = 18,
            Height = 18,
            BackgroundColor = ResolveOptionalColorSwatch(colorArgb, fallbackColorArgb)
        };
        ApplyHelp(swatch, help);

        string assignedText = colorArgb.HasValue ? DescribeSolidColor(colorArgb.Value) : defaultText;
        var assignedLabel = new Label
        {
            Text = EllipsizeText(assignedText, 24),
            VerticalAlignment = VerticalAlignment.Center,
            Wrap = WrapMode.None
        };
        ApplyHelp(assignedLabel, $"{help}\n{assignedText}");

        void PickColor()
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null)
                return;

            int initialArgb = colorArgb ?? fallbackColorArgb ?? unchecked((int)0xFF808080);
            var colorDialog = new ColorDialog
            {
                Color = ToEtoColor(System.Drawing.Color.FromArgb(initialArgb))
            };

            if (colorDialog.ShowDialog(RhinoEtoApp.MainWindowForDocument(doc)) != DialogResult.Ok)
                return;

            onCommit(ToArgb(colorDialog.Color));
        }

        swatch.MouseDown += (_, e) =>
        {
            if (e.Buttons == MouseButtons.Primary)
                PickColor();
        };

        var pickButton = MakeInlineButton("Pick", (_, _) => PickColor(), "Choose an explicit color for this output.");
        var clearButton = MakeInlineButton("Clear", (_, _) => onCommit(null), "Clear the explicit color and use the layer color instead.");
        var summaryRow = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                swatch,
                new StackLayoutItem(assignedLabel, expand: true)
            }
        };
        var buttonRow = CreateResponsiveControlGroup(4, pickButton, clearButton);
        var editor = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Padding(0, 1),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayoutItem(summaryRow, expand: true),
                buttonRow
            }
        };
        return new PropertyRow(CreateHelpLabel(label, help, 0), editor, expandWidget: true);
    }

    private static int GetOpacityPercent(int argb)
    {
        int alpha = System.Drawing.Color.FromArgb(argb).A;
        return (int)Math.Round((alpha / 255.0) * 100.0);
    }

    private static int WithOpacityPercent(int argb, int opacityPercent)
    {
        var color = System.Drawing.Color.FromArgb(argb);
        int alpha = (int)Math.Round(Math.Clamp(opacityPercent, 0, 100) / 100.0 * 255.0);
        return System.Drawing.Color.FromArgb(alpha, color.R, color.G, color.B).ToArgb();
    }

    private static int ToArgb(Color color)
    {
        int alpha = (int)Math.Round(Math.Clamp(color.A, 0f, 1f) * 255.0);
        int red = (int)Math.Round(Math.Clamp(color.R, 0f, 1f) * 255.0);
        int green = (int)Math.Round(Math.Clamp(color.G, 0f, 1f) * 255.0);
        int blue = (int)Math.Round(Math.Clamp(color.B, 0f, 1f) * 255.0);
        return System.Drawing.Color.FromArgb(alpha, red, green, blue).ToArgb();
    }

    private static Color ResolveOptionalColorSwatch(int? colorArgb, int? fallbackColorArgb)
    {
        int argb = colorArgb ?? fallbackColorArgb ?? unchecked((int)0xFF808080);
        return ToEtoColor(System.Drawing.Color.FromArgb(argb));
    }

    private static string DescribeSolidColor(int argb)
    {
        var color = System.Drawing.Color.FromArgb(argb);
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private static int? ResolveLayerColorArgb(string? layerPath) => AnalysisFormatting.ResolveLayerColorArgb(layerPath);

    private static string DescribeTerrainColor(int argb)
    {
        var color = System.Drawing.Color.FromArgb(argb);
        return $"#{color.R:X2}{color.G:X2}{color.B:X2} · {GetOpacityPercent(argb)}%";
    }

    private static Color ToEtoColor(System.Drawing.Color c) => Color.FromArgb(c.R, c.G, c.B, c.A);
}
