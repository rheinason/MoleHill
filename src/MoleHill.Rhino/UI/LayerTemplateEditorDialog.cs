using System.Diagnostics;
using System.Text.Json;
using Eto.Drawing;
using Eto.Forms;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
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

        /// <summary>
        /// The output this layer receives, as <c>LayerRoleRegistry</c> ids. Empty for a plain layer
        /// the template creates but nothing routes to.
        ///
        /// Carried on the node rather than recomputed from the path so a binding survives renaming
        /// or re-parenting the layer — which is the whole point of binding by role instead of by
        /// path in the first place.
        /// </summary>
        public List<string> Roles { get; set; } = new();

        public string? LinetypeName { get; set; }
        public string? AnnotationStyleName { get; set; }
        public string? HatchPatternName { get; set; }
        public double? HatchScale { get; set; }
        public double? HatchRotationDegrees { get; set; }
        public int? PreviewWidthPx { get; set; }

        /// <summary>What the Role column shows: the role's display name, or nothing.</summary>
        /// <summary>Set from the resolved role table on every refresh — see RefreshRoleMap.</summary>
        public string RoleText { get; set; } = string.Empty;

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
    private readonly RhinoDoc _doc;
    private readonly List<LayerTemplateDefinition> _templates;
    private int _activeIndex;
    private bool _suppressTemplateChange;
    private bool _loadingProperties;

    private readonly DropDown _templatePicker = new();
    private readonly TreeGridView _tree = new();
    private TreeGridItemCollection _rootItems = new();

    private readonly Label _pathLabel = new() { TextColor = UiTheme.MutedText };
    private readonly Button _rolePicker = new() { Text = "Nothing", Width = UiMetrics.Chs(40) };
    private readonly Button _addMissingButton = new() { Text = "All roles listed" };
    private List<string> _missingRoleLayers = new();
    private readonly Label _roleHint = new() { TextColor = UiTheme.MutedText, Wrap = WrapMode.Word };

    private readonly Panel _displaySwatch = new() { Width = 20, Height = 20 };
    private readonly Label _displayHex = new();
    private readonly Panel _printSwatch = new() { Width = 20, Height = 20 };
    private readonly Label _printHex = new();
    private readonly NumericStepper _weightStepper = new() { DecimalPlaces = 2, MinValue = 0.0, Increment = 0.05 };
    private readonly DropDown _annotationStylePicker = new() { Width = UiMetrics.Chs(28) };
    private readonly List<Control> _selectionDependent = new();

    private LayerTemplateEditorDialog(RhinoDoc doc, LayerTemplateStore store)
    {
        _doc = doc;
        _store = store;
        _templates = store.LoadTemplates().Select(template => template.Copy()).ToList();
        if (_templates.Count == 0)
            _templates.Add(new LayerTemplateDefinition { Name = "Template", Entries = { new LayerTemplateEntry { Path = "Layer" } } });

        Title = "MoleHill Layer Templates";
        Resizable = true;
        Padding = 12;
        MinimumSize = new Size(620, 600);
        this.UseRhinoStyle();

        Content = BuildLayout();

        RebuildTemplatePicker();
        LoadAnnotationStyles();
        LoadTemplate(0);
    }

    public static bool ShowDialog(RhinoDoc doc, LayerTemplateStore store)
    {
        var dialog = new LayerTemplateEditorDialog(doc, store);
        return dialog.ShowModal(RhinoEtoApp.MainWindowForDocument(doc));
    }

    // ── Layout ───────────────────────────────────────────────────────────────

    private Control BuildLayout()
    {
        var layout = new DynamicLayout { Spacing = new Size(8, 8) };

        layout.AddRow(new Label
        {
            Text = "The layers MoleHill creates, how they look, and which output lands on each. " +
                   "Appearance is applied when a layer is first created; after that the layer is " +
                   "yours and your edits in Rhino's Layers panel stick. Use mhResetLayerStyles to " +
                   "put a drifted document back.",
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
        // Read-only: the binding is chosen in the properties panel, where the list can be grouped
        // and show which roles are already taken. This column is so it is visible at a glance which
        // layers actually receive output and which are just here for the user's own geometry.
        _tree.Columns.Add(new GridColumn
        {
            HeaderText = "Receives",
            DataCell = new TextBoxCell { Binding = Binding.Delegate<LayerNode, string>(n => n.RoleText) }
        });

        _tree.SelectedItemChanged += (_, _) => RefreshProperties();
        _tree.CellEdited += (_, _) =>
        {
            // Renaming a layer re-paths its whole subtree, so what lands where can change.
            RefreshRoleMap();
            RefreshProperties();
        };

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

        _addMissingButton.Click += (_, _) => OnAddMissingRoleLayers();

        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Items = { addChild, addSibling, delete, new Panel { Width = 16 }, _addMissingButton }
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

        _rolePicker.Click += (_, _) => ShowRolePicker();
        _selectionDependent.Add(_rolePicker);

        _annotationStylePicker.SelectedIndexChanged += (_, _) =>
        {
            if (_loadingProperties || SelectedNode is not { } node || !IsAnnotationNode(node))
                return;

            string? selected = _annotationStylePicker.SelectedIndex >= 0
                ? _annotationStylePicker.Items[_annotationStylePicker.SelectedIndex].Text
                : null;
            node.AnnotationStyleName = string.IsNullOrWhiteSpace(selected) ? null : selected;
        };
        _selectionDependent.Add(_annotationStylePicker);

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
        grid.AddRow(
            new Label { Text = "Receives:", VerticalAlignment = VerticalAlignment.Center },
            new StackLayout { Orientation = Orientation.Horizontal, Items = { _rolePicker } },
            null);
        grid.AddRow(
            new Label { Text = "Annotation style:", VerticalAlignment = VerticalAlignment.Center },
            new StackLayout { Orientation = Orientation.Horizontal, Items = { _annotationStylePicker } },
            null);
        grid.AddRow(new Panel(), _roleHint, null);

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

    /// <summary>
    /// Fills the Receives column with every role that actually lands on each layer, which is not the
    /// same as the role bound to it: a role with no layer of its own resolves into an ancestor's, so
    /// before this the layer receiving retaining walls showed nothing at all.
    ///
    /// Roles whose layer is not in the template at all are reported separately, since there is no row
    /// to put them on.
    /// </summary>
    private void RefreshRoleMap()
    {
        var byPath = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var table = LayerRoleTable.Build(new LayerTemplateDefinition { Version = 1, Entries = FlattenRoot() });

        foreach (var descriptor in LayerRoleRegistry.All)
        {
            string path = table.Path(descriptor.Role);
            if (!byPath.TryGetValue(path, out var names))
                byPath[path] = names = new List<string>();
            names.Add(descriptor.DisplayName);
        }

        var missing = new List<string>(byPath.Keys);
        foreach (LayerNode node in AllNodes())
        {
            string path = NodePath(node);
            node.RoleText = byPath.TryGetValue(path, out var names) ? string.Join(", ", names) : string.Empty;
            missing.Remove(path);
        }

        _missingRoleLayers = missing;
        _addMissingButton.Enabled = missing.Count > 0;
        _addMissingButton.ToolTip = missing.Count == 0
            ? "Every role has a layer in this template."
            : "These roles land on layers this template does not list, so MoleHill creates them on "
                + "demand and you cannot style them here:" + Environment.NewLine + "  "
                + string.Join(Environment.NewLine + "  ", missing);
        _addMissingButton.Text = missing.Count == 0
            ? "All roles listed"
            : $"Add {missing.Count} missing role layer(s)";

        _tree.ReloadData();
    }

    /// <summary>
    /// Adds a row for each role whose layer the template does not list, at the path it already
    /// resolves to — so nothing moves, it just becomes visible and stylable.
    /// </summary>
    private void OnAddMissingRoleLayers()
    {
        if (_missingRoleLayers.Count == 0)
            return;

        var entries = FlattenRoot();
        foreach (string path in _missingRoleLayers)
            entries.Add(new LayerTemplateEntry { Path = path });

        _templates[_activeIndex].Entries = entries;
        LoadTemplate(_activeIndex);
    }

    private void LoadTemplate(int index)
    {
        _activeIndex = Math.Clamp(index, 0, _templates.Count - 1);
        _suppressTemplateChange = true;
        _templatePicker.SelectedIndex = _activeIndex;
        _suppressTemplateChange = false;

        _rootItems = BuildTree(_templates[_activeIndex]);
        _tree.DataStore = _rootItems;
        RefreshRoleMap();
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
                    node.ColorArgb = entry.ColorArgb ?? node.ColorArgb;
                    node.PrintColorArgb = entry.PrintColorArgb ?? node.PrintColorArgb;
                    node.PlotWeight = entry.PlotWeight ?? node.PlotWeight;
                    node.Roles = new List<string>(entry.Roles);
                    node.LinetypeName = entry.LinetypeName;
                    node.AnnotationStyleName = entry.AnnotationStyleName;
                    node.HatchPatternName = entry.HatchPatternName;
                    node.HatchScale = entry.HatchScale;
                    node.HatchRotationDegrees = entry.HatchRotationDegrees;
                    node.PreviewWidthPx = entry.PreviewWidthPx;
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
                Roles = new List<string>(node.Roles),
                ColorArgb = node.ColorArgb,
                PrintColorArgb = node.PrintColorArgb,
                PlotWeight = node.PlotWeight,
                LinetypeName = node.LinetypeName,
                AnnotationStyleName = node.AnnotationStyleName,
                HatchPatternName = node.HatchPatternName,
                HatchScale = node.HatchScale,
                HatchRotationDegrees = node.HatchRotationDegrees,
                PreviewWidthPx = node.PreviewWidthPx
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

    /// <summary>
    /// Which kinds of output this layer receives, chosen from a checklist rather than a dropdown so
    /// several can share one layer — an office that wants all the section furniture on a single
    /// layer rather than the sublayer-per-kind the defaults ship with.
    ///
    /// A role already on another layer is shown with where it currently is, and checking it here
    /// moves it: one role landing on two layers would duplicate its output.
    /// </summary>
    private void ShowRolePicker()
    {
        if (SelectedNode is not { } node)
            return;

        var owners = new Dictionary<string, LayerNode>(StringComparer.OrdinalIgnoreCase);
        foreach (LayerNode other in AllNodes())
        {
            foreach (string roleId in other.Roles)
                owners[roleId] = other;
        }

        var rows = LayerRoleRegistry.All
            .Select(descriptor => new RoleRow
            {
                Id = descriptor.Id,
                Checked = node.Roles.Contains(descriptor.Id, StringComparer.OrdinalIgnoreCase),
                Name = descriptor.DisplayName,
                Elsewhere = owners.TryGetValue(descriptor.Id, out LayerNode? owner) && !ReferenceEquals(owner, node)
                    ? NodePath(owner)
                    : string.Empty
            })
            .ToList();

        var grid = new GridView { DataStore = rows, ShowHeader = true, Height = 320 };
        grid.Columns.Add(new GridColumn
        {
            HeaderText = string.Empty,
            Editable = true,
            DataCell = new CheckBoxCell { Binding = Binding.Delegate<RoleRow, bool?>(r => r.Checked, (r, v) => r.Checked = v ?? false) }
        });
        grid.Columns.Add(new GridColumn
        {
            HeaderText = "Output",
            Expand = true,
            DataCell = new TextBoxCell { Binding = Binding.Delegate<RoleRow, string>(r => r.Name) }
        });
        grid.Columns.Add(new GridColumn
        {
            HeaderText = "Currently on",
            DataCell = new TextBoxCell { Binding = Binding.Delegate<RoleRow, string>(r => r.Elsewhere) }
        });

        var dialog = new Dialog<bool>
        {
            Title = "Output for " + NodePath(node),
            Padding = 12,
            Resizable = true,
            MinimumSize = new Size(520, 420)
        };
        dialog.UseRhinoStyle();

        var ok = new Button { Text = "OK" };
        ok.Click += (_, _) => dialog.Close(true);
        var cancel = new Button { Text = "Cancel" };
        cancel.Click += (_, _) => dialog.Close(false);
        dialog.DefaultButton = ok;
        dialog.AbortButton = cancel;

        dialog.Content = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 8,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new Label
                {
                    Text = "Tick everything that should be drawn on this layer. Anything left unticked "
                        + "goes to its own layer, or to the nearest parent layer that receives it.",
                    Wrap = WrapMode.Word
                },
                new StackLayoutItem(grid, expand: true),
                new StackLayout
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    HorizontalContentAlignment = HorizontalAlignment.Right,
                    Items = { null, ok, cancel }
                }
            }
        };

        if (!dialog.ShowModal(this))
            return;

        node.Roles = rows.Where(r => r.Checked).Select(r => r.Id).ToList();

        // Checking a role here takes it off whichever layer had it.
        foreach (RoleRow row in rows.Where(r => r.Checked && r.Elsewhere.Length > 0))
        {
            if (owners.TryGetValue(row.Id, out LayerNode? owner) && !ReferenceEquals(owner, node))
                owner.Roles.RemoveAll(id => string.Equals(id, row.Id, StringComparison.OrdinalIgnoreCase));
        }

        RefreshRoleMap();
        RefreshProperties();
    }

    private sealed class RoleRow
    {
        public string Id { get; set; } = string.Empty;
        public bool Checked { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Elsewhere { get; set; } = string.Empty;
    }

    /// <summary>
    /// What the layer receives, and for an inherited role where it would otherwise have gone — so
    /// leaving a layer unticked reads as a normal choice rather than an omission.
    /// </summary>
    private static string DescribeRoles(IReadOnlyList<string> roleIds)
    {
        if (roleIds.Count == 0)
            return "Nothing is routed here. MoleHill will create the layer and leave it to you.";

        var named = roleIds
            .Select(LayerRoleRegistry.ForId)
            .Where(descriptor => descriptor != null)
            .ToList();

        if (named.Count == 0)
            return "This layer is bound to output from a newer version of MoleHill.";

        if (named.Count == 1 && named[0]!.Parent != null)
        {
            return $"{named[0]!.DisplayName} lands here. Unticked, it would go to "
                + $"{LayerRoleRegistry.DefaultPath(named[0]!.Role)}.";
        }

        return string.Join(", ", named.Select(d => d!.DisplayName)) + " land on this layer.";
    }

    private IEnumerable<LayerNode> AllNodes()
    {
        var stack = new Stack<LayerNode>(_rootItems.Cast<LayerNode>());
        while (stack.Count > 0)
        {
            LayerNode node = stack.Pop();
            yield return node;
            foreach (var child in node.Children)
                stack.Push((LayerNode)child);
        }
    }

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
            _rolePicker.Text = "Nothing";
            _annotationStylePicker.Visible = false;
            _roleHint.Text = string.Empty;
        }
        else
        {
            _pathLabel.Text = NodePath(node);
            _displaySwatch.BackgroundColor = ToEto(node.ColorArgb);
            _displayHex.Text = Hex(node.ColorArgb);
            _printSwatch.BackgroundColor = ToEto(node.PrintColorArgb);
            _printHex.Text = Hex(node.PrintColorArgb);
            _weightStepper.Value = node.PlotWeight;

            _rolePicker.Text = node.Roles.Count == 0
                ? "Nothing…"
                : string.Join(", ", node.Roles.Select(id => LayerRoleRegistry.ForId(id)?.DisplayName ?? id)) + "…";
            _roleHint.Text = DescribeRoles(node.Roles);
            _annotationStylePicker.Visible = IsAnnotationNode(node);
            SelectAnnotationStyle(node.AnnotationStyleName);
        }
        _loadingProperties = false;
    }

    private void SelectAnnotationStyle(string? styleName)
    {
        string desired = AnnotationStyleService.ResolveStyleName(styleName);
        int index = Enumerable.Range(0, _annotationStylePicker.Items.Count)
            .FirstOrDefault(i => string.Equals(
                _annotationStylePicker.Items[i].Text,
                desired,
                StringComparison.OrdinalIgnoreCase), -1);
        _annotationStylePicker.SelectedIndex = index >= 0 ? index : 0;
    }

    private void LoadAnnotationStyles()
    {
        _annotationStylePicker.Items.Clear();
        foreach (string name in Enumerable.Range(0, _doc.DimStyles.Count)
                     .Select(index => _doc.DimStyles[index].Name)
                     .Where(name => !string.IsNullOrWhiteSpace(name))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            _annotationStylePicker.Items.Add(name);
        }

        if (_annotationStylePicker.Items.Count == 0)
            _annotationStylePicker.Items.Add(AnnotationStyleService.DefaultStyleName);
    }

    private static bool IsAnnotationNode(LayerNode node) =>
        node.Roles.Contains("annotation", StringComparer.OrdinalIgnoreCase);

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

            if (!ConfirmSharedRoots())
                return;

            _store.SaveTemplates(_templates);

            // The document's embedded copy is what it renders by, so the edit has to reach it too or
            // the saved change never shows in the drawing it was made from.
            LayerRoleService.PullEditedFromLocal(_doc, _store.LoadTemplates());
            Result = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Save Failed", MessageBoxButtons.OK, MessageBoxType.Error);
        }
    }

    /// <summary>
    /// A template with no <see cref="TerrainLayerNaming.Token"/> in any path gives every terrain the
    /// same layers. That is legitimate for one terrain and the cause of fighting over layers for
    /// several, so when the document has more than one it is worth a confirmation.
    /// </summary>
    private bool ConfirmSharedRoots()
    {
        if (TerrainController.Instance.GetTerrains(_doc).Count < 2)
            return true;

        var shared = _templates
            .Where(template => !template.Entries.Any(entry => TerrainLayerNaming.ContainsToken(entry.Path)))
            .Select(template => template.Name)
            .ToList();
        if (shared.Count == 0)
            return true;

        var result = MessageBox.Show(this,
            $"{string.Join(", ", shared)} has no {TerrainLayerNaming.Token} in any layer path, so every terrain that " +
            "uses it shares one set of layers. Put " + TerrainLayerNaming.Token + " in the root " +
            $"(for example \"{TerrainLayerNaming.DefaultRoot}\") to give each terrain its own.\n\nSave anyway?",
            "Terrains Share Layers", MessageBoxButtons.YesNo, MessageBoxType.Warning);
        return result == DialogResult.Yes;
    }

    private void OnReset()
    {
        var result = MessageBox.Show(this,
            "Discard changes and restore the built-in MoleHill default templates?",
            "Reset To Defaults", MessageBoxButtons.YesNo, MessageBoxType.Question);
        if (result != DialogResult.Yes)
            return;

        _templates.Clear();
        _templates.AddRange(_store.GetDefaultTemplates().Select(template => template.Copy()));
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
