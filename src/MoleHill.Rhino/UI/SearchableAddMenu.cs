using Eto.Drawing;
using Eto.Forms;
using MoleHill.Rhino.Registry;
using Rhino;
using Rhino.UI;

namespace MoleHill.Rhino.UI;

/// <summary>
/// The Add Modifier / Analysis / Annotation / Object drop-down, with a search box on top. A context menu
/// cannot host a text box, so this is a small borderless window under the button: type to filter
/// (<see cref="AddMenuSearch"/>), Up/Down to move, Enter or click to add, Esc or clicking away to close.
/// The highlighted entry's description shows under the list.
/// </summary>
internal sealed class SearchableAddMenu : Form
{
    private const int MenuWidth = 240;
    private const int RowHeight = 22;
    private const int MaxVisibleRows = 14;

    private readonly IReadOnlyList<AddMenuEntry> _entries;
    private IReadOnlyList<AddMenuEntry> _visible;
    private readonly TextBox _search;
    private readonly ListBox _list;
    private readonly Label _detail;
    private bool _closing;

    private SearchableAddMenu(IReadOnlyList<AddMenuEntry> entries)
    {
        _entries = entries;
        _visible = entries;

        WindowStyle = WindowStyle.None;
        Resizable = false;
        Maximizable = false;
        Minimizable = false;
        ShowInTaskbar = false;
        Topmost = true;
        var doc = RhinoDoc.ActiveDoc;
        if (doc != null)
            Owner = RhinoEtoApp.MainWindowForDocument(doc);
        this.UseRhinoStyle();
        BackgroundColor = UiTheme.RampPopup;

        _search = new TextBox { PlaceholderText = "Search…", Width = MenuWidth - 8 };
        UiControls.StyleInput(_search);
        _search.TextChanged += (_, _) => ApplyFilter();
        _search.KeyDown += OnSearchKeyDown;

        int rows = Math.Clamp(entries.Count, 1, MaxVisibleRows);
        _list = new ListBox
        {
            Width = MenuWidth - 8,
            Height = rows * RowHeight + 4,
            BackgroundColor = UiTheme.RampPopup,
            TextColor = UiTheme.PrimaryText,
        };
        _list.SelectedIndexChanged += (_, _) => UpdateDetail();
        _list.Activated += (_, _) => Choose(_list.SelectedIndex);
        _list.MouseUp += (_, e) =>
        {
            if (e.Buttons == MouseButtons.Primary)
                Choose(_list.SelectedIndex);
        };
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Keys.Escape)
            {
                e.Handled = true;
                CloseOnce();
            }
        };

        _detail = UiControls.Label(string.Empty, UiLabelRole.Meta, WrapMode.Word);
        _detail.Width = MenuWidth - 8;
        _detail.Height = 30;

        Content = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Padding = 4,
            Spacing = 4,
            Items = { _search, _list, _detail },
        };

        Shown += (_, _) => _search.Focus();
        LostFocus += (_, _) => Application.Instance.AsyncInvoke(() =>
        {
            if (!HasFocus && !_search.HasFocus && !_list.HasFocus)
                CloseOnce();
        });

        ApplyFilter();
    }

    /// <summary>Opens the menu just below <paramref name="anchor"/>.</summary>
    public static void Show(Control anchor, IReadOnlyList<AddMenuEntry> entries)
    {
        if (entries.Count == 0)
            return;

        var menu = new SearchableAddMenu(entries);
        PointF origin = anchor.PointToScreen(new PointF(0, anchor.Height));
        menu.Location = new Point((int)origin.X, (int)origin.Y);
        menu.Show();
    }

    private void ApplyFilter()
    {
        _visible = AddMenuSearch.Filter(_entries, _search.Text);
        _list.Items.Clear();
        foreach (AddMenuEntry entry in _visible)
        {
            Image? image = entry.IconName == null ? null : PanelIcons.Load(entry.IconName);
            _list.Items.Add(new ImageListItem { Text = entry.Label, Image = image });
        }

        _list.SelectedIndex = _visible.Count > 0 ? 0 : -1;
        UpdateDetail();
    }

    private void UpdateDetail()
    {
        int index = _list.SelectedIndex;
        _detail.Text = _visible.Count == 0
            ? "No match."
            : index >= 0 && index < _visible.Count ? _visible[index].Detail ?? string.Empty : string.Empty;
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Keys.Down:
                MoveSelection(+1);
                e.Handled = true;
                break;
            case Keys.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Keys.Enter:
                Choose(_list.SelectedIndex);
                e.Handled = true;
                break;
            case Keys.Escape:
                CloseOnce();
                e.Handled = true;
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        if (_visible.Count == 0)
            return;
        int index = _list.SelectedIndex < 0 ? 0 : _list.SelectedIndex + delta;
        _list.SelectedIndex = Math.Clamp(index, 0, _visible.Count - 1);
    }

    private void Choose(int index)
    {
        if (index < 0 || index >= _visible.Count)
            return;

        AddMenuEntry entry = _visible[index];
        CloseOnce();
        // After the window has gone, so the card rebuild does not race the popup's own teardown.
        Application.Instance.AsyncInvoke(() =>
        {
            try
            {
                entry.Invoke();
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"MoleHill: could not add {entry.Label}: {ex.Message}");
            }
        });
    }

    private void CloseOnce()
    {
        if (_closing)
            return;
        _closing = true;
        Close();
    }
}
