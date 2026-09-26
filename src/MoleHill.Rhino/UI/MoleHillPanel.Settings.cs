using System.Globalization;
using Eto.Drawing;
using Eto.Forms;
using MoleHill.Core.Analysis;
using MoleHill.Rhino.Model;
using MoleHill.Rhino.Registry;
using MoleHill.Rhino.Services;
using Rhino;
using Rhino.UI;

namespace MoleHill.Rhino.UI;

// Terrain Settings card: colour and opacity, wires and slow-build warning, preview line weight, detail
// size, slope units, bake tracking and the output layer template.
public sealed partial class MoleHillPanel : Panel
{
    /// <summary>The Terrain Settings card's controls: tolerance, opacity and colour, preview line weight, and bake tracking.</summary>
    private void WireSettingsControls()
    {
        _toleranceStepper.DecimalPlaces = 3;
        _toleranceStepper.Increment = 0.1;
        _toleranceStepper.MinValue = 0;
        ApplyHelp(_toleranceStepper, "Smallest terrain detail to preserve automatically. Smaller values keep more detail; larger values simplify and merge nearby geometry more aggressively.");
        void CommitTolerance()
        {
            if (_isRefreshing)
                return;

            var doc = RhinoDoc.ActiveDoc;
            var terrain = doc == null ? null : _controller.GetSelectedTerrain(doc);
            if (terrain == null)
                return;

            double value = _toleranceStepper.Value;
            if (!TerrainCommitGuard.HasMeaningfulNumericChange(terrain.GlobalTolerance, value))
                return;

            MutateSelectedTerrain(t => t.GlobalTolerance = value, scheduleRebuild: true);
        }

        var toleranceTimer = new UITimer { Interval = UiTiming.ToleranceCommitSeconds };
        toleranceTimer.Elapsed += (_, _) =>
        {
            toleranceTimer.Stop();
            CommitTolerance();
        };
        _toleranceStepper.ValueChanged += (_, _) =>
        {
            if (_isRefreshing) return;
            toleranceTimer.Stop();
            toleranceTimer.Start();
        };
        _toleranceStepper.LostFocus += (_, _) =>
        {
            toleranceTimer.Stop();
            CommitTolerance();
        };

        _terrainOpacityStepper.DecimalPlaces = 0;
        _terrainOpacityStepper.Increment = 5;
        _terrainOpacityStepper.MinValue = 0;
        _terrainOpacityStepper.MaxValue = 100;
        ApplyHelp(_terrainOpacityStepper, "Terrain opacity used for the preview and the baked terrain mesh.");
        ApplyHelp(_terrainOpacitySlider, "Drag to adjust terrain opacity for preview and bake.");
        ApplyHelp(_terrainColorSwatch, "Click to pick the terrain display color.");
        var (beginStepperDeferral, endStepperDeferral) = CreatePairedRefreshDeferral();
        _terrainOpacityStepper.GotFocus += (_, _) => beginStepperDeferral();
        _terrainColorSwatch.MouseDown += (_, e) =>
        {
            if (e.Buttons == MouseButtons.Primary)
                OnPickTerrainColor(_, EventArgs.Empty);
        };
        _terrainOpacityStepper.ValueChanged += (_, _) =>
        {
            if (_isRefreshing || _isUpdatingOpacityControls)
                return;

            int opacityPercent = (int)Math.Round(_terrainOpacityStepper.Value);
            SetTerrainOpacityControls(opacityPercent);
            ApplyTerrainOpacity(opacityPercent);
        };
        _terrainOpacityStepper.LostFocus += (_, _) =>
        {
            if (!_isRefreshing)
            {
                int opacityPercent = (int)Math.Round(_terrainOpacityStepper.Value);
                SetTerrainOpacityControls(opacityPercent);
                ApplyTerrainOpacity(opacityPercent);
            }
            endStepperDeferral();
        };
        _terrainOpacityStepper.KeyDown += (_, e) =>
        {
            if (e.Key != Keys.Enter || _isRefreshing)
                return;

            int opacityPercent = (int)Math.Round(_terrainOpacityStepper.Value);
            SetTerrainOpacityControls(opacityPercent);
            ApplyTerrainOpacity(opacityPercent);
            endStepperDeferral();
            e.Handled = true;
        };
        var (beginOpacitySliderDeferral, endOpacitySliderDeferral) = CreatePairedRefreshDeferral();
        _terrainOpacitySlider.MouseDown += (_, e) =>
        {
            if (e.Buttons == MouseButtons.Primary)
                beginOpacitySliderDeferral();
        };
        _terrainOpacitySlider.MouseUp += (_, e) =>
        {
            if (e.Buttons == MouseButtons.Primary)
                endOpacitySliderDeferral();
        };
        _terrainOpacitySlider.LostFocus += (_, _) => endOpacitySliderDeferral();
        _terrainOpacitySlider.ValueChanged += (_, _) =>
        {
            if (_isRefreshing || _isUpdatingOpacityControls)
                return;

            int opacityPercent = _terrainOpacitySlider.Value;
            SetTerrainOpacityControls(opacityPercent);
            ApplyTerrainOpacity(opacityPercent);
        };
        ApplyHelp(
            _previewLineWeightSlider,
            "Thickness of every line this terrain previews - contours, waterflow, sections, annotation. " +
            "Relative to each output's drawing weight, so the hierarchy is preserved. Display only: baked " +
            "geometry keeps the print width of its layer.");
        _previewLineWeightSlider.ValueChanged += (_, _) =>
        {
            if (_isRefreshing)
                return;

            double weight = _previewLineWeightSlider.Value / 10.0;
            _previewLineWeightValue.Text = weight.ToString("0.0", CultureInfo.CurrentCulture) + "x";
            MutateSelectedTerrainLive(terrain => terrain.PreviewLineWeight = weight);
        };
        var (beginLineWeightDeferral, endLineWeightDeferral) = CreatePairedRefreshDeferral();
        _previewLineWeightSlider.MouseDown += (_, e) =>
        {
            if (e.Buttons == MouseButtons.Primary)
                beginLineWeightDeferral();
        };
        _previewLineWeightSlider.MouseUp += (_, e) =>
        {
            if (e.Buttons == MouseButtons.Primary)
                endLineWeightDeferral();
        };
        _previewLineWeightSlider.LostFocus += (_, _) => endLineWeightDeferral();
        ApplyHelp(_showWiresCheck, "Show or hide MoleHill terrain mesh wires in preview and generated terrain meshes.");
        _showWiresCheck.CheckedChanged += (_, _) =>
        {
            if (_isRefreshing)
                return;

            MutateSelectedTerrain(terrain => terrain.ShowMeshWires = _showWiresCheck.Checked == true, scheduleRebuild: false);
            var doc = RhinoDoc.ActiveDoc;
            var terrain = doc == null ? null : _controller.GetSelectedTerrain(doc);
            if (doc != null && terrain != null)
                _controller.RefreshTerrainDisplay(doc, terrain.TerrainId);
        };
        ApplyHelp(_showSlowBuildWarningCheck, "Warn before preview or exact rebuild when recent timings or mesh size suggest this terrain may be slow to process.");
        _showSlowBuildWarningCheck.CheckedChanged += (_, _) =>
        {
            if (_isRefreshing)
                return;

            MutateSelectedTerrain(terrain => terrain.ShowSlowBuildWarning = _showSlowBuildWarningCheck.Checked != false, scheduleRebuild: false);
        };
        ApplyHelp(_replacePreviousBakesCheck, "When baking, delete this terrain's previously baked document objects before adding the new bake set.");
        _replacePreviousBakesCheck.CheckedChanged += (_, _) =>
        {
            if (_isRefreshing)
                return;

            MutateSelectedTerrain(terrain => terrain.ReplacePreviouslyBaked = _replacePreviousBakesCheck.Checked == true, scheduleRebuild: false);
        };
        ApplyHelp(_untrackSelectedBakesButton, "Remove the selected baked objects from this terrain's tracked bake set so future replace-bakes leave them alone.");
        _untrackSelectedBakesButton.Click += OnUntrackSelectedBakes;
        ApplyHelp(_untrackAllBakesButton, "Forget all baked objects currently tracked by this terrain without deleting them.");
        _untrackAllBakesButton.Click += OnUntrackAllBakes;
    }

    /// <summary>The collapsible Terrain Settings card.</summary>
    private Control BuildSettingsCard()
    {
        // ── Settings card (collapsible, expanded by default) ─────────
        var settingsDescription = UiControls.Label("Document defaults, layers, and terrain display", UiLabelRole.Meta);
        var settingsHeader = new SectionHeader(
            "Terrain Settings",
            settingsDescription,
            _settingsExpanded,
            expanded =>
            {
                _settingsExpanded = expanded;
                _settingsContent!.Visible = expanded;
            });
        settingsHeader.SizeChanged += (_, _) => settingsDescription.Visible = settingsHeader.Width <= 0 || settingsHeader.Width >= UiMetrics.HeaderStatusBreak;

        // Output routing and appearance both live in the layer template now, so the panel names the
        // template and opens the editor rather than carrying a layer picker per destination.
        var layerTemplateEditButton = MakeInlineButton(
            "Edit…",
            (_, _) => OnEditLayerTemplate(),
            "Edit the layer template: which layer each kind of output goes to, and how it looks.");
        var layerTemplateApplyButton = MakeInlineButton(
            "Apply",
            (_, _) => OnApplyLayerTemplate(),
            "Create this template's layers in the document. Existing layers are left as they are.");
        var layerTemplateControls = CreateResponsivePrimaryActionRow(
            _layerTemplateLabel,
            UiMetrics.SpaceSmall,
            layerTemplateEditButton,
            layerTemplateApplyButton);
        var layerTemplateRow = new PropertyRow(
            CreateHelpLabel(
                "Output Layers",
                "The layer template this terrain routes and styles its output through. The document "
                    + "keeps its own copy, so it looks the same wherever it is opened.",
                0),
            layerTemplateControls,
            expandWidget: true);

        var slopeUnitRow = CreateDropDownEditor(
            "Slope Units",
            SlopeUnitPreference.Choices
                .Select(unit => (AnalysisFormatting.GetSlopeUnitKey(unit), SlopeUnitLabel(unit)))
                .ToList(),
            AnalysisFormatting.GetSlopeUnitKey(SlopeUnitPreference.Current),
            OnSlopeUnitChanged,
            "The unit every slope field is shown in — grading batters, the stair daylight slope, the "
                + "scatter slope filter. Whatever is selected here, a slope field still accepts any unit "
                + "typed into it (25%, 1:3, 50‰, 14°) and converts. This is a personal display "
                + "preference: it is not saved into the document and never changes the terrain.");

        var toleranceRow = new PropertyRow(
            CreateHelpLabel("Detail Size", "Smallest terrain detail to preserve automatically. Smaller values keep more detail; larger values simplify and merge nearby geometry more aggressively.", 0),
            _toleranceStepper);
        var opacityLabel = CreateHelpLabel("Opacity", "Terrain opacity used for preview and bake.", UiMetrics.ShortLabel);
        var resetTerrainColorButton = MakeInlineButton("Reset", (_, _) => ResetTerrainColor(), "Restore the default terrain display color.");
        var terrainColorPrimary = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = UiMetrics.SpaceSmall,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items =
            {
                _terrainColorSwatch,
                opacityLabel,
                new StackLayoutItem(_terrainOpacitySlider, expand: true)
            }
        };
        // The stepper + reset button drop to their own line before the slider gets squeezed to nothing.
        var terrainColorControls = new AdaptivePrimaryActionRow(terrainColorPrimary, UiMetrics.SpaceSmall, _terrainOpacityStepper, resetTerrainColorButton);
        var terrainColorRow = new PropertyRow(
            CreateHelpLabel("Terrain Color", "Base display color for the terrain preview and baked terrain. Click the swatch to change. Opacity affects this terrain mesh only.", 0),
            terrainColorControls,
            expandWidget: true);
        var terrainDisplayRow = new PropertyRow(
            new Panel(),
            new AdaptiveControlGroup(UiMetrics.SpaceSmall, _showWiresCheck, _showSlowBuildWarningCheck),
            expandWidget: true);
        var previewLineWeightRow = new PropertyRow(
            CreateHelpLabel(
                "Preview Line Weight",
                "On-screen only: multiplies the thickness of previewed lines so a busy plan stays "
                    + "readable on a dense display. Everything else previews exactly as it bakes, so "
                    + "any value other than 1.0x makes the preview deliberately heavier than the print.",
                0),
            new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = UiMetrics.SpaceMedium,
                VerticalContentAlignment = VerticalAlignment.Center,
                Items =
                {
                    new StackLayoutItem(_previewLineWeightSlider, expand: true),
                    _previewLineWeightValue
                }
            },
            expandWidget: true);
        var bakeTrackingControls = new AdaptiveControlGroup(UiMetrics.SpaceSmall, _replacePreviousBakesCheck, _untrackSelectedBakesButton, _untrackAllBakesButton);
        var bakeTrackingRow = new PropertyRow(
            CreateHelpLabel("Tracking", "Replace previous bake sets automatically, or untrack baked objects you want to keep.", 0),
            bakeTrackingControls,
            expandWidget: true);
        var settingsInner = new StackLayout
        {
            Orientation = Orientation.Vertical, Spacing = UiMetrics.SpaceSmall, Padding = new Padding(UiMetrics.CardHorizontalPadding, UiMetrics.SpaceLarge),
            Items =
            {
                new StackLayoutItem(terrainColorRow, HorizontalAlignment.Stretch),
                new StackLayoutItem(terrainDisplayRow, HorizontalAlignment.Stretch),
                new StackLayoutItem(previewLineWeightRow, HorizontalAlignment.Stretch),
                new StackLayoutItem(toleranceRow, HorizontalAlignment.Stretch),
                new StackLayoutItem(slopeUnitRow, HorizontalAlignment.Stretch),
                new StackLayoutItem(bakeTrackingRow, HorizontalAlignment.Stretch),
                new StackLayoutItem(layerTemplateRow, HorizontalAlignment.Stretch)
            }
        };

        _settingsContent = new Panel { Content = settingsInner, Visible = _settingsExpanded, BackgroundColor = UiTheme.CardBackground };

        var settingsCardBody = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = 0,
            Items =
            {
                new StackLayoutItem(settingsHeader, HorizontalAlignment.Stretch),
                new StackLayoutItem(_settingsContent, HorizontalAlignment.Stretch)
            }
        };
        // Same left accent strip + outer padding every stack card uses, so this reads as one more card
        // in the stack rather than a plain toolbar block. Neutral gray (ZoneStripColor) since it isn't a
        // typed card with its own accent color.
        var settingsStrip = new Panel { Width = UiMetrics.CardAccentWidth, BackgroundColor = UiTheme.ZoneStripColor };
        var settingsCard = WrapCardControl(settingsCardBody, settingsStrip, UiTheme.CardBackground);

        return settingsCard;
    }

    private void ApplyTerrainOpacity(int opacityPercent)
    {
        MutateSelectedTerrainLive(
            terrain => terrain.TerrainColorArgb = WithOpacityPercent(terrain.TerrainColorArgb, opacityPercent));
    }

    private void SetTerrainOpacityControls(int opacityPercent)
    {
        _isUpdatingOpacityControls = true;
        try
        {
            _terrainOpacityStepper.Value = opacityPercent;
            _terrainOpacitySlider.Value = opacityPercent;
        }
        finally
        {
            _isUpdatingOpacityControls = false;
        }
    }

    private void OnPickTerrainColor(object? sender, EventArgs e)
    {
        var doc = RhinoDoc.ActiveDoc;
        var terrain = doc == null ? null : _controller.GetSelectedTerrain(doc);
        if (doc == null || terrain == null)
            return;

        var colorDialog = new ColorDialog
        {
            Color = ToEtoColor(System.Drawing.Color.FromArgb(terrain.TerrainColorArgb))
        };

        if (colorDialog.ShowDialog(RhinoEtoApp.MainWindowForDocument(doc)) != DialogResult.Ok)
            return;

        int opacityPercent = GetOpacityPercent(terrain.TerrainColorArgb);
        MutateSelectedTerrain(item => item.TerrainColorArgb = WithOpacityPercent(ToArgb(colorDialog.Color), opacityPercent), scheduleRebuild: false);
        _controller.RefreshTerrainDisplay(doc, terrain.TerrainId);
    }

    private void ResetTerrainColor()
    {
        MutateSelectedTerrain(terrain => terrain.TerrainColorArgb = TerrainDefinition.DefaultTerrainColorArgb, scheduleRebuild: false);
        var doc = RhinoDoc.ActiveDoc;
        var terrain = doc == null ? null : _controller.GetSelectedTerrain(doc);
        if (doc != null && terrain != null)
            _controller.RefreshTerrainDisplay(doc, terrain.TerrainId);
    }

    /// <summary>
    /// Switches the unit every slope field reads and writes in. Nothing about the terrain changes — the
    /// definitions still store degrees — so this saves no document and schedules no rebuild; it only
    /// relabels and reformats the cards, which a plain refresh already does by marking every tab stale.
    /// </summary>
    private void OnSlopeUnitChanged(string key)
    {
        SlopeAnalyzer.SlopeUnit unit = AnalysisFormatting.ParseSlopeUnit(key);
        if (unit == SlopeUnitPreference.Current)
            return;

        SlopeUnitPreference.Current = unit;
        RefreshUi();
    }

    /// <summary>Dropdown text for a slope unit: the name plus the symbol the fields will show.</summary>
    private static string SlopeUnitLabel(SlopeAnalyzer.SlopeUnit unit)
    {
        return unit == SlopeAnalyzer.SlopeUnit.Ratio
            ? "Ratio (1:3)"
            : $"{SlopeInput.Name(unit)} ({SlopeInput.Suffix(unit)})";
    }

    /// <summary>Sets the slider and its readout without re-entering the ValueChanged handler.</summary>
    private void SetPreviewLineWeightControls(double weight)
    {
        double clamped = Math.Clamp(weight > 0.0 ? weight : 1.0, 0.3, 4.0);
        _previewLineWeightSlider.Value = (int)Math.Round(clamped * 10.0);
        _previewLineWeightValue.Text = clamped.ToString("0.0", CultureInfo.CurrentCulture) + "x";
    }
}
