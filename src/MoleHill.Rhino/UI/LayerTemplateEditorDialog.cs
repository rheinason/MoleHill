using System.Diagnostics;
using System.Text.Json;
using Eto.Forms;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.UI;

namespace MoleHill.Rhino.UI;

internal sealed class LayerTemplateEditorDialog : Dialog<bool>
{
    private readonly LayerTemplateStore _store;
    private readonly TextArea _jsonEditor = new() { Wrap = false, Size = new Eto.Drawing.Size(860, 520) };

    private LayerTemplateEditorDialog(LayerTemplateStore store)
    {
        _store = store;
        Title = "MoleHill Layer Templates";
        Resizable = true;
        Padding = 12;
        MinimumSize = new Eto.Drawing.Size(900, 640);
        DefaultButton = new Button { Text = "Save" };
        AbortButton = new Button { Text = "Cancel" };
        this.UseRhinoStyle();

        DefaultButton.Click += (_, _) =>
        {
            try
            {
                var templates = JsonSerializer.Deserialize<List<LayerTemplateDefinition>>(_jsonEditor.Text ?? string.Empty);
                if (templates == null || templates.Count == 0)
                    throw new InvalidOperationException("At least one template is required.");

                _store.SaveTemplates(templates);
                Result = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Invalid Layer Template JSON", MessageBoxButtons.OK, MessageBoxType.Error);
            }
        };

        AbortButton.Click += (_, _) =>
        {
            Result = false;
            Close();
        };

        var resetButton = new Button { Text = "Reset To Defaults" };
        resetButton.Click += (_, _) => LoadJson(_store.LoadTemplates());

        var openFolderButton = new Button { Text = "Open Settings Folder" };
        openFolderButton.Click += (_, _) =>
        {
            string? folder = Path.GetDirectoryName(_store.GetStorePath());
            if (string.IsNullOrWhiteSpace(folder))
                return;

            Directory.CreateDirectory(folder);
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = folder,
                    UseShellExecute = true,
                    Verb = "open"
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Open Settings Folder Failed", MessageBoxButtons.OK, MessageBoxType.Error);
            }
        };

        var layout = new DynamicLayout { Spacing = new Eto.Drawing.Size(8, 8) };
        layout.AddRow(new Label
        {
            Text = "Edit local layer templates as JSON. Each entry supports Path, ColorArgb, PrintColorArgb, and PlotWeight.",
            Wrap = WrapMode.Word
        });
        layout.AddRow(_jsonEditor);
        layout.AddRow(new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Items =
            {
                DefaultButton,
                AbortButton,
                resetButton,
                openFolderButton
            }
        });
        Content = layout;

        LoadJson(store.LoadTemplates());
    }

    public static bool ShowDialog(RhinoDoc doc, LayerTemplateStore store)
    {
        var dialog = new LayerTemplateEditorDialog(store);
        return dialog.ShowModal(RhinoEtoApp.MainWindowForDocument(doc));
    }

    private void LoadJson(IReadOnlyList<LayerTemplateDefinition> templates)
    {
        _jsonEditor.Text = JsonSerializer.Serialize(templates, new JsonSerializerOptions { WriteIndented = true });
    }
}
