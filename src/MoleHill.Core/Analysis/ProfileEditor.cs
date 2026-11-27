// The editable long section behind mhEditProfile: bends, edits, undo, and the write-back to a plan path.
namespace MoleHill.Core.Analysis;

/// <summary>
/// A curve's long section as a short list of bends that the user moves, adds, deletes and types grades and
/// elevations into, with its own undo list for the duration of one edit session.
///
/// <para>The bends are the curve's own profile simplified (Douglas-Peucker on elevation), so a draped curve
/// with hundreds of points opens as a handful of grades. Simplifying would lose the curve's fine detail, so
/// the write-back (<see cref="ApplyToPath"/>) never re-elevates a stretch the user did not touch: a span
/// between two original bends that are both unmoved and still neighbours keeps the curve's own elevations
/// exactly. Only touched spans take the straight grades of the edited profile.</para>
///
/// <para>Stations are plan (XY) distances along the curve, from 0 to <see cref="Length"/>. The end bends
/// keep their stations; interior bends stay strictly between their neighbours, so the profile always names
/// one elevation per station.</para>
/// </summary>
public sealed class ProfileEditor
{
    private readonly double[] _sourceStations;
    private readonly double[] _sourceElevations;
    private readonly ProfileBend[] _initial;
    private readonly List<ProfileBend[]> _undo = new();
    private readonly List<ProfileBend[]> _redo = new();
    private List<ProfileBend> _bends;
    private ProfileBend[]? _gestureStart;

    private ProfileEditor(double[] stations, double[] elevations, ProfileBend[] initial, double tolerance, double simplifiedWithin, int verticalSteps)
    {
        _sourceStations = stations;
        _sourceElevations = elevations;
        _initial = initial;
        _bends = initial.ToList();
        Tolerance = tolerance;
        SimplifiedWithin = simplifiedWithin;
        VerticalSteps = verticalSteps;
    }

    /// <summary>Plan length of the curve: the last bend's station.</summary>
    public double Length => _sourceStations[^1];

    /// <summary>Stations closer than this are one station.</summary>
    public double Tolerance { get; }

    /// <summary>The vertical tolerance the opening bends were simplified within.</summary>
    public double SimplifiedWithin { get; }

    /// <summary>Points of the source that sat at the same station as the point before them (a vertical step in
    /// the curve). They survive in untouched stretches; a touched stretch has one elevation per station.</summary>
    public int VerticalSteps { get; }

    public IReadOnlyList<ProfileBend> Bends => _bends;

    public IReadOnlyList<ProfileBend> InitialBends => _initial;

    public int SegmentCount => _bends.Count - 1;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>True when the profile differs from the one the session opened with.</summary>
    public bool HasChanges => !SameBends(_bends, _initial);

    /// <summary>
    /// Builds an editor from a curve's own profile: plan stations (non-decreasing) and an elevation at each.
    /// The bends are those points simplified within <paramref name="simplifyTolerance"/>, loosened by doubling
    /// until there are at most <paramref name="maxBends"/>.
    /// </summary>
    public static ProfileEditor? Create(double[] stations, double[] elevations, double tolerance, double simplifyTolerance, int maxBends)
    {
        if (stations.Length != elevations.Length || stations.Length < 2)
            return null;
        double eps = Math.Max(Math.Abs(tolerance), 1e-12);
        if (!(stations[^1] - stations[0] > eps))
            return null;

        // One elevation per station: a point at the station of the one before it (a vertical step) is not a bend.
        var s = new List<double>(stations.Length) { stations[0] };
        var z = new List<double>(stations.Length) { elevations[0] };
        int steps = 0;
        for (int i = 1; i < stations.Length; i++)
        {
            if (stations[i] - s[^1] <= eps)
            {
                if (Math.Abs(elevations[i] - z[^1]) > eps)
                    steps++;
                continue;
            }

            s.Add(stations[i]);
            z.Add(elevations[i]);
        }

        double within = Math.Max(simplifyTolerance, eps);
        int limit = Math.Max(2, maxBends);
        List<int> kept = Simplify(s, z, within);
        while (kept.Count > limit)
        {
            within *= 2.0;
            kept = Simplify(s, z, within);
        }

        var initial = new ProfileBend[kept.Count];
        for (int i = 0; i < kept.Count; i++)
            initial[i] = new ProfileBend(s[kept[i]], z[kept[i]], i);

        // The ends sit exactly at the source's first and last stations, so a touched end lines up with the path.
        initial[0] = initial[0] with { Station = stations[0] };
        initial[^1] = initial[^1] with { Station = stations[^1] };
        return new ProfileEditor((double[])stations.Clone(), (double[])elevations.Clone(), initial, eps, within, steps);
    }

    // ── Reading ────────────────────────────────────────────────────────────

    /// <summary>Rise over run of segment <paramref name="segment"/> (between bend i and i+1).</summary>
    public double Grade(int segment)
    {
        ProfileBend a = _bends[segment], b = _bends[segment + 1];
        double run = b.Station - a.Station;
        return run > 0.0 ? (b.Elevation - a.Elevation) / run : 0.0;
    }

    /// <summary>The change of grade at an interior bend (outgoing minus incoming); 0 at the ends.</summary>
    public double GradeChange(int bend) =>
        bend <= 0 || bend >= _bends.Count - 1 ? 0.0 : Grade(bend) - Grade(bend - 1);

    /// <summary>The edited profile's elevation at a station, clamped to the ends.</summary>
    public double ElevationAt(double station) => Interpolate(_bends, station);

    /// <summary>The curve's own elevation at a station (the first one where it steps vertically).</summary>
    public double SourceElevationAt(double station)
    {
        if (station <= _sourceStations[0])
            return _sourceElevations[0];
        if (station >= _sourceStations[^1])
            return _sourceElevations[^1];
        int upper = LowerBound(_sourceStations, station);
        if (_sourceStations[upper] == station)
            return _sourceElevations[upper];
        int lower = upper - 1;
        double span = _sourceStations[upper] - _sourceStations[lower];
        double t = span > 0.0 ? (station - _sourceStations[lower]) / span : 0.0;
        return _sourceElevations[lower] + (_sourceElevations[upper] - _sourceElevations[lower]) * t;
    }

    /// <summary>The segment whose station range holds <paramref name="station"/>.</summary>
    public int SegmentAt(double station)
    {
        for (int i = 0; i < _bends.Count - 1; i++)
        {
            if (station <= _bends[i + 1].Station)
                return i;
        }

        return _bends.Count - 2;
    }

    /// <summary>True when bend <paramref name="index"/> is an original bend exactly where it started.</summary>
    public bool IsUnchanged(int index)
    {
        ProfileBend bend = _bends[index];
        return !bend.IsAdded && bend.Equals(_initial[bend.OriginalIndex]);
    }

    /// <summary>
    /// The station ranges the edit re-elevates: every span whose ends are not two unchanged original bends
    /// that were neighbours at the start, merged where they meet.
    /// </summary>
    public IReadOnlyList<(double Start, double End)> TouchedRanges()
    {
        var ranges = new List<(double Start, double End)>();
        for (int i = 0; i < _bends.Count - 1; i++)
        {
            bool untouched = IsUnchanged(i) && IsUnchanged(i + 1) &&
                _bends[i + 1].OriginalIndex == _bends[i].OriginalIndex + 1;
            if (untouched)
                continue;
            double start = _bends[i].Station, end = _bends[i + 1].Station;
            if (ranges.Count > 0 && Math.Abs(ranges[^1].End - start) <= Tolerance)
                ranges[^1] = (ranges[^1].Start, end);
            else
                ranges.Add((start, end));
        }

        return ranges;
    }

    /// <summary>
    /// True when a point at <paramref name="station"/> takes the edited profile: inside a touched range, or
    /// on a bend that moved or was added. A point on an unchanged bend at a range's edge keeps its own height,
    /// which the bend shares, so a vertical step there survives.
    /// </summary>
    public bool TakesProfile(double station)
    {
        foreach ((double start, double end) in TouchedRanges())
        {
            if (station > start + Tolerance && station < end - Tolerance)
                return true;
        }

        for (int i = 0; i < _bends.Count; i++)
        {
            if (Math.Abs(_bends[i].Station - station) <= Tolerance)
                return !IsUnchanged(i);
        }

        return false;
    }

    // ── Undo ───────────────────────────────────────────────────────────────

    /// <summary>Starts an edit that ends with <see cref="EndGesture"/>: a drag is one undo step, however many moves.</summary>
    public void BeginGesture() => _gestureStart ??= _bends.ToArray();

    /// <summary>Ends the edit; it becomes an undo step only if it changed the profile.</summary>
    public void EndGesture()
    {
        ProfileBend[]? start = _gestureStart;
        _gestureStart = null;
        if (start == null || SameBends(_bends, start))
            return;
        _undo.Add(start);
        _redo.Clear();
    }

    public bool Undo()
    {
        EndGesture();
        if (_undo.Count == 0)
            return false;
        _redo.Add(_bends.ToArray());
        _bends = _undo[^1].ToList();
        _undo.RemoveAt(_undo.Count - 1);
        return true;
    }

    public bool Redo()
    {
        EndGesture();
        if (_redo.Count == 0)
            return false;
        _undo.Add(_bends.ToArray());
        _bends = _redo[^1].ToList();
        _redo.RemoveAt(_redo.Count - 1);
        return true;
    }

    // ── Edits ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Moves a bend. The end bends keep their stations; an interior bend's station is held strictly between
    /// its neighbours. Returns the bend as placed. Part of the open gesture, or one undo step on its own.
    /// </summary>
    public ProfileBend Move(int index, double station, double elevation)
    {
        bool own = _gestureStart == null;
        BeginGesture();
        ProfileBend bend = _bends[index];
        double s = ClampStation(index, station);
        _bends[index] = bend with { Station = s, Elevation = double.IsFinite(elevation) ? elevation : bend.Elevation };
        if (own)
            EndGesture();
        return _bends[index];
    }

    /// <summary>
    /// Adds a bend on the profile at <paramref name="station"/> (so the profile's shape is unchanged until it
    /// is moved). Returns its index, or -1 when the station is on or beyond an existing bend.
    /// </summary>
    public int Insert(double station)
    {
        if (station <= Tolerance || station >= Length - Tolerance)
            return -1;
        int segment = SegmentAt(station);
        if (station - _bends[segment].Station <= Tolerance || _bends[segment + 1].Station - station <= Tolerance)
            return -1;

        bool own = _gestureStart == null;
        BeginGesture();
        _bends.Insert(segment + 1, new ProfileBend(station, ElevationAt(station), -1));
        if (own)
            EndGesture();
        return segment + 1;
    }

    /// <summary>Removes an interior bend; the ends cannot be removed.</summary>
    public bool Delete(int index)
    {
        if (index <= 0 || index >= _bends.Count - 1)
            return false;
        bool own = _gestureStart == null;
        BeginGesture();
        _bends.RemoveAt(index);
        if (own)
            EndGesture();
        return true;
    }

    /// <summary>Sets a bend's elevation, keeping its station.</summary>
    public void SetElevation(int index, double elevation) => Move(index, _bends[index].Station, elevation);

    /// <summary>
    /// Gives segment <paramref name="segment"/> a grade (rise over run, signed along increasing station) by
    /// moving one of its ends vertically: the later bend when <paramref name="holdStart"/>, else the earlier.
    /// </summary>
    public void SetGrade(int segment, double grade, bool holdStart)
    {
        ProfileBend a = _bends[segment], b = _bends[segment + 1];
        double run = b.Station - a.Station;
        if (holdStart)
            SetElevation(segment + 1, a.Elevation + grade * run);
        else
            SetElevation(segment, b.Elevation - grade * run);
    }

    // ── Write-back ─────────────────────────────────────────────────────────

    /// <summary>
    /// Re-elevates a flat XYZ plan path (the one the editor was built from) from the edited profile, keeping
    /// every point's XY. Untouched stretches keep their own elevations; touched stretches take the profile, and
    /// every bend inside one adds a path point at its station, so the profile's breaks survive exactly.
    /// </summary>
    public double[] ApplyToPath(double[] pathXyz)
    {
        int count = pathXyz.Length / 3;
        double[] pathStations = LongSectionProfile.PlanStations(pathXyz);
        var merged = new List<(double Station, int Segment, bool FromPath)>(count + _bends.Count);
        for (int i = 0; i < count; i++)
            merged.Add((pathStations[i], i, true));
        double length = count > 0 ? pathStations[^1] : 0.0;
        for (int i = 0; i < _bends.Count; i++)
        {
            double station = _bends[i].Station;
            if (station <= Tolerance || station >= length - Tolerance || !TakesProfile(station))
                continue;
            int index = Array.BinarySearch(pathStations, station);
            int segment = index >= 0 ? index : ~index - 1;
            merged.Add((station, Math.Clamp(segment, 0, Math.Max(0, count - 2)), false));
        }

        // Stable by station, path points first, so a bend that falls on a path point is the one dropped.
        merged = merged.OrderBy(item => item.Station).ThenBy(item => item.FromPath ? 0 : 1).ToList();
        var output = new List<double>(merged.Count * 3);
        double lastStation = double.NegativeInfinity;
        foreach ((double station, int segment, bool fromPath) in merged)
        {
            if (!fromPath && station - lastStation <= Tolerance)
                continue;
            double x, y, pathZ;
            if (fromPath)
            {
                x = pathXyz[segment * 3];
                y = pathXyz[segment * 3 + 1];
                pathZ = pathXyz[segment * 3 + 2];
            }
            else
            {
                double span = pathStations[segment + 1] - pathStations[segment];
                double t = span > 0.0 ? (station - pathStations[segment]) / span : 0.0;
                x = pathXyz[segment * 3] + (pathXyz[(segment + 1) * 3] - pathXyz[segment * 3]) * t;
                y = pathXyz[segment * 3 + 1] + (pathXyz[(segment + 1) * 3 + 1] - pathXyz[segment * 3 + 1]) * t;
                pathZ = pathXyz[segment * 3 + 2] + (pathXyz[(segment + 1) * 3 + 2] - pathXyz[segment * 3 + 2]) * t;
            }

            // A path point at the station just written that the profile takes would only repeat it.
            bool takes = TakesProfile(station);
            if (fromPath && takes && station - lastStation <= Tolerance)
                continue;

            output.Add(x);
            output.Add(y);
            output.Add(takes ? ElevationAt(station) : pathZ);
            lastStation = station;
        }

        return output.ToArray();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private double ClampStation(int index, double station)
    {
        if (index == 0)
            return _bends[0].Station;
        if (index == _bends.Count - 1)
            return _bends[^1].Station;
        double low = _bends[index - 1].Station + Tolerance * 2.0;
        double high = _bends[index + 1].Station - Tolerance * 2.0;
        if (!double.IsFinite(station) || low >= high)
            return _bends[index].Station;
        return Math.Clamp(station, low, high);
    }

    private static double Interpolate(IReadOnlyList<ProfileBend> bends, double station)
    {
        if (station <= bends[0].Station)
            return bends[0].Elevation;
        if (station >= bends[^1].Station)
            return bends[^1].Elevation;
        for (int i = 0; i < bends.Count - 1; i++)
        {
            ProfileBend a = bends[i], b = bends[i + 1];
            if (station > b.Station)
                continue;
            double run = b.Station - a.Station;
            double t = run > 0.0 ? (station - a.Station) / run : 0.0;
            return a.Elevation + (b.Elevation - a.Elevation) * t;
        }

        return bends[^1].Elevation;
    }

    private static int LowerBound(double[] values, double value)
    {
        int lo = 0, hi = values.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (values[mid] < value)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }

    private static bool SameBends(IReadOnlyList<ProfileBend> a, IReadOnlyList<ProfileBend> b)
    {
        if (a.Count != b.Count)
            return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!a[i].Equals(b[i]))
                return false;
        }

        return true;
    }

    /// <summary>Douglas-Peucker on (station, elevation), measured vertically: the indices kept, ends included.</summary>
    private static List<int> Simplify(List<double> s, List<double> z, double tolerance)
    {
        var keep = new bool[s.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int From, int To)>();
        stack.Push((0, s.Count - 1));
        while (stack.Count > 0)
        {
            (int from, int to) = stack.Pop();
            double run = s[to] - s[from];
            double worst = tolerance;
            int worstIndex = -1;
            for (int i = from + 1; i < to; i++)
            {
                double chord = z[from] + (z[to] - z[from]) * ((s[i] - s[from]) / run);
                double miss = Math.Abs(z[i] - chord);
                if (miss > worst)
                {
                    worst = miss;
                    worstIndex = i;
                }
            }

            if (worstIndex < 0)
                continue;
            keep[worstIndex] = true;
            stack.Push((from, worstIndex));
            stack.Push((worstIndex, to));
        }

        var kept = new List<int>();
        for (int i = 0; i < keep.Length; i++)
        {
            if (keep[i])
                kept.Add(i);
        }

        return kept;
    }
}
