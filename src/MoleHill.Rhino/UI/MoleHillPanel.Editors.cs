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
        string layText = lc == 0 ? "Layers" : $"{lc} Layers";
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
        var titleLabel = UiControls.Label(label);
        ApplyHelp(titleLabel, help ?? $"{label} accepts Rhino object picks and layers.");

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
        var stepper = new NumericStepper
        {
            Value = value,
            DecimalPlaces = decimalPlaces,
            Increment = step ?? (decimalPlaces == 0 ? 1 : 0.1),
            Width = UiMetrics.NumericField
        };
        if (minValue.HasValue)
            stepper.MinValue = minValue.Value;
        if (maxValue.HasValue)
            stepper.MaxValue = maxValue.Value;
        ApplyHelp(stepper, help);
        var timer = new UITimer { Interval = UiTiming.NumericCommitSeconds };
        double committedValue = value;
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
            stepper.GotFocus += (_, _) => BeginEdit();
        timer.Elapsed += (_, _) => Commit(stepper.Value);
        stepper.ValueChanged += (_, _) =>
        {
            if (_isRefreshing)
                return;

            BeginEdit();
            // Only liveEdit fields debounce-commit while typing; plain fields commit on Enter/blur only
            // (NumericStepper.ValueChanged fires per keystroke, so debouncing here would commit garbage
            // intermediate values, e.g. "1" while the user is still typing "125").
            if (liveEdit)
            {
                timer.Stop();
                timer.Start();
            }
        };
        stepper.KeyDown += (_, e) =>
        {
            if (e.Key != Keys.Enter)
                return;

            timer.Stop();
            Commit(stepper.Value);
            e.Handled = true;
        };
        stepper.LostFocus += (_, _) =>
        {
            if (_isRefreshing)
                return;

            timer.Stop();
            Commit(stepper.Value);
            EndEdit();
        };
        stepper.UnLoad += (_, _) =>
        {
            timer.Stop();
            EndEdit();
        };
        return new PropertyRow(CreateHelpLabel(label, help, 0), WithUnitSuffix(stepper, unitSuffix));
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
        var textBox = new TextBox
        {
            Width = UiMetrics.NumericField,
            Text = value > 0.0 ? FormatUserNumber(value, decimalPlaces) : string.Empty,
            PlaceholderText = FormatUserNumber(inheritedValue, decimalPlaces)
        };
        ApplyHelp(textBox, help);

        double committedValue = value > 0.0 ? value : 0.0;
        void Commit()
        {
            double parsed = 0.0;
            string text = textBox.Text?.Trim() ?? string.Empty;
            if (text.Length > 0 && TryParseUserNumber(text, out double v) && v > 0.0)
            {
                parsed = v;
            }

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

        return new PropertyRow(CreateHelpLabel(label, help, 0), WithUnitSuffix(textBox, unitSuffix));
    }

    /// <summary>
    /// Pairs an input widget with the dim trailing label that says what its number is in. Without one a
    /// card row is a bare number: "Fill Slope 3" reads as degrees, percent or 1:3 with nothing on screen
    /// to settle it. Passing no suffix returns the widget untouched, so unitless rows cost nothing.
    /// </summary>
    private static Control WithUnitSuffix(Control widget, string? unitSuffix, bool expandWidget = false)
    {
        if (string.IsNullOrEmpty(unitSuffix))
            return widget;

        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = UiMetrics.SpaceSmall,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayoutItem(widget, expand: expandWidget),
                UiControls.Label(unitSuffix, UiLabelRole.Meta)
            }
        };
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

        var textBox = new TextBox
        {
            Width = UiMetrics.NumericField,
            Text = optional && degrees <= 0.0 ? string.Empty : SlopeInput.FormatDegreesAsUnit(degrees, unit)
        };
        if (optional)
            textBox.PlaceholderText = SlopeInput.FormatDegreesAsUnit(inheritedDegrees!.Value, unit);
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
                    textBox.Text = SlopeInput.FormatDegreesAsUnit(committedValue, unit);
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
                    : SlopeInput.FormatDegreesAsUnit(committedValue, unit);
                return;
            }

            parsedDegrees = Math.Clamp(Math.Abs(parsedDegrees), 0.0, SlopeInput.MaxSlopeDegrees);

            // Echo the canonical text back so "1:3" resolves visibly into the display unit.
            textBox.Text = SlopeInput.FormatDegreesAsUnit(parsedDegrees, unit);
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

        return new PropertyRow(
            CreateHelpLabel(label, help, 0),
            WithUnitSuffix(textBox, SlopeInput.Suffix(unit)));
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
        Func<string, double?>? parseValue = null)
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
        var timer = new UITimer { Interval = UiTiming.SliderCommitSeconds };

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
                textBox.Text = formatValue?.Invoke(numericValue) ?? FormatSliderValue(numericValue, decimalPlaces);
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

            if (!TryParseUserNumber(text, out double numericValue))
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
        return new PropertyRow(
            CreateHelpLabel(label, help, 0),
            WithUnitSuffix(editor, unitSuffix, expandWidget: true),
            expandWidget: true);
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
            ? ((int)Math.Round(value)).ToString(CultureInfo.InvariantCulture)
            : value.ToString($"F{decimalPlaces}", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Shared numeric parse for free-typed text fields (slider text box, optional numeric editor, …).
    /// No AllowThousands: text may have been formatted with InvariantCulture (e.g. "1.000"), and in locales
    /// where '.' is the group separator (de-DE, nb-NO, …) AllowThousands would re-read that as 1000. Without
    /// it, a '.' is only ever a decimal point — CurrentCulture parse fails for "1.000" in those locales
    /// and the InvariantCulture fallback yields 1.0, while locale decimals (e.g. "1,5") still parse.
    /// </summary>
    private static bool TryParseUserNumber(string text, out double value)
    {
        const NumberStyles Styles = NumberStyles.Float;
        return double.TryParse(text, Styles, CultureInfo.CurrentCulture, out value) ||
               double.TryParse(text, Styles, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Shared numeric format for free-typed text fields — pairs with <see cref="TryParseUserNumber"/>.</summary>
    private static string FormatUserNumber(double value, int decimalPlaces)
    {
        string format = decimalPlaces <= 0 ? "0" : "0." + new string('#', decimalPlaces);
        return value.ToString(format, CultureInfo.CurrentCulture);
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

}
