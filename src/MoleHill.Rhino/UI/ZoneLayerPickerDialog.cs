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
        UiDialogs.Frame(this, "Add Zones From Layers", new Size(360, 440), resizable: true);

        _search = UiDialogs.Search("layers");
        _search.TextChanged += (_, _) => RebuildVisible();

        _list = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = UiMetrics.SpaceXSmall,
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
                Spacing = UiMetrics.DialogRowSpacing,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Padding(UiMetrics.SpaceSmall, UiMetrics.SpaceXSmall),
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

        var okButton = UiDialogs.Button("Add Zones", (_, _) =>
        {
            SelectedLayerPaths = _rows
                .Where(r => !r.AlreadyUsed && r.Check.Checked == true)
                .Select(r => r.Path)
                .ToList();
            Result = true;
            Close();
        });
        var cancelButton = UiDialogs.Button("Cancel", (_, _) =>
        {
            Result = false;
            Close();
        });

        var layout = new DynamicLayout { Spacing = UiMetrics.DialogGap };
        layout.AddRow(UiDialogs.Intro("Pick layers to turn into zones. Each checked layer becomes one zone."));
        layout.AddRow(_search);
        layout.Add(scroll, yscale: true);
        layout.AddRow(UiDialogs.Actions(this, okButton, cancelButton));
        UiDialogs.SetContent(this, layout);

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
                Padding = new Padding(UiMetrics.SpaceMedium, UiMetrics.SpaceSmall),
                Content = UiDialogs.Note("No matching layers.")
            }, HorizontalAlignment.Stretch));
        }
    }
}
