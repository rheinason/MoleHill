using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.UI;

namespace MoleHill.Rhino.UI;

/// <summary>
/// Modal layer picker for bulk-adding zones: shows a searchable, checkable list of document layers
/// (with each layer's color dot). Layers already backing a zone appear checked and disabled. On OK,
/// <see cref="SelectedLayerPaths"/> holds the newly checked layer paths (one zone per layer).
/// </summary>
internal sealed class ZoneLayerPickerDialog : Dialog<bool>
{
    private sealed class LayerRow
    {
        public required string Path { get; init; }
        public required string LowerPath { get; init; }
        public required bool AlreadyUsed { get; init; }
        public required CheckBox Check { get; init; }
        public required Control Container { get; init; }
    }

    private readonly List<LayerRow> _rows = new();
    private readonly StackLayout _list;
    private readonly TextBox _search;

    public IReadOnlyList<string> SelectedLayerPaths { get; private set; } = Array.Empty<string>();

    private ZoneLayerPickerDialog(RhinoDoc doc, ISet<string> usedLayerPaths)
    {
        Title = "Add Zones From Layers";
        Resizable = true;
        Padding = 12;
        MinimumSize = new Size(360, 440);
        this.UseRhinoStyle();

        _search = new TextBox { PlaceholderText = "Find a layer..." };
        _search.TextChanged += (_, _) => RebuildVisible();

        _list = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 2,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        var scroll = new Scrollable
        {
            Content = _list,
            Border = BorderType.None,
            ExpandContentWidth = true,
            ExpandContentHeight = false
        };
        UiControls.DisableHorizontalScrolling(scroll);

        foreach (var layer in doc.Layers)
        {
            if (layer.IsDeleted)
                continue;

            string path = layer.FullPath;
            bool used = usedLayerPaths.Contains(path);
            var check = new CheckBox
            {
                Text = used ? $"{path}  (already a zone)" : path,
                Checked = used,
                Enabled = !used
            };
            var dot = new Panel
            {
                Width = 12,
                Height = 12,
                BackgroundColor = new Color(layer.Color.R / 255f, layer.Color.G / 255f, layer.Color.B / 255f)
            };
            var row = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Padding(4, 2),
                Items = { dot, new StackLayoutItem(check, expand: true) }
            };
            _rows.Add(new LayerRow
            {
                Path = path,
                LowerPath = path.ToLowerInvariant(),
                AlreadyUsed = used,
                Check = check,
                Container = row
            });
        }

        var okButton = new Button { Text = "Add Zones" };
        okButton.Click += (_, _) =>
        {
            SelectedLayerPaths = _rows
                .Where(r => !r.AlreadyUsed && r.Check.Checked == true)
                .Select(r => r.Path)
                .ToList();
            Result = true;
            Close();
        };
        var cancelButton = new Button { Text = "Cancel" };
        cancelButton.Click += (_, _) =>
        {
            Result = false;
            Close();
        };
        DefaultButton = okButton;
        AbortButton = cancelButton;

        var layout = new DynamicLayout { Spacing = new Size(8, 8) };
        layout.AddRow(new Label
        {
            Text = "Pick layers to turn into zones. Each checked layer becomes one zone.",
            Wrap = WrapMode.Word
        });
        layout.AddRow(_search);
        layout.Add(scroll, yscale: true);
        layout.AddRow(new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Items = { null, okButton, cancelButton }
        });
        Content = layout;

        RebuildVisible();
    }

    public static IReadOnlyList<string>? ShowDialog(RhinoDoc doc, ISet<string> usedLayerPaths)
    {
        var dialog = new ZoneLayerPickerDialog(doc, usedLayerPaths);
        return dialog.ShowModal(RhinoEtoApp.MainWindowForDocument(doc)) ? dialog.SelectedLayerPaths : null;
    }

    private void RebuildVisible()
    {
        string query = (_search.Text ?? string.Empty).Trim().ToLowerInvariant();
        _list.Items.Clear();
        foreach (var row in _rows)
        {
            if (query.Length > 0 && !row.LowerPath.Contains(query))
                continue;

            _list.Items.Add(new StackLayoutItem(row.Container, HorizontalAlignment.Stretch));
        }

        if (_list.Items.Count == 0)
        {
            _list.Items.Add(new StackLayoutItem(new Panel
            {
                Padding = new Padding(6, 4),
                Content = new Label { Text = "No matching layers.", TextColor = UiTheme.MutedText }
            }, HorizontalAlignment.Stretch));
        }
    }
}
