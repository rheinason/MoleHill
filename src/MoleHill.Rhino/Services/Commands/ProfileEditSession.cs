// The live part of mhEditProfile: a get loop that drags bends, types grades and elevations, and redraws the section.
using System.Globalization;
using MoleHill.Core.Analysis;
using MoleHill.Shared;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using Rhino.Input;
using Rhino.Input.Custom;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Runs one profile edit as an ordinary Rhino command loop. The section is a preview (<see
/// cref="ProfileEditConduit"/>) beside the curve; the mouse drags its bends (press on the line to add one),
/// the command line types grades and elevations, and Ctrl+Z or Undo steps back one edit. Nothing touches the
/// document until Enter, which the command turns into one Rhino undo step; Esc leaves the curve as it was.
///
/// <para>Inside a get, Rhino blocks its own Undo accelerator, so Ctrl+Z here means the session's undo and
/// there is only ever one Undo in play (the reason the old in-panel profile editor was removed was two).</para>
/// </summary>
internal sealed class ProfileEditSession : IDisposable
{
    /// <summary>How close the cursor must come to a bend or the line to grab it, in pixels.</summary>
    private const double GrabPixels = 10.0;

    private static readonly string[] MoveLabels = { "Vertical", "Free" };
    private static readonly string[] HoldLabels = { "Start", "End" };
    internal static readonly string[] GroundLabels = { "ActiveTerrain", "Objects", "None" };
    internal static readonly string[] FitLabels = { "EditPoints", "Exact" };

    private const string ExaggerationKey = "MoleHill.EditProfile.Exaggeration";
    private const string MoveKey = "MoleHill.EditProfile.Move";
    private const string HoldKey = "MoleHill.EditProfile.Hold";

    private readonly RhinoDoc _doc;
    private readonly ProfileEditor _editor;
    private readonly double[] _path;
    private readonly double[] _pathStations;
    private readonly ModelUnitContext _units;
    private readonly Func<int, ProfileGroundLine?> _loadGround;
    private readonly ProfileEditConduit _conduit = new();
    private readonly double? _maxGrade;
    private readonly double? _breakLimit;
    private readonly bool _smooth;
    private readonly double _gridStep;
    private readonly double _stationStep;
    private readonly int _elevationDecimals;
    private ProfileGroundLine _ground;
    private LongSectionFrame _frame;
    private readonly double _planeZ;
    private double _exaggeration;
    private int _moveIndex;
    private int _holdIndex;

    private int _selected = -1;
    private int _hotBend = -1;
    private int _hotSegment = -1;
    private int _dragged = -1;
    private double? _cursorStation;
    private string[]? _readout;

    public ProfileEditSession(
        RhinoDoc doc,
        ProfileEditor editor,
        double[] path,
        ModelUnitContext units,
        ProfileGroundLine ground,
        int groundIndex,
        Func<int, ProfileGroundLine?> loadGround,
        bool smooth,
        int fitIndex,
        BoundingBox curveBox,
        Vector2d? placement,
        double? maxGrade,
        double? breakLimit)
    {
        _doc = doc;
        _editor = editor;
        _path = path;
        _pathStations = LongSectionProfile.PlanStations(path);
        _units = units;
        _ground = ground;
        GroundIndex = groundIndex;
        _loadGround = loadGround;
        _smooth = smooth;
        FitIndex = fitIndex;
        _maxGrade = maxGrade;
        _breakLimit = breakLimit;
        _exaggeration = Math.Clamp(CommandOptionCache.GetValue(ExaggerationKey, 5.0), 0.1, 1000.0);
        _moveIndex = Math.Clamp(CommandOptionCache.GetValue(MoveKey, 0), 0, MoveLabels.Length - 1);
        _holdIndex = Math.Clamp(CommandOptionCache.GetValue(HoldKey, 0), 0, HoldLabels.Length - 1);

        (double low, double high) = ElevationExtent();
        _gridStep = AnalysisRange.NiceStep(Math.Max(high - low, units.FromMeters(0.1)) / 6.0);
        _stationStep = AnalysisRange.NiceStep(_editor.Length / 8.0);
        _elevationDecimals = Math.Clamp((int)Math.Ceiling(-Math.Log10(_gridStep)) + 1, 0, 4);
        double datum = Math.Floor(low / _gridStep) * _gridStep;

        // Below the curve in plan, station 0 under its left edge, unless this curve was placed before.
        double height = (Math.Ceiling(high / _gridStep) * _gridStep - datum) * _exaggeration;
        double gap = Math.Max(Math.Max(_editor.Length * 0.05, height * 0.25), curveBox.Diagonal.Length * 0.05);
        Vector2d offset = placement ?? new Vector2d(0.0, -gap - height);
        _frame = new LongSectionFrame(curveBox.Min.X + offset.X, curveBox.Min.Y + offset.Y, datum, _exaggeration);
        _planeZ = curveBox.Min.Z;
        CurveBoxMin = new Point2d(curveBox.Min.X, curveBox.Min.Y);
    }

    public int GroundIndex { get; private set; }

    public int FitIndex { get; private set; }

    private Point2d CurveBoxMin { get; }

    /// <summary>Where the section sits relative to the curve's plan extents, remembered for the next edit of it.</summary>
    public Vector2d Placement => new(_frame.OriginX - CurveBoxMin.X, _frame.OriginY - CurveBoxMin.Y);

    /// <summary>Runs until Enter (true: apply) or Esc (false).</summary>
    public bool Run()
    {
        _conduit.Enabled = true;
        Refresh();
        EnsureVisible();

        while (true)
        {
            var getter = new ProfileGetPoint(this);
            getter.SetCommandPrompt(Prompt());
            getter.AcceptNothing(true);
            getter.AcceptUndo(true);
            getter.PermitObjectSnap(false);
            getter.PermitConstraintOptions(false);

            var exaggeration = new OptionDouble(_exaggeration, 0.1, 1000.0);
            int optElevation = getter.AddOption("Elevation");
            int optGrade = getter.AddOption("Grade");
            int optDelete = getter.AddOption("Delete");
            int optUndo = _editor.CanUndo ? getter.AddOption("Undo") : -1;
            int optRedo = _editor.CanRedo ? getter.AddOption("Redo") : -1;
            int optExaggeration = getter.AddOptionDouble("Exaggeration", ref exaggeration);
            int optMove = getter.AddOptionList("Move", MoveLabels, _moveIndex);
            int optGround = getter.AddOptionList("Ground", GroundLabels, GroundIndex);
            int optPlace = getter.AddOption("Place");
            int optFit = _smooth ? getter.AddOptionList("Fit", FitLabels, FitIndex) : -1;

            GetResult result = getter.Get(true);
            switch (result)
            {
                case GetResult.Point:
                    getter.FinishGesture();
                    Refresh();
                    continue;

                case GetResult.Undo:
                    getter.FinishGesture();
                    StepBack(redo: false);
                    continue;

                case GetResult.Nothing:
                    return true;

                case GetResult.Option:
                    break;

                default:
                    return false;
            }

            int option = getter.OptionIndex();
            if (option == optElevation)
                TypeElevation();
            else if (option == optGrade)
                TypeGrade();
            else if (option == optDelete)
                DeleteBend();
            else if (option == optUndo)
                StepBack(redo: false);
            else if (option == optRedo)
                StepBack(redo: true);
            else if (option == optExaggeration)
                SetExaggeration(exaggeration.CurrentValue);
            else if (option == optMove)
            {
                _moveIndex = getter.Option().CurrentListOptionIndex;
                CommandOptionCache.SetValue(MoveKey, _moveIndex);
            }
            else if (option == optGround)
                ChangeGround(getter.Option().CurrentListOptionIndex);
            else if (option == optPlace)
                Place();
            else if (option == optFit)
                FitIndex = getter.Option().CurrentListOptionIndex;

            Refresh();
        }
    }

    public void Dispose()
    {
        _conduit.Enabled = false;
        _doc.Views.Redraw();
    }

    // ── Prompt and options ─────────────────────────────────────────────────

    private string Prompt()
    {
        string edits = _editor.HasChanges ? "Enter applies, Esc cancels" : "Enter or Esc leaves the curve as it is";
        return $"Drag a bend (Shift: {(_moveIndex == 0 ? "also along" : "only up and down")}, Ctrl: no snap), or press on the line to add one. {edits}";
    }

    private void StepBack(bool redo)
    {
        bool done = redo ? _editor.Redo() : _editor.Undo();
        if (!done)
            RhinoApp.WriteLine(redo ? "Nothing to redo." : "Nothing to undo in this edit.");
        if (_selected >= _editor.Bends.Count)
            _selected = -1;
        Refresh();
    }

    private void TypeElevation()
    {
        int index = _selected >= 0 ? _selected : PickBend("Select a bend to type its elevation", interiorOnly: false);
        if (index < 0)
            return;
        _selected = index;
        Refresh();

        ProfileBend bend = _editor.Bends[index];
        var get = new GetNumber();
        get.SetCommandPrompt($"Elevation of the bend at station {FormatStation(bend.Station)}");
        get.SetDefaultNumber(Math.Round(bend.Elevation, _elevationDecimals + 1));
        get.AcceptNothing(true);
        if (get.Get() == GetResult.Number && double.IsFinite(get.Number()))
            _editor.SetElevation(index, get.Number());
    }

    private void TypeGrade()
    {
        int segment = PickSegment("Select a segment to type its grade");
        if (segment < 0)
            return;
        _hotSegment = segment;
        Refresh();

        SlopeAnalyzer.SlopeUnit unit = SlopeUnitPreference.Current;
        try
        {
            while (true)
            {
                ProfileBend a = _editor.Bends[segment], b = _editor.Bends[segment + 1];
                var get = new GetString();
                get.SetCommandPrompt(
                    $"Grade from station {FormatStation(a.Station)} to {FormatStation(b.Station)}, now {Grade(_editor.Grade(segment), unit)} " +
                    "(rising along the curve; negative falls; any unit: 2%, 20prom, 1:50, 1.2deg)");
                get.AcceptNothing(true);
                int optHold = get.AddOptionList("Hold", HoldLabels, _holdIndex);
                GetResult result = get.Get();
                if (result == GetResult.Option && get.OptionIndex() == optHold)
                {
                    _holdIndex = get.Option().CurrentListOptionIndex;
                    CommandOptionCache.SetValue(HoldKey, _holdIndex);
                    continue;
                }

                if (result != GetResult.String)
                    return;
                string text = get.StringResult();
                if (!SlopeInput.TryParse(text, unit, out double grade))
                {
                    RhinoApp.WriteLine($"'{text}' is not a grade. Type it in any unit: 2%, 20prom, 1:50 or 1.2deg.");
                    continue;
                }

                // A ratio typed as 1:50 has no sign; the sign of the text says whether it falls.
                if (text.TrimStart().StartsWith("-", StringComparison.Ordinal) && grade > 0.0)
                    grade = -grade;
                _editor.SetGrade(segment, grade, holdStart: _holdIndex == 0);
                return;
            }
        }
        finally
        {
            _hotSegment = -1;
        }
    }

    private void DeleteBend()
    {
        int index = _selected > 0 && _selected < _editor.Bends.Count - 1
            ? _selected
            : PickBend("Select a bend to delete (not an end)", interiorOnly: true);
        if (index < 0)
            return;
        if (_editor.Delete(index))
            _selected = -1;
        else
            RhinoApp.WriteLine("The end bends cannot be deleted.");
    }

    private void SetExaggeration(double value)
    {
        if (!double.IsFinite(value) || value <= 0.0 || Math.Abs(value - _exaggeration) < 1e-12)
            return;
        _exaggeration = value;
        _frame = _frame with { Exaggeration = value };
        CommandOptionCache.SetValue(ExaggerationKey, value);
        EnsureVisible();
    }

    private void ChangeGround(int index)
    {
        ProfileGroundLine? ground = _loadGround(index);
        if (ground == null)
            return;
        _ground = ground;
        GroundIndex = index;
    }

    /// <summary>Moves the section: the user picks where its lower-left corner goes, with the outline following the cursor.</summary>
    private void Place()
    {
        ProfileEditView view = BuildView();
        BoundingBox box = view.DrawingBounds();
        Point3d corner = view.ToWorld(0.0, view.GridLow);
        var get = new GetPoint();
        get.SetCommandPrompt("Place the profile's lower-left corner");
        get.SetBasePoint(corner, true);
        get.DynamicDraw += (_, e) =>
        {
            Vector3d move = e.CurrentPoint - corner;
            move.Z = 0.0;
            var outline = new Polyline(new[]
            {
                new Point3d(box.Min.X, box.Min.Y, _planeZ) + move, new Point3d(box.Max.X, box.Min.Y, _planeZ) + move,
                new Point3d(box.Max.X, box.Max.Y, _planeZ) + move, new Point3d(box.Min.X, box.Max.Y, _planeZ) + move,
                new Point3d(box.Min.X, box.Min.Y, _planeZ) + move,
            });
            e.Display.DrawPolyline(outline, System.Drawing.Color.FromArgb(255, 150, 0), 2);
        };
        if (get.Get() != GetResult.Point)
            return;
        Vector3d delta = get.Point() - corner;
        _frame = _frame with { OriginX = _frame.OriginX + delta.X, OriginY = _frame.OriginY + delta.Y };
    }

    // ── Picking ────────────────────────────────────────────────────────────

    private int PickBend(string prompt, bool interiorOnly)
    {
        var get = new ProfilePickPoint(this, bends: true);
        get.SetCommandPrompt(prompt);
        get.PermitObjectSnap(false);
        while (get.Get() == GetResult.Point)
        {
            int index = get.Resolve();
            if (index >= 0 && (!interiorOnly || (index > 0 && index < _editor.Bends.Count - 1)))
                return index;
        }

        return -1;
    }

    private int PickSegment(string prompt)
    {
        var get = new ProfilePickPoint(this, bends: false);
        get.SetCommandPrompt(prompt);
        get.PermitObjectSnap(false);
        while (get.Get() == GetResult.Point)
        {
            int index = get.Resolve();
            if (index >= 0)
                return index;
        }

        return -1;
    }

    /// <summary>The cursor on the section's plane, seen through the viewport (so a tilted view still lands right).</summary>
    internal bool TryPlanePoint(RhinoViewport viewport, System.Drawing.Point window, out Point3d point)
    {
        point = Point3d.Unset;
        if (!viewport.GetFrustumLine(window.X, window.Y, out Line ray))
            return false;
        if (!Intersection.LinePlane(ray, new Plane(new Point3d(0.0, 0.0, _planeZ), Vector3d.ZAxis), out double t))
            return false;
        point = ray.PointAt(t);
        return true;
    }

    private static double PixelsToWorld(RhinoViewport viewport, Point3d at, double pixels)
    {
        viewport.GetWorldToScreenScale(at, out double pixelsPerUnit);
        return pixelsPerUnit > 1e-12 ? pixels / pixelsPerUnit : pixels;
    }

    internal int HitBend(RhinoViewport viewport, Point3d point)
    {
        double radius = PixelsToWorld(viewport, point, GrabPixels);
        int best = -1;
        double bestDistance = radius;
        for (int i = 0; i < _editor.Bends.Count; i++)
        {
            (double x, double y) = _frame.ToDrawing(_editor.Bends[i].Station, _editor.Bends[i].Elevation);
            double distance = Math.Sqrt((x - point.X) * (x - point.X) + (y - point.Y) * (y - point.Y));
            if (distance <= bestDistance)
            {
                best = i;
                bestDistance = distance;
            }
        }

        return best;
    }

    internal int HitSegment(RhinoViewport viewport, Point3d point)
    {
        double radius = PixelsToWorld(viewport, point, GrabPixels);
        int best = -1;
        double bestDistance = radius;
        for (int i = 0; i < _editor.Bends.Count - 1; i++)
        {
            (double ax, double ay) = _frame.ToDrawing(_editor.Bends[i].Station, _editor.Bends[i].Elevation);
            (double bx, double by) = _frame.ToDrawing(_editor.Bends[i + 1].Station, _editor.Bends[i + 1].Elevation);
            var segment = new Line(ax, ay, _planeZ, bx, by, _planeZ);
            double distance = segment.DistanceTo(new Point3d(point.X, point.Y, _planeZ), true);
            if (distance <= bestDistance)
            {
                best = i;
                bestDistance = distance;
            }
        }

        return best;
    }

    // ── Gestures (called on the UI thread by the getter) ───────────────────

    /// <summary>A press: grab the bend under the cursor, or add one where the line is and grab that.</summary>
    internal void Press(RhinoViewport viewport, Point3d point)
    {
        int bend = HitBend(viewport, point);
        if (bend < 0)
        {
            int segment = HitSegment(viewport, point);
            if (segment < 0)
            {
                _selected = -1;
                Refresh();
                return;
            }

            (double station, _) = _frame.FromDrawing(point.X, point.Y);
            _editor.BeginGesture();
            bend = _editor.Insert(station);
            if (bend < 0)
            {
                _editor.EndGesture();
                return;
            }
        }
        else
        {
            _editor.BeginGesture();
        }

        _dragged = bend;
        _selected = bend;
        Refresh();
    }

    internal bool IsDragging => _dragged >= 0;

    internal void Drag(RhinoViewport viewport, Point3d point, bool shift, bool noSnap)
    {
        if (_dragged < 0)
            return;
        ProfileBend bend = _editor.Bends[_dragged];
        (double station, double elevation) = _frame.FromDrawing(point.X, point.Y);
        bool free = (_moveIndex == 1) ^ shift;
        if (!free)
            station = bend.Station;
        ProfileBend placed = _editor.Move(_dragged, station, elevation);

        ProfileSnapResult snap = new(placed.Elevation, ProfileSnapKind.None);
        if (!noSnap)
        {
            double reach = PixelsToWorld(viewport, point, GrabPixels) / _exaggeration;
            ProfileBend? previous = _dragged > 0 ? _editor.Bends[_dragged - 1] : null;
            ProfileBend? next = _dragged < _editor.Bends.Count - 1 ? _editor.Bends[_dragged + 1] : null;
            snap = ProfileSnap.Snap(placed.Station, elevation, reach, previous, next, _ground.ElevationAt(placed.Station), SlopeUnitPreference.Current);
            placed = _editor.Move(_dragged, placed.Station, snap.Elevation);
        }

        _cursorStation = placed.Station;
        _readout = Readout(_dragged, snap);
        Refresh();
    }

    internal void Hover(RhinoViewport viewport, Point3d point)
    {
        int bend = HitBend(viewport, point);
        int segment = bend < 0 ? HitSegment(viewport, point) : -1;
        (double station, double elevation) = _frame.FromDrawing(point.X, point.Y);
        double? cursor = station >= 0.0 && station <= _editor.Length ? station : null;
        if (bend == _hotBend && segment == _hotSegment && cursor == _cursorStation)
            return;
        _hotBend = bend;
        _hotSegment = segment;
        _cursorStation = cursor;
        _readout = bend >= 0 ? Readout(bend, new ProfileSnapResult(elevation, ProfileSnapKind.None)) : null;
        Refresh();
    }

    /// <summary>The gesture ended (mouse up): one undo step if it changed anything.</summary>
    internal void Release()
    {
        _editor.EndGesture();
        _dragged = -1;
        _readout = null;
    }

    private string[] Readout(int index, ProfileSnapResult snap)
    {
        SlopeAnalyzer.SlopeUnit unit = SlopeUnitPreference.Current;
        ProfileBend bend = _editor.Bends[index];
        var lines = new List<string>
        {
            $"Station {FormatStation(bend.Station)}   Elevation {FormatElevation(bend.Elevation)}"
        };
        string incoming = index > 0 ? "in " + Grade(_editor.Grade(index - 1), unit) : string.Empty;
        string outgoing = index < _editor.Bends.Count - 1 ? "out " + Grade(_editor.Grade(index), unit) : string.Empty;
        lines.Add(string.Join("   ", new[] { incoming, outgoing }.Where(text => text.Length > 0)));
        string? snapped = snap.Kind switch
        {
            ProfileSnapKind.Ground => "on the ground",
            ProfileSnapKind.Level => snap.FromNext ? "level with the next bend" : "level with the previous bend",
            ProfileSnapKind.Grade => $"{Grade(snap.Grade, unit)} {(snap.FromNext ? "to the next bend" : "from the previous bend")}",
            ProfileSnapKind.Elevation => "round elevation",
            _ => null,
        };
        if (snapped != null)
            lines.Add("Snap: " + snapped);
        return lines.ToArray();
    }

    // ── Drawing ────────────────────────────────────────────────────────────

    private void Refresh()
    {
        _conduit.Show(BuildView());
        _doc.Views.Redraw();
    }

    private ProfileEditView BuildView()
    {
        SlopeAnalyzer.SlopeUnit unit = SlopeUnitPreference.Current;
        ProfileBend[] bends = _editor.Bends.ToArray();
        int segments = bends.Length - 1;
        var steep = new bool[segments];
        var grades = new string[segments];
        for (int i = 0; i < segments; i++)
        {
            double grade = _editor.Grade(i);
            steep[i] = _maxGrade is { } limit && Math.Abs(grade) > limit + 1e-9;
            grades[i] = Grade(grade, unit);
        }

        var breaks = new bool[bends.Length];
        var elevations = new string[bends.Length];
        for (int i = 0; i < bends.Length; i++)
        {
            breaks[i] = _breakLimit is { } limit && Math.Abs(_editor.GradeChange(i)) > limit + 1e-9;
            elevations[i] = FormatElevation(bends[i].Elevation);
        }

        (double low, double high) = ElevationExtent();
        double gridLow = Math.Min(_frame.Datum, Math.Floor(low / _gridStep) * _gridStep);
        double gridHigh = Math.Max(gridLow + _gridStep, Math.Ceiling(high / _gridStep) * _gridStep);
        int lines = (int)Math.Round((gridHigh - gridLow) / _gridStep) + 1;
        string[] gridLabels = Enumerable.Range(0, lines).Select(k => FormatElevation(gridLow + k * _gridStep, _elevationDecimals)).ToArray();

        Point3d? marker = null;
        if (_cursorStation is { } station)
        {
            (double x, double y) = PlanPointAt(station);
            double z = _editor.TakesProfile(station) ? _editor.ElevationAt(station) : _editor.SourceElevationAt(station);
            marker = new Point3d(x, y, z);
        }

        Polyline? preview = null;
        if (_editor.HasChanges)
        {
            double[] applied = _editor.ApplyToPath(_path);
            preview = new Polyline(applied.Length / 3);
            for (int i = 0; i < applied.Length / 3; i++)
                preview.Add(applied[i * 3], applied[i * 3 + 1], applied[i * 3 + 2]);
        }

        int readoutBend = _dragged >= 0 ? _dragged : _hotBend;
        return new ProfileEditView
        {
            Frame = _frame,
            PlaneZ = _planeZ,
            Length = _editor.Length,
            Bends = bends,
            Initial = _editor.InitialBends.ToArray(),
            SegmentTooSteep = steep,
            BendBreak = breaks,
            SegmentGrades = grades,
            BendElevations = elevations,
            Ground = _ground.Runs,
            GridStep = _gridStep,
            GridLow = gridLow,
            GridHigh = gridHigh,
            GridLabels = gridLabels,
            StationStep = _stationStep,
            FormatStation = FormatStation,
            Preview = preview,
            PlanMarker = marker,
            CursorStation = _cursorStation,
            HotBend = _hotBend,
            SelectedBend = _selected,
            HotSegment = _hotSegment,
            Readout = _readout,
            ReadoutAt = readoutBend >= 0 && readoutBend < bends.Length
                ? new Point3d(_frame.ToDrawing(bends[readoutBend].Station, bends[readoutBend].Elevation).X,
                    _frame.ToDrawing(bends[readoutBend].Station, bends[readoutBend].Elevation).Y, _planeZ)
                : Point3d.Origin,
        };
    }

    /// <summary>Lowest and highest elevation the section has to show: the profile now, as it started, and the ground.</summary>
    private (double Low, double High) ElevationExtent()
    {
        double low = double.PositiveInfinity, high = double.NegativeInfinity;
        foreach (ProfileBend bend in _editor.Bends.Concat(_editor.InitialBends))
        {
            low = Math.Min(low, bend.Elevation);
            high = Math.Max(high, bend.Elevation);
        }

        if (_ground.Extent() is { } ground)
        {
            low = Math.Min(low, ground.Low);
            high = Math.Max(high, ground.High);
        }

        return (low, high);
    }

    /// <summary>Zooms to show the curve and its section when the section is not already in view.</summary>
    private void EnsureVisible()
    {
        RhinoView? view = _doc.Views.ActiveView;
        if (view == null)
            return;
        BoundingBox section = BuildView().DrawingBounds();
        if (view.ActiveViewport.IsVisible(section))
            return;
        BoundingBox both = section;
        for (int i = 0; i < _path.Length / 3; i++)
            both.Union(new Point3d(_path[i * 3], _path[i * 3 + 1], _path[i * 3 + 2]));
        both.Inflate(both.Diagonal.Length * 0.05);
        view.ActiveViewport.ZoomBoundingBox(both);
        view.Redraw();
    }

    private (double X, double Y) PlanPointAt(double station)
    {
        int index = Array.BinarySearch(_pathStations, station);
        if (index >= 0)
            return (_path[index * 3], _path[index * 3 + 1]);
        int upper = Math.Min(~index, _pathStations.Length - 1);
        int lower = Math.Max(upper - 1, 0);
        double span = _pathStations[upper] - _pathStations[lower];
        double t = span > 0.0 ? (station - _pathStations[lower]) / span : 0.0;
        return (_path[lower * 3] + (_path[upper * 3] - _path[lower * 3]) * t,
            _path[lower * 3 + 1] + (_path[upper * 3 + 1] - _path[lower * 3 + 1]) * t);
    }

    private string FormatStation(double station)
    {
        int decimals = Math.Clamp((int)Math.Ceiling(-Math.Log10(_stationStep)) + 1, 0, 3);
        return station.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    /// <summary>A grade as a drawing shows it: two decimals at most (16.83%, 1:12.5), not a field's three.</summary>
    private static string Grade(double ratio, SlopeAnalyzer.SlopeUnit unit) => SlopeInput.FormatWithUnit(ratio, unit, "0.0#");

    private string FormatElevation(double elevation) => FormatElevation(elevation, _elevationDecimals + 1);

    private static string FormatElevation(double elevation, int decimals) =>
        elevation.ToString("F" + Math.Clamp(decimals, 0, 5).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    // ── Getters ────────────────────────────────────────────────────────────

    /// <summary>
    /// The main getter: a press on a bend grabs it, a press on the line adds a bend there and grabs it, and
    /// dragging moves the grabbed bend with snaps. Returns on mouse-up, one Get per gesture.
    /// </summary>
    private sealed class ProfileGetPoint : GetPoint
    {
        private const double ClickPixels = 3.0;

        private readonly ProfileEditSession _session;
        private System.Drawing.Point _pressedAt;
        private bool _moved;

        public ProfileGetPoint(ProfileEditSession session)
        {
            _session = session;
        }

        protected override void OnMouseDown(GetPointMouseEventArgs e)
        {
            base.OnMouseDown(e);
            _pressedAt = e.WindowPoint;
            _moved = false;
            if (_session.TryPlanePoint(e.Viewport, e.WindowPoint, out Point3d point))
                _session.Press(e.Viewport, point);
        }

        protected override void OnMouseMove(GetPointMouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_session.TryPlanePoint(e.Viewport, e.WindowPoint, out Point3d point))
                return;
            if (_session.IsDragging && e.LeftButtonDown)
            {
                if (Math.Abs(e.WindowPoint.X - _pressedAt.X) + Math.Abs(e.WindowPoint.Y - _pressedAt.Y) > ClickPixels)
                    _moved = true;
                if (_moved)
                    _session.Drag(e.Viewport, point, e.ShiftKeyDown, e.ControlKeyDown);
                return;
            }

            _session.Hover(e.Viewport, point);
        }

        /// <summary>Called whenever Get returns: closes the gesture so it is one undo step.</summary>
        public void FinishGesture() => _session.Release();
    }

    /// <summary>A getter that highlights the bend or segment under the cursor and reports which was clicked.</summary>
    private sealed class ProfilePickPoint : GetPoint
    {
        private readonly ProfileEditSession _session;
        private readonly bool _bends;

        public ProfilePickPoint(ProfileEditSession session, bool bends)
        {
            _session = session;
            _bends = bends;
        }

        private bool _pressed;

        public int Picked { get; private set; } = -1;

        /// <summary>
        /// What the returned point picked: the press under the mouse, or, for a point typed or scripted at the
        /// prompt (no mouse event), the bend or segment nearest that point in the section's plane.
        /// </summary>
        public int Resolve()
        {
            if (_pressed)
            {
                _pressed = false;
                return Picked;
            }

            RhinoViewport? viewport = View()?.ActiveViewport ?? _session._doc.Views.ActiveView?.ActiveViewport;
            if (viewport == null)
                return -1;
            Point3d typed = Point();
            var onPlane = new Point3d(typed.X, typed.Y, _session._planeZ);
            return _bends ? _session.HitBend(viewport, onPlane) : _session.HitSegment(viewport, onPlane);
        }

        protected override void OnMouseMove(GetPointMouseEventArgs e)
        {
            base.OnMouseMove(e);
            Picked = Hit(e);
            int bend = _bends ? Picked : -1, segment = _bends ? -1 : Picked;
            if (bend == _session._hotBend && segment == _session._hotSegment)
                return;
            _session._hotBend = bend;
            _session._hotSegment = segment;
            _session.Refresh();
        }

        protected override void OnMouseDown(GetPointMouseEventArgs e)
        {
            base.OnMouseDown(e);
            Picked = Hit(e);
            _pressed = true;
        }

        private int Hit(GetPointMouseEventArgs e)
        {
            if (!_session.TryPlanePoint(e.Viewport, e.WindowPoint, out Point3d point))
                return -1;
            return _bends ? _session.HitBend(e.Viewport, point) : _session.HitSegment(e.Viewport, point);
        }
    }
}
