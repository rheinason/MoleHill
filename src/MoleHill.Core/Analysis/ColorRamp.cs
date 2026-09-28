namespace MoleHill.Core.Analysis;

/// <summary>
/// An editable colour ramp: an ordered set of stops at normalized 0..1 positions.
///
/// This is the pure, Eto-free half of the panel's ramp card — every gesture the card offers (drag a stop,
/// add, remove, distribute, reverse, recolour) is a method here that returns a new ramp, so the editing
/// semantics can be pinned by tests without a UI. Sampling itself is not duplicated: a ramp hands its
/// <see cref="Stops"/> to <see cref="AnalysisColorMapper"/>, which stays the single owner of how a value
/// becomes a colour, so the swatch under the cursor and the face on the mesh cannot disagree.
/// </summary>
public sealed class ColorRamp
{
    /// <summary>A ramp is a ramp because it has somewhere to go — two ends at minimum.</summary>
    public const int MinimumStops = 2;

    /// <summary>More stops than a narrow panel card can address, and more than a legend can label.</summary>
    public const int MaximumStops = 32;

    private readonly SlopeAnalyzer.ColorStop[] _stops;

    public ColorRamp(IReadOnlyList<SlopeAnalyzer.ColorStop> stops)
    {
        _stops = Normalize(stops);
    }

    /// <summary>Stops in ascending position order. Always at least <see cref="MinimumStops"/> long.</summary>
    public IReadOnlyList<SlopeAnalyzer.ColorStop> Stops => _stops;

    public int Count => _stops.Length;

    public SlopeAnalyzer.ColorStop this[int index] => _stops[index];

    /// <summary>The colour at a normalized position, by the same rule the mesh is coloured with.</summary>
    public SlopeAnalyzer.ColorStop Sample(double t) => AnalysisColorMapper.SamplePalette(t, _stops);

    /// <summary>
    /// Moves one stop to a new position. The result is re-sorted; callers that need to retain the exact
    /// stop across coincident positions should use the overload that reports its new index.
    /// </summary>
    public ColorRamp WithStopAt(int index, double position)
    {
        return WithStopAt(index, position, out _);
    }

    /// <summary>
    /// Moves one stop and reports where that same stop landed after sorting. This is deliberately not
    /// derived with <see cref="IndexNearest"/>: two stops may occupy the same position, and nearest would
    /// then switch a drag onto whichever stop happens to win the tie.
    /// </summary>
    public ColorRamp WithStopAt(int index, double position, out int movedIndex)
    {
        movedIndex = index;
        if (!IsValidIndex(index))
            return this;

        double clamped = Math.Clamp(position, 0.0, 1.0);
        var next = (SlopeAnalyzer.ColorStop[])_stops.Clone();
        var stop = next[index];
        next[index] = new SlopeAnalyzer.ColorStop(clamped, stop.R, stop.G, stop.B);

        // Normalize uses a stable position sort. Count exactly the entries that will precede the moved
        // stop, including equal-position entries that preceded it in the source array.
        movedIndex = 0;
        for (int i = 0; i < next.Length; i++)
        {
            if (i == index)
                continue;

            if (next[i].Position < clamped || (next[i].Position == clamped && i < index))
                movedIndex++;
        }

        return new ColorRamp(next);
    }

    /// <summary>Recolours one stop, leaving its position alone.</summary>
    public ColorRamp WithStopColor(int index, byte r, byte g, byte b)
    {
        if (!IsValidIndex(index))
            return this;

        var next = (SlopeAnalyzer.ColorStop[])_stops.Clone();
        next[index] = new SlopeAnalyzer.ColorStop(next[index].Position, r, g, b);
        return new ColorRamp(next);
    }

    /// <summary>
    /// Inserts a stop in the widest gap, coloured with what the ramp already shows there — so pressing "+"
    /// never changes how anything looks, it only gives you a new handle to move.
    /// </summary>
    public ColorRamp Insert()
    {
        if (_stops.Length >= MaximumStops)
            return this;

        double widest = -1.0;
        double at = 0.5;
        for (int i = 0; i < _stops.Length - 1; i++)
        {
            double gap = _stops[i + 1].Position - _stops[i].Position;
            if (gap > widest)
            {
                widest = gap;
                at = (_stops[i].Position + _stops[i + 1].Position) * 0.5;
            }
        }

        return InsertAt(at);
    }

    /// <summary>Inserts a stop at an explicit position, taking the ramp's current colour there.</summary>
    public ColorRamp InsertAt(double position)
    {
        if (_stops.Length >= MaximumStops)
            return this;

        double clamped = Math.Clamp(position, 0.0, 1.0);
        var colour = Sample(clamped);

        var next = new SlopeAnalyzer.ColorStop[_stops.Length + 1];
        Array.Copy(_stops, next, _stops.Length);
        next[^1] = new SlopeAnalyzer.ColorStop(clamped, colour.R, colour.G, colour.B);
        return new ColorRamp(next);
    }

    /// <summary>Removes a stop, refusing to go below <see cref="MinimumStops"/>.</summary>
    public ColorRamp RemoveAt(int index)
    {
        if (!IsValidIndex(index) || _stops.Length <= MinimumStops)
            return this;

        var next = new SlopeAnalyzer.ColorStop[_stops.Length - 1];
        Array.Copy(_stops, 0, next, 0, index);
        Array.Copy(_stops, index + 1, next, index, _stops.Length - index - 1);
        return new ColorRamp(next);
    }

    /// <summary>Spreads the stops evenly from 0 to 1, keeping their colours in order.</summary>
    public ColorRamp Distribute()
    {
        var next = new SlopeAnalyzer.ColorStop[_stops.Length];
        double last = _stops.Length - 1;
        for (int i = 0; i < _stops.Length; i++)
        {
            var stop = _stops[i];
            next[i] = new SlopeAnalyzer.ColorStop(i / last, stop.R, stop.G, stop.B);
        }

        return new ColorRamp(next);
    }

    /// <summary>
    /// Mirrors the ramp: colours swap end for end while the positions stay put. Reversing the positions
    /// instead would be a no-op after re-sorting, and would also undo any uneven spacing the user set.
    /// </summary>
    public ColorRamp Reverse()
    {
        var next = new SlopeAnalyzer.ColorStop[_stops.Length];
        for (int i = 0; i < _stops.Length; i++)
        {
            var source = _stops[_stops.Length - 1 - i];
            next[i] = new SlopeAnalyzer.ColorStop(_stops[i].Position, source.R, source.G, source.B);
        }

        return new ColorRamp(next);
    }

    /// <summary>
    /// Resamples this ramp to a given number of evenly spaced stops. This is how picking a preset preserves
    /// the stop count the user was working with rather than snapping back to the preset's own.
    /// </summary>
    public ColorRamp Resample(int stopCount)
    {
        int count = Math.Clamp(stopCount, MinimumStops, MaximumStops);
        var next = new SlopeAnalyzer.ColorStop[count];
        double last = count - 1;
        for (int i = 0; i < count; i++)
        {
            double t = i / last;
            var colour = Sample(t);
            next[i] = new SlopeAnalyzer.ColorStop(t, colour.R, colour.G, colour.B);
        }

        return new ColorRamp(next);
    }

    /// <summary>Index of the stop closest to a position — the hit test behind handle dragging.</summary>
    public int IndexNearest(double position)
    {
        int best = 0;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < _stops.Length; i++)
        {
            double distance = Math.Abs(_stops[i].Position - position);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    /// <summary>True when two ramps would paint identically — used to skip redundant recolours.</summary>
    public bool Matches(ColorRamp? other)
    {
        if (other == null || other._stops.Length != _stops.Length)
            return false;

        for (int i = 0; i < _stops.Length; i++)
        {
            var a = _stops[i];
            var b = other._stops[i];
            if (a.R != b.R || a.G != b.G || a.B != b.B || Math.Abs(a.Position - b.Position) > 1e-9)
                return false;
        }

        return true;
    }

    private bool IsValidIndex(int index) => index >= 0 && index < _stops.Length;

    /// <summary>
    /// Sorts, caps, and guarantees the minimum. A degenerate input (empty, or a single stop) is widened
    /// into a usable ramp rather than rejected, because this is fed by deserialized documents.
    /// </summary>
    private static SlopeAnalyzer.ColorStop[] Normalize(IReadOnlyList<SlopeAnalyzer.ColorStop> stops)
    {
        if (stops == null || stops.Count == 0)
        {
            return new SlopeAnalyzer.ColorStop[]
            {
                new(0.0, 180, 180, 180),
                new(1.0, 180, 180, 180)
            };
        }

        if (stops.Count == 1)
        {
            var only = stops[0];
            return new SlopeAnalyzer.ColorStop[]
            {
                new(0.0, only.R, only.G, only.B),
                new(1.0, only.R, only.G, only.B)
            };
        }

        // OrderBy is a stable sort, so two stops dragged onto the same position keep the order the user
        // last saw them in rather than flipping arbitrarily mid-drag.
        var sorted = stops.OrderBy(stop => stop.Position).ToArray();
        return sorted.Length <= MaximumStops ? sorted : sorted[..MaximumStops];
    }
}
