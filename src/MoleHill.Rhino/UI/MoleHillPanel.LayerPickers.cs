using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.UI;

namespace MoleHill.Rhino.UI;

// Layer source/picker popovers and the floating layer-picker popup form.
public sealed partial class MoleHillPanel
{
    private void ShowLayerSourcePopover(
        Button anchor,
        IReadOnlyList<string> currentLayerPaths,
        Action<string> onAddLayer,
        Action<string> onRemoveLayer)
    {
        ShowLayerPickerPopover(
            anchor,
            currentLayerPaths,
            onAddLayer,
            onRemoveLayer,
            onClear: null,
            preferredSize: new Size(300, 320),
            clearToolTip: string.Empty);
    }

    private void ShowLayerPickerPopover(
        Button anchor,
        IReadOnlyList<string> currentLayerPaths,
        Action<string> onCommitLayer,
        Action<string>? onRemoveLayer,
        Action? onClear,
        Size preferredSize,
        string clearToolTip)
    {
        var doc = RhinoDoc.ActiveDoc;
        if (doc == null)
            return;

        LayerPickerMode mode = onRemoveLayer == null ? LayerPickerMode.SingleSelect : LayerPickerMode.MultiSelect;
        var popup = CreateLayerPickerPopup(doc);
        var searchBox = new TextBox { PlaceholderText = "Find a layer..." };
        StyleTextBox(searchBox);

        var selectedSet = new HashSet<string>(currentLayerPaths, StringComparer.OrdinalIgnoreCase);
        var availableEntries = doc.Layers
            .Where(layer => !layer.IsDeleted && !selectedSet.Contains(layer.FullPath))
            .Select(layer => new LayerPickerEntry(layer.FullPath, layer.FullPath, ToEtoColor(layer.Color)))
            .ToList();

        var selectedStack = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 2,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        foreach (var path in currentLayerPaths)
        {
            var capturedPath = path;
            var rhinoLayer = doc.Layers.FirstOrDefault(layer => !layer.IsDeleted &&
                string.Equals(layer.FullPath, path, StringComparison.OrdinalIgnoreCase));
            var removeButton = MakeInlineButton("Remove", (_, _) =>
            {
                onRemoveLayer?.Invoke(capturedPath);
                popup.Close();
            }, "Remove this watched layer.");
            selectedStack.Items.Add(new StackLayoutItem(
                CreateLayerPickerRow(
                    GetLeafLayerName(path),
                    rhinoLayer != null ? ToEtoColor(rhinoLayer.Color) : Color.FromArgb(80, 80, 80),
                    highlighted: false,
                    trailingControl: removeButton),
                HorizontalAlignment.Stretch));
        }

        var availableStack = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 2,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        var availableScroll = new Scrollable
        {
            Content = availableStack,
            Border = BorderType.None,
            ExpandContentWidth = true,
            ExpandContentHeight = false
        };
        UiControls.DisableHorizontalScrolling(availableScroll);

        List<LayerPickerEntry> visibleEntries = new();
        int highlightedIndex = -1;

        void EnsureHighlightedRowVisible()
        {
            if (highlightedIndex < 0)
                return;

            int targetY = Math.Max(0, highlightedIndex * LayerPickerRowHeight - LayerPickerRowHeight);
            availableScroll.ScrollPosition = new Point(0, targetY);
        }

        void CommitLayer(string path)
        {
            onCommitLayer(path);
            popup.Close();
        }

        void RebuildAvailableRows()
        {
            string query = searchBox.Text ?? string.Empty;
            visibleEntries = availableEntries
                .Where(entry => string.IsNullOrWhiteSpace(query) || entry.Path.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (visibleEntries.Count == 0)
            {
                highlightedIndex = -1;
            }
            else if (highlightedIndex < 0)
            {
                highlightedIndex = 0;
            }
            else if (highlightedIndex >= visibleEntries.Count)
            {
                highlightedIndex = visibleEntries.Count - 1;
            }

            availableStack.Items.Clear();
            if (visibleEntries.Count == 0)
            {
                availableStack.Items.Add(new StackLayoutItem(new Panel
                {
                    Padding = new Padding(8, 6),
                    Content = new Label
                    {
                        Text = "No matching layers.",
                        TextColor = UiTheme.MutedText
                    }
                }, HorizontalAlignment.Stretch));
                return;
            }

            for (int index = 0; index < visibleEntries.Count; index++)
            {
                int capturedIndex = index;
                LayerPickerEntry capturedEntry = visibleEntries[index];
                var row = CreateLayerPickerRow(
                    capturedEntry.DisplayText,
                    capturedEntry.DotColor,
                    highlighted: capturedIndex == highlightedIndex);
                row.MouseDown += (_, e) =>
                {
                    if (e.Buttons != MouseButtons.Primary)
                        return;

                    highlightedIndex = capturedIndex;
                    CommitLayer(capturedEntry.Path);
                    e.Handled = true;
                };
                availableStack.Items.Add(new StackLayoutItem(row, HorizontalAlignment.Stretch));
            }
        }

        void MoveHighlight(int delta)
        {
            if (visibleEntries.Count == 0)
                return;

            if (highlightedIndex < 0)
                highlightedIndex = delta >= 0 ? 0 : visibleEntries.Count - 1;
            else
                highlightedIndex = Math.Clamp(highlightedIndex + delta, 0, visibleEntries.Count - 1);

            RebuildAvailableRows();
            EnsureHighlightedRowVisible();
        }

        searchBox.TextChanged += (_, _) =>
        {
            highlightedIndex = 0;
            RebuildAvailableRows();
        };
        searchBox.KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Keys.Escape:
                    popup.Close();
                    e.Handled = true;
                    break;
                case Keys.Down:
                    MoveHighlight(1);
                    e.Handled = true;
                    break;
                case Keys.Up:
                    MoveHighlight(-1);
                    e.Handled = true;
                    break;
                case Keys.Enter:
                    if (highlightedIndex >= 0 && highlightedIndex < visibleEntries.Count)
                    {
                        CommitLayer(visibleEntries[highlightedIndex].Path);
                        e.Handled = true;
                    }
                    break;
            }
        };

        var toolbar = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Padding = new Padding(4),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items = { new StackLayoutItem(searchBox, expand: true) }
        };
        if (onClear != null)
        {
            toolbar.Items.Add(MakeInlineButton("Clear", (_, _) =>
            {
                onClear();
                popup.Close();
            }, clearToolTip));
        }

        var content = new DynamicLayout { DefaultSpacing = new Size(0, 0), Padding = new Padding(0) };
        if (mode == LayerPickerMode.MultiSelect && selectedStack.Items.Count > 0)
        {
            content.Add(selectedStack, yscale: false);
            content.Add(new Panel { Height = 1, BackgroundColor = UiTheme.ToolbarBackground }, yscale: false);
        }
        content.Add(toolbar, yscale: false);
        content.Add(availableScroll, yscale: true);

        popup.Content = content;
        RebuildAvailableRows();
        PositionLayerPickerPopup(popup, anchor, preferredSize);
        popup.Show();
        Application.Instance.AsyncInvoke(() =>
        {
            if (!popup.IsDisposed)
                searchBox.Focus();
        });
    }

    private static StackLayout CreateLayerPickerRow(string text, Color dotColor, bool highlighted, Control? trailingControl = null)
    {
        var row = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Padding(6, 3),
            BackgroundColor = highlighted ? UiTheme.ListSelectionBackground : Colors.Transparent,
            Items =
            {
                new Panel { Width = 10, Height = 10, BackgroundColor = dotColor },
                new StackLayoutItem(new Label
                {
                    Text = text,
                    VerticalAlignment = VerticalAlignment.Center
                }, expand: true)
            }
        };

        if (trailingControl != null)
            row.Items.Add(trailingControl);

        return row;
    }

    private static Form CreateLayerPickerPopup(RhinoDoc doc)
    {
        var popup = new Form
        {
            ShowInTaskbar = false,
            Resizable = false,
            Minimizable = false,
            Maximizable = false,
            Title = string.Empty,
            Owner = RhinoEtoApp.MainWindowForDocument(doc)
        };
        popup.UseRhinoStyle();
        popup.LostFocus += (_, _) =>
        {
            Application.Instance.AsyncInvoke(() =>
            {
                if (!popup.HasFocus && !popup.IsDisposed)
                    popup.Close();
            });
        };
        return popup;
    }

    private void PositionLayerPickerPopup(Form popup, Control anchor, Size preferredSize)
    {
        var anchorTop = anchor.PointToScreen(PointF.Empty);
        var anchorBottom = anchor.PointToScreen(new PointF(0, anchor.Height));
        Screen screen = Screen.FromPoint(anchorBottom) ?? Screen.PrimaryScreen;
        var workingArea = screen.WorkingArea;
        int areaX = (int)Math.Round(workingArea.X);
        int areaY = (int)Math.Round(workingArea.Y);
        int areaWidth = (int)Math.Round(workingArea.Width);
        int areaHeight = (int)Math.Round(workingArea.Height);

        int maxWidth = Math.Max(120, areaWidth - (LayerPickerMargin * 2));
        int maxHeight = Math.Max(LayerPickerMinHeight, areaHeight - (LayerPickerMargin * 2));
        int width = Math.Min(preferredSize.Width, maxWidth);
        int height = Math.Min(preferredSize.Height, maxHeight);

        int spaceBelow = (areaY + areaHeight) - (int)anchorBottom.Y - LayerPickerMargin;
        int spaceAbove = (int)anchorTop.Y - areaY - LayerPickerMargin;
        bool openAbove = spaceBelow < height && spaceAbove > spaceBelow;
        int availableHeight = openAbove ? spaceAbove : spaceBelow;
        if (availableHeight > 0)
            height = Math.Min(height, Math.Max(LayerPickerMinHeight, availableHeight));
        height = Math.Min(height, maxHeight);

        int x = Math.Clamp((int)anchorBottom.X, areaX + LayerPickerMargin, areaX + areaWidth - width - LayerPickerMargin);
        int y = openAbove
            ? (int)anchorTop.Y - height
            : (int)anchorBottom.Y;
        y = Math.Clamp(y, areaY + LayerPickerMargin, areaY + areaHeight - height - LayerPickerMargin);

        popup.Size = new Size(width, height);
        popup.Location = new Point(x, y);
    }

}
