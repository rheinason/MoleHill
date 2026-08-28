// Hosts the live curve inspector and computes length, grade, terrain-coverage, and compliance diagnostics.
using System.Globalization;
using Eto.Drawing;
using Eto.Forms;
using MoleHill.Rhino.Model;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;
using EtoPoint = Eto.Drawing.Point;

namespace MoleHill.Rhino.Services;

internal readonly record struct CurveReviewSnapshot(
    bool IsValid,
    double PlanLength,
    double Length3d,
    double StartElevation,
    double EndElevation,
    double MinimumGrade,
    double MaximumGrade,
    double AverageGrade,
    int ReversalCount,
    int VerticalBreakCount,
    int TerrainSamples,
    int TerrainMisses,
    string Message)
{
    public static CurveReviewSnapshot Invalid(string message) => new(
        false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, message);
}

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

    public static CurveReviewSnapshot Evaluate(RhinoDoc doc, Guid objectId, double? maximumGradePercent)
    {
        RhinoObject? rhinoObject = doc.Objects.FindId(objectId);
        if (rhinoObject?.Geometry is not Curve curve || !curve.IsValid)
            return CurveReviewSnapshot.Invalid("Curve was deleted or is no longer valid.");

        double length = curve.GetLength();
        double planLength = GeometryCommandAlgorithms.CalculatePlanLength(curve, curve.Domain.T0, curve.Domain.T1);
        if (!double.IsFinite(length) || !double.IsFinite(planLength) || planLength <= doc.ModelAbsoluteTolerance)
            return CurveReviewSnapshot.Invalid("Curve is too short to inspect.");

        const int sampleCount = 64;
        var samples = new List<Point3d>(sampleCount + 1);
        for (int i = 0; i <= sampleCount; i++)
        {
            double distance = length * i / sampleCount;
            if (!curve.LengthParameter(distance, out double parameter))
                parameter = i == 0 ? curve.Domain.T0 : curve.Domain.T1;
            samples.Add(curve.PointAt(parameter));
        }

        var grades = new List<double>(sampleCount);
        int reversals = 0;
        int verticalBreaks = 0;
        double weightedGradeTotal = 0.0;
        double totalPlanDistance = 0.0;
        double? previousGrade = null;
        for (int i = 1; i < samples.Count; i++)
        {
            double planDistance = GeometryCommandAlgorithms.CalculatePlanDistance(samples[i - 1], samples[i]);
            if (planDistance <= doc.ModelAbsoluteTolerance)
                continue;

            double grade = (samples[i].Z - samples[i - 1].Z) / planDistance * 100.0;
            grades.Add(grade);
            weightedGradeTotal += grade * planDistance;
            totalPlanDistance += planDistance;
            if (previousGrade.HasValue && Math.Abs(grade - previousGrade.Value) > 10.0)
                verticalBreaks++;
            if (previousGrade.HasValue && Math.Abs(previousGrade.Value) > 0.05 &&
                Math.Abs(grade) > 0.05 && Math.Sign(previousGrade.Value) != Math.Sign(grade))
                reversals++;
            previousGrade = grade;
        }

        int terrainSamples = 0;
        int terrainMisses = 0;
        TerrainDefinition? terrain = TerrainController.Instance.GetSelectedTerrain(doc);
        Mesh? terrainMesh = terrain == null
            ? null
            : TerrainController.Instance.DuplicateFinalTerrainMesh(doc, terrain.TerrainId);
        try
        {
            if (terrainMesh != null)
            {
                foreach (Point3d point in samples)
                {
                    if (TerrainMeshProjection.TryProjectPointAlongWorldZ(
                            terrainMesh, new Point3d(point.X, point.Y, 0.0), doc.ModelAbsoluteTolerance, out _))
                        terrainSamples++;
                    else
                        terrainMisses++;
                }
            }
        }
        finally
        {
            terrainMesh?.Dispose();
        }

        double minimum = grades.Count == 0 ? 0.0 : grades.Min();
        double maximum = grades.Count == 0 ? 0.0 : grades.Max();
        double average = totalPlanDistance <= RhinoMath.ZeroTolerance
            ? 0.0
            : weightedGradeTotal / totalPlanDistance;
        string message = maximumGradePercent.HasValue && grades.Any(g => Math.Abs(g) > maximumGradePercent.Value)
            ? $"FAIL: exceeds {maximumGradePercent.Value.ToString("F2", CultureInfo.InvariantCulture)}%"
            : reversals > 0 || verticalBreaks > 0 || terrainMisses > 0
                ? "WARNING: review highlighted stations"
                : "OK: no active warnings";

        return new CurveReviewSnapshot(
            true, planLength, length, curve.PointAtStart.Z, curve.PointAtEnd.Z,
            minimum, maximum, average, reversals, verticalBreaks, terrainSamples, terrainMisses, message);
    }
}

internal sealed class CurveReviewForm : Form
{
    private readonly RhinoDoc _doc;
    private readonly Label _objectLabel = new();
    private readonly Label _summary = new();
    private readonly Label _grades = new();
    private readonly Label _events = new();
    private readonly Label _terrain = new();
    private readonly TextBox _maximumGrade = new() { Width = 70 };
    private readonly UITimer _timer;
    private Guid _objectId;
    private bool _closing;

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
        _maximumGrade.ToolTip = "Optional maximum absolute grade in percent.";
        _maximumGrade.TextChanged += (_, _) => RefreshReview();

        var close = new Button { Text = "Close", Width = 54 };
        close.Click += (_, _) => Close();
        var change = new Button { Text = "Change", Width = 58 };
        change.Click += (_, _) => ChangeObject();

        _objectLabel.TextColor = MoleHill.Rhino.UI.UiTheme.MutedText;
        foreach (Label label in new[] { _summary, _grades, _events, _terrain })
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
                _events,
                _terrain,
                new StackLayout
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 5,
                    Items = { new Label { Text = "Max grade %", VerticalAlignment = VerticalAlignment.Center }, _maximumGrade, change, close }
                }
            }
        };

        _timer = new UITimer { Interval = 0.25 };
        _timer.Elapsed += (_, _) => RefreshReview();
        _timer.Start();
        Closed += (_, _) =>
        {
            _closing = true;
            _timer.Stop();
        };
        RefreshReview();
    }

    public void SetObject(Guid objectId)
    {
        _objectId = objectId;
        RefreshReview();
    }

    public void PositionOverActiveView(RhinoDoc doc)
    {
        var view = doc.Views.ActiveView;
        if (view != null)
        {
            var rect = view.ScreenRectangle;
            Location = new EtoPoint(rect.Right - 300, rect.Top + 12);
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

    private void RefreshReview()
    {
        if (_closing)
            return;
        if (!double.TryParse(_maximumGrade.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double maximum))
            maximum = double.NaN;
        CurveReviewSnapshot snapshot = CurveReviewService.Evaluate(_doc, _objectId, double.IsFinite(maximum) ? Math.Abs(maximum) : null);
        if (!snapshot.IsValid)
        {
            _objectLabel.Text = "Curve unavailable";
            _summary.Text = snapshot.Message;
            _grades.Text = string.Empty;
            _events.Text = string.Empty;
            _terrain.Text = string.Empty;
            if (_doc.Objects.FindId(_objectId) == null)
                Close();
            return;
        }

        _objectLabel.Text = $"Curve {_objectId.ToString("D")[..8]}  •  {snapshot.Message}";
        _summary.Text = $"Plan {snapshot.PlanLength:F2}   3D {snapshot.Length3d:F2}\n" +
                        $"Z {snapshot.StartElevation:F2} → {snapshot.EndElevation:F2}";
        _grades.Text = $"Grade  min {snapshot.MinimumGrade:F2}%   max {snapshot.MaximumGrade:F2}%   avg {snapshot.AverageGrade:F2}%";
        _events.Text = $"Events  reversals {snapshot.ReversalCount}   vertical breaks {snapshot.VerticalBreakCount}";
        _terrain.Text = snapshot.TerrainSamples + snapshot.TerrainMisses == 0
            ? "Terrain  no final active terrain"
            : $"Terrain  {snapshot.TerrainSamples} samples   {snapshot.TerrainMisses} outside";
    }
}
