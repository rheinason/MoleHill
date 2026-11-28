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
    private readonly NumberField _tolerance = new() { MinValue = 0.000000001 };
    private readonly Label _summary = UiDialogs.Note();
    private TerrainValidationPreparation? _preview;

    private TerrainInputValidationDialog(IReadOnlyList<TerrainInputGeometry> selected, double defaultTolerance, string unit)
    {
        _selected = selected;
        _defaultTolerance = defaultTolerance;
        UiDialogs.Frame(this, "Validate Terrain Inputs", new Size(460, 310));
        int toleranceDecimals = Math.Clamp(
            (int)Math.Ceiling(-Math.Log10(Math.Max(defaultTolerance, 1e-9))) + 2,
            3,
            9);
        _tolerance.DecimalPlaces = toleranceDecimals;
        _tolerance.Increment = Math.Pow(10.0, -Math.Max(1, toleranceDecimals - 1));
        _tolerance.Value = defaultTolerance;
        _tolerance.Unit = unit;
        _tolerance.Width = UiMetrics.DialogNumberField;

        var preview = UiDialogs.Button("Preview", (_, _) => RefreshSummary(), "Work out what the checked operations would change, without changing anything.");
        var apply = UiDialogs.Button("Apply", (_, _) =>
        {
            RefreshSummary();
            TerrainValidationPreparation? preparation = _preview;
            _preview = null;
            Result = new TerrainValidationDialogResult(true, CreateOptions(), preparation);
            Close();
        });
        var cancel = UiDialogs.Button("Cancel", (_, _) =>
        {
            Result = new TerrainValidationDialogResult(false, CreateOptions(), null);
            Close();
        });
        Closed += (_, _) => DisposePreview();

        UiDialogs.SetContent(this, new DynamicLayout
        {
            DefaultSpacing = UiMetrics.DialogGap,
            Rows =
            {
                UiDialogs.Intro(
                    $"Selected terrain inputs: {selected.Count:N0}. Checked operations are applied in a fixed order. " +
                    "Surviving objects retain their Rhino settings and object IDs."),
                _overkill,
                _joinEndpoints,
                _collapseShort,
                _cleanDuplicates,
                UiDialogs.Field("Tolerance", _tolerance, "How close two points or endpoints must be to count as the same.", expand: false),
                _summary,
                UiDialogs.Actions(this, apply, cancel, preview)
            }
        });

        _summary.Text = "Choose cleanup operations, then select Preview to calculate the expected changes.";
    }

    public static TerrainValidationDialogResult Show(
        RhinoDoc doc,
        IReadOnlyList<TerrainInputGeometry> selected,
        double defaultTolerance)
    {
        using var dialog = new TerrainInputValidationDialog(selected, defaultTolerance, ModelUnits.Abbreviation(doc.ModelUnitSystem))
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
