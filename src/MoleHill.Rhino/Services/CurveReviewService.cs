// Hosts the live curve inspector: builds the review analysis, reports it in a panel and draws it in the viewport.
using System.Globalization;
using Eto.Drawing;
using Eto.Forms;
using MoleHill.Rhino.Model;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input.Custom;
using Rhino.Input;
using Rhino.UI;
using EtoPoint = Eto.Drawing.Point;

namespace MoleHill.Rhino.Services;

internal static class CurveReviewService
{
    private static readonly Dictionary<uint, CurveReviewForm> OpenForms = new();

    public static Result Start(RhinoDoc doc)
    {
        var getObject = new GetObject();
        getObject.SetCommandPrompt("Select curve to inspect");
        getObject.GeometryFilter = ObjectType.Curve;
        getObject.EnablePreSelect(true, true);
        if (getObject.Get() != GetResult.Object)
            return getObject.CommandResult();

        Guid objectId = getObject.Object(0).ObjectId;
        if (OpenForms.TryGetValue(doc.RuntimeSerialNumber, out CurveReviewForm? existing))
        {
            existing.SetObject(objectId);
            existing.BringToFront();
            return Result.Success;
        }

        var form = new CurveReviewForm(doc, objectId);
        OpenForms[doc.RuntimeSerialNumber] = form;
        form.Closed += (_, _) => OpenForms.Remove(doc.RuntimeSerialNumber);
        form.PositionOverActiveView(doc);
        form.Show();
        return Result.Success;
    }

    /// <summary>
    /// Builds the review for the inspected object. The terrain mesh is peeked, not duplicated, because the
    /// inspector re-evaluates on a timer.
    /// </summary>
    public static CurveReviewAnalysis? Evaluate(
        RhinoDoc doc,
        Guid objectId,
        double? maximumGradePercent,
        double? minimumPlanRadius,
        out string? error)
    {
        error = null;
        RhinoObject? rhinoObject = doc.Objects.FindId(objectId);
        if (rhinoObject?.Geometry is not Curve curve)
        {
            error = "Curve was deleted or is no longer valid.";
            return null;
        }

        return CurveReviewAnalyzer.Build(
            doc, curve, PeekTerrainMesh(doc), maximumGradePercent, minimumPlanRadius, out error);
    }

    public static Mesh? PeekTerrainMesh(RhinoDoc doc)
    {
        TerrainDefinition? terrain = TerrainController.Instance.GetSelectedTerrain(doc);
        return terrain == null ? null : TerrainController.Instance.PeekFinalTerrainMesh(doc, terrain.TerrainId);
    }

    public static string DescribeStatus(CurveReviewAnalysis analysis)
    {
        if (analysis.GradeFails)
        {
            double limit = analysis.MaximumGradeLimit ?? 0.0;
            return $"FAIL: {analysis.GradeExceedances.Count} stretch(es) over {limit.ToString("F2", CultureInfo.InvariantCulture)}%";
        }

        if (analysis.RadiusFails)
        {
            double limit = analysis.MinimumRadiusLimit ?? 0.0;
            return $"FAIL: {analysis.RadiusViolations.Count} stretch(es) under R {limit.ToString("F1", CultureInfo.InvariantCulture)}";
        }

        return analysis.ReversalCount > 0 || analysis.VerticalBreakCount > 0 || analysis.TerrainMisses > 0
            ? "WARNING: review highlighted stations"
            : "OK: no active warnings";
    }
}

internal sealed class CurveReviewForm : Form
{
    private readonly RhinoDoc _doc;
    private readonly Label _objectLabel = new();
    private readonly Label _summary = new();
    private readonly Label _grades = new();
    private readonly Label _profile = new();
    private readonly Label _events = new();
    private readonly Label _terrain = new();
    private readonly TextBox _maximumGrade = new() { Width = 60 };
    private readonly TextBox _minimumRadius = new() { Width = 60 };
    private readonly CheckBox _showGrade = new() { Text = "Grade", Checked = true };
    private readonly CheckBox _showLabels = new() { Text = "Labels", Checked = true };
    private readonly CheckBox _showEvents = new() { Text = "Events", Checked = true };
    private readonly CheckBox _showTerrain = new() { Text = "Terrain", Checked = true };
    private readonly DropDown _labelDensity = new() { Width = 78 };
    private readonly NumericStepper _lineWeight = new()
    {
        Width = 58,
        MinValue = 0.5,
        MaxValue = 4.0,
        Increment = 0.5,
        DecimalPlaces = 1,
        Value = 1.0
    };
    private readonly DropDown _labelContent = new() { Width = 116 };
    private readonly Button _labelCurve = new() { Text = "Label", Width = 54 };
    private readonly CurveReviewConduit _conduit = new();
    private readonly UITimer _timer;
    private Guid _objectId;
    private string _fingerprint = string.Empty;
    private bool _closing;
    private bool _labelling;

    /// <summary>Density options, as the cap on how many stretch labels are drawn at once.</summary>
    private static readonly (string Label, int MaximumSpanLabels)[] LabelDensities =
    {
        ("Sparse", 8),
        ("Normal", 16),
        ("Dense", 40)
    };

    /// <summary>What a placed label says. Kept in the order a drafter usually wants them.</summary>
    private static readonly (string Label, CurveLabelContent Content)[] LabelContents =
    {
        ("Elevation", CurveLabelContent.Elevation),
        ("Grade", CurveLabelContent.Grade),
        ("Station", CurveLabelContent.Station),
        ("Cut / fill", CurveLabelContent.CutFill),
        ("Sta + elev", CurveLabelContent.Station | CurveLabelContent.Elevation),
        ("Elev + grade", CurveLabelContent.Elevation | CurveLabelContent.Grade)
    };

    public CurveReviewForm(RhinoDoc doc, Guid objectId)
    {
        _doc = doc;
        _objectId = objectId;
        Title = "Inspect Curve";
        Resizable = false;
        Maximizable = false;
        Minimizable = false;
        ShowInTaskbar = false;
        Owner = RhinoEtoApp.MainWindowForDocument(doc);
        this.UseRhinoStyle();

        _maximumGrade.Text = string.Empty;
        _maximumGrade.ToolTip = "Optional maximum absolute grade in percent. Stretches over it are flagged red.";
        _maximumGrade.TextChanged += (_, _) => RefreshReview(force: true);
        _minimumRadius.Text = string.Empty;
        _minimumRadius.ToolTip = "Optional minimum plan (horizontal) radius in model units.";
        _minimumRadius.TextChanged += (_, _) => RefreshReview(force: true);

        foreach (CheckBox toggle in new[] { _showGrade, _showLabels, _showEvents, _showTerrain })
        {
            toggle.CheckedChanged += (_, _) =>
            {
                ApplyOverlayParts();
                _doc.Views.Redraw();
            };
        }

        foreach ((string label, int _) in LabelDensities)
            _labelDensity.Items.Add(new ListItem { Text = label });
        _labelDensity.SelectedIndex = 1;
        _labelDensity.ToolTip = "How many stretch labels to draw. Fewer labels read better on a long alignment.";
        _labelDensity.SelectedIndexChanged += (_, _) =>
        {
            ApplyOverlayParts();
            _doc.Views.Redraw();
        };

        _lineWeight.ToolTip = "Thickness of the overlay. The grade ribbon has to be wide enough to read its color.";
        _lineWeight.ValueChanged += (_, _) =>
        {
            ApplyOverlayParts();
            _doc.Views.Redraw();
        };

        foreach ((string label, CurveLabelContent _) in LabelContents)
            _labelContent.Items.Add(new ListItem { Text = label });
        _labelContent.SelectedIndex = 0;
        _labelContent.ToolTip = "What a placed label says. Values come from the profile already on screen.";

        _labelCurve.ToolTip =
            "Pick points along the curve to drop labels on the terrain annotation layer. " +
            "Enter finishes; one Undo removes the whole run.";
        _labelCurve.Click += (_, _) => LabelCurve();

        _showGrade.ToolTip = "Recolor the curve by grade (green to red across the limit).";
        _showLabels.ToolTip = "Elevations at kinks, grade and length per stretch, limit violations.";
        _showEvents.ToolTip = "Crests, sags, vertical breaks, sharp plan radii and terrain gaps.";
        _showTerrain.ToolTip = "Terrain drape under the curve with cut/fill ties and extremes.";

        var close = new Button { Text = "Close", Width = 54 };
        close.Click += (_, _) => Close();
        var change = new Button { Text = "Change", Width = 58 };
        change.Click += (_, _) => ChangeObject();

        _objectLabel.TextColor = MoleHill.Rhino.UI.UiTheme.MutedText;
        foreach (Label label in new[] { _summary, _grades, _profile, _events, _terrain })
            label.Wrap = WrapMode.Word;

        Content = new StackLayout
        {
            Padding = new Padding(10, 8),
            Spacing = 5,
            Items =
            {
                _objectLabel,
                _summary,
                _grades,
                _profile,
                _events,
                _terrain,
                new StackLayout
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Items = { _showGrade, _showLabels, _showEvents, _showTerrain }
                },
                new StackLayout
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 5,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Items =
                    {
                        new Label { Text = "Weight", VerticalAlignment = VerticalAlignment.Center },
                        _lineWeight,
                        new Label { Text = "Labels", VerticalAlignment = VerticalAlignment.Center },
                        _labelDensity
                    }
                },
                new StackLayout
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 5,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Items = { _labelContent, _labelCurve }
                },
                new StackLayout
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 5,
                    Items =
                    {
                        new Label { Text = "Max grade %", VerticalAlignment = VerticalAlignment.Center },
                        _maximumGrade,
                        new Label { Text = "Min radius", VerticalAlignment = VerticalAlignment.Center },
                        _minimumRadius,
                        change,
                        close
                    }
                }
            }
        };

        ApplyOverlayParts();
        _conduit.Enabled = true;
        _timer = new UITimer { Interval = 0.25 };
        _timer.Elapsed += (_, _) => RefreshReview(force: false);
        _timer.Start();
        Closed += (_, _) =>
        {
            _closing = true;
            _timer.Stop();
            _conduit.Enabled = false;
            _conduit.SetAnalysis(null);
            _doc.Views.Redraw();
        };
        RefreshReview(force: true);
    }

    public void SetObject(Guid objectId)
    {
        _objectId = objectId;
        RefreshReview(force: true);
    }

    public void PositionOverActiveView(RhinoDoc doc)
    {
        var view = doc.Views.ActiveView;
        if (view != null)
        {
            var rect = view.ScreenRectangle;
            Location = new EtoPoint(rect.Right - 320, rect.Top + 12);
        }
    }

    private void ChangeObject()
    {
        var getObject = new GetObject();
        getObject.SetCommandPrompt("Select curve to inspect");
        getObject.GeometryFilter = ObjectType.Curve;
        if (getObject.Get() == GetResult.Object)
            SetObject(getObject.Object(0).ObjectId);
    }

    private void ApplyOverlayParts()
    {
        CurveReviewOverlayParts parts = CurveReviewOverlayParts.None;
        if (_showGrade.Checked == true)
            parts |= CurveReviewOverlayParts.GradeRibbon;
        if (_showLabels.Checked == true)
            parts |= CurveReviewOverlayParts.Labels;
        if (_showEvents.Checked == true)
            parts |= CurveReviewOverlayParts.Events;
        if (_showTerrain.Checked == true)
            parts |= CurveReviewOverlayParts.Terrain;
        _conduit.Parts = parts;
        _conduit.LineWeight = _lineWeight.Value;
        _conduit.MaximumSpanLabels = LabelDensities[Math.Clamp(_labelDensity.SelectedIndex, 0, LabelDensities.Length - 1)].MaximumSpanLabels;
    }

    /// <summary>
    /// Runs the labelling pick loop. The form is a non-modal window owned by the Rhino main window, so it
    /// stays up and the overlay keeps drawing while the get runs; its controls are disabled for the
    /// duration so a setting cannot change out from under the loop.
    /// </summary>
    private void LabelCurve()
    {
        if (_labelling || _closing)
            return;

        CurveReviewAnalysis? analysis = _conduit.CurrentAnalysis;
        if (analysis == null)
        {
            RhinoApp.WriteLine("Nothing to label: the curve has no usable profile.");
            return;
        }

        CurveLabelContent content = LabelContents[Math.Clamp(_labelContent.SelectedIndex, 0, LabelContents.Length - 1)].Content;
        _labelling = true;
        SetInteractive(false);
        try
        {
            CurveReviewLabeller.Run(_doc, _objectId, analysis, content);
        }
        finally
        {
            _labelling = false;
            SetInteractive(true);
            _doc.Views.Redraw();
        }
    }

    private void SetInteractive(bool enabled)
    {
        if (Content is Control content)
            content.Enabled = enabled;
    }

    /// <summary>
    /// The timer ticks four times a second, so the analysis is only rebuilt when the object, the terrain
    /// mesh or a limit actually changed.
    /// </summary>
    private void RefreshReview(bool force)
    {
        // A rebuild during the labelling get would swap the analysis the loop is reading from.
        if (_closing || _labelling)
            return;

        double? maximumGrade = ParseLimit(_maximumGrade.Text);
        double? minimumRadius = ParseLimit(_minimumRadius.Text);
        string fingerprint = BuildFingerprint(maximumGrade, minimumRadius);
        if (!force && fingerprint == _fingerprint)
            return;
        _fingerprint = fingerprint;

        CurveReviewAnalysis? analysis = CurveReviewService.Evaluate(
            _doc, _objectId, maximumGrade, minimumRadius, out string? error);
        _conduit.SetAnalysis(analysis);
        _doc.Views.Redraw();

        if (analysis == null)
        {
            _objectLabel.Text = "Curve unavailable";
            _summary.Text = error ?? "Curve cannot be inspected.";
            _grades.Text = string.Empty;
            _profile.Text = string.Empty;
            _events.Text = string.Empty;
            _terrain.Text = string.Empty;
            if (_doc.Objects.FindId(_objectId) == null)
                Close();
            return;
        }

        _objectLabel.Text = $"Curve {_objectId.ToString("D")[..8]}  •  {CurveReviewService.DescribeStatus(analysis)}";
        _summary.Text = $"Plan {analysis.PlanLength:F2}   3D {analysis.Length3d:F2}\n" +
                        $"Z {analysis.StartElevation:F2} → {analysis.EndElevation:F2}   " +
                        $"Δ {analysis.EndElevation - analysis.StartElevation:+0.00;-0.00;0.00}";
        _grades.Text = $"Grade  min {analysis.MinimumGrade:F2}%   max {analysis.MaximumGrade:F2}%   avg {analysis.AverageGrade:F2}%\n" +
                       $"Steepest {analysis.SteepestGrade:F2}% at station {analysis.SteepestGradeStation:F1}";
        _profile.Text = $"Plan  min radius {FormatRadius(analysis.MinimumPlanRadius)}" +
                        (double.IsFinite(analysis.MinimumPlanRadius) ? $" at station {analysis.MinimumPlanRadiusStation:F1}" : string.Empty) +
                        $"   kinks {analysis.KinkCount}   stretches {analysis.Spans.Count}";
        _events.Text = $"Events  reversals {analysis.ReversalCount}   vertical breaks {analysis.VerticalBreakCount}   " +
                       $"over grade {analysis.GradeExceedances.Count}   under radius {analysis.RadiusViolations.Count}";
        _terrain.Text = analysis.TerrainSamples + analysis.TerrainMisses == 0
            ? "Terrain  no final active terrain"
            : $"Terrain  {analysis.TerrainSamples} samples   {analysis.TerrainMisses} outside\n" +
              $"Max fill {analysis.MaximumFill:F2} at {analysis.MaximumFillStation:F1}   " +
              $"max cut {analysis.MaximumCut:F2} at {analysis.MaximumCutStation:F1}";
    }

    private static string FormatRadius(double radius) =>
        double.IsFinite(radius) ? radius.ToString("F1", CultureInfo.CurrentCulture) : "straight";

    private static double? ParseLimit(string text)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) &&
            !double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
            return null;
        return double.IsFinite(value) && value > 0.0 ? Math.Abs(value) : null;
    }

    /// <summary>Cheap change key: the object serial number changes on every edit, and so does the rebuilt terrain mesh.</summary>
    private string BuildFingerprint(double? maximumGrade, double? minimumRadius)
    {
        RhinoObject? rhinoObject = _doc.Objects.FindId(_objectId);
        Mesh? terrainMesh = CurveReviewService.PeekTerrainMesh(_doc);
        int terrainKey = terrainMesh == null
            ? 0
            : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(terrainMesh);
        return string.Create(CultureInfo.InvariantCulture,
            $"{_objectId:N}|{rhinoObject?.RuntimeSerialNumber ?? 0}|{terrainKey}|{maximumGrade ?? -1}|{minimumRadius ?? -1}");
    }
}
