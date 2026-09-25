using System;
using System.Globalization;
using Eto.Drawing;
using Eto.Forms;
using MoleHill.Core.Analysis;
using MoleHill.Rhino.Services;

namespace MoleHill.Rhino.UI;

/// <summary>
/// The colour-ramp card: a legend that unfolds into an editor.
///
/// Collapsed it is what the old read-only legend was — histogram, ramp, tick labels — plus the mapping
/// controls that were previously stranded in a separate "Coloring &amp; intervals" section above it.
/// Clicking the ramp grows handles on the same bar and reveals a stop table beneath, so there is never a
/// second gradient on screen and the numbers under the ramp are the stop values you are editing. That
/// identity is the point: the legend labels *are* the inputs, so reading and editing are one gesture.
///
/// The control owns its own state and re-lays itself out; its callbacks commit through the panel's
/// recolour path, which does not rebuild the card. That is what lets a stop be dragged smoothly.
///
/// Every size here comes from <see cref="UiMetrics"/> and every multi-control run is laid out with the
/// adaptive primitives, so the card reflows down to <see cref="UiMetrics.CompactPanelTarget"/> instead of
/// pushing the dock sideways: a panel that scrolls horizontally is a broken panel.
/// </summary>
internal sealed class ColorRampControl : Panel
{
    private readonly ColorRampOptions _options;
    private readonly ColorRampBar _bar = new();
    private readonly StackLayout _root = new()
    {
        Orientation = Orientation.Vertical,
        Spacing = UiMetrics.SpaceMedium,
        HorizontalContentAlignment = HorizontalAlignment.Stretch
    };

    private ColorRamp _ramp;
    private AnalysisRange _range;
    private AnalysisColorMapper.Mode _mode;
    private double _interval;
    private bool _autoRange;
    private bool _expanded;
    private int _selected;

    public ColorRampControl(ColorRampOptions options)
    {
        _options = options;
        _ramp = options.Ramp;
        _range = options.Range.EnsureNonDegenerate();
        _mode = options.Mode;
        _interval = options.Interval;
        _autoRange = options.AutoRange;
        _expanded = options.Expanded;
        _selected = Math.Clamp(options.SelectedIndex, 0, _ramp.Count - 1);

        _bar.RampChanged += (_, e) =>
        {
            _ramp = e.Ramp;
            _options.OnRampChanged?.Invoke(e.Ramp, e.Live);
            // Mid-drag only the bar and the stop values move; a full Rebuild would re-create the very
            // Drawable the pointer is captured on.
            if (e.Live)
                RefreshStopTable();
            else
                Rebuild();
        };
        _bar.SelectionChanged += (_, index) =>
        {
            SetSelected(index);
            RefreshStopTable();
        };
        _bar.EditRequested += (_, _) => SetExpanded(true);

        Content = _root;
        Rebuild();
    }

    private void Rebuild()
    {
        SuspendLayout();
        _root.Items.Clear();

        Add(BuildHeader());
        Add(_bar);
        Add(BuildTickRow());

        if (_expanded)
        {
            Add(BuildStopTable());
            Add(BuildStopToolbar());
        }

        Add(BuildMappingHeader());
        Add(BuildModeRow());

        if (_mode == AnalysisColorMapper.Mode.Stepped)
            Add(BuildIntervalRow());

        // A cyclic quantity's range is the compass: 0 to 360, fixed. Auto-fit has nothing to fit and the
        // bounds have nothing to set, so the two rows are omitted rather than shown inert — which is the
        // confusion this card was rebuilt to remove.
        if (_options.Shape != RangeShape.Cyclic)
        {
            Add(BuildAutoRow());
            Add(BuildRangeRow());
        }

        _bar.Update(_ramp, _range, _mode, _interval, _options.Histogram, _expanded, _selected);
        ResumeLayout();
    }

    private void Add(Control control) =>
        _root.Items.Add(new StackLayoutItem(control, HorizontalAlignment.Stretch));

    // ── Header ─────────────────────────────────────────────────────────────

    private Control BuildHeader()
    {
        var chevron = UiControls.Label(_expanded ? "▾" : "▸");
        chevron.Width = UiMetrics.SpaceXLarge;

        var title = UiControls.Label(_options.Title);
        title.Font = new Font(SystemFont.Bold);

        var mode = UiControls.Label(DescribeMode(), UiLabelRole.Meta);

        var layout = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = UiMetrics.SpaceMedium,
            Padding = new Padding(UiMetrics.SpaceMedium, UiMetrics.SpaceXSmall),
            VerticalContentAlignment = VerticalAlignment.Center,
            Items = { chevron, title, new StackLayoutItem(null, expand: true), mode }
        };

        var header = new Panel
        {
            BackgroundColor = UiTheme.RampHeader,
            Content = layout,
            Cursor = Cursors.Pointer,
            ToolTip = "Show or hide the ramp's stops."
        };

        // The mode tag is secondary metadata: it disappears before the title is squeezed, the same rule
        // the analysis card headers follow, so a narrow dock never has to widen to fit it.
        //
        // The title is then ellipsized to whatever is left. An analysis can be renamed to anything, and a
        // Label reports its full text as its preferred width — which is exactly how a single long name
        // would push the whole dock into horizontal scroll.
        void FitHeader()
        {
            int width = header.Width;
            if (width <= 0)
            {
                mode.Visible = true;
                title.Text = _options.Title;
                return;
            }

            mode.Visible = width >= UiMetrics.HeaderStatusBreak;

            int chrome = (UiMetrics.SpaceMedium * 2) + UiMetrics.SpaceXLarge + UiMetrics.SpaceMedium;
            if (mode.Visible)
                chrome += UiMetrics.Chs(DescribeMode().Length) + UiMetrics.SpaceMedium;

            int available = Math.Max(UiMetrics.Chs(4), width - chrome);
            title.Text = Ellipsize(_options.Title, available / Math.Max(1, UiMetrics.Chs(1)));
        }

        header.SizeChanged += (_, _) => FitHeader();
        FitHeader();

        header.MouseDown += (_, e) =>
        {
            if (e.Buttons == MouseButtons.Primary)
                SetExpanded(!_expanded);
        };

        return header;
    }

    private static string Ellipsize(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars)
            return text;

        return maxChars <= 3 ? text[..Math.Max(1, maxChars)] : text[..(maxChars - 3)] + "...";
    }

    /// <summary>The one-line summary in the header: what the ramp is doing when you cannot see its controls.</summary>
    private string DescribeMode()
    {
        if (!_options.HasData)
            return "not built yet";

        string suffix = _autoRange ? " · auto-fit" : string.Empty;
        switch (_mode)
        {
            case AnalysisColorMapper.Mode.Stepped:
                int bands = AnalysisColorMapper.ResolveBands(_range, _interval, _ramp.Stops).Count;
                return $"stepped · {bands}{suffix}";

            case AnalysisColorMapper.Mode.Constant:
                return $"constant · {_ramp.Count}{suffix}";

            default:
                return "linear" + suffix;
        }
    }

    // ── Tick row ───────────────────────────────────────────────────────────

    /// <summary>
    /// The numbers under the ramp. Positioned by their real place in the range rather than spread evenly,
    /// so a tick always sits under the colour it describes, and thinned to what the current width can
    /// actually hold. This is painted rather than built from Labels precisely so it can never widen the
    /// panel: a row of Labels would each demand their preferred width and push the dock sideways.
    /// </summary>
    private Control BuildTickRow()
    {
        var ticks = ColorRampBar.BuildTicks(_range);
        var font = SystemFonts.Default(SystemFonts.Default().Size - 1.5f);

        var row = new Drawable
        {
            Height = UiMetrics.SpaceXLarge,
            MinimumSize = new Size(0, UiMetrics.SpaceXLarge)
        };
        row.Paint += (_, e) =>
        {
            int width = row.Width;
            if (width <= 0)
                return;

            var g = e.Graphics;
            float widest = 0f;
            foreach (double tick in ticks)
                widest = Math.Max(widest, g.MeasureString(font, _options.FormatValue(tick)).Width);

            // Thin until the widest label plus a gap fits its slot. At 240px with long numbers this can
            // come down to just the two ends, which is still an honest legend — unlike overlapping text.
            int slots = Math.Max(1, (int)(width / Math.Max(1f, widest + UiMetrics.SpaceLarge)));
            int stride = Math.Max(1, (int)Math.Ceiling(ticks.Count / (double)slots));

            for (int i = 0; i < ticks.Count; i += stride)
            {
                // Until something has been coloured the range is a guess; ticks would dress it up as a
                // measurement, so the ramp shows its colours and stays quiet about values.
                string text = _options.HasData ? _options.FormatValue(ticks[i]) : string.Empty;
                if (text.Length == 0)
                    continue;

                SizeF size = g.MeasureString(font, text);
                float x = (float)_range.Normalize(ticks[i]) * (width - 1);
                float left = Math.Clamp(x - (size.Width * 0.5f), 0f, Math.Max(0f, width - size.Width));
                g.DrawText(font, UiTheme.MutedText, left, 0f, text);
            }
        };

        return row;
    }

    // ── Stop table ─────────────────────────────────────────────────────────

    private StackLayout? _stopTable;

    private Control BuildStopTable()
    {
        _stopTable = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = UiMetrics.SpaceHairline,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        FillStopTable();
        return _stopTable;
    }

    private void RefreshStopTable()
    {
        _bar.Update(_ramp, _range, _mode, _interval, _options.Histogram, _expanded, _selected);
        if (_stopTable == null || !_expanded)
            return;

        _stopTable.SuspendLayout();
        _stopTable.Items.Clear();
        FillStopTable();
        _stopTable.ResumeLayout();
    }

    private void FillStopTable()
    {
        if (_stopTable == null)
            return;

        for (int i = 0; i < _ramp.Count; i++)
            _stopTable.Items.Add(new StackLayoutItem(BuildStopRow(i), HorizontalAlignment.Stretch));
    }

    /// <summary>
    /// One stop: swatch, its value in the analysis's unit, its position, and a delete. The value column
    /// shows the value rather than a hex code, because that is the number a reader of the map cares about
    /// — the hex is one click away in the colour dialog.
    ///
    /// Every fixed part of the row is deliberately small and the value takes the slack, so the row fits
    /// the narrowest supported panel on one line. A stop table that reflowed as you dragged would be
    /// unusable, so this row never wraps — it only lets the value column shrink.
    /// </summary>
    private Control BuildStopRow(int index)
    {
        var stop = _ramp[index];
        bool selected = index == _selected;
        var stopColour = Color.FromArgb(stop.R, stop.G, stop.B);

        var swatch = new Drawable
        {
            Width = UiMetrics.IconSize,
            Height = UiMetrics.SpaceXLarge,
            Cursor = Cursors.Pointer,
            ToolTip = "Click to pick this stop's colour."
        };
        swatch.Paint += (_, e) =>
        {
            e.Graphics.FillRectangle(stopColour, 0, 0, swatch.Width, swatch.Height);
            e.Graphics.DrawRectangle(UiTheme.RampSwatchOutline, 0, 0, swatch.Width - 1, swatch.Height - 1);
        };
        swatch.MouseDown += (_, e) =>
        {
            if (e.Buttons != MouseButtons.Primary)
                return;

            e.Handled = true;
            PickStopColour(index, stopColour);
        };

        Color textColour = selected ? UiTheme.RampAccentText : UiTheme.PrimaryText;

        var value = UiControls.Label(_options.FormatValue(_range.Low + (stop.Position * _range.Span)));
        value.TextColor = textColour;

        var position = UiControls.Label(
            (stop.Position * 100.0).ToString("0", CultureInfo.CurrentCulture) + "%");
        position.TextColor = selected ? UiTheme.RampAccentText : UiTheme.MutedText;
        position.Width = UiMetrics.Chs(4);
        position.TextAlignment = TextAlignment.Right;

        bool removable = _ramp.Count > ColorRamp.MinimumStops;
        var delete = UiControls.Label("×");
        delete.TextColor = removable ? textColour : UiTheme.MutedText;
        delete.Width = UiMetrics.SpaceXLarge;
        delete.TextAlignment = TextAlignment.Center;
        delete.ToolTip = removable ? "Remove this stop." : "A ramp needs at least two stops.";
        if (removable)
        {
            delete.Cursor = Cursors.Pointer;
            delete.MouseDown += (_, e) =>
            {
                if (e.Buttons != MouseButtons.Primary)
                    return;

                e.Handled = true;
                ApplyRamp(_ramp.RemoveAt(index), Math.Max(0, index - 1));
            };
        }

        var layout = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = UiMetrics.SpaceMedium,
            Padding = new Padding(UiMetrics.SpaceSmall, UiMetrics.SpaceXSmall),
            VerticalContentAlignment = VerticalAlignment.Center,
            Items =
            {
                swatch,
                new StackLayoutItem(value, expand: true),
                position,
                delete
            }
        };

        var row = new Panel
        {
            Content = layout,
            BackgroundColor = selected ? UiTheme.RampAccent : Colors.Transparent,
            Cursor = Cursors.Pointer
        };
        row.MouseDown += (_, e) =>
        {
            if (e.Buttons != MouseButtons.Primary)
                return;

            SetSelected(index);
            RefreshStopTable();
        };

        return row;
    }

    private void PickStopColour(int index, Color current)
    {
        var dialog = new ColorDialog { Color = current, AllowAlpha = false };
        if (dialog.ShowDialog(this) != DialogResult.Ok)
            return;

        Color picked = dialog.Color;
        ApplyRamp(
            _ramp.WithStopColor(
                index,
                (byte)Math.Round(picked.Rb * 255f),
                (byte)Math.Round(picked.Gb * 255f),
                (byte)Math.Round(picked.Bb * 255f)),
            index);
    }

    // ── Stop toolbar ───────────────────────────────────────────────────────

    /// <summary>
    /// The stop verbs. An <see cref="AdaptiveControlGroup"/> rather than a fixed row: six buttons do not
    /// fit a 240px dock on one line, and wrapping them onto a second line is right where clipping or
    /// sideways scroll is not.
    /// </summary>
    private Control BuildStopToolbar()
    {
        var add = ToolButton("+", "Add a stop in the widest gap.", () => ApplyRamp(_ramp.Insert(), _selected));
        var remove = ToolButton("−", "Remove the selected stop.",
            () => ApplyRamp(_ramp.RemoveAt(_selected), Math.Max(0, _selected - 1)));
        var distribute = ToolButton("⇉", "Space the stops evenly.", () => ApplyRamp(_ramp.Distribute(), _selected));
        var reverse = ToolButton("⇄", "Reverse the ramp.", () => ApplyRamp(_ramp.Reverse(), _selected));

        var presets = UiControls.Button("Presets", (_, _) => ShowPresetMenu(),
            "Replace the ramp with a built-in or saved one.", UiButtonRole.Inline);
        var save = UiControls.Button("Save", (_, _) => SaveCurrentRamp(),
            "Save this ramp so other analyses can use it.", UiButtonRole.Inline);

        return new AdaptiveControlGroup(
            UiMetrics.SpaceSmall, add, remove, distribute, reverse, presets, save);
    }

    private static Button ToolButton(string glyph, string help, Action onClick)
    {
        var button = UiControls.Button(glyph, (_, _) => onClick(), help, UiButtonRole.Inline);
        button.Width = UiMetrics.IconButtonWidth;
        return button;
    }

    // ── Mapping ────────────────────────────────────────────────────────────

    private Control BuildMappingHeader()
    {
        var label = UiControls.Label("Mapping", UiLabelRole.Section);
        var rule = new Panel { Height = UiMetrics.SpaceHairline, BackgroundColor = UiTheme.RampDivider };

        // Collapsed, the stop toolbar that carries these is hidden, so the two things you would still
        // reach for — the editor itself, and a different preset — surface on this divider instead. It is
        // an adaptive group because those two buttons plus the rule do not fit a narrow dock.
        if (!_expanded)
        {
            var edit = UiControls.Button("Edit ramp", (_, _) => SetExpanded(true),
                "Show the ramp's stops.", UiButtonRole.Inline);
            var presets = UiControls.Button("Presets", (_, _) => ShowPresetMenu(),
                "Replace the ramp with a built-in or saved one.", UiButtonRole.Inline);

            return new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = UiMetrics.SpaceSmall,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    new StackLayoutItem(SectionRule(label, rule), HorizontalAlignment.Stretch),
                    // Stretch, not Left: left-aligned the group reports a narrow width to its own
                    // relayout and wraps two buttons that comfortably fit even at 240px.
                    new StackLayoutItem(
                        new AdaptiveControlGroup(UiMetrics.SpaceSmall, edit, presets),
                        HorizontalAlignment.Stretch)
                }
            };
        }

        return SectionRule(label, rule);
    }

    private static Control SectionRule(Control label, Control rule) => new StackLayout
    {
        Orientation = Orientation.Horizontal,
        Spacing = UiMetrics.SpaceLarge,
        VerticalContentAlignment = VerticalAlignment.Center,
        Items = { label, new StackLayoutItem(rule, expand: true) }
    };

    private Control BuildModeRow()
    {
        var linear = Tab("Linear", _mode == AnalysisColorMapper.Mode.Gradient,
            "Blend smoothly between the stops.",
            () => SetMode(AnalysisColorMapper.Mode.Gradient));
        var stepped = Tab("Stepped", _mode == AnalysisColorMapper.Mode.Stepped,
            "Equal-width bands across the range, sized by the interval below.",
            () => SetMode(AnalysisColorMapper.Mode.Stepped));
        var constant = Tab("Constant", _mode == AnalysisColorMapper.Mode.Constant,
            "Hold each stop's colour until the next stop, so the stops are the band edges. " +
            "Use this for unequal bands — one wide acceptable range, then a few narrow ones.",
            () => SetMode(AnalysisColorMapper.Mode.Constant));

        return new AdaptiveColumns(UiMetrics.SpaceSmall, UiMetrics.Chs(8), linear, stepped, constant);
    }

    /// <summary>
    /// The band width, on its own row. It shares no line with the mode tabs because at the narrowest
    /// supported width the two together do not fit, and this is the field most likely to hold a long
    /// number.
    /// </summary>
    private Control BuildIntervalRow()
    {
        // Zero means "pick a readable width for me", which is the default and has to stay reachable —
        // that is what alt-click resets to. The star marks that the shown number is the automatic one.
        double resolved = AnalysisColorMapper.ResolveInterval(_range.Low, _range.High, _interval);
        bool automatic = _interval <= 0.0;

        return new ScrubField(
            "Interval",
            automatic ? resolved : _interval,
            value => _options.FormatValue(value) + (automatic ? "*" : string.Empty),
            SetInterval,
            () => 0.0,
            min: 0.0,
            help: "Width of each band. Drag to scrub; alt-click for an automatic width (shown with *).")
        {
            Step = ScrubField.StepForSpan(_range.Span)
        };
    }

    private Control BuildAutoRow()
    {
        var auto = new CheckBox
        {
            Text = "Auto-fit range",
            Checked = _autoRange,
            ToolTip = "Fit the range to the data, ignoring outliers so one steep or deep face cannot " +
                      "flatten the whole ramp. Turn off to set the bounds by hand.",
            TextColor = UiTheme.PrimaryText
        };
        auto.CheckedChanged += (_, _) => SetAutoRange(auto.Checked == true);
        return auto;
    }

    private Control BuildRangeRow()
    {
        double span = _range.Span;
        bool symmetric = _options.Shape == RangeShape.SymmetricAboutZero;
        bool fromZero = _options.Shape == RangeShape.FromZero;

        // A symmetric range keeps the larger magnitude of the pair it is given, so pairing the edited
        // bound with the other, unedited one could widen the range but never narrow it. The edited
        // field's magnitude must win: mirror it onto the other side before the shape is applied.
        var min = new ScrubField(
            symmetric ? "Cut" : "Min",
            _range.Low,
            _options.FormatValue,
            (value, live) => SetRange(value, symmetric ? -value : _range.High, live),
            () => _range.Low,
            max: _range.High,
            help: symmetric
                ? "Largest cut depth mapped. The range stays symmetric, so no change sits mid-ramp."
                : "Values at or below this use the first colour. Drag to scrub.")
        {
            Step = ScrubField.StepForSpan(span)
        };

        var max = new ScrubField(
            symmetric ? "Fill" : "Max",
            _range.High,
            _options.FormatValue,
            (value, live) => SetRange(symmetric ? -value : _range.Low, value, live),
            () => _range.High,
            min: fromZero ? 0.0 : null,
            help: "Values at or above this use the last colour. Drag to scrub.")
        {
            Step = ScrubField.StepForSpan(span)
        };

        // Auto-fit owns the bounds while it is on, so the fields become a readout rather than lying about
        // being editable. This was the core of the old card's confusion: five rows, three of them inert.
        min.Enabled = !_autoRange;
        max.Enabled = !_autoRange && !symmetric;

        return new AdaptiveColumns(UiMetrics.SpaceSmall, UiMetrics.Chs(10), min, max);
    }

    // ── Presets ────────────────────────────────────────────────────────────

    private void ShowPresetMenu()
    {
        var menu = new ContextMenu();

        foreach (var preset in ColorRampPresets.All)
        {
            string key = preset.Key;
            var item = new ButtonMenuItem { Text = preset.Label };
            item.Click += (_, _) => _options.OnPresetPicked?.Invoke(key);
            menu.Items.Add(item);
        }

        var saved = ColorRampPresetStore.All;
        if (saved.Count > 0)
        {
            menu.Items.Add(new SeparatorMenuItem());
            foreach (var preset in saved)
            {
                ColorRamp ramp = preset.Ramp;
                var item = new ButtonMenuItem { Text = preset.Label };
                item.Click += (_, _) => ApplyRamp(ramp, 0);
                menu.Items.Add(item);
            }
        }

        menu.Show(this);
    }

    private void SaveCurrentRamp()
    {
        var box = new TextBox { Text = _options.Title, Width = UiMetrics.Chs(24) };
        UiControls.StyleInput(box);

        var dialog = new Dialog { Title = "Save colour ramp", Padding = new Padding(UiMetrics.SpaceXLarge) };
        bool commit = false;

        var ok = UiControls.Button("Save", (_, _) => { commit = true; dialog.Close(); }, role: UiButtonRole.Toolbar);
        var cancel = UiControls.Button("Cancel", (_, _) => dialog.Close(), role: UiButtonRole.Toolbar);
        dialog.DefaultButton = ok;
        dialog.AbortButton = cancel;

        dialog.Content = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = UiMetrics.SpaceLarge,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                UiControls.Label("Name this ramp so other analyses can use it:"),
                box,
                new StackLayout
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = UiMetrics.SpaceMedium,
                    Items = { new StackLayoutItem(null, expand: true), cancel, ok }
                }
            }
        };

        dialog.ShowModal(this);

        string name = box.Text?.Trim() ?? string.Empty;
        if (commit && name.Length > 0)
            ColorRampPresetStore.Save(name, _ramp);
    }

    // ── State transitions ──────────────────────────────────────────────────

    private void ApplyRamp(ColorRamp ramp, int selectIndex)
    {
        if (ramp.Matches(_ramp))
            return;

        _ramp = ramp;
        SetSelected(Math.Clamp(selectIndex, 0, ramp.Count - 1));
        _options.OnRampChanged?.Invoke(ramp, false);
        Rebuild();
    }

    /// <summary>
    /// Mid-gesture, touch only what moved; on release, relay the whole card out. Rebuilding during a drag
    /// replaces the control under the pointer and ends the gesture.
    /// </summary>
    private void RefreshAfter(bool live)
    {
        if (live)
        {
            _bar.Update(_ramp, _range, _mode, _interval, _options.Histogram, _expanded, _selected);
            Invalidate(true);
        }
        else
        {
            Rebuild();
        }
    }

    private void SetSelected(int index)
    {
        int next = Math.Clamp(index, 0, _ramp.Count - 1);
        if (_selected == next)
            return;

        _selected = next;
        _options.OnSelectionChanged?.Invoke(next);
    }

    private void SetExpanded(bool expanded)
    {
        if (_expanded == expanded)
            return;

        _expanded = expanded;
        _options.OnExpandedChanged?.Invoke(expanded);
        Rebuild();
    }

    private void SetMode(AnalysisColorMapper.Mode mode)
    {
        if (_mode == mode)
            return;

        _mode = mode;
        _options.OnModeChanged?.Invoke(mode);
        Rebuild();
    }

    private void SetInterval(double interval, bool live)
    {
        double next = Math.Max(0.0, interval);
        if (Math.Abs(_interval - next) < 1e-12)
            return;

        _interval = next;
        _options.OnIntervalChanged?.Invoke(next, live);
        RefreshAfter(live);
    }

    private void SetRange(double low, double high, bool live)
    {
        // Route through the shape so cut/fill cannot be dragged asymmetric and slope cannot start below
        // zero — the same rule the mesh colouring applies, applied here so the card can never show a range
        // the mesh would refuse.
        AnalysisRange next = AnalysisRange.FromRequested(low, high, _options.Shape);
        if (Math.Abs(next.Low - _range.Low) < 1e-12 && Math.Abs(next.High - _range.High) < 1e-12)
            return;

        _range = next;

        // Setting a bound by hand is the gesture that means "stop fitting this for me".
        if (_autoRange)
        {
            _autoRange = false;
            _options.OnAutoRangeChanged?.Invoke(false);
        }

        _options.OnRangeChanged?.Invoke(next.Low, next.High, live);
        RefreshAfter(live);
    }

    private void SetAutoRange(bool auto)
    {
        if (_autoRange == auto)
            return;

        _autoRange = auto;
        _options.OnAutoRangeChanged?.Invoke(auto);
        Rebuild();
    }

    /// <summary>One half of the Linear/Stepped pair. A real Button so it keeps native focus and keyboard
    /// use; only its fill says which one is active.</summary>
    private static Button Tab(string text, bool active, string help, Action onClick)
    {
        var button = UiControls.Button(text, (_, _) => onClick(), help, UiButtonRole.Inline);
        if (active)
        {
            button.BackgroundColor = UiTheme.RampAccent;
            button.TextColor = UiTheme.RampAccentText;
        }

        return button;
    }
}
