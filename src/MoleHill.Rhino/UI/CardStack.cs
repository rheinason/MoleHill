using Eto.Drawing;
using Eto.Forms;

namespace MoleHill.Rhino.UI;

/// <summary>
/// One tab's vertical stack of cards: the toolbar and notes above, each card wrapped in its accent strip,
/// and — for a reorderable stack — a drop target between cards, the drag handle that starts a reorder,
/// and the drag feedback. Modifiers, zones, analyses, annotations and objects were five copies of this,
/// differing only in the drag key and what a drop does; a stack now differs in exactly those two
/// arguments.
///
/// Drag feedback is the same everywhere: the hovered card's strip takes
/// <see cref="UiTheme.CardDropHighlight"/>, and the drop target above it lights with
/// <see cref="UiTheme.SepHighlight"/>.
/// </summary>
internal sealed class CardStack
{
    /// <summary>What a drop does: move <c>sourceId</c> to the drop target above <c>anchorId</c>
    /// (<see cref="Guid.Empty"/> = the tail target below the last card).</summary>
    public delegate void DropHandler(Guid terrainId, Guid sourceId, Guid anchorId);

    private readonly string? _dragKey;
    private readonly string _dropHelp;
    private readonly DropHandler? _onDrop;
    private readonly Dictionary<Guid, Panel> _targets = new();
    private readonly Dictionary<Guid, (Panel Strip, Color Accent)> _strips = new();
    private Guid _terrainId;
    private Guid? _dragOver;

    private CardStack(string? dragKey, string dropHelp, DropHandler? onDrop)
    {
        _dragKey = dragKey;
        _dropHelp = dropHelp;
        _onDrop = onDrop;
    }

    /// <summary>A stack whose cards the user can drag into a new order.</summary>
    public static CardStack Reorderable(string dragKey, string dropHelp, DropHandler onDrop) =>
        new(dragKey, dropHelp, onDrop);

    /// <summary>A stack in a fixed order: no handles, no drop targets.</summary>
    public static CardStack Fixed() => new(null, string.Empty, null);

    public StackLayout Layout { get; } = new()
    {
        Orientation = Orientation.Vertical,
        Spacing = 0,
        HorizontalContentAlignment = HorizontalAlignment.Stretch
    };

    /// <summary>Empties the stack and binds the drops that follow to <paramref name="terrainId"/>.</summary>
    public void Clear(Guid terrainId = default)
    {
        _terrainId = terrainId;
        _targets.Clear();
        _strips.Clear();
        _dragOver = null;
        Layout.Items.Clear();
    }

    /// <summary>A full-width control above or between the cards: a toolbar, a style row.</summary>
    public void Add(Control control) =>
        Layout.Items.Add(new StackLayoutItem(control, HorizontalAlignment.Stretch));

    /// <summary>A muted explanatory note: the empty state, or a hint about how the stack resolves.</summary>
    public void AddNote(string text) => Add(new Panel
    {
        Padding = new Padding(UiMetrics.SpaceLarge, UiMetrics.SpaceSmall),
        Content = new Label { Text = text, TextColor = UiTheme.MutedText, Wrap = WrapMode.Word }
    });

    /// <summary>Adds <paramref name="card"/> in its accent strip, with a drop target above it when reorderable.</summary>
    public void AddCard(Guid id, Control card, Color accent, Color? background = null)
    {
        if (_dragKey != null)
            AddDropTarget(id);

        var strip = CreateStrip(accent);
        var wrapper = WrapCard(card, strip, background ?? UiTheme.CardBackground);

        if (_dragKey != null)
        {
            _strips[id] = (strip, accent);
            WireCardDrop(wrapper, id);
        }

        Add(wrapper);
    }

    /// <summary>The coloured strip down a card's left edge.</summary>
    public static Panel CreateStrip(Color accent) =>
        new() { Width = UiMetrics.CardAccentWidth, BackgroundColor = accent };

    /// <summary>A card inside its strip and outer inset; also used for cards outside a stack (Terrain Settings).</summary>
    public static Panel WrapCard(Control card, Panel strip, Color background) => new()
    {
        Padding = new Padding(UiMetrics.SpaceSmall, UiMetrics.SpaceXSmall),
        BackgroundColor = background,
        Content = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 0,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items = { strip, new StackLayoutItem(card, expand: true) }
        }
    };

    /// <summary>Closes a reorderable stack with the drop target below its last card.</summary>
    public void AddTail()
    {
        if (_dragKey != null)
            AddDropTarget(Guid.Empty);
    }

    /// <summary>The grip that starts dragging card <paramref name="id"/>; greyed when <paramref name="enabled"/> is false.</summary>
    public Drawable CreateHandle(Guid id, string help, bool enabled = true)
    {
        var handle = CreateGrip();
        handle.ToolTip = help;
        if (!enabled || _dragKey == null)
        {
            handle.Enabled = false;
            handle.Cursor = Cursors.Default;
            return handle;
        }

        string key = _dragKey;
        handle.MouseDown += (_, e) =>
        {
            if (e.Buttons != MouseButtons.Primary)
                return;

            var data = new DataObject();
            data.SetString(id.ToString(), key);
            handle.DoDragDrop(data, DragEffects.Move);
        };
        return handle;
    }

    private void AddDropTarget(Guid anchorId)
    {
        var highlight = new Panel { BackgroundColor = Colors.Transparent };
        var target = new Panel
        {
            Height = UiMetrics.StackGap,
            Padding = new Padding(0, UiMetrics.SpaceXSmall),
            Content = highlight,
            ToolTip = _dropHelp,
            AllowDrop = true
        };
        _targets[anchorId] = highlight;

        target.DragEnter += (_, e) =>
        {
            if (!e.Data.Contains(_dragKey!)) return;
            e.Effects = DragEffects.Move;
            SetDragOver(null);
            highlight.BackgroundColor = UiTheme.SepHighlight;
        };
        target.DragLeave += (_, _) => highlight.BackgroundColor = Colors.Transparent;
        target.DragDrop += (_, e) => Drop(e, anchorId);

        Add(target);
    }

    private void WireCardDrop(Control wrapper, Guid id)
    {
        wrapper.AllowDrop = true;
        wrapper.DragEnter += (_, e) =>
        {
            if (!e.Data.Contains(_dragKey!)) return;
            e.Effects = DragEffects.Move;
            SetDragOver(id);
            if (_targets.TryGetValue(id, out var target))
                target.BackgroundColor = UiTheme.SepHighlight;
        };
        wrapper.DragLeave += (_, _) =>
        {
            if (_dragOver == id)
                SetDragOver(null);
        };
        wrapper.DragDrop += (_, e) => Drop(e, id);
    }

    private void Drop(DragEventArgs e, Guid anchorId)
    {
        if (!e.Data.Contains(_dragKey!) || !Guid.TryParse(e.Data.GetString(_dragKey!), out var sourceId))
            return;

        SetDragOver(null);
        _onDrop!(_terrainId, sourceId, anchorId);
    }

    /// <summary>Moves the card highlight to <paramref name="id"/> (or clears it) and clears every drop target.</summary>
    private void SetDragOver(Guid? id)
    {
        if (_dragOver is { } previous && _strips.TryGetValue(previous, out var old))
            old.Strip.BackgroundColor = old.Accent;

        _dragOver = id;
        if (id is { } current && _strips.TryGetValue(current, out var hovered))
            hovered.Strip.BackgroundColor = UiTheme.CardDropHighlight;

        foreach (var target in _targets.Values)
            target.BackgroundColor = Colors.Transparent;
    }

    private static Drawable CreateGrip()
    {
        var handle = new Drawable { Width = 16, Height = 22, Cursor = Cursors.Move };
        handle.Paint += (_, e) =>
        {
            var ink = SystemColors.ControlText;
            var dot = new Color(ink.R, ink.G, ink.B, 0.6f);
            float[] xs = { 5f, 10f };
            float[] ys = { 6f, 11f, 16f };
            foreach (var x in xs)
                foreach (var y in ys)
                    e.Graphics.FillEllipse(dot, x - 1.5f, y - 1.5f, 3f, 3f);
        };
        return handle;
    }
}
