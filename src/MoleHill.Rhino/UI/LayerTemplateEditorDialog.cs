using System.Diagnostics;
using System.Text.Json;
using Eto.Drawing;
using Eto.Forms;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.UI;

namespace MoleHill.Rhino.UI;

/// <summary>
/// Graphical editor for MoleHill layer templates. Presents each template's layer hierarchy as a
/// <see cref="TreeGridView"/> (with display/print color swatches and plot-weight columns) plus a
/// properties panel for editing the selected layer. Supports multiple named templates and JSON
/// import/export. Persists via <see cref="LayerTemplateStore"/> (the existing
/// <c>%APPDATA%\MoleHill\layer-templates.json</c> schema is unchanged).
/// </summary>
internal sealed class LayerTemplateEditorDialog : Dialog<bool>
{
    /// <summary>A single layer in the tree. Mirrors a <see cref="LayerTemplateEntry"/> but identity is
    /// positional — the full <c>::</c> path is recomputed from ancestors on flatten.</summary>
    private sealed class LayerNode : TreeGridItem
    {
        public string Name { get; set; } = "Layer";
        public int ColorArgb { get; set; } = unchecked((int)0xFF808080);
        public int PrintColorArgb { get; set; } = unchecked((int)0xFF808080);
        public double PlotWeight { get; set; }

        private Bitmap? _swatch;
        private int? _swatchArgb;
        private Bitmap? _printSwatch;
        private int? _printArgb;

        public Image GetSwatch()
        {
            if (_swatch == null || _swatchArgb != ColorArgb)
            {
                _swatch = MakeSwatch(ColorArgb);
                _swatchArgb = ColorArgb;
            }
            return _swatch;
        }

        public Image GetPrintSwatch()
        {
            if (_printSwatch == null || _printArgb != PrintColorArgb)
            {
                _printSwatch = MakeSwatch(PrintColorArgb);
                _printArgb = PrintColorArgb;
            }
            return _printSwatch;
        }

        private static Bitmap MakeSwatch(int argb)
        {
            var fill = ToEto(argb);
            var bmp = new Bitmap(14, 14, PixelFormat.Format32bppRgba);
            using var g = new Graphics(bmp);
            g.Clear(fill);
            g.DrawRectangle(Color.FromArgb(0, 0, 0, 90), 0, 0, 13, 13);
            return bmp;
        }
    }

    private readonly LayerTemplateStore _store;
    private readonly List<LayerTemplateDefinition> _templates;
    private int _activeIndex;
    private bool _suppressTemplateChange;
    private bool _loadingProperties;

    private readonly DropDown _templatePicker = new();
    private readonly TreeGridView _tree = new();
    private TreeGridItemCollection _rootItems = new();

    private readonly Label _pathLabel = new() { TextColor = UiTheme.MutedText };
    private readonly Panel _displaySwatch = new() { Width = 20, Height = 20 };
    private readonly Label _displayHex = new();
    private readonly Panel _printSwatch = new() { Width = 20, Height = 20 };
    private readonly Label _printHex = new();
    private readonly NumericStepper _weightStepper = new() { DecimalPlaces = 2, MinValue = 0.0, Increment = 0.05 };
    private readonly List<Control> _selectionDependent = new();

    private LayerTemplateEditorDialog(LayerTemplateStore store)
    {
        _store = store;
        _templates = store.LoadTemplates().Select(Clone).ToList();
        if (_templates.Count == 0)
            _templates.Add(new LayerTemplateDefinition { Name = "Template", Entries = { new LayerTemplateEntry { Path = "Layer" } } });

        Title = "MoleHill Layer Templates";
        Resizable = true;
        Padding = 12;
        MinimumSize = new Size(620, 600);
        this.UseRhinoStyle();

        Content = BuildLayout();

        RebuildTemplatePicker();
        LoadTemplate(0);
    }

    public static bool ShowDialog(RhinoDoc doc, LayerTemplateStore store)
    {
        var dialog = new LayerTemplateEditorDialog(store);
        return dialog.ShowModal(RhinoEtoApp.MainWindowForDocument(doc));
    }

    // ── Layout ───────────────────────────────────────────────────────────────

    private Control BuildLayout()
    {
        var layout = new DynamicLayout { Spacing = new Size(8, 8) };

        layout.AddRow(new Label
        {
            Text = "Edit layer templates graphically. Each layer's display color, print color and plot " +
                   "weight are applied when you run MoleHillApplyLayerTemplate.",
            Wrap = WrapMode.Word
        });

        layout.AddRow(BuildTemplateRow());
        layout.Add(BuildTreeView(), yscale: true);
        layout.AddRow(BuildNodeActions());
        layout.AddRow(BuildPropertiesPanel());
        layout.AddRow(BuildButtonRow());

        return layout;
    }

    private Control BuildTemplateRow()
    {
        _templatePicker.SelectedIndexChanged += (_, _) =>
        {
            if (_suppressTemplateChange)
                return;
            CommitActiveTree();
            LoadTemplate(_templatePicker.SelectedIndex);
        };

        var newButton = new Button { Text = "New" };
        newButton.Click += (_, _) => OnNewTemplate();
        var renameButton = new Button { Text = "Rename" };
        renameButton.Click += (_, _) => OnRenameTemplate();
        var deleteButton = new Button { Text = "Delete" };
        deleteButton.Click += (_, _) => OnDeleteTemplate();

        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items =
            {
                new Label { Text = "Template:", VerticalAlignment = VerticalAlignment.Center },
                new StackLayoutItem(_templatePicker, expand: true),
                newButton,
                renameButton,
                deleteButton
            }
        };
    }

    private Control BuildTreeView()
    {
        _tree.ShowHeader = true;
        _tree.AllowMultipleSelection = false;
        _tree.Columns.Add(new GridColumn
        {
            HeaderText = "Layer",
            Editable = true,
            Expand = true,
            DataCell = new ImageTextCell
            {
                TextBinding = Binding.Delegate<LayerNode, string>(n => n.Name, (n, v) =>
                {
                    if (!string.IsNullOrWhiteSpace(v))
                        n.Name = v!.Trim();
                }),
                ImageBinding = Binding.Delegate<LayerNode, Image>(n => n.GetSwatch())
            }
        });
        _tree.Columns.Add(new GridColumn
        {
            HeaderText = "Print",
            DataCell = new ImageViewCell { Binding = Binding.Delegate<LayerNode, Image>(n => n.GetPrintSwatch()) }
        });
        _tree.Columns.Add(new GridColumn
        {
            HeaderText = "Weight",
            DataCell = new TextBoxCell { Binding = Binding.Delegate<LayerNode, string>(n => n.PlotWeight.ToString("0.##")) }
        });

        _tree.SelectedItemChanged += (_, _) => RefreshProperties();
        _tree.CellEdited += (_, _) => RefreshProperties();

        return _tree;
    }

    private Control BuildNodeActions()
    {
        var addChild = new Button { Text = "+ Child" };
        addChild.Click += (_, _) => OnAddNode(asChild: true);
        var addSibling = new Button { Text = "+ Sibling" };
        addSibling.Click += (_, _) => OnAddNode(asChild: false);
        var delete = new Button { Text = "Delete Layer" };
        delete.Click += (_, _) => OnDeleteNode();

        _selectionDependent.Add(addSibling);
        _selectionDependent.Add(delete);

        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Items = { addChild, addSibling, delete }
        };
    }

    private Control BuildPropertiesPanel()
    {
        _displaySwatch.MouseDown += (_, _) => PickColor(isPrint: false);
        _printSwatch.MouseDown += (_, _) => PickColor(isPrint: true);

        var pickDisplay = new Button { Text = "Pick" };
        pickDisplay.Click += (_, _) => PickColor(isPrint: false);
        var pickPrint = new Button { Text = "Pick" };
        pickPrint.Click += (_, _) => PickColor(isPrint: true);

        _weightStepper.ValueChanged += (_, _) =>
        {
            if (_loadingProperties)
                return;
            if (SelectedNode is { } node)
            {
                node.PlotWeight = _weightStepper.Value;
                _tree.ReloadData();
            }
        };

        _selectionDependent.Add(_displaySwatch);
        _selectionDependent.Add(pickDisplay);
        _selectionDependent.Add(_printSwatch);
        _selectionDependent.Add(pickPrint);
        _selectionDependent.Add(_weightStepper);

        var grid = new DynamicLayout { Spacing = new Size(8, 6), Padding = new Padding(0, 4) };
        grid.AddRow(new Label { Text = "Selected:", TextColor = UiTheme.MutedText }, _pathLabel, null);
        grid.AddRow(
            new Label { Text = "Color:", VerticalAlignment = VerticalAlignment.Center },
            SwatchRow(_displaySwatch, _displayHex, pickDisplay),
            null);
        grid.AddRow(
            new Label { Text = "Print color:", VerticalAlignment = VerticalAlignment.Center },
            SwatchRow(_printSwatch, _printHex, pickPrint),
            null);
        grid.AddRow(
            new Label { Text = "Plot weight:", VerticalAlignment = VerticalAlignment.Center },
            new StackLayout { Orientation = Orientation.Horizontal, Items = { _weightStepper } },
            null);

        return new Panel
        {
            BackgroundColor = UiTheme.HeaderBackground,
            Padding = new Padding(8),
            Content = grid
        };
    }

    private static Control SwatchRow(Panel swatch, Label hex, Button pick) => new StackLayout
    {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
        VerticalContentAlignment = VerticalAlignment.Center,
        Items = { swatch, hex, pick }
    };

    private Control BuildButtonRow()
    {
        var importButton = new Button { Text = "Import…" };
        importButton.Click += (_, _) => OnImport();
        var exportButton = new Button { Text = "Export…" };
        exportButton.Click += (_, _) => OnExport();

        var saveButton = new Button { Text = "Save" };
        saveButton.Click += (_, _) => OnSave();
        var cancelButton = new Button { Text = "Cancel" };
        cancelButton.Click += (_, _) => { Result = false; Close(); };
        var resetButton = new Button { Text = "Reset To Defaults" };
        resetButton.Click += (_, _) => OnReset();
        var openFolderButton = new Button { Text = "Open Settings Folder" };
        openFolderButton.Click += (_, _) => OnOpenSettingsFolder();

        DefaultButton = saveButton;
        AbortButton = cancelButton;

        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Items =
            {
                importButton,
                exportButton,
                null,
                saveButton,
                cancelButton,
                resetButton,
                openFolderButton
            }
        };
    }

    // ── Template management ────────────────────────────────────────────────────

    private void RebuildTemplatePicker()
    {
        _suppressTemplateChange = true;
        _templatePicker.Items.Clear();
        foreach (var template in _templates)
            _templatePicker.Items.Add(template.Name);
        _templatePicker.SelectedIndex = Math.Clamp(_activeIndex, 0, _templates.Count - 1);
        _suppressTemplateChange = false;
    }

    private void LoadTemplate(int index)
    {
        _activeIndex = Math.Clamp(index, 0, _templates.Count - 1);
        _suppressTemplateChange = true;
        _templatePicker.SelectedIndex = _activeIndex;
        _suppressTemplateChange = false;

        _rootItems = BuildTree(_templates[_activeIndex]);
        _tree.DataStore = _rootItems;
        SelectFirst();
        RefreshProperties();
    }

    private void CommitActiveTree()
    {
        if (_activeIndex < 0 || _activeIndex >= _templates.Count)
            return;
        _templates[_activeIndex].Entries = FlattenRoot();
    }

    private void OnNewTemplate()
    {
        string? name = PromptForName("New template name", "Template");
        if (name == null)
            return;
        name = UniqueTemplateName(name);

        CommitActiveTree();
        _templates.Add(new LayerTemplateDefinition
        {
            Name = name,
            Entries = { new LayerTemplateEntry { Path = "Layer" } }
        });
        _activeIndex = _templates.Count - 1;
        RebuildTemplatePicker();
        LoadTemplate(_activeIndex);
    }

    private void OnRenameTemplate()
    {
        string? name = PromptForName("Rename template", _templates[_activeIndex].Name);
        if (name == null)
            return;
        _templates[_activeIndex].Name = UniqueTemplateName(name, ignoreIndex: _activeIndex);
        RebuildTemplatePicker();
    }

    private void OnDeleteTemplate()
    {
        if (_templates.Count <= 1)
        {
            MessageBox.Show(this, "At least one template is required.", "Cannot Delete",
                MessageBoxButtons.OK, MessageBoxType.Warning);
            return;
        }

        var result = MessageBox.Show(this, $"Delete template '{_templates[_activeIndex].Name}'?",
            "Delete Template", MessageBoxButtons.YesNo, MessageBoxType.Question);
        if (result != DialogResult.Yes)
            return;

        _templates.RemoveAt(_activeIndex);
        _activeIndex = Math.Clamp(_activeIndex, 0, _templates.Count - 1);
        RebuildTemplatePicker();
        LoadTemplate(_activeIndex);
    }

    private string UniqueTemplateName(string name, int ignoreIndex = -1)
    {
        bool Exists(string candidate) => _templates
            .Where((_, i) => i != ignoreIndex)
            .Any(t => string.Equals(t.Name, candidate, StringComparison.OrdinalIgnoreCase));

        if (!Exists(name))
            return name;

        for (int i = 2; ; i++)
        {
            string candidate = $"{name} ({i})";
            if (!Exists(candidate))
                return candidate;
        }
    }

    // ── Tree <-> model ──────────────────────────────────────────────────────────

    private static TreeGridItemCollection BuildTree(LayerTemplateDefinition template)
    {
        var root = new TreeGridItemCollection();
        var byPath = new Dictionary<string, LayerNode>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in template.Entries)
        {
            string[] segments = entry.Path.Split(new[] { "::" },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0)
                continue;

            LayerNode? parent = null;
            string currentPath = string.Empty;
            for (int i = 0; i < segments.Length; i++)
            {
                currentPath = currentPath.Length == 0 ? segments[i] : $"{currentPath}::{segments[i]}";
                if (!byPath.TryGetValue(currentPath, out var node))
                {
                    node = new LayerNode { Name = segments[i], Expanded = true };
                    byPath[currentPath] = node;
                    if (parent == null)
                    {
                        root.Add(node);
                    }
                    else
                    {
                        node.Parent = parent;
                        parent.Children.Add(node);
                    }
                }

                if (i == segments.Length - 1)
                {
                    node.ColorArgb = entry.ColorArgb;
                    node.PrintColorArgb = entry.PrintColorArgb;
                    node.PlotWeight = entry.PlotWeight;
                }

                parent = node;
            }
        }

        return root;
    }

    private List<LayerTemplateEntry> FlattenRoot()
    {
        var entries = new List<LayerTemplateEntry>();
        foreach (var item in _rootItems)
            Visit((LayerNode)item, string.Empty);
        return entries;

        void Visit(LayerNode node, string parentPath)
        {
            string path = parentPath.Length == 0 ? node.Name : $"{parentPath}::{node.Name}";
            entries.Add(new LayerTemplateEntry
            {
                Path = path,
                ColorArgb = node.ColorArgb,
                PrintColorArgb = node.PrintColorArgb,
                PlotWeight = node.PlotWeight
            });
            foreach (var child in node.Children)
                Visit((LayerNode)child, path);
        }
    }

    // ── Node actions ────────────────────────────────────────────────────────────

    private LayerNode? SelectedNode => _tree.SelectedItem as LayerNode;

    private void OnAddNode(bool asChild)
    {
        var node = new LayerNode { Name = "Layer", Expanded = true };
        var selected = SelectedNode;

        if (asChild && selected != null)
        {
            node.Parent = selected;
            selected.Children.Add(node);
            selected.Expanded = true;
        }
        else if (selected?.Parent is LayerNode parent)
        {
            node.Parent = parent;
            parent.Children.Add(node);
        }
        else
        {
            _rootItems.Add(node);
        }

        _tree.DataStore = _rootItems;
        _tree.SelectedItem = node;
        RefreshProperties();
    }

    private void OnDeleteNode()
    {
        if (SelectedNode is not { } node)
            return;

        if (node.Parent is LayerNode parent)
            parent.Children.Remove(node);
        else
            _rootItems.Remove(node);

        _tree.DataStore = _rootItems;
        SelectFirst();
        RefreshProperties();
    }

    private void SelectFirst()
    {
        _tree.SelectedItem = _rootItems.Count > 0 ? (ITreeGridItem)_rootItems[0] : null;
    }

    // ── Properties panel ────────────────────────────────────────────────────────

    private void RefreshProperties()
    {
        var node = SelectedNode;
        bool has = node != null;
        foreach (var control in _selectionDependent)
            control.Enabled = has;

        _loadingProperties = true;
        if (node == null)
        {
            _pathLabel.Text = "(no layer selected)";
            _displaySwatch.BackgroundColor = UiTheme.PillBackground;
            _printSwatch.BackgroundColor = UiTheme.PillBackground;
            _displayHex.Text = string.Empty;
            _printHex.Text = string.Empty;
            _weightStepper.Value = 0;
        }
        else
        {
            _pathLabel.Text = NodePath(node);
            _displaySwatch.BackgroundColor = ToEto(node.ColorArgb);
            _displayHex.Text = Hex(node.ColorArgb);
            _printSwatch.BackgroundColor = ToEto(node.PrintColorArgb);
            _printHex.Text = Hex(node.PrintColorArgb);
            _weightStepper.Value = node.PlotWeight;
        }
        _loadingProperties = false;
    }

    private void PickColor(bool isPrint)
    {
        if (SelectedNode is not { } node)
            return;

        int current = isPrint ? node.PrintColorArgb : node.ColorArgb;
        var dialog = new ColorDialog { Color = ToEto(current), AllowAlpha = false };
        if (dialog.ShowDialog(this) != DialogResult.Ok)
            return;

        int argb = ToArgb(dialog.Color);
        if (isPrint)
            node.PrintColorArgb = argb;
        else
            node.ColorArgb = argb;

        _tree.ReloadData();
        RefreshProperties();
    }

    private static string NodePath(LayerNode node)
    {
        var parts = new List<string>();
        TreeGridItem? cursor = node;
        while (cursor is LayerNode layer)
        {
            parts.Insert(0, layer.Name);
            cursor = layer.Parent as TreeGridItem;
        }
        return string.Join("::", parts);
    }

    // ── Import / Export / Save ──────────────────────────────────────────────────

    private void OnExport()
    {
        CommitActiveTree();
        var dialog = new Eto.Forms.SaveFileDialog
        {
            Title = "Export Layer Templates",
            FileName = "layer-templates.json",
            Filters = { new FileFilter("JSON", ".json") }
        };
        if (dialog.ShowDialog(this) != DialogResult.Ok)
            return;

        try
        {
            string json = JsonSerializer.Serialize(_templates, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(dialog.FileName, json);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Export Failed", MessageBoxButtons.OK, MessageBoxType.Error);
        }
    }

    private void OnImport()
    {
        var dialog = new Eto.Forms.OpenFileDialog
        {
            Title = "Import Layer Templates",
            MultiSelect = false,
            Filters = { new FileFilter("JSON", ".json") }
        };
        if (dialog.ShowDialog(this) != DialogResult.Ok)
            return;

        try
        {
            string json = File.ReadAllText(dialog.FileName);
            var imported = JsonSerializer.Deserialize<List<LayerTemplateDefinition>>(json);
            if (imported == null || imported.Count == 0)
                throw new InvalidOperationException("The file does not contain any templates.");

            CommitActiveTree();
            int firstNewIndex = _templates.Count;
            foreach (var template in imported)
            {
                if (template.Entries == null || template.Entries.Count == 0)
                    continue;
                template.Name = UniqueTemplateName(string.IsNullOrWhiteSpace(template.Name) ? "Imported" : template.Name);
                _templates.Add(template);
            }

            if (_templates.Count == firstNewIndex)
                throw new InvalidOperationException("The file does not contain any usable templates.");

            _activeIndex = firstNewIndex;
            RebuildTemplatePicker();
            LoadTemplate(_activeIndex);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Import Failed", MessageBoxButtons.OK, MessageBoxType.Error);
        }
    }

    private void OnSave()
    {
        try
        {
            CommitActiveTree();
            if (_templates.Count == 0 || _templates.All(t => t.Entries.Count == 0))
                throw new InvalidOperationException("At least one template with one layer is required.");

            _store.SaveTemplates(_templates);
            Result = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Save Failed", MessageBoxButtons.OK, MessageBoxType.Error);
        }
    }

    private void OnReset()
    {
        var result = MessageBox.Show(this,
            "Discard changes and restore the built-in MoleHill default templates?",
            "Reset To Defaults", MessageBoxButtons.YesNo, MessageBoxType.Question);
        if (result != DialogResult.Yes)
            return;

        _templates.Clear();
        _templates.AddRange(_store.GetDefaultTemplates().Select(Clone));
        _activeIndex = 0;
        RebuildTemplatePicker();
        LoadTemplate(0);
    }

    private void OnOpenSettingsFolder()
    {
        string? folder = Path.GetDirectoryName(_store.GetStorePath());
        if (string.IsNullOrWhiteSpace(folder))
            return;

        Directory.CreateDirectory(folder);
        try
        {
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true, Verb = "open" });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Open Settings Folder Failed", MessageBoxButtons.OK, MessageBoxType.Error);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private string? PromptForName(string title, string initial)
    {
        var box = new TextBox { Text = initial, Width = 260 };
        var ok = new Button { Text = "OK" };
        var cancel = new Button { Text = "Cancel" };
        var dialog = new Dialog<string?> { Title = title, Padding = 12, Resizable = false };
        dialog.DefaultButton = ok;
        dialog.AbortButton = cancel;
        ok.Click += (_, _) => { dialog.Result = box.Text?.Trim(); dialog.Close(); };
        cancel.Click += (_, _) => { dialog.Result = null; dialog.Close(); };
        dialog.Content = new StackLayout
        {
            Spacing = 8,
            Items =
            {
                new Label { Text = title },
                new StackLayoutItem(box, HorizontalAlignment.Stretch),
                new StackLayout
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Items = { null, ok, cancel }
                }
            }
        };

        string? result = dialog.ShowModal(this);
        return string.IsNullOrWhiteSpace(result) ? null : result.Trim();
    }

    private static LayerTemplateDefinition Clone(LayerTemplateDefinition source) => new()
    {
        Name = source.Name,
        Entries = source.Entries.Select(e => new LayerTemplateEntry
        {
            Path = e.Path,
            ColorArgb = e.ColorArgb,
            PrintColorArgb = e.PrintColorArgb,
            PlotWeight = e.PlotWeight
        }).ToList()
    };

    private static Color ToEto(int argb)
    {
        var c = System.Drawing.Color.FromArgb(argb);
        return Color.FromArgb(c.R, c.G, c.B, 255);
    }

    private static int ToArgb(Color color)
    {
        int r = (int)Math.Round(Math.Clamp(color.R, 0f, 1f) * 255.0);
        int g = (int)Math.Round(Math.Clamp(color.G, 0f, 1f) * 255.0);
        int b = (int)Math.Round(Math.Clamp(color.B, 0f, 1f) * 255.0);
        return System.Drawing.Color.FromArgb(255, r, g, b).ToArgb();
    }

    private static string Hex(int argb)
    {
        var c = System.Drawing.Color.FromArgb(argb);
        return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }
}
