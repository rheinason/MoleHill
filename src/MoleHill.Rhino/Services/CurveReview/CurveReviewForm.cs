// Hosts the read-only mhInspectCurve palette: profile, checks, measurements, events and overlay display.
using System.Globalization;
using Eto.Drawing;
using Eto.Forms;
using MoleHill.Core.Analysis;
using MoleHill.Rhino.UI;
using MoleHill.Shared;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;

namespace MoleHill.Rhino.Services;

/// <summary>
/// The curve inspector reports; it never edits. Geometry is changed in Rhino, or through the terrain
/// definition — a second editing surface with its own preview and undo stack is what turned this panel
/// into two applications fighting for one column. The layout is therefore a reading order: what the
/// profile looks like, what passed, what it measures, what happened where.
/// </summary>
internal sealed class CurveReviewForm : Form
{
    private static readonly (string Label, CurveLabelContent Content)[] LabelContents =
    {
        ("Elevation", CurveLabelContent.Elevation),
        ("Grade", CurveLabelContent.Grade),
        ("Station", CurveLabelContent.Station),
        ("Cut / fill", CurveLabelContent.CutFill),
        ("Sta + elev", CurveLabelContent.Station | CurveLabelContent.Elevation),
        ("Elev + grade", CurveLabelContent.Elevation | CurveLabelContent.Grade)
    };

    private static readonly (string Label, CurveReviewMetric Metric)[] Metrics =
    {
        ("Grade", CurveReviewMetric.Grade),
        ("Elevation", CurveReviewMetric.Elevation),
        ("Cut / fill", CurveReviewMetric.CutFill),
        ("Plan radius", CurveReviewMetric.PlanRadius)
    };

    private readonly RhinoDoc _doc;
    private readonly ModelUnitContext _units;
    private readonly CurveReviewConduit _conduit = new();
    private readonly CurveProfileControl _profile = new();
    private readonly Label _objectLabel = UiControls.Label(string.Empty, UiLabelRole.Meta);
    private readonly Label _status = UiControls.Label(string.Empty);
    private readonly DynamicLayout _checks = new() { DefaultSpacing = new Size(UiMetrics.SpaceMedium, UiMetrics.SpaceXSmall) };
    private readonly DynamicLayout _limits = new() { DefaultSpacing = new Size(UiMetrics.SpaceSmall, UiMetrics.SpaceXSmall) };
    private readonly Panel _measurements = new();
    private readonly DynamicLayout _events = new() { DefaultSpacing = new Size(UiMetrics.SpaceMedium, UiMetrics.SpaceXSmall) };
    private readonly DropDown _metric = new() { Width = UiMetrics.DropDown };
    private readonly CheckBox _showRibbon = new() { Text = "Ribbon", Checked = true };
    private readonly CheckBox _showLabels = new() { Text = "Labels", Checked = true };
    private readonly CheckBox _showEvents = new() { Text = "Events", Checked = true };
    private readonly CheckBox _showTerrain = new() { Text = "Terrain", Checked = true };
    private readonly DropDown _labelContent = new() { Width = UiMetrics.DropDown };
    private readonly UITimer _timer;
    private CurveReviewRuleSettings _rules;
    private CurveReviewAnalysis? _analysis;
    private Guid _objectId;
    private uint _sourceSerial;
    private string _fingerprint = string.Empty;
    private bool _closing;
    private bool _labelling;
    private bool _refreshing;
    private bool _ruleRefreshPending;

    public CurveReviewForm(RhinoDoc doc, Guid objectId)
    {
        _doc = doc;
        _units = ModelUnitContext.FromDocument(doc);
        _objectId = objectId;
        _rules = CurveReviewRuleStore.Load(doc);

        Title = "Inspect Curve";
        Resizable = true;
        Maximizable = false;
        Minimizable = false;
        ShowInTaskbar = false;
        Owner = RhinoEtoApp.MainWindowForDocument(doc);
        MinimumSize = new Size(350, 440);
        Size = new Size(430, 640);
        this.UseRhinoStyle();

        _profile.Unit = _units.Abbreviation;
        _conduit.Unit = _units.Abbreviation;
        _profile.HoverStationChanged += (_, station) =>
        {
            _conduit.HoveredStation = station;
            _doc.Views.Redraw();
        };

        Content = BuildContent();
        ApplyOverlayParts();
        _timer = new UITimer { Interval = 0.25 };
        _timer.Elapsed += OnTimer;
        Closed += (_, _) => Shutdown();
        Shown += (_, _) =>
        {
            // Display conduits touch Rhino's native display pipeline. Defer enabling until Eto has
            // created the native window and the first review has been built, rather than enabling it
            // from the command/pick callback while Rhino is still unwinding GetObject().
            try
            {
                _timer.Start();
                _conduit.Enabled = true;
                _doc.Views.Redraw();
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"MoleHill Inspect Curve display overlay disabled: {ex.Message}");
            }
        };

        SetObject(objectId);
    }

    public void PositionOverActiveView()
    {
        var view = _doc.Views.ActiveView;
        if (view == null)
            return;
        var rect = view.ScreenRectangle;
        Location = new Eto.Drawing.Point(rect.Right - Width - 18, rect.Top + 12);
    }

    public void SetObject(Guid objectId)
    {
        _objectId = objectId;
        _sourceSerial = _doc.Objects.FindId(objectId)?.RuntimeSerialNumber ?? 0;
        _fingerprint = string.Empty;
        RefreshReview(true);
    }

    // ── Layout ─────────────────────────────────────────────────────────────

    private Control BuildContent()
    {
        var scrollContent = new DynamicLayout
        {
            Padding = new Padding(UiMetrics.SpaceMedium),
            DefaultSpacing = new Size(UiMetrics.SpaceSmall, UiMetrics.SpaceLarge)
        };
        scrollContent.Add(BuildHeader(), yscale: false);
        scrollContent.Add(BuildSection("Elevation profile", _profile, BuildMetricPicker()), yscale: false);
        scrollContent.Add(BuildSection("Checks", BuildChecksBody(), null), yscale: false);
        scrollContent.Add(BuildSection("Measurements", _measurements, null), yscale: false);
        scrollContent.Add(BuildSection("Events", _events, null), yscale: false);
        scrollContent.Add(BuildSection("Display", BuildDisplayFooter(), null), yscale: false);

        var scroll = new Scrollable
        {
            Content = scrollContent,
            Border = BorderType.None,
            ExpandContentWidth = false,
            ExpandContentHeight = false
        };
        UiControls.DisableHorizontalScrolling(scroll);
        void SyncContentWidth()
        {
            int width = scroll.ClientSize.Width;
            if (width > 0 && scrollContent.Width != width)
                scrollContent.Width = width;
        }

        scroll.SizeChanged += (_, _) => SyncContentWidth();
        scroll.Shown += (_, _) => SyncContentWidth();
        return scroll;
    }

    private Control BuildHeader()
    {
        var pick = UiControls.Button("Pick curve", (_, _) => ChangeObject(), "Inspect a different curve.", UiButtonRole.Inline);
        var close = UiControls.Button("Close", (_, _) => Close(), role: UiButtonRole.Inline);
        return new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = UiMetrics.SpaceMedium,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items = { pick, new StackLayoutItem(_objectLabel, true), _status, close }
        };
    }

    private Control BuildMetricPicker()
    {
        foreach ((string label, CurveReviewMetric _) in Metrics)
            _metric.Items.Add(label);
        _metric.SelectedIndex = 0;
        _metric.ToolTip = "Colour the profile and the curve in the viewport by this quantity.";
        _metric.SelectedIndexChanged += (_, _) =>
        {
            CurveReviewMetric metric = Metrics[Math.Clamp(_metric.SelectedIndex, 0, Metrics.Length - 1)].Metric;
            _profile.Metric = metric;
            _conduit.Metric = metric;
            _doc.Views.Redraw();
        };
        return _metric;
    }

    /// <summary>Checks read; the limits that drive them are one disclosure away, because they are set rarely.</summary>
    private Control BuildChecksBody()
    {
        _limits.Visible = false;
        var toggle = UiControls.Button("▶ Rule limits", null, "Show the thresholds these checks are measured against.", UiButtonRole.Inline);
        toggle.Click += (_, _) =>
        {
            _limits.Visible = !_limits.Visible;
            toggle.Text = (_limits.Visible ? "▼ " : "▶ ") + "Rule limits";
        };

        return new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = UiMetrics.SpaceSmall,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items = { _checks, toggle, _limits }
        };
    }

    private Control BuildDisplayFooter()
    {
        foreach (CheckBox toggle in new[] { _showRibbon, _showLabels, _showEvents, _showTerrain })
        {
            toggle.CheckedChanged += (_, _) =>
            {
                ApplyOverlayParts();
                _doc.Views.Redraw();
            };
        }

        foreach ((string label, CurveLabelContent _) in LabelContents)
            _labelContent.Items.Add(label);
        _labelContent.SelectedIndex = 0;
        _labelContent.ToolTip = "What a placed label reads.";
        var labelButton = UiControls.Button("Label", (_, _) => LabelCurve(), "Place a text label at a picked point on the curve.", UiButtonRole.Inline);

        return new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = UiMetrics.SpaceSmall,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new AdaptiveControlGroup(UiMetrics.SpaceMedium, _showRibbon, _showLabels, _showEvents, _showTerrain),
                new AdaptiveControlGroup(UiMetrics.SpaceSmall, _labelContent, labelButton)
            }
        };
    }

    /// <summary>A titled block: uppercase heading, hairline rule, body. The panel's only structural device.</summary>
    private static Control BuildSection(string title, Control body, Control? trailing)
    {
        Label heading = UiControls.Label(title.ToUpperInvariant(), UiLabelRole.Section);
        var headingRow = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = UiMetrics.SpaceMedium,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items = { new StackLayoutItem(heading, true) }
        };
        if (trailing != null)
            headingRow.Items.Add(trailing);

        var rule = new Panel { Height = 1, BackgroundColor = UiTheme.RampDivider };
        return new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = UiMetrics.SpaceSmall,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items = { headingRow, rule, body }
        };
    }

    // ── Content ────────────────────────────────────────────────────────────

    private void RebuildChecks()
    {
        _checks.Clear();
        _limits.Clear();
        if (_analysis == null)
            return;

        foreach (CurveReviewCheckResult result in _analysis.Checks)
        {
            _checks.AddRow(BuildCheckRow(result));
            _limits.AddRow(BuildLimitRow(result));
        }

        _checks.Create();
        _limits.Create();
    }

    /// <summary>One scannable line: state chip, rule, what the curve actually does, what it may do.</summary>
    private Control BuildCheckRow(CurveReviewCheckResult result)
    {
        Color chipColor = !result.IsAvailable || result.Mode == CurveReviewRuleMode.Off
            ? UiTheme.MutedText
            : result.IsWarning
                ? Color.FromArgb(232, 84, 64)
                : result.IsViolation
                    ? UiTheme.ActiveBadge
                    : Color.FromArgb(86, 196, 116);
        var chip = new Panel { Size = new Size(8, 8), BackgroundColor = chipColor };
        var chipCell = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            VerticalContentAlignment = VerticalAlignment.Center,
            Width = 12,
            Items = { chip }
        };

        Label actual = UiControls.Label(
            result.IsAvailable ? FormatActual(result) : "n/a",
            result.IsWarning ? UiLabelRole.Warning : UiLabelRole.Body);
        Label limit = UiControls.Label(
            result.Mode == CurveReviewRuleMode.Off ? "off" : $"limit {FormatThreshold(result)}",
            UiLabelRole.Meta);

        var row = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = UiMetrics.SpaceSmall,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items =
            {
                chipCell,
                new StackLayoutItem(UiControls.Label(RuleLabel(result.Kind)), true),
                actual,
                limit
            }
        };

        if (!result.IsViolation)
            return row;

        // A failing check is the one thing in the panel worth jumping to, so make the whole line the jump.
        row.Cursor = Cursors.Pointer;
        row.ToolTip = "Click to zoom to the first occurrence.";
        row.MouseDown += (_, _) => ZoomTo(result.Kind);
        return row;
    }

    private Control BuildLimitRow(CurveReviewCheckResult result)
    {
        var mode = new DropDown { Width = UiMetrics.Chs(8) };
        mode.Items.Add("Off");
        mode.Items.Add("Report");
        mode.Items.Add("Warn");
        mode.SelectedIndex = (int)result.Mode;
        mode.SelectedIndexChanged += (_, _) =>
        {
            SetRuleMode(result.Kind, (CurveReviewRuleMode)Math.Clamp(mode.SelectedIndex, 0, 2));
            SaveRules();
        };

        var threshold = new TextBox
        {
            Text = CurveReviewThresholdInput.Format(result.Kind, result.Threshold, SlopeUnitPreference.Current),
            Width = UiMetrics.NumericField,
            Height = UiMetrics.CompactControlHeight
        };
        UiControls.StyleInput(threshold);
        BindCommitted(threshold, () =>
        {
            // Unparseable or empty text reverts the field instead of committing a zero limit.
            SlopeAnalyzer.SlopeUnit slopeUnit = SlopeUnitPreference.Current;
            if (!CurveReviewThresholdInput.TryParse(result.Kind, threshold.Text, slopeUnit, out double value))
            {
                threshold.Text = CurveReviewThresholdInput.Format(result.Kind, GetRuleThreshold(result.Kind), slopeUnit);
                return;
            }

            SetRuleThreshold(result.Kind, value);
            SaveRules();
        });

        return new AdaptiveControlGroup(
            UiMetrics.SpaceSmall,
            UiControls.Label(RuleLabel(result.Kind), UiLabelRole.Meta),
            mode,
            threshold,
            UiControls.Label(RuleUnit(result.Kind), UiLabelRole.Meta));
    }

    private void RebuildMeasurements()
    {
        if (_analysis == null)
        {
            _measurements.Content = null;
            return;
        }

        CurveReviewAnalysis a = _analysis;
        string unit = _units.Abbreviation;
        double coverage = a.TerrainSamples + a.TerrainMisses > 0
            ? 100.0 * a.TerrainSamples / (a.TerrainSamples + a.TerrainMisses)
            : double.NaN;

        var entries = new List<(string Label, string Value, string Note)>
        {
            ("Plan length", Number(a.PlanLength, unit), string.Empty),
            ("3D length", Number(a.Length3d, unit), string.Empty),
            ("Start elevation", Number(a.StartElevation, unit), Station(0.0)),
            ("End elevation", Number(a.EndElevation, unit), Station(a.PlanLength)),
            ("Minimum grade", Percent(a.MinimumGrade), string.Empty),
            ("Maximum grade", Percent(a.MaximumGrade), string.Empty),
            ("Average grade", Percent(a.AverageGrade), "weighted"),
            ("Steepest grade", Percent(a.SteepestGrade), Station(a.SteepestGradeStation)),
            ("Reversals", a.ReversalCount.ToString(CultureInfo.CurrentCulture), "crest / sag"),
            ("Kinks (G1)", a.KinkCount.ToString(CultureInfo.CurrentCulture), string.Empty),
            ("Min plan radius", a.PlanCornerCount > 0
                ? "corner"
                : double.IsFinite(a.MinimumPlanRadius) ? Number(a.MinimumPlanRadius, unit) : "straight",
                double.IsFinite(a.MinimumPlanRadiusStation) ? Station(a.MinimumPlanRadiusStation) : string.Empty),
            ("Terrain coverage", double.IsFinite(coverage) ? Invariant($"{coverage:F0}%") : "no terrain",
                a.TerrainMisses > 0 ? Invariant($"{a.TerrainMisses} misses") : string.Empty),
            ("Maximum fill", a.HasTerrain ? Number(a.MaximumFill, unit) : "n/a",
                a.HasTerrain && a.MaximumFill > 0.0 ? Station(a.MaximumFillStation) : string.Empty),
            ("Maximum cut", a.HasTerrain ? Number(a.MaximumCut, unit) : "n/a",
                a.HasTerrain && a.MaximumCut > 0.0 ? Station(a.MaximumCutStation) : string.Empty)
        };

        int half = (entries.Count + 1) / 2;
        _measurements.Content = new AdaptiveColumns(
            UiMetrics.SpaceLarge,
            UiMetrics.Chs(24),
            BuildMeasurementColumn(entries.Take(half)),
            BuildMeasurementColumn(entries.Skip(half)));
    }

    private static Control BuildMeasurementColumn(IEnumerable<(string Label, string Value, string Note)> entries)
    {
        var column = new StackLayout
        {
            Orientation = Orientation.Vertical,
            Spacing = UiMetrics.SpaceXSmall,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        foreach ((string label, string value, string note) in entries)
        {
            var row = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = UiMetrics.SpaceSmall,
                VerticalContentAlignment = VerticalAlignment.Center,
                Items = { new StackLayoutItem(UiControls.Label(label, UiLabelRole.Meta), true), UiControls.Label(value) }
            };
            if (!string.IsNullOrEmpty(note))
                row.Items.Add(UiControls.Label(note, UiLabelRole.Meta));
            column.Items.Add(row);
        }

        return column;
    }

    private void RebuildEvents()
    {
        _events.Clear();
        if (_analysis == null || _analysis.Events.Count == 0)
        {
            _events.AddRow(UiControls.Label("No profile events.", UiLabelRole.Meta));
            _events.Create();
            return;
        }

        foreach (CurveReviewEvent item in _analysis.Events.Take(24))
        {
            Color color = item.Kind switch
            {
                CurveReviewEventKind.VerticalBreak => Color.FromArgb(196, 96, 32),
                CurveReviewEventKind.SharpRadius or CurveReviewEventKind.PlanCorner => Color.FromArgb(232, 84, 64),
                CurveReviewEventKind.TerrainGap => UiTheme.MutedText,
                _ => UiTheme.ActiveBadge
            };
            var chip = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                VerticalContentAlignment = VerticalAlignment.Center,
                Width = 12,
                Items = { new Panel { Size = new Size(8, 8), BackgroundColor = color } }
            };
            var row = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = UiMetrics.SpaceSmall,
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Pointer,
                ToolTip = "Click to zoom to this event.",
                Items =
                {
                    UiControls.Label(Invariant($"Sta {item.Station:F1}"), UiLabelRole.Meta),
                    chip,
                    new StackLayoutItem(UiControls.Label(DescribeEvent(item.Kind)), true),
                    UiControls.Label(item.Label, UiLabelRole.Meta)
                }
            };
            Point3d point = item.Point;
            row.MouseDown += (_, _) => ZoomTo(point);
            _events.AddRow(row);
        }

        _events.Create();
    }

    // ── Review lifecycle ───────────────────────────────────────────────────

    private void OnTimer(object? sender, EventArgs e)
    {
        if (_closing || _labelling)
            return;
        RhinoObject? obj = _doc.Objects.FindId(_objectId);
        if (obj == null)
        {
            Close();
            return;
        }

        if (obj.RuntimeSerialNumber != _sourceSerial)
        {
            _sourceSerial = obj.RuntimeSerialNumber;
            RefreshReview(true);
            return;
        }

        RefreshReview(false);
    }

    private void RefreshReview(bool force)
    {
        if (_closing || _labelling || _refreshing)
            return;
        _refreshing = true;
        try
        {
            Mesh? terrain = CurveReviewService.PeekTerrainMesh(_doc);
            int terrainKey = terrain == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(terrain);
            string fingerprint = $"{_objectId:N}|{_sourceSerial}|{terrainKey}";
            if (!force && fingerprint == _fingerprint)
                return;
            _fingerprint = fingerprint;

            var curve = _doc.Objects.FindId(_objectId)?.Geometry as Curve;
            string? error = null;
            _analysis = curve == null ? null : CurveReviewService.Evaluate(_doc, curve, _rules, out error);
            _profile.SetAnalysis(_analysis);
            _conduit.SetAnalysis(_analysis);
            if (_analysis == null)
            {
                _objectLabel.Text = "Curve unavailable";
                _status.Text = error ?? "No usable profile";
                _status.TextColor = UiTheme.WarningText;
                _checks.Clear();
                _limits.Clear();
                _measurements.Content = null;
                _events.Clear();
                _doc.Views.Redraw();
                return;
            }

            _objectLabel.Text = $"Curve {_objectId.ToString("D")[..8]}";
            _status.Text = CurveReviewService.DescribeStatus(_analysis);
            _status.TextColor = _analysis.WarningCount > 0 ? UiTheme.WarningText : UiTheme.PrimaryText;
            RebuildChecks();
            RebuildMeasurements();
            RebuildEvents();
            _doc.Views.Redraw();
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ChangeObject()
    {
        SetInteractive(false);
        try
        {
            var getObject = new GetObject();
            getObject.SetCommandPrompt("Select curve to inspect");
            getObject.GeometryFilter = ObjectType.Curve;
            if (getObject.Get() == GetResult.Object)
                SetObject(getObject.Object(0).ObjectId);
        }
        finally
        {
            SetInteractive(true);
        }
    }

    private void LabelCurve()
    {
        if (_labelling || _analysis == null)
            return;
        _labelling = true;
        SetInteractive(false);
        try
        {
            CurveLabelContent content = LabelContents[Math.Clamp(_labelContent.SelectedIndex, 0, LabelContents.Length - 1)].Content;
            CurveReviewLabeller.Run(_doc, _objectId, _analysis, content);
        }
        finally
        {
            _labelling = false;
            SetInteractive(true);
            _doc.Views.Redraw();
        }
    }

    private void ApplyOverlayParts()
    {
        CurveReviewOverlayParts parts = CurveReviewOverlayParts.None;
        if (_showRibbon.Checked == true) parts |= CurveReviewOverlayParts.Ribbon;
        if (_showLabels.Checked == true) parts |= CurveReviewOverlayParts.Labels;
        if (_showEvents.Checked == true) parts |= CurveReviewOverlayParts.Events;
        if (_showTerrain.Checked == true) parts |= CurveReviewOverlayParts.Terrain;
        _conduit.Parts = parts;
    }

    private void Shutdown()
    {
        _closing = true;
        _timer.Stop();
        _conduit.Enabled = false;
        _conduit.SetAnalysis(null);
        _doc.Views.Redraw();
    }

    private void SetInteractive(bool enabled)
    {
        if (Content != null)
            Content.Enabled = enabled;
    }

    // ── Rules ──────────────────────────────────────────────────────────────

    private void SaveRules()
    {
        CurveReviewRuleStore.Save(_doc, _rules);
        _fingerprint = string.Empty;
        if (_ruleRefreshPending)
            return;

        // Rebuilding the rule rows from inside one of their own event handlers destroys the control that
        // raised the event. Let the Eto event unwind first, then rebuild.
        _ruleRefreshPending = true;
        Application.Instance.AsyncInvoke(() =>
        {
            _ruleRefreshPending = false;
            if (_closing)
                return;
            try
            {
                RefreshReview(true);
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"MoleHill could not refresh Inspect Curve rules: {ex.Message}");
            }
        });
    }

    private void SetRuleMode(CurveReviewRuleKind kind, CurveReviewRuleMode mode)
    {
        switch (kind)
        {
            case CurveReviewRuleKind.MaximumGrade: _rules.MaximumGradeMode = mode; break;
            case CurveReviewRuleKind.MinimumPlanRadius: _rules.MinimumRadiusMode = mode; break;
            case CurveReviewRuleKind.VerticalGradeChange: _rules.VerticalBreakMode = mode; break;
            case CurveReviewRuleKind.TerrainCoverage: _rules.TerrainCoverageMode = mode; break;
        }
    }

    private double GetRuleThreshold(CurveReviewRuleKind kind) => kind switch
    {
        CurveReviewRuleKind.MaximumGrade => _rules.MaximumGradePercent,
        CurveReviewRuleKind.MinimumPlanRadius => _rules.MinimumRadius,
        CurveReviewRuleKind.VerticalGradeChange => _rules.VerticalBreakThresholdPercent,
        CurveReviewRuleKind.TerrainCoverage => _rules.MinimumTerrainCoveragePercent,
        _ => 0.0
    };

    private void SetRuleThreshold(CurveReviewRuleKind kind, double value)
    {
        if (!double.IsFinite(value))
            return;
        switch (kind)
        {
            case CurveReviewRuleKind.MaximumGrade: _rules.MaximumGradePercent = Math.Max(value, 0.01); break;
            case CurveReviewRuleKind.MinimumPlanRadius: _rules.MinimumRadius = Math.Max(value, _doc.ModelAbsoluteTolerance); break;
            case CurveReviewRuleKind.VerticalGradeChange: _rules.VerticalBreakThresholdPercent = Math.Max(value, 0.01); break;
            case CurveReviewRuleKind.TerrainCoverage: _rules.MinimumTerrainCoveragePercent = Math.Clamp(value, 0.0, 100.0); break;
        }
    }

    // ── Navigation ─────────────────────────────────────────────────────────

    private void ZoomTo(CurveReviewRuleKind kind)
    {
        if (_analysis == null)
            return;
        Point3d? point = kind switch
        {
            CurveReviewRuleKind.MaximumGrade => _analysis.GradeExceedances.FirstOrDefault()?.PeakPoint,
            CurveReviewRuleKind.MinimumPlanRadius => _analysis.RadiusViolations.FirstOrDefault()?.PeakPoint
                ?? CurveReviewAnalysis.FirstEventPoint(_analysis.Events, CurveReviewEventKind.PlanCorner),
            CurveReviewRuleKind.VerticalGradeChange => CurveReviewAnalysis.FirstEventPoint(_analysis.Events, CurveReviewEventKind.VerticalBreak),
            CurveReviewRuleKind.TerrainCoverage => CurveReviewAnalysis.FirstEventPoint(_analysis.Events, CurveReviewEventKind.TerrainGap),
            _ => null
        };
        if (point.HasValue && point.Value.IsValid)
            ZoomTo(point.Value);
    }

    private void ZoomTo(Point3d point)
    {
        var view = _doc.Views.ActiveView;
        if (view == null || !point.IsValid)
            return;
        double radius = Math.Max((_analysis?.Bounds.Diagonal.Length ?? 1.0) * 0.05, _doc.ModelAbsoluteTolerance * 20.0);
        view.ActiveViewport.ZoomBoundingBox(new BoundingBox(
            point - new Vector3d(radius, radius, radius),
            point + new Vector3d(radius, radius, radius)));
        view.Redraw();
    }

    // ── Formatting ─────────────────────────────────────────────────────────

    private static void BindCommitted(TextBox box, Action commit)
    {
        box.LostFocus += (_, _) => commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Keys.Enter)
                return;
            commit();
            e.Handled = true;
        };
    }

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Number(double value, string unit) =>
        double.IsFinite(value) ? Invariant($"{value:F2} {unit}").TrimEnd() : "n/a";

    private static string Percent(double value) => double.IsFinite(value) ? Invariant($"{value:+0.00;-0.00;0.00}%") : "n/a";

    private static string Station(double station) => double.IsFinite(station) ? Invariant($"Sta {station:F0}") : string.Empty;

    private static string DescribeEvent(CurveReviewEventKind kind) => kind switch
    {
        CurveReviewEventKind.VerticalBreak => "Vertical break",
        CurveReviewEventKind.Crest => "Crest",
        CurveReviewEventKind.Sag => "Sag",
        CurveReviewEventKind.SharpRadius => "Sharp radius",
        CurveReviewEventKind.PlanCorner => "Plan corner",
        CurveReviewEventKind.TerrainGap => "Terrain gap",
        _ => "Profile event"
    };

    private static string RuleLabel(CurveReviewRuleKind kind) => kind switch
    {
        CurveReviewRuleKind.MaximumGrade => "Maximum grade",
        CurveReviewRuleKind.MinimumPlanRadius => "Minimum plan radius",
        CurveReviewRuleKind.VerticalGradeChange => "Vertical grade change",
        CurveReviewRuleKind.TerrainCoverage => "Terrain coverage",
        _ => kind.ToString()
    };

    private static string FormatActual(CurveReviewCheckResult result) => result.Kind switch
    {
        CurveReviewRuleKind.MaximumGrade => SlopeInput.FormatWithUnit(result.Actual / 100.0, SlopeUnitPreference.Current),
        CurveReviewRuleKind.MinimumPlanRadius => result.HasPlanCorners
            ? "corner"
            : double.IsFinite(result.Actual) ? Invariant($"R {result.Actual:F1}") : "straight",
        CurveReviewRuleKind.VerticalGradeChange => Invariant($"Δ {result.Actual:F1}%"),
        CurveReviewRuleKind.TerrainCoverage => Invariant($"{result.Actual:F0}%"),
        _ => Invariant($"{result.Actual:F2}")
    };

    private string FormatThreshold(CurveReviewCheckResult result) => result.Kind switch
    {
        CurveReviewRuleKind.MaximumGrade => SlopeInput.FormatWithUnit(result.Threshold / 100.0, SlopeUnitPreference.Current),
        CurveReviewRuleKind.MinimumPlanRadius => Invariant($"{result.Threshold:F0} {_units.Abbreviation}").TrimEnd(),
        CurveReviewRuleKind.VerticalGradeChange => Invariant($"{result.Threshold:F0}% pts"),
        CurveReviewRuleKind.TerrainCoverage => Invariant($"{result.Threshold:F0}%"),
        _ => Invariant($"{result.Threshold:F2}")
    };

    private string RuleUnit(CurveReviewRuleKind kind) => kind switch
    {
        CurveReviewRuleKind.MaximumGrade => SlopeInput.Suffix(SlopeUnitPreference.Current),
        CurveReviewRuleKind.MinimumPlanRadius => _units.Abbreviation,
        CurveReviewRuleKind.VerticalGradeChange => "% pts",
        CurveReviewRuleKind.TerrainCoverage => "%",
        _ => string.Empty
    };
}
