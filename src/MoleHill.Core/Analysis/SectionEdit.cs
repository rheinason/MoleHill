// Profile mode's write-back: reads edited long-section pieces against the curve's own profile.
using MoleHill.Core.Geometry;

namespace MoleHill.Core.Analysis;

/// <summary>
/// What a long section edited with ordinary tools says about its plan curve. The section may have been
/// trimmed, split, rebuilt or spliced, so it arrives as one or more <em>pieces</em>, each a polyline in
/// (station, elevation). Stations a piece covers take its heights; stations no piece covers keep the curve's
/// own. And within a piece only what actually changed is re-elevated: a stretch where the piece still lies on
/// the original profile keeps the curve's own points exactly, so an edit at one end never resamples the rest.
///
/// <para>A piece may run either way along the section (a replacement drawn right to left is reversed). It may
/// not double back in station, and two pieces may not give different heights for the same station: both are
/// refused with the station where it happens, rather than guessed at.</para>
/// </summary>
public sealed class SectionEdit
{
    private readonly double[] _sourceStations;
    private readonly double[] _sourceElevations;
    private readonly List<Piece> _pieces;
    private readonly List<(double Start, double End)> _changed;
    private readonly double[] _movedVertices;

    private SectionEdit(
        double[] stations, double[] elevations, List<Piece> pieces, List<(double Start, double End)> changed, double[] movedVertices, double tolerance)
    {
        _sourceStations = stations;
        _sourceElevations = elevations;
        _pieces = pieces;
        _changed = changed;
        _movedVertices = movedVertices;
        Tolerance = tolerance;
    }

    /// <summary>Stations and heights closer than this are the same.</summary>
    public double Tolerance { get; }

    /// <summary>Plan length of the curve.</summary>
    public double Length => _sourceStations[^1];

    /// <summary>The station ranges the edit re-elevates, in order and not overlapping.</summary>
    public IReadOnlyList<(double Start, double End)> ChangedRanges => _changed;

    public bool HasChanges => _changed.Count > 0;

    /// <summary>Station ranges no piece covers: the curve keeps its own heights there.</summary>
    public IReadOnlyList<(double Start, double End)> Uncovered()
    {
        var gaps = new List<(double Start, double End)>();
        double at = 0.0;
        foreach (Piece piece in _pieces.OrderBy(p => p.Start))
        {
            if (piece.Start - at > Tolerance)
                gaps.Add((at, piece.Start));
            at = Math.Max(at, piece.End);
        }

        if (Length - at > Tolerance)
            gaps.Add((at, Length));
        return gaps;
    }

    /// <summary>
    /// Reads <paramref name="pieces"/> (each a flat [station, elevation, ...] polyline) against the curve's own
    /// profile (<paramref name="stations"/> non-decreasing from 0, an elevation at each). Returns null with a
    /// reason when the pieces cannot describe one profile.
    /// </summary>
    public static SectionEdit? Create(
        double[] stations, double[] elevations, IReadOnlyList<double[]> pieces, double tolerance, out string? error)
    {
        error = null;
        double tol = Math.Max(Math.Abs(tolerance), 1e-9);
        if (stations.Length < 2 || stations.Length != elevations.Length || !(stations[^1] - stations[0] > tol))
        {
            error = "The curve has no length in plan.";
            return null;
        }

        double length = stations[^1];
        var read = new List<Piece>();
        foreach (double[] flat in pieces)
        {
            Piece? piece = Piece.Read(flat, length, tol, out error);
            if (error != null)
                return null;
            if (piece != null)
                read.Add(piece);
        }

        if (read.Count == 0)
        {
            error = "No part of the section lies over the curve's stations.";
            return null;
        }

        read.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (int i = 1; i < read.Count; i++)
        {
            Piece previous = read[i - 1], next = read[i];
            double overlapEnd = Math.Min(previous.End, next.End);
            if (overlapEnd - next.Start <= tol)
                continue;
            // Overlap is allowed only where both pieces say the same thing (a piece copied over itself).
            foreach (double s in previous.StationsIn(next.Start, overlapEnd).Concat(next.StationsIn(next.Start, overlapEnd)))
            {
                if (Math.Abs(previous.ElevationAt(s) - next.ElevationAt(s)) > tol)
                {
                    error = FormattableString.Invariant(
                        $"Two parts of the section give different heights between stations {next.Start:0.###} and {overlapEnd:0.###}; trim one so each station has one height.");
                    return null;
                }
            }
        }

        var changed = new List<(double Start, double End)>();
        foreach (Piece piece in read)
            changed.AddRange(piece.ChangedRanges(stations, elevations, tol));
        changed.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(double Start, double End)>();
        foreach ((double start, double end) in changed)
        {
            if (merged.Count > 0 && start <= merged[^1].End + tol)
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, end));
            else
                merged.Add((start, end));
        }

        double[] moved = read.SelectMany(piece => piece.MovedVertexStations(stations, elevations, tol)).OrderBy(s => s).ToArray();
        return new SectionEdit((double[])stations.Clone(), (double[])elevations.Clone(), read, merged, moved, tol);
    }

    /// <summary>
    /// True when a point at <paramref name="station"/> takes the edited section: strictly inside a changed
    /// range, or on a section vertex that moved off the original profile. A range's other ends are vertices the
    /// edit left on the profile, so they keep their own height (and a vertical step there survives).
    /// </summary>
    public bool TakesProfile(double station)
    {
        foreach ((double start, double end) in _changed)
        {
            if (station > start + Tolerance && station < end - Tolerance)
                return true;
        }

        int index = Array.BinarySearch(_movedVertices, station);
        if (index >= 0)
            return true;
        int upper = ~index;
        return (upper < _movedVertices.Length && _movedVertices[upper] - station <= Tolerance) ||
            (upper > 0 && station - _movedVertices[upper - 1] <= Tolerance);
    }

    /// <summary>The edited height at a station inside a changed range; the curve's own elsewhere.</summary>
    public double ElevationAt(double station)
    {
        if (TakesProfile(station) && PieceAt(station) is { } piece)
            return piece.ElevationAt(station);
        return LongSectionProfile.ElevationAt(_sourceStations, _sourceElevations, Math.Clamp(station, 0.0, Length));
    }

    /// <summary>
    /// Re-elevates the flat XYZ plan path the source profile came from, keeping every point's XY. Outside the
    /// changed ranges the path is returned point for point; inside, path points take the section's heights and
    /// each section vertex adds a point at its station (both points of a vertical step), so the edit's breaks
    /// survive exactly.
    /// </summary>
    public double[] ApplyToPath(double[] pathXyz)
    {
        int count = pathXyz.Length / 3;
        double[] pathStations = LongSectionProfile.PlanStations(pathXyz);
        var items = new List<(double Station, double Order, double Z, int Segment, bool FromPath)>(count + 16);
        for (int i = 0; i < count; i++)
        {
            double s = pathStations[i];
            items.Add((s, i, TakesProfile(s) ? ElevationAt(s) : pathXyz[i * 3 + 2], i, true));
        }

        foreach (Piece piece in _pieces)
        {
            for (int k = 0; k < piece.Count; k++)
            {
                double s = piece.Station(k);
                if (!TakesProfile(s))
                    continue;
                int index = Array.BinarySearch(pathStations, s);
                int segment = Math.Clamp(index >= 0 ? index : ~index - 1, 0, Math.Max(0, count - 2));
                items.Add((s, segment + 0.5 + k * 1e-9, piece.Elevation(k), segment, false));
            }
        }

        // A section vertex at a path point's station replaces it: the vertex carries the edit's height (and,
        // at a step, its order), the path point only an interpolation of it.
        var pieceStations = items.Where(item => !item.FromPath).Select(item => item.Station).OrderBy(s => s).ToArray();
        bool NearPieceVertex(double s)
        {
            int index = Array.BinarySearch(pieceStations, s);
            if (index >= 0)
                return true;
            int upper = ~index;
            return (upper < pieceStations.Length && pieceStations[upper] - s <= Tolerance) ||
                (upper > 0 && s - pieceStations[upper - 1] <= Tolerance);
        }

        var output = new List<double>(items.Count * 3);
        foreach (var item in items
                     .Where(item => !(item.FromPath && TakesProfile(item.Station) && NearPieceVertex(item.Station)))
                     .OrderBy(item => item.Station)
                     .ThenBy(item => item.Order))
        {
            double x, y;
            if (item.FromPath)
            {
                x = pathXyz[item.Segment * 3];
                y = pathXyz[item.Segment * 3 + 1];
            }
            else
            {
                int a = item.Segment, b = Math.Min(item.Segment + 1, count - 1);
                double span = pathStations[b] - pathStations[a];
                double t = span > 0.0 ? Math.Clamp((item.Station - pathStations[a]) / span, 0.0, 1.0) : 0.0;
                x = pathXyz[a * 3] + (pathXyz[b * 3] - pathXyz[a * 3]) * t;
                y = pathXyz[a * 3 + 1] + (pathXyz[b * 3 + 1] - pathXyz[a * 3 + 1]) * t;
            }

            output.Add(x);
            output.Add(y);
            output.Add(item.Z);
        }

        return output.ToArray();
    }

    private Piece? PieceAt(double station)
    {
        foreach (Piece piece in _pieces)
        {
            if (station >= piece.Start - Tolerance && station <= piece.End + Tolerance)
                return piece;
        }

        return null;
    }

    /// <summary>One edited stretch: a polyline in (station, elevation), stations non-decreasing.</summary>
    private sealed class Piece
    {
        private readonly double[] _s;
        private readonly double[] _z;

        private Piece(double[] s, double[] z)
        {
            _s = s;
            _z = z;
        }

        public int Count => _s.Length;
        public double Start => _s[0];
        public double End => _s[^1];
        public double Station(int k) => _s[k];
        public double Elevation(int k) => _z[k];

        public static Piece? Read(double[] flat, double length, double tol, out string? error)
        {
            error = null;
            int n = flat.Length / 2;
            if (n < 2)
                return null;
            var s = new double[n];
            var z = new double[n];
            for (int i = 0; i < n; i++)
            {
                s[i] = flat[i * 2];
                z[i] = flat[i * 2 + 1];
            }

            if (s[^1] < s[0])
            {
                Array.Reverse(s);
                Array.Reverse(z);
            }

            for (int i = 1; i < n; i++)
            {
                if (s[i] < s[i - 1] - tol)
                {
                    error = FormattableString.Invariant(
                        $"The section doubles back near station {s[i - 1]:0.###}; a profile has one height per station.");
                    return null;
                }

                s[i] = Math.Max(s[i], s[i - 1]);
            }

            return Clip(s, z, length, tol);
        }

        /// <summary>Clipped to the curve's stations [0, length]; null when nothing of it lies over them.</summary>
        private static Piece? Clip(double[] s, double[] z, double length, double tol)
        {
            if (s[^1] < -tol || s[0] > length + tol || s[^1] - s[0] <= tol)
                return null;
            var cs = new List<double>(s.Length + 2);
            var cz = new List<double>(s.Length + 2);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] < 0.0)
                {
                    if (i + 1 < s.Length && s[i + 1] > 0.0)
                    {
                        cs.Add(0.0);
                        cz.Add(HeightBetween(s[i], z[i], s[i + 1], z[i + 1], 0.0));
                    }

                    continue;
                }

                if (s[i] > length)
                {
                    if (i > 0 && s[i - 1] < length)
                    {
                        cs.Add(length);
                        cz.Add(HeightBetween(s[i - 1], z[i - 1], s[i], z[i], length));
                    }

                    break;
                }

                cs.Add(s[i]);
                cz.Add(z[i]);
            }

            return cs.Count >= 2 && cs[^1] - cs[0] > tol ? new Piece(cs.ToArray(), cz.ToArray()) : null;
        }

        /// <summary>The height at a station: the first of a vertical step, linear between vertices.</summary>
        public double ElevationAt(double station)
        {
            if (station <= _s[0])
                return _z[0];
            for (int i = 1; i < _s.Length; i++)
            {
                if (station <= _s[i])
                    return HeightBetween(_s[i - 1], _z[i - 1], _s[i], _z[i], station);
            }

            return _z[^1];
        }

        public IEnumerable<double> StationsIn(double start, double end) => _s.Where(s => s >= start && s <= end);

        public IEnumerable<double> MovedVertexStations(double[] stations, double[] elevations, double tol)
        {
            for (int k = 0; k < _s.Length; k++)
            {
                if (OffProfile(stations, elevations, _s[k], _z[k], tol))
                    yield return _s[k];
            }
        }

        /// <summary>
        /// Where this piece leaves the original profile. A vertex off the profile changes the spans either side
        /// of it; an original vertex the piece no longer passes through (a grip deleted, a stretch redrawn)
        /// changes the piece span holding it.
        /// </summary>
        public IEnumerable<(double Start, double End)> ChangedRanges(double[] stations, double[] elevations, double tol)
        {
            for (int k = 0; k < _s.Length; k++)
            {
                if (OffProfile(stations, elevations, _s[k], _z[k], tol))
                    yield return (_s[Math.Max(0, k - 1)], _s[Math.Min(_s.Length - 1, k + 1)]);
            }

            for (int j = 0; j < stations.Length; j++)
            {
                double s = stations[j];
                if (s < Start + tol || s > End - tol)
                    continue;
                if (!OnPiece(s, elevations[j], tol))
                {
                    int upper = Array.FindIndex(_s, value => value >= s);
                    int lower = Math.Max(0, upper - 1);
                    yield return (_s[lower], _s[Math.Max(upper, 0)]);
                }
            }
        }

        /// <summary>True when (s, z) is not on the polyline profile: further than tol from every height it takes at s.</summary>
        private static bool OffProfile(double[] stations, double[] elevations, double s, double z, double tol)
        {
            (double low, double high) = HeightsAt(stations, elevations, s, tol);
            return z < low - tol || z > high + tol;
        }

        private bool OnPiece(double s, double z, double tol)
        {
            (double low, double high) = HeightsAt(_s, _z, s, tol);
            return z >= low - tol && z <= high + tol;
        }

        /// <summary>The range of heights a polyline profile takes at a station: one value, or a vertical step's span.</summary>
        private static (double Low, double High) HeightsAt(double[] s, double[] z, double station, double tol)
        {
            double low = double.PositiveInfinity, high = double.NegativeInfinity;
            void Take(double value)
            {
                low = Math.Min(low, value);
                high = Math.Max(high, value);
            }

            for (int i = 0; i < s.Length; i++)
            {
                if (Math.Abs(s[i] - station) <= tol)
                    Take(z[i]);
                if (i + 1 < s.Length && s[i] < station && s[i + 1] > station)
                    Take(HeightBetween(s[i], z[i], s[i + 1], z[i + 1], station));
            }

            if (!double.IsFinite(low))
            {
                double end = station <= s[0] ? z[0] : z[^1];
                return (end, end);
            }

            return (low, high);
        }

        /// <summary>The height at station <paramref name="s"/> on the segment (s0, z0)-(s1, z1).</summary>
        private static double HeightBetween(double s0, double z0, double s1, double z1, double s)
        {
            double run = s1 - s0;
            return run > 0.0 ? Geometry2D.Lerp(z0, z1, (s - s0) / run) : z0;
        }
    }
}
