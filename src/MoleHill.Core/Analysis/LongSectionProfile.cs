// Maps between a curve's stations and elevations and a flat long-section drawing of them.
namespace MoleHill.Core.Analysis;

/// <summary>
/// Where a long section is drawn: station 0 at the datum sits at (<see cref="OriginX"/>,
/// <see cref="OriginY"/>), station runs along +X at true scale and elevation along +Y, measured from
/// <see cref="Datum"/> and multiplied by <see cref="Exaggeration"/>.
/// </summary>
public readonly record struct LongSectionFrame(double OriginX, double OriginY, double Datum, double Exaggeration)
{
    public (double X, double Y) ToDrawing(double station, double elevation) =>
        (OriginX + station, OriginY + (elevation - Datum) * Exaggeration);

    public (double Station, double Elevation) FromDrawing(double x, double y) =>
        (x - OriginX, Datum + (y - OriginY) / Exaggeration);
}

/// <summary>
/// The round trip behind mhExtractProfile and mhApplyProfile. A profile drawn (or edited) in the frame is
/// read back into stations and elevations; a profile that doubles back on itself names two elevations for
/// one station, so it is refused rather than guessed at.
/// </summary>
public static class LongSectionProfile
{
    /// <summary>
    /// Reads a drawn profile (flat XY points in drawing coordinates) as strictly increasing stations with
    /// an elevation at each. A profile drawn right to left is read in reverse. Points closer in station than
    /// <paramref name="tolerance"/> are merged when their elevations also agree; otherwise that is a vertical
    /// step and, like any backwards step, makes the profile unreadable.
    /// </summary>
    public static bool TryRead(
        double[] drawingXy,
        LongSectionFrame frame,
        double tolerance,
        out double[] stations,
        out double[] elevations,
        out string? error)
    {
        stations = Array.Empty<double>();
        elevations = Array.Empty<double>();
        error = null;
        int count = drawingXy.Length / 2;
        if (count < 2)
        {
            error = "A profile needs at least two points.";
            return false;
        }

        if (!double.IsFinite(frame.Exaggeration) || Math.Abs(frame.Exaggeration) < 1e-12)
        {
            error = "The profile's vertical exaggeration is not usable.";
            return false;
        }

        var s = new double[count];
        var z = new double[count];
        for (int i = 0; i < count; i++)
            (s[i], z[i]) = frame.FromDrawing(drawingXy[i * 2], drawingXy[i * 2 + 1]);
        if (s[^1] < s[0])
        {
            Array.Reverse(s);
            Array.Reverse(z);
        }

        double eps = Math.Max(Math.Abs(tolerance), 1e-12);
        var outStations = new List<double>(count) { s[0] };
        var outElevations = new List<double>(count) { z[0] };
        for (int i = 1; i < count; i++)
        {
            double step = s[i] - outStations[^1];
            if (step < -eps)
            {
                error = $"The profile doubles back at station {s[i]:F3}; each station can have only one elevation.";
                return false;
            }

            if (step <= eps)
            {
                if (Math.Abs(z[i] - outElevations[^1]) > eps)
                {
                    error = $"The profile steps vertically at station {s[i]:F3}; give the step a run.";
                    return false;
                }

                continue;
            }

            outStations.Add(s[i]);
            outElevations.Add(z[i]);
        }

        if (outStations.Count < 2)
        {
            error = "The profile has no length along the station axis.";
            return false;
        }

        stations = outStations.ToArray();
        elevations = outElevations.ToArray();
        return true;
    }

    /// <summary>Linear elevation at a station; NaN outside the profile's station range.</summary>
    public static double ElevationAt(double[] stations, double[] elevations, double station, double tolerance = 0.0)
    {
        if (stations.Length == 0)
            return double.NaN;
        if (station < stations[0] - tolerance || station > stations[^1] + tolerance)
            return double.NaN;
        if (station <= stations[0])
            return elevations[0];
        if (station >= stations[^1])
            return elevations[^1];

        int index = Array.BinarySearch(stations, station);
        if (index >= 0)
            return elevations[index];
        int upper = ~index;
        int lower = upper - 1;
        double t = (station - stations[lower]) / (stations[upper] - stations[lower]);
        return elevations[lower] + (elevations[upper] - elevations[lower]) * t;
    }

    /// <summary>Cumulative plan (XY) distance at each point of a flat XYZ path.</summary>
    public static double[] PlanStations(double[] pathXyz)
    {
        int count = pathXyz.Length / 3;
        var stations = new double[count];
        for (int i = 1; i < count; i++)
        {
            double dx = pathXyz[i * 3] - pathXyz[(i - 1) * 3];
            double dy = pathXyz[i * 3 + 1] - pathXyz[(i - 1) * 3 + 1];
            stations[i] = stations[i - 1] + Math.Sqrt(dx * dx + dy * dy);
        }

        return stations;
    }

    /// <summary>
    /// Re-elevates a plan path from a profile, keeping its XY. Every path point keeps its plan position and
    /// takes the profile's elevation at its station, and every profile point inside the path's length adds a
    /// path point at its station, so the profile's own breaks survive. A station the profile does not reach
    /// keeps the path's elevation and is counted in <paramref name="uncovered"/>.
    /// </summary>
    public static double[] Apply(
        double[] pathXyz,
        double[] stations,
        double[] elevations,
        double tolerance,
        out int uncovered)
    {
        uncovered = 0;
        int count = pathXyz.Length / 3;
        double[] pathStations = PlanStations(pathXyz);
        double eps = Math.Max(Math.Abs(tolerance), 1e-12);
        var merged = new List<(double Station, int Segment, bool FromPath)>(count + stations.Length);
        for (int i = 0; i < count; i++)
            merged.Add((pathStations[i], i, true));
        double length = count > 0 ? pathStations[^1] : 0.0;
        foreach (double station in stations)
        {
            if (station <= eps || station >= length - eps)
                continue;
            int index = Array.BinarySearch(pathStations, station);
            int segment = index >= 0 ? index : ~index - 1;
            merged.Add((station, Math.Clamp(segment, 0, Math.Max(0, count - 2)), false));
        }

        merged.Sort((a, b) => a.Station.CompareTo(b.Station));
        var output = new List<double>(merged.Count * 3);
        double lastStation = double.NegativeInfinity;
        foreach ((double station, int segment, bool fromPath) in merged)
        {
            // A point at the station just written adds nothing: the profile gives both one elevation, so a
            // profile point on a path point, or a vertical step in the source, would only repeat a point.
            if (station - lastStation <= eps)
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

            double z = ElevationAt(stations, elevations, station, eps);
            if (double.IsNaN(z))
            {
                z = pathZ;
                uncovered++;
            }

            output.Add(x);
            output.Add(y);
            output.Add(z);
            lastStation = station;
        }

        return output.ToArray();
    }

    /// <summary>
    /// A datum a little below the lowest elevation, on a whole multiple of <paramref name="step"/>, so the
    /// section's base line reads as a round number.
    /// </summary>
    public static double SuggestDatum(double lowestElevation, double step)
    {
        if (!double.IsFinite(lowestElevation))
            return 0.0;
        if (!(step > 0.0))
            return lowestElevation;
        return Math.Floor(lowestElevation / step) * step - step;
    }
}
