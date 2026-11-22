using System.Globalization;
using Eto.Forms;
using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Services;
using Rhino;
using RhinoObjectType = Rhino.DocObjects.ObjectType;

namespace MoleHill.Rhino.UI;

// Reusable input-editor widgets shared across the panel's card bodies: labelled numeric steppers
// (plain, optional, and slider-backed), check/dropdown/text editors, read-only and summary rows,
// source (object + layer) pickers, layer assignment, and color editors. These are the panel's
// form-control vocabulary — a card body is assembled mostly by composing these.
public sealed partial class MoleHillPanel
{
    private Control CreateSourceEditor(
        string label,
        SourceReferenceSet sourceSet,
        Action<Action<SourceReferenceSet>> mutateSourceSet,
        RhinoObjectType objectFilter,
        Func<RhinoDoc, IEnumerable<string>> getLayerPaths,
        string? help = null,
        Control? objectAccessory = null)
    {
        // ── Objects pill ──────────────────────────────────────────
        var activeDoc = RhinoDoc.ActiveDoc;
        var liveObjectIds = activeDoc == null
            ? sourceSet.ObjectIds.Distinct().ToList()
            : sourceSet.ObjectIds
                .Where(id => activeDoc.Objects.FindId(id) != null)
                .Distinct()
                .ToList();

        string objText = liveObjectIds.Count == 0
            ? "Objects"
            : $"{liveObjectIds.Count} Obj";
        var objectsPill = MakePillButton(objText,
            $"Edit the {label.ToLowerInvariant()} object set. Press Enter to accept.");
        objectsPill.Click += (_, _) =>
        {
            var doc = RhinoDoc.ActiveDoc;
            if (doc == null)
                return;

            Application.Instance.AsyncInvoke(() =>
            {
                if (IsDisposed)
                    return;

                var selectedIds = _controller.EditSourceObjectIds(
                    doc,
                    sourceSet.ObjectIds,
                    objectFilter,
                    $"Adjust {label} selection. Press Enter to accept.");
                if (selectedIds == null)
                    return;

                mutateSourceSet(set => set.ReplaceObjects(selectedIds));
                RefreshUi();
            });
        };

        var objectsRow = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = liveObjectIds.Count > 0 ? 0 : UiMetrics.SpaceSmall,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items = { new StackLayoutItem(objectsPill, expand: true) }
        };
        if (liveObjectIds.Count > 0)
        {
            objectsRow.Items.Add(MakeIconButton(PanelButtonIcon.Clear, (_, _) =>
                {
                    mutateSourceSet(set => set.ReplaceObjects(Array.Empty<Guid>()));
                    RefreshUi();
                },
                "Clear all referenced objects."));
        }
        if (objectAccessory != null)
            objectsRow.Items.Add(objectAccessory);

        // ── Layers pill ───────────────────────────────────────────
        int lc = sourceSet.LayerPaths.Count;
        string layText = lc == 0 ? "Layers" : Count(lc, "Layer", "Layers");
        var layersPill = MakePillButton(layText, lc == 0 ? "Manage input layers." : $"Manage input layers. {lc} assigned.");
        layersPill.Click += (_, _) => ShowLayerSourcePopover(
            layersPill,
            sourceSet.LayerPaths,
            path =>
            {
                mutateSourceSet(set => set.AddLayer(path));
                RefreshUi();
            },
            path =>
            {
                mutateSourceSet(set => set.RemoveLayer(path));
                RefreshUi();
            });

        var layersClear = MakeIconButton(PanelButtonIcon.Clear, (_, _) =>
        {
            mutateSourceSet(set => set.ReplaceLayers(Array.Empty<string>()));
            RefreshUi();
        },
            "Clear all assigned layers.");
        var layersRow = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = lc > 0 ? 0 : UiMetrics.SpaceSmall,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items = { new StackLayoutItem(layersPill, expand: true) }
        };
        if (lc > 0)
            layersRow.Items.Add(layersClear);

        // ── Layout ────────────────────────────────────────────────
        // The total sits on the heading ("Sources — 3"), never on a row of its own (design review UI-13,
        // UI-19); the tooltip says what it is made of.
        int total = liveObjectIds.Count + lc;
        var titleLabel = UiControls.Label(total > 0 ? $"{label} — {total}" : label);
        string breakdown = total > 0
            ? $"{Count(liveObjectIds.Count, "object", "objects")}, {Count(lc, "watched layer", "watched layers")}. "
            : string.Empty;
        ApplyHelp(titleLabel, breakdown + (help ?? $"{label} accepts Rhino object picks and layers."));

        var pillsLayout = new AdaptiveColumns(
            UiMetrics.SpaceSmall,
            UiMetrics.Chs(9),
            objectsRow,
            layersRow);

        return new PropertyRow(titleLabel, pillsLayout, expandWidget: true);
    }

    private Control CreateNumericEditor(
        string label,
        double value,
        Action<double> onChanged,
        int decimalPlaces = 3,
        string? help = null,
        double? minValue = 0,
        double? maxValue = null,
        bool liveEdit = false,
        double? step = null,
        string? unitSuffix = null)
    {
        help ??= GetNumericHelp(label);
        // The unit inside the field and one reading of typed text for every number row (NumberField).
        var field = new NumberField
        {
            DecimalPlaces = decimalPlaces,
            Unit = unitSuffix,
            MinValue = minValue ?? double.MinValue,
            MaxValue = maxValue ?? double.MaxValue,
            Increment = step ?? (decimalPlaces == 0 ? 1 : 0.1),
            Value = value
        };
        ApplyHelp(field.TextBox, help);
        var timer = new UITimer { Interval = UiTiming.NumericCommitSeconds };
        double committedValue = field.Value;
        void Commit(double numericValue)
        {
            timer.Stop();
            if (!TerrainCommitGuard.HasMeaningfulNumericChange(committedValue, numericValue))
                return;

            committedValue = numericValue;
            onChanged(numericValue);
        }

        // liveEdit brackets the focus gesture in the controller refresh deferral (like the slider editor),
        // so commits while the field is focused don't tear the card down mid-edit (focus loss / scroll
        // jump). Callers pair this with deferDocumentSave + suppressImmediateUiRefresh on their mutate.
        bool editActive = false;
        void BeginEdit()
        {
            if (!liveEdit || editActive)
                return;

            editActive = true;
            BeginControllerRefreshDeferral();
        }
        void EndEdit()
        {
            if (!editActive)
                return;

            editActive = false;
            EndControllerRefreshDeferral();
        }
        if (liveEdit)
            field.TextBox.GotFocus += (_, _) => BeginEdit();

        // Only liveEdit rows commit while typing, debounced, and only text that already reads as a number;
        // other rows wait for Enter, leaving the field or an arrow, so "1" is never committed on the way
        // to "125".
        timer.Elapsed += (_, _) =>
        {
            if (field.TryReadTyped(out double typed))
                Commit(typed);
        };
        field.Edited += (_, _) =>
        {
            if (_isRefreshing || !liveEdit)
                return;

            BeginEdit();
            timer.Stop();
            timer.Start();
        };
        field.ValueChanged += (_, _) =>
        {
            if (_isRefreshing)
                return;

            Commit(field.Value);
        };
        field.TextBox.LostFocus += (_, _) => EndEdit();
        field.UnLoad += (_, _) =>
        {
            timer.Stop();
            EndEdit();
        };
        return new PropertyRow(CreateHelpLabel(label, help, 0), field, expandWidget: true);
    }

    /// <summary>
    /// A numeric editor that is "inherited" when left blank: the field shows the
    /// <paramref name="inheritedValue"/> as greyed placeholder text until the user types an override.
    /// A current <paramref name="value"/> &lt;= 0 is treated as inherited (blank). Clearing the field
    /// commits 0 (inherit again).
    /// </summary>
    private Control CreateOptionalNumericEditor(
        string label,
        double value,
        double inheritedValue,
        Action<double> onChanged,
        int decimalPlaces = 3,
        string? help = null,
        string? unitSuffix = null)
    {
        help ??= GetNumericHelp(label);
        string Show(double number) => UserNumber.WithUnit(UserNumber.Format(number, decimalPlaces), unitSuffix);
        var textBox = new TextBox
        {
            Text = value > 0.0 ? Show(value) : string.Empty,
            // Greyed: what a blank box stands for. Nothing resolved yet (0) reads as Auto.
            PlaceholderText = inheritedValue > 0.0 ? Show(inheritedValue) : "Auto"
        };
        ApplyHelp(textBox, help);

        double committedValue = value > 0.0 ? value : 0.0;
        void Commit()
        {
            double parsed = 0.0;
            string text = textBox.Text?.Trim() ?? string.Empty;
            if (text.Length > 0 && UserNumber.TryParseLeading(text, out double v) && v > 0.0)
            {
                parsed = v;
            }

            // Echo the reading back with its unit, so "3" becomes "3 m" and a rejected entry clears.
            textBox.Text = parsed > 0.0 ? Show(parsed) : string.Empty;

            if (!TerrainCommitGuard.HasMeaningfulNumericChange(committedValue, parsed))
                return;

            committedValue = parsed;
            onChanged(parsed);
        }
        // Commit on Enter/blur only — TextChanged fires per keystroke, so debouncing off it would still
        // commit garbage intermediate values while the user is mid-type.
        textBox.KeyDown += (_, e) =>
        {
            if (e.Key != Keys.Enter)
                return;

            Commit();
            e.Handled = true;
        };
        textBox.LostFocus += (_, _) =>
        {
            if (_isRefreshing)
                return;

            Commit();
        };

        return new PropertyRow(CreateHelpLabel(label, help, 0), textBox, expandWidget: true);
    }

    /// <summary>
    /// A slope field. The value is stored as an angle in degrees, but shown in whatever slope unit the
    /// user works in and parsed with <see cref="SlopeInput"/>, so the same field accepts 25%, 1:3, 50‰ or
    /// 14° regardless of which unit it is currently displaying — the Rhino length-field bargain, where
    /// "10mm" is understood in a document working in metres.
    ///
    /// <para>A text box rather than a NumericStepper, because a stepper cannot hold "1:3". Unparseable
    /// text reverts to the last committed value rather than committing a guess, and a good parse is
    /// echoed back in the display unit immediately — which is what makes the vertical:horizontal reading
    /// of "1:3" self-correcting for anyone whose office writes it the other way round.</para>
    ///
    /// <para><paramref name="inheritedDegrees"/> turns the field into the inherit-when-blank variant used
    /// by the cut-slope overrides: blank shows the inherited slope as greyed placeholder text, and
    /// clearing the field commits 0 (inherit again).</para>
    /// </summary>
    private Control CreateSlopeEditor(
        string label,
        double degrees,
        Action<double> onChanged,
        string? help = null,
        double? inheritedDegrees = null)
    {
        SlopeAnalyzer.SlopeUnit unit = SlopeUnitPreference.Current;
        bool optional = inheritedDegrees.HasValue;

        help = ComposeSlopeHelp(help ?? GetNumericHelp(label), optional);

        // The unit is part of the text ("25%", "1:3"), which SlopeInput reads back as written.
        string Show(double slopeDegrees) => UserNumber.WithUnit(
            SlopeInput.FormatDegreesAsUnit(slopeDegrees, unit),
            unit == SlopeAnalyzer.SlopeUnit.Ratio ? null : SlopeInput.Suffix(unit));
        var textBox = new TextBox
        {
            Text = optional && degrees <= 0.0 ? string.Empty : Show(degrees)
        };
        if (optional)
            textBox.PlaceholderText = Show(inheritedDegrees!.Value);
        StyleTextBox(textBox);
        ApplyHelp(textBox, help);

        double committedValue = optional && degrees <= 0.0 ? 0.0 : degrees;
        void Commit()
        {
            string text = (textBox.Text ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                // Blank means "inherit" on an optional field and "unchanged" on a required one; neither
                // should be read as a slope of zero the user asked for.
                if (!optional)
                {
                    textBox.Text = Show(committedValue);
                    return;
                }

                if (!TerrainCommitGuard.HasMeaningfulNumericChange(committedValue, 0.0))
                    return;

                committedValue = 0.0;
                onChanged(0.0);
                return;
            }

            if (!SlopeInput.TryParseToDegrees(text, unit, out double parsedDegrees))
            {
                // Reverting beats committing a misread: the user sees their entry rejected rather than
                // a silently wrong batter.
                textBox.Text = optional && committedValue <= 0.0
                    ? string.Empty
                    : Show(committedValue);
                return;
            }

            parsedDegrees = Math.Clamp(Math.Abs(parsedDegrees), 0.0, SlopeInput.MaxSlopeDegrees);

            // Echo the canonical text back so "1:3" resolves visibly into the display unit.
            textBox.Text = Show(parsedDegrees);
            if (!TerrainCommitGuard.HasMeaningfulNumericChange(committedValue, parsedDegrees))
                return;

            committedValue = parsedDegrees;
            onChanged(parsedDegrees);
        }

        // Commit on Enter/blur only, like the other free-typed fields: TextChanged fires per keystroke,
        // and "1:3" is not a valid slope until the "3" arrives.
        textBox.KeyDown += (_, e) =>
        {
            if (e.Key != Keys.Enter)
                return;

            Commit();
            e.Handled = true;
        };
        textBox.LostFocus += (_, _) =>
        {
            if (_isRefreshing)
                return;

            Commit();
        };

        return new PropertyRow(CreateHelpLabel(label, help, 0), textBox, expandWidget: true);
    }

    /// <summary>Appends the typed-unit cheat sheet to a slope field's own help text.</summary>
    private static string ComposeSlopeHelp(string help, bool optional)
    {
        string composed = help.TrimEnd();
        if (composed.Length > 0 && !composed.EndsWith('.'))
            composed += ".";

        if (optional)
            composed += " Leave blank to inherit.";

        return composed
            + " "
            + SlopeInput.AcceptedFormatsHelp
            + " The unit shown is set in Terrain Settings › Slope Units.";
    }

    private Control CreateSliderNumericEditor(
        string label,
        double value,
        Action<double> onChanged,
        double softMin,
        double softMax,
        int decimalPlaces = 3,
        double? hardMin = null,
        double? hardMax = null,
        string? help = null,
        string? unitSuffix = null,
        Func<double, string>? formatValue = null,
        Func<string, double?>? parseValue = null,
        bool streamCommits = false)
    {
        help ??= GetNumericHelp(label);
        double committedValue = ClampSliderValue(value, hardMin, hardMax);
        double currentMin = softMin;
        double currentMax = softMax;
        ExpandSliderRange(committedValue, ref currentMin, ref currentMax);
        ClampSliderRangeToHardBounds(ref currentMin, ref currentMax, hardMin, hardMax);

        var slider = new Slider
        {
            MinValue = 0,
            MaxValue = 1000,
            Width = UiMetrics.SliderMin
        };
        var textBox = new TextBox
        {
            Width = UiMetrics.SliderText
        };
        StyleTextBox(textBox);
        ApplyHelp(slider, help);
        ApplyHelp(textBox, help);

        double pendingValue = committedValue;
        bool syncing = false;
        bool sliderEditActive = false;
        bool textEditActive = false;
        // A streaming slider commits at most once per interval while it moves, so the terrain follows the
        // drag. Any other slider commits once the value has rested, because its commit saves the document
        // and relays the panel out. See UiTiming.SliderStreamSeconds.
        var timer = new UITimer
        {
            Interval = streamCommits ? UiTiming.SliderStreamSeconds : UiTiming.SliderCommitSeconds
        };

        void BeginSliderEdit()
        {
            if (sliderEditActive)
                return;

            sliderEditActive = true;
            BeginControllerRefreshDeferral();
        }

        void EndSliderEdit()
        {
            if (!sliderEditActive)
                return;

            sliderEditActive = false;
            EndControllerRefreshDeferral();
        }

        void BeginTextEdit()
        {
            if (textEditActive)
                return;

            textEditActive = true;
            BeginControllerRefreshDeferral();
        }

        void EndTextEdit()
        {
            if (!textEditActive)
                return;

            textEditActive = false;
            EndControllerRefreshDeferral();
        }

        void SyncControls(double numericValue, bool updateTextBox = true)
        {
            numericValue = ClampSliderValue(numericValue, hardMin, hardMax);
            syncing = true;
            ExpandSliderRange(numericValue, ref currentMin, ref currentMax);
            ClampSliderRangeToHardBounds(ref currentMin, ref currentMax, hardMin, hardMax);
            if (updateTextBox)
                textBox.Text = UserNumber.WithUnit(
                    formatValue?.Invoke(numericValue) ?? FormatSliderValue(numericValue, decimalPlaces),
                    unitSuffix);
            slider.Value = ToSliderValue(numericValue, currentMin, currentMax, slider.MaxValue);
            syncing = false;
        }

        void Commit(double numericValue)
        {
            timer.Stop();
            numericValue = ClampSliderValue(numericValue, hardMin, hardMax);
            pendingValue = numericValue;
            SyncControls(numericValue);
            if (!TerrainCommitGuard.HasMeaningfulNumericChange(committedValue, numericValue))
                return;

            committedValue = numericValue;
            onChanged(numericValue);
        }

        void CommitText()
        {
            timer.Stop();
            string text = (textBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(text))
            {
                SyncControls(committedValue);
                return;
            }

            // A transformed slider (slope) parses through its own unit-aware reader; the slider track
            // itself stays in the stored unit, so only the text needs translating.
            double? transformed = parseValue?.Invoke(text);
            if (parseValue != null)
            {
                if (transformed == null)
                {
                    SyncControls(committedValue);
                    return;
                }

                Commit(transformed.Value);
                return;
            }

            if (!UserNumber.TryParseLeading(text, out double numericValue))
            {
                SyncControls(committedValue);
                return;
            }

            Commit(numericValue);
        }

        timer.Elapsed += (_, _) => Commit(pendingValue);
        slider.MouseDown += (_, e) =>
        {
            if (e.Buttons == MouseButtons.Primary)
                BeginSliderEdit();
        };
        slider.MouseUp += (_, e) =>
        {
            if (e.Buttons == MouseButtons.Primary)
            {
                Commit(pendingValue);
                EndSliderEdit();
            }
        };
        slider.LostFocus += (_, _) =>
        {
            Commit(pendingValue);
            EndSliderEdit();
        };
        slider.ValueChanged += (_, _) =>
        {
            if (_isRefreshing || syncing)
                return;

            BeginSliderEdit();
            pendingValue = FromSliderValue(slider.Value, currentMin, currentMax, slider.MaxValue);
            SyncControls(pendingValue, updateTextBox: !textEditActive);
            if (streamCommits)
            {
                // Throttle: leave a running timer alone so it fires mid-drag. Restarting it on every
                // tick is a trailing debounce, under which nothing is committed until the hand stops.
                if (!timer.Started)
                    timer.Start();
                return;
            }

            timer.Stop();
            timer.Start();
        };
        textBox.GotFocus += (_, _) => BeginTextEdit();
        textBox.LostFocus += (_, _) =>
        {
            CommitText();
            EndTextEdit();
        };
        textBox.KeyDown += (_, e) =>
        {
            if (e.Key != Keys.Enter)
                return;

            CommitText();
            EndTextEdit();
            e.Handled = true;
        };
        slider.UnLoad += (_, _) =>
        {
            timer.Stop();
            EndSliderEdit();
            EndTextEdit();
        };
        textBox.UnLoad += (_, _) =>
        {
            timer.Stop();
            EndSliderEdit();
            EndTextEdit();
        };

        SyncControls(committedValue);
        var editor = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = UiMetrics.SpaceMedium,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayoutItem(slider, expand: true),
                textBox
            }
        };
        return new PropertyRow(CreateHelpLabel(label, help, 0), editor, expandWidget: true);
    }

    private static void ExpandSliderRange(double value, ref double min, ref double max)
    {
        min = Math.Min(min, value);
        max = Math.Max(max, value);
        if (max <= min)
            max = min + 1.0;

        if (value > max * 0.98)
            max = NiceNumber(Math.Max(value, max));
        if (value < min * 1.02 && value > 0)
            min = Math.Min(min, NiceNumber(value * 0.5));
    }

    private static void ClampSliderRangeToHardBounds(ref double min, ref double max, double? hardMin, double? hardMax)
    {
        if (hardMin.HasValue)
            min = Math.Max(min, hardMin.Value);
        if (hardMax.HasValue)
            max = Math.Min(max, hardMax.Value);
        if (max <= min)
            max = min + 1.0;
    }

    private static int ToSliderValue(double value, double min, double max, int sliderMax)
    {
        if (max <= min)
            return 0;

        double t = Math.Clamp((value - min) / (max - min), 0.0, 1.0);
        return (int)Math.Round(t * sliderMax);
    }

    private static double FromSliderValue(int sliderValue, double min, double max, int sliderMax)
    {
        if (sliderMax <= 0 || max <= min)
            return min;

        double t = sliderValue / (double)sliderMax;
        return min + (max - min) * t;
    }

    private static string FormatSliderValue(double value, int decimalPlaces)
    {
        return decimalPlaces == 0
            ? UserNumber.Format(Math.Round(value), "0")
            : UserNumber.Format(value, $"F{decimalPlaces}");
    }

    private static double ClampSliderValue(double value, double? hardMin, double? hardMax)
    {
        if (hardMin.HasValue)
            value = Math.Max(hardMin.Value, value);
        if (hardMax.HasValue)
            value = Math.Min(hardMax.Value, value);
        return value;
    }

    private static double NiceNumber(double value)
    {
        if (value <= 0)
            return 1.0;

        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        foreach (double factor in new[] { 1.0, 2.0, 5.0, 10.0 })
        {
            double candidate = magnitude * factor;
            if (candidate >= value)
                return candidate;
        }

        return magnitude * 10.0;
    }

    private Control CreateReadOnlyValueRow(string label, string value, string help)
    {
        var valueLabel = UiControls.Label(value, UiLabelRole.Input, WrapMode.Word);

        return new PropertyRow(CreateHelpLabel(label, help, 0), valueLabel, expandWidget: true);
    }

    private Control CreateSelectableSummaryEditor(string label, string value, string help, int minHeight = 110)
    {
        var textArea = new TextArea
        {
            Text = value,
            ReadOnly = true,
            Wrap = true,
            Height = minHeight
        };
        StyleTextArea(textArea);
        ApplyHelp(textArea, help);

        return new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = UiMetrics.SpaceSmall,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                CreateHelpLabel(label, help, 0),
                new StackLayoutItem(textArea, HorizontalAlignment.Stretch)
            }
        };
    }

    private Control CreateCheckEditor(string label, bool value, Action<bool> onChanged, string help)
    {
        var checkBox = new CheckBox { Checked = value };
        ApplyHelp(checkBox, help);
        checkBox.CheckedChanged += (_, _) => onChanged(checkBox.Checked == true);

        return new PropertyRow(CreateHelpLabel(label, help, 0), checkBox);
    }

    private Control CreateDropDownEditor(
        string label,
        IReadOnlyList<(string Key, string Label)> options,
        string selectedKey,
        Action<string> onChanged,
        string help)
    {
        // No fixed width: it made a dropdown row line up with nothing else on the card and truncated its
        // own text ("Auto (fast fo..."). It now fills the widget column like every other editor.
        var dropDown = new DropDown();
        foreach (var option in options)
            dropDown.Items.Add(new ListItem { Text = option.Label });

        int selectedIndex = options
            .Select((option, index) => (option, index))
            .FirstOrDefault(item => string.Equals(item.option.Key, selectedKey, StringComparison.OrdinalIgnoreCase))
            .index;
        dropDown.SelectedIndex = selectedIndex >= 0 && selectedIndex < options.Count ? selectedIndex : 0;
        ApplyHelp(dropDown, help);
        dropDown.SelectedIndexChanged += (_, _) =>
        {
            if (_isRefreshing)
                return;

            int index = dropDown.SelectedIndex;
            if (index < 0 || index >= options.Count)
                return;

            onChanged(options[index].Key);
        };

        return new PropertyRow(CreateHelpLabel(label, help, 0), dropDown, expandWidget: true);
    }

    /// <summary>A choice between two or three modes as side-by-side segments; see <see cref="SegmentedControl"/>.</summary>
    private Control CreateSegmentedEditor(
        string label,
        IReadOnlyList<(string Key, string Label)> options,
        string selectedKey,
        Action<string> onChanged,
        string help,
        IReadOnlyDictionary<string, string>? optionHelp = null,
        bool fullRow = false)
    {
        int selectedIndex = Math.Max(0, options
            .Select((option, index) => (option, index))
            .Where(item => string.Equals(item.option.Key, selectedKey, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.index)
            .DefaultIfEmpty(0)
            .First());
        var segments = new SegmentedControl(
            options.Select(option => option.Label).ToList(),
            selectedIndex,
            options.Select(option => optionHelp != null && optionHelp.TryGetValue(option.Key, out var text) ? text : null).ToList())
        {
            ToolTip = string.IsNullOrWhiteSpace(help)
                ? string.Join(" · ", options.Select(option => option.Label))
                : help
        };
        segments.SelectedIndexChanged += (_, _) =>
        {
            if (!_isRefreshing)
                onChanged(options[segments.SelectedIndex].Key);
        };

        // A card's primary mode spans the whole row, unlabelled: its segments are the heading of the card.
        if (fullRow)
            return new Panel { Padding = new Eto.Drawing.Padding(0, UiMetrics.PropertyRowVerticalPadding), Content = segments };

        return new PropertyRow(CreateHelpLabel(label, help, 0), segments, expandWidget: true);
    }

    private Control CreateCommittedTextEditor(
        string label,
        string value,
        Action<string> onCommit,
        string help,
        bool trim = true)
    {
        var textBox = new TextBox { Text = value };
        StyleTextBox(textBox);
        ApplyHelp(textBox, help);
        BindCommittedText(textBox, () => value, onCommit, trim: trim);

        return new PropertyRow(CreateHelpLabel(label, help, 0), textBox, expandWidget: true);
    }

    /// <summary>A file's full path, typed or picked with Browse; committed like any text row.</summary>
    private Control CreateFilePathEditor(
        string label,
        string value,
        Action<string> onCommit,
        string help,
        string filterName,
        IReadOnlyList<string> extensions)
    {
        var textBox = new TextBox { Text = value };
        StyleTextBox(textBox);
        ApplyHelp(textBox, help);
        BindCommittedText(textBox, () => value, onCommit);

        Button browse = MakeInlineButton("Browse…", (_, _) =>
        {
            var dialog = new OpenFileDialog { Title = label, MultiSelect = false };
            if (extensions.Count > 0)
                dialog.Filters.Add(new FileFilter(filterName, extensions.ToArray()));
            if (!string.IsNullOrWhiteSpace(value) && File.Exists(value))
                dialog.FileName = value;
            if (dialog.ShowDialog(this) != DialogResult.Ok || string.IsNullOrWhiteSpace(dialog.FileName))
                return;
            if (!string.Equals(dialog.FileName, value, StringComparison.Ordinal))
                onCommit(dialog.FileName);
        }, "Choose the file.");

        var widget = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = UiMetrics.SpaceSmall,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items = { new StackLayoutItem(textBox, expand: true), browse }
        };
        return new PropertyRow(CreateHelpLabel(label, help, 0), widget, expandWidget: true);
    }

    private void BindCommittedText(TextBox textBox, Func<string> getCurrentValue, Action<string> onCommit, bool trim = true)
    {
        void Commit()
        {
            if (_isRefreshing)
                return;

            string text = textBox.Text ?? string.Empty;
            if (trim)
                text = text.Trim();

            if (string.Equals(text, getCurrentValue(), StringComparison.Ordinal))
                return;

            onCommit(text);
        }

        textBox.LostFocus += (_, _) => Commit();
        textBox.KeyDown += (_, e) =>
        {
            if (e.Key == Keys.Enter)
            {
                Commit();
                e.Handled = true;
            }
        };
    }

    private static string GetNumericHelp(string label)
    {
        return label switch
        {
            "Detail Size" => "Changes are applied after a short pause. Smaller values keep more terrain detail; larger values simplify and merge nearby geometry more aggressively.",
            "Edge Length" => "Changes are applied after a short pause. Smaller values make denser triangles; larger values make coarser meshes.",
            "Max Area" => "Changes are applied after a short pause. Smaller values refine the mesh; larger values keep bigger faces.",
            "Min Angle" => "Changes are applied after a short pause. Around 20-30 is moderate; very high values can overconstrain the triangulation.",
            "Iterations" => "Changes are applied after a short pause. 1 is light smoothing; 2-4 is moderate; higher values flatten more detail.",
            "Strength" => "Changes are applied after a short pause. Around 0.2 is gentle, 0.5 is strong, and 1.0 is extreme.",
            "Protect Hold" => "Changes are applied after a short pause. 1.0 keeps protected curves exactly as they are, 0.5 lets them soften, 0.0 smooths them like any other vertex.",
            "Max Distance" => "Changes are applied after a short pause. 0 means unlimited; smaller values stop the batter sooner.",
            "Width" => "Changes are applied after a short pause. This is the controlled path width before side grading starts.",
            "Symbol Scale" => "Changes are applied after a short pause. 1.0 is default size; below 1.0 is smaller; above 1.0 is larger.",
            _ => $"{label}. Changes are applied after a short pause."
        };
    }
}
