// Stations a plan curve, reads the terrain under each station and turns the hits into draped runs.
namespace MoleHill.Core.Processing;

/// <summary>What a drape does with a station the terrain does not cover.</summary>
public enum DrapeMissPolicy
{
    /// <summary>End the run at the terrain's edge and start a new one where the terrain resumes.</summary>
    Split,

    /// <summary>Carry a straight grade across a gap between two hits. A gap at either end has nothing to
    /// bridge to and is dropped.</summary>
    Bridge,

    /// <summary>Keep the curve's own elevation where there is no terrain.</summary>
    KeepOriginal
}

public sealed class CurveDrapeOptions
{
    /// <summary>Plan distance between stations, in model units.</summary>
    public required double Spacing { get; init; }

    /// <summary>Model tolerance: coincident points closer than this are merged and the terrain edge is
    /// located to within it.</summary>
    public required double Tolerance { get; init; }

    public DrapeMissPolicy MissPolicy { get; init; } = DrapeMissPolicy.Split;

    /// <summary>
    /// Null drapes the whole curve. A length makes this a section drape: both ends keep the curve's own
    /// elevation, so it ties back into the curve it was cut from, and the drape ramps in from each end over
    /// this distance (zero ramps over a single station).
    /// </summary>
    public double? TieInLength { get; init; }

    /// <summary>Ease the tie-in ramp in and out rather than ramping linearly.</summary>
    public bool SmoothTieIn { get; init; } = true;

    /// <summary>Extra stations (distances along the curve) to sample besides the regular ones, such as
    /// the source's corners, which regular stationing would otherwise cut.</summary>
    public IReadOnlyList<double>? ExtraStations { get; init; }

    /// <summary>Bisection steps spent locating each terrain edge between a hit and a miss.</summary>
    public int EdgeRefinementSteps { get; init; } = 20;
}

public sealed class CurveDrapeResult
{
    /// <summary>The draped runs, each a flat XYZ polyline of at least two points. One run when the
    /// terrain covers the whole curve or the policy closes every gap.</summary>
    public required IReadOnlyList<double[]> Runs { get; init; }

    public required int StationCount { get; init; }

    /// <summary>Stations the terrain did not cover, whatever the policy then did with them.</summary>
    public required int MissedStations { get; init; }

    public required int BridgedStations { get; init; }

    public required int KeptStations { get; init; }

    /// <summary>Missed stations left out of every run: split gaps, and unbridgeable gaps at the ends.</summary>
    public required int DroppedStations { get; init; }

    /// <summary>Why nothing came out; null when at least one run did.</summary>
    public string? Failure { get; init; }

    public bool Succeeded => Runs.Count > 0;
}

/// <summary>A loose drape's new edit-point heights and what happened to the points with no surface under them.</summary>
public sealed record LooseDrapeResult(
    double[] Elevations,
    int PointCount,
    int MissedPoints,
    int BridgedPoints,
    int KeptPoints,
    string? Failure)
{
    public bool Succeeded => Failure == null;
}

/// <summary>
/// The drape kernel behind mhDrapeCurve and mhDrapeCurveSection. Geometry comes in as two callbacks — a
/// point at a distance along the curve and a terrain probe returning the height under a plan point, or
/// null where there is none — so the policy decisions are testable without Rhino.
/// </summary>
public static class CurveDrape
{
    private readonly record struct Station(double S, double X, double Y, double OriginalZ, double? HitZ);

    public static CurveDrapeResult Drape(
        double length,
        Func<double, (double X, double Y, double Z)> pointAt,
        Func<double, double, double?> probe,
        CurveDrapeOptions options)
    {
        ArgumentNullException.ThrowIfNull(pointAt);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(options);
        double tolerance = Math.Max(Math.Abs(options.Tolerance), 1e-12);
        if (!double.IsFinite(length) || length <= tolerance)
            return Failed("The curve is too short to drape.");

        double spacing = Math.Max(Math.Abs(options.Spacing), tolerance);
        int intervals = Math.Max(1, (int)Math.Ceiling(length / spacing));
        var distances = new List<double>(intervals + 1 + (options.ExtraStations?.Count ?? 0));
        for (int i = 0; i <= intervals; i++)
            distances.Add(length * i / intervals);
        if (options.ExtraStations != null)
        {
            foreach (double extra in options.ExtraStations)
            {
                if (double.IsFinite(extra) && extra > 0.0 && extra < length)
                    distances.Add(extra);
            }
        }

        distances.Sort();
        var stations = new List<Station>(distances.Count);
        foreach (double s in distances)
        {
            if (stations.Count > 0 && s - stations[^1].S <= tolerance)
                continue;
            stations.Add(Sample(s));
        }

        // A miss beside a hit leaves the run's end up to one spacing short of the terrain's edge.
        // Bisecting each change of coverage puts the end on the edge instead.
        var refined = new List<Station>(stations.Count + 8);
        for (int i = 0; i < stations.Count; i++)
        {
            if (i > 0 && stations[i - 1].HitZ.HasValue != stations[i].HitZ.HasValue)
            {
                Station? edge = LocateEdge(stations[i - 1], stations[i]);
                if (edge is { } found &&
                    found.S - refined[^1].S > tolerance && stations[i].S - found.S > tolerance)
                    refined.Add(found);
            }

            refined.Add(stations[i]);
        }

        int missed = refined.Count(station => !station.HitZ.HasValue);
        if (missed == refined.Count)
            return Failed("The curve does not pass over the terrain anywhere.");

        // Target elevation per station: the hit, or what the miss policy makes of a miss (NaN = break).
        var target = new double[refined.Count];
        int bridged = 0, kept = 0, dropped = 0;
        for (int i = 0; i < refined.Count; i++)
            target[i] = refined[i].HitZ ?? double.NaN;

        if (options.MissPolicy == DrapeMissPolicy.KeepOriginal)
        {
            for (int i = 0; i < refined.Count; i++)
            {
                if (refined[i].HitZ.HasValue)
                    continue;
                target[i] = refined[i].OriginalZ;
                kept++;
            }
        }
        else if (options.MissPolicy == DrapeMissPolicy.Bridge)
        {
            int previousHit = -1;
            for (int i = 0; i < refined.Count; i++)
            {
                if (!refined[i].HitZ.HasValue)
                    continue;
                if (previousHit >= 0 && i - previousHit > 1)
                {
                    Station a = refined[previousHit], b = refined[i];
                    for (int j = previousHit + 1; j < i; j++)
                    {
                        double t = (refined[j].S - a.S) / (b.S - a.S);
                        target[j] = a.HitZ!.Value + (b.HitZ!.Value - a.HitZ.Value) * t;
                        bridged++;
                    }
                }

                previousHit = i;
            }
        }

        for (int i = 0; i < refined.Count; i++)
        {
            if (double.IsNaN(target[i]))
                dropped++;
        }

        // A section keeps both ends on the curve and ramps the drape in from each.
        if (options.TieInLength is { } tieIn)
        {
            double ramp = Math.Max(0.0, tieIn);
            for (int i = 0; i < refined.Count; i++)
            {
                if (double.IsNaN(target[i]))
                    continue;
                double weight = TieInWeight(refined[i].S, length, ramp, options.SmoothTieIn);
                target[i] = refined[i].OriginalZ + (target[i] - refined[i].OriginalZ) * weight;
            }
        }

        var runs = new List<double[]>();
        var current = new List<double>();
        void Flush()
        {
            if (current.Count >= 6)
                runs.Add(current.ToArray());
            else if (current.Count > 0)
                dropped += current.Count / 3;
            current.Clear();
        }

        for (int i = 0; i < refined.Count; i++)
        {
            if (double.IsNaN(target[i]))
            {
                Flush();
                continue;
            }

            double x = refined[i].X, y = refined[i].Y, z = target[i];
            if (current.Count >= 3)
            {
                double dx = x - current[^3], dy = y - current[^2], dz = z - current[^1];
                if (Math.Sqrt(dx * dx + dy * dy + dz * dz) <= tolerance)
                    continue;
            }

            current.Add(x);
            current.Add(y);
            current.Add(z);
        }

        Flush();
        return new CurveDrapeResult
        {
            Runs = runs,
            StationCount = refined.Count,
            MissedStations = missed,
            BridgedStations = bridged,
            KeptStations = kept,
            DroppedStations = dropped,
            Failure = runs.Count == 0 ? "No stretch of the curve over the terrain was long enough to keep." : null
        };

        Station Sample(double s)
        {
            (double x, double y, double z) = pointAt(s);
            double? hit = probe(x, y);
            return new Station(s, x, y, z, hit is { } value && double.IsFinite(value) ? value : null);
        }

        Station? LocateEdge(Station a, Station b)
        {
            Station inside = a.HitZ.HasValue ? a : b;
            Station outside = a.HitZ.HasValue ? b : a;
            for (int step = 0; step < options.EdgeRefinementSteps; step++)
            {
                if (Math.Abs(inside.S - outside.S) <= tolerance)
                    break;
                Station middle = Sample((inside.S + outside.S) * 0.5);
                if (middle.HitZ.HasValue)
                    inside = middle;
                else
                    outside = middle;
            }

            return inside.S == a.S || inside.S == b.S ? null : inside;
        }
    }

    /// <summary>
    /// The loose drape: the curve's own edit points are dropped onto the terrain and nothing is added, so
    /// the curve keeps its type, degree and point count (Rhino's ProjectToMesh Loose). Between edit points the
    /// curve does not follow the ground. An edit point with no surface under it cannot be left out without
    /// changing the curve, so <see cref="DrapeMissPolicy.Split"/> keeps its height like
    /// <see cref="DrapeMissPolicy.KeepOriginal"/>; <see cref="DrapeMissPolicy.Bridge"/> grades across a gap
    /// between hits by station and keeps the height of a gap at an end.
    /// </summary>
    /// <param name="stations">Distance along the curve of each edit point, ascending.</param>
    /// <param name="pointsXyz">The edit points, flat XYZ.</param>
    public static LooseDrapeResult DrapeEditPoints(
        double[] stations,
        double[] pointsXyz,
        Func<double, double, double?> probe,
        DrapeMissPolicy missPolicy)
    {
        ArgumentNullException.ThrowIfNull(stations);
        ArgumentNullException.ThrowIfNull(pointsXyz);
        ArgumentNullException.ThrowIfNull(probe);
        int count = pointsXyz.Length / 3;
        var z = new double[count];
        var hit = new bool[count];
        int missed = 0;
        for (int i = 0; i < count; i++)
        {
            double? sampled = probe(pointsXyz[i * 3], pointsXyz[i * 3 + 1]);
            hit[i] = sampled is { } value && double.IsFinite(value);
            z[i] = hit[i] ? sampled!.Value : pointsXyz[i * 3 + 2];
            if (!hit[i])
                missed++;
        }

        if (missed == count)
            return new LooseDrapeResult(z, count, missed, 0, missed, "No edit point lies over the terrain.");

        int bridged = 0;
        if (missPolicy == DrapeMissPolicy.Bridge)
        {
            int previous = -1;
            for (int i = 0; i < count; i++)
            {
                if (!hit[i])
                    continue;
                if (previous >= 0 && i - previous > 1)
                {
                    double span = stations[i] - stations[previous];
                    for (int j = previous + 1; j < i; j++)
                    {
                        double t = span > 0.0 ? (stations[j] - stations[previous]) / span : 0.0;
                        z[j] = z[previous] + (z[i] - z[previous]) * t;
                        bridged++;
                    }
                }

                previous = i;
            }
        }

        return new LooseDrapeResult(z, count, missed, bridged, missed - bridged, null);
    }

    /// <summary>
    /// 0 at either end of the section, 1 once <paramref name="ramp"/> in from both. Two ramps longer than
    /// half the section would overlap and never reach full strength, so each is capped at half.
    /// A zero ramp holds only the end stations themselves.
    /// </summary>
    public static double TieInWeight(double station, double length, double ramp, bool smooth)
    {
        double distanceToEnd = Math.Min(station, length - station);
        if (distanceToEnd <= 0.0)
            return 0.0;
        double cappedRamp = Math.Min(ramp, length * 0.5);
        if (cappedRamp <= 0.0 || distanceToEnd >= cappedRamp)
            return 1.0;
        double t = distanceToEnd / cappedRamp;
        return smooth ? 0.5 - 0.5 * Math.Cos(Math.PI * t) : t;
    }

    private static CurveDrapeResult Failed(string reason) => new()
    {
        Runs = Array.Empty<double[]>(),
        StationCount = 0,
        MissedStations = 0,
        BridgedStations = 0,
        KeptStations = 0,
        DroppedStations = 0,
        Failure = reason
    };
}
