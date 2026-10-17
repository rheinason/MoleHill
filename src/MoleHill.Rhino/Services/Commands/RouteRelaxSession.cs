// The live part of mhRelaxRoute: a band relaxing on a worker while the user drags pins and turns weights.
using MoleHill.Core.Routing;
using MoleHill.Rhino.UI;
using MoleHill.Shared;
using Rhino;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace MoleHill.Rhino.Services;

/// <summary>
/// Runs a <see cref="RouteRelaxer"/> live. A worker steps the band a couple of sweeps at a time and
/// publishes a <see cref="RouteRelaxSnapshot"/> for the viewport; a second worker scores it
/// (<see cref="RouteRelaxScore"/>) and, once it settles, fits the curve Keep will add. Meanwhile the
/// command's get loop turns mouse drags into pins, the floating <see cref="RouteRelaxForm"/> turns its
/// controls into settings, and every change wakes the band. All access to the relaxer is under one lock,
/// held for a few milliseconds at a time, so a drag never waits on the band.
/// </summary>
internal sealed class RouteRelaxSession : IDisposable
{
    /// <summary>Sweeps per batch on the worker: a few milliseconds, so a drag never waits long for the lock.</summary>
    private const int SweepsPerBatch = 2;

    /// <summary>How close the cursor must come to a pin or the band to grab it, in pixels.</summary>
    private const double GrabPixels = 10.0;

    private const int ProgressMilliseconds = 100;
    private const int ScoreMilliseconds = 250;

    private readonly RhinoDoc _doc;
    private readonly RouteRelaxer _relaxer;
    private readonly RouteHeightGrid _grid;
    private readonly Mesh _terrain;
    private readonly RouteOptionSet _options;
    private readonly ModelUnitContext _units;
    private readonly double _tolerance;
    private readonly RouteRelaxConduit _conduit;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _workers = new();
    private RouteRelaxForm? _form;
    private RelaxGetPoint? _getter;
    private volatile bool _paused;
    private volatile bool _disposed;
    private volatile RouteRelaxSnapshot? _snapshot;
    private volatile RouteRelaxScore? _score;
    private int _version;
    private int _redrawQueued;
    private int _progressQueued;
    private long _lastProgress;

    public RouteRelaxSession(
        RhinoDoc doc,
        RouteRelaxer relaxer,
        RouteHeightGrid grid,
        Mesh terrain,
        RouteOptionSet options,
        ModelUnitContext units,
        double tolerance,
        Polyline sketch,
        BoundingBox domain)
    {
        _doc = doc;
        _relaxer = relaxer;
        _grid = grid;
        _terrain = terrain;
        _options = options;
        _units = units;
        _tolerance = tolerance;
        _conduit = new RouteRelaxConduit(sketch, domain);
    }

    private enum Request
    {
        Keep,
        Cancel,
        HelpMe,
        Reset,
        UnpinAll,
    }

    /// <summary>
    /// Runs the session until the user keeps the route or leaves. Returns the evaluation to add, with its
    /// fitted curve, or null when cancelled.
    /// </summary>
    public RouteRelaxEvaluation? Run()
    {
        _conduit.Enabled = true;
        ShowForm();
        Publish();
        _workers.Add(Task.Factory.StartNew(RelaxLoop, TaskCreationOptions.LongRunning));
        _workers.Add(Task.Factory.StartNew(ScoreLoop, TaskCreationOptions.LongRunning));

        while (true)
        {
            var getter = new RelaxGetPoint(this);
            getter.SetCommandPrompt("Drag the route or its pins to steer it; click a pin to release it. Enter keeps the route");
            getter.AcceptNothing(true);
            getter.AcceptCustomMessage(true);
            getter.PermitObjectSnap(false);
            getter.Constrain(_terrain, false);
            _options.AddTo(getter);
            int helpOption = getter.AddOption("HelpMe");
            int resetOption = getter.AddOption("Reset");
            int unpinOption = getter.AddOption("UnpinAll");
            _getter = getter;

            GetResult result = getter.Get(true);
            _getter = null;
            switch (result)
            {
                case GetResult.Point:
                    getter.FinishGesture();
                    continue;

                case GetResult.Nothing:
                    return Keep();

                case GetResult.CustomMessage when getter.CustomMessage() is Request request:
                    switch (request)
                    {
                        case Request.Keep:
                            return Keep();
                        case Request.Cancel:
                            return null;
                        case Request.HelpMe:
                            HelpMe();
                            break;
                        case Request.Reset:
                            Reset();
                            break;
                        case Request.UnpinAll:
                            UnpinAll();
                            break;
                    }

                    continue;

                case GetResult.Option:
                    int chosen = getter.OptionIndex();
                    if (chosen == helpOption)
                        HelpMe();
                    else if (chosen == resetOption)
                        Reset();
                    else if (chosen == unpinOption)
                        UnpinAll();
                    else
                        CommitOptions(getter, result);
                    continue;

                default:
                    return null;
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _stop.Cancel();
        try
        {
            Task.WaitAll(_workers.ToArray(), 2000);
        }
        catch (AggregateException)
        {
            // A worker that faulted has nothing left to clean up.
        }

        _conduit.Enabled = false;
        RouteRelaxForm? form = _form;
        _form = null;
        form?.Close();
        _doc.Views.Redraw();
        _stop.Dispose();
    }

    // ── Settings ───────────────────────────────────────────────────────────

    private RouteSuggestionSettings Settings => _options.Settings;

    private void CommitOptions(GetBaseClass getter, GetResult result)
    {
        _options.Commit(getter, result);
        ApplySettings(save: true);
    }

    /// <summary>Hands the current options to the band (which wakes it) and mirrors them on the panel.</summary>
    private void ApplySettings(bool save)
    {
        string? invalid = Settings.Validate(Math.Abs(_options.MaxGrade.Ratio));
        if (invalid != null)
        {
            RhinoApp.WriteLine(invalid);
            return;
        }

        RouteRelaxSettings settings = Settings.ToRelaxSettings(Math.Abs(_options.MaxGrade.Ratio), _tolerance, _units);
        lock (_gate)
        {
            _relaxer.Settings = settings;
            Interlocked.Increment(ref _version);
        }

        if (save)
            Settings.Save(_units);
        SyncForm();
    }

    private void SyncForm() =>
        _form?.SyncSettings(Settings.DistanceWeight, Settings.SteepnessWeight, Settings.EarthworksWeight, Settings.Fidelity, Settings.Simplicity);

    // ── Actions ────────────────────────────────────────────────────────────

    private void Reset()
    {
        lock (_gate)
        {
            _relaxer.Reset();
            Interlocked.Increment(ref _version);
        }

        _form?.ClearHistory();
        Publish();
    }

    private void UnpinAll()
    {
        lock (_gate)
        {
            _relaxer.RemoveAllPins();
            Interlocked.Increment(ref _version);
        }

        Publish();
    }

    /// <summary>
    /// Searches for a route near the band through its pins, on a worker while Rhino's message loop keeps the
    /// viewport live; Esc cancels and leaves the band as it was.
    /// </summary>
    private void HelpMe()
    {
        _paused = true;
        _form?.SetBusy("Searching for a route near your sketch. Esc cancels");
        RhinoApp.SetCommandPrompt("Searching for a route near your sketch. Press Esc to cancel");
        using var cancellation = new CancellationTokenSource();
        void OnEscape(object? sender, EventArgs e) => cancellation.Cancel();
        RhinoApp.EscapeKeyPressed += OnEscape;
        try
        {
            CancellationToken token = cancellation.Token;
            Task<RouteSuggestionResult> work = Task.Run(() =>
            {
                lock (_gate)
                    return _relaxer.HelpMe(token);
            });
            while (!work.Wait(30))
                RhinoApp.Wait();

            RouteSuggestionResult result = work.Result;
            if (result.Succeeded)
            {
                Interlocked.Increment(ref _version);
                RhinoApp.WriteLine("Found a route near your sketch; it keeps relaxing from there. Reset goes back to what you drew.");
            }
            else
            {
                RhinoApp.WriteLine(RouteSuggestionReadout.DescribeFailure(result, Math.Abs(_options.MaxGrade.Ratio), SlopeUnitPreference.Current));
            }
        }
        catch (AggregateException error) when (error.InnerExceptions.All(inner => inner is OperationCanceledException))
        {
            RhinoApp.WriteLine("Help me cancelled; the route is as it was.");
        }
        finally
        {
            RhinoApp.EscapeKeyPressed -= OnEscape;
            _paused = false;
            _form?.SetBusy(null);
            Publish();
        }
    }

    /// <summary>The route to add: the band scored and its curve fitted, on a worker while the viewport stays live.</summary>
    private RouteRelaxEvaluation? Keep()
    {
        _paused = true;
        _form?.SetBusy("Fitting the curve…");
        RhinoApp.SetCommandPrompt("Fitting the curve");
        try
        {
            Task<RouteRelaxEvaluation> work = Task.Run(() =>
            {
                RouteRelaxEvaluation evaluation;
                lock (_gate)
                    evaluation = _relaxer.Evaluate(fitCurve: false);
                (FittedRouteCurve curve, RouteMetrics metrics) = _relaxer.FitCurve(evaluation);
                return evaluation.WithCurve(curve, metrics);
            });
            while (!work.Wait(30))
                RhinoApp.Wait();

            return work.Result;
        }
        finally
        {
            _form?.SetBusy(null);
        }
    }

    // ── Pins (called on the UI thread by the getter) ──────────────────────

    internal int PinNear(Point3d point, double radius)
    {
        lock (_gate)
            return _relaxer.PinNear(point.X, point.Y, radius);
    }

    internal bool IsOnBand(Point3d point, double radius)
    {
        lock (_gate)
            return _relaxer.DistanceToBand(point.X, point.Y) <= radius;
    }

    internal int AddPin(Point3d point)
    {
        int id;
        lock (_gate)
        {
            id = _relaxer.AddPin(point.X, point.Y);
            Interlocked.Increment(ref _version);
        }

        Publish();
        return id;
    }

    internal void MovePin(int id, Point3d point)
    {
        lock (_gate)
        {
            if (!_relaxer.MovePin(id, point.X, point.Y))
                return;
            Interlocked.Increment(ref _version);
        }

        Publish();
    }

    internal void RemovePin(int id)
    {
        lock (_gate)
        {
            if (!_relaxer.RemovePin(id))
                return;
            Interlocked.Increment(ref _version);
        }

        Publish();
    }

    internal RouteRelaxConduit Conduit => _conduit;

    internal static double WorldRadius(global::Rhino.Display.RhinoViewport viewport, Point3d at)
    {
        viewport.GetWorldToScreenScale(at, out double pixelsPerUnit);
        return pixelsPerUnit > 1e-12 ? GrabPixels / pixelsPerUnit : GrabPixels;
    }

    // ── Workers ────────────────────────────────────────────────────────────

    private void RelaxLoop()
    {
        CancellationToken stop = _stop.Token;
        while (!stop.IsCancellationRequested)
        {
            bool moved = false;
            if (!_paused)
            {
                lock (_gate)
                {
                    if (!_relaxer.IsSettled)
                    {
                        moved = _relaxer.Step(SweepsPerBatch) > 0;
                        if (moved)
                            Interlocked.Increment(ref _version);
                    }
                }
            }

            if (moved)
                Publish();
            else
                Thread.Sleep(15);
        }
    }

    private void ScoreLoop()
    {
        CancellationToken stop = _stop.Token;
        int scored = -1, fitted = -1;
        while (!stop.IsCancellationRequested)
        {
            Thread.Sleep(ScoreMilliseconds);
            if (_paused)
                continue;

            int version = Volatile.Read(ref _version);
            RouteRelaxEvaluation evaluation;
            bool settled;
            lock (_gate)
            {
                settled = _relaxer.IsSettled;
                if (version == scored && (!settled || version == fitted))
                    continue;
                evaluation = _relaxer.Evaluate(fitCurve: false);
            }

            scored = version;
            Polyline? curve = null;
            if (settled)
            {
                (FittedRouteCurve fittedCurve, RouteMetrics metrics) = _relaxer.FitCurve(evaluation);
                if (Volatile.Read(ref _version) != version)
                    continue;
                evaluation = evaluation.WithCurve(fittedCurve, metrics);
                curve = RouteCurveBuilder.ToPolyline(fittedCurve.PointsXyz);
                fitted = version;
            }

            var issues = evaluation.Issues
                .Select(issue => (issue, new Point3d(issue.X, issue.Y, GroundAt(issue.X, issue.Y)), RouteRelaxReadout.IssueTag(issue, _units)))
                .ToList();
            var score = new RouteRelaxScore(evaluation, issues, curve, version);
            _score = score;
            _conduit.Show(score);
            OnUiThread(() =>
            {
                ShowScore(score);
                _doc.Views.Redraw();
            });
        }
    }

    /// <summary>Takes a snapshot of the band for the viewport and schedules a redraw and a panel update, throttled.</summary>
    private void Publish()
    {
        RouteRelaxSnapshot snapshot;
        lock (_gate)
        {
            double[] plan = _relaxer.PlanXy();
            var band = new Polyline(plan.Length / 2);
            for (int i = 0; i < plan.Length / 2; i++)
                band.Add(plan[i * 2], plan[(i * 2) + 1], GroundAt(plan[i * 2], plan[(i * 2) + 1]));
            var pins = _relaxer.Pins().Select(pin => (pin.Id, new Point3d(pin.X, pin.Y, GroundAt(pin.X, pin.Y)))).ToList();
            snapshot = new RouteRelaxSnapshot(band, pins, _relaxer.Cost(), _relaxer.SketchCost, _relaxer.IsSettled, _relaxer.Sweeps);
        }

        _snapshot = snapshot;
        _conduit.Show(snapshot);
        // One redraw queued at a time: the UI thread paces it, however fast the band publishes.
        if (Interlocked.Exchange(ref _redrawQueued, 1) == 0)
        {
            OnUiThread(() =>
            {
                Volatile.Write(ref _redrawQueued, 0);
                _doc.Views.Redraw();
            });
        }

        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastProgress) >= ProgressMilliseconds && Interlocked.Exchange(ref _progressQueued, 1) == 0)
        {
            Interlocked.Exchange(ref _lastProgress, now);
            OnUiThread(() =>
            {
                Volatile.Write(ref _progressQueued, 0);
                if (_snapshot is { } latest)
                    ShowProgress(latest);
            });
        }
    }

    private void ShowProgress(RouteRelaxSnapshot snapshot) =>
        _form?.ShowProgress(
            RouteRelaxReadout.Score(snapshot.Improvement),
            snapshot.Improvement,
            snapshot.Settled ? "Settled. Drag the route or a pin to steer it, or Keep." : $"Relaxing… {snapshot.Sweeps} sweeps");

    private void ShowScore(RouteRelaxScore score)
    {
        RouteRelaxEvaluation evaluation = score.Evaluation;
        MoleHill.Core.Analysis.SlopeAnalyzer.SlopeUnit unit = SlopeUnitPreference.Current;
        RouteMetrics metrics = evaluation.CurveMetrics ?? evaluation.Metrics;
        string figures = RouteRelaxReadout.Figures(metrics, _units, unit);
        string? curve = evaluation.Curve is { } fitted ? RouteRelaxReadout.Curve(fitted, _units) : null;
        var issues = evaluation.Issues
            .Select(issue => RouteRelaxReadout.IssueSentence(issue, evaluation.Settings.MaxGrade, _units, unit))
            .ToList();
        _form?.ShowEvaluation(figures, curve, issues);
    }

    private double GroundAt(double x, double y) => _grid.TrySample(x, y, out double z) ? z : 0.0;

    private void OnUiThread(Action action)
    {
        if (_disposed)
            return;
        RhinoApp.InvokeOnUiThread(new Action(() =>
        {
            if (!_disposed)
                action();
        }));
    }

    private void ShowForm()
    {
        var form = new RouteRelaxForm(_doc);
        form.WeightsChanged += (distance, steepness, earthworks, commit) =>
        {
            Settings.DistanceWeight = distance;
            Settings.SteepnessWeight = steepness;
            Settings.EarthworksWeight = earthworks;
            ApplySettings(save: commit);
        };
        form.FidelityChanged += fidelity =>
        {
            Settings.Fidelity = fidelity;
            ApplySettings(save: true);
        };
        form.SimplicityChanged += simplicity =>
        {
            Settings.Simplicity = simplicity;
            ApplySettings(save: true);
        };
        form.HelpRequested += () => Post(Request.HelpMe);
        form.ResetRequested += () => Post(Request.Reset);
        form.UnpinAllRequested += () => Post(Request.UnpinAll);
        form.KeepRequested += () => Post(Request.Keep);
        form.CancelRequested += () => Post(Request.Cancel);
        form.Closed += (_, _) =>
        {
            if (!_disposed)
                Post(Request.Cancel);
        };

        form.Show();
        form.PositionOverActiveView(_doc);
        _form = form;
        SyncForm();
        RhinoApp.SetFocusToMainWindow();
    }

    /// <summary>Ends the getter's wait with a request from the panel.</summary>
    private void Post(Request request)
    {
        if (_getter != null)
            GetBaseClass.PostCustomMessage(request);
    }

    /// <summary>
    /// The session's getter. A press on a pin grabs it; a press on the band pins it there and grabs the new
    /// pin; dragging moves the grabbed pin while the band relaxes around it. A press and release on a pin
    /// without moving releases it. Returns on mouse-up, one Get per gesture.
    /// </summary>
    private sealed class RelaxGetPoint : GetPoint
    {
        private const double ClickPixels = 3.0;

        private readonly RouteRelaxSession _session;
        private int _grabbed = -1;
        private bool _created;
        private bool _moved;
        private System.Drawing.Point _pressedAt;

        public RelaxGetPoint(RouteRelaxSession session)
        {
            _session = session;
        }

        protected override void OnMouseDown(GetPointMouseEventArgs e)
        {
            base.OnMouseDown(e);
            double radius = WorldRadius(e.Viewport, e.Point);
            _pressedAt = e.WindowPoint;
            _moved = false;
            _created = false;
            _grabbed = _session.PinNear(e.Point, radius);
            if (_grabbed < 0 && _session.IsOnBand(e.Point, radius))
            {
                _grabbed = _session.AddPin(e.Point);
                _created = true;
            }

            _session.Conduit.DraggedPin = _grabbed;
        }

        protected override void OnMouseMove(GetPointMouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_grabbed >= 0 && e.LeftButtonDown)
            {
                if (Math.Abs(e.WindowPoint.X - _pressedAt.X) + Math.Abs(e.WindowPoint.Y - _pressedAt.Y) > ClickPixels)
                    _moved = true;
                if (_moved)
                    _session.MovePin(_grabbed, e.Point);
                return;
            }

            int hot = _session.PinNear(e.Point, WorldRadius(e.Viewport, e.Point));
            if (hot != _session.Conduit.HotPin)
            {
                _session.Conduit.HotPin = hot;
                e.Viewport.ParentView?.Redraw();
            }
        }

        /// <summary>Called when Get returns a point: a click on an existing pin, without a drag, releases it.</summary>
        public void FinishGesture()
        {
            if (_grabbed >= 0 && !_moved && !_created)
                _session.RemovePin(_grabbed);
            _session.Conduit.DraggedPin = -1;
            _grabbed = -1;
        }
    }
}
