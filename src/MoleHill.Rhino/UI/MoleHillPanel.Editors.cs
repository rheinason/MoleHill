using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Eto.Drawing;
using Eto.Forms;
using MoleHill.Core.Analysis;
using MoleHill.Core.Grading;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.UI;
using RhinoObjectType = Rhino.DocObjects.ObjectType;
using RhinoPoint3d = Rhino.Geometry.Point3d;
using RhinoGetPoint = Rhino.Input.Custom.GetPoint;
using RhinoGetResult = Rhino.Input.GetResult;

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
        string? help = null)
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
            });
        };

        var objectsRow = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = liveObjectIds.Count > 0 ? 0 : 4,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items = { new StackLayoutItem(objectsPill, expand: true) }
        };
        if (liveObjectIds.Count > 0)
        {
            objectsRow.Items.Add(MakeMiniIconButton(PanelButtonIcon.Clear, (_, _) =>
                    mutateSourceSet(set => set.ReplaceObjects(Array.Empty<Guid>())),
                "Clear all referenced objects.", width: 28));
        }

        // ── Layers pill ───────────────────────────────────────────
        int lc = sourceSet.LayerPaths.Count;
        string layText = lc == 0 ? "Layers" : $"{lc} Layers";
        var layersPill = MakePillButton(layText, lc == 0 ? "Manage input layers." : $"Manage input layers. {lc} assigned.");
        layersPill.Click += (_, _) => ShowLayerSourcePopover(
            layersPill,
            sourceSet.LayerPaths,
            path => mutateSourceSet(set => set.AddLayer(path)),
            path => mutateSourceSet(set => set.RemoveLayer(path)));

        var layersClear = MakeMiniIconButton(PanelButtonIcon.Clear, (_, _) =>
            mutateSourceSet(set => set.ReplaceLayers(Array.Empty<string>())),
            "Clear all assigned layers.", width: 28);
        var layersRow = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = lc > 0 ? 0 : 4,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items = { new StackLayoutItem(layersPill, expand: true) }
        };
        if (lc > 0)
            layersRow.Items.Add(layersClear);

        // ── Layout ────────────────────────────────────────────────
        var titleLabel = new Label { Text = label, VerticalAlignment = VerticalAlignment.Center };
        ApplyHelp(titleLabel, help ?? $"{label} accepts Rhino object picks and layers.");

        var pillsLayout = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayout
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    Items = { new StackLayoutItem(objectsRow, expand: true) }
                },
                new StackLayout
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    Items = { new StackLayoutItem(layersRow, expand: true) }
                }
            }
        };

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
        double? step = null)
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
        var timer = new UITimer { Interval = 0.25 };
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
        stepper.UnLoad += (_, _) => timer.Stop();
        return new PropertyRow(CreateHelpLabel(label, help, 0), stepper);
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
        string? help = null)
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

        return new PropertyRow(CreateHelpLabel(label, help, 0), textBox);
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
        string? help = null)
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
        var timer = new UITimer { Interval = 0.12 };

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
                textBox.Text = FormatSliderValue(numericValue, decimalPlaces);
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
                EndSliderEdit();
        };
        slider.LostFocus += (_, _) => EndSliderEdit();
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
        slider.UnLoad += (_, _) => timer.Stop();
        textBox.UnLoad += (_, _) => timer.Stop();

        SyncControls(committedValue);
        var editor = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
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
        var valueLabel = new Label
        {
            Text = value,
            VerticalAlignment = VerticalAlignment.Center,
            TextColor = UiTheme.InputText,
            Wrap = WrapMode.Word
        };

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
            Spacing = 4,
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
        var dropDown = new DropDown { Width = UiMetrics.DropDown };
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

        return new PropertyRow(CreateHelpLabel(label, help, 0), dropDown);
    }

    private static List<(string Key, string Label)> GetValueFormatOptions(string selectedFormat) =>
        AnalysisFormatting.GetValueFormatOptions(selectedFormat);

    private Control CreateValueFormatDropDown(string selectedFormat, Action<string> onChanged, string help)
    {
        var options = GetValueFormatOptions(selectedFormat);
        var dropDown = new DropDown
        {
            Width = UiMetrics.DropDown
        };
        foreach (var option in options)
            dropDown.Items.Add(new ListItem { Text = option.Label });

        int selectedIndex = options
            .Select((option, index) => (option, index))
            .FirstOrDefault(item => string.Equals(item.option.Key, selectedFormat, StringComparison.OrdinalIgnoreCase))
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

        return dropDown;
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

    private static string GetAnalysisOutputColorText(TerrainDefinition terrain, string? outputLayerPath) =>
        AnalysisFormatting.GetAnalysisOutputColorText(terrain, outputLayerPath);
}
