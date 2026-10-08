using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.DocObjects;
using Rhino.UI;

namespace MoleHill.Rhino.UI;

/// <summary>
/// A lightweight, Insert-style picker that lists the document's block definitions and lets the user
/// select several at once to add to a scatter card, or one to use on an annotation card (see
/// <see cref="Options"/>). Returns the chosen block definition names, or null if cancelled. The name list opens immediately; thumbnails are generated in small UI-idle batches only
/// for rows Eto actually formats, so large block libraries do not stall the dialog before it appears.
/// </summary>
internal sealed class BlockSelectorDialog : Dialog<List<string>?>
{
    private const int ThumbnailSize = 40;
    private const double ThumbnailIntervalSeconds = 0.05;

    /// <summary>What the dialog is for: its title, prompt and button, and whether it takes several blocks.</summary>
    internal sealed record Options(
        string Title,
        string Prompt,
        string ConfirmText,
        bool AllowMultiple,
        string? SelectedName = null)
    {
        public static readonly Options Scatter = new(
            "Add Blocks to Scatter", "Select one or more blocks to scatter.", "Add Selected", true);

        public static Options Annotation(string? currentName) => new(
            "Choose Annotation Block",
            "Select the block drawn at each label.",
            "Use Block",
            false,
            currentName);
    }

    private sealed class BlockRow
    {
        public required string Name { get; init; }

        public required InstanceDefinition Definition { get; init; }

        public Bitmap? Thumbnail { get; set; }

        public bool ThumbnailLoaded { get; set; }

        public bool ThumbnailQueued { get; set; }
    }

    private readonly RhinoDoc _doc;
    private readonly GridView _grid;
    private readonly List<BlockRow> _allRows;
    private IReadOnlyList<BlockRow> _visibleRows;
    private readonly Queue<BlockRow> _thumbnailQueue = new();
    private readonly UITimer _thumbnailTimer;
    private bool _isShown;

    private BlockSelectorDialog(RhinoDoc doc, IReadOnlyList<InstanceDefinition> blocks, Options options)
    {
        _doc = doc;
        Title = options.Title;
        Resizable = true;
        Padding = 12;
        MinimumSize = new Size(360, 480);
        this.UseRhinoStyle();

        _allRows = blocks
            .Where(definition => definition != null && !string.IsNullOrWhiteSpace(definition.Name))
            .Select(definition => new BlockRow { Name = definition.Name, Definition = definition })
            .ToList();
        _visibleRows = _allRows;

        _thumbnailTimer = new UITimer { Interval = ThumbnailIntervalSeconds };
        _thumbnailTimer.Elapsed += ProcessNextThumbnail;

        _grid = new GridView
        {
            AllowMultipleSelection = options.AllowMultiple,
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
        _grid.CellFormatting += (_, e) =>
        {
            if (e.Item is BlockRow row)
                QueueThumbnail(row);
        };
        _grid.DataStore = _visibleRows;
        if (options.SelectedName is { Length: > 0 } selectedName)
        {
            BlockRow? current = _allRows.FirstOrDefault(
                row => string.Equals(row.Name, selectedName, StringComparison.OrdinalIgnoreCase));
            if (current != null)
                _grid.SelectRow(_allRows.IndexOf(current));
        }

        var addButton = new Button { Text = options.ConfirmText };
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

        // Double-clicking a row takes just that block.
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
            _visibleRows = string.IsNullOrEmpty(text)
                ? _allRows
                : _allRows.Where(row => row.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
            _grid.DataStore = _visibleRows;
        };

        Shown += (_, _) =>
        {
            _isShown = true;
            if (_thumbnailQueue.Count > 0 && !_thumbnailTimer.Started)
                _thumbnailTimer.Start();
        };
        Closed += (_, _) =>
        {
            _isShown = false;
            _thumbnailTimer.Stop();
            _thumbnailQueue.Clear();
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
            layout.AddRow(new Label { Text = options.Prompt });
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

    private void QueueThumbnail(BlockRow row)
    {
        if (row.ThumbnailLoaded || row.ThumbnailQueued)
            return;

        row.ThumbnailQueued = true;
        _thumbnailQueue.Enqueue(row);
        if (_isShown && !_thumbnailTimer.Started)
            _thumbnailTimer.Start();
    }

    private void ProcessNextThumbnail(object? sender, EventArgs e)
    {
        if (IsDisposed)
        {
            _thumbnailTimer.Stop();
            _thumbnailQueue.Clear();
            return;
        }

        while (_thumbnailQueue.Count > 0)
        {
            BlockRow row = _thumbnailQueue.Dequeue();
            row.ThumbnailQueued = false;

            int visibleIndex = IndexOfVisibleRow(row);
            if (visibleIndex < 0)
                continue;

            row.Thumbnail = BlockThumbnailRenderer.Get(_doc, row.Definition, ThumbnailSize);
            row.ThumbnailLoaded = true;
            _grid.ReloadData(visibleIndex);
            break;
        }

        if (_thumbnailQueue.Count == 0)
            _thumbnailTimer.Stop();
    }

    private int IndexOfVisibleRow(BlockRow row)
    {
        for (int index = 0; index < _visibleRows.Count; index++)
        {
            if (ReferenceEquals(_visibleRows[index], row))
                return index;
        }

        return -1;
    }

    public static List<string>? Show(RhinoDoc doc, IReadOnlyList<InstanceDefinition> blocks, Options? options = null)
    {
        var dialog = new BlockSelectorDialog(doc, blocks, options ?? Options.Scatter);
        return dialog.ShowModal(RhinoEtoApp.MainWindowForDocument(doc));
    }
}
