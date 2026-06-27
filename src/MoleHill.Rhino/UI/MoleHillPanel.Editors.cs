using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Eto.Drawing;
using Eto.Forms;
using MoleHill.Core.Analysis;
using MoleHill.Core.Grading;
using MoleHill.Rhino.Model;
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
            ? "Add Objects"
            : $"{liveObjectIds.Count} Object{(liveObjectIds.Count == 1 ? "" : "s")}";
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
            objectsRow.Items.Add(MakeMiniButton("X", (_, _) =>
                    mutateSourceSet(set => set.ReplaceObjects(Array.Empty<Guid>())),
                "Clear all referenced objects.", width: 28));
        }

        // ── Layers pill ───────────────────────────────────────────
        int lc = sourceSet.LayerPaths.Count;
        string layText = lc == 0 ? "Assign Layers" : $"Layers ({lc})";
        var layersPill = MakePillButton(layText, "Manage input layers.");
        layersPill.Click += (_, _) => ShowLayerSourcePopover(
            layersPill,
            sourceSet.LayerPaths,
            path => mutateSourceSet(set => set.AddLayer(path)),
            path => mutateSourceSet(set => set.RemoveLayer(path)));

        var layersClear = MakeMiniButton("Clear", (_, _) =>
            mutateSourceSet(set => set.ReplaceLayers(Array.Empty<string>())),
            "Clear all assigned layers.", width: 48);
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
        if (!UseStackedSourceEditors())
            titleLabel.Width = PropertyLabelWidth;
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

        if (UseStackedSourceEditors())
        {
            return new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                Padding = new Padding(0, 3),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    titleLabel,
                    new StackLayoutItem(pillsLayout, expand: true)
                }
            };
        }

        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Padding(0, 3),
            Items = { titleLabel, new StackLayoutItem(pillsLayout, expand: true) }
        };
    }

    private Control CreateNumericEditor(
        string label,
        double value,
        Action<double> onChanged,
        int decimalPlaces = 3,
        string? help = null,
        double? minValue = 0,
        double? maxValue = null,
        bool liveEdit = false)
    {
        help ??= GetNumericHelp(label);
        var stepper = new NumericStepper
        {
            Value = value,
            DecimalPlaces = decimalPlaces,
            Increment = decimalPlaces == 0 ? 1 : 0.1,
            Width = 100
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
            timer.Stop();
            timer.Start();
        };
        stepper.LostFocus += (_, _) =>
        {
            if (_isRefreshing)
                return;

            timer.Stop();
            Commit(stepper.Value);
            EndEdit();
        };
        if (UseStackedFormRows())
        {
            return new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                Padding = new Padding(0, 3),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    CreateHelpLabel(label, help, 0),
                    stepper
                }
            };
        }

        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Items =
            {
                CreateHelpLabel(label, help, NumericLabelWidth),
                stepper
            }
        };
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
            Width = 100,
            Text = value > 0.0 ? value.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture) : string.Empty,
            PlaceholderText = inheritedValue.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture)
        };
        ApplyHelp(textBox, help);

        var timer = new UITimer { Interval = 0.25 };
        double committedValue = value > 0.0 ? value : 0.0;
        void Commit()
        {
            timer.Stop();
            double parsed = 0.0;
            string text = textBox.Text?.Trim() ?? string.Empty;
            if (text.Length > 0 &&
                double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out double v) &&
                v > 0.0)
            {
                parsed = v;
            }

            if (!TerrainCommitGuard.HasMeaningfulNumericChange(committedValue, parsed))
                return;

            committedValue = parsed;
            onChanged(parsed);
        }
        timer.Elapsed += (_, _) => Commit();
        textBox.TextChanged += (_, _) =>
        {
            if (_isRefreshing)
                return;

            timer.Stop();
            timer.Start();
        };
        textBox.LostFocus += (_, _) =>
        {
            if (_isRefreshing)
                return;

            Commit();
        };

        if (UseStackedFormRows())
        {
            return new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                Padding = new Padding(0, 3),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items = { CreateHelpLabel(label, help, 0), textBox }
            };
        }

        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Items = { CreateHelpLabel(label, help, NumericLabelWidth), textBox }
        };
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
            MaxValue = 1000
        };
        if (!UseStackedFormRows())
            slider.Width = 140;
        var textBox = new TextBox
        {
            Width = 88
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

            if (!TryParseSliderNumericValue(text, out double numericValue))
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

        SyncControls(committedValue);
        if (UseStackedFormRows())
        {
            var compactEditor = new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    new StackLayoutItem(slider, HorizontalAlignment.Stretch),
                    new StackLayout
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        VerticalContentAlignment = VerticalAlignment.Center,
                        Items =
                        {
                            textBox,
                            new StackLayoutItem(new Panel(), expand: true)
                        }
                    }
                }
            };

            return new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                Padding = new Padding(0, 3),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    CreateHelpLabel(label, help, 0),
                    new StackLayoutItem(compactEditor, HorizontalAlignment.Stretch)
                }
            };
        }

        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items =
            {
                CreateHelpLabel(label, help, NumericLabelWidth),
                slider,
                textBox
            }
        };
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

    private static bool TryParseSliderNumericValue(string text, out double value)
    {
        // No AllowThousands: the slider formats with InvariantCulture (e.g. "1.000"), and in locales where
        // '.' is the group separator (de-DE, nb-NO, …) AllowThousands would re-read that as 1000. Without
        // it, a '.' is only ever a decimal point — CurrentCulture parse fails for "1.000" in those locales
        // and the InvariantCulture fallback yields 1.0, while locale decimals (e.g. "1,5") still parse.
        const NumberStyles Styles = NumberStyles.Float;
        return double.TryParse(text, Styles, CultureInfo.CurrentCulture, out value) ||
               double.TryParse(text, Styles, CultureInfo.InvariantCulture, out value);
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

        if (UseStackedFormRows())
        {
            return new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                Padding = new Padding(0, 3),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    CreateHelpLabel(label, help, 0),
                    valueLabel
                }
            };
        }

        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Items =
            {
                CreateHelpLabel(label, help, NumericLabelWidth),
                valueLabel
            }
        };
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
                CreateHelpLabel(label, help, NumericLabelWidth),
                new StackLayoutItem(textArea, HorizontalAlignment.Stretch)
            }
        };
    }

    private Control CreateCheckEditor(string label, bool value, Action<bool> onChanged, string help)
    {
        var checkBox = new CheckBox { Checked = value };
        ApplyHelp(checkBox, help);
        checkBox.CheckedChanged += (_, _) => onChanged(checkBox.Checked == true);

        if (UseStackedFormRows())
        {
            return new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                Padding = new Padding(0, 3),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    CreateHelpLabel(label, help, 0),
                    checkBox
                }
            };
        }

        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Items =
            {
                CreateHelpLabel(label, help, NumericLabelWidth),
                checkBox
            }
        };
    }

    private Control CreateDropDownEditor(
        string label,
        IReadOnlyList<(string Key, string Label)> options,
        string selectedKey,
        Action<string> onChanged,
        string help)
    {
        var dropDown = new DropDown();
        if (!UseStackedFormRows())
            dropDown.Width = 160;
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

        if (UseStackedFormRows())
        {
            return new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                Padding = new Padding(0, 3),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    CreateHelpLabel(label, help, 0),
                    dropDown
                }
            };
        }

        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Items =
            {
                CreateHelpLabel(label, help, NumericLabelWidth),
                dropDown
            }
        };
    }

    private DropDown CreateValueFormatDropDown(string selectedFormat, Action<string> onChanged, string help)
    {
        var options = GetValueFormatOptions(selectedFormat);
        var dropDown = new DropDown();
        if (!UseStackedFormRows())
            dropDown.Width = 160;

        foreach (var option in options)
            dropDown.Items.Add(new ListItem { Text = option.Label });

        int selectedIndex = options.FindIndex(option => string.Equals(option.Key, selectedFormat, StringComparison.OrdinalIgnoreCase));
        dropDown.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
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

    private Control CreateValueFormatEditor(
        string label,
        string selectedFormat,
        Action<string> onChanged,
        string help)
    {
        var dropDown = CreateValueFormatDropDown(selectedFormat, onChanged, help);

        if (UseStackedFormRows())
        {
            return new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                Padding = new Padding(0, 3),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    CreateHelpLabel(label, help, 0),
                    dropDown
                }
            };
        }

        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Items =
            {
                CreateHelpLabel(label, help, NumericLabelWidth),
                dropDown
            }
        };
    }

    private static List<(string Key, string Label)> GetValueFormatOptions(string selectedFormat)
    {
        var options = new List<(string Key, string Label)>
        {
            ("F0", "Whole number"),
            ("F1", "1 decimal place"),
            ("F2", "2 decimal places"),
            ("F3", "3 decimal places"),
            ("G4", "Compact")
        };

        if (!string.IsNullOrWhiteSpace(selectedFormat) &&
            !options.Any(option => string.Equals(option.Key, selectedFormat, StringComparison.OrdinalIgnoreCase)))
        {
            options.Add((selectedFormat, $"Custom ({selectedFormat})"));
        }

        return options;
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

        if (UseStackedFormRows())
        {
            return new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                Padding = new Padding(0, 3),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    CreateHelpLabel(label, help, 0),
                    textBox
                }
            };
        }

        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Items =
            {
                CreateHelpLabel(label, help, NumericLabelWidth),
                new StackLayoutItem(textBox, expand: true)
            }
        };
    }

    private Control CreateSlopeUnitDropDown(
        SlopeAnalyzer.SlopeUnit unit,
        Action<SlopeAnalyzer.SlopeUnit> onChanged,
        string help)
    {
        var options = new[]
        {
            (GetSlopeUnitKey(SlopeAnalyzer.SlopeUnit.Percent), "Percent"),
            (GetSlopeUnitKey(SlopeAnalyzer.SlopeUnit.Promille), "Promille"),
            (GetSlopeUnitKey(SlopeAnalyzer.SlopeUnit.Ratio), "Ratio"),
            (GetSlopeUnitKey(SlopeAnalyzer.SlopeUnit.Degrees), "Degrees")
        };

        return CreateDropDownEditor(
            "Units",
            options,
            GetSlopeUnitKey(unit),
            value => onChanged(ParseSlopeUnit(value)),
            help);
    }

    private void AddBlockAttributeAnalysisRows<TAnalysis>(
        DynamicLayout layout,
        TerrainDefinition terrain,
        TAnalysis analysis,
        RhinoObjectType objectFilter,
        Action<Action<TAnalysis>> mutate,
        string sourceHelp,
        string formatHelp,
        Action<DynamicLayout>? extraRows = null)
        where TAnalysis : BlockAttributeAnalysisDefinition
    {
        layout.AddRow(CreateSourceEditor(
            "Sources",
            analysis.Sources,
            apply => mutate(item => apply(item.Sources)),
            objectFilter,
            doc => _controller.GetSelectedLayerPaths(doc),
            sourceHelp));
        extraRows?.Invoke(layout);
        layout.AddRow(CreateValueFormatEditor(
            "Decimals",
            analysis.ValueFormat,
            format => mutate(item => item.ValueFormat = format),
            formatHelp));
        layout.AddRow(CreateCommittedTextEditor(
            "Prefix",
            analysis.AttributePrefix,
            text => mutate(item => item.AttributePrefix = text),
            "Text prepended to the formatted value when filling the DISPLAY block attribute.",
            trim: false));
        layout.AddRow(CreateCommittedTextEditor(
            "Suffix",
            analysis.AttributeSuffix,
            text => mutate(item => item.AttributeSuffix = text),
            "Text appended after the formatted value and unit when filling the DISPLAY block attribute.",
            trim: false));
        layout.AddRow(CreateNumericEditor(
            "Block Scale",
            analysis.BlockScale,
            value => mutate(item => item.BlockScale = value),
            help: "Scale factor for inserted annotation blocks.",
            minValue: 0.01));
        layout.AddRow(CreateLayerAssignmentEditor(
            "Output Layer",
            analysis.OutputLayerPath,
            path => mutate(item => item.OutputLayerPath = path),
            "Layer used for generated annotation instances. Leave empty to use the terrain auxiliary layer."));
        layout.AddRow(CreateOptionalColorEditor(
            "Color",
            analysis.ColorArgb,
            value => mutate(item => item.ColorArgb = value),
            "Explicit display and bake color for generated annotation blocks. Clear to use the output layer color.",
            ResolveLayerColorArgb(analysis.OutputLayerPath ?? terrain.AnnotationLayerPath),
            GetAnalysisOutputColorText(terrain, analysis.OutputLayerPath)));
    }

    private static string GetAnalysisOutputColorText(TerrainDefinition terrain, string? outputLayerPath)
    {
        if (string.IsNullOrWhiteSpace(outputLayerPath))
        {
            return string.IsNullOrWhiteSpace(terrain.AnnotationLayerPath)
                ? $"By Layer ({TerrainDefinition.DefaultAnnotationLayerPath})"
                : $"By Layer ({GetLeafLayerName(terrain.AnnotationLayerPath!)})";
        }

        return $"By Layer ({GetLeafLayerName(outputLayerPath)})";
    }
}
