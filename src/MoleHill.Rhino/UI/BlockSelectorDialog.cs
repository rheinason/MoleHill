using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.UI;

namespace MoleHill.Rhino.UI;

/// <summary>
/// A lightweight, Insert-style picker that lists the document's block definitions and lets the user
/// select several at once to add to a scatter card. Returns the chosen block definition names, or null
/// if cancelled. A first-stab selector: name list + filter + multi-select (thumbnails are a later step).
/// </summary>
internal sealed class BlockSelectorDialog : Dialog<List<string>?>
{
    private sealed class BlockRow
    {
        public required string Name { get; init; }

        public Bitmap? Thumbnail { get; init; }
    }

    private readonly GridView _grid;
    private readonly List<BlockRow> _allRows;

    private BlockSelectorDialog(IReadOnlyList<(string Name, Bitmap? Thumbnail)> blocks)
    {
        Title = "Add Blocks to Scatter";
        Resizable = true;
        Padding = 12;
        MinimumSize = new Size(360, 480);
        this.UseRhinoStyle();

        _allRows = blocks
            .Where(block => !string.IsNullOrWhiteSpace(block.Name))
            .Select(block => new BlockRow { Name = block.Name, Thumbnail = block.Thumbnail })
            .ToList();

        _grid = new GridView
        {
            DataStore = _allRows,
            AllowMultipleSelection = true,
            ShowHeader = false,
            RowHeight = 44,
            Size = new Size(340, 380)
        };
        _grid.Columns.Add(new GridColumn
        {
            HeaderText = "Block",
            Editable = false,
            Expand = true,
            DataCell = new ImageTextCell
            {
                ImageBinding = Binding.Property((BlockRow row) => (Image?)row.Thumbnail),
                TextBinding = Binding.Property((BlockRow row) => row.Name)
            }
        });

        var addButton = new Button { Text = "Add Selected" };
        var cancelButton = new Button { Text = "Cancel" };
        DefaultButton = addButton;
        AbortButton = cancelButton;

        addButton.Click += (_, _) =>
        {
            Result = _grid.SelectedItems.OfType<BlockRow>().Select(row => row.Name).ToList();
            Close();
        };
        cancelButton.Click += (_, _) =>
        {
            Result = null;
            Close();
        };

        // Double-clicking a row adds just that block.
        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.Item is BlockRow row)
            {
                Result = new List<string> { row.Name };
                Close();
            }
        };

        var filter = new TextBox { PlaceholderText = "Filter blocks…" };
        filter.TextChanged += (_, _) =>
        {
            string text = filter.Text?.Trim() ?? string.Empty;
            _grid.DataStore = string.IsNullOrEmpty(text)
                ? _allRows
                : _allRows.Where(row => row.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
        };

        var layout = new DynamicLayout { Spacing = new Size(8, 8) };
        if (_allRows.Count == 0)
        {
            layout.AddRow(new Label
            {
                Text = "No block definitions found in this document. Insert a block first.",
                Wrap = WrapMode.Word
            });
        }
        else
        {
            layout.AddRow(new Label { Text = "Select one or more blocks to scatter." });
            layout.AddRow(filter);
            layout.AddRow(_grid);
        }

        layout.AddRow(new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Items = { null, DefaultButton, AbortButton }
        });
        Content = layout;
    }

    public static List<string>? Show(RhinoDoc doc, IReadOnlyList<(string Name, Bitmap? Thumbnail)> blocks)
    {
        var dialog = new BlockSelectorDialog(blocks);
        return dialog.ShowModal(RhinoEtoApp.MainWindowForDocument(doc));
    }
}
