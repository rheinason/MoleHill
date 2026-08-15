// Rhino-parented Eto dialog for selecting terrain-input cleanup operations.
using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.UI;

namespace MoleHill.Rhino.UI;

using MoleHill.Rhino.Services;

internal readonly record struct TerrainValidationDialogResult(
    bool Accepted,
    TerrainValidationOptions Options,
    TerrainValidationPreparation? Preparation);

internal sealed class TerrainInputValidationDialog : Dialog<TerrainValidationDialogResult>
{
    private readonly IReadOnlyList<TerrainInputGeometry> _selected;
    private readonly double _defaultTolerance;
    private readonly CheckBox _overkill = new() { Text = "Remove duplicate/coincident objects", Checked = true };
    private readonly CheckBox _joinEndpoints = new() { Text = "Join near endpoints (same layer only)", Checked = true };
    private readonly CheckBox _collapseShort = new() { Text = "Collapse short segments", Checked = true };
    private readonly CheckBox _cleanDuplicates = new() { Text = "Clean duplicate curve vertices", Checked = true };
    private readonly NumericStepper _tolerance = new() { MinValue = 0.000000001 };
    private readonly Label _summary = new() { TextColor = UiTheme.MutedText, Wrap = WrapMode.Word };
    private TerrainValidationPreparation? _preview;

    private TerrainInputValidationDialog(IReadOnlyList<TerrainInputGeometry> selected, double defaultTolerance)
    {
        _selected = selected;
        _defaultTolerance = defaultTolerance;
        Title = "Validate Terrain Inputs";
        Owner = null;
        Resizable = false;
        Padding = 12;
        MinimumSize = new Size(460, 310);
        this.UseRhinoStyle();
        int toleranceDecimals = Math.Clamp(
            (int)Math.Ceiling(-Math.Log10(Math.Max(defaultTolerance, 1e-9))) + 2,
            3,
            9);
        _tolerance.DecimalPlaces = toleranceDecimals;
        _tolerance.Increment = Math.Pow(10.0, -Math.Max(1, toleranceDecimals - 1));
        _tolerance.Value = defaultTolerance;

        var preview = new Button { Text = "Preview" };
        var apply = new Button { Text = "Apply" };
        var cancel = new Button { Text = "Cancel" };
        DefaultButton = apply;
        AbortButton = cancel;

        preview.Click += (_, _) => RefreshSummary();
        apply.Click += (_, _) =>
        {
            RefreshSummary();
            TerrainValidationPreparation? preparation = _preview;
            _preview = null;
            Result = new TerrainValidationDialogResult(true, CreateOptions(), preparation);
            Close();
        };
        cancel.Click += (_, _) =>
        {
            Result = new TerrainValidationDialogResult(false, CreateOptions(), null);
            Close();
        };
        Closed += (_, _) => DisposePreview();

        Content = new DynamicLayout
        {
            DefaultSpacing = new Size(8, 8),
            Rows =
            {
                new Label
                {
                    Text = $"Selected terrain inputs: {selected.Count:N0}. Checked operations are applied in a fixed order. " +
                           "Surviving objects retain their Rhino settings and object IDs.",
                    Wrap = WrapMode.Word
                },
                _overkill,
                _joinEndpoints,
                _collapseShort,
                _cleanDuplicates,
                new StackLayout
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Items =
                    {
                        new Label { Text = "Tolerance:", VerticalAlignment = VerticalAlignment.Center },
                        _tolerance,
                        new Label { Text = "model units", TextColor = UiTheme.MutedText, VerticalAlignment = VerticalAlignment.Center }
                    }
                },
                _summary,
                new StackLayout
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalContentAlignment = HorizontalAlignment.Right,
                    Items =
                    {
                        new StackLayoutItem(new Panel(), expand: true),
                        preview,
                        cancel,
                        apply
                    }
                }
            }
        };

        _summary.Text = "Choose cleanup operations, then select Preview to calculate the expected changes.";
    }

    public static TerrainValidationDialogResult Show(
        RhinoDoc doc,
        IReadOnlyList<TerrainInputGeometry> selected,
        double defaultTolerance)
    {
        using var dialog = new TerrainInputValidationDialog(selected, defaultTolerance)
        {
            Owner = RhinoEtoApp.MainWindowForDocument(doc)
        };
        return dialog.ShowModal(RhinoEtoApp.MainWindowForDocument(doc));
    }

    private TerrainValidationOptions CreateOptions()
    {
        return new TerrainValidationOptions(
            _overkill.Checked == true,
            _joinEndpoints.Checked == true,
            _collapseShort.Checked == true,
            _cleanDuplicates.Checked == true,
            Math.Max(_tolerance.Value, _defaultTolerance * 0.000001));
    }

    private void RefreshSummary()
    {
        DisposePreview();
        _preview = TerrainInputCommandAlgorithms.PrepareValidation(_selected, CreateOptions());
        TerrainValidationSummary summary = _preview.Summary;
        _summary.Text = $"Preview: {summary.OutputObjects:N0} output objects; " +
                        $"{summary.DuplicateObjectsRemoved:N0} duplicates and " +
                        $"{summary.DuplicateSegmentsRemoved:N0} duplicate segments removed, " +
                        $"{summary.JoinedCurveCount:N0} joins, " +
                        $"{summary.ShortSegmentsCollapsed:N0} short segments collapsed, " +
                        $"{summary.DuplicateVerticesRemoved:N0} duplicate vertices removed.";
    }

    private void DisposePreview()
    {
        if (_preview == null)
            return;

        foreach (TerrainInputGeometry item in _preview.Objects)
            item.Curve?.Dispose();
        _preview = null;
    }
}
